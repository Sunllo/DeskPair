using System.Diagnostics;
using DeskPair.Core.Video;
using Xunit.Abstractions;

namespace DeskPair.Core.Tests;

public class TileHashTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(64, 64)]
    [InlineData(40, 64)]
    [InlineData(64, 17)]
    [InlineData(3, 5)]
    public void Any_single_byte_change_changes_the_hash(int w, int h)
    {
        var rng = new Random(w * 7 + h);
        int stride = w * 4 + 8;
        byte[] tile = new byte[stride * h];
        rng.NextBytes(tile);
        ulong baseline = TileHash.Compute(tile, stride, w, h);
        TileHash.Compute(tile, stride, w, h).ShouldBe(baseline);

        for (int trial = 0; trial < 200; trial++)
        {
            int row = rng.Next(h);
            int index = row * stride + rng.Next(w * 4);
            byte[] copy = (byte[])tile.Clone();
            copy[index] ^= (byte)(1 << rng.Next(8));
            TileHash.Compute(copy, stride, w, h).ShouldNotBe(baseline);
        }

        // Bytes in the stride padding must not influence the hash.
        byte[] padded = (byte[])tile.Clone();
        padded[w * 4 + 2] ^= 0xFF;
        TileHash.Compute(padded, stride, w, h).ShouldBe(baseline);
    }

    [Fact]
    public void Row_order_matters()
    {
        byte[] a = new byte[64 * 4 * 2];
        byte[] b = new byte[a.Length];
        var rng = new Random(3);
        rng.NextBytes(a);
        a.AsSpan(0, 256).CopyTo(b.AsSpan(256));
        a.AsSpan(256, 256).CopyTo(b.AsSpan(0));
        TileHash.Compute(a, 256, 64, 2).ShouldNotBe(TileHash.Compute(b, 256, 64, 2));
    }

    [Fact]
    public void Full_frame_hash_cost_is_reported()
    {
        const int W = 2560, H = 1440;
        byte[] frame = new byte[W * H * 4];
        new Random(1).NextBytes(frame);
        var detector = new ChangeDetector();
        detector.Update(frame, W * 4, W, H);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
        {
            detector.Update(frame, W * 4, W, H);
        }

        output.WriteLine($"{W}x{H}: {sw.Elapsed.TotalMilliseconds / 10:F2} ms per full-frame change scan ({detector.TileCount} tiles)");
    }
}
