using System.Buffers;
using System.Diagnostics;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Cursor;
using DeskPair.Platform.Linux.Native;
using Microsoft.Extensions.Logging;

namespace DeskPair.Platform.Linux.Wayland;

/// <summary>
/// One shared monitor, read from its PipeWire stream. The compositor sends a picture when something changed, so
/// unlike the X11 and scanout capturers there is no polling and no comparing: a picture that arrives is new.
///
/// The picture is handed on without copying when it can be. The contract lets the caller keep a frame until the
/// next one is returned, and the shim keeps a locked buffer mapped until exactly then, even through a
/// renegotiation. That needs a buffer to spare -- one held here, one waiting, one for the compositor to draw
/// into -- so with fewer than three, or with memory the shim did not map itself, it is copied out instead and
/// given straight back.
///
/// The size is the engine's contract with the encoder, which was built for <see cref="Display"/>'s size. A picture
/// of another size (the monitor changed mode, or scaling changed) is not delivered: the capturer reports a desktop
/// switch, and tells whoever listens so the display list can be read again with the new size.
/// </summary>
internal sealed class PortalScreenCapturer : IScreenCapturer
{
    /// <summary>The longest single wait, so a cancellation is noticed within this.</summary>
    private const int SliceMs = 100;

    private readonly IPipeWireStream _stream;
    private readonly ILogger _log;
    private readonly Action<DisplayDescriptor, int, int>? _sizeChanged;
    private readonly Action<PortalScreenCapturer>? _onDisposed;
    private readonly object _cursorLock = new();
    private byte[] _cursorBitmap = [];
    private ulong _shapeSerial = ulong.MaxValue;
    private CursorImage? _shape;
    private BorrowedPixels? _borrowed;
    private byte[] _copy = [];
    private byte[] _spare = [];
    private bool _copyOnly;
    private string? _describedMode;
    private double _lastWait;
    private double _lastCopy;
    private bool _disposed;

    public PortalScreenCapturer(
        IPipeWireStream stream, DisplayDescriptor display, ILogger log, Action<DisplayDescriptor, int, int>? sizeChanged = null, Action<PortalScreenCapturer>? disposed = null)
    {
        _onDisposed = disposed;
        _stream = stream;
        _log = log;
        _sizeChanged = sizeChanged;
        Display = display;
    }

    public DisplayDescriptor Display { get; }

    public bool SupportsGpuTexture => false;

    public GpuApi GpuApi => GpuApi.None;

    public (double WaitMs, double ReadbackMs) LastFrameTiming => (_lastWait, _lastCopy);

