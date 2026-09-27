using Microsoft.Extensions.Logging;
using DeskPair.Core.Session;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Services;

/// <summary>A session that receives published messages.</summary>
public interface IServiceSubscriber
{
    int ConnectionId { get; }

    ValueTask PublishAsync(Message message, MessagePriority priority, CancellationToken ct);

    /// <summary>Queues a video frame; false means the subscriber's queue was full and a frame was dropped.</summary>
    bool TryPublishVideo(Message frame);

    /// <summary>The viewer can apply <see cref="TileUpdate"/> patches (lossless tiles).</summary>
    bool SupportsLosslessTiles => false;

    /// <summary>
    /// What this viewer says it can decode. <c>None</c> means it said nothing, which
    /// <see cref="CodecNegotiation.Viewer"/> reads as H.264 only.
    /// </summary>
    Platform.Abstractions.Codec.SupportedCodecs DecodableCodecs => Platform.Abstractions.Codec.SupportedCodecs.None;

    /// <summary>The codec this viewer asked for, when it named one.</summary>
    Platform.Abstractions.Codec.VideoCodec? PreferredCodec => null;
}

/// <summary>
/// A producer (screen, cursor, audio, clipboard) shared by every session that subscribes to it.
/// The producer loop runs only while there is at least one subscriber, so an idle host does no work.
/// </summary>
public abstract class PublisherService : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<int, IServiceSubscriber> _subscribers = new();
    private IServiceSubscriber[] _snapshot = [];
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private bool _disposed;

    protected PublisherService(string name, ILogger log)
    {
        Name = name;
        Log = log;
    }

    public string Name { get; }

    protected ILogger Log { get; }

    public bool HasSubscribers
    {
        get
        {
            lock (_lock)
            {
                return _subscribers.Count > 0;
            }
        }
    }

    public int SubscriberCount
    {
        get
        {
            lock (_lock)
            {
                return _subscribers.Count;
            }
        }
    }

    public bool IsRunning => _runTask is { IsCompleted: false };

    public void Subscribe(IServiceSubscriber subscriber)
    {
        bool start;
        CancellationToken ct;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscribers[subscriber.ConnectionId] = subscriber;
            _snapshot = _subscribers.Values.ToArray();
            start = _runTask is null || _runTask.IsCompleted;
            if (start)
            {
                _runCts?.Dispose();
                _runCts = new CancellationTokenSource();
                ct = _runCts.Token;
                OnStarting();
                _runTask = Task.Run(() => RunGuardedAsync(ct), CancellationToken.None);
            }
            else
            {
                ct = _runCts!.Token;
            }
        }

        _ = OnSubscribedAsync(subscriber, ct).AsTask().ContinueWith(
            t => Log.LogWarning(t.Exception, "{Service}: snapshot for {Conn} failed", Name, subscriber.ConnectionId),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    public async Task UnsubscribeAsync(int connectionId)
    {
        Task? toAwait = null;
        CancellationTokenSource? cts = null;
        lock (_lock)
        {
            if (!_subscribers.Remove(connectionId))
            {
                return;
            }

            _snapshot = _subscribers.Values.ToArray();

            if (_subscribers.Count == 0 && _runTask is not null)
            {
                cts = _runCts;
                toAwait = _runTask;
                _runTask = null;
                _runCts = null;
            }
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await toAwait!.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            cts.Dispose();
        }

        OnUnsubscribed(connectionId);
    }

    protected IReadOnlyList<IServiceSubscriber> Subscribers => SubscriberSnapshot();

    /// <summary>Copy-on-write snapshot of the subscribers; safe to iterate without the lock and allocation-free per call.</summary>
    protected IServiceSubscriber[] SubscriberSnapshot() => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Stops the producer loop and starts it again with the same subscribers, so a setting the loop only
    /// reads at startup (the codec) can change without dropping anyone. Does nothing when nothing is running.
    /// </summary>
    protected async Task RestartAsync()
    {
        Task? toAwait;
        CancellationTokenSource? cts;
        lock (_lock)
        {
            if (_disposed || _runTask is null || _subscribers.Count == 0)
            {
                return;
            }

            cts = _runCts;
            toAwait = _runTask;
            _runTask = null;
            _runCts = null;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await toAwait!.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            cts.Dispose();
        }

        IServiceSubscriber[] again;
        CancellationToken ct;
        lock (_lock)
        {
            if (_disposed || _runTask is not null || _subscribers.Count == 0)
            {
                return;
            }

            _runCts = new CancellationTokenSource();
            ct = _runCts.Token;
            OnStarting();
            _runTask = Task.Run(() => RunGuardedAsync(ct), CancellationToken.None);
            again = _snapshot;
        }

        foreach (IServiceSubscriber s in again)
        {
            _ = OnSubscribedAsync(s, ct).AsTask().ContinueWith(
                t => Log.LogWarning(t.Exception, "{Service}: snapshot for {Conn} failed", Name, s.ConnectionId),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Called once, just before the run loop is started, while the subscriber lock is held.
    ///
    /// For the work that has to be finished before anything can be published -- in practice, throwing
    /// away a backlog that predates the first subscriber. Doing that as the run loop's first act instead
    /// looks equivalent and is not: the loop is on a pool thread, so it races both the snapshot this
    /// class sends next and anything the source produces in between, and the loser is discarded. What
    /// that cost in the product was a copy made in the instant somebody connected, swallowed; what it
    /// cost in the tests was a failure roughly one run in three, only under load.
    ///
    /// Held under the lock deliberately, because that is what makes it happen-before everything else.
    /// So it must not block, wait on anything, or call back out into code that might take the lock again.
    /// </summary>
    protected virtual void OnStarting()
    {
    }

    /// <summary>Producer loop; return when <paramref name="ct"/> is cancelled.</summary>
    protected abstract Task RunAsync(CancellationToken ct);

    /// <summary>Brings a new subscriber up to date (format announcements, current cursor, a keyframe).</summary>
    protected virtual ValueTask OnSubscribedAsync(IServiceSubscriber subscriber, CancellationToken ct) => ValueTask.CompletedTask;

    protected virtual void OnUnsubscribed(int connectionId)
    {
    }

    protected async ValueTask BroadcastAsync(Message message, MessagePriority priority, CancellationToken ct)
    {
        foreach (IServiceSubscriber s in Subscribers)
        {
            await s.PublishAsync(message, priority, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Publishes a frame to every subscriber; returns the ids whose queue dropped a frame.</summary>
    protected List<int> BroadcastVideo(Message frame, Func<IServiceSubscriber, bool>? filter = null) => BroadcastVideo(frame, SubscriberSnapshot(), filter);

    /// <summary>Publishes a frame to the given subscribers; returns the ids whose queue dropped a frame.</summary>
    protected static List<int> BroadcastVideo(Message frame, IServiceSubscriber[] subscribers, Func<IServiceSubscriber, bool>? filter)
    {
        var dropped = new List<int>();
        foreach (IServiceSubscriber s in subscribers)
        {
            if (filter is not null && !filter(s))
            {
                continue;
            }

            if (!s.TryPublishVideo(frame))
            {
                dropped.Add(s.ConnectionId);
            }
        }

        return dropped;
    }

    private async Task RunGuardedAsync(CancellationToken ct)
    {
        try
        {
            await RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Log.LogError(e, "{Service} loop crashed", Name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? toAwait;
        CancellationTokenSource? cts;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _subscribers.Clear();
            cts = _runCts;
            toAwait = _runTask;
            _runCts = null;
            _runTask = null;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await toAwait!.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            cts.Dispose();
        }

        await DisposeCoreAsync().ConfigureAwait(false);
    }

    protected virtual ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;
}
