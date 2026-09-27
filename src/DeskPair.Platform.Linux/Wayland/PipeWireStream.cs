using System.Runtime.InteropServices;
using DeskPair.Platform.Linux.Native;

namespace DeskPair.Platform.Linux.Wayland;

/// <summary>One picture from a PipeWire stream, as the shim locked it.</summary>
/// <param name="Data">The first visible pixel.</param>
/// <param name="Width">Pixels.</param>
/// <param name="Height">Pixels.</param>
/// <param name="Stride">Bytes from one row to the next.</param>
/// <param name="Format"><see cref="WaylandShim.FormatBgrx"/> or <see cref="WaylandShim.FormatRgbx"/>.</param>
/// <param name="Sequence">Pictures the stream has produced so far, this one included.</param>
/// <param name="Buffers">How many buffers the stream negotiated.</param>
/// <param name="Borrowable">Whether the pixels stay mapped until the next lock, so they can be handed on uncopied.</param>
/// <param name="Readable">Bytes that may be read from <paramref name="Data"/> onwards.</param>
internal readonly record struct PipeWireFrame(
    nint Data, int Width, int Height, int Stride, int Format, ulong Sequence, int Buffers, bool Borrowable, long Readable);

/// <summary>The pointer as a stream last described it, in that stream's pixels.</summary>
/// <param name="Visible">Whether the pointer is over this stream's monitor.</param>
/// <param name="X">Hotspot position.</param>
/// <param name="Y">Hotspot position.</param>
/// <param name="HotX">Hotspot within the bitmap.</param>
/// <param name="HotY">Hotspot within the bitmap.</param>
/// <param name="Width">Bitmap size; 0 until a shape has arrived.</param>
/// <param name="Height">Bitmap size.</param>
/// <param name="Format"><see cref="WaylandShim.FormatBgra"/> or <see cref="WaylandShim.FormatRgba"/>.</param>
/// <param name="ShapeSerial">Changes whenever the bitmap does.</param>
internal readonly record struct PipeWireCursor(
    bool Visible, int X, int Y, int HotX, int HotY, int Width, int Height, int Format, ulong ShapeSerial);

/// <summary>The PipeWire stream a capturer reads: the shim on a real machine, a fake in tests.</summary>
internal interface IPipeWireStream : IDisposable
{
    /// <summary>Why the stream is over, or null while it is not.</summary>
    string? Failure { get; }

    /// <summary>1: a picture is waiting; 0: none came within <paramref name="timeoutMs"/>; -1: the stream is over.</summary>
    int Wait(int timeoutMs);

    /// <summary>Takes the newest picture, giving back the one locked before it.</summary>
    bool TryLock(out PipeWireFrame frame);

    /// <summary>Gives back the locked picture; its pixels must not be read after this.</summary>
    void Release();

    /// <summary>
    /// The pointer as the stream last described it, the bitmap copied into <paramref name="bitmap"/> when it fits
    /// (width * height * 4 bytes, tight). Safe to call from another thread than the one taking pictures.
    /// </summary>
    PipeWireCursor ReadCursor(Span<byte> bitmap);
}

/// <summary>
/// A stream through <c>libSunlloWaylandShim.so</c>.
///
/// Two threads use one: the capture loop takes pictures, the cursor service reads the pointer. The shim is safe for
/// that; closing is not, because a call already inside the library would be reading freed memory. So every call
/// holds the read side of a lock and closing takes the write side, waiting out whatever is in flight -- at most one
/// wait slice.
/// </summary>
internal sealed class ShimPipeWireStream : IPipeWireStream
{
    private const int ErrorLength = 256;

    private readonly ReaderWriterLockSlim _gate = new();
    private nint _pw;

    private ShimPipeWireStream(nint pw) => _pw = pw;

    public unsafe string? Failure
    {
        get
        {
            byte* error = stackalloc byte[ErrorLength];
            error[0] = 0;
            int state = Use(pw => WaylandShim.dp_pw_state(pw, error, ErrorLength), WaylandShim.StateClosed);
            return state switch
            {
                WaylandShim.StateError => Text(error) ?? "the PipeWire stream failed",
                WaylandShim.StateClosed => "the compositor closed the screen-sharing stream",
                _ => null,
            };
        }
    }

    /// <summary>
    /// Starts a stream from <paramref name="node"/> over <paramref name="remote"/>, the socket the portal's
    /// <c>OpenPipeWireRemote</c> gave. The shim keeps a duplicate, so the caller still owns and closes its own.
    /// </summary>
    /// <exception cref="IOException">The shim is missing, or PipeWire would not start the stream.</exception>
    public static unsafe ShimPipeWireStream Open(SafeHandle remote, uint node)
    {
        if (!WaylandShim.IsAvailable)
        {
            throw new IOException(WaylandShim.UnavailableReason);
        }

        byte* error = stackalloc byte[ErrorLength];
        error[0] = 0;
        bool added = false;
        remote.DangerousAddRef(ref added);
        try
        {
            int result = WaylandShim.dp_pw_open((int)remote.DangerousGetHandle(), node, out nint pw, error, ErrorLength);
            if (result != 0)
            {
                throw new IOException($"PipeWire node {node}: {Text(error) ?? "the stream did not start"} ({result})");
            }

            return new ShimPipeWireStream(pw);
        }
        finally
        {
            if (added)
            {
                remote.DangerousRelease();
            }
        }
    }

    public int Wait(int timeoutMs) => Use(pw => WaylandShim.dp_pw_wait(pw, timeoutMs), -1);

    public bool TryLock(out PipeWireFrame frame)
    {
        WaylandShim.DpFrame f = default;
        if (Use(pw => WaylandShim.dp_pw_lock_frame(pw, out f), 0) != 1)
        {
            frame = default;
            return false;
        }

        frame = new PipeWireFrame(
            f.Data, f.Width, f.Height, f.Stride, f.Format, f.Sequence, f.Buffers, (f.Flags & WaylandShim.FrameBorrowable) != 0, f.Readable);
        return true;
    }

    public void Release() => Use(pw =>
    {
        WaylandShim.dp_pw_release_frame(pw);
        return 0;
    }, 0);

    public unsafe PipeWireCursor ReadCursor(Span<byte> bitmap)
    {
        WaylandShim.DpCursor c = default;
        fixed (byte* p = bitmap)
        {
            byte* target = p;
            int capacity = bitmap.Length;
            if (Use(pw => WaylandShim.dp_pw_cursor(pw, out c, target, capacity), -1) != 0)
            {
                return default;
            }
        }

        return new PipeWireCursor(c.Visible != 0, c.X, c.Y, c.HotX, c.HotY, c.Width, c.Height, c.Format, c.ShapeSerial);
    }

    public void Dispose()
    {
        _gate.EnterWriteLock();
        try
        {
            nint pw = _pw;
            _pw = 0;
            if (pw != 0)
            {
                WaylandShim.dp_pw_close(pw);
            }
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    /// <summary>A call into the shim under the read side of the gate; <paramref name="closed"/> once the stream is gone.</summary>
    private T Use<T>(Func<nint, T> call, T closed)
    {
        _gate.EnterReadLock();
        try
        {
            return _pw == 0 ? closed : call(_pw);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    private static unsafe string? Text(byte* text) => text[0] == 0 ? null : Marshal.PtrToStringUTF8((nint)text);
}
