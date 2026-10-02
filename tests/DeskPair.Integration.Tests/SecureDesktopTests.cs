using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Testing;
using DeskPair.Core.Transport;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Protocol.Messages;

namespace DeskPair.Integration.Tests;

/// <summary>
/// When the host's engine slips onto a desktop it cannot read -- a UAC prompt on the secure desktop, or the lock
/// screen -- it tells every viewer, and tells one that connects while it is already there. Back on an ordinary
/// desktop, it clears it. A host with no monitor (the SYSTEM engine, or a system with no secure desktop) says nothing.
/// </summary>
public class SecureDesktopTests
{
    private static async Task<(ControllerSession Session, TestCallbacks Cb)> ConnectAsync(Testbed bed, HostRuntime host, string password)
    {
        (ControllerSession session, TestCallbacks cb, PeerConnector connector) = bed.CreateController();
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(password, CancellationToken.None)).Success.ShouldBeTrue();
        return (session, cb);
    }

    private static SecureDesktop.Types.Kind? Last(TestCallbacks cb) =>
        cb.SecureDesktops.Count > 0 ? cb.SecureDesktops[^1].Kind : null;

    [Fact]
    public async Task A_viewer_is_told_when_the_host_hits_a_uac_prompt_and_when_it_clears()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, secureDesktop: true);
        (_, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);

        bed.SecureDesktop!.Kind = SecureDesktopKind.Uac;
        await Testbed.WaitUntilAsync(() => Last(cb) == SecureDesktop.Types.Kind.SdUac, "told about the UAC prompt", 15_000);

        bed.SecureDesktop!.Kind = SecureDesktopKind.None;
        await Testbed.WaitUntilAsync(() => Last(cb) == SecureDesktop.Types.Kind.SdNone, "told the prompt cleared", 15_000);
    }

    [Fact]
    public async Task A_viewer_connecting_while_the_screen_is_locked_hears_about_it()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, secureDesktop: true);

        // Lock it, then leave time for the host to poll and take it in before anyone connects: what the joining
        // viewer then hears comes from the on-connect send, not a change broadcast.
        bed.SecureDesktop!.Kind = SecureDesktopKind.Locked;
        await Task.Delay(1_200);

        (_, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        await Testbed.WaitUntilAsync(() => Last(cb) == SecureDesktop.Types.Kind.SdLocked, "the locked screen on connect", 15_000);
    }

    [Fact]
    public async Task Under_the_listed_only_policy_only_a_listed_device_sees_the_secure_desktop()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, secureDesktop: true);
        host.SecureDesktopForListedOnly = true;
        host.SecureDesktopList = DeskPair.Core.Session.Host.Auth.PeerAllowlist.Create(true, ["id:listed-device"], out _);

        // A SYSTEM engine on the secure desktop: it can read it (Kind None) but it is still the secure desktop.
        bed.SecureDesktop!.Kind = SecureDesktopKind.None;
        bed.SecureDesktop!.OnSecureDesktop = true;
        await Testbed.WaitUntilAsync(() => host.OnSecureDesktop, "the host takes in the secure desktop", 5_000);

        // A device not on the list is told there is a UAC it may not see.
        (ControllerSession outsider, TestCallbacks outCb, PeerConnector c1) = bed.CreateController(myId: "outsider");
        await outsider.ConnectAsync(c1, host.Identity.Id, CancellationToken.None);
        (await outsider.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();
        await Testbed.WaitUntilAsync(() => Last(outCb) == SecureDesktop.Types.Kind.SdUac, "a non-listed device gets the banner", 15_000);

        // A listed device is shown it: Kind None, so it is told nothing (there is no banner to raise).
        (ControllerSession listed, TestCallbacks listedCb, PeerConnector c2) = bed.CreateController(myId: "listed-device");
        await listed.ConnectAsync(c2, host.Identity.Id, CancellationToken.None);
        (await listed.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();
        await Task.Delay(1_200);
        (Last(listedCb) is null or SecureDesktop.Types.Kind.SdNone).ShouldBeTrue("a listed device is not shut out of the secure desktop");
    }

    [Fact]
    public async Task Without_a_monitor_no_secure_desktop_message_is_ever_sent()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true);
        (_, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);

        await Task.Delay(1_500);
        cb.SecureDesktops.ShouldBeEmpty("a host with no monitor never speaks of a secure desktop");
    }
}
