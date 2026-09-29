using System.Reflection;
using Microsoft.Extensions.Logging;
using DeskPair.Core.Services;
using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Transport;
using DeskPair.Core.Testing;
using DeskPair.Integration.Tests;
using Xunit.Abstractions;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Platform.Windows.Codec;

namespace DeskPair.Platform.Windows.Tests;

/// <summary>Full session over the in-process servers with the real Media Foundation H.264 encoder and decoder.</summary>
public class MfSessionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Synthetic_desktop_streams_as_h264_and_decodes_at_full_size()
    {
        if (!MediaFoundationEncoding.CanEncode(VideoCodec.H264))
        {
            return; // a machine whose Media Foundation lists an H.264 encoder that will not encode
        }

        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, encoders: new MfVideoEncoderFactory(bed.Logs));
        (ControllerSession session, TestCallbacks cb, PeerConnector connector) = bed.CreateController(decoders: new MfVideoDecoderFactory(bed.Logs));
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();

        await Testbed.WaitUntilAsync(() => cb.VideoFrames >= 30, "30 decoded H.264 frames", 20_000);
        cb.FirstFrameWasKey.ShouldBe(true);
        cb.LastFrameSize.ShouldBe((640, 360));
        cb.KeyFrames.ShouldBeGreaterThanOrEqualTo(1);
        session.VideoFramesDecoded.ShouldBe(cb.VideoFrames);

        await session.CloseAsync("done");
    }

    /// <summary>
    /// Whole pipeline at 2560x1440 with full-screen motion: fake capture -> SIMD convert -> NVENC -> loopback TCP ->
    /// MF decode -> SIMD convert -> compositor. Reports the host's per-stage cost and the viewer's achieved frame rate.
    /// </summary>
    [Fact]
    public async Task Full_screen_motion_at_1440p_sustains_the_frame_rate()
    {
        if (!MediaFoundationEncoding.CanEncode(VideoCodec.H264))
        {
            return; // a machine whose Media Foundation lists an H.264 encoder that will not encode
        }

        const int W = 2560, H = 1440;
        _ = timeBeginPeriod(1); // the fake capturer bypasses the DXGI factory that normally raises the timer resolution
        await using Testbed bed = await Testbed.StartAsync();
        var capturers = new FakeScreenCapturerFactory
        {
            Generator = (frame, bgra, stride, w, h) =>
            {
                // Scroll the picture by 8 rows and paint a fresh noisy band: every tile changes every frame.
                bgra.AsSpan(8 * stride, (h - 8) * stride).CopyTo(bgra.AsSpan(0));
                var rng = new Random(frame);
                rng.NextBytes(bgra.AsSpan((h - 8) * stride, 8 * stride));
                return true;
            },
        };
        var platform = new HostPlatform(new FakeDisplayEnumerator(W, H), capturers, new MfVideoEncoderFactory(bed.Logs), new FakeInputInjector(), new FakeCursorProvider());
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(platform: platform);
        (ControllerSession session, TestCallbacks cb, PeerConnector connector) = bed.CreateController(decoders: new MfVideoDecoderFactory(bed.Logs));
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();

        await Testbed.WaitUntilAsync(() => cb.VideoFrames >= 10, "stream started", 20_000);
        int start = cb.VideoFrames;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(6000);
        double fps = (cb.VideoFrames - start) / sw.Elapsed.TotalSeconds;
        VideoService video = bed.Media!.GetVideoService(0)!;
        VideoStats? stats = video.LastStats;
        output.WriteLine($"viewer {fps:F1} fps; host: {stats}");
        bed.Logs.CreateLogger("test").LogInformation("viewer {Fps:F1} fps; host {Stats}", fps, stats);
        cb.LastFrameSize.ShouldBe((W, H));
        stats.ShouldNotBeNull();
        bool optimized = typeof(VideoService).Assembly.GetCustomAttribute<System.Diagnostics.DebuggableAttribute>()?.IsJITOptimizerDisabled != true;
        if (optimized)
        {
            // Debug builds run the SIMD kernels unoptimised; only a Release build says anything about the pipeline.
            stats.Fps.ShouldBeGreaterThan(45);
            fps.ShouldBeGreaterThan(45);
        }

        await session.CloseAsync("done");
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);
}
