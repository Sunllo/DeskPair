using DeskPair.Core.Config;
using DeskPair.Core.Ipc;
using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Session.Host.Auth;
using DeskPair.Core.Transport;
using DeskPair.Protocol.Crypto;
using DeskPair.Protocol.Ipc;
using DeskPair.Protocol.Messages;

namespace DeskPair.Integration.Tests;

/// <summary>
/// The main window shows a notice as soon as someone starts connecting, and the connection manager only
/// appears once there is something to decide. Both rest on the engine telling every local client about a
/// connection the moment the peer identifies itself, well before it is let in.
/// </summary>
public class IncomingNoticeTests
{
    [Fact]
    public async Task The_main_window_hears_about_a_connection_before_it_is_authorized()
    {
        await using Fixture fixture = await Fixture.StartAsync(new HostPolicy { ApproveMode = ApproveMode.ApproveClick });
        await using IpcClient ui = await fixture.ConnectAsync(IpcRoles.Ui);
        var seen = new List<IpcMessage>();
        ui.Pushed += m =>
        {
            lock (seen)
            {
                seen.Add(m);
            }
        };

        (ControllerSession session, _, PeerConnector connector) = fixture.Bed.CreateController();
        Task<LoginResult> login = ConnectAndLoginAsync(session, connector, fixture.Host.Identity.Id);

        await Testbed.WaitUntilAsync(
            () => Snapshot(seen).Any(m => m.UnionCase == IpcMessage.UnionOneofCase.ConnectionOpened && !m.ConnectionOpened.Authorized),
            "the ui is told about the unauthorized connection");

        // Nothing has been approved yet, so no "authorized" event may have arrived.
        Snapshot(seen).ShouldNotContain(m => m.UnionCase == IpcMessage.UnionOneofCase.ConnectionOpened && m.ConnectionOpened.Authorized);

        await fixture.ApproveAsync(Snapshot(seen).First(m => m.UnionCase == IpcMessage.UnionOneofCase.ConnectionOpened).ConnectionOpened.ConnId);
        (await login).Success.ShouldBeTrue();
        await Testbed.WaitUntilAsync(
            () => Snapshot(seen).Any(m => m.UnionCase == IpcMessage.UnionOneofCase.ConnectionOpened && m.ConnectionOpened.Authorized),
            "the ui is told the connection is in");
    }

    [Fact]
    public async Task A_connection_that_goes_away_is_taken_off_the_notice()
    {
        await using Fixture fixture = await Fixture.StartAsync(new HostPolicy { ApproveMode = ApproveMode.ApproveClick });
        await using IpcClient ui = await fixture.ConnectAsync(IpcRoles.Ui);
        var seen = new List<IpcMessage>();
        ui.Pushed += m =>
        {
            lock (seen)
            {
                seen.Add(m);
            }
        };

        (ControllerSession session, _, PeerConnector connector) = fixture.Bed.CreateController();
        Task<LoginResult> login = ConnectAndLoginAsync(session, connector, fixture.Host.Identity.Id);
        await Testbed.WaitUntilAsync(
            () => Snapshot(seen).Any(m => m.UnionCase == IpcMessage.UnionOneofCase.ConnectionOpened),
            "the ui is told about the connection");

        int connId = Snapshot(seen).First(m => m.UnionCase == IpcMessage.UnionOneofCase.ConnectionOpened).ConnectionOpened.ConnId;
        await fixture.RejectAsync(connId);
        (await login).Success.ShouldBeFalse();

        await Testbed.WaitUntilAsync(
            () => Snapshot(seen).Any(m => m.UnionCase == IpcMessage.UnionOneofCase.ConnectionClosed && m.ConnectionClosed.ConnId == connId),
            "the ui is told the connection is gone");
    }

