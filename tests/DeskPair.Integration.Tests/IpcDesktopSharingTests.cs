using DeskPair.Core.Config;
using DeskPair.Core.Ipc;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Session.Host.Auth;
using DeskPair.Core.Transport;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Protocol.Ipc;
using DesktopSharingStateMessage = DeskPair.Protocol.Ipc.DesktopSharingState;
using DesktopSharingStateValue = DeskPair.Platform.Abstractions.Capture.DesktopSharingState;

namespace DeskPair.Integration.Tests;

/// <summary>
/// The settings page asks the unattended engine to have the user allow sharing their desktop: the engine answers
/// for the caller's own account, asks it on its own screen, and answers when the person has -- without holding up
/// anything else the same window asks meanwhile. A Unix socket says who is calling; the Windows pipe of this test
/// does not, and a caller with no uid has no desktop of its own to speak of.
/// </summary>
public class IpcDesktopSharingTests
{
    [Fact]
    public async Task The_user_is_asked_on_their_own_screen_and_nothing_else_waits_for_them()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (IpcServer server, byte[] token, IpcEndpoint endpoint, HostIpcBridge bridge) = await StartServiceAsync(bed);
        var consent = new FakeConsent();
        bridge.DesktopSharing = consent;
        await using IpcClient app = await IpcClient.ConnectAsync(endpoint, token, IpcRoles.Ui, bed.Logs.CreateLogger("app"));

        IpcMessage state = await app.RequestAsync(new IpcMessage { DesktopSharingRequest = new DesktopSharingRequest() });
        if (OperatingSystem.IsWindows())
        {
            state.DesktopSharingState.State.ShouldBe(0, "no uid, so no desktop of the caller's");
            consent.Uids.ShouldBeEmpty();
            return;
        }

        state.DesktopSharingState.State.ShouldBe((int)DesktopSharingStateValue.NotYet);
        consent.Uids.ShouldHaveSingleItem();

        Task<IpcMessage> asking = app.RequestAsync(new IpcMessage { DesktopSharingRequest = new DesktopSharingRequest { Ask = true } }, default, TimeSpan.FromSeconds(30));
        await Testbed.WaitUntilAsync(() => consent.Asking, "the engine asks");
        (await app.RequestAsync(new IpcMessage { GetId = new GetId() })).UnionCase.ShouldBe(IpcMessage.UnionOneofCase.IdChanged, "the window is not held up while the person decides");
        asking.IsCompleted.ShouldBeFalse();

        consent.Answer(DesktopSharingOutcome.Allowed);

        DesktopSharingStateMessage answer = (await asking).DesktopSharingState;
        answer.Outcome.ShouldBe((int)DesktopSharingOutcome.Allowed + 1);
        answer.State.ShouldBe((int)DesktopSharingStateValue.Allowed);
        server.ClientCount.ShouldBe(1);
    }

    [Fact]
    public async Task An_engine_that_cannot_share_a_desktop_says_so()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (_, byte[] token, IpcEndpoint endpoint, _) = await StartServiceAsync(bed);
        await using IpcClient app = await IpcClient.ConnectAsync(endpoint, token, IpcRoles.Ui, bed.Logs.CreateLogger("app"));

        IpcMessage asked = await app.RequestAsync(new IpcMessage { DesktopSharingRequest = new DesktopSharingRequest { Ask = true } });

        asked.DesktopSharingState.State.ShouldBe(0);
        asked.DesktopSharingState.Outcome.ShouldBe(0, "nobody was asked");
    }

    private static async Task<(IpcServer Server, byte[] Token, IpcEndpoint Endpoint, HostIpcBridge Bridge)> StartServiceAsync(Testbed bed)
    {
        string dir = bed.NewTempDir();
        var store = new FileSecretStore(dir);
        PeerIdentityStore identity = await PeerIdentityStore.LoadOrCreateAsync(store, await StoredMachineIdProvider.LoadOrCreateAsync(store));
        HostPasswords passwords = await HostPasswords.LoadAsync(store);
        var bridge = new HostIpcBridge(new HostConfigStore(Path.Combine(dir, "config.json")), bed.Logs.CreateLogger("bridge"));
        var runtime = new HostRuntime(bed.Settings, identity, passwords, new HostPolicy(), bridge, bed.Logs) { DirectAccessPort = 0 };
        IpcEndpoint endpoint = IpcEndpoint.ForTest();
        byte[] token = IpcServer.NewToken();
        var server = new IpcServer(endpoint, token, bridge, bed.Logs.CreateLogger("ipc"));
        bridge.Attach(runtime, server);
        server.Start();
        bed.Track(server);
        bed.Track(runtime);
        await runtime.StartAsync();
        return (server, token, endpoint, bridge);
    }

    /// <summary>A desktop whose user has not allowed anything yet, and answers when told to.</summary>
    private sealed class FakeConsent : IDesktopSharingConsent
    {
        private readonly TaskCompletionSource<DesktopSharingOutcome> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DesktopSharingStateValue _state = DesktopSharingStateValue.NotYet;

        public List<uint> Uids { get; } = [];

        public bool Asking { get; private set; }

        public void Answer(DesktopSharingOutcome outcome) => _answer.TrySetResult(outcome);

        public Task<DesktopSharingStateValue> SharingStateAsync(uint uid, CancellationToken ct)
        {
            lock (Uids)
            {
                Uids.Add(uid);
            }

            return Task.FromResult(_state);
        }

        public async Task<(DesktopSharingOutcome Outcome, string? Detail)> AskSharingAsync(uint uid, CancellationToken ct)
        {
            Asking = true;
            DesktopSharingOutcome outcome = await _answer.Task;
            _state = outcome == DesktopSharingOutcome.Allowed ? DesktopSharingStateValue.Allowed : _state;
            return (outcome, null);
        }
    }
}
