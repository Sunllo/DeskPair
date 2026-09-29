using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Testing;
using DeskPair.Core.Transport;
using DeskPair.Protocol.Messages;

namespace DeskPair.Integration.Tests;

/// <summary>
/// A viewer whose remote display follows the size of its window: it asks for a new size at every pause of a resize,
/// and the host has to keep up without holding the viewer's input or everyone else's picture hostage.
/// </summary>
public class FollowWindowTests
{
    private static async Task<(ControllerSession Session, TestCallbacks Cb)> ConnectAsync(Testbed bed, HostRuntime host, string password)
    {
        (ControllerSession session, TestCallbacks callbacks, PeerConnector connector) = bed.CreateController();
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(password, CancellationToken.None)).Success.ShouldBeTrue();
        return (session, callbacks);
    }

    private static Resolution Size(int w, int h) => new() { Width = w, Height = h };

    /// <summary>The host can make up any size for its displays, as a Linux desktop's outputs can be taught one.</summary>
    private static async Task<(HostRuntime Host, string Password)> StartHostAsync(Testbed bed, int displays = 1)
    {
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, displays: displays);
        bed.VirtualDisplays!.TeachAll = true;
        return (host, passwords.TemporaryPassword);
    }

    [Fact]
    public async Task A_display_of_any_size_takes_exactly_the_size_asked_for()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, string password) = await StartHostAsync(bed);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, password);
        await Testbed.WaitUntilAsync(() => cb.VideoFrames >= 3, "frames at the original size", 15_000);
        SizeRange range = cb.PeerInfo!.Displays[0].AnySize.ShouldNotBeNull("the host says it can make up a size");
        range.Step.ShouldBe(2);
        range.MaxWidth.ShouldBeLessThanOrEqualTo(4096);

        await session.SetResolutionAsync(0, Size(1234, 776));

        await Testbed.WaitUntilAsync(() => cb.LastFrameSize == (1234, 776), "frames at exactly the window's size", 15_000);
        cb.DisplaysChanges[^1].Displays[0].Width.ShouldBe(1234);

        await session.CloseAsync("done");
        await Testbed.WaitUntilAsync(() => bed.Displays!.Displays[0].Width == 640 && bed.DisplayModes!.TaughtCount == 0, "restored and forgotten", 15_000);
    }

    /// <summary>
    /// The whole round: a follower told its window's size asks, the host makes that size, and the answer tells the
    /// follower it is matched. A resize asks again, and turning it off gives the display back its own size.
    /// </summary>
    [Fact]
    public async Task A_follower_keeps_the_display_the_size_of_its_window()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, string password) = await StartHostAsync(bed);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, password);
        await Testbed.WaitUntilAsync(() => cb.PeerInfo is not null && cb.VideoFrames >= 1, "connected", 15_000);
        using var follower = new ResolutionFollower(TimeProvider.System, (display, mode) => session.SetResolutionAsync(display, mode).AsTask());
        cb.DisplaysChangedHook = m => follower.Heard(0, m.Displays.Count > 0 ? m.Displays[0] : null, m.Changed, m.Failure);

        follower.Start(0, cb.PeerInfo!.Displays[0]);
        follower.Window(1100, 700, 1);
        await Testbed.WaitUntilAsync(() => cb.LastFrameSize == (1100, 700) && follower.State == FollowState.Matched, "the display the size of the window", 15_000);

        follower.Window(900, 640, 1);
        await Testbed.WaitUntilAsync(() => cb.LastFrameSize == (900, 640) && follower.State == FollowState.Matched, "and again after a resize", 15_000);

        await follower.StopAsync();
        await Testbed.WaitUntilAsync(() => bed.Displays!.Displays[0].Width == 640, "given back when following stops", 15_000);
        bed.DisplayModes!.TaughtCount.ShouldBe(0, "each size taught was taken back as the display left it");
    }

    /// <summary>A resize asks faster than a monitor can switch: the requests between two changes collapse into the last.</summary>
    [Fact]
    public async Task Requests_faster_than_changes_are_merged_and_the_last_size_wins()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, string password) = await StartHostAsync(bed);
        bed.DisplayModes!.Delay = TimeSpan.FromMilliseconds(300);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, password);
        await Testbed.WaitUntilAsync(() => cb.VideoFrames >= 3, "frames", 15_000);

        for (int i = 0; i < 10; i++)
        {
            await session.SetResolutionAsync(0, Size(700 + (i * 60), 400 + (i * 40)));
        }

        await Testbed.WaitUntilAsync(() => bed.Displays!.Displays[0].Width == 1240 && cb.LastFrameSize == (1240, 760), "the last size in force", 15_000);
        bed.DisplayModes.Calls.Count.ShouldBeLessThanOrEqualTo(3, "ten requests, a first change and the last size");
    }

    /// <summary>A change takes as long as the monitor does; the viewer's mouse does not wait for it.</summary>
    [Fact]
    public async Task Input_keeps_flowing_while_a_change_is_made()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, string password) = await StartHostAsync(bed);
        bed.DisplayModes!.Delay = TimeSpan.FromMilliseconds(1500);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, password);
        await Testbed.WaitUntilAsync(() => cb.VideoFrames >= 3, "frames", 15_000);
        FakeInputInjector injector = bed.Injector!;
        int before = injector.MouseCount;

        await session.SetResolutionAsync(0, Size(320, 180));
        await session.SendMouseAsync(new MouseEvent { Mask = 0, X = 10, Y = 10, FrameWidth = 640, FrameHeight = 360 });

        await Testbed.WaitUntilAsync(() => injector.MouseCount > before, "the move injected", 1_000);
        cb.DisplaysChanges.ShouldBeEmpty("the move arrived while the change was still being made");
        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges.Count >= 1, "the change answered", 15_000);
    }

    /// <summary>One viewer's window following its size costs a viewer of another display nothing, not even a keyframe.</summary>
    [Fact]
    public async Task Only_the_stream_of_the_display_that_changed_restarts()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, string password) = await StartHostAsync(bed, displays: 2);
        (ControllerSession a, TestCallbacks ca) = await ConnectAsync(bed, host, password);
        (ControllerSession b, TestCallbacks cb) = await ConnectAsync(bed, host, password);
        await b.SwitchDisplayAsync(1);
        await Testbed.WaitUntilAsync(() => ca.FramesByDisplay.GetValueOrDefault(0) >= 3 && cb.FramesByDisplay.GetValueOrDefault(1) >= 3, "both watching", 15_000);
        int otherStarts = bed.Capturers!.CreatedFor("FAKE1");
        int ownStarts = bed.Capturers.CreatedFor("FAKE0");

        await a.SetResolutionAsync(0, Size(1000, 600));

        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges.Count >= 1 && ca.LastFrameSize == (1000, 600), "changed, and everyone told", 15_000);
        bed.Capturers.CreatedFor("FAKE0").ShouldBeGreaterThan(ownStarts);
        bed.Capturers.CreatedFor("FAKE1").ShouldBe(otherStarts, "the other display's stream kept running");
    }

    /// <summary>Asking for what the display already is changes nothing, restarts nothing, and bothers nobody else.</summary>
    [Fact]
    public async Task A_request_for_the_size_in_force_is_answered_to_the_asker_alone()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, string password) = await StartHostAsync(bed);
        (ControllerSession a, TestCallbacks ca) = await ConnectAsync(bed, host, password);
        (_, TestCallbacks cb) = await ConnectAsync(bed, host, password);
        await Testbed.WaitUntilAsync(() => ca.VideoFrames >= 3 && cb.VideoFrames >= 3, "both watching", 15_000);
        int starts = bed.Capturers!.CreatedFor("FAKE0");

        await a.SetResolutionAsync(0, Size(640, 360));

        await Testbed.WaitUntilAsync(() => ca.DisplaysChanges.Count >= 1, "answered", 15_000);
        ca.DisplaysChanges[0].Failure.ShouldBeEmpty();
        await Task.Delay(300);
        cb.DisplaysChanges.ShouldBeEmpty("nothing changed for anybody else to hear about");
        bed.Capturers.CreatedFor("FAKE0").ShouldBe(starts);
        bed.DisplayModes!.Calls.ShouldBeEmpty();
    }

    /// <summary>The last viewer leaves while its change is being made: the change lands first, then the screen goes back.</summary>
    [Fact]
    public async Task The_last_viewer_leaving_during_a_change_leaves_the_screen_as_it_was()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, string password) = await StartHostAsync(bed);
        bed.DisplayModes!.Delay = TimeSpan.FromMilliseconds(1000);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, password);
        await Testbed.WaitUntilAsync(() => cb.VideoFrames >= 3, "frames", 15_000);

        await session.SetResolutionAsync(0, Size(1000, 600));
        await Testbed.WaitUntilAsync(() => bed.DisplayModes.Calls.Count >= 1, "the change under way", 15_000);
        await session.CloseAsync("done");

        await Testbed.WaitUntilAsync(() => host.Sessions.Count == 0, "the session closed", 15_000);
        await Testbed.WaitUntilAsync(() => bed.Displays!.Displays[0].Width == 640, "the original put back", 15_000);
        await Task.Delay(1500);
        bed.Displays!.Displays[0].Width.ShouldBe(640, "nothing landed after the restore");
        bed.Media!.DisplayModes.HasChanges.ShouldBeFalse();
        bed.DisplayModes.TaughtCount.ShouldBe(0);
    }
}
