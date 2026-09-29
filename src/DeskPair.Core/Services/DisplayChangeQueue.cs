using Microsoft.Extensions.Logging;

namespace DeskPair.Core.Services;

/// <summary>
/// Changes to the displays that viewers asked for, done one at a time and away from the session that asked.
///
/// A change takes seconds -- a monitor resynchronises, the host waits for the list to agree, a display plugged in
/// takes up to five to appear -- and done where the request was read, it held up everything that viewer sent after
/// it: a mouse that stopped for as long as a new resolution took. A viewer whose window follows its size asks at
/// every pause of a resize, so requests also come faster than changes can be made. Here each is queued and the
/// session carries on; a later request for the same display replaces one not yet started (the last word is the one
/// that counts, whoever said it); and each change is followed by a pause, so the platform settles before the next.
/// </summary>
public sealed class DisplayChangeQueue : IAsyncDisposable
{
    /// <summary>The pause after each change before the next one starts.</summary>
    public static readonly TimeSpan DefaultGap = TimeSpan.FromMilliseconds(500);

    private readonly object _lock = new();
    private readonly List<Item> _pending = [];
    private readonly TimeProvider _time;
    private readonly TimeSpan _gap;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stopping = new();
    private Task _worker = Task.CompletedTask;
    private bool _working;

    /// <summary>
    /// Set on the worker's own flow. A change it makes can end a session, and a session ending waits for the queue:
    /// from inside the worker that wait would be for itself.
    /// </summary>
    private readonly AsyncLocal<bool> _onWorker = new();

    public DisplayChangeQueue(TimeProvider time, ILogger log, TimeSpan? gap = null)
    {
        _time = time;
        _log = log;
        _gap = gap ?? DefaultGap;
    }

    /// <summary>Changes waiting to start; diagnostics and tests.</summary>
    public int Pending
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    /// Queues <paramref name="work"/> for <paramref name="connectionId"/>. With a <paramref name="key"/> (a display's
    /// name) it replaces a change for the same key that has not started yet, keeping that one's place in the queue.
    /// </summary>
    public void Enqueue(int connectionId, string? key, Func<CancellationToken, Task> work)
    {
        lock (_lock)
        {
            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            var item = new Item(connectionId, key, work);
            int same = key is null ? -1 : _pending.FindIndex(p => p.Key == key);
            if (same >= 0)
            {
                _pending[same] = item;
            }
            else
            {
                _pending.Add(item);
            }

            if (!_working)
            {
                _working = true;
                _worker = Task.Run(RunAsync);
            }
        }
    }

    /// <summary>Forgets what <paramref name="connectionId"/> asked for and has not started: it has gone.</summary>
    public void Drop(int connectionId)
    {
        lock (_lock)
        {
            _pending.RemoveAll(p => p.ConnectionId == connectionId);
        }
    }

    /// <summary>
    /// Forgets everything not started and waits for the change under way, if any: what the last viewer leaving does
    /// before it puts the displays back, so no change of theirs lands after the screen was restored.
    /// </summary>
    public async Task QuiesceAsync()
    {
        Task worker;
        lock (_lock)
        {
            _pending.Clear();
            worker = _worker;
        }

        if (!_onWorker.Value)
        {
            await worker.ConfigureAwait(false);
        }
    }

    private async Task RunAsync()
    {
        _onWorker.Value = true;
        CancellationToken ct = _stopping.Token;
        while (true)
        {
            Item item;
            lock (_lock)
            {
                if (_pending.Count == 0 || ct.IsCancellationRequested)
                {
                    _working = false;
                    return;
                }

                item = _pending[0];
                _pending.RemoveAt(0);
            }

            try
            {
                await item.Work(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "A display change for session {Id} failed", item.ConnectionId);
            }

            try
            {
                await Task.Delay(_gap, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task worker;
        lock (_lock)
        {
            _pending.Clear();
            _stopping.Cancel();
            worker = _worker;
        }

        if (!_onWorker.Value)
        {
            await worker.ConfigureAwait(false);
        }

        _stopping.Dispose();
    }

    private sealed record Item(int ConnectionId, string? Key, Func<CancellationToken, Task> Work);
}
