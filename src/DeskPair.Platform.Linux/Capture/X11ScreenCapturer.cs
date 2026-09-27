using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Linux.Native;

namespace DeskPair.Platform.Linux.Capture;

public sealed class X11ScreenCapturerFactory : IScreenCapturerFactory
{
    private readonly ILoggerFactory _logs;

    public X11ScreenCapturerFactory(ILoggerFactory logs)
    {
        _logs = logs;
    }

    public IScreenCapturer Create(DisplayDescriptor display, bool preferGpu) =>
        new X11ScreenCapturer(display, _logs.CreateLogger<X11ScreenCapturer>());
}

/// <summary>
/// Captures one monitor's rectangle out of the X root window with MIT-SHM. The shared segment is what makes
/// this viable: XShmGetImage copies the server's framebuffer straight into memory both processes map, so a
/// frame costs a copy rather than eight megabytes marshalled through the X socket.
///
/// X11 has no "tell me when it changed" for the root window, so this compares each frame against the last
/// and reports Timeout when nothing moved — the same contract the capturer interface expects, and the same
/// shape as the GDI fallback on Windows. On a rootless Xwayland server the root window shows only X11
/// clients, not the native Wayland desktop; that is a property of the session, and the Wayland capturer
/// (portal + PipeWire) is the answer there.
/// </summary>
public sealed class X11ScreenCapturer : IScreenCapturer
{
    /// <summary>How often the root is re-measured. A resize does not need noticing within one frame.</summary>
    private const long RootCheckIntervalMs = 500;

    private readonly ILogger _log;
    private readonly DisplayDescriptor _display;
    private readonly nint _dpy;
    private readonly nint _root;
    private nint _imagePtr;
    private readonly nint _shmInfoPtr; // stable unmanaged XShmSegmentInfo; the image's obdata points here
    private bool _shmAttached;

    /// <summary>
    /// The root's size when the shared image was allocated. A screen that is resized afterwards does not
    /// resize this image, and X clips the request silently rather than failing, so without this check the
    /// viewer keeps receiving a frame of the old size for the rest of the session and nothing says why.
    /// </summary>
    private int _rootWidth;
    private int _rootHeight;
    private long _rootCheckedTicks;
    private int _width;
    private int _height;
    private byte[] _buffer = [];
    private byte[] _previous = [];
    private bool _havePrevious;
    private bool _disposed;

