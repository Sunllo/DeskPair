using System.Text;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Clipboard;
using DeskPair.Platform.MacOS.Native;

namespace DeskPair.Platform.MacOS.Clipboard;

/// <summary>
/// The loop that answers pastes of promised files on macOS.
///
/// The shim publishes one lazy pasteboard item per copied item and blocks inside AppKit's callback until we
/// answer; this drains those requests, fetches the file or folder into staging, and hands back its path.
/// It pulls with a timeout rather than being called back, which is how every async capability in the shim
/// works — no function pointers cross the ABI.
///
/// Items are offered as the peer copied them, folders included. macOS delivers a promise as a file URL, and
/// a URL is one thing: offering a folder's files individually would paste them flat, losing the folder.
/// </summary>
internal sealed class MacFilePromises : IAsyncDisposable
{
    private static readonly TimeSpan PullTimeout = TimeSpan.FromMilliseconds(500);

    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private IFilePromiseSource? _source;
    private IReadOnlyList<FilePromiseEntry> _entries = [];

    public MacFilePromises(ILogger log)
    {
        _log = log;
    }

    /// <summary>Whether a promise of ours is on the pasteboard right now.</summary>
    public bool IsOffering => _source is not null;

    /// <summary>Publishes the promise and whatever text was copied with it, in one pasteboard write.</summary>
    public bool Offer(IReadOnlyList<FilePromiseEntry> entries, IFilePromiseSource source, string? text)
    {
        _source = source;
        _entries = entries;

        byte[] names = PackNames(entries.Select(e => e.RelativePath));
        byte[]? textUtf8 = text is null ? null : Encoding.UTF8.GetBytes(text + "\0");

        if (MacShim.fd_clipboard_write_file_promises(names, entries.Count, textUtf8) != 0)
        {
            _log.LogWarning("The pasteboard refused the file promise");
            return false;
        }

        _loop ??= Task.Run(() => RunAsync(_cts.Token));
        return true;
    }

    /// <summary>Stops offering. Anything still waiting inside AppKit is failed rather than left hanging.</summary>
    public void Withdraw()
    {
        _source = null;
        _entries = [];
        MacShim.fd_promise_shutdown();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            int index = MacShim.fd_promise_next((int)PullTimeout.TotalMilliseconds, out long request);
            if (index < 0)
            {
                continue;   // nothing pasted in that window
            }

            IFilePromiseSource? source = _source;
            IReadOnlyList<FilePromiseEntry> entries = _entries;
            if (source is null || index >= entries.Count)
            {
                MacShim.fd_promise_complete(request, null);
                continue;
            }

            FilePromiseEntry entry = entries[index];
            string? staged = null;
            try
            {
                string destination = source.StagedPathFor(entry.Id);
                await source.FetchAsync(entry.Id, destination, null, ct).ConfigureAwait(false);
                staged = destination;
            }
            catch (OperationCanceledException)
            {
                // Shutting down, or the promise was superseded; the paste fails, which is the honest answer.
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Could not deliver the promised item {Path}", entry.RelativePath);
            }

            // Always answer: the pasting application is blocked inside AppKit until we do.
            MacShim.fd_promise_complete(request, staged);
        }
    }

    /// <summary>NUL-separated UTF-8 with a trailing NUL, which is what the shim splits on.</summary>
    internal static byte[] PackNames(IEnumerable<string> names)
    {
        var buffer = new List<byte>(256);
        foreach (string name in names)
        {
            buffer.AddRange(Encoding.UTF8.GetBytes(name));
            buffer.Add(0);
        }

        buffer.Add(0);
        return [.. buffer];
    }

    public async ValueTask DisposeAsync()
    {
        Withdraw();
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The loop observes cancellation; nothing to surface on teardown.
            }
        }

        _cts.Dispose();
    }
}
