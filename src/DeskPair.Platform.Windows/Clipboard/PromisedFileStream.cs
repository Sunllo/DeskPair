using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Clipboard;

/// <summary>
/// The <c>IStream</c> Explorer drains for one promised file. It tails the staging file as the transfer writes
/// it, so the paste starts moving before the download has finished.
///
/// Two things here are load-bearing and easy to get wrong.
///
/// <c>Read</c> arrives on our STA thread by RPC, so a plain blocking wait for more bytes stops the message
/// loop that services the very call it is inside — the paste would hang forever and so would Cancel, because
/// Explorer's Release is itself an RPC. Every wait goes through <see cref="Wait"/>, which dispatches. No lock
/// may be held across it.
///
/// The file is opened <c>FileShare.ReadWrite | FileShare.Delete</c>. Delete is not optional: the transfer
/// engine renames its <c>.sunllo-part</c> file to the final name while we hold this handle, and without
/// Delete that rename fails.
/// </summary>
[GeneratedComClass]
internal sealed partial class PromisedFileStream : IStreamNative, IDisposable
{
    /// <summary>How long to wait for the writer to produce more before declaring the transfer stuck.</summary>
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(120);

    private const uint SliceMs = 200;

    private readonly string _path;
    private readonly long _expectedSize;
    private readonly Task _download;
    private readonly CancellationToken _ct;
    private readonly ILogger _log;
    private readonly nint _idle;

    private FileStream? _file;
    private long _position;
    private bool _disposed;

    public PromisedFileStream(string path, long expectedSize, Task download, CancellationToken ct, ILogger log)
    {
        _path = path;
        _expectedSize = expectedSize;
        _download = download;
        _ct = ct;
        _log = log;
        _idle = Kernel32.CreateEventW(0, manualReset: true, initialState: false, null);
    }

    public int Read(nint pv, uint cb, nint pcbRead)
    {
        if (pcbRead != 0)
        {
            Marshal.WriteInt32(pcbRead, 0);
        }

        if (pv == 0)
        {
            return Ole32.STG_E_INVALIDPOINTER;
        }

        try
        {
            int read = ReadCore(pv, cb);
            if (pcbRead != 0)
            {
                Marshal.WriteInt32(pcbRead, read);
            }

            return Ole32.S_OK;
        }
        catch (OperationCanceledException)
        {
            return Ole32.STG_E_READFAULT;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Reading promised file {Path} failed", _path);
            return Ole32.STG_E_READFAULT;
        }
    }

    private int ReadCore(nint destination, uint count)
    {
        int total = 0;
        byte[] buffer = new byte[Math.Min(count, 64 * 1024)];

        while (total < count)
        {
            _ct.ThrowIfCancellationRequested();

            FileStream file = Open();
            file.Position = _position;
            int n = file.Read(buffer, 0, (int)Math.Min((uint)buffer.Length, count - (uint)total));
            if (n > 0)
            {
                Marshal.Copy(buffer, 0, destination + total, n);
                _position += n;
                total += n;
                continue;
            }

            // Nothing there yet. Either the file is finished, or the transfer has not caught up.
            if (_download.IsCompleted)
            {
                // Surfaces a failed transfer as a failed read rather than as a silently short file.
                _download.GetAwaiter().GetResult();
                break;
            }

            if (!WaitForMore())
            {
                throw new TimeoutException($"The transfer of {_path} produced nothing for {Stall.TotalSeconds:0} s.");
            }
        }

        return total;
    }

    /// <summary>Opens the staging file, tolerating the window before the transfer has created it.</summary>
    private FileStream Open()
    {
        if (_file is not null)
        {
            return _file;
        }

        while (true)
        {
            try
            {
                _file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return _file;
            }
            catch (FileNotFoundException) when (!_download.IsCompleted)
            {
                if (!WaitForMore())
                {
                    throw new TimeoutException($"The transfer of {_path} never created the file.");
                }
            }
            catch (DirectoryNotFoundException) when (!_download.IsCompleted)
            {
                if (!WaitForMore())
                {
                    throw new TimeoutException($"The transfer of {_path} never created the file.");
                }
            }
        }
    }

    /// <summary>
    /// Waits for the writer to get further, dispatching COM calls throughout. Returns false once nothing has
    /// arrived for <see cref="Stall"/>, so a dead transfer fails the paste instead of hanging it.
    /// </summary>
    private bool WaitForMore()
    {
        long before = Length();
        long deadline = Environment.TickCount64 + (long)Stall.TotalMilliseconds;

        while (Environment.TickCount64 < deadline)
        {
            _ct.ThrowIfCancellationRequested();
            Wait(SliceMs);

            if (_download.IsCompleted || Length() != before)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The pumping wait. Nothing may hold a lock across this.</summary>
    private void Wait(uint milliseconds)
    {
        int hr = Ole32.CoWaitForMultipleHandles(
            Ole32.COWAIT_DISPATCH_CALLS | Ole32.COWAIT_DISPATCH_WINDOW_MESSAGES,
            milliseconds,
            1,
            [_idle],
            out _);

        if (hr is not (Ole32.S_OK or Ole32.RPC_S_CALLPENDING))
        {
            // Not an STA thread, or OLE is shutting down. A plain sleep is wrong here but better than a spin.
            Thread.Sleep((int)milliseconds);
        }
    }

    private long Length()
    {
        try
        {
            return _file?.Length ?? new FileInfo(_path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    public int Stat(nint stat, uint flag)
    {
        if (stat == 0)
        {
            return Ole32.E_POINTER;
        }

        var value = new STATSTG
        {
            type = Ole32.STGTY_STREAM,
            cbSize = (ulong)Math.Max(0, _expectedSize),
            grfMode = 0,
            pwcsName = flag == 1 /* STATFLAG_NONAME */ ? 0 : Marshal.StringToCoTaskMemUni(Path.GetFileName(_path)),
        };

        Marshal.StructureToPtr(value, stat, fDeleteOld: false);
        return Ole32.S_OK;
    }

    public int Seek(long move, uint origin, nint newPosition)
    {
        long target = origin switch
        {
            0 => move,                       // STREAM_SEEK_SET
            1 => _position + move,           // STREAM_SEEK_CUR
            2 => _expectedSize + move,       // STREAM_SEEK_END
            _ => -1,
        };

        if (target < 0)
        {
            return Ole32.STG_E_INVALIDFUNCTION;
        }

        _position = target;
        if (newPosition != 0)
        {
            Marshal.WriteInt64(newPosition, target);
        }

        return Ole32.S_OK;
    }

    public int Write(nint pv, uint cb, nint pcbWritten) => Ole32.STG_E_INVALIDFUNCTION;

    public int SetSize(ulong size) => Ole32.STG_E_INVALIDFUNCTION;

    public int CopyTo(nint destination, ulong cb, nint read, nint written) => Ole32.E_NOTIMPL;

    public int Commit(uint flags) => Ole32.S_OK;

    public int Revert() => Ole32.S_OK;

    public int LockRegion(ulong offset, ulong cb, uint type) => Ole32.E_NOTIMPL;

    public int UnlockRegion(ulong offset, ulong cb, uint type) => Ole32.E_NOTIMPL;

    public int Clone(out nint clone)
    {
        clone = 0;
        return Ole32.E_NOTIMPL;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _file?.Dispose();
        _file = null;
        if (_idle != 0)
        {
            Kernel32.CloseHandle(_idle);
        }
    }
}
