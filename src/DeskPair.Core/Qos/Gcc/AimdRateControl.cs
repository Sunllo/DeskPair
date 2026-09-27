namespace DeskPair.Core.Qos.Gcc;

/// <summary>
/// GCC's rate controller: grows the estimate multiplicatively (8 %/s) while the path is fine, additively once it is
/// close to the last known capacity, drops to 85 % of the measured throughput on overuse, and holds while the queue
/// drains. The estimate never runs far ahead of what is actually being delivered.
/// </summary>
public sealed class AimdRateControl
{
    private const double Beta = 0.85;
    private const double MultiplicativeRatePerSecond = 1.08;
    private const long MinDecreaseIntervalUs = 200_000;

    private RateState _state = RateState.Hold;
    private long _lastUpdateUs = -1;
    private long _lastDecreaseUs = -1;
    private double? _linkCapacity;
    private double _linkCapacityVariance = 0.4;

    public AimdRateControl(double startBps, double minBps, double maxBps)
    {
        TargetBps = startBps;
        MinBps = minBps;
        MaxBps = maxBps;
    }

    private enum RateState
    {
        Hold,
        Increase,
        Decrease,
    }

    public double TargetBps { get; private set; }

    public double MinBps { get; set; }

    public double MaxBps { get; set; }

    /// <summary>Smoothed throughput at recent overuse events; null when unknown or invalidated.</summary>
    public double? LinkCapacityBps => _linkCapacity;

    /// <summary>
    /// Adopts a measured capacity (a successful probe) as the new estimate. The old link capacity is forgotten so
    /// growth from here is multiplicative again.
    /// </summary>
    public void SetEstimate(double bps)
    {
        TargetBps = Math.Clamp(bps, MinBps, Math.Max(MinBps, MaxBps));
        _linkCapacity = null;
        _state = RateState.Hold;
    }

    public double Update(BandwidthUsage usage, double? ackedBps, long nowUs)
    {
        _state = usage switch
        {
            BandwidthUsage.Overusing => RateState.Decrease,
            BandwidthUsage.Underusing => RateState.Hold,
            _ => RateState.Increase,
        };

        double dt = _lastUpdateUs < 0 ? 0 : Math.Min((nowUs - _lastUpdateUs) / 1_000_000.0, 1.0);
        _lastUpdateUs = nowUs;

        switch (_state)
        {
            case RateState.Increase:
                if (_linkCapacity is { } capacity && ackedBps is { } a
                    && a > capacity * (1 + 3 * Math.Sqrt(_linkCapacityVariance)))
                {
                    _linkCapacity = null; // the path got faster than we thought; probe multiplicatively again
                }

                double increased = _linkCapacity is not null
                    ? TargetBps + Math.Max(10_000, 0.05 * TargetBps) * dt
                    : TargetBps * Math.Pow(MultiplicativeRatePerSecond, dt);

                // Do not grow past 1.5x what is actually delivered (an idle desktop proves nothing about the link),
                // but do not shrink either: the estimate stays ready for the next burst of motion.
                if (ackedBps is { } acked)
                {
                    increased = Math.Min(increased, Math.Max(TargetBps, 1.5 * acked + 10_000));
                }

                TargetBps = increased;
                break;
            case RateState.Decrease:
                if (_lastDecreaseUs < 0 || nowUs - _lastDecreaseUs >= MinDecreaseIntervalUs)
                {
                    // Normally 0.85x the delivered rate. A mostly idle sender (throughput far below the target) did not
                    // cause the queue; halve at most per event rather than collapsing to the idle rate.
                    double basis = ackedBps is { } delivered ? Math.Max(delivered, 0.5 * TargetBps) : TargetBps;
                    TargetBps = Math.Min(TargetBps, Beta * basis);
                    if (ackedBps is { } sample)
                    {
                        UpdateLinkCapacity(sample);
                    }

                    _lastDecreaseUs = nowUs;
                }

                break;
        }

        TargetBps = Math.Clamp(TargetBps, MinBps, Math.Max(MinBps, MaxBps));
        return TargetBps;
    }

    private void UpdateLinkCapacity(double sample)
    {
        const double Alpha = 0.05;
        if (_linkCapacity is not { } capacity)
        {
            _linkCapacity = sample;
            return;
        }

        double error = capacity - sample;
        double updated = (1 - Alpha) * capacity + Alpha * sample;
        _linkCapacity = updated;
        double norm = Math.Max(updated, 1);
        _linkCapacityVariance = Math.Clamp((1 - Alpha) * _linkCapacityVariance + Alpha * error * error / (norm * norm), 0.4, 2.5);
    }
}
