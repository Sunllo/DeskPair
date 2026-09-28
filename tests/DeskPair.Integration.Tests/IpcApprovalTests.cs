using DeskPair.Core.Config;
using DeskPair.Core.Ipc;
using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Session.Host.Auth;
using DeskPair.Core.Transport;
using DeskPair.Protocol.Ipc;
using DeskPair.Protocol.Messages;

namespace DeskPair.Integration.Tests;

/// <summary>The service process as it will exist: runtime + IPC server + bridge; the "connection manager" is an IpcClient.</summary>
public class IpcApprovalTests
{
    private static async Task<(HostRuntime Runtime, HostPasswords Passwords, IpcServer Server, byte[] Token, IpcEndpoint Endpoint, HostIpcBridge Bridge)> StartServiceAsync(Testbed bed, HostPolicy policy)
    {
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
        return (runtime, passwords, server, token, endpoint, bridge);
    }

    [Fact]
    public async Task Connection_manager_approves_over_ipc_and_sees_session_events()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, _, IpcServer server, byte[] token, IpcEndpoint endpoint, _) = await StartServiceAsync(bed, new HostPolicy { ApproveMode = ApproveMode.ApproveClick });

        await using IpcClient cm = await IpcClient.ConnectAsync(endpoint, token, IpcRoles.ConnectionManager, bed.Logs.CreateLogger("cm"));
        var events = new List<IpcMessage>();
        cm.Pushed += m =>
        {
            lock (events)
            {
                events.Add(m);
            }

            if (m.UnionCase == IpcMessage.UnionOneofCase.ApprovalRequest)
            {
                _ = cm.SendAsync(new IpcMessage { ApprovalDecision = new ApprovalDecision { ConnId = m.ApprovalRequest.ConnId, Accept = true, Granted = { Permission.PermKeyboard } } });
            }
        };
        (await cm.RequestAsync(new IpcMessage { GetId = new GetId() })).IdChanged.Id.ShouldBe(host.Identity.Id);

        (ControllerSession session, TestCallbacks cb, PeerConnector connector) = bed.CreateController();
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        LoginResult r = await session.LoginAsync(null, CancellationToken.None);
        r.Success.ShouldBeTrue();
        await Testbed.WaitUntilAsync(() => cb.Permissions.Any(p => p.Permission == Permission.PermClipboard && !p.Enabled), "granted set narrowed permissions");
        cb.Permissions.First(p => p.Permission == Permission.PermKeyboard).Enabled.ShouldBeTrue();

        await session.SendChatAsync("hi cm");
        await Testbed.WaitUntilAsync(() => Snapshot(events).Any(m => m.UnionCase == IpcMessage.UnionOneofCase.Chat && m.Chat.Text == "hi cm"), "chat relayed to cm");
        int connId = Snapshot(events).First(m => m.UnionCase == IpcMessage.UnionOneofCase.ApprovalRequest).ApprovalRequest.ConnId;

        await cm.SendAsync(new IpcMessage { Chat = new ChatRelay { ConnId = connId, Text = "hello from cm", FromPeer = false } });
        await Testbed.WaitUntilAsync(() => cb.Chats.Any(c => c.Text == "hello from cm"), "chat from cm");

        await cm.SendAsync(new IpcMessage { CloseConnection = new CloseConnection { ConnId = connId } });
        await Testbed.WaitUntilAsync(() => cb.CloseReason is not null, "closed by cm");
        await Testbed.WaitUntilAsync(() => Snapshot(events).Any(m => m.UnionCase == IpcMessage.UnionOneofCase.ConnectionClosed), "close event");
        server.ClientCount.ShouldBe(1);
    }

    /// <summary>
    /// Accepting a terminal is a deliberate act. The request names the account the shell would run as;
    /// an acceptance that does not name the terminal permission -- an empty list, or a connection manager
    /// too old to know about terminals -- does not grant it, whatever the policy says.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_terminal_is_granted_only_when_the_acceptance_names_it(bool named)
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, _, _, byte[] token, IpcEndpoint endpoint, HostIpcBridge bridge) = await StartServiceAsync(bed, new HostPolicy { ApproveMode = ApproveMode.ApproveClick, TerminalEnabled = true });
        bridge.TerminalIdentity = () => "root";
        await using IpcClient cm = await IpcClient.ConnectAsync(endpoint, token, IpcRoles.ConnectionManager, bed.Logs.CreateLogger("cm"));
        ApprovalRequest? asked = null;
        cm.Pushed += m =>
        {
            if (m.UnionCase == IpcMessage.UnionOneofCase.ApprovalRequest)
            {
                asked = m.ApprovalRequest;
                var decision = new ApprovalDecision { ConnId = m.ApprovalRequest.ConnId, Accept = true };
                if (named)
                {
                    decision.Granted.Add(Permission.PermTerminal);
                }

                _ = cm.SendAsync(new IpcMessage { ApprovalDecision = decision });
            }
        };

        (ControllerSession session, TestCallbacks cb, PeerConnector connector) = bed.CreateController(connType: Protocol.Rendezvous.ConnType.ConnTerminal);
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(null, CancellationToken.None)).Success.ShouldBeTrue();

        asked.ShouldNotBeNull();
        asked.ConnType.ShouldBe(Protocol.Rendezvous.ConnType.ConnTerminal);
        asked.TerminalIdentity.ShouldBe("root");
        await Testbed.WaitUntilAsync(() => cb.Permissions.Any(p => p.Permission == Permission.PermTerminal && p.Enabled == named), "the terminal permission as decided");
        host.Sessions.Single().Context.Permissions.Has(Permission.PermTerminal).ShouldBe(named);
    }

    [Fact]
    public async Task Without_a_connection_manager_click_mode_rejects()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, _, _, _, _, _) = await StartServiceAsync(bed, new HostPolicy { ApproveMode = ApproveMode.ApproveClick });
        (ControllerSession session, _, PeerConnector connector) = bed.CreateController();
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        LoginResult r = await session.LoginAsync(null, CancellationToken.None);
        r.Success.ShouldBeFalse();
        r.Error!.Code.ShouldBe(LoginError.Types.Code.RejectedByUser);
    }

    [Fact]
    public async Task Ui_reads_and_updates_password_and_config()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, HostPasswords passwords, _, byte[] token, IpcEndpoint endpoint, HostIpcBridge bridge) = await StartServiceAsync(bed, new HostPolicy());
        await using IpcClient ui = await IpcClient.ConnectAsync(endpoint, token, IpcRoles.Ui, bed.Logs.CreateLogger("ui"));

        (await ui.RequestAsync(new IpcMessage { GetTempPassword = new GetTempPassword() })).TempPassword.Password.ShouldBe(passwords.TemporaryPassword);
        (await ui.RequestAsync(new IpcMessage { SetPermanentPassword = new SetPermanentPassword { Password = "hunter2" } })).PasswordAck.Ok.ShouldBeTrue();
        passwords.HasPermanentPassword.ShouldBeTrue();

        HostConfig? changed = null;
        bridge.ConfigChanged += c => changed = c;
        var cfg = new HostConfig { RendezvousServer = "rdv:21116", AudioEnabled = false };
        IpcMessage reply = await ui.RequestAsync(new IpcMessage { SetConfig = new SetConfig { Json = cfg.ToJson() } });
        HostConfig.FromJson(reply.ConfigSnapshot.Json).ShouldBe(cfg);
        changed.ShouldBe(cfg);
        HostConfig.FromJson((await ui.RequestAsync(new IpcMessage { GetConfig = new GetConfig() })).ConfigSnapshot.Json).ShouldBe(cfg);

        (ControllerSession session, _, PeerConnector connector) = bed.CreateController();
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync("hunter2", CancellationToken.None)).Success.ShouldBeTrue();
    }

    /// <summary>
    /// A window whose account is not the owner of this computer is told no, and told it in words: silence was the old
    /// answer, and a window that hears nothing waits out its timeout, takes that for a lost engine and reconnects. On
    /// a machine whose owner worked over remote desktop the home page said "host service not running" for a moment
    /// every twelve seconds, while the service ran fine. Nothing is read or written on the refusal's way out, and what
    /// anybody may ask is still answered -- the window keeps its link and its id.
    /// </summary>
    [Fact]
    public async Task A_caller_who_is_not_the_owner_is_told_no_and_nothing_changes()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, HostPasswords passwords, _, _, _, HostIpcBridge bridge) = await StartServiceAsync(bed, new HostPolicy());
        var stranger = new IpcClientInfo(7, IpcRoles.Ui) { Caller = IpcCaller.Unknown };

        IpcMessage? read = await bridge.HandleAsync(new IpcMessage { GetTempPassword = new GetTempPassword() }, stranger, CancellationToken.None);
        IpcMessage? written = await bridge.HandleAsync(
            new IpcMessage { SetPermanentPassword = new SetPermanentPassword { Password = "hunter2" } }, stranger, CancellationToken.None);
        IpcMessage? id = await bridge.HandleAsync(new IpcMessage { GetId = new GetId() }, stranger, CancellationToken.None);

        read.ShouldNotBeNull().UnionCase.ShouldBe(IpcMessage.UnionOneofCase.Refused);
        read.Refused.Reason.ShouldNotBeEmpty();
        read.ToString().ShouldNotContain(passwords.TemporaryPassword);
        written.ShouldNotBeNull().UnionCase.ShouldBe(IpcMessage.UnionOneofCase.Refused);
        passwords.HasPermanentPassword.ShouldBeFalse();
        id.ShouldNotBeNull().IdChanged.Id.ShouldBe(host.Identity.Id);
    }

    private static List<IpcMessage> Snapshot(List<IpcMessage> events)
    {
        lock (events)
        {
            return events.ToList();
        }
    }
}
