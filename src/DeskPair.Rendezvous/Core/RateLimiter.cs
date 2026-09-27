using System.Collections.Concurrent;
using System.Net;

namespace DeskPair.Rendezvous.Core;

/// <summary>Sliding one-minute per-address counter; entries are pruned lazily.</summary>
public sealed class RateLimiter
{
    private readonly ConcurrentDictionary<IPAddress, Queue<long>> _hits = new();
    private readonly int _limitPerMinute;
    private readonly TimeProvider _time;
    private long _lastPrune;

    public RateLimiter(int limitPerMinute, TimeProvider time)
    {
        _limitPerMinute = limitPerMinute;
        _time = time;
        _lastPrune = time.GetTimestamp();
    }

    /// <summary>Records a hit and returns true when the address is still within its budget.</summary>
    public bool TryAcquire(IPAddress address)
    {
        if (_limitPerMinute <= 0)
        {
            return true;
        }

        long now = _time.GetTimestamp();
        long window = _time.TimestampFrequency * 60;
        Queue<long> q = _hits.GetOrAdd(address, _ => new Queue<long>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() > window)
            {
                q.Dequeue();
            }

            if (q.Count >= _limitPerMinute)
            {
                return false;
            }

            q.Enqueue(now);
        }

        if (now - Volatile.Read(ref _lastPrune) > window)
        {
            Volatile.Write(ref _lastPrune, now);
            foreach (KeyValuePair<IPAddress, Queue<long>> kv in _hits)
            {
                lock (kv.Value)
                {
                    if (kv.Value.Count == 0 || now - kv.Value.Peek() > window)
                    {
                        _hits.TryRemove(kv);
                    }
                }
            }
        }

        return true;
    }
}
