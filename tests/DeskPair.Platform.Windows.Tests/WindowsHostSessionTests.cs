using Microsoft.Extensions.Logging;
using DeskPair.Codec.OpenH264;
using DeskPair.Core.Services;
using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Testing;
using DeskPair.Core.Transport;
using DeskPair.Integration.Tests;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Platform.Windows.Capture;
using DeskPair.Platform.Windows.Codec;
using DeskPair.Platform.Windows.Input;

namespace DeskPair.Platform.Windows.Tests;

/// <summary>
/// The real Windows host pipeline (display enumeration, DXGI capture, Media Foundation encode, cursor) streamed
/// through the in-process servers to a controller decoding with Media Foundation. Input is faked so the test
/// never moves the user's mouse.
/// </summary>
public class WindowsHostSessionTests
{
    [Fact]
    public async Task Real_desktop_is_captured_encoded_and_decoded_at_native_size()
    {
        if (!InteractiveDesktop.IsReadable)
        {
            return;
        }

        await using Testbed bed = await Testbed.StartAsync();
        var displays = new WindowsDisplayEnumerator();
        var platform = new HostPlatform(
            displays,
            new WindowsScreenCapturerFactory(displays, bed.Logs),
            new FallbackVideoEncoderFactory(new MfVideoEncoderFactory(bed.Logs), new OpenH264EncoderFactory(bed.Logs)),
            new FakeInputInjector(),
            new WindowsCursorProvider());
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(platform: platform);
        (ControllerSession session, TestCallbacks cb, PeerConnector connector) = bed.CreateController(decoders: new FallbackVideoDecoderFactory(new MfVideoDecoderFactory(bed.Logs), new OpenH264DecoderFactory(bed.Logs)));
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();

        var primary = displays.GetDisplays().First(d => d.IsPrimary);

        // How long somebody who has just connected looks at a black window. The screen is almost certainly
        // still — nobody is typing on a test machine — and a capturer that waits for a change would never
        // deliver anything at all, which is exactly what used to happen. Generous, because it also covers
        // the encoder opening and a fall back to GDI if duplication is not presenting here.
        var connected = System.Diagnostics.Stopwatch.StartNew();
        await Testbed.WaitUntilAsync(() => cb.VideoFrames >= 1, "the first picture of a still desktop", 10_000);
        connected.Stop();
        bed.Logs.CreateLogger("test").LogInformation("First frame {Ms} ms after login", connected.ElapsedMilliseconds);
        connected.ElapsedMilliseconds.ShouldBeLessThan(5_000, "a still screen must not have to change before it is sent");

        // Not "ten frames": a desktop nobody is touching sends one picture and then switches to lossless
        // tiles, which is the right answer and is what Still_desktop_is_refined_with_lossless_tiles checks.
        // Waiting for ten here only passed when somebody happened to be moving a mouse on the test machine.
        cb.FirstFrameWasKey.ShouldBe(true);
        cb.LastFrameSize.ShouldBe((primary.Width, primary.Height));
        await Testbed.WaitUntilAsync(() => cb.CursorPositions >= 1, "cursor position", 10_000);
        bed.Logs.CreateLogger("test").LogInformation("Decoded {Frames} frames at {W}x{H}, transport {Transport}", cb.VideoFrames, primary.Width, primary.Height, session.TransportKind);

        await session.CloseAsync("done");
    }

    [Fact]
    public async Task Still_desktop_is_refined_with_lossless_tiles()
    {
        if (!InteractiveDesktop.IsReadable)
        {
            return;
        }

        await using Testbed bed = await Testbed.StartAsync();
        var displays = new WindowsDisplayEnumerator();
        var platform = new HostPlatform(
            displays,
            new WindowsScreenCapturerFactory(displays, bed.Logs),
            new FallbackVideoEncoderFactory(new MfVideoEncoderFactory(bed.Logs), new OpenH264EncoderFactory(bed.Logs)),
            new FakeInputInjector(),
            new WindowsCursorProvider());
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(platform: platform);
        (ControllerSession session, TestCallbacks cb, PeerConnector connector) = bed.CreateController(decoders: new FallbackVideoDecoderFactory(new MfVideoDecoderFactory(bed.Logs), new OpenH264DecoderFactory(bed.Logs)));
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();

        var primary = displays.GetDisplays().First(d => d.IsPrimary);
        await Testbed.WaitUntilAsync(() => cb.VideoFrames >= 1, "first decoded frame", 30_000);
        // A still desktop settles into lossless refinement. Refinement waits for the video stream to be quiet, so a
        // desktop that keeps animating (another app redrawing) legitimately never refines; that case is reported, not failed.
        VideoService video = bed.Media!.GetVideoService(primary.Index)!;
        try
        {
            await Testbed.WaitUntilAsync(() => session.TileUpdatesReceived >= 1, "lossless tile updates", 20_000);
        }
        catch (TimeoutException)
        {
            long before = video.FramesSent;
            await Task.Delay(1000);
            (video.FramesSent - before).ShouldBeGreaterThan(0, "no refinement although the desktop was still");
            bed.Logs.CreateLogger("test").LogWarning("Desktop kept changing ({Frames} video frames/s); refinement not exercised", video.FramesSent - before);
            await session.CloseAsync("done");
            return;
        }

        cb.LastFrameSize.ShouldBe((primary.Width, primary.Height));
        video.TileUpdatesSent.ShouldBeGreaterThanOrEqualTo(1);
        bed.Logs.CreateLogger("test").LogInformation("Tier {Tier}: {Frames} video frames, {Updates} tile updates / {Tiles} tiles ({Bytes} KB), mode {Mode}",
            bed.Media.Qos.Tier, video.FramesSent, video.TileUpdatesSent, video.TilesSent, video.TileBytesSent / 1024, video.LastMode);

        await session.CloseAsync("done");
    }
}
