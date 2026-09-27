namespace DeskPair.Core.Transport.Udp.Fec;

/// <summary>How a frame's shards are grouped into FEC blocks and how much parity each block carries.</summary>
public readonly record struct FecPlan(int Blocks, int DataPerBlock, int ParityPerBlock)
{
    public int ShardsPerBlock => DataPerBlock + ParityPerBlock;
}

/// <summary>
/// Chooses parity from the receiver's measured loss: for each block the smallest m such that the chance of
/// losing more than m of the k + m shards (binomial, with a safety margin on the loss estimate) is below a
/// target, clamped so a clean link pays little and a bad one never more than half. Small frames need a
/// proportionally larger cushion than large ones, which a fixed ratio would get wrong in both directions.
/// </summary>
public sealed class FecPlanner
{
    public const int MaxDataPerBlock = 128;
    public const double MinRatio = 0.10;
    public const double MaxRatio = 0.5;
    public const double DeltaTarget = 1e-3;
    public const double KeyframeTarget = 1e-4;

    private double _loss;

    /// <summary>Smoothed packet-loss fraction reported by the receiver (before FEC).</summary>
    public double Loss => _loss;

    /// <summary>Loss assumed when sizing parity: the estimate with headroom, never below 1%.</summary>
    public double AssumedLoss => Math.Clamp(1.5 * _loss + 0.01, 0.01, 0.5);

    public void ReportLoss(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        _loss = _loss == 0 ? fraction : 0.7 * _loss + 0.3 * fraction;
    }

    public FecPlan Plan(int shardCount, bool keyframe)
    {
        shardCount = Math.Max(1, shardCount);
        int blocks = (shardCount + MaxDataPerBlock - 1) / MaxDataPerBlock;
        int k = (shardCount + blocks - 1) / blocks;
        return new FecPlan(blocks, k, ParityFor(k, keyframe));
    }

    /// <summary>Parity shards for a block of <paramref name="k"/> data shards.</summary>
    public int ParityFor(int k, bool keyframe)
    {
        double p = AssumedLoss;
        double target = keyframe ? KeyframeTarget : DeltaTarget;
        int min = Math.Max(1, (int)Math.Ceiling(k * MinRatio));
        int max = Math.Max(min, Math.Min((int)Math.Ceiling(k * MaxRatio), ReedSolomon.MaxShards - k));
        for (int m = min; m <= max; m++)
        {
            if (TailProbability(k + m, m, p) < target)
            {
                return m;
            }
        }

        return max;
    }

    /// <summary>P(X > m) for X ~ Binomial(n, p).</summary>
    internal static double TailProbability(int n, int m, double p)
    {
        double q = 1 - p;
        double term = Math.Pow(q, n); // P(X = 0)
        double cumulative = term;
        for (int x = 1; x <= m; x++)
        {
            term *= (double)(n - x + 1) / x * p / q;
            cumulative += term;
        }

        return Math.Max(0, 1 - cumulative);
    }
}
