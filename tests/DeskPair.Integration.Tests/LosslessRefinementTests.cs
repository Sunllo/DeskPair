using DeskPair.Core.Qos;
using DeskPair.Core.Services;
using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Testing;
using DeskPair.Core.Transport;

namespace DeskPair.Integration.Tests;

/// <summary>RDP-style hybrid streaming: video while things move, lossless tiles once the screen settles.</summary>
public class LosslessRefinementTests
{
    private const int W = 640, H = 360;

    /// <summary>Synthetic editor page: dark glyph strokes on a light background, scrolled by <paramref name="offset"/> rows.</summary>
    private static void Paint(byte[] bgra, int width, int height, int offset)
    {
        var rng = new Random(7);
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 0xF4;
            bgra[i + 1] = 0xF4;
            bgra[i + 2] = 0xF2;
            bgra[i + 3] = 0xFF;
        }

        for (int line = 4; line < height * 2; line += 14)
        {
            for (int x = 8; x < width - 8; x += rng.Next(5, 9))
            {
                int glyphH = rng.Next(5, 9);
                int glyphW = rng.Next(2, 5);
                for (int y = line - offset; y < line - offset + glyphH; y++)
                {
                    if (y < 0 || y >= height)
                    {
                        continue;
                    }

                    for (int dx = 0; dx < glyphW && x + dx < width; dx++)
                    {
                        int o = (y * width + x + dx) * 4;
                        bgra[o] = 0x20;
                        bgra[o + 1] = 0x22;
                        bgra[o + 2] = 0x24;
                    }
                }
            }
        }
    }

    private static byte[] Truth(int offset)
    {
        byte[] b = new byte[W * H * 4];
        Paint(b, W, H, offset);
        return b;
    }

    /// <summary>The editor page with a 200x120 "window" (a flat panel with a frame) whose left edge is at <paramref name="windowX"/>.</summary>
    private static void PaintWithWindow(byte[] bgra, int width, int height, int windowX)
    {
        Paint(bgra, width, height, 0);
        for (int y = 100; y < 220; y++)
        {
            for (int x = windowX; x < windowX + 200 && x < width; x++)
            {
                if (x < 0)
                {
                    continue;
                }

                bool border = y == 100 || y == 219 || x == windowX || x == windowX + 199;
                int o = (y * width + x) * 4;
                bgra[o] = border ? (byte)0x80 : (byte)0xC0;
                bgra[o + 1] = border ? (byte)0x40 : (byte)0x90;
                bgra[o + 2] = border ? (byte)0x10 : (byte)0x30;
            }
        }
    }

    private static byte[] TruthWithWindow(int windowX)
    {
        byte[] b = new byte[W * H * 4];
        PaintWithWindow(b, W, H, windowX);
        return b;
    }

    private static async Task<(ControllerSession Session, TestCallbacks Cb, HostRuntime Host)> StartAsync(Testbed bed, FakeFrameGenerator generator, bool losslessTiles = true, TimeSpan ackDelay = default, double mediaLoss = 0)
    {
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true);
        bed.Capturers!.UnchangedEvery = 0;
        bed.Capturers.Generator = generator;
        var cb = new TestCallbacks { KeepLastFrame = true, AckDelay = ackDelay };
        (ControllerSession session, _, PeerConnector connector) = bed.CreateController(callbacks: cb, losslessTiles: losslessTiles, mediaLoss: mediaLoss);
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();
        return (session, cb, host);
    }

    [Fact]
    public async Task Static_screen_is_refined_until_the_viewer_matches_the_host_bit_for_bit()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (ControllerSession session, TestCallbacks cb, _) = await StartAsync(bed, (frame, bgra, _, w, h) =>
        {
            if (frame > 1)
            {
                return false; // nothing changes after the first frame
            }

            Paint(bgra, w, h, 0);
            return true;
        });

        await Testbed.WaitUntilAsync(() => cb.VideoFrames >= 1, "first (lossy) video frame", 15_000);
        cb.LastFrame!.ShouldNotBe(Truth(0)); // the fake codec is lossy on purpose

        VideoService video = bed.Media!.GetVideoService(0)!;
        await Testbed.WaitUntilAsync(() => session.TilesReceived >= 60, "all 60 tiles refined", 15_000);
        await Testbed.WaitUntilAsync(() => cb.LastFrame!.AsSpan().SequenceEqual(Truth(0)), "viewer bit-exact", 5_000);
        video.TileUpdatesSent.ShouldBeGreaterThan(0);
        video.LastMode.ShouldBeOneOf(VideoMode.Refining, VideoMode.Idle);

        // Once refined, a still screen costs nothing more.
        long tiles = video.TilesSent, frames = video.FramesSent;
        await Task.Delay(1500);
        video.TilesSent.ShouldBe(tiles);
        video.FramesSent.ShouldBe(frames);
    }

    [Fact]
    public async Task Small_change_on_a_settled_picture_goes_out_as_lossless_tiles_on_the_same_timeline()
    {
        await using Testbed bed = await Testbed.StartAsync();
        int phase = 0;
        (ControllerSession session, TestCallbacks cb, _) = await StartAsync(bed, (frame, bgra, _, w, h) =>
        {
            if (frame == 1)
            {
                Paint(bgra, w, h, 0);
                return true;
            }

            if (Volatile.Read(ref phase) == 1)
            {
                // A caret-sized change: 2 pixels in one tile.
                bgra[(100 * w + 100) * 4] = 0x00;
                bgra[(101 * w + 100) * 4] = 0x00;
                Volatile.Write(ref phase, 2);
                return true;
            }

            return false;
        });

        await Testbed.WaitUntilAsync(() => cb.LastFrame is not null && cb.LastFrame.AsSpan().SequenceEqual(Truth(0)), "initial refinement", 15_000);
        VideoService video = bed.Media!.GetVideoService(0)!;
        await Task.Delay(VideoService.RefinementQuietTime + TimeSpan.FromMilliseconds(100));
        long frames = video.FramesSent;
        long updates = video.TileUpdatesSent;

        Volatile.Write(ref phase, 1);
        byte[] expected = Truth(0);
        expected[(100 * W + 100) * 4] = 0x00;
        expected[(101 * W + 100) * 4] = 0x00;
        await Testbed.WaitUntilAsync(() => video.TileUpdatesSent > updates && cb.LastFrame!.AsSpan().SequenceEqual(expected), "caret patched losslessly", 10_000);
        video.FramesSent.ShouldBe(frames); // no video restart for a caret on a settled picture
        session.TilesRejectedBrokenReference.ShouldBe(0);
    }

    [Fact]
    public async Task Scrolling_streams_video_and_settles_into_lossless()
    {
        await using Testbed bed = await Testbed.StartAsync();
        int scrolling = 1;
        int lastOffset = 0;
        (ControllerSession session, TestCallbacks cb, _) = await StartAsync(bed, (frame, bgra, _, w, h) =>
        {
            if (Volatile.Read(ref scrolling) == 1)
            {
                lastOffset = frame % 200;
                Paint(bgra, w, h, lastOffset);
                return true;
            }

            return false;
        });

        await Testbed.WaitUntilAsync(() => cb.VideoFrames >= 30, "30 video frames while scrolling", 15_000);
        VideoService video = bed.Media!.GetVideoService(0)!;
        video.FramesSent.ShouldBeGreaterThanOrEqualTo(30);
        video.TileUpdatesSent.ShouldBe(0); // whole-screen motion never goes out as tiles
        video.LastMode.ShouldBe(VideoMode.Video);

        Volatile.Write(ref scrolling, 0);
        await Task.Delay(100);
        int offset = lastOffset;
        await Testbed.WaitUntilAsync(() => cb.LastFrame!.AsSpan().SequenceEqual(Truth(offset)), "refined after scrolling stops", 15_000);
        session.TilesReceived.ShouldBeGreaterThanOrEqualTo(60);
    }

    [Fact]
    public async Task A_playing_video_window_streams_as_video_even_though_it_is_small()
    {
        await using Testbed bed = await Testbed.StartAsync();
        int playing = 1;
        (ControllerSession session, TestCallbacks cb, _) = await StartAsync(bed, (frame, bgra, stride, w, h) =>
        {
            if (frame == 1)
            {
                Paint(bgra, w, h, 0);
                return true;
            }

            if (Volatile.Read(ref playing) == 0)
            {
                return false;
            }

            // A 128x128 "player" (4 of 60 tiles, under every tile threshold) with new noise every frame.
            var rng = new Random(frame);
            for (int y = 64; y < 192; y++)
            {
                rng.NextBytes(bgra.AsSpan(y * stride + 64 * 4, 128 * 4));
            }

            return true;
        });

        await Testbed.WaitUntilAsync(() => cb.VideoFrames >= 40, "frames while playing", 15_000);
        VideoService video = bed.Media!.GetVideoService(0)!;
        await Testbed.WaitUntilAsync(() => video.FramesSent >= 30, "video path chosen for the player", 15_000);
        video.LastMode.ShouldBe(VideoMode.Video);
        video.TileUpdatesSent.ShouldBe(0); // one stream while anything moves

        Volatile.Write(ref playing, 0);
        await Testbed.WaitUntilAsync(() => session.TilesReceived >= 60, "refined after playback stops", 15_000);
        session.TileUpdatesReceived.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.05)]
    public async Task Dragging_a_window_streams_one_video_timeline_and_settles_bit_exact(double loss)
    {
        await using Testbed bed = await Testbed.StartAsync();
        int dragging = 0;
        int windowX = 20;
        (ControllerSession session, TestCallbacks cb, _) = await StartAsync(bed, (frame, bgra, _, w, h) =>
        {
            if (frame == 1)
            {
                PaintWithWindow(bgra, w, h, windowX);
                return true;
            }

            if (Volatile.Read(ref dragging) == 1)
            {
                windowX = 20 + (frame % 300);
                PaintWithWindow(bgra, w, h, windowX);
                return true;
            }

            return false;
        }, mediaLoss: loss);

        await Testbed.WaitUntilAsync(() => cb.LastFrame is not null && cb.LastFrame.AsSpan().SequenceEqual(TruthWithWindow(20)), "settled before the drag", 15_000);
        VideoService video = bed.Media!.GetVideoService(0)!;
        long tilesBefore = video.TileUpdatesSent;
        int keyFramesBefore = cb.KeyFrames;

        Volatile.Write(ref dragging, 1);
        await Task.Delay(2500);
        // Continuous motion is video; at most the very first step (before the region counts as moving) is patched.
        (video.TileUpdatesSent - tilesBefore).ShouldBeLessThanOrEqualTo(2);
        Volatile.Write(ref dragging, 0);
        await Task.Delay(100); // let a capture already in progress finish before reading the final position
        int finalX = Volatile.Read(ref windowX);
        if (loss == 0)
        {
            (cb.KeyFrames - keyFramesBefore).ShouldBeLessThanOrEqualTo(2);
        }

        await Testbed.WaitUntilAsync(() => cb.LastFrame!.AsSpan().SequenceEqual(TruthWithWindow(finalX)), "bit-exact after the drag stops", 15_000);
        video.TileUpdatesSent.ShouldBeGreaterThan(tilesBefore);
    }

    [Fact]
    public async Task Legacy_viewer_without_tile_support_only_gets_video()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (ControllerSession session, TestCallbacks cb, HostRuntime host) = await StartAsync(bed, (frame, bgra, _, w, h) =>
        {
            if (frame > 1)
            {
                return false;
            }

            Paint(bgra, w, h, 0);
            return true;
        }, losslessTiles: false);

        await Testbed.WaitUntilAsync(() => cb.VideoFrames >= 3, "keep-alive video frames", 15_000);
        host.Sessions.Single().Context.Options.SupportedDecoding?.Tiles.ShouldNotBe(true);
        VideoService video = bed.Media!.GetVideoService(0)!;
        await Task.Delay(2500);
        video.TileUpdatesSent.ShouldBe(0);
        session.TileUpdatesReceived.ShouldBe(0);
        cb.LastFrame!.ShouldNotBe(Truth(0));
    }

    [Fact]
    public async Task Slow_viewer_is_refined_within_the_ack_window_and_still_converges()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (ControllerSession session, TestCallbacks cb, HostRuntime host) = await StartAsync(bed, (frame, bgra, _, w, h) =>
        {
            if (frame > 1)
            {
                return false;
            }

            Paint(bgra, w, h, 0);
            return true;
        }, ackDelay: TimeSpan.FromMilliseconds(60));

        VideoQosController qos = bed.Media!.Qos;
        int conn = host.Sessions.Single().Context.ConnectionId;
        await Testbed.WaitUntilAsync(() => cb.LastFrame is not null && cb.LastFrame.AsSpan().SequenceEqual(Truth(0)), "converged despite slow acks", 30_000);
        VideoService video = bed.Media.GetVideoService(0)!;
        qos.IsCongested(conn, 0).ShouldBeFalse();
        video.TileUpdatesSent.ShouldBe(session.TileUpdatesReceived);
    }
}
