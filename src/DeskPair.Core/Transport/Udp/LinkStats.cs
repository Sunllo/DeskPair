namespace DeskPair.Core.Transport.Udp;

/// <summary>
/// What one UDP path delivered: bytes, and packet loss before FEC over the last second, read from gaps in the
/// packet sequence.
///
/// A property of the link, not of any stream on it. Every stream and every control packet draws from the one
/// packet sequence, so a count kept per stream would read the other streams' packets as gaps and report a
/// plausible and wrong loss rate -- which is why this used to live inside the frame assembler only while
/// there was one stream, and is its own thing now that there can be several.
/// </summary>
public sealed class LinkStats
{
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private ulong _windowFirstSeq, _windowLastSeq, _windowReceived;
    private long _windowStart;

    public LinkStats(TimeProvider time)
    {
        _time = time;
        _windowStart = time.GetTimestamp();
    }

    public ulong ReceivedBytes { get; private set; }

    /// <summary>Packet loss before FEC over the last second (per mille), from gaps in the packet sequence.</summary>
    public int LossPermille
    {
        get
        {
            if (_windowLastSeq <= _windowFirstSeq || _windowReceived == 0)
            {
                return 0;
            }

            ulong expected = _windowLastSeq - _windowFirstSeq + 1;
            return (int)Math.Clamp(1000.0 * (1.0 - (double)_windowReceived / expected), 0, 1000);
        }
    }

    /// <summary>Counts every authenticated datagram (of any type) for the loss estimate.</summary>
    public void NotePacket(ulong packetSeq, int bytes)
    {
        lock (_lock)
        {
            ReceivedBytes += (ulong)bytes;
            if (_time.GetElapsedTime(_windowStart) > TimeSpan.FromSeconds(1))
            {
                _windowStart = _time.GetTimestamp();
                _windowFirstSeq = _windowLastSeq = packetSeq;
                _windowReceived = 0;
            }

            if (_windowReceived == 0 || packetSeq < _windowFirstSeq)
            {
                _windowFirstSeq = packetSeq;
            }

            if (packetSeq > _windowLastSeq)
            {
                _windowLastSeq = packetSeq;
            }

            _windowReceived++;
        }
    }
}
