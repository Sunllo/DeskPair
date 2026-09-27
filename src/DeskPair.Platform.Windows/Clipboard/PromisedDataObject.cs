using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Clipboard;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Clipboard;

/// <summary>
/// What the Windows clipboard holds while this machine is offering files it has not downloaded. Explorer
/// reads <c>CFSTR_FILEDESCRIPTORW</c> to learn what it is about to get, then pulls each file's bytes as an
/// <c>IStream</c> through <c>CFSTR_FILECONTENTS</c> indexed by <c>lindex</c>.
///
/// It never advertises <c>CF_HDROP</c>. Explorer prefers HDROP when both are present, and HDROP is a list of
/// paths — it would copy from files that do not exist.
///
/// Nothing is fetched until a paste asks. The descriptor is built on the first <c>GetData</c>, which is also
/// where a copied directory is walked, so copying a large tree costs nothing until someone pastes it.
/// </summary>
[GeneratedComClass]
internal sealed partial class PromisedDataObject : IDataObjectNative, IDisposable
{
    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint FD_FILESIZE = 0x40;
    private const uint FD_WRITESTIME = 0x20;
    private const uint FD_PROGRESSUI = 0x4000;
    private const int DescriptorBytes = 592;
    private const int MaxFileNameChars = 260;
    private const uint DROPEFFECT_COPY = 2;

    private static readonly uint CfFileDescriptor = User32.RegisterClipboardFormatW("FileGroupDescriptorW");
    private static readonly uint CfFileContents = User32.RegisterClipboardFormatW("FileContents");
    private static readonly uint CfPreferredDropEffect = User32.RegisterClipboardFormatW("Preferred DropEffect");

    private readonly IFilePromiseSource _source;
    private readonly IReadOnlyList<ClipboardItem> _items;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private readonly List<PromisedFileStream> _streams = [];
    private readonly Dictionary<int, Task> _downloads = [];

    private IReadOnlyList<FilePromiseEntry>? _files;
    private bool _disposed;

    public PromisedDataObject(IFilePromiseSource source, IReadOnlyList<ClipboardItem> items, ILogger log)
    {
        _source = source;
        _items = items;
        _log = log;
    }

    /// <summary>How many times something actually pulled a file's bytes. For diagnostics and tests.</summary>
    public int ContentsServed { get; private set; }

    public int QueryGetData(ref FORMATETC format)
    {
        if (format.dwAspect != Ole32.DVASPECT_CONTENT)
        {
            return Ole32.DV_E_DVASPECT;
        }

        if (format.cfFormat == CfFileDescriptor || format.cfFormat == CfPreferredDropEffect)
        {
            return (format.tymed & Ole32.TYMED_HGLOBAL) != 0 ? Ole32.S_OK : Ole32.DV_E_TYMED;
        }

        if (format.cfFormat == CfFileContents)
        {
            if ((format.tymed & Ole32.TYMED_ISTREAM) == 0)
            {
                return Ole32.DV_E_TYMED;
            }

            // -1 is the "any index" probe OLE uses while walking EnumFormatEtc. Refusing it there makes
            // OleSetClipboard fail with a bare E_FAIL and no clue why.
            return format.lindex is -1 or >= 0 ? Ole32.S_OK : Ole32.DV_E_LINDEX;
        }

        return MatchItem(format.cfFormat) is not null && (format.tymed & Ole32.TYMED_HGLOBAL) != 0
            ? Ole32.S_OK
            : Ole32.DV_E_FORMATETC;
    }

    public int GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        int check = QueryGetData(ref format);
        if (check != Ole32.S_OK)
        {
            return check;
        }

