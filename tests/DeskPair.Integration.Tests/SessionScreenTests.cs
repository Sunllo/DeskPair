using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Testing;
using DeskPair.Core.Transport;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Protocol.Messages;

namespace DeskPair.Integration.Tests;

/// <summary>
/// The private session screen: a viewer following its window gets a display of the window's size with the owner's
/// screens off -- only when the owner allowed it and the viewer may type -- and the owner's screens come back when
/// nobody follows any more, when the last viewer leaves, or when somebody at the computer turns them back on.
/// </summary>
public class SessionScreenTests
{
    private static SessionOptions Following(int width = 1500, int height = 900, bool withWindow = true)
    {
        var options = new SessionOptions { FollowWindow = BoolOption.BoYes };
        if (withWindow)
        {
            options.Viewport = new Resolution { Width = width, Height = height };
        }

        options.Screens.Add(new Resolution { Width = 1920, Height = 1080 });
        options.Screens.Add(new Resolution { Width = 1920, Height = 1040 });
        return options;
    }

    private static async Task<(ControllerSession Session, TestCallbacks Cb)> ConnectAsync(Testbed bed, HostRuntime host, string password, SessionOptions options)
    {
        (ControllerSession session, TestCallbacks cb, PeerConnector connector) = bed.CreateController(options: options);
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(password, CancellationToken.None)).Success.ShouldBeTrue();
        return (session, cb);
    }

    private static bool OnlyTheSessionScreen(TestCallbacks cb, int width, int height) =>
        cb.DisplaysChanges.Count > 0 && cb.DisplaysChanges[^1].Displays is [{ SessionScreen: true } only] && only.Width == width && only.Height == height;

    [Fact]
    public async Task A_viewer_following_its_window_gets_a_screen_of_its_own_until_it_stops()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, displays: 2, sessionScreen: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword, Following());

        await Testbed.WaitUntilAsync(() => OnlyTheSessionScreen(cb, 1500, 900), "the session screen, alone, the window's size", 15_000);
        bed.SessionScreen!.LastSizes.ShouldBe([new DisplayMode(1920, 1080), new DisplayMode(1920, 1040)], "the viewer's screens, for later");

        await session.SetOptionsAsync(new SessionOptions { FollowWindow = BoolOption.BoNo });

        await Testbed.WaitUntilAsync(() => !bed.SessionScreen.IsOpen && cb.DisplaysChanges[^1].Displays.Count == 2, "the owner's screens back", 15_000);
        cb.DisplaysChanges[^1].Displays.ShouldAllBe(d => !d.SessionScreen);
    }

    [Fact]
    public async Task The_last_viewer_leaving_puts_the_owners_screens_back()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, displays: 2, sessionScreen: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword, Following());
        await Testbed.WaitUntilAsync(() => OnlyTheSessionScreen(cb, 1500, 900), "the session screen", 15_000);

        await session.CloseAsync("done");

        await Testbed.WaitUntilAsync(() => !bed.SessionScreen!.IsOpen && bed.Displays!.Displays.Count == 2, "the owner's screens back", 15_000);
    }

    [Fact]
    public async Task It_is_off_unless_the_owner_allowed_it()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, displays: 2);
        await ConnectAsync(bed, host, passwords.TemporaryPassword, Following());

        await Task.Delay(1500);

        bed.SessionScreen!.Opened.ShouldBe(0);
        bed.Displays!.Displays.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_viewer_who_may_only_watch_does_not_turn_the_owners_screens_off()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(policy: new HostPolicy { KeyboardEnabled = false }, media: true, displays: 2, sessionScreen: true);
        await ConnectAsync(bed, host, passwords.TemporaryPassword, Following());

        await Task.Delay(1500);

        bed.SessionScreen!.Opened.ShouldBe(0);
    }

    [Fact]
    public async Task Without_the_size_of_its_window_it_waits_to_be_told()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, displays: 2, sessionScreen: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword, Following(withWindow: false));
        await Task.Delay(1000);
        bed.SessionScreen!.Opened.ShouldBe(0);

        await session.SetOptionsAsync(Following(1280, 800));

        await Testbed.WaitUntilAsync(() => OnlyTheSessionScreen(cb, 1280, 800), "the session screen once the window's size is known", 15_000);
    }

    /// <summary>A display added beside the private screen would be a set Windows lights the owner's screens up for: refused, with the way out.</summary>
    [Fact]
    public async Task No_display_is_added_while_the_private_screen_is_up()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, displays: 2, sessionScreen: true, virtualDisplays: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword, Following());
        await Testbed.WaitUntilAsync(() => OnlyTheSessionScreen(cb, 1500, 900), "the session screen", 15_000);

        await session.AddVirtualDisplayAsync(new Resolution { Width = 1280, Height = 720 });

        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges[^1].Failure.Length > 0, "the refusal", 15_000);
        cb.DisplaysChanges[^1].Failure.ShouldContain("Match window");
        bed.VirtualDisplays!.Count.ShouldBe(0);
        bed.SessionScreen!.IsOpen.ShouldBeTrue();
    }

    /// <summary>Win+P at the computer is the person there taking their screens back: it ends, and does not come back while the viewer follows.</summary>
    [Fact]
    public async Task Somebody_at_the_computer_turning_the_screens_back_on_ends_it()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, displays: 2, sessionScreen: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword, Following());
        await Testbed.WaitUntilAsync(() => OnlyTheSessionScreen(cb, 1500, 900), "the session screen", 15_000);
        await Task.Delay(2500); // past the moment in which a display coming back is the session screen's own doing

        bed.SessionScreen!.TakeBack();

        await Testbed.WaitUntilAsync(() => !bed.SessionScreen.IsOpen && bed.Displays!.Displays.Count == 2, "the owner's screens back", 15_000);
        await session.SetOptionsAsync(Following(1400, 800));
        await Task.Delay(1500);
        bed.SessionScreen.Opened.ShouldBe(1, "not again while the viewer keeps following");
    }
}
