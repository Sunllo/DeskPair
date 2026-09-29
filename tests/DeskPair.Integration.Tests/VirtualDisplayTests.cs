using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Transport;
using DeskPair.Protocol.Messages;

namespace DeskPair.Integration.Tests;

/// <summary>
/// Displays that do not exist, plugged in at a viewer's request: allowed only by the owner and to a viewer who may
/// type, answered like a resolution change, gone again when the last viewer leaves, and taught any size.
/// </summary>
public class VirtualDisplayTests
{
    private static async Task<(ControllerSession Session, TestCallbacks Cb)> ConnectAsync(Testbed bed, HostRuntime host, string password)
    {
        (ControllerSession session, TestCallbacks cb, PeerConnector connector) = bed.CreateController();
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(password, CancellationToken.None)).Success.ShouldBeTrue();
        return (session, cb);
    }

    [Fact]
    public async Task A_viewer_adds_a_display_everyone_hears_and_the_last_one_out_takes_it_away()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, virtualDisplays: true);
        (ControllerSession a, TestCallbacks ca) = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        (ControllerSession b, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);

        await a.AddVirtualDisplayAsync(new Resolution { Width = 1280, Height = 720 }, [new Resolution { Width = 2560, Height = 1440 }, new Resolution { Width = 2560, Height = 1392 }]);

        await Testbed.WaitUntilAsync(() => ca.DisplaysChanges.Count >= 1 && cb.DisplaysChanges.Count >= 1, "both viewers told", 15_000);
        bed.VirtualDisplays!.LastSizes.ShouldBe([new DisplayMode(2560, 1440), new DisplayMode(2560, 1392)], "the other sizes the viewer named reach the platform");
        DisplaysChanged told = cb.DisplaysChanges[^1];
        told.Failure.ShouldBeEmpty();
        told.Changed.ShouldBe(1, "the display that was added");
        told.Displays.Count.ShouldBe(2);
        told.Displays[1].VirtualDisplay.ShouldBeTrue();
        told.Displays[0].VirtualDisplay.ShouldBeFalse();
        (told.Displays[1].Width, told.Displays[1].Height).ShouldBe((1280, 720));

        // It streams like any other display.
        await a.SubscribeDisplaysAsync([0, 1], focus: 1);
        await Testbed.WaitUntilAsync(() => ca.FramesByDisplay.GetValueOrDefault(1) >= 3, "frames of the added display", 15_000);

        await a.CloseAsync("done");
        await Testbed.WaitUntilAsync(() => host.Sessions.Count == 1, "one session left");
        await Task.Delay(200);
        bed.VirtualDisplays!.Count.ShouldBe(1, "somebody is still connected");

        await b.CloseAsync("done");
        await Testbed.WaitUntilAsync(() => bed.VirtualDisplays.Count == 0, "unplugged when the last viewer left", 15_000);
        bed.Displays!.Displays.Count.ShouldBe(1);
    }

    [Fact]
    public async Task It_is_off_unless_the_owner_allowed_it()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);

        await session.AddVirtualDisplayAsync();

        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges.Count >= 1, "an answer", 15_000);
        cb.DisplaysChanges[^1].Failure.ShouldContain("not allowed");
        bed.VirtualDisplays!.Count.ShouldBe(0);
    }

    [Fact]
    public async Task A_viewer_who_may_only_watch_cannot_add_a_display()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(policy: new HostPolicy { KeyboardEnabled = false }, media: true, virtualDisplays: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);

        await session.AddVirtualDisplayAsync();

        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges.Count >= 1, "an answer", 15_000);
        cb.DisplaysChanges[^1].Failure.ShouldContain("keyboard and mouse permission");
        bed.VirtualDisplays!.Count.ShouldBe(0);
    }

    /// <summary>Only what was plugged in from here can be unplugged; a viewer on it is carried to what remains.</summary>
    [Fact]
    public async Task Removing_takes_only_an_added_display_and_moves_its_viewer()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, virtualDisplays: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);

        await session.RemoveVirtualDisplayAsync(0);
        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges.Count >= 1, "a refusal", 15_000);
        cb.DisplaysChanges[^1].Failure.ShouldContain("added from here");

        await session.AddVirtualDisplayAsync();
        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges.Count >= 2, "the display added", 15_000);
        await session.SubscribeDisplaysAsync([0, 1], focus: 1);
        await Testbed.WaitUntilAsync(() => cb.FramesByDisplay.GetValueOrDefault(1) >= 3, "watching it", 15_000);
        int answers = cb.DisplaySubscriptions.Count;

        await session.RemoveVirtualDisplayAsync(1);

        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges.Count >= 3, "the display removed", 15_000);
        cb.DisplaysChanges[^1].Failure.ShouldBeEmpty();
        cb.DisplaysChanges[^1].Displays.Count.ShouldBe(1);
        await Testbed.WaitUntilAsync(() => cb.DisplaySubscriptions.Count > answers, "the set carried over", 15_000);
        cb.DisplaySubscriptions[^1].Displays.ShouldBe([0]);
        bed.VirtualDisplays!.Count.ShouldBe(0);
    }

    /// <summary>A host with no screen gets one for the session, or the session would have nothing to show.</summary>
    [Fact]
    public async Task A_host_with_no_screen_gets_one_for_the_session()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, displays: 0, virtualDisplays: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);

        await Testbed.WaitUntilAsync(() => cb.FramesByDisplay.GetValueOrDefault(0) >= 3, "frames of the display it was given", 15_000);
        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges.Count >= 1, "told about it", 15_000);
        cb.DisplaysChanges[^1].Displays.ShouldHaveSingleItem().VirtualDisplay.ShouldBeTrue();

        await session.CloseAsync("done");
        await Testbed.WaitUntilAsync(() => bed.Displays!.Displays.Count == 0, "taken away with the session", 15_000);
    }

    /// <summary>
    /// Windows brings a monitor back at whatever size it last had, not at the size asked for: the host sets the size
    /// itself once the display has appeared.
    /// </summary>
    [Fact]
    public async Task A_display_that_comes_back_at_another_size_is_set_to_the_one_asked_for()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, virtualDisplays: true);
        bed.VirtualDisplays!.IgnoresRequestedSize = true;
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);

        await session.AddVirtualDisplayAsync(new Resolution { Width = 1280, Height = 720 });

        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges.Count >= 1, "told about it", 15_000);
        DisplaysChanged told = cb.DisplaysChanges[^1];
        told.Failure.ShouldBeEmpty();
        (told.Displays[1].Width, told.Displays[1].Height).ShouldBe((1280, 720), "set after it came up at 640x360");
    }

    /// <summary>
    /// A size the added display never advertised is taught to it and set the usual way, and forgotten once the last
    /// viewer has gone. A real display is not taught anything.
    /// </summary>
    [Fact]
    public async Task An_added_display_takes_any_size_and_a_real_one_does_not()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, virtualDisplays: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        await session.AddVirtualDisplayAsync();
        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges.Count >= 1, "the display added", 15_000);

        await session.SetResolutionAsync(1, new Resolution { Width = 1000, Height = 600 });
        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges.Count >= 2, "the size answered", 15_000);
        DisplaysChanged sized = cb.DisplaysChanges[^1];
        sized.Failure.ShouldBeEmpty();
        (sized.Displays[1].Width, sized.Displays[1].Height).ShouldBe((1000, 600));
        bed.DisplayModes!.TaughtCount.ShouldBe(1);

        await session.SetResolutionAsync(0, new Resolution { Width = 1000, Height = 600 });
        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges.Count >= 3, "the real display's answer", 15_000);
        cb.DisplaysChanges[^1].Failure.ShouldContain("not a size this display can show");

        await session.CloseAsync("done");
        await Testbed.WaitUntilAsync(() => bed.DisplayModes.TaughtCount == 0 && bed.VirtualDisplays!.Count == 0, "forgotten and unplugged", 15_000);
    }
}