        try
        {
            if (format.cfFormat == CfFileDescriptor)
            {
                return AsGlobal(BuildDescriptor(), ref medium);
            }

            if (format.cfFormat == CfPreferredDropEffect)
            {
                return AsGlobal(BitConverter.GetBytes(DROPEFFECT_COPY), ref medium);
            }

            if (format.cfFormat == CfFileContents)
            {
                return AsStream(format.lindex, ref medium);
            }

            ClipboardItem? item = MatchItem(format.cfFormat);
            return item is null
                ? Ole32.DV_E_FORMATETC
                : AsGlobal(WindowsClipboard.NativePayloadOf(item), ref medium);
        }
        catch (OperationCanceledException)
        {
            return Ole32.DV_E_FORMATETC;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Serving clipboard format {Format} failed", format.cfFormat);
            return Ole32.DV_E_FORMATETC;
        }
    }

    /// <summary>
    /// FILEGROUPDESCRIPTORW: a uint count, then one 592-byte FILEDESCRIPTORW per file. Setting FD_FILESIZE
    /// with a correct high/low split is what makes the progress dialog right from its first frame rather
    /// than counting up from an unknown total.
    /// </summary>
    private byte[] BuildDescriptor()
    {
        IReadOnlyList<FilePromiseEntry> files = Files();
        byte[] buffer = new byte[4 + (DescriptorBytes * files.Count)];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), (uint)files.Count);

        for (int i = 0; i < files.Count; i++)
        {
            FilePromiseEntry file = files[i];
            Span<byte> entry = buffer.AsSpan(4 + (i * DescriptorBytes), DescriptorBytes);

            BitConverter.TryWriteBytes(entry[..4], FD_FILESIZE | FD_WRITESTIME | FD_PROGRESSUI);
            BitConverter.TryWriteBytes(entry.Slice(56, 8), file.Modified.ToFileTime());              // ftLastWriteTime
            BitConverter.TryWriteBytes(entry.Slice(64, 4), (uint)(file.Size >> 32));                 // nFileSizeHigh
            BitConverter.TryWriteBytes(entry.Slice(68, 4), (uint)(file.Size & 0xFFFFFFFF));          // nFileSizeLow

            // cFileName is MAX_PATH wide characters and Explorer reads it as a path relative to the drop
            // target, with backslashes. The decoder already refused anything that would not fit.
            string name = file.RelativePath.Replace('/', '\\');
            int chars = Math.Min(name.Length, MaxFileNameChars - 1);
            Encoding.Unicode.GetBytes(name.AsSpan(0, chars), entry.Slice(72, chars * 2));
        }

        return buffer;
    }

    /// <summary>Hands out one <see cref="PromisedFileStream"/> per file, starting its download the first time.</summary>
    private int AsStream(int index, ref STGMEDIUM medium)
    {
        IReadOnlyList<FilePromiseEntry> files = Files();
        if (index < 0 || index >= files.Count)
        {
            return Ole32.DV_E_LINDEX;
        }

        FilePromiseEntry file = files[index];
        string path = _source.StagedPathFor(file.Id);

        Task download;
        lock (_gate)
        {
            if (!_downloads.TryGetValue(index, out download!))
            {
                CancellationToken ct = _cts.Token;
                download = Task.Run(() => _source.FetchAsync(file.Id, path, null, ct), ct);
                _downloads[index] = download;
            }

            ContentsServed++;
        }

        var stream = new PromisedFileStream(path, file.Size, download, _cts.Token, _log);
        lock (_gate)
        {
            _streams.Add(stream);
        }

        medium.tymed = Ole32.TYMED_ISTREAM;
        unsafe
        {
            medium.unionmember = (nint)ComInterfaceMarshaller<IStreamNative>.ConvertToUnmanaged(stream);
        }

        medium.pUnkForRelease = 0;  // the caller owns the reference we just handed over
        return Ole32.S_OK;
    }

    /// <summary>
    /// Resolves the promise into the files actually on offer. This blocks the calling thread, which is the
    /// paste, and it is the first thing a paste does — so the wait is visible where it belongs rather than
    /// happening at copy time when nobody asked for it.
    /// </summary>
    private IReadOnlyList<FilePromiseEntry> Files()
    {
        if (_files is not null)
        {
            return _files;
        }

        // No lock is held across this: another format may be asked for on another thread meanwhile.
        IReadOnlyList<FilePromiseEntry> resolved = _source.ResolveAsync(_cts.Token).GetAwaiter().GetResult();
        _files = resolved;
        return resolved;
    }

    private ClipboardItem? MatchItem(ushort format)
    {
        foreach (ClipboardItem item in _items)
        {
            if (WindowsClipboard.NativeFormatOf(item.Format) == format)
            {
                return item;
            }
        }

        return null;
    }

    private static int AsGlobal(byte[] bytes, ref STGMEDIUM medium)
    {
        nint handle = Kernel32.GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
        if (handle == 0)
        {
            return Ole32.E_OUTOFMEMORY;
        }

        nint p = Kernel32.GlobalLock(handle);
        if (p == 0)
        {
            Kernel32.GlobalFree(handle);
            return Ole32.E_OUTOFMEMORY;
        }

        Marshal.Copy(bytes, 0, p, bytes.Length);
        Kernel32.GlobalUnlock(handle);
        medium.tymed = Ole32.TYMED_HGLOBAL;
        medium.unionmember = handle;
        medium.pUnkForRelease = 0;
        return Ole32.S_OK;
    }

    public int EnumFormatEtc(uint direction, out nint enumerator)
    {
        enumerator = 0;
        if (direction != Ole32.DATADIR_GET)
        {
            return Ole32.E_NOTIMPL;
        }

        var formats = new List<FORMATETC>
        {
            new() { cfFormat = (ushort)CfFileDescriptor, dwAspect = Ole32.DVASPECT_CONTENT, lindex = -1, tymed = Ole32.TYMED_HGLOBAL },
            new() { cfFormat = (ushort)CfFileContents, dwAspect = Ole32.DVASPECT_CONTENT, lindex = -1, tymed = Ole32.TYMED_ISTREAM },
            new() { cfFormat = (ushort)CfPreferredDropEffect, dwAspect = Ole32.DVASPECT_CONTENT, lindex = -1, tymed = Ole32.TYMED_HGLOBAL },
        };

        foreach (ClipboardItem item in _items)
        {
            ushort native = WindowsClipboard.NativeFormatOf(item.Format);
            if (native != 0)
            {
                formats.Add(new FORMATETC { cfFormat = native, dwAspect = Ole32.DVASPECT_CONTENT, lindex = -1, tymed = Ole32.TYMED_HGLOBAL });
            }
        }

        return Ole32.SHCreateStdEnumFmtEtc((uint)formats.Count, [.. formats], out enumerator);
    }

    public int GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => Ole32.E_NOTIMPL;

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = 0;
        return Ole32.DATA_S_SAMEFORMATETC;
    }

    /// <summary>Windows itself calls this with its own shell formats; refusing them harms nothing.</summary>
    public int SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) => Ole32.E_NOTIMPL;

    public int DAdvise(ref FORMATETC format, uint advf, nint sink, out uint connection)
    {
        connection = 0;
        return Ole32.OLE_E_ADVISENOTSUPPORTED;
    }

    public int DUnadvise(uint connection) => Ole32.OLE_E_ADVISENOTSUPPORTED;

    public int EnumDAdvise(out nint enumerator)
    {
        enumerator = 0;
        return Ole32.OLE_E_ADVISENOTSUPPORTED;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();

        PromisedFileStream[] streams;
        lock (_gate)
        {
            streams = [.. _streams];
            _streams.Clear();
        }

        foreach (PromisedFileStream stream in streams)
        {
            stream.Dispose();
        }

        _cts.Dispose();
    }
}
