using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Core.Config;
using DeskPair.Core.Ipc;
using DeskPair.Protocol.Ipc;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Tests;

public class IpcTests
{
    private sealed class EchoBridge : IIpcHostBridge
    {
        public List<string> Connected { get; } = [];

        /// <summary>Who the server said was asking, for the last request that reached this bridge.</summary>
        public IpcCaller? LastCaller { get; private set; }

        public Task<IpcMessage?> HandleAsync(IpcMessage request, IpcClientInfo client, CancellationToken ct)
        {
            LastCaller = client.Caller;
            return Task.FromResult<IpcMessage?>(request.UnionCase switch
            {
                IpcMessage.UnionOneofCase.Ping => new IpcMessage { Pong = new Pong() },
                IpcMessage.UnionOneofCase.GetId => new IpcMessage { IdChanged = new IdChanged { Id = "123456789" } },
                IpcMessage.UnionOneofCase.GetTempPassword =>
                    new IpcMessage { TempPassword = new TempPassword { Password = "abc123" } },
                _ => null,
            });
        }

        public void ClientConnected(IpcClientInfo client) => Connected.Add(client.Role);
    }

    /// <summary>
    /// The privileged commands still work for the person the engine belongs to, over a real pipe.
    ///
    /// The authority check is enforced in HostIpcBridge, not here, so what this pins is the layer
    /// underneath it: that the server identifies an ordinary client at all, and identifies it as the
    /// owner. Get that wrong and nothing visible breaks in a unit test while every password and every
    /// setting is refused in the product -- which is precisely what the first implementation did, by
    /// asking for an impersonation the client had not granted.
    /// </summary>
    [Fact]
    public async Task The_owner_is_recognised_over_a_real_connection()
    {
        IpcEndpoint endpoint = IpcEndpoint.ForTest();
        byte[] token = IpcServer.NewToken();
        var bridge = new EchoBridge();
        await using var server = new IpcServer(endpoint, token, bridge, NullLogger.Instance);
        server.Start();

        await using IpcClient client = await IpcClient.ConnectAsync(endpoint, token, IpcRoles.Ui, NullLogger.Instance);
        (await client.RequestAsync(new IpcMessage { GetTempPassword = new GetTempPassword() }))
            .TempPassword.Password.ShouldBe("abc123");

        bridge.LastCaller.ShouldNotBeNull();
        bridge.LastCaller!.Known.ShouldBeTrue();
        bridge.LastCaller.IsOwner.ShouldBeTrue(bridge.LastCaller.Because);
    }

    [Fact]
    public async Task Client_with_valid_token_can_request_and_receive_pushes()
    {
        IpcEndpoint endpoint = IpcEndpoint.ForTest();
        byte[] token = IpcServer.NewToken();
        var bridge = new EchoBridge();
        await using var server = new IpcServer(endpoint, token, bridge, NullLogger.Instance);
        server.Start();

        await using IpcClient client = await IpcClient.ConnectAsync(endpoint, token, IpcRoles.Ui, NullLogger.Instance);
        (await client.RequestAsync(new IpcMessage { Ping = new Ping() })).UnionCase.ShouldBe(IpcMessage.UnionOneofCase.Pong);
        (await client.RequestAsync(new IpcMessage { GetId = new GetId() })).IdChanged.Id.ShouldBe("123456789");
        bridge.Connected.ShouldBe([IpcRoles.Ui]);

        var pushed = new TaskCompletionSource<IpcMessage>();
        client.Pushed += m => pushed.TrySetResult(m);
        await server.BroadcastAsync(new IpcMessage { TempPassword = new TempPassword { Password = "abc123" } });
        (await pushed.Task.WaitAsync(TimeSpan.FromSeconds(5))).TempPassword.Password.ShouldBe("abc123");

        await server.BroadcastAsync(new IpcMessage { Ping = new Ping() }, role: IpcRoles.ConnectionManager);
        server.ClientCount.ShouldBe(1);
    }

    [Fact]
    public async Task Client_with_wrong_token_is_rejected()
    {
        IpcEndpoint endpoint = IpcEndpoint.ForTest();
        await using var server = new IpcServer(endpoint, IpcServer.NewToken(), new EchoBridge(), NullLogger.Instance);
        server.Start();

        await Should.ThrowAsync<UnauthorizedAccessException>(() => IpcClient.ConnectAsync(endpoint, IpcServer.NewToken(), IpcRoles.Ui, NullLogger.Instance));
        server.ClientCount.ShouldBe(0);
    }

    [Fact]
    public async Task Requests_fail_when_the_service_goes_away()
    {
        IpcEndpoint endpoint = IpcEndpoint.ForTest();
        byte[] token = IpcServer.NewToken();
        var server = new IpcServer(endpoint, token, new EchoBridge(), NullLogger.Instance);
        server.Start();
        await using IpcClient client = await IpcClient.ConnectAsync(endpoint, token, IpcRoles.Tray, NullLogger.Instance);
        var disconnected = new TaskCompletionSource<string>();
        client.Disconnected += r => disconnected.TrySetResult(r);

        await server.DisposeAsync();
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Should.ThrowAsync<Exception>(() => client.RequestAsync(new IpcMessage { Ping = new Ping() }, timeout: TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Host_config_round_trips_through_json_and_the_store()
    {
        var config = new HostConfig { RendezvousServer = "rdv.example:21116", ApproveMode = ApproveMode.ApproveBoth, AudioEnabled = false, DirectAccessPort = 4444 };
        HostConfig back = HostConfig.FromJson(config.ToJson());
        back.ShouldBe(config);

        string path = Path.Combine(Path.GetTempPath(), "sunllo-cfg-" + Guid.NewGuid().ToString("N"), "config.json");
        var store = new HostConfigStore(path);
        store.Load().ShouldBe(new HostConfig());
        store.Save(config);
        store.Load().ShouldBe(config);
        File.WriteAllText(path, "{not json");
        store.Load().ShouldBe(new HostConfig());
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void Hello_token_is_compared_as_bytes()
    {
        var hello = new IpcHello { Token = ByteString.CopyFrom(1, 2, 3), Role = IpcRoles.Ui };
        hello.Token.Span.SequenceEqual(new byte[] { 1, 2, 3 }).ShouldBeTrue();
    }
}
