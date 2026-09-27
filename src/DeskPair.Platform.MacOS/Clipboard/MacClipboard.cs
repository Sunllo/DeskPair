using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Clipboard;
using DeskPair.Platform.MacOS.Native;

namespace DeskPair.Platform.MacOS.Clipboard;

/// <summary>
/// The general pasteboard: text and images. macOS has no pasteboard change notification, so a background task
/// polls the change count a few times a second and publishes external changes; the app's own writes bump the
/// count too, so the count they produce is remembered and not echoed back. Rich text is still a follow-on.
/// </summary>
public sealed class MacClipboard : IClipboard, IFilePromiseClipboard
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly ILogger _log;
    private readonly Channel<IReadOnlyList<ClipboardItem>> _changes =
        Channel.CreateBounded<IReadOnlyList<ClipboardItem>>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _poller;
    private long _lastSeen;
    private bool _disposed;
    private MacFilePromises? _promises;

    public MacClipboard(ILogger log)
    {
        _log = log;
        _lastSeen = MacShim.fd_clipboard_change_count();
        _poller = Task.Run(PollAsync);
    }

    public ChannelReader<IReadOnlyList<ClipboardItem>> Changes => _changes.Reader;

    public ValueTask<IReadOnlyList<ClipboardItem>> ReadAsync(CancellationToken ct) =>
        ValueTask.FromResult(ReadItems());

    /// <summary>macOS can hand out a file URL for a file it does not have yet, so a promise is keepable.</summary>
    public bool CanPromiseFiles => !_disposed;

    public ValueTask<IReadOnlyList<string>> ReadCopiedFilePathsAsync(CancellationToken ct)
    {
        // Our own promise is on the pasteboard as lazy file URLs, and reading one is what makes AppKit ask
        // us for the file. Asking ourselves would fetch the peer's whole selection to answer a question
        // about what the local user copied.
        if (_promises is { IsOffering: true })
        {
            return ValueTask.FromResult<IReadOnlyList<string>>([]);
        }

        int count = MacShim.fd_clipboard_read_file_paths(out nint block);
        if (count <= 0 || block == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<string>>([]);
        }

        try
        {
            var paths = new List<string>(count);
            nint p = block;
            while (paths.Count < count && Marshal.ReadByte(p) != 0)
            {
                string? path = Marshal.PtrToStringUTF8(p);
                if (string.IsNullOrEmpty(path))
                {
                    break;
                }

                paths.Add(path);
                p += Encoding.UTF8.GetByteCount(path) + 1;
            }

            return ValueTask.FromResult<IReadOnlyList<string>>(paths);
        }
        finally
        {
            MacShim.fd_free(block);
        }
    }

    public ValueTask WriteWithPromiseAsync(
        IReadOnlyList<ClipboardItem> items,
        FilePromiseListing listing,
        IFilePromiseSource source,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ClipboardItem? text = items.FirstOrDefault(i => i.Format == ClipboardItemFormat.Text);
        _promises ??= new MacFilePromises(_log);

        // Top level only: a promise is delivered as a file URL, and a URL is one thing. Offering a folder's
        // files separately would paste them flat and lose the folder.
        if (_promises.Offer(listing.Entries, source, text is null ? null : Encoding.UTF8.GetString(text.Payload.Span)))
        {
            _lastSeen = MacShim.fd_clipboard_change_count();  // our own write; do not echo it back
            _log.LogInformation(
                "Offering {Count} promised item(s) on the pasteboard (token {Token})",
                listing.Entries.Count,
                listing.Token);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask WriteAsync(IReadOnlyList<ClipboardItem> items, CancellationToken ct)
    {
        // Whatever was promised is gone the moment something else is written.
        _promises?.Withdraw();

        ClipboardItem? text = items.FirstOrDefault(i => i.Format == ClipboardItemFormat.Text);
        ClipboardItem? image = items.FirstOrDefault(i => i.Format == ClipboardItemFormat.ImagePng);
        string? utf8 = text is null ? null : Encoding.UTF8.GetString(text.Payload.Span);

        if (image is not null)
        {
            // One call for both, because a second declareTypes: on the pasteboard throws away the first.
            unsafe
            {
                fixed (byte* png = image.Payload.Span)
                {
                    MacShim.fd_clipboard_write_image(png, image.Payload.Length, utf8);
                }
            }
        }
        else if (utf8 is not null)
        {
            MacShim.fd_clipboard_write_text(utf8);
        }
        else
        {
            // Nothing this pasteboard can hold. Leaving it alone is deliberate: clearing it would throw
            // away whatever the user had, for a clipboard update that carried nothing we understand.
            return ValueTask.CompletedTask;
        }

        _lastSeen = MacShim.fd_clipboard_change_count(); // our own write; do not echo it back
        return ValueTask.CompletedTask;
    }

    private async Task PollAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, _cts.Token).ConfigureAwait(false);
                long now = MacShim.fd_clipboard_change_count();
                if (now == _lastSeen)
                {
                    continue;
                }

                _lastSeen = now;
                IReadOnlyList<ClipboardItem> items = ReadItems();
                if (items.Count > 0)
                {
                    _changes.Writer.TryWrite(items);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "Clipboard polling ended");
        }
    }

    /// <summary>
    /// Whatever the pasteboard holds that we understand.
    /// </summary>
    /// <remarks>
    /// The change count says something changed, never what, so both questions get asked on every bump.
    /// Asking whether there is an image is one <c>availableTypeFromArray:</c> and copies nothing, so the
    /// expensive part happens only when there really is a picture.
    /// </remarks>
    private static IReadOnlyList<ClipboardItem> ReadItems()
    {
        var items = new List<ClipboardItem>(2);

        string? text = ReadText();
        if (!string.IsNullOrEmpty(text))
        {
            items.Add(new ClipboardItem(ClipboardItemFormat.Text, Encoding.UTF8.GetBytes(text)));
        }

        if (MacShim.fd_clipboard_has_image() == 1 && ReadImage() is { } png)
        {
            items.Add(new ClipboardItem(ClipboardItemFormat.ImagePng, png));
        }

        return items;
    }

    /// <summary>The pasteboard's image as PNG, or null when there is none or it is too big to carry.</summary>
    private static byte[]? ReadImage()
    {
        if (MacShim.fd_clipboard_read_png(out nint data, out int len) != 1 || data == 0)
        {
            return null;
        }

        try
        {
            // Mirrors ProtocolConstants.MaxClipboardBytes, which platform projects may not reference.
            // Refusing here saves copying something that would only be dropped further up.
            const int maxImageBytes = 16 * 1024 * 1024;
            if (len <= 0 || len > maxImageBytes)
            {
                return null;
            }

            byte[] png = new byte[len];
            Marshal.Copy(data, png, 0, len);
            return png;
        }
        finally
        {
            MacShim.fd_free(data);
        }
    }

    private static string? ReadText()
    {
        nint ptr = MacShim.fd_clipboard_read_text();
        if (ptr == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(ptr);
        }
        finally
        {
            MacShim.fd_free(ptr);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _cts.Cancel();
            try
            {
                await _poller.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The poller observes cancellation; nothing to surface on teardown.
            }

            if (_promises is not null)
            {
                await _promises.DisposeAsync().ConfigureAwait(false);
                _promises = null;
            }

            _changes.Writer.TryComplete();
            _cts.Dispose();
        }
    }
}
