using System.Collections.Concurrent;
using System.Net;

namespace DeskPair.Core.Session.Host.Auth;

/// <summary>
/// Per-source brute-force protection.
///
/// Three limits, and the longest wait wins. Consecutive failures from one source back off exponentially
/// (2, 4, 8, 16 seconds, then <see cref="MaxBackoff"/>), so a guess costs more each time it is wrong and
/// a person who mistyped once barely notices. On top of that, a short window with a small budget and a
/// long window with a larger one, for a source that spreads its guesses out. IPv6 sources are bucketed
/// by /64, since a /64 is what one subscriber usually holds.
///
/// The backoff is what lets the temporary password stop rotating on a single source's failures: a
/// password that moved every ten wrong guesses could be moved by anyone on the internet, under the
/// person reading it out; a source that has to wait half a minute per guess cannot get anywhere against
/// thirty bits either way.
/// </summary>
public sealed class LoginFailureTracker
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public LoginFailureTracker(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public int ShortWindowLimit { get; init; } = 6;
    public TimeSpan ShortWindow { get; init; } = TimeSpan.FromSeconds(60);
    public int LongWindowLimit { get; init; } = 30;
    public TimeSpan LongWindow { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Where the exponential backoff stops growing.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);

    private sealed class Bucket
    {
        public readonly List<DateTimeOffset> Failures = [];
        public int Consecutive;
        public DateTimeOffset BlockedUntil;
    }

    /// <summary>Returns how long the source must wait, or <see cref="TimeSpan.Zero"/> when an attempt is allowed.</summary>
    public TimeSpan CheckBlocked(IPAddress source)
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (!_buckets.TryGetValue(BucketOf(source), out Bucket? b))
        {
            return TimeSpan.Zero;
        }

        lock (b)
        {
            return b.BlockedUntil > now ? b.BlockedUntil - now : TimeSpan.Zero;
        }
    }

    public void RecordFailure(IPAddress source)
    {
        DateTimeOffset now = _time.GetUtcNow();
        Bucket b = _buckets.GetOrAdd(BucketOf(source), _ => new Bucket());
        lock (b)
        {
            b.Failures.Add(now);
            b.Failures.RemoveAll(t => now - t > LongWindow);
            b.Consecutive++;
            int recent = b.Failures.Count(t => now - t <= ShortWindow);

            DateTimeOffset until = now + Backoff(b.Consecutive);
            if (b.Failures.Count >= LongWindowLimit)
            {
                until = Later(until, now + LongWindow);
            }
            else if (recent >= ShortWindowLimit)
            {
                until = Later(until, now + ShortWindow);
            }

            b.BlockedUntil = Later(b.BlockedUntil, until);
        }
    }

    public void RecordSuccess(IPAddress source) => _buckets.TryRemove(BucketOf(source), out _);

    /// <summary>The wait after the n-th consecutive failure: 2^n seconds, capped.</summary>
    public TimeSpan Backoff(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return TimeSpan.Zero;
        }

        double seconds = Math.Pow(2, Math.Min(consecutiveFailures, 30));
        return seconds >= MaxBackoff.TotalSeconds ? MaxBackoff : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// The key one address counts under: the address itself for IPv4, the /64 for IPv6. Shared with the
    /// password store, so "how many sources are guessing" and "how long must this source wait" agree on
    /// what a source is.
    /// </summary>
    public static string BucketOf(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            byte[] bytes = address.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return new IPAddress(bytes).ToString();
        }

        return address.ToString();
    }

    private static DateTimeOffset Later(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
