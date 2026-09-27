namespace DeskPair.Relay.Core;

public sealed class RelayStats
{
    /// <summary>
    /// How long a throughput sample covers.
    ///
    /// Long enough that one large frame does not read as a spike, short enough that a rendezvous polling
    /// every thirty seconds is looking at what is happening now rather than at a minute ago.
    /// </summary>
    private static readonly TimeSpan ThroughputWindow = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time;
    private readonly Lock _window = new();

    private long _activeSessions;
    private long _pendingPeers;
    private long _totalSessions;
    private long _totalBytes;
    private long _rejectedLimit;
    private long _pairingTimeouts;
    private long _protocolErrors;
    private long _ticketsRejected;
    private long _udpPairs;
    private long _udpPending;
    private long _udpPaired;
    private long _udpRejected;
    private long _udpBytes;

    // The current throughput window: when it opened, and what has flowed since. Rolled over on read or
    // write once it is older than ThroughputWindow, so an idle relay decays to zero rather than reporting
    // whatever it last managed.
    private long _windowStarted;
    private long _windowBytes;
    private long _lastRateBytesPerSecond;

    public RelayStats(TimeProvider time)
    {
        _time = time;
        _windowStarted = time.GetTimestamp();
    }

    public long ActiveSessions => Interlocked.Read(ref _activeSessions);
    public long PendingPeers => Interlocked.Read(ref _pendingPeers);
    public long TotalSessions => Interlocked.Read(ref _totalSessions);
    public long TotalBytes => Interlocked.Read(ref _totalBytes);
    public long RejectedLimit => Interlocked.Read(ref _rejectedLimit);
    public long PairingTimeouts => Interlocked.Read(ref _pairingTimeouts);
    public long ProtocolErrors => Interlocked.Read(ref _protocolErrors);
    public long TicketsRejected => Interlocked.Read(ref _ticketsRejected);

    internal void TicketRejected() => Interlocked.Increment(ref _ticketsRejected);

    internal void PendingInc() => Interlocked.Increment(ref _pendingPeers);
    internal void PendingDec() => Interlocked.Decrement(ref _pendingPeers);
    internal void SessionStarted() { Interlocked.Increment(ref _activeSessions); Interlocked.Increment(ref _totalSessions); }
    internal void SessionEnded() => Interlocked.Decrement(ref _activeSessions);
    internal void AddBytes(long n) => Interlocked.Add(ref _totalBytes, n);

    /// <summary>
    /// Bytes as they pass, rather than in one lump when a session ends.
    ///
    /// <see cref="AddBytes"/> is called once per session, at the end, so a relay carrying ten busy sessions
    /// for an hour reported nothing at all until they closed. That is fine for a lifetime total and useless
    /// for deciding where to send the next connection, which is what this is for.
    /// </summary>
    internal void Forwarded(long n)
    {
        lock (_window)
        {
            RollWindow();
            _windowBytes += n;
        }
    }

    /// <summary>What is flowing right now, for load-based relay selection.</summary>
    public long BytesPerSecond
    {
        get
        {
            lock (_window)
            {
                RollWindow();

                // Mid-window, the completed window's rate is a better answer than a partial count: a poll
                // one second into a new window would otherwise report a tenth of the real throughput.
                TimeSpan elapsed = _time.GetElapsedTime(_windowStarted);
                return elapsed < TimeSpan.FromSeconds(1)
                    ? _lastRateBytesPerSecond
                    : Math.Max(_lastRateBytesPerSecond, (long)(_windowBytes / elapsed.TotalSeconds));
            }
        }
    }

    private void RollWindow()
    {
        TimeSpan elapsed = _time.GetElapsedTime(_windowStarted);
        if (elapsed < ThroughputWindow)
        {
            return;
        }

        _lastRateBytesPerSecond = (long)(_windowBytes / elapsed.TotalSeconds);
        _windowBytes = 0;
        _windowStarted = _time.GetTimestamp();
    }
    internal void LimitRejected() => Interlocked.Increment(ref _rejectedLimit);
    internal void PairingTimedOut() => Interlocked.Increment(ref _pairingTimeouts);
    internal void ProtocolError() => Interlocked.Increment(ref _protocolErrors);
    internal void UdpPaired() => Interlocked.Increment(ref _udpPaired);
    internal void UdpRejected() => Interlocked.Increment(ref _udpRejected);
    internal void UdpForwarded(long n) => Interlocked.Add(ref _udpBytes, n);
    internal void UdpSetActive(int pairs, int pending)
    {
        Interlocked.Exchange(ref _udpPairs, pairs);
        Interlocked.Exchange(ref _udpPending, pending);
    }

    public long UdpActivePairs => Interlocked.Read(ref _udpPairs);
    public long UdpBytes => Interlocked.Read(ref _udpBytes);

    /// <summary>
    /// What this relay is and how busy it is, for the rendezvous to choose with and a dashboard to graph.
    ///
    /// <paramref name="options"/> supplies the fixed half — where this relay is and what it is willing to
    /// carry. A chooser needs both: sessions alone says nothing when one of them is a 4K stream.
    /// </summary>
    public object Snapshot(RelayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new
        {
            region = options.Region,
            maxSessions = options.MaxSessions,
            maxBitrateKbps = options.MaxBitrateKbps,
            bytesPerSecond = BytesPerSecond,
            activeSessions = ActiveSessions,
            pendingPeers = PendingPeers,
            totalSessions = TotalSessions,
            totalBytes = TotalBytes,
            rejectedLimit = RejectedLimit,
            pairingTimeouts = PairingTimeouts,
            protocolErrors = ProtocolErrors,
            ticketsRejected = TicketsRejected,
            udpActivePairs = Interlocked.Read(ref _udpPairs),
            udpPendingBinds = Interlocked.Read(ref _udpPending),
            udpTotalPairs = Interlocked.Read(ref _udpPaired),
            udpRejected = Interlocked.Read(ref _udpRejected),
            udpBytes = Interlocked.Read(ref _udpBytes),
        };
    }

}
