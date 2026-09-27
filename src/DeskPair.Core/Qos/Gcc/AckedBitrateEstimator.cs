namespace DeskPair.Core.Qos.Gcc;

/// <summary>Throughput actually delivered, from acknowledged packet sizes over a 500 ms window of arrival time.</summary>
public sealed class AckedBitrateEstimator
{
    public const long WindowUs = 500_000;

    private readonly Queue<(long ArrivalUs, int Size)> _packets = new();
    private long _bytes;
    private long _firstArrivalUs = -1;
    private long _latestArrivalUs;

    public void Add(long arrivalUs, int sizeBytes)
    {
        if (_firstArrivalUs < 0)
        {
            _firstArrivalUs = arrivalUs;
        }

        _latestArrivalUs = Math.Max(_latestArrivalUs, arrivalUs);
        _packets.Enqueue((arrivalUs, sizeBytes));
        _bytes += sizeBytes;
        while (_packets.Count > 0 && _latestArrivalUs - _packets.Peek().ArrivalUs > WindowUs)
        {
            _bytes -= _packets.Dequeue().Size;
        }
    }

    /// <summary>Bits per second over the window, or null until half a second of arrivals has been seen.</summary>
    public double? BitrateBps => _firstArrivalUs < 0 || _latestArrivalUs - _firstArrivalUs < WindowUs
        ? null
        : _bytes * 8.0 * 1_000_000 / WindowUs;
}
