using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;
using DeskPair.Desktop.ViewModels;
using DeskPair.Protocol.Messages;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// The viewer offers to ask for the secure desktop only when the host can raise a helper, this viewer may type, and a
/// UAC prompt is actually up; and it says where a request stands. Nothing here clicks the UAC -- that stays with the
/// person at the host.
/// </summary>
[Collection("ProcessState")] // reads the string table, which the language tests move
public class ElevationBannerTests
{
    private static RemoteSessionViewModel Connected(bool elevation)
    {
        var vm = new RemoteSessionViewModel("123456789", "me", new DesktopConfig(), NullLoggerFactory.Instance);
        vm.OnPeerInfo(new PeerInfo { Hostname = "Office", Platform = "Windows", MultiDisplay = true, Elevation = elevation });
        Dispatcher.UIThread.RunJobs();
        return vm;
    }

    private static void Say(RemoteSessionViewModel vm, SecureDesktop.Types.Kind kind, SecureDesktop.Types.Elevation elevation)
    {
        vm.OnSecureDesktop(new SecureDesktop { Kind = kind, Elevation = elevation });
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void The_button_shows_on_a_uac_prompt_and_the_status_follows_the_request()
    {
        RemoteSessionViewModel vm = Connected(elevation: true);

        Say(vm, SecureDesktop.Types.Kind.SdUac, SecureDesktop.Types.Elevation.ElNone);
        vm.CanRequestElevation.ShouldBeTrue("a UAC is up, the host can elevate, and this viewer may type");
        vm.HasElevationStatus.ShouldBeFalse("nobody has asked yet");

        Say(vm, SecureDesktop.Types.Kind.SdUac, SecureDesktop.Types.Elevation.ElRequested);
        vm.CanRequestElevation.ShouldBeFalse("a request is already in flight");
        vm.ElevationStatus.ShouldBe(Strings.Get("session.elevation.requested"));

        Say(vm, SecureDesktop.Types.Kind.SdUac, SecureDesktop.Types.Elevation.ElActive);
        vm.CanRequestElevation.ShouldBeFalse();
        vm.ElevationStatus.ShouldBe(Strings.Get("session.elevation.active"));

        Say(vm, SecureDesktop.Types.Kind.SdUac, SecureDesktop.Types.Elevation.ElRefused);
        vm.CanRequestElevation.ShouldBeTrue("refused, so the viewer may try again");
        vm.ElevationStatus.ShouldBe(Strings.Get("session.elevation.refused"));

        Say(vm, SecureDesktop.Types.Kind.SdNone, SecureDesktop.Types.Elevation.ElNone);
        vm.CanRequestElevation.ShouldBeFalse("back on an ordinary desktop: nothing to ask about");
    }

    [AvaloniaFact]
    public void A_host_that_cannot_elevate_never_offers_the_button()
    {
        RemoteSessionViewModel vm = Connected(elevation: false);
        Say(vm, SecureDesktop.Types.Kind.SdUac, SecureDesktop.Types.Elevation.ElNone);
        vm.CanRequestElevation.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void A_viewer_that_may_not_type_never_offers_the_button()
    {
        RemoteSessionViewModel vm = Connected(elevation: true);
        vm.OnPermission(new PermissionInfo { Permission = Permission.PermKeyboard, Enabled = false });
        Dispatcher.UIThread.RunJobs();

        Say(vm, SecureDesktop.Types.Kind.SdUac, SecureDesktop.Types.Elevation.ElNone);
        vm.CanRequestElevation.ShouldBeFalse();
    }
}
