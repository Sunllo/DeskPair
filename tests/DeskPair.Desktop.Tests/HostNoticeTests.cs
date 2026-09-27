using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Desktop.Services;
using DeskPair.Desktop.ViewModels;
using DeskPair.Protocol.Messages;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// The host speaking for itself about its displays: a Wayland desktop that is waiting for its person to allow
/// screen sharing, or whose person said no, is not "a computer with no screen", and the viewer says what the host
/// said instead.
/// </summary>
public class HostNoticeTests
{
    [AvaloniaFact]
    public void The_hosts_words_replace_no_screen_until_there_is_a_screen()
    {
        var vm = new RemoteSessionViewModel("123456789", "me", new DesktopConfig(), NullLoggerFactory.Instance);
        vm.OnPeerInfo(new PeerInfo { Hostname = "Desk", Platform = "Linux", MultiDisplay = true });
        Dispatcher.UIThread.RunJobs();
        vm.HasNoDisplays.ShouldBeTrue();
        vm.HasHostNotice.ShouldBeFalse("nothing said yet: the ordinary no-screen text");

        vm.OnDisplaysChanged(new DisplaysChanged { CurrentDisplay = 0, Changed = -1, Notice = "Waiting for someone there to allow it." });
        Dispatcher.UIThread.RunJobs();
        vm.HostNotice.ShouldBe("Waiting for someone there to allow it.");
        vm.HasHostNotice.ShouldBeTrue();
        vm.HasNoDisplays.ShouldBeTrue();

        var opened = new DisplaysChanged { CurrentDisplay = 0, Changed = -1 };
        opened.Displays.Add(new DisplayInfo { Width = 1280, Height = 800, Name = "wayland@0,0", Primary = true });
        vm.OnDisplaysChanged(opened);
        Dispatcher.UIThread.RunJobs();
        vm.HasHostNotice.ShouldBeFalse("an empty notice clears the last one");
        vm.HasNoDisplays.ShouldBeFalse();
    }

    /// <summary>Each way asking from the settings page can end has its own words; only the unforeseen one carries the portal's.</summary>
    [AvaloniaFact]
    public void Asking_from_the_settings_page_ends_in_words_for_each_outcome()
    {
        string[] said =
        [
            .. new[] { WaylandAskOutcome.Allowed, WaylandAskOutcome.WatchOnly, WaylandAskOutcome.Declined, WaylandAskOutcome.Unanswered }
                .Select(o => DeskPair.Desktop.ViewModels.Settings.SecuritySettingsViewModel.WaylandNotice(o, null)),
        ];

        said.Distinct().Count().ShouldBe(4);
        said.ShouldAllBe(s => s.Length > 0);
        DeskPair.Desktop.ViewModels.Settings.SecuritySettingsViewModel.WaylandNotice(WaylandAskOutcome.Failed, "no portal backend").ShouldContain("no portal backend");
    }

    /// <summary>The Linux platform's outcomes and the page's are the same list in the same order: one is cast to the other.</summary>
    [AvaloniaFact]
    public void The_pages_outcomes_line_up_with_the_platforms() =>
        Enum.GetNames<WaylandAskOutcome>().ShouldBe(Enum.GetNames<DeskPair.Platform.Linux.Wayland.PortalAskOutcome>());

    [AvaloniaFact]
    public void A_notice_is_not_mistaken_for_a_refused_request()
    {
        var vm = new RemoteSessionViewModel("123456789", "me", new DesktopConfig(), NullLoggerFactory.Instance);
        var info = new PeerInfo { Hostname = "Desk", Platform = "Linux", MultiDisplay = true };
        info.Displays.Add(new DisplayInfo { Width = 1280, Height = 800, Name = "wayland@0,0", Primary = true });
        vm.OnPeerInfo(info);
        Dispatcher.UIThread.RunJobs();

        // Sharing stopped at the machine: the list goes, and with it the display the viewer was on.
        vm.OnDisplaysChanged(new DisplaysChanged { CurrentDisplay = 0, Changed = -1, Notice = "The person at the remote computer stopped sharing its screen." });
        Dispatcher.UIThread.RunJobs();

        vm.HasNoDisplays.ShouldBeTrue("the list is taken as it is, unlike a failure, which leaves it alone");
        vm.HostNotice.ShouldContain("stopped sharing");
    }
}
