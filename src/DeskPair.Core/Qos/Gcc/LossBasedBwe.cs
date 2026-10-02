namespace DeskPair.Core.Qos.Gcc;

/// <summary>
/// GCC's loss-based limit: below 4 % loss it may grow (8 %/s), between 4 and 20 % it holds, above 20 % it drops to
/// rate × (1 − loss/2). It only ever caps the delay-based estimate. The 4 % / 20 % thresholds are twice the usual
/// GCC 2 % / 10 %, so the estimate is half as quick to stop growing or to be cut -- FEC already repairs the packets
/// these fractions count, so on a direct path this keeps the bitrate up instead of collapsing on a little loss.
/// </summary>
public sealed class LossBasedBwe
{
    private const int MinPacketsPerSample = 100; // smaller samples make 3% random loss read as >10% now and then
    private const long MinDecreaseIntervalUs = 300_000;

    private int _lost;
    private int _received;
    private long _lastUpdateUs = -1;
    private long _lastDecreaseUs = -1;

    public LossBasedBwe(double startBps, double maxBps)
    {
        MaxBps = maxBps;
        EstimateBps = maxBps;
        _ = startBps;
    }

    public double EstimateBps { get; private set; }

    public double MaxBps { get; set; }

    public double LossFraction { get; private set; }

    /// <summary>
    /// Evidence that the path has no standing loss (a probe burst that arrived whole): the limit may jump straight
    /// to what the probe measured instead of crawling back at 8 %/s after one burst of loss at start-up.
    /// </summary>
    public void RaiseTo(double bps)
    {
        EstimateBps = Math.Min(MaxBps, Math.Max(EstimateBps, bps));
        _lost = _received = 0;
    }

    /// <summary>
    /// Adds a report's packet counts. <paramref name="deliveredBps"/> is the measured throughput (when known): a
    /// decrease starts from what actually got through, so the burst loss that follows an overshoot is not
    /// compounded on top of the delay-based decrease that already happened.
    /// </summary>
    public void Update(int lost, int received, long nowUs, double currentTargetBps, double? deliveredBps = null)
    {
        _lost += lost;
        _received += received;
        if (_lost + _received < MinPacketsPerSample)
        {
            return;
        }

        LossFraction = (double)_lost / (_lost + _received);
        _lost = _received = 0;
        double dt = _lastUpdateUs < 0 ? 0 : Math.Min((nowUs - _lastUpdateUs) / 1_000_000.0, 1.0);
        _lastUpdateUs = nowUs;

        if (LossFraction < 0.04)
        {
            EstimateBps = Math.Min(MaxBps, EstimateBps * Math.Pow(1.08, dt));
        }
        else if (LossFraction > 0.20)
        {
            if (_lastDecreaseUs < 0 || nowUs - _lastDecreaseUs >= MinDecreaseIntervalUs)
            {
                // Decrease from what is actually being sent, not from a limit that may sit far above it.
                double basis = deliveredBps is { } delivered ? Math.Max(delivered, 0.5 * currentTargetBps) : currentTargetBps;
                EstimateBps = Math.Min(EstimateBps, basis) * (1 - 0.5 * LossFraction);
                _lastDecreaseUs = nowUs;
            }
        }
        else
        {
            // Hold, but do not keep a cap far above the current target: the next increase starts from here.
            EstimateBps = Math.Min(EstimateBps, Math.Max(currentTargetBps, EstimateBps));
        }

        EstimateBps = Math.Min(EstimateBps, MaxBps);
    }
}
