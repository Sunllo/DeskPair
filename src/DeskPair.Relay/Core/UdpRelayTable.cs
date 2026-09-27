using System.Net;

namespace DeskPair.Relay.Core;

public enum UdpBindResult
{
    Pending,
    Paired,
    Rejected,
}

/// <summary>
/// Pure pairing logic for the UDP relay: the first endpoint presenting a token waits, the second one with the
/// same token becomes its partner, and from then on datagrams from either are forwarded to the other.
/// Time-driven expiry is explicit (<see cref="Sweep"/>) so it can be unit tested with a fake clock.
/// </summary>
public sealed class UdpRelayTable
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _pairingTimeout;
    private readonly TimeSpan _idleTimeout;
    private readonly int _maxPairs;
    private readonly object _lock = new();
    private readonly Dictionary<TokenKey, Pending> _pending = new();
    private readonly Dictionary<IPEndPoint, Pair> _pairs = new();
    private readonly HashSet<TokenKey> _pairedTokens = new();

    public UdpRelayTable(TimeProvider time, TimeSpan pairingTimeout, TimeSpan idleTimeout, int maxPairs)
    {
        _time = time;
        _pairingTimeout = pairingTimeout;
        _idleTimeout = idleTimeout;
        _maxPairs = maxPairs;
    }

    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    public int PairCount
    {
        get
        {
            lock (_lock)
            {
                return _pairs.Count / 2;
            }
        }
    }

    /// <summary>Registers a Bind; on <see cref="UdpBindResult.Paired"/> <paramref name="peer"/> is the partner endpoint.</summary>
    public UdpBindResult Bind(ReadOnlySpan<byte> token, IPEndPoint from, out IPEndPoint? peer)
    {
        peer = null;
        var key = new TokenKey(token);
        DateTimeOffset now = _time.GetUtcNow();
        lock (_lock)
        {
            if (_pairs.TryGetValue(from, out Pair? existing))
            {
                // Re-bind from an already paired endpoint (lost ack): answer again without changing anything.
                if (existing.Token == key)
                {
                    peer = existing.Peer;
                    existing.LastSeen = now;
                    return UdpBindResult.Paired;
                }

                return UdpBindResult.Rejected;
            }

            if (_pairedTokens.Contains(key))
            {
                return UdpBindResult.Rejected; // a third endpoint presenting a token that already paired
            }

            if (_pending.TryGetValue(key, out Pending? waiting))
            {
                if (waiting.From.Equals(from))
                {
                    waiting.Since = now;
                    return UdpBindResult.Pending;
                }

                if (_pairs.Count / 2 >= _maxPairs)
                {
                    return UdpBindResult.Rejected;
                }

                _pending.Remove(key);
                _pairedTokens.Add(key);
                _pairs[from] = new Pair(key, waiting.From) { LastSeen = now };
                _pairs[waiting.From] = new Pair(key, from) { LastSeen = now };
                peer = waiting.From;
                return UdpBindResult.Paired;
            }

            if (_pending.Count >= _maxPairs * 2)
            {
                return UdpBindResult.Rejected;
            }

            _pending[key] = new Pending(from) { Since = now };
            return UdpBindResult.Pending;
        }
    }

    /// <summary>Partner of a paired endpoint (touching its idle timer); null for unknown senders.</summary>
    public IPEndPoint? PeerOf(IPEndPoint from)
    {
        lock (_lock)
        {
            if (_pairs.TryGetValue(from, out Pair? pair))
            {
                pair.LastSeen = _time.GetUtcNow();
                return pair.Peer;
            }

            return null;
        }
    }

    /// <summary>Drops unpaired binds older than the pairing timeout and pairs idle longer than the idle timeout.</summary>
    public int Sweep()
    {
        DateTimeOffset now = _time.GetUtcNow();
        int removed = 0;
        lock (_lock)
        {
            foreach (KeyValuePair<TokenKey, Pending> p in _pending.Where(p => now - p.Value.Since > _pairingTimeout).ToList())
            {
                _pending.Remove(p.Key);
                removed++;
            }

            foreach (KeyValuePair<IPEndPoint, Pair> p in _pairs.Where(p => now - p.Value.LastSeen > _idleTimeout).ToList())
            {
                _pairs.Remove(p.Key);
                _pairedTokens.Remove(p.Value.Token);
                removed++;
            }
        }

        return removed;
    }

    private sealed class Pending(IPEndPoint from)
    {
        public IPEndPoint From { get; } = from;
        public DateTimeOffset Since { get; set; }
    }

    private sealed class Pair(TokenKey token, IPEndPoint peer)
    {
        public TokenKey Token { get; } = token;
        public IPEndPoint Peer { get; } = peer;
        public DateTimeOffset LastSeen { get; set; }
    }

    private readonly struct TokenKey : IEquatable<TokenKey>
    {
        private readonly ulong _a;
        private readonly ulong _b;

        public TokenKey(ReadOnlySpan<byte> token)
        {
            _a = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(token);
            _b = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(token[8..]);
        }

        public bool Equals(TokenKey other) => _a == other._a && _b == other._b;

        public override bool Equals(object? obj) => obj is TokenKey k && Equals(k);

        public override int GetHashCode() => HashCode.Combine(_a, _b);

        public static bool operator ==(TokenKey left, TokenKey right) => left.Equals(right);

        public static bool operator !=(TokenKey left, TokenKey right) => !left.Equals(right);
    }
}
