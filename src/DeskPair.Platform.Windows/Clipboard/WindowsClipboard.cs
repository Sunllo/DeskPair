using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Clipboard;
using DeskPair.Platform.Abstractions.Imaging;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Clipboard;

/// <summary>
/// Win32 clipboard with change notifications from <c>AddClipboardFormatListener</c> on a hidden
/// message-only window. Text, HTML ("HTML Format"), RTF and images (registered "PNG" or CF_DIB) are exchanged.
/// Own writes are recognised by their clipboard sequence number and not echoed as changes.
/// </summary>
public sealed class WindowsClipboard : IClipboard, IFilePromiseClipboard
{
    /// <summary>
    /// One window class per instance. A process runs two of these when it is a host and a controller at
    /// once (the engine's, and the one a remote session watches for things to send), and a class registered
    /// once carries the first instance's WndProc: the second instance's window then delivered every
    /// clipboard change to the first object, and the controller never sent a thing while it still received
    /// everything the host copied.
    /// </summary>
    private readonly string _className = $"DeskPairClipboard.{Guid.NewGuid():N}";
    private const int OpenRetries = 10;
    private const int ErrorClassAlreadyExists = 1410;

    private static readonly uint HtmlFormat = User32.RegisterClipboardFormatW("HTML Format");
    private static readonly uint RtfFormat = User32.RegisterClipboardFormatW("Rich Text Format");
    private static readonly uint PngFormat = User32.RegisterClipboardFormatW("PNG");

    private readonly ILogger _log;
    private readonly Channel<IReadOnlyList<ClipboardItem>> _changes = Channel.CreateBounded<IReadOnlyList<ClipboardItem>>(new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Thread _pump;
    private readonly TaskCompletionSource<nint> _windowReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly User32.WndProc _wndProc; // kept alive for the window's lifetime
    private nint _hwnd;
    private int _ownSequence = -1;
    private bool _disposed;

    public WindowsClipboard(ILogger log)
    {
        _log = log;
        _wndProc = WindowProc;
        _pump = new Thread(MessageLoop) { Name = "clipboard-listener", IsBackground = true };
        _pump.SetApartmentState(ApartmentState.STA);
        _pump.Start();
        _hwnd = _windowReady.Task.GetAwaiter().GetResult();
    }

    public ChannelReader<IReadOnlyList<ClipboardItem>> Changes => _changes.Reader;

    public ValueTask<IReadOnlyList<ClipboardItem>> ReadAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ValueTask.FromResult(Read());
    }

    public ValueTask WriteAsync(IReadOnlyList<ClipboardItem> items, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Write(items);
        return ValueTask.CompletedTask;
    }

    private IReadOnlyList<ClipboardItem> Read()
    {
        if (!TryOpen())
        {
            return [];
        }

        try
        {
            var items = new List<ClipboardItem>(3);
            if (User32.IsClipboardFormatAvailable(User32.CF_UNICODETEXT) != 0 && ReadGlobal(User32.CF_UNICODETEXT) is { } utf16)
            {
                string text = Encoding.Unicode.GetString(utf16);
                int nul = text.IndexOf('\0');
                items.Add(new ClipboardItem(ClipboardItemFormat.Text, Encoding.UTF8.GetBytes(nul >= 0 ? text[..nul] : text)));
            }

            if (User32.IsClipboardFormatAvailable(HtmlFormat) != 0 && ReadGlobal(HtmlFormat) is { } html)
            {
                items.Add(new ClipboardItem(ClipboardItemFormat.Html, Encoding.UTF8.GetBytes(StripHtmlHeader(html))));
            }

            if (User32.IsClipboardFormatAvailable(RtfFormat) != 0 && ReadGlobal(RtfFormat) is { } rtf)
            {
                int nul = Array.IndexOf(rtf, (byte)0);
                items.Add(new ClipboardItem(ClipboardItemFormat.Rtf, nul >= 0 ? rtf.AsMemory(0, nul) : rtf));
            }

            if (User32.IsClipboardFormatAvailable(PngFormat) != 0 && ReadGlobal(PngFormat) is { } png && PngCodec.IsPng(png))
            {
                items.Add(new ClipboardItem(ClipboardItemFormat.ImagePng, png));
            }
            else if (User32.IsClipboardFormatAvailable(User32.CF_DIB) != 0 && ReadGlobal(User32.CF_DIB) is { } dib && DibToPng(dib) is { } encoded)
            {
                items.Add(new ClipboardItem(ClipboardItemFormat.ImagePng, encoded));
            }

            return items;
        }
        finally
        {
            User32.CloseClipboard();
        }
    }

