using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Transport;
using DeskPair.Protocol.Messages;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Integration.Tests;

public class RelayedSessionTests
{
    [Fact]
    public async Task Host_registers_and_gets_a_nine_digit_id()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, _, _) = await bed.StartHostAsync();
        host.Identity.Id.Length.ShouldBe(9);
        host.Identity.Id.All(char.IsAsciiDigit).ShouldBeTrue();
        await Testbed.WaitUntilAsync(() => host.Rendezvous.SignedIdentity is not null, "signed identity");
    }

    [Fact]
    public async Task Controller_logs_in_through_relay_and_chats_both_ways()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, TestApprover approver) = await bed.StartHostAsync();
        (ControllerSession session, TestCallbacks cb, PeerConnector connector) = bed.CreateController(forceRelay: true);

        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        session.State.ShouldBe(ControllerSessionState.AwaitingLogin);
        session.TransportKind.ShouldBe(TransportKind.Relay);
        session.Challenge!.ApproveMode.ShouldBe(ApproveMode.ApprovePassword);

        LoginResult result = await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None);
        result.Success.ShouldBeTrue();
        result.PeerInfo!.Hostname.ShouldBe(Environment.MachineName);
        result.PeerInfo.Granted.ShouldContain(Permission.PermKeyboard);
        session.State.ShouldBe(ControllerSessionState.Authorized);
        await Testbed.WaitUntilAsync(() => host.Sessions.Any(s => s.Context.State == HostSessionState.Authorized), "host authorized");

        await session.SendChatAsync("hello host");
        await Testbed.WaitUntilAsync(() => approver.Chats.Contains("hello host"), "host chat");

        HostSession hostSession = host.Sessions.Single();
        await hostSession.Context.SendAsync(new Message { Misc = new Misc { Chat = new ChatMessage { Text = "hello controller" } } });
        await Testbed.WaitUntilAsync(() => cb.Chats.Any(c => c.Text == "hello controller"), "controller chat");

        await session.SendPingAsync();
        await Testbed.WaitUntilAsync(() => cb.LastRtt is not null, "rtt");
        await Testbed.WaitUntilAsync(() => cb.Permissions.Count >= 5, "permission infos");

        await session.CloseAsync("done");
        await Testbed.WaitUntilAsync(() => host.Sessions.Count == 0, "host session removed");
        approver.Events.ShouldContain(e => e.Kind == HostEventKind.Closed);
    }

    /// <summary>
    /// One wrong guess is answered as such; the next one from the same address, straight after, is
    /// not looked at -- it is told how long to wait. The wait doubles with each failure (see
    /// <c>LoginFailureTracker</c>), so a guesser gets one answer every two, four, eight seconds.
    /// </summary>
    [Fact]
    public async Task Wrong_password_is_rejected_and_the_next_guess_has_to_wait()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, _, _) = await bed.StartHostAsync(new HostPolicy { FailedLoginDelay = TimeSpan.FromMilliseconds(10) });

        (ControllerSession session, _, PeerConnector connector) = bed.CreateController();
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);

        LoginResult first = await session.LoginAsync("definitely-wrong", CancellationToken.None);
        first.Success.ShouldBeFalse();
        first.Error!.Code.ShouldBe(LoginError.Types.Code.WrongPassword);

        LoginResult second = await session.LoginAsync("definitely-wrong", CancellationToken.None);
        second.Success.ShouldBeFalse();
        second.Error!.Code.ShouldBe(LoginError.Types.Code.TooManyAttempts);
        second.Error.RetryAfterMs.ShouldBeInRange(1u, 2000u, "the first failure costs two seconds");
        await session.CloseAsync("next");

        // A fresh connection does not reset the wait: it is the address that is being held off.
        (ControllerSession again, _, PeerConnector connector2) = bed.CreateController();
        await again.ConnectAsync(connector2, host.Identity.Id, CancellationToken.None);
        LoginResult third = await again.LoginAsync("definitely-wrong", CancellationToken.None);
        third.Error!.Code.ShouldBe(LoginError.Types.Code.TooManyAttempts);
        await again.CloseAsync("done");
    }

    [Fact]
    public async Task Click_mode_asks_the_host_user_and_honours_rejection()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, _, TestApprover approver) = await bed.StartHostAsync(new HostPolicy { ApproveMode = ApproveMode.ApproveClick });
        approver.Decision = false;

        (ControllerSession session, TestCallbacks cb, PeerConnector connector) = bed.CreateController();
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        session.Challenge!.ApproveMode.ShouldBe(ApproveMode.ApproveClick);
        LoginResult r = await session.LoginAsync(null, CancellationToken.None);
        r.Success.ShouldBeFalse();
        r.Error!.Code.ShouldBe(LoginError.Types.Code.RejectedByUser);
        approver.Requests.Single().PeerId.ShouldBe("controller");
        cb.States.ShouldContain(ControllerSessionState.AwaitingApproval);

        approver.Decision = true;
        (ControllerSession session2, _, _) = bed.CreateController("controller-2");
        await session2.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session2.LoginAsync(null, CancellationToken.None)).Success.ShouldBeTrue();
    }

    [Fact]
    public async Task Unknown_id_and_offline_peer_fail_fast()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (ControllerSession session, _, PeerConnector connector) = bed.CreateController();
        PeerConnectException e = await Should.ThrowAsync<PeerConnectException>(() => session.ConnectAsync(connector, "123456789", CancellationToken.None));
        e.Failure.ShouldBe(PunchHoleResponse.Types.Failure.IdNotExist);
    }

    [Fact]
    public async Task Direct_connection_bypasses_the_servers()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(directPort: -1);
        (ControllerSession session, _, PeerConnector connector) = bed.CreateController();

        await session.ConnectAsync(connector, $"127.0.0.1:{host.BoundDirectAccessPort}", CancellationToken.None);
        session.TransportKind.ShouldBe(TransportKind.DirectTcp);
        (await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();
    }

    [Fact]
    public async Task Host_disconnect_reaches_the_controller()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync();
        (ControllerSession session, TestCallbacks cb, PeerConnector connector) = bed.CreateController();
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();

        await host.Sessions.Single().CloseAsync("kicked");
        await Testbed.WaitUntilAsync(() => cb.CloseReason is not null, "controller close");
        cb.CloseReason!.ShouldContain("kicked");
        session.State.ShouldBe(ControllerSessionState.Closed);
    }
}
