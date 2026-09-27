using System.Diagnostics;
using Google.Protobuf;
using DeskPair.Core.Video;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Protocol.Messages;
using TileCodec = DeskPair.Core.Video.TileCodec;
using Xunit.Abstractions;

namespace DeskPair.Core.Tests;

public class TileCodecTests(ITestOutputHelper output)
{
    /// <summary>Synthetic "text": dark glyph-like strokes on a light background, as in an editor.</summary>
    public static byte[] TextLike(int width, int height, int seed = 1)
    {
        var rng = new Random(seed);
        byte[] bgra = new byte[width * height * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 0xF4;
            bgra[i + 1] = 0xF4;
            bgra[i + 2] = 0xF2;
            bgra[i + 3] = 0xFF;
        }

        for (int line = 4; line < height - 8; line += 14)
        {
            for (int x = 8; x < width - 8; x += rng.Next(5, 9))
            {
                int glyphH = rng.Next(5, 9);
                for (int y = line; y < Math.Min(height, line + glyphH); y++)
                {
                    for (int dx = 0; dx < rng.Next(2, 5) && x + dx < width; dx++)
                    {
                        int o = (y * width + x + dx) * 4;
                        bgra[o] = 0x20;
                        bgra[o + 1] = 0x22;
                        bgra[o + 2] = 0x24;
                    }
                }
            }
        }

        return bgra;
    }

    [Theory]
    [InlineData(64, 64)]
    [InlineData(17, 9)]
    [InlineData(1, 1)]
    public void Random_tiles_round_trip_exactly(int w, int h)
    {
        var rng = new Random(w * 100 + h);
        byte[] src = new byte[w * h * 4];
        rng.NextBytes(src);
        for (int i = 3; i < src.Length; i += 4)
        {
            src[i] = 255;
        }

        byte[] encoded = new byte[TileCodec.MaxEncodedLength(w, h)];
        int n = TileCodec.Encode(src, w * 4, w, h, encoded);
        byte[] back = new byte[w * h * 4];
        TileCodec.Decode(encoded.AsSpan(0, n), w, h, back, w * 4);
        back.ShouldBe(src);
    }

    [Fact]
    public void Tile_inside_a_larger_frame_uses_the_stride()
    {
        const int W = 200, H = 120;
        byte[] frame = TextLike(W, H);
        byte[] encoded = new byte[TileCodec.MaxEncodedLength(64, 64)];
        int n = TileCodec.Encode(frame.AsSpan((10 * W + 20) * 4), W * 4, 64, 64, encoded);
        byte[] target = new byte[frame.Length];
        TileCodec.Decode(encoded.AsSpan(0, n), 64, 64, target.AsSpan((10 * W + 20) * 4), W * 4);
        for (int y = 0; y < 64; y++)
        {
            target.AsSpan(((10 + y) * W + 20) * 4, 64 * 4).ToArray().ShouldBe(frame.AsSpan(((10 + y) * W + 20) * 4, 64 * 4).ToArray());
        }
    }

    [Fact]
    public void Text_and_flat_tiles_compress_well()
    {
        byte[] text = TextLike(64, 64);
        byte[] encoded = new byte[TileCodec.MaxEncodedLength(64, 64)];
        int n = TileCodec.Encode(text, 64 * 4, 64, 64, encoded);
        double ratio = 64.0 * 64 * 4 / n;
        output.WriteLine($"text tile: {n} bytes, ratio {ratio:F1}x");
        ratio.ShouldBeGreaterThan(8);

        byte[] flat = new byte[64 * 64 * 4];
        Array.Fill(flat, (byte)0x30);
        int m = TileCodec.Encode(flat, 64 * 4, 64, 64, encoded);
        output.WriteLine($"flat tile: {m} bytes");
        m.ShouldBeLessThan(64);
    }

    [Fact]
    public void Full_desktop_refinement_cost_is_reported()
    {
        const int W = 2560, H = 1440;
        byte[] frame = TextLike(W, H);
        var detector = new ChangeDetector();
        byte[] encoded = new byte[TileCodec.MaxEncodedLength(64, 64)];
        var sw = Stopwatch.StartNew();
        List<int> tiles = detector.Update(frame, W * 4, W, H);
        long hashMs = sw.ElapsedMilliseconds;
        long bytes = 0;
        sw.Restart();
        foreach (int t in tiles)
        {
            (int x, int y, int w, int h) = detector.TileRect(t);
            bytes += TileCodec.Encode(frame.AsSpan((y * W + x) * 4), W * 4, w, h, encoded);
        }

        output.WriteLine($"{W}x{H}: hash {hashMs} ms, encode {tiles.Count} tiles {sw.ElapsedMilliseconds} ms, {bytes / 1024} KB ({W * H * 4.0 / bytes:F1}x)");
        tiles.Count.ShouldBe(detector.TileCount);
    }