    private void Write(IReadOnlyList<ClipboardItem> items)
    {
        // Anything we had promised is gone the moment something else is copied, and OLE has to be told before
        // EmptyClipboard takes the clipboard out from under it: mixing the two corrupts OLE's bookkeeping.
        _ole?.Clear();

        if (!TryOpen())
        {
            throw new InvalidOperationException("The clipboard is held by another application.");
        }

        try
        {
            User32.EmptyClipboard();
            foreach (ClipboardItem item in items)
            {
                switch (item.Format)
                {
                    case ClipboardItemFormat.Text:
                        SetGlobal(User32.CF_UNICODETEXT, Encoding.Unicode.GetBytes(Encoding.UTF8.GetString(item.Payload.Span) + "\0"));
                        break;
                    case ClipboardItemFormat.Html:
                        SetGlobal(HtmlFormat, BuildHtmlFormat(Encoding.UTF8.GetString(item.Payload.Span)));
                        break;
                    case ClipboardItemFormat.Rtf:
                        byte[] rtf = new byte[item.Payload.Length + 1];
                        item.Payload.Span.CopyTo(rtf);
                        SetGlobal(RtfFormat, rtf);
                        break;
                    case ClipboardItemFormat.ImagePng:
                        SetGlobal(PngFormat, item.Payload.Span);
                        try
                        {
                            SetGlobal(User32.CF_DIB, ToDib(PngCodec.Decode(item.Payload.Span)));
                        }
                        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
                        {
                            _log.LogDebug(e, "Image placed on the clipboard as PNG only");
                        }

                        break;
                }
            }
        }
        finally
        {
            User32.CloseClipboard();
            Volatile.Write(ref _ownSequence, User32.GetClipboardSequenceNumber());
        }
    }

    /// <summary>
    /// The native clipboard format an item goes out as, or 0 for one that has none. Shared with
    /// <see cref="PromisedDataObject"/> so a promise-carrying write offers exactly the formats a plain
    /// write does.
    /// </summary>
    internal static ushort NativeFormatOf(ClipboardItemFormat format) => format switch
    {
        ClipboardItemFormat.Text => (ushort)User32.CF_UNICODETEXT,
        ClipboardItemFormat.Html => (ushort)HtmlFormat,
        ClipboardItemFormat.Rtf => (ushort)RtfFormat,
        ClipboardItemFormat.ImagePng => (ushort)PngFormat,
        _ => 0,
    };

    /// <summary>The bytes for that format: UTF-16 for text, the CF_HTML header for HTML, NUL-terminated RTF.</summary>
    internal static byte[] NativePayloadOf(ClipboardItem item) => item.Format switch
    {
        ClipboardItemFormat.Text => Encoding.Unicode.GetBytes(Encoding.UTF8.GetString(item.Payload.Span) + "\0"),
        ClipboardItemFormat.Html => BuildHtmlFormat(Encoding.UTF8.GetString(item.Payload.Span)),
        ClipboardItemFormat.Rtf => NulTerminated(item.Payload.Span),
        _ => item.Payload.ToArray(),
    };

    private static byte[] NulTerminated(ReadOnlySpan<byte> payload)
    {
        byte[] result = new byte[payload.Length + 1];
        payload.CopyTo(result);
        return result;
    }

    // Created on first use: a session that never pastes files never starts an extra STA thread.
    private OleClipboardThread? _ole;

    /// <summary>Windows can hand out a stream for a file it does not have, so a promise is always keepable.</summary>
    public bool CanPromiseFiles => !_disposed;