    public X11ScreenCapturer(DisplayDescriptor display, ILogger log)
    {
        Xlib.EnsureThreadSafe();
        _log = log;
        _display = display;
        _dpy = Xlib.XOpenDisplay(null);
        if (_dpy == 0)
        {
            throw new InvalidOperationException("Cannot open the X display.");
        }

        _root = Xlib.XDefaultRootWindow(_dpy);
        if (!XShm.XShmQueryExtension(_dpy))
        {
            throw new NotSupportedException("This X server has no MIT-SHM extension; X11 capture needs it.");
        }

        _shmInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<XShm.XShmSegmentInfo>());
        Marshal.StructureToPtr(default(XShm.XShmSegmentInfo), _shmInfoPtr, false); // zero before the first Release reads it
        Allocate(display.Width, display.Height);
        (_rootWidth, _rootHeight) = RootSize();
        _log.LogInformation("X11 capture on {Name} ({W}x{H}) at ({X},{Y}) via MIT-SHM", display.Name, _width, _height, display.X, display.Y);
    }

    public DisplayDescriptor Display => _display;

    public bool SupportsGpuTexture => false;

    public GpuApi GpuApi => GpuApi.None;

    public ValueTask<CaptureResult> AcquireFrameAsync(TimeSpan timeout, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            // A screen that is resized mid-session does not resize this image, and X clips the request
            // silently instead of failing: the viewer would keep getting a frame of the old size, cropped or
            // padded, for the rest of the session with nothing to say why. Report it as a desktop switch,
            // which the media module already handles by rebuilding the capturer from a fresh display list.
            // Throttled because it is a round trip, and a resize is not something that needs noticing within
            // one frame.
            if (Environment.TickCount64 - _rootCheckedTicks >= RootCheckIntervalMs)
            {
                _rootCheckedTicks = Environment.TickCount64;
                (int rootW, int rootH) = RootSize();
                if (rootW != 0 && (rootW != _rootWidth || rootH != _rootHeight))
                {
                    _log.LogInformation("The X screen changed from {OldW}x{OldH} to {NewW}x{NewH}; rebuilding the capturer", _rootWidth, _rootHeight, rootW, rootH);
                    _rootWidth = rootW;
                    _rootHeight = rootH;
                    return ValueTask.FromResult(new CaptureResult(CaptureStatus.DesktopSwitched, default));
                }
            }

            // Copy the monitor's rectangle from the root window into the shared image. The whole root is one
            // drawable spanning every monitor, so a multi-monitor setup reads its own sub-rectangle.
            if (!XShm.XShmGetImage(_dpy, _root, _imagePtr, _display.X, _display.Y, nuint.MaxValue))
            {
                return ValueTask.FromResult(CaptureResult.TimedOut);
            }

            Xlib.XSync(_dpy, discard: false);

            unsafe
            {
                var image = (Xlib.XImage*)_imagePtr;
                int stride = image->BytesPerLine;
                var src = new ReadOnlySpan<byte>((void*)image->Data, stride * _height);
                int rowBytes = _width * 4;
                if (stride == rowBytes)
                {
                    src[..(rowBytes * _height)].CopyTo(_buffer);
                }
                else
                {
                    for (int y = 0; y < _height; y++)
                    {
                        src.Slice(y * stride, rowBytes).CopyTo(_buffer.AsSpan(y * rowBytes));
                    }
                }
            }

            if (_havePrevious && !Changed(_buffer, _previous))
            {
                return ValueTask.FromResult(CaptureResult.TimedOut);
            }

            _buffer.CopyTo(_previous, 0);
            _havePrevious = true;

            return ValueTask.FromResult(new CaptureResult(CaptureStatus.Frame, new CaptureFrame
            {
                Width = _width,
                Height = _height,
                Format = PixelFormat.Bgra32,
                Rotation = _display.Rotation,
                Cpu = new ReadOnlyMemory<byte>(_buffer, 0, _width * 4 * _height),
                Stride = _width * 4,
                TimestampTicks = DateTime.UtcNow.Ticks,
            }));
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "X11 capture failed");
            return ValueTask.FromResult(CaptureResult.Failed(e));
        }
    }

    /// <summary>
    /// Whether the picture moved. X11 has no "tell me when it changed" for the root, so every frame is
    /// compared with the last one; pulled out of the loop so it can be tested without a display.
    /// </summary>
    internal static bool Changed(ReadOnlySpan<byte> now, ReadOnlySpan<byte> before) => !now.SequenceEqual(before);

    /// <summary>The root window's current size, or (0,0) when the server would not say.</summary>
    private (int Width, int Height) RootSize() =>
        Xlib.XGetWindowAttributes(_dpy, _root, out Xlib.XWindowAttributes attributes) != 0
            ? (attributes.Width, attributes.Height)
            : (0, 0);

    public void ForceFallbackPath()
    {
        // X11 has no faster/slower pair the way DXGI has GDI; there is nothing to fall back to here.
    }

    private void Allocate(int width, int height)
    {
        Release();
        _width = width;
        _height = height;

        // The XShmSegmentInfo lives at a stable address (_shmInfoPtr) because XShmCreateImage records that very
        // address in the image's obdata and XShmGetImage reads the segment id back out of it on every frame.
        var shm = default(XShm.XShmSegmentInfo);
        Marshal.StructureToPtr(shm, _shmInfoPtr, false);

        // 24-bit depth, 32-bit pixels: the server packs BGRA, which is exactly the format the pipeline wants.
        _imagePtr = XShm.XShmCreateImage(_dpy, 0, 24, XShm.ZPixmap, 0, _shmInfoPtr, (uint)width, (uint)height);
        if (_imagePtr == 0)
        {
            throw new InvalidOperationException("XShmCreateImage failed.");
        }

        int shmid;
        nint shmaddr;
        unsafe
        {
            var image = (Xlib.XImage*)_imagePtr;
            nuint size = (nuint)(image->BytesPerLine * height);
            shmid = LibC.shmget(0, size, LibC.CreateOwnerReadWrite);
            if (shmid < 0)
            {
                throw new InvalidOperationException($"shmget failed ({Marshal.GetLastPInvokeError()}).");
            }

            shmaddr = LibC.shmat(shmid, 0, 0);
            if (shmaddr == -1)
            {
                throw new InvalidOperationException($"shmat failed ({Marshal.GetLastPInvokeError()}).");
            }

            image->Data = shmaddr;
        }

        // Write shmid/shmaddr into the stable segment info, then attach (the server fills in Shmseg there).
        shm.Shmid = shmid;
        shm.Shmaddr = shmaddr;
        shm.ReadOnly = 0;
        Marshal.StructureToPtr(shm, _shmInfoPtr, false);

        if (!XShm.XShmAttach(_dpy, _shmInfoPtr))
        {
            throw new InvalidOperationException("XShmAttach failed.");
        }

        _shmAttached = true;
        Xlib.XSync(_dpy, discard: false);

        // The segment is marked for removal now: it stays alive while attached and is reclaimed by the kernel
        // once both processes detach, so a crash cannot leak it.
        LibC.shmctl(shmid, 0, 0);

        _buffer = new byte[width * 4 * height];
        _previous = new byte[_buffer.Length];
        _havePrevious = false;
    }

    private void Release()
    {
        if (_shmAttached)
        {
            XShm.XShmDetach(_dpy, _shmInfoPtr);
            _shmAttached = false;
        }

        if (_imagePtr != 0)
        {
            Xlib.XFree(_imagePtr);
            _imagePtr = 0;
        }

        nint shmaddr = Marshal.PtrToStructure<XShm.XShmSegmentInfo>(_shmInfoPtr).Shmaddr;
        if (shmaddr != 0 && shmaddr != -1)
        {
            LibC.shmdt(shmaddr);
            var cleared = default(XShm.XShmSegmentInfo);
            Marshal.StructureToPtr(cleared, _shmInfoPtr, false);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            Release();
            if (_shmInfoPtr != 0)
            {
                Marshal.FreeHGlobal(_shmInfoPtr);
            }

            if (_dpy != 0)
            {
                Xlib.XCloseDisplay(_dpy);
            }
        }

        return ValueTask.CompletedTask;
    }
}
