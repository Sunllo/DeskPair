using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Transport;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Protocol.Messages;

namespace DeskPair.Integration.Tests;

/// <summary>
/// Several of the host's displays streamed to one viewer at once: the set is asked for whole, refused whole,
/// survives the host's SwitchDisplay replies, follows a monitor being pulled, and leaves viewers that never
/// asked for a set exactly as they were.
/// </summary>
public class MultiDisplayTests
{
    private static async Task<(ControllerSession Session, TestCallbacks Cb)> ConnectAsync(Testbed bed, HostRuntime host, string password, bool udpMedia = false)
    {
        (ControllerSession session, TestCallbacks cb, PeerConnector connector) = bed.CreateController(udpMedia: udpMedia);
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(password, CancellationToken.None)).Success.ShouldBeTrue();
        return (session, cb);
    }

    private static int Frames(TestCallbacks cb, int display) => cb.FramesByDisplay.GetValueOrDefault(display);

    [Fact]
    public async Task A_viewer_watches_two_displays_and_one_that_never_asked_is_left_alone()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, displays: 2);
        (ControllerSession a, TestCallbacks ca) = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        (ControllerSession b, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        a.HostSupportsMultiDisplay.ShouldBeTrue();

        await a.SubscribeDisplaysAsync([0, 1], focus: 0);

        await Testbed.WaitUntilAsync(() => ca.DisplaySubscriptions.Count >= 1, "the set confirmed", 15_000);
        DisplaySubscription confirmed = ca.DisplaySubscriptions[^1];
        confirmed.Failure.ShouldBeEmpty();
        confirmed.Displays.ShouldBe([0, 1]);
        confirmed.Focus.ShouldBe(0);
        int zero = Frames(ca, 0), one = Frames(ca, 1);
        await Testbed.WaitUntilAsync(() => Frames(ca, 0) >= zero + 10 && Frames(ca, 1) >= one + 10, "frames of both displays", 15_000);
        int first = host.Sessions.Min(s => s.Context.ConnectionId);
        bed.Media!.SubscribedDisplays(first).ShouldBe([0, 1]);

        // The other viewer switches the old way and hears only the old answer.
        await b.SwitchDisplayAsync(1);
        await Testbed.WaitUntilAsync(() => cb.DisplaySwitches.Contains(1), "switch confirmed", 15_000);
        await Testbed.WaitUntilAsync(() => Frames(cb, 1) >= 3, "frames on display 1", 15_000);
        cb.DisplaySubscriptions.ShouldBeEmpty("a viewer that never asked for a set is never sent one");

        await a.CloseAsync("done");
        await b.CloseAsync("done");
    }

    /// <summary>
    /// Both displays over UDP, with the focus on the second. The host confirms the focus with SwitchDisplay as it
    /// always has; a viewer that took that to mean "display 1 only" would drop display 0's datagrams.
    /// </summary>
    [Fact]
    public async Task Two_displays_come_over_udp_and_the_focus_does_not_narrow_them()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, displays: 2);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword, udpMedia: true);
        await Testbed.WaitUntilAsync(() => session.VideoFramesReceivedUdp >= 10, "UDP video", 15_000);

        await session.SubscribeDisplaysAsync([0, 1], focus: 1);
        await Testbed.WaitUntilAsync(() => cb.DisplaySwitches.Contains(1), "the focus confirmed as a switch", 15_000);
        int zero = Frames(cb, 0), one = Frames(cb, 1);
        long udp = session.VideoFramesReceivedUdp;
        await Testbed.WaitUntilAsync(() => Frames(cb, 0) >= zero + 30 && Frames(cb, 1) >= one + 30, "both displays keep coming", 15_000);
        (session.VideoFramesReceivedUdp - udp).ShouldBeGreaterThanOrEqualTo(60, "over UDP, not a TCP fallback");

        int connection = host.Sessions.Single().Context.ConnectionId;
        bed.Media!.Qos.IsCongested(connection, 0).ShouldBeFalse();
        bed.Media.Qos.IsCongested(connection, 1).ShouldBeFalse();
        await session.CloseAsync("done");
    }

    [Fact]
    public async Task A_set_is_refused_whole_and_the_old_one_kept()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, displays: 3);
        (ControllerSession a, TestCallbacks ca) = await ConnectAsync(bed, host, passwords.TemporaryPassword);

        await a.SubscribeDisplaysAsync([0, 1, 2], focus: 0);
        await Testbed.WaitUntilAsync(() => ca.DisplaySubscriptions.Count >= 1, "an answer", 15_000);
        ca.DisplaySubscriptions[^1].Failure.ShouldContain("at most 2");
        ca.DisplaySubscriptions[^1].Displays.ShouldBe([0], "the set it had");

        await a.SubscribeDisplaysAsync([0, 7], focus: 7);
        await Testbed.WaitUntilAsync(() => ca.DisplaySubscriptions.Count >= 2, "a second answer", 15_000);
        ca.DisplaySubscriptions[^1].Failure.ShouldContain("no display 8");

        // Across viewers: two streams at most, and one viewer already has both of them.
        bed.Media!.MaxConcurrentStreams = 2;
        await a.SubscribeDisplaysAsync([0, 1], focus: 0);
        await Testbed.WaitUntilAsync(() => ca.DisplaySubscriptions.Count >= 3 && ca.DisplaySubscriptions[^1].Failure.Length == 0, "two displays for the first viewer", 15_000);
        (ControllerSession b, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        await b.SubscribeDisplaysAsync([2], focus: 2);
        await Testbed.WaitUntilAsync(() => cb.DisplaySubscriptions.Count >= 1, "the second viewer's answer", 15_000);
        cb.DisplaySubscriptions[^1].Failure.ShouldContain("as many displays as it will");
        bed.Media.GetVideoService(2).ShouldBeNull("no third encoder was started");

        await a.CloseAsync("done");
        await b.CloseAsync("done");
    }

    /// <summary>
    /// The middle one of three monitors is pulled while a viewer watches the second and third. Every index after
    /// it moves down, so the set is carried over by name: the third display is now display 1, and the viewer is
    /// told so before the new list arrives. Held by index, it would have gone on showing whatever landed there.
    /// </summary>
    [Fact]
    public async Task A_pulled_monitor_carries_the_set_over_by_name()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, displays: 3);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        await session.SubscribeDisplaysAsync([1, 2], focus: 2);
        await Testbed.WaitUntilAsync(() => Frames(cb, 1) >= 3 && Frames(cb, 2) >= 3, "frames of displays 1 and 2", 15_000);
        int answers = cb.DisplaySubscriptions.Count;

        // What a real enumerator reports: the list without FAKE1, indices renumbered from zero.
        List<DisplayDescriptor> list = bed.Displays!.Displays;
        list[1] = list[2] with { Index = 1 };
        list.RemoveAt(2);
        bed.Displays.Raise();

        await Testbed.WaitUntilAsync(() => cb.DisplaySubscriptions.Count > answers, "the carried set", 15_000);
        DisplaySubscription carried = cb.DisplaySubscriptions[^1];
        carried.Displays.ShouldBe([1], "FAKE2 is display 1 now; FAKE1 is gone");
        carried.Names.ShouldBe(["FAKE2"], "named, so the viewer can match windows before the new list arrives");
        carried.Focus.ShouldBe(1);
        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges.Count >= 1, "the new list", 15_000);
        cb.DisplaysChanges[^1].Displays.Select(d => d.Name).ShouldBe(["FAKE0", "FAKE2"]);
        int now = Frames(cb, 1);
        await Testbed.WaitUntilAsync(() => Frames(cb, 1) >= now + 3, "display 1 keeps coming", 15_000);
        bed.Media!.GetVideoService(2).ShouldBeNull();
        await session.CloseAsync("done");
    }

    /// <summary>
    /// The screen goes back to its owner's resolution when the last session leaves -- counted in sessions, not in
    /// subscriptions. A viewer watching no display is still connected, and restoring under it would change the
    /// screen in the middle of its session.
    /// </summary>
    [Fact]
    public async Task A_viewer_watching_nothing_still_holds_the_resolution()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true);
        (ControllerSession a, TestCallbacks ca) = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        await Testbed.WaitUntilAsync(() => ca.VideoFrames >= 1, "a frame", 15_000);
        await a.SetResolutionAsync(0, new Resolution { Width = 320, Height = 180 });
        await Testbed.WaitUntilAsync(() => bed.Displays!.Displays[0].Width == 320, "resolution changed", 15_000);

        await a.SubscribeDisplaysAsync([], focus: -1);
        await Testbed.WaitUntilAsync(() => ca.DisplaySubscriptions.Count >= 1, "subscribed to nothing", 15_000);
        ca.DisplaySubscriptions[^1].Displays.ShouldBeEmpty();

        (ControllerSession b, _) = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        await b.CloseAsync("done");
        await Testbed.WaitUntilAsync(() => host.Sessions.Count == 1, "one session left");
        await Task.Delay(300);
        bed.Displays!.Displays[0].Width.ShouldBe(320, "the first viewer is still here");

        await a.CloseAsync("done");
        await Testbed.WaitUntilAsync(() => bed.Displays.Displays[0].Width == 640, "restored once the last one left", 15_000);
    }
}