    public ValueTask<CaptureResult> AcquireFrameAsync(TimeSpan timeout, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            double left = (timeout - Stopwatch.GetElapsedTime(started)).TotalMilliseconds;
            int result = _stream.Wait((int)Math.Clamp(left, 0, SliceMs));
            if (result < 0)
            {
                Invalidate();
                return ValueTask.FromResult(CaptureResult.Failed(new IOException(_stream.Failure ?? "the screen-sharing stream ended")));
            }

            if (result > 0 && _stream.TryLock(out PipeWireFrame frame))
            {
                // Taking this picture gave the last one back to the stream: whatever still points at it must not read it.
                Invalidate();
                _lastWait = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                return ValueTask.FromResult(Deliver(frame));
            }

            if (Stopwatch.GetElapsedTime(started) >= timeout)
            {
                return ValueTask.FromResult(CaptureResult.TimedOut);
            }
        }
    }

    /// <summary>Copies every picture from now on; a borrowed one was somehow the wrong thing to hand on.</summary>
    public void ForceFallbackPath() => _copyOnly = true;

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _onDisposed?.Invoke(this);
            lock (_cursorLock)
            {
                _disposed = true;
            }

            Invalidate();
            _stream.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The pointer when it is over this monitor: its shape, and its hotspot on the engine's virtual screen. Null when
    /// it is on another monitor or hidden. The stream carries it beside the picture (cursor metadata), so it moves
    /// without a new picture and is never painted into one; read from the cursor service's thread, not the capture's.
    /// </summary>
    internal (CursorImage Shape, int X, int Y)? Cursor()
    {
        lock (_cursorLock)
        {
            if (_disposed)
            {
                return null;
            }

            PipeWireCursor cursor = _stream.ReadCursor([]);
            if (!cursor.Visible)
            {
                return null;
            }

            if (cursor.ShapeSerial != _shapeSerial && cursor.Width > 0 && cursor.Height > 0)
            {
                // The bitmap is copied only when the shape changed, not on every poll.
                int size = cursor.Width * cursor.Height * 4;
                if (_cursorBitmap.Length < size)
                {
                    _cursorBitmap = new byte[size];
                }

                cursor = _stream.ReadCursor(_cursorBitmap);
                _shapeSerial = cursor.ShapeSerial;
                _shape = ToImage(cursor, _cursorBitmap);
            }

            return _shape is null ? null : (_shape, Display.X + cursor.X, Display.Y + cursor.Y);
        }
    }

    /// <summary>
    /// A cursor bitmap as the engine wants it: BGRA, tight, premultiplied (the compositor sends it premultiplied
    /// already), with an id made from its content so that a shape seen before -- on this monitor or another --
    /// has the id a viewer has cached it under.
    /// </summary>
    internal static CursorImage? ToImage(in PipeWireCursor cursor, ReadOnlySpan<byte> bitmap)
    {
        int size = cursor.Width * cursor.Height * 4;
        if (cursor.Width <= 0 || cursor.Height <= 0 || bitmap.Length < size)
        {
            return null;
        }

        byte[] bgra = bitmap[..size].ToArray();
        if (cursor.Format == WaylandShim.FormatRgba)
        {
            for (int i = 0; i < bgra.Length; i += 4)
            {
                (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
            }
        }
        else if (cursor.Format != WaylandShim.FormatBgra)
        {
            return null;
        }

        // FNV-1a over the shape and its hotspot; never 0, which the engine reads as "no shape".
        ulong hash = 14695981039346656037UL;
        foreach (int value in (ReadOnlySpan<int>)[cursor.Width, cursor.Height, cursor.HotX, cursor.HotY])
        {
            hash = (hash ^ (uint)value) * 1099511628211UL;
        }

        foreach (byte b in bgra)
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return new CursorImage(hash == 0 ? 1 : hash, cursor.HotX, cursor.HotY, cursor.Width, cursor.Height, bgra);
    }

    private CaptureResult Deliver(in PipeWireFrame frame)
    {
        PixelFormat? format = frame.Format switch
        {
            WaylandShim.FormatBgrx => PixelFormat.Bgra32,
            WaylandShim.FormatRgbx => PixelFormat.Rgba32,
            _ => null,
        };
        if (format is null)
        {
            _stream.Release();
            return CaptureResult.Failed(new IOException($"the stream delivered pixel format {frame.Format}, which this build cannot read"));
        }

        if (frame.Width != Display.Width || frame.Height != Display.Height)
        {
            _stream.Release();
            _log.LogInformation("{Display} now sends {Width}x{Height}, not {OldWidth}x{OldHeight}; rebuilding the capturer",
                Display.Name, frame.Width, frame.Height, Display.Width, Display.Height);
            _sizeChanged?.Invoke(Display, frame.Width, frame.Height);
            return CaptureResult.Switched;
        }

        long needed = (long)frame.Stride * frame.Height;
        bool borrow = !_copyOnly && frame.Borrowable && frame.Readable >= needed && needed <= int.MaxValue;
        Describe(frame, borrow);

        ReadOnlyMemory<byte> pixels;
        int stride;
        if (borrow)
        {
            _borrowed = new BorrowedPixels(frame.Data, (int)needed);
            pixels = _borrowed.Memory;
            stride = frame.Stride;
            _lastCopy = 0;
        }
        else
        {
            long t = Stopwatch.GetTimestamp();
            stride = frame.Width * 4;
            CopyOut(frame, stride);
            _stream.Release();
            pixels = _copy;
            _lastCopy = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        }

        return new CaptureResult(CaptureStatus.Frame, new CaptureFrame
        {
            Width = frame.Width,
            Height = frame.Height,
            Format = format.Value,
            Rotation = FrameRotation.None,
            TimestampTicks = Environment.TickCount64 * TimeSpan.TicksPerMillisecond,
            Cpu = pixels,
            Stride = stride,
        });
    }

    /// <summary>Rows copied out tight into the spare buffer, which then becomes the picture handed out.</summary>
    private unsafe void CopyOut(in PipeWireFrame frame, int rowBytes)
    {
        int size = rowBytes * frame.Height;
        if (_spare.Length != size)
        {
            _spare = new byte[size];
        }

        byte* source = (byte*)frame.Data;
        fixed (byte* destination = _spare)
        {
            for (int y = 0; y < frame.Height; y++)
            {
                Buffer.MemoryCopy(source + ((long)y * frame.Stride), destination + ((long)y * rowBytes), rowBytes, rowBytes);
            }
        }

        // The caller may still be reading the previous picture until this one is returned, so it is not written over.
        (_copy, _spare) = (_spare, _copy);
    }

    private void Invalidate()
    {
        _borrowed?.Invalidate();
        _borrowed = null;
    }

    private void Describe(in PipeWireFrame frame, bool borrow)
    {
        string mode = $"{frame.Width}x{frame.Height} stride {frame.Stride}, {frame.Buffers} buffers, {(borrow ? "handed on uncopied" : "copied")}";
        if (mode != _describedMode)
        {
            _describedMode = mode;
            _log.LogInformation("{Display}: PipeWire pictures {Mode}", Display.Name, mode);
        }
    }

    /// <summary>
    /// A locked PipeWire buffer as memory the rest of the engine can hold. Invalidated the moment the buffer goes
    /// back to the stream, so a reference kept past the contract fails with an exception rather than reading memory
    /// the compositor is drawing into -- or that is no longer mapped at all.
    /// </summary>
    private sealed unsafe class BorrowedPixels(nint pointer, int length) : MemoryManager<byte>
    {
        private nint _pointer = pointer;
        private int _length = length;

        public void Invalidate()
        {
            _pointer = 0;
            _length = 0;
        }

        public override Span<byte> GetSpan() => new((void*)_pointer, _length);

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)elementIndex, (uint)_length, nameof(elementIndex));
            return new MemoryHandle((byte*)_pointer + elementIndex);
        }

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing) => Invalidate();
    }
}
