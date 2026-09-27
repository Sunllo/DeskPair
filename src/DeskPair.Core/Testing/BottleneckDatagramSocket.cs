using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using DeskPair.Core.Transport.Udp;

namespace DeskPair.Core.Testing;

/// <summary>
/// Test double for a narrow link in front of a datagram socket's receive side: incoming datagrams go through a
/// fixed-capacity FIFO with a bounded queueing delay (drop-tail), like a home uplink's modem buffer. Sends pass through.
/// </summary>
public sealed class BottleneckDatagramSocket : IDatagramSocket
{
    private readonly IDatagramSocket _inner;
    private readonly TimeProvider _time;
    private readonly Channel<(byte[] Data, int Length, SocketAddress From)> _delivered = Channel.CreateUnbounded<(byte[], int, SocketAddress)>();
    private readonly ConcurrentQueue<(byte[] Data, int Length, SocketAddress From, long DueTicks)> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private readonly Thread _delivery;
    private long _linkFreeTicks;

    public BottleneckDatagramSocket(IDatagramSocket inner, double capacityBps, TimeSpan maxQueueDelay, TimeProvider? time = null)
    {
        _inner = inner;
        _time = time ?? TimeProvider.System;
        CapacityBps = capacityBps;
        MaxQueueDelay = maxQueueDelay;
        _pump = Task.Run(PumpAsync);
        _delivery = new Thread(Deliver) { IsBackground = true, Name = "bottleneck-delivery" };
        _delivery.Start();
    }

    /// <summary>Link capacity in bits per second; may be changed while running.</summary>
    public double CapacityBps { get; set; }

    public TimeSpan MaxQueueDelay { get; }

    public long Dropped { get; private set; }

    public long Delivered { get; private set; }

    public IPEndPoint LocalEndPoint => _inner.LocalEndPoint;

    public int SendTo(ReadOnlySpan<byte> datagram, IPEndPoint target) => _inner.SendTo(datagram, target);

    public async ValueTask<int> ReceiveFromAsync(Memory<byte> buffer, SocketAddress from, CancellationToken ct)
    {
        (byte[] data, int length, SocketAddress source) = await _delivered.Reader.ReadAsync(ct).ConfigureAwait(false);
        data.AsSpan(0, length).CopyTo(buffer.Span);
        from.Size = source.Size;
        source.Buffer.Span[..source.Size].CopyTo(from.Buffer.Span);
        return length;
    }

    private async Task PumpAsync()
    {
        byte[] buffer = new byte[2048];
        var from = new SocketAddress(AddressFamily.InterNetworkV6);
        while (!_cts.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await _inner.ReceiveFromAsync(buffer, from, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            long now = _time.GetTimestamp();
            long start = Math.Max(now, _linkFreeTicks);
            if (start - now > MaxQueueDelay.TotalSeconds * _time.TimestampFrequency)
            {
                Dropped++;
                continue;
            }

            _linkFreeTicks = start + (long)(n * 8 / CapacityBps * _time.TimestampFrequency);
            var source = new SocketAddress(from.Family, from.Size);
            from.Buffer.Span[..from.Size].CopyTo(source.Buffer.Span);
            _queue.Enqueue((buffer.AsSpan(0, n).ToArray(), n, source, _linkFreeTicks));
            _signal.Release();
        }
    }

    private void Deliver()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _signal.Wait(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!_queue.TryDequeue(out (byte[] Data, int Length, SocketAddress From, long DueTicks) item))
            {
                continue;
            }

            while (!_cts.IsCancellationRequested)
            {
                long remaining = item.DueTicks - _time.GetTimestamp();
                if (remaining <= 0)
                {
                    break;
                }

                if (remaining * 1000 / _time.TimestampFrequency > 20)
                {
                    Thread.Sleep(1);
                }
                else
                {
                    Thread.Yield();
                }
            }

            Delivered++;
            _delivered.Writer.TryWrite((item.Data, item.Length, item.From));
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _inner.Dispose();
        _delivery.Join(TimeSpan.FromSeconds(1));
        try
        {
            _pump.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
        }

        _delivered.Writer.TryComplete();
        _signal.Dispose();
        _cts.Dispose();
    }
}