    /// <summary>
    /// What the user copied in Explorer, read from CF_HDROP. We never publish HDROP ourselves, but every
    /// file manager does, and it is the only thing that says "these files were copied".
    /// </summary>
    public ValueTask<IReadOnlyList<string>> ReadCopiedFilePathsAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (User32.IsClipboardFormatAvailable(User32.CF_HDROP) == 0 || !TryOpen())
        {
            return ValueTask.FromResult<IReadOnlyList<string>>([]);
        }

        try
        {
            nint handle = User32.GetClipboardData(User32.CF_HDROP);
            if (handle == 0)
            {
                return ValueTask.FromResult<IReadOnlyList<string>>([]);
            }

            nint drop = Kernel32.GlobalLock(handle);
            if (drop == 0)
            {
                return ValueTask.FromResult<IReadOnlyList<string>>([]);
            }

            try
            {
                uint count = User32.DragQueryFileW(drop, 0xFFFFFFFF, null, 0);
                var paths = new List<string>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    uint chars = User32.DragQueryFileW(drop, i, null, 0);
                    if (chars == 0)
                    {
                        continue;
                    }

                    char[] buffer = new char[chars + 1];
                    uint written = User32.DragQueryFileW(drop, i, buffer, (uint)buffer.Length);
                    if (written > 0)
                    {
                        paths.Add(new string(buffer, 0, (int)written));
                    }
                }

                return ValueTask.FromResult<IReadOnlyList<string>>(paths);
            }
            finally
            {
                Kernel32.GlobalUnlock(handle);
            }
        }
        finally
        {
            User32.CloseClipboard();
        }
    }

    public ValueTask WriteWithPromiseAsync(
        IReadOnlyList<ClipboardItem> items,
        FilePromiseListing listing,
        IFilePromiseSource source,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A write carrying files takes the OLE path for the whole write. Mixing OleSetClipboard with
        // SetClipboardData corrupts OLE's bookkeeping, so the fork is all or nothing.
        OleClipboardThread ole = _ole ??= new OleClipboardThread(_log);
        var data = new PromisedDataObject(source, items, _log);
        ole.Publish(data);
        Volatile.Write(ref _ownSequence, User32.GetClipboardSequenceNumber());

        _log.LogInformation(
            "Offering {Count} promised file(s) on the clipboard (token {Token})",
            listing.Entries.Count,
            listing.Token);
        return ValueTask.CompletedTask;
    }

    private bool TryOpen()
    {
        for (int i = 0; i < OpenRetries; i++)
        {
            if (User32.OpenClipboard(_hwnd) != 0)
            {
                return true;
            }

            Thread.Sleep(10);
        }

        _log.LogWarning("OpenClipboard failed: {Error}", Marshal.GetLastPInvokeError());
        return false;
    }

    private static byte[]? ReadGlobal(uint format)
    {
        nint h = User32.GetClipboardData(format);
        if (h == 0)
        {
            return null;
        }

        nint p = Kernel32.GlobalLock(h);
        if (p == 0)
        {
            return null;
        }

        try
        {
            int size = checked((int)Kernel32.GlobalSize(h));
            byte[] data = new byte[size];
            Marshal.Copy(p, data, 0, size);
            return data;
        }
        finally
        {
            Kernel32.GlobalUnlock(h);
        }
    }

    private static void SetGlobal(uint format, ReadOnlySpan<byte> data)
    {
        nint h = Kernel32.GlobalAlloc(Kernel32.GMEM_MOVEABLE, (nuint)data.Length);
        if (h == 0)
        {
            throw new OutOfMemoryException("GlobalAlloc failed.");
        }

        nint p = Kernel32.GlobalLock(h);
        try
        {
            unsafe
            {
                data.CopyTo(new Span<byte>((void*)p, data.Length));
            }
        }
        finally
        {
            Kernel32.GlobalUnlock(h);
        }

        if (User32.SetClipboardData(format, h) == 0)
        {
            Kernel32.GlobalFree(h);
            throw new InvalidOperationException($"SetClipboardData({format}) failed: {Marshal.GetLastPInvokeError()}");
        }
    }

    // ---- HTML Format ----

    /// <summary>Extracts the fragment (or the whole document) from a CF_HTML payload.</summary>
    internal static string StripHtmlHeader(byte[] payload)
    {
        int nul = Array.IndexOf(payload, (byte)0);
        ReadOnlySpan<byte> bytes = nul >= 0 ? payload.AsSpan(0, nul) : payload;
        string ascii = Encoding.ASCII.GetString(bytes);
        int start = HeaderValue(ascii, "StartFragment:");
        int end = HeaderValue(ascii, "EndFragment:");
        if (start < 0 || end < 0 || end > bytes.Length || start >= end)
        {
            start = HeaderValue(ascii, "StartHTML:");
            end = HeaderValue(ascii, "EndHTML:");
        }

        if (start < 0 || end < 0 || end > bytes.Length || start >= end)
        {
            int body = ascii.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
            return Encoding.UTF8.GetString(body >= 0 ? bytes[body..] : bytes);
        }

        return Encoding.UTF8.GetString(bytes[start..end]);
    }

    private static int HeaderValue(string header, string key)
    {
        int i = header.IndexOf(key, StringComparison.Ordinal);
        if (i < 0)
        {
            return -1;
        }

        int j = i + key.Length;
        int k = j;
        while (k < header.Length && char.IsDigit(header[k]))
        {
            k++;
        }

        return k > j && int.TryParse(header.AsSpan(j, k - j), out int value) ? value : -1;
    }

    /// <summary>Wraps a fragment in the CF_HTML envelope with byte offsets.</summary>
    internal static byte[] BuildHtmlFormat(string fragment)
    {
        const string Prefix = "<html><body><!--StartFragment-->";
        const string Suffix = "<!--EndFragment--></body></html>";
        const string Template = "Version:0.9\r\nStartHTML:0000000000\r\nEndHTML:0000000000\r\nStartFragment:0000000000\r\nEndFragment:0000000000\r\n";
        int startHtml = Encoding.UTF8.GetByteCount(Template);
        int startFragment = startHtml + Encoding.UTF8.GetByteCount(Prefix);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(Suffix);
        string header = $"Version:0.9\r\nStartHTML:{startHtml:D10}\r\nEndHTML:{endHtml:D10}\r\nStartFragment:{startFragment:D10}\r\nEndFragment:{endFragment:D10}\r\n";
        return Encoding.UTF8.GetBytes(header + Prefix + fragment + Suffix + "\0");
    }

    // ---- DIB <-> PNG ----

    /// <summary>Converts a packed CF_DIB (BITMAPINFOHEADER, 24/32 bpp, uncompressed or BI_BITFIELDS) to PNG.</summary>
    internal static byte[]? DibToPng(byte[] dib)
    {
        if (dib.Length < 40)
        {
            return null;
        }

        ReadOnlySpan<byte> s = dib;
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(s);
        int width = BinaryPrimitives.ReadInt32LittleEndian(s[4..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(s[8..]);
        int bpp = BinaryPrimitives.ReadUInt16LittleEndian(s[14..]);
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(s[16..]);
        if (width <= 0 || height == 0 || bpp is not (24 or 32) || (compression != 0 && compression != 3))
        {
            return null;
        }

        bool topDown = height < 0;
        height = Math.Abs(height);
        int pixelOffset = headerSize + (compression == 3 && headerSize == 40 ? 12 : 0);
        int srcStride = (width * bpp / 8 + 3) & ~3;
        if (dib.Length < pixelOffset + srcStride * height)
        {
            return null;
        }

        byte[] bgra = new byte[width * height * 4];
        bool anyAlpha = false;
        int bytesPerPixel = bpp / 8;
        for (int y = 0; y < height; y++)
        {
            int srcRow = pixelOffset + (topDown ? y : height - 1 - y) * srcStride;
            int dstRow = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int si = srcRow + x * bytesPerPixel;
                bgra[dstRow + x * 4] = dib[si];
                bgra[dstRow + x * 4 + 1] = dib[si + 1];
                bgra[dstRow + x * 4 + 2] = dib[si + 2];
                byte a = bpp == 32 ? dib[si + 3] : (byte)255;
                anyAlpha |= a != 0;
                bgra[dstRow + x * 4 + 3] = a;
            }
        }

        if (!anyAlpha)
        {
            // 32-bpp DIBs frequently carry a zero alpha channel that means "opaque".
            for (int i = 3; i < bgra.Length; i += 4)
            {
                bgra[i] = 255;
            }
        }

        return PngCodec.Encode(bgra, width * 4, width, height);
    }

    /// <summary>Packs a BGRA image as a bottom-up 32-bpp CF_DIB.</summary>
    internal static byte[] ToDib(BgraImage image)
    {
        int stride = image.Width * 4;
        byte[] dib = new byte[40 + stride * image.Height];
        Span<byte> h = dib;
        BinaryPrimitives.WriteInt32LittleEndian(h, 40);
        BinaryPrimitives.WriteInt32LittleEndian(h[4..], image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(h[8..], image.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(h[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h[14..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(h[20..], stride * image.Height);
        for (int y = 0; y < image.Height; y++)
        {
            image.Pixels.AsSpan(y * stride, stride).CopyTo(dib.AsSpan(40 + (image.Height - 1 - y) * stride, stride));
        }

        return dib;
    }

    // ---- message window ----

    private void MessageLoop()
    {
        nint instance = Kernel32.GetModuleHandleW(null);
        nint className = Marshal.StringToHGlobalUni(_className);
        try
        {
            var wc = new User32.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<User32.WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = instance,
                lpszClassName = className,
            };
            if (User32.RegisterClassExW(ref wc) == 0 && Marshal.GetLastPInvokeError() != ErrorClassAlreadyExists)
            {
                _windowReady.TrySetException(new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastPInvokeError()}"));
                return;
            }

            nint hwnd = User32.CreateWindowExW(0, _className, "Sunllo DeskPair clipboard", 0, 0, 0, 0, 0, User32.HWND_MESSAGE, 0, instance, 0);
            if (hwnd == 0)
            {
                _windowReady.TrySetException(new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}"));
                return;
            }

            if (User32.AddClipboardFormatListener(hwnd) == 0)
            {
                _log.LogWarning("AddClipboardFormatListener failed: {Error}", Marshal.GetLastPInvokeError());
            }

            _windowReady.TrySetResult(hwnd);
            while (User32.GetMessageW(out User32.MSG msg, 0, 0, 0) > 0)
            {
                User32.TranslateMessage(ref msg);
                User32.DispatchMessageW(ref msg);
            }
        }
        finally
        {
            User32.UnregisterClassW(_className, instance);
            Marshal.FreeHGlobal(className);
        }
    }

    private nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case User32.WM_CLIPBOARDUPDATE:
                if (User32.GetClipboardSequenceNumber() != Volatile.Read(ref _ownSequence))
                {
                    try
                    {
                        _changes.Writer.TryWrite(Read());
                    }
                    catch (Exception e)
                    {
                        _log.LogDebug(e, "Reading the clipboard after a change failed");
                    }
                }

                return 0;
            case User32.WM_CLOSE:
                User32.RemoveClipboardFormatListener(hwnd);
                User32.DestroyWindow(hwnd);
                return 0;
            case User32.WM_DESTROY:
                User32.PostMessageW(0, User32.WM_QUIT, 0, 0);
                return 0;
        }

        return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _changes.Writer.TryComplete();
        if (_hwnd != 0)
        {
            User32.PostMessageW(_hwnd, User32.WM_CLOSE, 0, 0);
            _hwnd = 0;
        }

        _pump.Join(TimeSpan.FromSeconds(2));

        // Takes the promise off the clipboard rather than rendering it: OleFlushClipboard here would download
        // the whole selection on the way out.
        _ole?.Dispose();
        _ole = null;
        return ValueTask.CompletedTask;
    }
}
