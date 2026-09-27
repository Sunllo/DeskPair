namespace DeskPair.Core.Video;

/// <summary>
/// Turns bursty frame arrivals into an even presentation cadence. Each arriving frame gets a due time:
/// the previous due time plus the estimated production interval, never earlier than now, and never so far
/// ahead that the buffer grows without bound. Pure timing logic (timestamps in <see cref="TimeProvider"/>
/// ticks) so it can be unit tested; the view owns the queue and the actual presentation.
/// </summary>
public sealed class FramePacer
{
    private readonly TimeProvider _time;
    private readonly long _minInterval;
    private readonly long _maxInterval;
    private long _lastArrival;
    private long _lastDue;
    private double _interval;

    public FramePacer(TimeProvider time, double initialFps = 60)
    {
        _time = time;
        _minInterval = time.TimestampFrequency / 240;
        _maxInterval = time.TimestampFrequency / 10;
        _interval = time.TimestampFrequency / initialFps;
    }

    /// <summary>Present frames at their production cadence (true) or as soon as they arrive (false).</summary>
    public bool Smooth { get; set; } = true;

    /// <summary>How far ahead of "now" a due time may run, in intervals; bounds the added latency and the queue depth.</summary>
    public double MaxLead { get; set; } = 1.5;

    /// <summary>Estimated production interval in ticks.</summary>
    public double IntervalTicks => _interval;

    public double EstimatedFps => _time.TimestampFrequency / _interval;

    /// <summary>Returns the presentation time for a frame arriving now.</summary>
    public long Schedule() => Schedule(_time.GetTimestamp());

    public long Schedule(long now)
    {
        long delta = now - _lastArrival;
        bool cadence = _lastArrival != 0 && delta > 0 && delta < _maxInterval;
        if (cadence)
        {
            _interval = Math.Clamp(0.85 * _interval + 0.15 * delta, _minInterval, _maxInterval);
        }

        _lastArrival = now;
        long due = now;
        if (Smooth && cadence)
        {
            due = Math.Max(now, _lastDue + (long)_interval);
            long lead = (long)(MaxLead * _interval);
            if (due - now > lead)
            {
                due = now + lead; // the estimate ran ahead of reality: catch up instead of stacking latency
            }
        }

        _lastDue = due;
        return due;
    }
}
