namespace DeskPair.Relay.Core;

/// <summary>
/// The relay's bandwidth ceiling, as a token bucket every session's copy loop draws from before it sends.
///
/// <see cref="RelayOptions.MaxBitrateKbps"/> was a number the rendezvous read to spread load and nothing
/// on this machine enforced: a busy relay saturated its link, every session's QoS collapsed together and
/// the stats endpoint went on reporting a comfortable session count. Now a byte that would push the relay
/// over its ceiling waits its turn, and the peers see the delay as latency their congestion control backs
/// off from. One bucket for the whole relay rather than one per session, because the ceiling is the
/// link's.
/// </summary>
public sealed class RelayThrottle
{
    private readonly TimeProvider _time;
    private readonly double _bytesPerSecond;
    private readonly double _capacity;
    private readonly object _lock = new();
    private double _tokens;
    private long _lastRefill;

    /// <param name="maxBitrateKbps">0 means unmetered.</param>
    /// <param name="time">The clock the bucket refills by; a fake one in tests.</param>
    public RelayThrottle(int maxBitrateKbps, TimeProvider time)
    {
        _time = time;
        _bytesPerSecond = maxBitrateKbps > 0 ? maxBitrateKbps * 1000.0 / 8 : 0;

        // A quarter second of credit, and never less than one full copy buffer, so a single send can
        // always go through in one piece and short bursts are not shaped into a trickle.
        _capacity = Math.Max(_bytesPerSecond / 4, 64 * 1024);
        _tokens = _capacity;
        _lastRefill = time.GetTimestamp();
    }

    public bool IsUnmetered => _bytesPerSecond <= 0;

    /// <summary>Returns once <paramref name="bytes"/> may be sent; immediately when the relay is unmetered.</summary>
    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        if (IsUnmetered || bytes <= 0)
        {
            return;
        }

        while (true)
        {
            TimeSpan wait;
            lock (_lock)
            {
                Refill();
                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }

                // A send larger than the bucket is let through once the bucket is full: it can never be
                // paid for in one go, and holding it forever would be a stall, not a limit.
                if (bytes >= _capacity && _tokens >= _capacity)
                {
                    _tokens = 0;
                    return;
                }

                double needed = Math.Min(bytes, _capacity) - _tokens;
                wait = TimeSpan.FromSeconds(needed / _bytesPerSecond);
            }

            await Task.Delay(wait < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : wait, _time, ct).ConfigureAwait(false);
        }
    }

    private void Refill()
    {
        long now = _time.GetTimestamp();
        double elapsed = _time.GetElapsedTime(_lastRefill, now).TotalSeconds;
        _lastRefill = now;
        _tokens = Math.Min(_capacity, _tokens + elapsed * _bytesPerSecond);
    }
}
