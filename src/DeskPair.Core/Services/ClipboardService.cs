using Microsoft.Extensions.Logging;
using DeskPair.Core.Clipboard;
using DeskPair.Core.FileTransfer;
using DeskPair.Core.Session;
using DeskPair.Core.Session.Host;
using DeskPair.Platform.Abstractions.Clipboard;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Services;

/// <summary>Publishes host clipboard changes to viewers and applies what viewers send, with echo suppression.</summary>
public sealed class ClipboardService : PublisherService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly IClipboard _clipboard;
    private readonly TimeProvider _time;
    private readonly ClipboardSync _sync = new();

    public ClipboardService(IClipboard clipboard, TimeProvider time, ILogger log)
        : base("clipboard", log)
    {
        _clipboard = clipboard;
        _time = time;
    }

    public long Published { get; private set; }

    public long Applied { get; private set; }

    /// <summary>
    /// Set when this host may keep a file promise a viewer sends. Null leaves the feature off entirely, and
    /// a file list then simply never reaches the clipboard.
    /// </summary>
    public ClipboardPromiseRouter? PromiseRouter { get; set; }

    /// <summary>The transfer engine for a connection, which is how a promised file is fetched when pasted.</summary>
    public Func<int, FileTransferEngine?>? FileEngineFor { get; set; }

    /// <summary>
    /// Records what the host's user has copied so viewers can be offered it. Null leaves the host publishing
    /// content only, as before.
    /// </summary>
    public LocalFileOffer? Offer { get; set; }

    protected override async ValueTask OnSubscribedAsync(IServiceSubscriber subscriber, CancellationToken ct)
    {
        IReadOnlyList<ClipboardItem> items = await WithCopiedFilesAsync(await _clipboard.ReadAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        MultiClipboards? wire = ClipboardSync.ToWire(items);
        if (wire is not null)
        {
            await subscriber.PublishAsync(new Message { Clipboard = wire }, MessagePriority.Bulk, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Everything already queued predates the first subscriber, so none of it is news to anyone.
    ///
    /// The platform clipboards poll continuously into a bounded channel, and the loop below only runs
    /// while somebody is subscribed, so without this a session that connects later is handed the last
    /// several things the host's user copied, one after another, as if each had just happened. The newest
    /// of them would also arrive last and win, leaving the viewer holding something stale. The content as
    /// it stands now still goes out: OnSubscribedAsync reads it live.
    ///
    /// Here rather than at the top of RunAsync, which is where it used to be and where it was a race: the
    /// loop starts on a pool thread, so a copy made in that instant could reach the channel first and be
    /// thrown away with the backlog. From here the discard is finished before the run loop exists.
    /// </summary>
    protected override void OnStarting()
    {
        while (_clipboard.Changes.TryRead(out _))
        {
        }
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        long lastSent = 0;
        await foreach (IReadOnlyList<ClipboardItem> items in _clipboard.Changes.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (_time.GetElapsedTime(lastSent) < Debounce && lastSent != 0)
            {
                await Task.Delay(Debounce, _time, ct).ConfigureAwait(false);
            }

            MultiClipboards? wire = ClipboardSync.ToWire(await WithCopiedFilesAsync(items, ct).ConfigureAwait(false));
            if (wire is null || !_sync.ShouldSend(wire.ContentHash.Span))
            {
                continue;
            }

            _sync.MarkSent(wire.ContentHash.ToByteArray());
            await BroadcastAsync(new Message { Clipboard = wire }, MessagePriority.Bulk, ct).ConfigureAwait(false);
            Published++;
            lastSent = _time.GetTimestamp();
        }
    }

    /// <summary>
    /// Adds a listing of the files the host's user copied. Names and sizes only; a viewer that pastes fetches
    /// the bytes through its own session, where its file permission and the configured roots both apply.
    /// </summary>
    private async ValueTask<IReadOnlyList<ClipboardItem>> WithCopiedFilesAsync(IReadOnlyList<ClipboardItem> items, CancellationToken ct)
    {
        if (Offer is null || _clipboard is not IFilePromiseClipboard promiser)
        {
            return items;
        }

        try
        {
            IReadOnlyList<string> paths = await promiser.ReadCopiedFilePathsAsync(ct).ConfigureAwait(false);
            ClipboardItem? listing = Offer.Offer(paths, LocalFileSystem.Instance);
            return listing is null ? items : [.. items, listing];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Offer.Withdraw();
            return items;
        }
    }

    /// <summary>
    /// Applies clipboard content received from a viewer. <paramref name="filesAllowed"/> is that viewer's
    /// file permission: without it a copied file list is dropped, because keeping the promise would mean
    /// fetching files over a channel the viewer is not allowed to use.
    /// </summary>
    public async ValueTask ApplyAsync(MultiClipboards clipboards, int connectionId, bool filesAllowed, CancellationToken ct)
    {
        byte[] hash = clipboards.ContentHash.Length == 32 ? clipboards.ContentHash.ToByteArray() : ClipboardSync.Hash(clipboards);
        if (!_sync.ShouldApply(hash))
        {
            return;
        }

        _sync.MarkApplied(hash);
        IReadOnlyList<ClipboardItem> items = ClipboardSync.FromWire(clipboards);
        if (PromiseRouter is { } router)
        {
            await router.WriteAsync(_clipboard, items, FileEngineFor?.Invoke(connectionId), filesAllowed, ct).ConfigureAwait(false);
        }
        else
        {
            await _clipboard.WriteAsync(items, ct).ConfigureAwait(false);
        }

        Applied++;
    }
}

public sealed class ClipboardHandler : ISessionHandler<HostSessionContext>
{
    private readonly ClipboardService _service;

    public ClipboardHandler(ClipboardService service)
    {
        _service = service;
    }

    public IEnumerable<Message.UnionOneofCase> Handles => [Message.UnionOneofCase.Clipboard];

    public ValueTask HandleAsync(HostSessionContext context, Message message, CancellationToken ct) =>
        context.Permissions.Has(Permission.PermClipboard)
            ? _service.ApplyAsync(message.Clipboard, context.ConnectionId, context.Permissions.Has(Permission.PermFile), ct)
            : ValueTask.CompletedTask;
}
