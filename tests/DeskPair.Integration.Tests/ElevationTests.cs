using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Transport;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Protocol.Messages;

namespace DeskPair.Integration.Tests;

/// <summary>
/// A viewer may ask a Windows app-mode host to raise a helper so it can see and drive the secure desktop (a UAC
/// prompt). The person at the host allows it and completes the real UAC themselves; nothing clicks it for them.
/// The host tells the viewer where the request stands. A host with no elevator never advertises the capability, so
/// a viewer never sends the request to one that would not understand it.
/// </summary>
public class ElevationTests
{
    private static async Task<(ControllerSession Session, TestCallbacks Cb)> ConnectAsync(Testbed bed, HostRuntime host, string password, SessionOptions? options = null)
    {
        (ControllerSession session, TestCallbacks cb, PeerConnector connector) = bed.CreateController(options: options);
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(password, CancellationToken.None)).Success.ShouldBeTrue();
        return (session, cb);
    }

    private static SecureDesktop.Types.Elevation? Last(TestCallbacks cb) =>
        cb.SecureDesktops.Count > 0 ? cb.SecureDesktops[^1].Elevation : null;

    [Fact]
    public async Task An_allowed_request_that_completes_the_uac_elevates_the_session()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, elevation: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);

        cb.PeerInfo!.Elevation.ShouldBeTrue("the host advertises it can elevate");
        session.HostSupportsElevation.ShouldBeTrue();

        bed.ApproveElevation = true;
        bed.Elevator!.Succeed = true;
        await session.RequestElevationAsync();

        await Testbed.WaitUntilAsync(() => Last(cb) == SecureDesktop.Types.Elevation.ElActive, "the session becomes elevated", 15_000);
        bed.ElevationAsks.Count.ShouldBe(1, "the person at the host was asked once");
        bed.Elevator!.Raised.ShouldBe(1);
    }

    [Fact]
    public async Task A_permanent_approval_tells_the_elevator_to_make_the_device_permanent()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, elevation: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);

        bed.ApproveElevation = true;
        bed.ElevationPermanent = true; // the person ticks "allow this device unattended from now on"
        bed.Elevator!.Succeed = true;
        await session.RequestElevationAsync();
        await Testbed.WaitUntilAsync(() => Last(cb) == SecureDesktop.Types.Elevation.ElActive, "the session becomes elevated", 15_000);

        bed.Elevator!.LastPermanent.ShouldBeTrue("the elevator is asked to make it permanent");
        bed.Elevator!.LastPeerId.ShouldBe("controller"); // the test controller's id, to be added to the allowed devices
    }

    [Fact]
    public async Task An_elevated_session_holds_when_the_desktop_briefly_returns_to_an_ordinary_one()
    {
        // Raising the helper takes seconds of real UAC choreography, during which the desktop flaps back to an
        // ordinary one between prompts. Lowering on that transient tore the helper down a few hundred ms after it
        // came up, so the secure desktop the viewer asked to see was never served. Elevation now holds for the
        // connection instead (lowered when it ends or the helper dies), matching "elevated for this connection".
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, elevation: true, secureDesktop: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);

        bed.SecureDesktop!.Kind = SecureDesktopKind.Uac;
        bed.ApproveElevation = true;
        bed.Elevator!.Succeed = true;
        await session.RequestElevationAsync();
        await Testbed.WaitUntilAsync(() => Last(cb) == SecureDesktop.Types.Elevation.ElActive, "the session becomes elevated", 15_000);

        // The desktop goes back to ordinary (the bootstrap settled / the prompt was dismissed).
        bed.SecureDesktop!.Kind = SecureDesktopKind.None;
        await Task.Delay(1_500);

        bed.Elevator!.Lowered.ShouldBe(0, "a transient ordinary desktop must not lower the helper");
        Last(cb).ShouldBe(SecureDesktop.Types.Elevation.ElActive, "the session stays elevated for the connection");
    }

    [Fact]
    public async Task A_denied_request_is_refused_and_no_helper_is_raised()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, elevation: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);

        bed.ApproveElevation = false;
        await session.RequestElevationAsync();

        await Testbed.WaitUntilAsync(() => Last(cb) == SecureDesktop.Types.Elevation.ElRefused, "refused when the person says no", 15_000);
        bed.Elevator!.Raised.ShouldBe(0);
    }

    [Fact]
    public async Task A_dismissed_uac_is_refused()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, elevation: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);

        bed.ApproveElevation = true;
        bed.Elevator!.Succeed = false; // allowed at the prompt, but the real UAC was dismissed
        await session.RequestElevationAsync();

        await Testbed.WaitUntilAsync(() => Last(cb) == SecureDesktop.Types.Elevation.ElRefused, "refused when the UAC is dismissed", 15_000);
        bed.Elevator!.Raised.ShouldBe(1);
    }

    [Fact]
    public async Task A_viewer_who_may_not_type_is_refused_without_asking()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, elevation: true);
        (ControllerSession session, _) = await ConnectAsync(bed, host, passwords.TemporaryPassword, new SessionOptions { DisableKeyboard = BoolOption.BoYes });

        await session.RequestElevationAsync();
        await Task.Delay(1_000);

        bed.ElevationAsks.ShouldBeEmpty("someone who may not type is never even asked");
        bed.Elevator!.Raised.ShouldBe(0);
    }

    [Fact]
    public async Task A_host_with_no_elevator_never_advertises_it_and_the_request_is_a_no_op()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);

        cb.PeerInfo!.Elevation.ShouldBeFalse();
        session.HostSupportsElevation.ShouldBeFalse();

        // The capability guard makes the request a no-op, so an old host never sees an unknown message or closes.
        await session.RequestElevationAsync();
        await Task.Delay(500);
        cb.SecureDesktops.ShouldBeEmpty();
    }
}