    [Fact]
    public async Task Only_the_connection_manager_may_answer_an_approval()
    {
        await using Fixture fixture = await Fixture.StartAsync(new HostPolicy { ApproveMode = ApproveMode.ApproveClick, ApprovalTimeout = TimeSpan.FromSeconds(2) });
        await using IpcClient ui = await fixture.ConnectAsync(IpcRoles.Ui);
        var seen = new List<IpcMessage>();
        ui.Pushed += m =>
        {
            lock (seen)
            {
                seen.Add(m);
            }
        };

        (ControllerSession session, _, PeerConnector connector) = fixture.Bed.CreateController();
        Task<LoginResult> login = ConnectAndLoginAsync(session, connector, fixture.Host.Identity.Id);
        await Testbed.WaitUntilAsync(
            () => Snapshot(seen).Any(m => m.UnionCase == IpcMessage.UnionOneofCase.ApprovalRequest),
            "the ui sees the request");

        // The main window shows the request, but accepting from there must not let anyone in.
        int connId = Snapshot(seen).First(m => m.UnionCase == IpcMessage.UnionOneofCase.ApprovalRequest).ApprovalRequest.ConnId;
        await ui.SendAsync(new IpcMessage { ApprovalDecision = new ApprovalDecision { ConnId = connId, Accept = true } });

        LoginResult result = await login;
        result.Success.ShouldBeFalse();
    }

    private static async Task<LoginResult> ConnectAndLoginAsync(ControllerSession session, PeerConnector connector, string id)
    {
        await session.ConnectAsync(connector, id, CancellationToken.None);
        return await session.LoginAsync(null, CancellationToken.None);
    }

    private static List<IpcMessage> Snapshot(List<IpcMessage> events)
    {
        lock (events)
        {
            return [.. events];
        }
    }

    /// <summary>An engine with its IPC server, and a connection manager standing by to answer approvals.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private IpcClient _cm = null!;

        private Fixture(Testbed bed, HostRuntime host, IpcEndpoint endpoint, byte[] token)
        {
            Bed = bed;
            Host = host;
            Endpoint = endpoint;
            Token = token;
        }

        public Testbed Bed { get; }

        public HostRuntime Host { get; }

        public IpcEndpoint Endpoint { get; }

        public byte[] Token { get; }

        public static async Task<Fixture> StartAsync(HostPolicy policy)
        {
            Testbed bed = await Testbed.StartAsync();
            string dir = bed.NewTempDir();
            var store = new FileSecretStore(dir);
            PeerIdentityStore identity = await PeerIdentityStore.LoadOrCreateAsync(store, await StoredMachineIdProvider.LoadOrCreateAsync(store));
            HostPasswords passwords = await HostPasswords.LoadAsync(store);
            var bridge = new HostIpcBridge(new HostConfigStore(Path.Combine(dir, "config.json")), bed.Logs.CreateLogger("bridge"));
            var runtime = new HostRuntime(bed.Settings, identity, passwords, policy, bridge, bed.Logs) { DirectAccessPort = 0 };
            IpcEndpoint endpoint = IpcEndpoint.ForTest();
            byte[] token = IpcServer.NewToken();
            var server = new IpcServer(endpoint, token, bridge, bed.Logs.CreateLogger("ipc"));
            bridge.Attach(runtime, server);
            server.Start();
            bed.Track(server);
            bed.Track(runtime);
            await runtime.StartAsync();
            await Testbed.WaitUntilAsync(() => !runtime.Identity.IsLocalId, "host id");

            var fixture = new Fixture(bed, runtime, endpoint, token);

            // A connection manager has to be attached, or click mode has nobody to ask.
            fixture._cm = await IpcClient.ConnectAsync(endpoint, token, IpcRoles.ConnectionManager, bed.Logs.CreateLogger("cm"));
            return fixture;
        }

        public Task<IpcClient> ConnectAsync(string role) =>
            IpcClient.ConnectAsync(Endpoint, Token, role, Bed.Logs.CreateLogger(role));

        public Task ApproveAsync(int connId) =>
            _cm.SendAsync(new IpcMessage { ApprovalDecision = new ApprovalDecision { ConnId = connId, Accept = true, Granted = { Permission.PermKeyboard } } });

        public Task RejectAsync(int connId) =>
            _cm.SendAsync(new IpcMessage { ApprovalDecision = new ApprovalDecision { ConnId = connId, Accept = false } });

        public async ValueTask DisposeAsync()
        {
            await _cm.DisposeAsync();
            await Bed.DisposeAsync();
        }
    }
}
