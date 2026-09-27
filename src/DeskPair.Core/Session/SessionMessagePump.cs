using System.Threading.Channels;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using DeskPair.Protocol;
using DeskPair.Protocol.Framing;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Session;

public enum MessagePriority
{
    Control = 0,
    Input = 1,
    Video = 2,
    Bulk = 3,
}

/// <summary>
/// Owns the read and write loops of an authorized session. One reader feeds <see cref="Incoming"/>;
/// one writer drains four priority queues so control and input traffic is never stuck behind video or file blocks.
/// Video is a bounded drop-oldest queue; everything else is unbounded and awaited by the producer.
/// </summary>
public sealed class SessionMessagePump : IAsyncDisposable
{
    private const int VideoQueueCapacity = 8;
    private const int BulkQueueCapacity = 16;

    private readonly FramedStream _stream;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Channel<Message> _incoming = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });
    private readonly Channel<Message>[] _outgoing =
    [
        Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true }),
        Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true }),
        Channel.CreateBounded<Message>(new BoundedChannelOptions(VideoQueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true }),
        // Bulk (file blocks) is bounded so producers feel TCP backpressure instead of buffering whole files.
        Channel.CreateBounded<Message>(new BoundedChannelOptions(BulkQueueCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true }),
    ];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<string> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _reader;
    private Task? _writer;
    private Task? _keepalive;
    private int _videoDropped;
    private bool _stalled;

    public SessionMessagePump(FramedStream stream, ILogger log, TimeProvider? time = null)
    {
        _stream = stream;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public ChannelReader<Message> Incoming => _incoming.Reader;

    /// <summary>Completes with the close reason once the pump has stopped; faults if the stream failed.</summary>
    public Task<string> Completion => _closed.Task;

    public int VideoDropped => Volatile.Read(ref _videoDropped);

    public TimeSpan ReadTimeout { get; init; } = ProtocolConstants.SessionReadTimeout;

    public TimeSpan HeartbeatInterval { get; init; } = ProtocolConstants.SessionHeartbeatInterval;

    public TimeSpan StalledAfter { get; init; } = ProtocolConstants.SessionStalledAfter;

    /// <summary>
    /// True while nothing has been heard from the peer for <see cref="StalledAfter"/>, false again the
    /// moment something is. Raised on the keepalive thread. This is not a close: the session is still open
    /// and still has until <see cref="ReadTimeout"/> to come back, and most stalls do — a laptop lid, a
    /// change of network, a moment of congestion. It exists so the window can say so instead of showing a
    /// frozen picture and nothing else.
    /// </summary>
    public event Action<bool>? Stalled;

    public void Start()
    {
        if (_reader is not null)
        {
            throw new InvalidOperationException("Pump already started.");
        }

        _reader = Task.Run(() => ReadLoopAsync(_cts.Token));
        _writer = Task.Run(() => WriteLoopAsync(_cts.Token));
        _keepalive = Task.Run(() => KeepAliveLoopAsync(_cts.Token));
    }

    public ValueTask SendAsync(Message message, MessagePriority priority = MessagePriority.Control, CancellationToken ct = default)
    {
        if (_closed.Task.IsCompleted)
        {
            return ValueTask.CompletedTask;
        }

        if (priority == MessagePriority.Video)
        {
            TrySendVideo(message);
            return ValueTask.CompletedTask;
        }

        if (priority == MessagePriority.Bulk)
        {
            return SendBulkAsync(message, ct);
        }

        if (_outgoing[(int)priority].Writer.TryWrite(message))
        {
            _signal.Release();
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask SendBulkAsync(Message message, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        try
        {
            await _outgoing[(int)MessagePriority.Bulk].Writer.WriteAsync(message, linked.Token).ConfigureAwait(false);
            _signal.Release();
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Session closed while the producer was blocked; drop silently like the other priorities.
        }
        catch (ChannelClosedException)
        {
        }
    }

    /// <summary>Queues a video frame; when the peer is behind the new frame is dropped (queued ones may include a keyframe). Returns false when dropped.</summary>
    public bool TrySendVideo(Message frame)
    {
        Channel<Message> video = _outgoing[(int)MessagePriority.Video];
        bool dropped = video.Reader.Count >= VideoQueueCapacity;
        if (dropped)
        {
            Interlocked.Increment(ref _videoDropped);
        }

        if (video.Writer.TryWrite(frame))
        {
            _signal.Release();
        }

        return !dropped;
    }

    public async Task CloseAsync(string reason)
    {
        if (_closed.TrySetResult(reason))
        {
            _incoming.Writer.TryComplete();
            await _cts.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ReadTimeout);
                Frame? frame;
                try
                {
                    frame = await _stream.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Fail("read timeout");
                    return;
                }

                if (frame is null)
                {
                    Fail("peer closed");
                    return;
                }

                Message message;
                using (frame)
                {
                    message = Message.Parser.ParseFrom(frame.Payload.Span);
                }

                await _incoming.Writer.WriteAsync(message, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is ProtocolException or InvalidProtocolBufferException or IOException)
        {
            _log.LogDebug(e, "Session read loop ended");
            Fail($"read error: {e.Message}");
        }
        catch (Exception e)
        {
            _log.LogError(e, "Session read loop crashed");
            Fail($"read error: {e.Message}");
        }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
                while (TryDequeue(out Message? message))
                {
                    await _stream.SendAsync(message, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IOException or ProtocolException or ObjectDisposedException)
        {
            _log.LogDebug(e, "Session write loop ended");
            Fail($"write error: {e.Message}");
        }
        catch (Exception e)
        {
            _log.LogError(e, "Session write loop crashed");
            Fail($"write error: {e.Message}");
        }
    }

    private bool TryDequeue([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Message? message)
    {
        foreach (Channel<Message> channel in _outgoing)
        {
            if (channel.Reader.TryRead(out message))
            {
                return true;
            }
        }

        message = null;
        return false;
    }

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (_time.GetUtcNow() - _stream.LastSentUtc >= HeartbeatInterval)
                {
                    await _stream.SendHeartbeatAsync(ct).ConfigureAwait(false);
                }

                // Heartbeats count: the question is whether the peer is reachable, not whether it has
                // anything to say.
                bool silent = _time.GetUtcNow() - _stream.LastReceivedUtc >= StalledAfter;
                if (silent != _stalled)
                {
                    _stalled = silent;
                    Stalled?.Invoke(silent);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            Fail($"heartbeat failed: {e.Message}");
        }
    }

    private void Fail(string reason)
    {
        if (_closed.TrySetResult(reason))
        {
            _incoming.Writer.TryComplete();
            _cts.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync("disposed").ConfigureAwait(false);
        foreach (Task? t in new[] { _reader, _writer, _keepalive })
        {
            if (t is not null)
            {
                try
                {
                    await t.ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }
        }

        _cts.Dispose();
        _signal.Dispose();
    }
}
