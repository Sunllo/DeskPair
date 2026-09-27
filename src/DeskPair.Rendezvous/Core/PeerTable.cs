using System.Collections.Concurrent;
using System.Threading.Channels;
using DeskPair.Rendezvous.Persistence;

namespace DeskPair.Rendezvous.Core;

/// <summary>
/// In-memory registry of peers keyed by id, with write-behind persistence of the durable fields
/// (id, uuid, key, timestamps). Presence (UDP endpoint, last seen) lives only in memory.
/// </summary>
public sealed class PeerTable : IHostedService, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, PeerEntry> _byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _idByUuid = new(StringComparer.Ordinal);
    private readonly Channel<StoredPeer> _persistQueue = Channel.CreateUnbounded<StoredPeer>(new UnboundedChannelOptions { SingleReader = true });
    private readonly IPeerStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<PeerTable> _log;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _flushTask;
    private bool _disposed;

    public PeerTable(IPeerStore store, TimeProvider time, ILogger<PeerTable> log)
    {
        _store = store;
        _time = time;
        _log = log;
    }

    public int Count => _byId.Count;

    public int OnlineCount(TimeSpan offlineAfter)
    {
        DateTimeOffset now = _time.GetUtcNow();
        return _byId.Values.Count(p => p.IsOnline(now, offlineAfter));
    }

    /// <summary>
    /// How many peers online now run each version of the app, largest first, the rest past <paramref name="most"/>
    /// folded into one line named <c>other</c>. The version is what a peer reported with its commit dropped
    /// (<c>0.4.1+9cf1691</c> counts as <c>0.4.1</c>): a histogram of commits would be one bar per test build.
    /// </summary>
    public IReadOnlyList<(string Version, int Online)> OnlineVersions(TimeSpan offlineAfter, int most)
    {
        DateTimeOffset now = _time.GetUtcNow();
        List<(string Version, int Online)> counted = [.. _byId.Values
            .Where(p => p.IsOnline(now, offlineAfter))
            .GroupBy(p => ReleaseOf(p.Version), StringComparer.Ordinal)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(v => v.Item2)
            .ThenBy(v => v.Key, StringComparer.Ordinal)];

        return counted.Count <= most
            ? counted
            : [.. counted.Take(most - 1), ("other", counted.Skip(most - 1).Sum(v => v.Online))];
    }

    /// <summary>A version without its commit, cut to a length a chart label can hold.</summary>
    internal static string ReleaseOf(string? version)
    {
        string release = (version ?? string.Empty).Split('+')[0].Trim();
        return release.Length > 32 ? release[..32] : release;
    }

    public PeerEntry? Get(string id) => _byId.GetValueOrDefault(id);

    public PeerEntry? GetByUuid(byte[] uuid) =>
        _idByUuid.TryGetValue(UuidKey(uuid), out string? id) ? Get(id) : null;

    public bool Contains(string id) => _byId.ContainsKey(id);

    /// <summary>Atomically replaces the entry for <paramref name="id"/>; returns the stored entry.</summary>
    public PeerEntry Update(string id, Func<PeerEntry?, PeerEntry> update, bool persist)
    {
        PeerEntry result = _byId.AddOrUpdate(id, _ => update(null), (_, existing) => update(existing));
        _idByUuid[UuidKey(result.Uuid)] = id;
        if (persist)
        {
            _persistQueue.Writer.TryWrite(ToStored(result));
        }

        return result;
    }

    /// <summary>Inserts a brand-new entry; returns false if the id is already taken.</summary>
    public bool TryAdd(PeerEntry entry)
    {
        if (!_byId.TryAdd(entry.Id, entry))
        {
            return false;
        }

        _idByUuid[UuidKey(entry.Uuid)] = entry.Id;
        _persistQueue.Writer.TryWrite(ToStored(entry));
        return true;
    }

    public bool Remove(string id)
    {
        if (!_byId.TryRemove(id, out PeerEntry? entry))
        {
            return false;
        }

        _idByUuid.TryRemove(new KeyValuePair<string, string>(UuidKey(entry.Uuid), id));
        return true;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<StoredPeer> stored = await _store.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (StoredPeer p in stored)
        {
            var entry = new PeerEntry(p.Id, p.Uuid, p.IdentityPk, p.CreatedUtc, p.LastSeenUtc, null, p.Version, null, DateTimeOffset.MinValue);
            _byId[p.Id] = entry;
            _idByUuid[UuidKey(p.Uuid)] = p.Id;
        }

        _log.LogInformation("Loaded {Count} peers from store", stored.Count);
        _flushTask = Task.Run(() => FlushLoopAsync(_stopping.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_flushTask is not null)
        {
            try
            {
                await _flushTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        var batch = new Dictionary<string, StoredPeer>(StringComparer.Ordinal);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), _time);
        try
        {
            while (true)
            {
                bool tick;
                try
                {
                    tick = await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    tick = false;
                }

                while (_persistQueue.Reader.TryRead(out StoredPeer? p))
                {
                    batch[p.Id] = p; // last write wins within a batch
                }

                if (batch.Count > 0)
                {
                    try
                    {
                        await _store.UpsertAsync(batch.Values.ToList(), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        _log.LogError(e, "Persisting {Count} peers failed; will retry", batch.Count);
                        if (tick)
                        {
                            continue;
                        }
                    }

                    batch.Clear();
                }

                if (!tick)
                {
                    return; // final flush after cancellation
                }
            }
        }
        catch (Exception e)
        {
            _log.LogError(e, "Peer persistence loop terminated");
        }
    }

    private static StoredPeer ToStored(PeerEntry e) => new(e.Id, e.Uuid, e.IdentityPk, e.CreatedUtc, e.LastSeenUtc, e.Version);

    private static string UuidKey(byte[] uuid) => Convert.ToHexString(uuid);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        _stopping.Dispose();
        if (_store is IAsyncDisposable d)
        {
            await d.DisposeAsync().ConfigureAwait(false);
        }
    }
}
