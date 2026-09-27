using DeskPair.Core.Services;
using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Testing;
using DeskPair.Core.Transport;
using DeskPair.Platform.Abstractions.Audio;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Protocol.Messages;

namespace DeskPair.Integration.Tests;

public class MediaSessionTests
{
    private static async Task<(ControllerSession Session, TestCallbacks Cb)> ConnectAndLoginAsync(Testbed bed, HostRuntime host, string password, SessionOptions? options = null, TestCallbacks? cb = null)
    {
        (ControllerSession session, TestCallbacks callbacks, PeerConnector connector) = bed.CreateController(options: options, callbacks: cb);
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(password, CancellationToken.None)).Success.ShouldBeTrue();
        return (session, callbacks);
    }

    [Fact]
    public async Task Controller_receives_decoded_video_and_acks_it()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAndLoginAsync(bed, host, passwords.TemporaryPassword);

        await Testbed.WaitUntilAsync(() => cb.VideoFrames >= 30, "30 decoded frames", 15_000);
        cb.FirstFrameWasKey.ShouldBe(true);
        cb.LastFrameSize.ShouldBe((640, 360)); // fake decoder upscales its 1/8 thumbnail back to full size
        cb.DisplaySwitches.ShouldContain(0);
        session.VideoFramesDecoded.ShouldBe(cb.VideoFrames);

        HostSession hostSession = host.Sessions.Single();
        VideoService video = bed.Media!.GetVideoService(0)!;
        video.IsRunning.ShouldBeTrue();
        await Testbed.WaitUntilAsync(() => !bed.Media.Qos.IsCongested(hostSession.Context.ConnectionId, 0), "acks flowing");

        await session.CloseAsync("done");
        await Testbed.WaitUntilAsync(() => !video.IsRunning, "capture stops with last viewer");
    }

    [Fact]
    public async Task Cursor_shape_and_position_are_delivered()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true);
        (_, TestCallbacks cb) = await ConnectAndLoginAsync(bed, host, passwords.TemporaryPassword);

        await Testbed.WaitUntilAsync(() => cb.CursorShapes.Count >= 2, "two cursor shapes", 10_000);
        await Testbed.WaitUntilAsync(() => cb.CursorPositions >= 10, "cursor moves");
        await Testbed.WaitUntilAsync(() => cb.CursorIds >= 1, "cursor id reuse", 10_000);
    }

    [Fact]
    public async Task Input_is_injected_only_with_keyboard_permission()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAndLoginAsync(bed, host, passwords.TemporaryPassword);
        FakeInputInjector injector = bed.Injector!;

        await session.SendMouseAsync(new MouseEvent { Mask = 0, X = 100, Y = 50 });
        await session.SendMouseAsync(new MouseEvent { Mask = 1 | (1 << 3), X = 100, Y = 50 });
        await session.SendKeyAsync(new KeyEvent { Chr = 0x1E, Down = true, Mode = KeyboardMode.KmMap });
        await session.SendKeyAsync(new KeyEvent { ControlKey = ControlKey.CkReturn, Press = true });
        await Testbed.WaitUntilAsync(() => injector.MouseCount == 2 && injector.KeyCount == 3, "input injected");
        injector.Mouse[1].Buttons.ShouldBe(Platform.Abstractions.Input.MouseButtons.Left);
        injector.Keys[0].Code.ShouldBe(0x1Eu);
        injector.Keys[1].Control.ShouldBe(Platform.Abstractions.Input.ControlKey.Return);

        HostSessionContext ctx = host.Sessions.Single().Context;
        ctx.Permissions.SetOverride(Permission.PermKeyboard, false);
        await Testbed.WaitUntilAsync(() => cb.Permissions.Any(p => p.Permission == Permission.PermKeyboard && !p.Enabled), "permission revoked");
        await session.SendMouseAsync(new MouseEvent { Mask = 0, X = 1, Y = 1 });
        await session.SendPingAsync();
        await Testbed.WaitUntilAsync(() => cb.LastRtt is not null, "ping after input");
        injector.MouseCount.ShouldBe(2);

        await session.CloseAsync("done");
        await Testbed.WaitUntilAsync(() => injector.ReleaseAllCalls >= 1, "keys released on close");
    }

    [Fact]
    public async Task Audio_is_encoded_decoded_and_played()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true);
        var playback = new RecordingPlayback();
        (ControllerSession session, TestCallbacks cb, PeerConnector connector) = bed.CreateController(playback: playback);
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();

        await Testbed.WaitUntilAsync(() => cb.AudioFormat is not null, "audio format");
        cb.AudioFormat!.Value.SampleRate.ShouldBe(48000);
        await Testbed.WaitUntilAsync(() => playback.Samples >= 48000 * 2 / 4, "250 ms of audio", 10_000);
        playback.Peak.ShouldBeGreaterThan(0.05f);
    }

    [Fact]
    public async Task Slow_viewer_is_throttled_by_acks_and_recovers_with_a_keyframe()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true);
        var cb = new TestCallbacks { AckDelay = TimeSpan.FromMilliseconds(400) };
        (ControllerSession session, _) = await ConnectAndLoginAsync(bed, host, passwords.TemporaryPassword, cb: cb);

        await Testbed.WaitUntilAsync(() => cb.VideoFrames >= 5, "some frames", 15_000);
        int hostConn = host.Sessions.Single().Context.ConnectionId;
        await Testbed.WaitUntilAsync(() => bed.Media!.Qos.IsCongested(hostConn, 0), "congestion detected", 15_000);

        cb.AckDelay = TimeSpan.Zero;
        int before = cb.KeyFrames;
        await Testbed.WaitUntilAsync(() => cb.KeyFrames > before, "keyframe after recovery", 15_000);
        await session.CloseAsync("done");
    }

    [Fact]
    public async Task Switching_display_moves_the_subscription()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, displays: 2);
        (ControllerSession session, TestCallbacks cb) = await ConnectAndLoginAsync(bed, host, passwords.TemporaryPassword);
        await Testbed.WaitUntilAsync(() => cb.VideoFrames >= 3, "frames on display 0", 15_000);

        await session.SwitchDisplayAsync(1);
        await Testbed.WaitUntilAsync(() => cb.DisplaySwitches.Contains(1), "switch acknowledged");
        await Testbed.WaitUntilAsync(() => cb.FramesByDisplay.GetValueOrDefault(1) >= 3, "frames on display 1", 15_000);
        await Testbed.WaitUntilAsync(() => !bed.Media!.GetVideoService(0)!.IsRunning, "display 0 capture stopped");
        bed.Media!.GetVideoService(1)!.IsRunning.ShouldBeTrue();

        // Input follows the picture: the viewer's pixels are of display 1, which on the fake host starts
        // where display 0 ends. A click that stayed at (10, 20) would have landed on display 0.
        FakeInputInjector injector = bed.Injector!;
        int before = injector.MouseCount;
        await session.SendMouseAsync(new MouseEvent { Mask = 0, X = 10, Y = 20, Display = 1 });
        await session.SendMouseAsync(new MouseEvent { Mask = 0, X = 10, Y = 20, Display = 0 });
        await Testbed.WaitUntilAsync(() => injector.MouseCount == before + 2, "moves injected");
        (injector.Mouse[before].X, injector.Mouse[before].Y).ShouldBe((bed.Displays!.Displays[1].X + 10, 20));
        (injector.Mouse[before + 1].X, injector.Mouse[before + 1].Y).ShouldBe((10, 20));
    }

    /// <summary>
    /// A monitor is pulled out while a viewer is watching it. The host notices on its own -- nobody asked
    /// for anything -- moves the viewer to the primary, drops the stream nobody can capture any more, and
    /// tells everyone the new list. Without this the viewer sat on a dead stream until it happened to switch.
    /// </summary>
    [Fact]
    public async Task A_display_that_disappears_moves_its_viewer_to_the_primary()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, displays: 2);
        (ControllerSession session, TestCallbacks cb) = await ConnectAndLoginAsync(bed, host, passwords.TemporaryPassword);
        await session.SwitchDisplayAsync(1);
        await Testbed.WaitUntilAsync(() => cb.FramesByDisplay.GetValueOrDefault(1) >= 3, "frames on display 1", 15_000);
        int switchesToPrimary = cb.DisplaySwitches.Count(d => d == 0);
        int framesOnPrimary = cb.FramesByDisplay.GetValueOrDefault(0);

        bed.Displays!.Displays.RemoveAt(1);
        bed.Displays.Raise();

        await Testbed.WaitUntilAsync(() => cb.DisplaySwitches.Count(d => d == 0) > switchesToPrimary, "moved to the primary", 15_000);
        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges.Count >= 1, "told about the new list", 15_000);
        DisplaysChanged told = cb.DisplaysChanges[^1];
        told.Failure.ShouldBeEmpty();
        told.Changed.ShouldBe(-1, "no single display changed; the set did");
        told.Displays.Count.ShouldBe(1);
        told.CurrentDisplay.ShouldBe(0);
        session.PeerInfo!.CurrentDisplay.ShouldBe(0);
        await Testbed.WaitUntilAsync(() => cb.FramesByDisplay.GetValueOrDefault(0) > framesOnPrimary + 2, "frames on the primary again", 15_000);
        bed.Media!.GetVideoService(1).ShouldBeNull("the stream for the missing display is gone");
        bed.Media.GetVideoService(0)!.IsRunning.ShouldBeTrue();
    }

    /// <summary>
    /// The last display goes, then one comes back: the viewer is on it and seeing it without asking. With every
    /// display gone there was no primary to move the viewer to, so its set used to stay as it was, and when a display
    /// returned that set read as "already watching" -- no stream started, and the viewer looked at nothing.
    /// </summary>
    [Fact]
    public async Task A_viewer_whose_displays_all_went_watches_the_first_that_comes_back()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAndLoginAsync(bed, host, passwords.TemporaryPassword);
        await Testbed.WaitUntilAsync(() => cb.FramesByDisplay.GetValueOrDefault(0) >= 3, "frames on the display", 15_000);
        DisplayDescriptor only = bed.Displays!.Displays[0];

        bed.Displays.Displays.Clear();
        bed.Displays.Raise();
        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges.Count >= 1 && cb.DisplaysChanges[^1].Displays.Count == 0, "told there is none", 15_000);
        int before = cb.FramesByDisplay.GetValueOrDefault(0);

        bed.Displays.Displays.Add(only);
        bed.Displays.Raise();

        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges[^1].Displays.Count == 1, "told it is back", 15_000);
        await Testbed.WaitUntilAsync(() => cb.FramesByDisplay.GetValueOrDefault(0) >= before + 3, "the picture again", 15_000);
        bed.Media!.GetVideoService(0)!.IsRunning.ShouldBeTrue();
        await session.CloseAsync("done");
    }

    /// <summary>
    /// Somebody at the desk changes the resolution. The host did not do it, so the platform's notice is
    /// the only way to find out; streams restart at the new size and viewers hear it. The same notice
    /// arrives for changes the host made itself, and for those nothing happens twice.
    /// </summary>
    [Fact]
    public async Task A_change_made_at_the_desk_restarts_the_streams_and_an_echo_does_nothing()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAndLoginAsync(bed, host, passwords.TemporaryPassword);
        await Testbed.WaitUntilAsync(() => cb.VideoFrames >= 3, "frames at the original size", 15_000);

        // The host's own change: one broadcast, and the notice that follows it is recognised as an echo.
        await session.SetResolutionAsync(0, new Resolution { Width = 320, Height = 180 });
        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges.Count >= 1, "the host's change is broadcast", 15_000);
        bed.Displays!.Raise();
        await Task.Delay(HostMediaModule.DisplayChangeSettle * 3);
        cb.DisplaysChanges.Count.ShouldBe(1, "an echo of the host's own change is not broadcast again");

        // Somebody at the desk puts it back: the host finds out from the platform alone.
        bed.Displays.Displays[0] = bed.Displays.Displays[0] with { Width = 640, Height = 360 };
        bed.Displays.Raise();

        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges.Count >= 2, "told about the desk's change", 15_000);
        cb.DisplaysChanges[1].Displays[0].Width.ShouldBe(640);
        cb.DisplaysChanges[1].Changed.ShouldBe(-1);
        await Testbed.WaitUntilAsync(() => cb.LastFrameSize == (640, 360), "frames at the size the desk chose", 15_000);
    }

    /// <summary>
    /// A viewer chooses a display mode. The host's screen changes, every viewer hears the new geometry
    /// and gets frames at the new size, and the screen goes back to its original once the last viewer
    /// leaves -- whoever made the change.
    /// </summary>
    [Fact]
    public async Task A_viewer_changes_the_resolution_everyone_hears_and_the_last_one_out_restores_it()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true);
        (ControllerSession a, TestCallbacks ca) = await ConnectAndLoginAsync(bed, host, passwords.TemporaryPassword);
        (ControllerSession b, TestCallbacks cb) = await ConnectAndLoginAsync(bed, host, passwords.TemporaryPassword);
        await Testbed.WaitUntilAsync(() => ca.VideoFrames >= 3 && cb.VideoFrames >= 3, "frames at the original size", 15_000);
        ca.PeerInfo!.Displays[0].Modes.Count.ShouldBe(2, "the fake advertises two modes");
        ca.PeerInfo.Displays[0].Original.ShouldBeNull("nothing has been changed yet");

        await a.SetResolutionAsync(0, new Resolution { Width = 320, Height = 180 });

        await Testbed.WaitUntilAsync(() => ca.DisplaysChanges.Count >= 1 && cb.DisplaysChanges.Count >= 1, "both viewers told", 15_000);
        DisplaysChanged told = cb.DisplaysChanges[0];
        told.Failure.ShouldBeEmpty();
        told.Changed.ShouldBe(0);
        told.Displays[0].Width.ShouldBe(320);
        told.Displays[0].Original.ShouldNotBeNull();
        told.Displays[0].Original.Width.ShouldBe(640);
        b.PeerInfo!.Displays[0].Width.ShouldBe(320, "the session's own copy is kept current");
        await Testbed.WaitUntilAsync(() => ca.LastFrameSize == (320, 180) && cb.LastFrameSize == (320, 180), "frames at the new size", 15_000);
        bed.Displays!.Displays[0].Width.ShouldBe(320);

        // A size the display never advertised is refused, and only the asker hears about it.
        await b.SetResolutionAsync(0, new Resolution { Width = 999, Height = 999 });
        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges.Count >= 2, "refusal delivered");
        cb.DisplaysChanges[1].Failure.ShouldContain("999x999");
        ca.DisplaysChanges.Count.ShouldBe(1, "a refusal is not broadcast");

        // The first viewer leaves: nothing changes, somebody is still watching.
        await a.CloseAsync("done");
        await Testbed.WaitUntilAsync(() => host.Sessions.Count == 1, "one session left");
        bed.Displays.Displays[0].Width.ShouldBe(320);

        // The last one leaves: the screen is put back.
        await b.CloseAsync("done");
        await Testbed.WaitUntilAsync(() => host.Sessions.Count == 0, "all sessions closed");
        await Testbed.WaitUntilAsync(() => bed.Displays.Displays[0].Width == 640, "original restored");
        bed.DisplayModes!.Calls.Count.ShouldBe(2, "one change, one restore; the refused size never reached the platform");
    }

    [Fact]
    public async Task A_viewer_without_keyboard_permission_cannot_change_the_resolution()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(policy: new HostPolicy { KeyboardEnabled = false }, media: true);
        (ControllerSession session, TestCallbacks cb) = await ConnectAndLoginAsync(bed, host, passwords.TemporaryPassword);
        await Testbed.WaitUntilAsync(() => cb.VideoFrames >= 1, "a frame", 15_000);

        await session.SetResolutionAsync(0, new Resolution { Width = 320, Height = 180 });

        await Testbed.WaitUntilAsync(() => cb.DisplaysChanges.Count >= 1, "refusal delivered");
        cb.DisplaysChanges[0].Failure.ShouldContain("permission");
        bed.Displays!.Displays[0].Width.ShouldBe(640);
    }

    private sealed class RecordingPlayback : IAudioPlayback
    {
        public long Samples;
        public float Peak;

        public ValueTask ConfigureAsync(AudioStreamFormat format, CancellationToken ct) => ValueTask.CompletedTask;

        public void Enqueue(ReadOnlySpan<float> interleavedPcm)
        {
            Interlocked.Add(ref Samples, interleavedPcm.Length);
            foreach (float s in interleavedPcm)
            {
                Peak = Math.Max(Peak, Math.Abs(s));
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
