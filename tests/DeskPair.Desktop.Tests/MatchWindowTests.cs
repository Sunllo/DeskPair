using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using DeskPair.Core.Session.Controller;
using DeskPair.Desktop.Localization;
using DeskPair.Desktop.Services;
using DeskPair.Desktop.ViewModels;
using DeskPair.Desktop.ViewModels.Settings;
using DeskPair.Protocol.Messages;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// The remote display following the window: a default in the display settings, a switch on the toolbar, offered for a
/// display that can be changed, and a word on the toolbar when it cannot do what it was asked.
/// </summary>
[Collection("ProcessState")] // reads the string table, which the language tests move
public sealed class MatchWindowTests
{
    private static RemoteSessionViewModel Told(DesktopConfig config, DisplayInfo display)
    {
        var vm = new RemoteSessionViewModel("123456789", "me", config, NullLoggerFactory.Instance);
        var info = new PeerInfo { Hostname = "Office", Platform = "Linux", MultiDisplay = true };
        info.Displays.Add(display);
        vm.OnPeerInfo(info);
        Dispatcher.UIThread.RunJobs();
        return vm;
    }

    private static DisplayInfo Display(bool anySize = false, bool modes = false)
    {
        var display = new DisplayInfo { Width = 1920, Height = 1080, Name = "HDMI-1", Primary = true };
        if (anySize)
        {
            display.AnySize = new SizeRange { MinWidth = 640, MinHeight = 480, MaxWidth = 4096, MaxHeight = 4096, Step = 2 };
        }

        if (modes)
        {
            display.Modes.Add(new Resolution { Width = 1920, Height = 1080 });
            display.Modes.Add(new Resolution { Width = 1280, Height = 720 });
        }

        return display;
    }

    [AvaloniaFact]
    public void Sessions_start_as_the_display_settings_say()
    {
        Told(new DesktopConfig(), Display(anySize: true)).MatchWindow.ShouldBeFalse();
        Told(new DesktopConfig { MatchWindowResolution = true }, Display(anySize: true)).MatchWindow.ShouldBeTrue();
    }

    [AvaloniaFact]
    public void Only_a_display_that_can_change_can_follow_the_window()
    {
        Told(new DesktopConfig(), Display(anySize: true)).CanMatchWindow.ShouldBeTrue("any size");
        Told(new DesktopConfig(), Display(modes: true)).CanMatchWindow.ShouldBeTrue("the nearest of its modes");
        Told(new DesktopConfig(), Display()).CanMatchWindow.ShouldBeFalse("a Wayland screen, the scanout daemon, an RDP host");
    }

    [AvaloniaFact]
    public void Without_the_keyboard_the_display_cannot_follow()
    {
        RemoteSessionViewModel vm = Told(new DesktopConfig(), Display(anySize: true));

        vm.OnPermission(new PermissionInfo { Permission = Permission.PermKeyboard, Enabled = false });
        Dispatcher.UIThread.RunJobs();

        vm.CanMatchWindow.ShouldBeFalse();
    }

    /// <summary>Nothing is followed before the session is signed in; the wish is kept for when it is.</summary>
    [AvaloniaFact]
    public void Nothing_follows_before_the_session_is_in()
    {
        RemoteSessionViewModel vm = Told(new DesktopConfig { MatchWindowResolution = true }, Display(anySize: true));

        vm.IsFollowing.ShouldBeFalse();
        vm.MatchWindowStatus.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public void The_toolbar_says_what_following_could_not_do()
    {
        var time = new FakeTimeProvider();
        using var follower = new ResolutionFollower(time, (_, _) => Task.CompletedTask);
        RemoteSessionViewModel.FollowStatus(false, true, follower).ShouldBeEmpty("not wanted: nothing to say");
        RemoteSessionViewModel.FollowStatus(true, false, follower).ShouldBe(Strings.Get("session.matchWindow.unsupported"));

        follower.Start(0, Display(anySize: true));
        follower.Window(1000, 700, 1);
        time.Advance(ResolutionFollower.Settle);
        RemoteSessionViewModel.FollowStatus(true, true, follower).ShouldBe(Strings.Get("session.matchWindow.waiting"));

        follower.Heard(0, Display(anySize: true), changed: 0, failure: "1000x700 is not a size this display can be given.");
        RemoteSessionViewModel.FollowStatus(true, true, follower).ShouldBe(
            Strings.Format("session.matchWindow.failed", "1000x700 is not a size this display can be given."));
    }

    [AvaloniaFact]
    public void The_default_is_kept_in_the_display_settings()
    {
        var section = new DisplaySettingsViewModel();
        section.Load(new DesktopConfig { MatchWindowResolution = true }, null);
        section.MatchWindowResolution.ShouldBeTrue();

        int changes = 0;
        section.Changed += () => changes++;
        section.MatchWindowResolution = false;

        section.Apply(new DesktopConfig { MatchWindowResolution = true }).MatchWindowResolution.ShouldBeFalse();
        changes.ShouldBe(1);
    }
}
