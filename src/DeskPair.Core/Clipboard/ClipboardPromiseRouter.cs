using Microsoft.Extensions.Logging;
using DeskPair.Core.FileTransfer;
using DeskPair.Platform.Abstractions.Clipboard;

namespace DeskPair.Core.Clipboard;

/// <summary>
/// The one place that decides what to do with an incoming clipboard that contains a file promise, shared by
/// the host and the controller so the two cannot drift. It branches on what the clipboard can do, never on
/// which operating system it is.
/// </summary>
public sealed class ClipboardPromiseRouter
{
    private readonly ClipboardStaging _staging;
    private readonly ILogger _log;
    private EngineFilePromiseSource? _current;
    private bool _warned;

    public ClipboardPromiseRouter(ClipboardStaging staging, ILogger log)
    {
        _staging = staging;
        _log = log;
    }

    /// <summary>The promise currently on this machine's clipboard, if any.</summary>
    public EngineFilePromiseSource? Current => _current;

    /// <summary>
    /// Writes <paramref name="items"/> to <paramref name="clipboard"/>, promising files if there is a file
    /// listing among them and the clipboard can keep that promise.
    /// </summary>
    public async ValueTask WriteAsync(
        IClipboard clipboard,
        IReadOnlyList<ClipboardItem> items,
        FileTransferEngine? engine,
        bool filesAllowed,
        CancellationToken ct)
    {
        ClipboardItem? listingItem = items.FirstOrDefault(i => i.Format == ClipboardItemFormat.FileList);
        if (listingItem is null)
        {
            // Plain content. A previous promise stops being current the moment something else is copied.
            Supersede();
            await clipboard.WriteAsync(items, ct).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<ClipboardItem> withoutFiles = items.Where(i => i.Format != ClipboardItemFormat.FileList).ToList();

        if (!filesAllowed)
        {
            WarnOnce("A copied file list was dropped: this session does not have file transfer permission");
            await WriteRestAsync(clipboard, withoutFiles, ct).ConfigureAwait(false);
            return;
        }

        if (engine is null || clipboard is not IFilePromiseClipboard { CanPromiseFiles: true } promiser)
        {
            WarnOnce("A copied file list was dropped: this platform cannot offer files it has not downloaded");
            await WriteRestAsync(clipboard, withoutFiles, ct).ConfigureAwait(false);
            return;
        }

        FilePromiseListing? listing = FilePromiseCodec.Decode(listingItem.Payload, out int rejected);
        if (listing is null)
        {
            WarnOnce("A copied file list was dropped: the listing could not be read");
            await WriteRestAsync(clipboard, withoutFiles, ct).ConfigureAwait(false);
            return;
        }

        if (rejected > 0)
        {
            _log.LogWarning("Dropped {Count} promised file(s) whose names were not usable", rejected);
        }

        if (listing.Entries.Count == 0)
        {
            await WriteRestAsync(clipboard, withoutFiles, ct).ConfigureAwait(false);
            return;
        }

        // Whatever was promised before is now unreachable: only one thing can be on a clipboard.
        Supersede();
        var source = new EngineFilePromiseSource(engine, listing, _staging, _log);
        _current = source;

        await promiser.WriteWithPromiseAsync(withoutFiles, listing, source, ct).ConfigureAwait(false);
        _log.LogInformation("Offering {Count} promised file(s) from the peer, token {Token}", listing.Entries.Count, listing.Token);
    }

    /// <summary>The promise is no longer on the clipboard; its staged bytes are no longer reachable.</summary>
    public void Supersede()
    {
        EngineFilePromiseSource? previous = Interlocked.Exchange(ref _current, null);
        previous?.Discard();
    }

    private static ValueTask WriteRestAsync(IClipboard clipboard, IReadOnlyList<ClipboardItem> items, CancellationToken ct) =>
        items.Count == 0 ? ValueTask.CompletedTask : clipboard.WriteAsync(items, ct);

    /// <summary>Once per router, not once per copy: this would otherwise be every Ctrl+C for the whole session.</summary>
    private void WarnOnce(string message)
    {
        if (!_warned)
        {
            _warned = true;
            _log.LogWarning("{Message}", message);
        }
    }
}
