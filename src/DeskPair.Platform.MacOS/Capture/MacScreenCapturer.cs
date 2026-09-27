using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.MacOS.Native;

namespace DeskPair.Platform.MacOS.Capture;

public sealed class MacScreenCapturerFactory : IScreenCapturerFactory
{
    private readonly ILogger _log;

    public MacScreenCapturerFactory(ILoggerFactory logs)
    {
        _log = logs.CreateLogger<MacScreenCapturer>();
    }

    public IScreenCapturer Create(DisplayDescriptor display, bool preferGpu) => new MacScreenCapturer(display, _log);
}

/// <summary>
/// Captures one display through ScreenCaptureKit (the shim runs the SCStream). Frames arrive as BGRA in a
/// locked pixel buffer; this copies the tight rectangle into a reusable buffer and hands it up, then releases
/// the lock. When nothing changed within the timeout the shim returns a timeout, which becomes
/// <see cref="CaptureStatus.Timeout"/> so the caller reuses the last picture. The cursor is composited into
/// the frame (macOS has no public separate-cursor image), so no GPU-surface path is offered here yet.
/// </summary>
public sealed class MacScreenCapturer : IScreenCapturer
{
    private readonly ILogger _log;
    private nint _handle;
    private byte[] _buffer = [];
    private bool _disposed;

    public MacScreenCapturer(DisplayDescriptor display, ILogger log)
    {
        _log = log;
        Display = display;
        // The cursor is sent as a shape, not drawn into the picture: composited here it could only move when a
        // frame arrived, so it trailed the viewer's own pointer and the two were visibly separate.
        //
        // That only holds while the shape can actually be read. If it cannot, drawing it into the frame is far
        // better than a session with no visible pointer at all, so ask once and decide.
        bool shapeAvailable = MacShim.fd_cursor_image(out _, out _, out _, out _, [], 0) != 0;
        if (!shapeAvailable)
        {
            log.LogWarning("The system cursor shape could not be read; compositing the cursor into the picture instead, which makes it lag the video");
        }

        _handle = MacShim.fd_capture_create((uint)display.AdapterLuid, display.Width, display.Height, showCursor: shapeAvailable ? 0 : 1);
        if (_handle == 0)
        {
            throw new InvalidOperationException(
                "ScreenCaptureKit could not start; grant Screen Recording permission to DeskPair in System Settings > Privacy & Security.");
        }
    }

    public DisplayDescriptor Display { get; }

    public bool SupportsGpuTexture => false;

    public GpuApi GpuApi => GpuApi.None;

    public ValueTask<CaptureResult> AcquireFrameAsync(TimeSpan timeout, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int ms = (int)Math.Clamp(timeout.TotalMilliseconds, 1, 1000);
        int rc = MacShim.fd_capture_acquire(_handle, ms, out MacShim.FdFrame frame);
        if (rc == 0)
        {
            return ValueTask.FromResult(CaptureResult.TimedOut);
        }

        if (rc < 0 || frame.Data == 0 || frame.Width <= 0 || frame.Height <= 0)
        {
            return ValueTask.FromResult(CaptureResult.Failed(new InvalidOperationException("ScreenCaptureKit acquire failed.")));
        }

        try
        {
            int tightStride = frame.Width * 4;
            int needed = tightStride * frame.Height;
            if (_buffer.Length < needed)
            {
                _buffer = new byte[needed];
            }

            // Copy row by row: the pixel buffer's stride is padded to an alignment, the frame we hand up is tight.
            unsafe
            {
                byte* src = (byte*)frame.Data;
                for (int y = 0; y < frame.Height; y++)
                {
                    Marshal.Copy((nint)(src + (long)y * frame.Stride), _buffer, y * tightStride, tightStride);
                }
            }

            var captured = new CaptureFrame
            {
                Width = frame.Width,
                Height = frame.Height,
                Format = PixelFormat.Bgra32,
                Stride = tightStride,
                Cpu = _buffer.AsMemory(0, needed),
                TimestampTicks = DateTime.UtcNow.Ticks,
            };
            return ValueTask.FromResult(new CaptureResult(CaptureStatus.Frame, captured));
        }
        finally
        {
            MacShim.fd_capture_release(_handle);
        }
    }

    public void ForceFallbackPath()
    {
        // ScreenCaptureKit is the only path; there is no slower universal fallback to switch to.
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_handle != 0)
            {
                MacShim.fd_capture_destroy(_handle);
                _handle = 0;
            }
        }

        return ValueTask.CompletedTask;
    }
}
