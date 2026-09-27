using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Capture;

/// <summary>
/// BitBlt-based capture used where desktop duplication is unavailable (RDP sessions, some VMs, session 0).
/// Change detection compares the whole frame with the previous one, mirroring the reference implementation.
/// </summary>
internal sealed class GdiScreenCapturer : IDisposable
{
    private readonly WindowsDisplayEnumerator.WindowsDisplay _display;
    private readonly ILogger _log;
    private readonly nint _screenDc;
    private readonly nint _memoryDc;
    private readonly nint _bitmap;
    private readonly nint _oldBitmap;
    private readonly byte[] _buffer;
    private readonly byte[] _previous;
    private readonly int _width;
    private readonly int _height;
    private bool _havePrevious;
    private bool _reportedFailure;
    private nint _attachedDesktop;

    public GdiScreenCapturer(WindowsDisplayEnumerator.WindowsDisplay display, ILogger log)
    {
        _display = display;
        _log = log;
        _width = display.Descriptor.Width;
        _height = display.Descriptor.Height;
        _screenDc = Gdi32.CreateDC(display.DeviceName, null, null, 0);
        if (_screenDc == 0)
        {
            throw new InvalidOperationException($"CreateDC failed for {display.DeviceName}.");
        }

        _memoryDc = Gdi32.CreateCompatibleDC(_screenDc);
        _bitmap = Gdi32.CreateCompatibleBitmap(_screenDc, _width, _height);
        _oldBitmap = Gdi32.SelectObject(_memoryDc, _bitmap);
        _buffer = new byte[_width * _height * 4];
        _previous = new byte[_buffer.Length];
    }

    /// <summary>
    /// Moves this thread to the desktop that is currently taking input.
    ///
    /// BitBlt reads the desktop of the calling thread, not the screen, so a capture loop started while
    /// somebody was working stops returning anything the moment the machine locks and the sign-in screen
    /// takes over -- a different desktop, which this thread is not on. The picture goes black and stays
    /// black, and the log says the desktop is locked, which is true and sounds like nothing can be done.
    ///
    /// Something can, as LocalSystem: go there. Only on failure, because attaching costs two system calls
    /// and the ordinary frame has nothing to fix. The old handle is closed only after the switch, and the
    /// thread is left on the new desktop -- the capture loop has one thread of its own and no windows, so
    /// there is nothing to put back.
    /// </summary>
    private bool AttachToInputDesktop()
    {
        nint desktop = User32.OpenInputDesktop(0, 0, User32.GENERIC_ALL);
        if (desktop == 0)
        {
            return false;
        }

        if (User32.SetThreadDesktop(desktop) == 0)
        {
            User32.CloseDesktop(desktop);
            return false;
        }

        if (_attachedDesktop != 0)
        {
            User32.CloseDesktop(_attachedDesktop);
        }

        _attachedDesktop = desktop;
        _log.LogInformation("{Device}: capture moved to the desktop taking input", _display.DeviceName);
        return true;
    }

    /// <summary>
    /// The desktop could not be read at all: it is locked, it is the secure desktop, or this process has
    /// been moved off the one the user is looking at. Answering "nothing changed" is close enough for the
    /// caller, but only after waiting — an ordinary unchanged frame costs a BitBlt and a comparison of the
    /// whole picture, which paces the capture loop by itself, while a failure returns in microseconds and
    /// leaves the loop spinning a core flat out for as long as the desktop stays unreadable.
    /// </summary>
    private async ValueTask<CaptureResult> Unreadable(string call, TimeSpan timeout, CancellationToken ct)
    {
        if (!_reportedFailure)
        {
            _reportedFailure = true;
            _log.LogWarning(
                "{Call} cannot read {Device} (error {Error}); the desktop is locked, switched or not this process's to read",
                call,
                _display.DeviceName,
                Marshal.GetLastPInvokeError());
        }

        try
        {
            await Task.Delay(timeout <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(33) : timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        return CaptureResult.TimedOut;
    }

    public ValueTask<CaptureResult> AcquireFrameAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            // The monitor DC is already positioned at this display's origin.
            if (Gdi32.BitBlt(_memoryDc, 0, 0, _width, _height, _screenDc, 0, 0, Gdi32.SRCCOPY | Gdi32.CAPTUREBLT) == 0
                && !(AttachToInputDesktop()
                     && Gdi32.BitBlt(_memoryDc, 0, 0, _width, _height, _screenDc, 0, 0, Gdi32.SRCCOPY | Gdi32.CAPTUREBLT) != 0))
            {
                return Unreadable("BitBlt", timeout, ct);
            }

            var info = new Gdi32.BITMAPINFO
            {
                bmiHeader = new Gdi32.BITMAPINFOHEADER
                {
                    biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Gdi32.BITMAPINFOHEADER>(),
                    biWidth = _width,
                    biHeight = -_height, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = Gdi32.BI_RGB,
                },
            };
            unsafe
            {
                fixed (byte* p = _buffer)
                {
                    if (Gdi32.GetDIBits(_memoryDc, _bitmap, 0, (uint)_height, p, ref info, Gdi32.DIB_RGB_COLORS) == 0)
                    {
                        return Unreadable("GetDIBits", timeout, ct);
                    }
                }
            }

            _reportedFailure = false;
            if (_havePrevious && _buffer.AsSpan().SequenceEqual(_previous))
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
                Cpu = _buffer,
                Stride = _width * 4,
                TimestampTicks = Environment.TickCount64,
            }));
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "GDI capture failed");
            return ValueTask.FromResult(CaptureResult.Failed(e));
        }
    }

    public void Dispose()
    {
        Gdi32.SelectObject(_memoryDc, _oldBitmap);
        Gdi32.DeleteObject(_bitmap);
        Gdi32.DeleteDC(_memoryDc);
        Gdi32.DeleteDC(_screenDc);
        if (_attachedDesktop != 0)
        {
            User32.CloseDesktop(_attachedDesktop);
            _attachedDesktop = 0;
        }
    }
}
