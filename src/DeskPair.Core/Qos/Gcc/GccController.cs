namespace DeskPair.Core.Qos.Gcc;

/// <summary>
/// Google Congestion Control, sender side: packet arrival reports drive a delay-based estimate (inter-arrival
/// grouping, trendline overuse detector, AIMD) and a loss-based cap; the target is the lower of the two.
/// Follows the structure of draft-ietf-rmcat-gcc and libwebrtc/Pion. Pure logic, driven by report timestamps.
/// </summary>
public sealed class GccController
{
    public const double DefaultStartBps = 6_500_000;
    public const double DefaultMinBps = 1_000_000;

    private readonly InterArrival _interArrival = new();
    private readonly TrendlineEstimator _trendline = new();
    private readonly AckedBitrateEstimator _acked = new();
    private readonly AimdRateControl _aimd;
    private readonly LossBasedBwe _loss;
    private double _maxBps;

    public GccController(double startBps = DefaultStartBps, double minBps = DefaultMinBps, double maxBps = 150_000_000)
    {
        _maxBps = maxBps;
        _aimd = new AimdRateControl(Math.Min(startBps, maxBps), minBps, maxBps);
        _loss = new LossBasedBwe(startBps, maxBps);
        TargetBps = Math.Clamp(startBps, minBps, Math.Max(minBps, maxBps));
    }

    public double TargetBps { get; private set; }

    public BandwidthUsage State => _trendline.State;

    public double? AckedBps => _acked.BitrateBps;

    public double LossFraction => _loss.LossFraction;

    public double DelayBasedBps => _aimd.TargetBps;

    public double LossBasedBps => _loss.EstimateBps;

    public long Reports { get; private set; }

    /// <summary>Probe results that raised the estimate (diagnostics).</summary>
    public long ProbesApplied { get; private set; }

    /// <summary>The highest a probe result has raised the estimate to (diagnostics).</summary>
    public double ProbedToBps { get; private set; }

    /// <summary>
    /// A probe cluster measured this capacity. Normally it only raises the estimate (a probe measuring less than we
    /// already send is usually noise). When the link could not carry the burst (<paramref name="saturated"/>), the
    /// measurement is what it really delivers and may lower the estimate too, which saves a slow link from seconds of
    /// overshoot before the detectors catch up; such a decrease never goes below the rate already being delivered.
    /// </summary>
    public double OnProbeResult(double bps, bool saturated = false)
    {
        double capped = Math.Min(bps, _maxBps);
        if (!saturated)
        {
            // The burst arrived whole, so whatever loss the stream saw earlier is not a standing property of the path.
            _loss.RaiseTo(capped);
        }

        if (capped > _aimd.TargetBps)
        {
            _aimd.SetEstimate(capped);
            ProbesApplied++;
            ProbedToBps = Math.Max(ProbedToBps, capped);
        }
        else if (saturated)
        {
            _aimd.SetEstimate(Math.Max(capped, AckedBps ?? 0));
            ProbesApplied++;
        }

        TargetBps = Math.Clamp(Math.Min(_aimd.TargetBps, _loss.EstimateBps), _aimd.MinBps, Math.Max(_aimd.MinBps, _maxBps));
        return TargetBps;
    }

    /// <summary>Upper bound (resolution and quality preference); lowering it clamps the current target.</summary>
    public double MaxBps
    {
        get => _maxBps;
        set
        {
            _maxBps = value;
            _aimd.MaxBps = value;
            _loss.MaxBps = value;
            TargetBps = Math.Clamp(TargetBps, _aimd.MinBps, Math.Max(_aimd.MinBps, value));
        }
    }

    /// <summary>
    /// Processes one arrival report. <paramref name="received"/> must be sorted by send time; <paramref name="lost"/>
    /// counts packets in the report's range that never arrived.
    /// </summary>
    public double OnFeedback(IReadOnlyList<PacketResult> received, int lost, long nowUs)
    {
        Reports++;
        for (int i = 0; i < received.Count; i++)
        {
            PacketResult p = received[i];
            _acked.Add(p.ArrivalUs, p.SizeBytes);
            if (_interArrival.TryAdd(in p, out double sendDelta, out double arrivalDelta, out long groupArrival))
            {
                _trendline.Update(arrivalDelta, sendDelta, groupArrival);
            }
        }

        double delayBased = _aimd.Update(_trendline.State, _acked.BitrateBps, nowUs);
        _loss.Update(lost, received.Count, nowUs, TargetBps, _acked.BitrateBps);
        TargetBps = Math.Clamp(Math.Min(delayBased, _loss.EstimateBps), _aimd.MinBps, Math.Max(_aimd.MinBps, _maxBps));
        return TargetBps;
    }
}