    [Fact]
    public void Change_detector_reports_only_touched_tiles()
    {
        const int W = 300, H = 200;
        byte[] frame = TextLike(W, H);
        var detector = new ChangeDetector();
        detector.Update(frame, W * 4, W, H).Count.ShouldBe(detector.TileCount);
        detector.Update(frame, W * 4, W, H).Count.ShouldBe(0);

        frame[(100 * W + 150) * 4] ^= 0xFF; // tile (2,1)
        List<int> changed = detector.Update(frame, W * 4, W, H);
        changed.ShouldBe([1 * detector.Columns + 2]);

        // Dirty-rect path must agree with the full scan.
        frame[(10 * W + 10) * 4] ^= 0xFF;
        detector.Update(frame, W * 4, W, H, new[] { new PixelRect(10, 10, 1, 1) }).ShouldBe([0]);

        detector.MarkAllLossy();
        detector.PendingRefinement.ShouldBe(detector.TileCount);
        List<int> nearest = detector.RefinementCandidates(150, 100, 3);
        nearest.Count.ShouldBe(3);
        nearest.ShouldContain(1 * detector.Columns + 2);
        detector.MarkClean(nearest);
        detector.PendingRefinement.ShouldBe(detector.TileCount - 3);
    }

    [Fact]
    public void Tiles_changing_every_frame_are_motion_and_are_not_refined_until_they_settle()
    {
        const int W = 300, H = 200;
        byte[] frame = TextLike(W, H);
        var detector = new ChangeDetector();
        detector.Update(frame, W * 4, W, H);
        detector.Update(frame, W * 4, W, H);
        detector.Update(frame, W * 4, W, H);
        detector.MotionTileCount.ShouldBe(0);

        // A "video window" covering tile (2,1) changes on every frame.
        int video = 1 * detector.Columns + 2;
        for (int i = 0; i < 3; i++)
        {
            frame[(100 * W + 150) * 4] = (byte)i;
            List<int> changed = detector.Update(frame, W * 4, W, H);
            changed.ShouldBe([video]);
        }

        detector.IsMotion(video).ShouldBeTrue();
        detector.AnyMotion([video]).ShouldBeTrue();
        detector.MotionTileCount.ShouldBe(1);

        // A single edit elsewhere is not motion.
        frame[(10 * W + 10) * 4] ^= 0xFF;
        detector.Update(frame, W * 4, W, H).ShouldBe([0]);
        detector.IsMotion(0).ShouldBeFalse();

        detector.MarkAllLossy();
        detector.RefinementCandidates(150, 100, 100).ShouldNotContain(video);

        for (int i = 0; i < 12; i++)
        {
            detector.Idle();
        }

        detector.IsMotion(video).ShouldBeFalse();
        detector.MotionTileCount.ShouldBe(0);
        detector.RefinementCandidates(150, 100, 100).ShouldContain(video);
    }

    [Fact]
    public void Compositor_applies_video_then_tiles_and_rejects_size_mismatch()
    {
        const int W = 130, H = 70;
        byte[] truth = TextLike(W, H);
        byte[] blurry = new byte[truth.Length];
        Array.Fill(blurry, (byte)0x80);
        var compositor = new FrameCompositor();
        compositor.ApplyVideo(new DecodedFrame { Width = W, Height = H, Format = PixelFormat.Bgra32, Cpu = blurry, Stride = W * 4 });
        compositor.Current.Cpu.ToArray().ShouldBe(blurry);

        var detector = new ChangeDetector();
        var update = new TileUpdate { Width = W, Height = H, TileSize = 64 };
        byte[] encoded = new byte[TileCodec.MaxEncodedLength(64, 64)];
        foreach (int t in detector.Update(truth, W * 4, W, H))
        {
            (int x, int y, int w, int h) = detector.TileRect(t);
            int n = TileCodec.Encode(truth.AsSpan((y * W + x) * 4), W * 4, w, h, encoded);
            update.Tiles.Add(new Tile { Col = (uint)(x / 64), Row = (uint)(y / 64), W = (uint)w, H = (uint)h, Data = ByteString.CopyFrom(encoded, 0, n) });
        }

        compositor.ApplyTiles(update).ShouldBeTrue();
        compositor.Current.Cpu.ToArray().ShouldBe(truth);
        compositor.ApplyTiles(new TileUpdate { Width = W + 1, Height = H, TileSize = 64 }).ShouldBeFalse();
    }
}
