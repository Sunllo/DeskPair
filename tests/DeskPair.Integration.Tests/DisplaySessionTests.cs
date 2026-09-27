using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Transport;
using DeskPair.Protocol.Messages;

namespace DeskPair.Integration.Tests;

/// <summary>
/// Displays that exist only while somebody watches -- a Wayland desktop shared through its portal -- driven end to end
/// through a real host and controller: opened for the first viewer, closed after the last, and every viewer told in
/// words what is happening while there is nothing to show.
/// </summary>
public class DisplaySessionTests
{
    private static async Task<(ControllerSession Session, TestCallbacks Cb)> ConnectAsync(Testbed bed, HostRuntime host, string password)
    {
        (ControllerSession session, TestCallbacks cb, PeerConnector connector) = bed.CreateController();
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(password, CancellationToken.None)).Success.ShouldBeTrue();
        return (session, cb);
    }

    [Fact]
    public async Task The_displays_open_for_the_first_viewer_and_close_after_the_last()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, displaySession: true);
        bed.Displays!.Displays.ShouldBeEmpty("nothing is shared while nobody watches");

        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);

        await Testbed.WaitUntilAsync(() => cb.FramesByDisplay.GetValueOrDefault(0) >= 3, "frames once the displays opened", 15_000);
        cb.DisplaysChanges[^1].Displays.Count.ShouldBe(1);
        cb.DisplaysChanges[^1].Notice.ShouldBeEmpty();
        bed.DisplaySession!.Opens.ShouldBe(1);

        await session.CloseAsync("done");
        await Testbed.WaitUntilAsync(() => bed.DisplaySession.Closes >= 1 && !bed.DisplaySession.IsOpen, "closed after the last viewer", 15_000);
        bed.Displays.Displays.ShouldBeEmpty();
    }

    [Fact]
    public async Task Viewers_hear_that_the_machine_is_asking_and_then_that_it_said_no()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, displaySession: true);
        bed.DisplaySession!.AskFirst = "Waiting for someone there to allow it.";

        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);

        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges.Any(d => d.Notice == "Waiting for someone there to allow it."), "told it is being asked", 15_000);
        bed.DisplaySession.Refusal = "The person at the remote computer declined to share its screen.";
        bed.DisplaySession.Consent.SetResult();

        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges[^1].Notice.StartsWith("The person at the remote computer declined", StringComparison.Ordinal), "told it was refused", 15_000);
        DisplaysChanged last = cb.DisplaysChanges[^1];
        last.Displays.ShouldBeEmpty();
        last.Failure.ShouldBeEmpty("a notice is the host speaking, not an answer to a request of this viewer's");
        cb.FramesByDisplay.GetValueOrDefault(0).ShouldBe(0);
        await session.CloseAsync("done");
    }

    [Fact]
    public async Task Stopping_the_sharing_at_the_machine_tells_every_viewer_why_the_picture_went()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, displaySession: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        await Testbed.WaitUntilAsync(() => cb.FramesByDisplay.GetValueOrDefault(0) >= 3, "watching", 15_000);

        bed.DisplaySession!.StopFromTheMachine("The person at the remote computer stopped sharing its screen.");

        await Testbed.WaitUntilAsync(
            () => cb.DisplaysChanges[^1] is { Displays.Count: 0 } d && d.Notice.Contains("stopped sharing", StringComparison.Ordinal),
            "told the sharing stopped",
            15_000);
        await session.CloseAsync("done");
    }

    [Fact]
    public async Task A_share_a_lock_ended_comes_back_when_the_screen_does()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, displaySession: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        await Testbed.WaitUntilAsync(() => cb.FramesByDisplay.GetValueOrDefault(0) >= 3, "watching", 15_000);

        bed.DisplaySession!.StopFromTheMachine("The remote computer's screen is locked.");
        await Testbed.WaitUntilAsync(
            () => cb.DisplaysChanges[^1] is { Displays.Count: 0 } d && d.Notice.Contains("locked", StringComparison.Ordinal),
            "told the screen is locked",
            15_000);
        int before = cb.FramesByDisplay.GetValueOrDefault(0);

        bed.DisplaySession.ScreenBack();

        await Testbed.WaitUntilAsync(
            () => bed.DisplaySession.Opens >= 2 && cb.DisplaysChanges[^1] is { Displays.Count: 1 } d && d.Notice.Length == 0,
            "the display again, with nothing left to explain",
            15_000);
        await Testbed.WaitUntilAsync(() => cb.FramesByDisplay.GetValueOrDefault(0) >= before + 3, "the picture again, without reconnecting", 15_000);
        await session.CloseAsync("done");
    }

    [Fact]
    public async Task A_screen_coming_back_with_nobody_watching_opens_nothing()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, displaySession: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        await Testbed.WaitUntilAsync(() => cb.FramesByDisplay.GetValueOrDefault(0) >= 3, "watching", 15_000);
        bed.DisplaySession!.StopFromTheMachine("The remote computer's screen is locked.");
        await session.CloseAsync("done");
        await Testbed.WaitUntilAsync(() => bed.DisplaySession.Closes >= 1, "the last viewer gone", 15_000);
        int opens = bed.DisplaySession.Opens;

        bed.DisplaySession.ScreenBack();
        await Task.Delay(500);

        bed.DisplaySession.Opens.ShouldBe(opens, "nothing is shared for nobody");
        bed.DisplaySession.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task Two_viewers_arriving_while_the_machine_asks_share_one_question()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, displaySession: true);
        bed.DisplaySession!.AskFirst = "Waiting for someone there to allow it.";

        (ControllerSession a, TestCallbacks ca) = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        (ControllerSession b, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        await Testbed.WaitUntilAsync(
            () => ca.DisplaysChanges.Any(d => d.Notice.Length > 0) && cb.DisplaysChanges.Any(d => d.Notice.Length > 0),
            "both told it is being asked",
            15_000);

        bed.DisplaySession.Consent.SetResult();

        await Testbed.WaitUntilAsync(
            () => ca.FramesByDisplay.GetValueOrDefault(0) >= 3 && cb.FramesByDisplay.GetValueOrDefault(0) >= 3,
            "both watching once it opened",
            15_000);
        bed.DisplaySession.Opens.ShouldBe(1, "one question for everybody waiting");
        ca.DisplaysChanges[^1].Notice.ShouldBeEmpty("the waiting notice is cleared once there is something to show");

        await a.CloseAsync("done");
        bed.DisplaySession.IsOpen.ShouldBeTrue("one viewer is still watching");
        await b.CloseAsync("done");
        await Testbed.WaitUntilAsync(() => !bed.DisplaySession.IsOpen, "closed after the last viewer", 15_000);
    }

    [Fact]
    public async Task The_last_viewer_leaving_while_the_machine_asks_takes_the_question_back()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, displaySession: true);
        bed.DisplaySession!.AskFirst = "Waiting for someone there to allow it.";
        (ControllerSession session, TestCallbacks cb) = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges.Any(d => d.Notice.Length > 0), "being asked", 15_000);

        await session.CloseAsync("gave up");
        await Testbed.WaitUntilAsync(() => bed.DisplaySession.Closes >= 1, "the question taken back", 15_000);

        // Consent arriving after everybody left opens nothing for nobody.
        bed.DisplaySession.Consent.SetResult();
        await Task.Delay(300);
        bed.DisplaySession.IsOpen.ShouldBeFalse();
    }
}
