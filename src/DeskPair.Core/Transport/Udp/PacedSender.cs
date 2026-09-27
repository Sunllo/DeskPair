using System.Net;

namespace DeskPair.Core.Transport.Udp;

/// <summary>
/// Sends a frame's datagrams spread over a short window instead of as one burst, on a dedicated thread.
/// Large frames are paced at the congestion controller's pacing rate (GCC target x 2.5) when one is set,
/// otherwise at twice the running rate; frames that waited too long behind them are dropped whole.
/// </summary>
public sealed class PacedSender : IDisposable
{
    private static readonly TimeSpan MaxSpread = TimeSpan.FromMilliseconds(12);
    private static readonly TimeSpan MaxBurstSpread = TimeSpan.FromMilliseconds(100);
    /// <summary>Frames that have waited longer than this behind a large frame are dropped (the caller then asks for a keyframe).</summary>
    private static readonly TimeSpan MaxQueueDelay = TimeSpan.FromMilliseconds(150);
    private const double MinPacingBytesPerSecond = 3_000_000; // 24 Mbps floor so keyframes never crawl on a fast link

    private readonly IDatagramSocket _socket;
    private readonly Func<IPEndPoint?> _remote;
    private readonly Action<ulong, long, int>? _onSent;
    private readonly TimeProvider _time;
    private readonly Queue<(List<PooledDatagram> Datagrams, TimeSpan Spread, long Queued, bool Probe)> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _lock = new();
    private readonly Thread _thread;
    private volatile bool _stopping;
    private double _bytesPerSecond;

    /// <summary><paramref name="onSent"/> is called per datagram with its packet sequence, send timestamp and size (round trips, bandwidth estimation).</summary>
    public PacedSender(IDatagramSocket socket, Func<IPEndPoint?> remote, TimeProvider time, Action<ulong, long, int>? onSent = null)
    {
        _socket = socket;
        _remote = remote;
        _time = time;
        _onSent = onSent;
        _thread = new Thread(Run) { IsBackground = true, Name = "udp-media-send" };
        _thread.Start();
    }

    public long FramesSent { get; private set; }

    public long FramesDropped { get; private set; }

    public long DatagramsSent { get; private set; }

    public long BytesSent { get; private set; }

    /// <summary>Smoothed send rate (bytes per second) the frames imply; large frames are paced against twice this.</summary>
    public double EstimatedBytesPerSecond => _bytesPerSecond;

    /// <summary>Pacing rate from the congestion controller (bytes per second); 0 falls back to the running-rate rule.</summary>
    public double PacingBytesPerSecond { get; set; }

    /// <summary>Queues a frame; false when an older unsent frame had to be discarded to make room.</summary>
    public bool Enqueue(List<PooledDatagram> datagrams, TimeSpan frameInterval)
    {
        long bytes = 0;
        foreach (PooledDatagram d in datagrams)
        {
            bytes += d.Length;
        }

        double instant = bytes / Math.Max(frameInterval.TotalSeconds, 0.001);
        _bytesPerSecond = _bytesPerSecond == 0 ? instant : 0.9 * _bytesPerSecond + 0.1 * instant;

        // Normal frames go out inside their own interval; a keyframe many times larger is spread so the burst
        // stays near twice the running rate instead of hitting the uplink at line speed and losing half of it.
        TimeSpan spread = TimeSpan.FromTicks(Math.Min((long)(frameInterval.Ticks * 0.7), MaxSpread.Ticks));
        double pacing = PacingBytesPerSecond;
        // Never pace below what the encoder is actually producing (it follows the target with some lag), or the
        // pacer itself becomes the bottleneck and drops frames.
        double rateCap = pacing > 0 ? Math.Max(pacing, 1.25 * _bytesPerSecond) : Math.Max(2 * _bytesPerSecond, MinPacingBytesPerSecond);
        var byRate = TimeSpan.FromSeconds(bytes / rateCap);
        if (byRate > spread)
        {
            spread = byRate < MaxBurstSpread ? byRate : MaxBurstSpread;
        }

        // Queue depth is bounded by waiting time, not by count: a paced keyframe legitimately holds a few frames
        // behind it for tens of milliseconds, and dropping those would cost another keyframe (and start over).
        bool dropped = false;
        long now = _time.GetTimestamp();
        lock (_lock)
        {
            while (_queue.Count > 0 && _time.GetElapsedTime(_queue.Peek().Queued, now) > MaxQueueDelay)
            {
                var stale = _queue.Dequeue();
                foreach (PooledDatagram d in stale.Datagrams)
                {
                    d.Return();
                }

                if (!stale.Probe)
                {
                    FramesDropped++;
                    dropped = true;
                }
            }

            _queue.Enqueue((datagrams, spread, now, false));
        }

        _signal.Release();
        return !dropped;
    }

