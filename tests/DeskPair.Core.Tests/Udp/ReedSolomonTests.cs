using System.Diagnostics;
using DeskPair.Core.Transport.Udp.Fec;
using Xunit.Abstractions;

namespace DeskPair.Core.Tests.Udp;

public class ReedSolomonTests(ITestOutputHelper output)
{
    [Fact]
    public void Field_arithmetic_is_consistent()
    {
        for (int a = 1; a < 256; a++)
        {
            byte inv = GaloisField.Inverse((byte)a);
            GaloisField.Multiply((byte)a, inv).ShouldBe((byte)1);
            GaloisField.Divide((byte)a, (byte)a).ShouldBe((byte)1);
        }

        GaloisField.Multiply(0, 200).ShouldBe((byte)0);
        GaloisField.Multiply(3, 7).ShouldBe((byte)9); // (x+1)(x^2+x+1) = x^3+1
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 2)]
    [InlineData(40, 12)]
    [InlineData(128, 32)]
    public void Any_m_erasures_are_recovered(int k, int m)
    {
        var rng = new Random(k * 100 + m);
        const int Len = 1140;
        byte[][] data = new byte[k][];
        for (int i = 0; i < k; i++)
        {
            data[i] = new byte[Len];
            rng.NextBytes(data[i]);
        }

        byte[][] parity = new byte[m][];
        for (int i = 0; i < m; i++)
        {
            parity[i] = new byte[Len];
        }

        ReedSolomon.Encode(data.Select(d => (ReadOnlyMemory<byte>)d).ToArray(), parity.Select(p => (Memory<byte>)p).ToArray());

        for (int trial = 0; trial < 5; trial++)
        {
            // Erase exactly m shards, drawn from data and parity alike.
            int[] erased = Enumerable.Range(0, k + m).OrderBy(_ => rng.Next()).Take(m).ToArray();
            byte[][] shards = new byte[k + m][];
            bool[] present = new bool[k + m];
            for (int i = 0; i < k + m; i++)
            {
                bool lost = erased.Contains(i);
                present[i] = !lost;
                shards[i] = lost ? new byte[Len] : (byte[])(i < k ? data[i] : parity[i - k]).Clone();
            }

            ReedSolomon.Decode(shards.Select(s => (Memory<byte>)s).ToArray(), present, k).ShouldBeTrue();
            for (int i = 0; i < k; i++)
            {
                shards[i].ShouldBe(data[i]);
            }
        }

        // One erasure too many cannot be recovered.
        {
            byte[][] shards = new byte[k + m][];
            bool[] present = new bool[k + m];
            for (int i = 0; i < k + m; i++)
            {
                present[i] = i > m; // first m+1 missing
                shards[i] = present[i] ? (byte[])(i < k ? data[i] : parity[i - k]).Clone() : new byte[Len];
            }

            ReedSolomon.Decode(shards.Select(s => (Memory<byte>)s).ToArray(), present, k).ShouldBeFalse();
        }
    }

    [Fact]
    public void Parity_is_deterministic_and_nothing_to_do_when_all_data_is_present()
    {
        byte[][] data = [[1, 2, 3, 4], [5, 6, 7, 8]];
        byte[] p1 = new byte[4], p2 = new byte[4];
        ReedSolomon.Encode([data[0], data[1]], [p1]);
        ReedSolomon.Encode([data[0], data[1]], [p2]);
        p1.ShouldBe(p2);
        ReedSolomon.Decode([data[0], data[1], p1], [true, true, false], 2).ShouldBeTrue();
    }

    [Fact]
    public void Planner_scales_parity_with_loss()
    {
        var planner = new FecPlanner();
        FecPlan clean = planner.Plan(40, keyframe: false);
        clean.Blocks.ShouldBe(1);
        clean.DataPerBlock.ShouldBe(40);
        clean.ParityPerBlock.ShouldBeInRange((int)Math.Ceiling(40 * FecPlanner.MinRatio), 6);

        planner.ReportLoss(0.08);
        planner.ReportLoss(0.08);
        planner.ReportLoss(0.08);
        FecPlan lossy = planner.Plan(40, keyframe: true);
        lossy.ParityPerBlock.ShouldBeGreaterThan(clean.ParityPerBlock);
        lossy.ParityPerBlock.ShouldBeLessThanOrEqualTo((int)Math.Ceiling(40 * FecPlanner.MaxRatio));
        // The residual failure chance at the assumed loss is below the target for both frame kinds.
        FecPlanner.TailProbability(40 + lossy.ParityPerBlock, lossy.ParityPerBlock, planner.AssumedLoss).ShouldBeLessThan(FecPlanner.KeyframeTarget);
        FecPlanner.TailProbability(20, 5, 0.1).ShouldBeInRange(0.005, 0.02); // P(X > 5), X ~ B(20, 0.1) is about 1.1%
        // Small frames get a proportionally larger cushion than big ones.
        ((double)planner.ParityFor(10, false) / 10).ShouldBeGreaterThan((double)planner.ParityFor(128, false) / 128);

        FecPlan big = planner.Plan(300, keyframe: false);
        big.Blocks.ShouldBe(3);
        big.DataPerBlock.ShouldBe(100);
    }

    [Fact]
    public void Encoding_a_large_frame_is_cheap()
    {
        const int K = 128, M = 38, Len = 1140;
        var rng = new Random(9);
        byte[][] data = Enumerable.Range(0, K).Select(_ => { byte[] b = new byte[Len]; rng.NextBytes(b); return b; }).ToArray();
        byte[][] parity = Enumerable.Range(0, M).Select(_ => new byte[Len]).ToArray();
        ReadOnlyMemory<byte>[] dataSpans = data.Select(d => (ReadOnlyMemory<byte>)d).ToArray();
        Memory<byte>[] paritySpans = parity.Select(p => (Memory<byte>)p).ToArray();
        ReedSolomon.Encode(dataSpans, paritySpans);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
        {
            ReedSolomon.Encode(dataSpans, paritySpans);
        }

        output.WriteLine($"RS({K},{M}) x {Len} B: {sw.Elapsed.TotalMilliseconds / 10:F2} ms per block ({K * Len / 1024} KB)");
    }
}
