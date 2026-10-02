using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Core.Config;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;
using DeskPair.Desktop.ViewModels;
using DeskPair.Desktop.ViewModels.Settings;
using DeskPair.Protocol.Messages;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// Displays a viewer asks the host for: offered only where they can be made, marked once they exist, and allowed
/// or not by the host's owner from the display settings.
/// </summary>
[Collection("ProcessState")] // reads the string table, which the language tests move
public class AddedDisplayTests
{
    private static RemoteSessionViewModel Told(string platform, bool multi, int current = 0, params bool[] added)
    {
        var vm = new RemoteSessionViewModel("123456789", "me", new DesktopConfig(), NullLoggerFactory.Instance);
        var info = new PeerInfo { Hostname = "Office", Platform = platform, MultiDisplay = multi, CurrentDisplay = current };
        for (int i = 0; i < Math.Max(2, added.Length); i++)
        {
            info.Displays.Add(new DisplayInfo { Width = 1920, Height = 1080, X = 1920 * i, Name = $"DISPLAY{i + 1}", Primary = i == 0, VirtualDisplay = i < added.Length && added[i] });
        }

        vm.OnPeerInfo(info);
        Dispatcher.UIThread.RunJobs();
        return vm;
    }

    /// <summary>Only Windows can make a display; any other host would refuse, so it is not offered.</summary>
    [AvaloniaFact]
    public void Only_a_windows_host_that_streams_several_displays_is_offered_one_more()
    {
        Told("Windows", multi: true).CanAddDisplays.ShouldBeTrue();
        Told("Linux", multi: true).CanAddDisplays.ShouldBeFalse();
        Told("Windows", multi: false).CanAddDisplays.ShouldBeFalse("a host too old to stream several displays is too old to add one");
    }

    [AvaloniaFact]
    public void An_added_display_is_marked_and_its_tab_can_take_it_away()
    {
        RemoteSessionViewModel onAdded = Told("Windows", multi: true, current: 1, false, true);
        onAdded.Displays[1].ShouldEndWith(Strings.Get("session.addedDisplay"));
        onAdded.Displays[0].ShouldNotContain(Strings.Get("session.addedDisplay"));
        onAdded.CurrentDisplayIsAdded.ShouldBeTrue();

        Told("Windows", multi: true, current: 0, false, true).CurrentDisplayIsAdded.ShouldBeFalse("a real display cannot be removed from here");
    }

    /// <summary>
    /// A display added beside the host's private screen would turn the owner's screens back on, and the host refuses it:
    /// the button is off while the private screen is up and says why, in the viewer's language, before it is pressed.
    /// </summary>
    [AvaloniaFact]
    public void Beside_a_private_screen_no_display_is_offered_and_the_button_says_why()
    {
        var vm = new RemoteSessionViewModel("123456789", "me", new DesktopConfig(), NullLoggerFactory.Instance);
        var info = new PeerInfo { Hostname = "Office", Platform = "Windows", MultiDisplay = true };
        info.Displays.Add(new DisplayInfo { Width = 1500, Height = 850, Name = "DISPLAY39", Primary = true, SessionScreen = true });
        vm.OnPeerInfo(info);
        Dispatcher.UIThread.RunJobs();

        vm.PrivateScreenOn.ShouldBeTrue();
        vm.AddDisplayTip.ShouldBe(Strings.Get("session.addDisplay.private"));
        vm.Displays[0].ShouldEndWith(Strings.Get("session.privateScreen"));

        Told("Windows", multi: true).AddDisplayTip.ShouldBe(Strings.Get("session.addDisplay"));
    }

    [AvaloniaFact]
    public void The_owner_decides_in_the_display_settings()
    {
        var section = new DisplaySettingsViewModel();
        section.Load(new DesktopConfig(), new HostConfig { AllowVirtualDisplay = true });
        section.AllowVirtualDisplay.ShouldBeTrue();

        int changes = 0;
        section.Changed += () => changes++;
        section.AllowVirtualDisplay = false;
        section.Apply(new HostConfig { AllowVirtualDisplay = true }).AllowVirtualDisplay.ShouldBeFalse();
        changes.ShouldBe(1);

        // What the page says about the driver is read back, not saved.
        section.RefreshDriver();
        changes.ShouldBe(1);
    }
}