    /// <summary>Queues a probe cluster: its datagrams go out evenly over exactly <paramref name="duration"/>.</summary>
    public void EnqueueProbe(List<PooledDatagram> datagrams, TimeSpan duration)
    {
        lock (_lock)
        {
            _queue.Enqueue((datagrams, duration, _time.GetTimestamp(), true));
        }

        _signal.Release();
    }

    /// <summary>Probe clusters sent (diagnostics).</summary>
    public long ProbesSent { get; private set; }

    /// <summary>Control datagrams bypass pacing.</summary>
    public void SendNow(ReadOnlySpan<byte> datagram, IPEndPoint target) => _socket.SendTo(datagram, target);

    private void Run()
    {
        while (!_stopping)
        {
            try
            {
                _signal.Wait();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            (List<PooledDatagram> Datagrams, TimeSpan Spread, long Queued, bool Probe) item;
            lock (_lock)
            {
                if (!_queue.TryDequeue(out item))
                {
                    continue;
                }
            }

            IPEndPoint? target = _remote();
            long start = _time.GetTimestamp();
            long perPacket = datagrams(item).Count > 1 ? (long)(spread(item).TotalSeconds * _time.TimestampFrequency / (datagrams(item).Count - 1)) : 0;
            int index = 0;
            foreach (PooledDatagram d in datagrams(item))
            {
                if (target is not null && !_stopping)
                {
                    long due = start + index * perPacket;
                    WaitUntil(due);
                    _socket.SendTo(d.Span, target);
                    DatagramsSent++;
                    BytesSent += d.Length;
                    _onSent?.Invoke(PacketSeqOf(d), _time.GetTimestamp(), d.Length);
                }

                d.Return();
                index++;
            }

            if (item.Probe)
            {
                ProbesSent++;
            }
            else
            {
                FramesSent++;
            }
        }

        static List<PooledDatagram> datagrams((List<PooledDatagram> Datagrams, TimeSpan Spread, long Queued, bool Probe) i) => i.Datagrams;
        static TimeSpan spread((List<PooledDatagram> Datagrams, TimeSpan Spread, long Queued, bool Probe) i) => i.Spread;
    }

    private static ulong PacketSeqOf(PooledDatagram d) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(d.Span[4..]);

    private void WaitUntil(long due)
    {
        while (!_stopping)
        {
            long remaining = due - _time.GetTimestamp();
            if (remaining <= 0)
            {
                return;
            }

            double ms = remaining * 1000.0 / _time.TimestampFrequency;
            if (ms > 1.5)
            {
                Thread.Sleep(1);
            }
            else
            {
                Thread.SpinWait(40);
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _signal.Release();
        _thread.Join(TimeSpan.FromSeconds(1));
        lock (_lock)
        {
            while (_queue.TryDequeue(out (List<PooledDatagram> Datagrams, TimeSpan Spread, long Queued, bool Probe) item))
            {
                foreach (PooledDatagram d in item.Datagrams)
                {
                    d.Return();
                }
            }
        }

        _signal.Dispose();
    }
}
