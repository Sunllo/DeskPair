namespace DeskPair.Core.Qos.Gcc;

/// <summary>
/// GCC's delay-based overuse detector: accumulates the per-group delay variation, smooths it, fits a line over the
/// last 20 groups and compares the (gain-scaled) slope with an adaptive threshold. A rising slope means a queue is
/// building somewhere on the path, well before packets are lost.
/// </summary>
public sealed class TrendlineEstimator
{
    public const int WindowSize = 20;
    private const double Smoothing = 0.9;
    private const double ThresholdGain = 4.0;
    private const double KUp = 0.0087;
    private const double KDown = 0.039;
    private const double OverusingTimeThresholdMs = 10;
    private const double MaxAdaptOffsetMs = 15;

    private readonly Queue<(double X, double Y)> _window = new();
    private double _accumulated;
    private double _smoothed;
    private long _firstArrivalUs = -1;
    private int _numDeltas;
    private double _threshold = 12.5;
    private long _lastThresholdUpdateUs = -1;
    private double _timeOverUsingMs = -1;
    private int _overuseCounter;
    private double _previousTrend;

    public BandwidthUsage State { get; private set; } = BandwidthUsage.Normal;

    public double Threshold => _threshold;

    public double LastModifiedTrend { get; private set; }

    public void Update(double arrivalDeltaMs, double sendDeltaMs, long arrivalUs)
    {
        double delta = arrivalDeltaMs - sendDeltaMs;
        _numDeltas = Math.Min(_numDeltas + 1, 1000);
        if (_firstArrivalUs < 0)
        {
            _firstArrivalUs = arrivalUs;
        }

        _accumulated += delta;
        _smoothed = Smoothing * _smoothed + (1 - Smoothing) * _accumulated;
        _window.Enqueue(((arrivalUs - _firstArrivalUs) / 1000.0, _smoothed));
        if (_window.Count > WindowSize)
        {
            _window.Dequeue();
        }

        double trend = _previousTrend;
        if (_window.Count == WindowSize && Slope() is { } slope)
        {
            trend = slope;
        }

        Detect(trend, sendDeltaMs, arrivalUs);
    }

    private double? Slope()
    {
        double sumX = 0, sumY = 0;
        foreach ((double x, double y) in _window)
        {
            sumX += x;
            sumY += y;
        }

        double meanX = sumX / _window.Count, meanY = sumY / _window.Count;
        double numerator = 0, denominator = 0;
        foreach ((double x, double y) in _window)
        {
            numerator += (x - meanX) * (y - meanY);
            denominator += (x - meanX) * (x - meanX);
        }

        return denominator == 0 ? null : numerator / denominator;
    }

    private void Detect(double trend, double sendDeltaMs, long nowUs)
    {
        if (_numDeltas < 2)
        {
            State = BandwidthUsage.Normal;
            return;
        }

        double modified = Math.Min(_numDeltas, 60) * trend * ThresholdGain;
        LastModifiedTrend = modified;
        if (modified > _threshold)
        {
            _timeOverUsingMs = _timeOverUsingMs < 0 ? sendDeltaMs / 2 : _timeOverUsingMs + sendDeltaMs;
            _overuseCounter++;
            if (_timeOverUsingMs > OverusingTimeThresholdMs && _overuseCounter > 1 && trend >= _previousTrend)
            {
                _timeOverUsingMs = 0;
                _overuseCounter = 0;
                State = BandwidthUsage.Overusing;
            }
        }
        else if (modified < -_threshold)
        {
            _timeOverUsingMs = -1;
            _overuseCounter = 0;
            State = BandwidthUsage.Underusing;
        }
        else
        {
            _timeOverUsingMs = -1;
            _overuseCounter = 0;
            State = BandwidthUsage.Normal;
        }

        _previousTrend = trend;
        UpdateThreshold(modified, nowUs);
    }

    private void UpdateThreshold(double modified, long nowUs)
    {
        if (_lastThresholdUpdateUs < 0)
        {
            _lastThresholdUpdateUs = nowUs;
        }

        if (Math.Abs(modified) > _threshold + MaxAdaptOffsetMs)
        {
            // A spike far above the threshold is one delayed burst; do not let it drag the threshold up.
            _lastThresholdUpdateUs = nowUs;
            return;
        }

        double k = Math.Abs(modified) < _threshold ? KDown : KUp;
        double dtMs = Math.Min((nowUs - _lastThresholdUpdateUs) / 1000.0, 100);
        _threshold = Math.Clamp(_threshold + k * (Math.Abs(modified) - _threshold) * dtMs, 6, 600);
        _lastThresholdUpdateUs = nowUs;
    }
}
