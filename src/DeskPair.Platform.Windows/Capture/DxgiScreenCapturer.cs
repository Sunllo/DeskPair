using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SharpGen.Runtime;
using DeskPair.Platform.Abstractions.Capture;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeskPair.Platform.Windows.Capture;

public sealed class WindowsScreenCapturerFactory : IScreenCapturerFactory
{
    private readonly WindowsDisplayEnumerator _displays;
    private readonly ILoggerFactory _logs;

    public WindowsScreenCapturerFactory(WindowsDisplayEnumerator displays, ILoggerFactory logs)
    {
        _displays = displays;
        _logs = logs;
    }

    /// <summary>Skip DXGI entirely (option "enable-directx-capture" off) and use GDI.</summary>
    public bool ForceGdi { get; set; }

    public IScreenCapturer Create(DisplayDescriptor display, bool preferGpu)
    {
        WindowsDisplayEnumerator.WindowsDisplay? wd = _displays.GetWindowsDisplays().FirstOrDefault(d => d.DeviceName == display.Name || d.Descriptor.Index == display.Index);
        if (wd is null)
        {
            throw new ArgumentException($"Display {display.Name} not found.", nameof(display));
        }

        Native.WinMm.BeginPeriod(); // 1 ms timer resolution while capturing; released by the capturer
        return new DxgiScreenCapturer(wd, ForceGdi, _logs.CreateLogger<DxgiScreenCapturer>());
    }
}

/// <summary>
/// Desktop Duplication capture with a GDI fallback. Frames are copied into a staging texture and mapped
/// as BGRA; the buffer is valid until the next acquire. Falls back to GDI after repeated failures.
/// </summary>
public sealed class DxgiScreenCapturer : IScreenCapturer
{
    /// <summary>
    /// How long duplication may say "nothing has changed" before it has produced a single frame, after which
    /// it is treated as not working on this output. Once one frame has arrived, timeouts are normal and last
    /// as long as nobody touches the machine, so this only ever applies to a stream that never started.
    ///
    /// Short, because a duplication that works answers the very first acquire with the current desktop — it
    /// does not wait for a change. So half a second of complete silence already means something is wrong,
    /// and every millisecond of it is a black window in front of whoever just connected.
    /// </summary>
    private static readonly TimeSpan SilenceBeforeGdi = TimeSpan.FromMilliseconds(500);

    private readonly WindowsDisplayEnumerator.WindowsDisplay _display;
    private readonly ILogger _log;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private GdiScreenCapturer? _gdi;
    private byte[] _buffer = [];
    private OutduplMoveRect[] _moveScratch = [];
    private RawRect[] _rectScratch = [];
    private PixelRect[] _dirtyScratch = new PixelRect[64];
    private long _duplicationStarted;
    private bool _everDelivered;
    private bool _frameAcquired;
    private bool _disposed;

    public DxgiScreenCapturer(WindowsDisplayEnumerator.WindowsDisplay display, bool forceGdi, ILogger log)
    {
        _display = display;
        _log = log;
        if (forceGdi || !TryInitDxgi())
        {
            ForceFallbackPath();
        }
    }

    public DisplayDescriptor Display => _display.Descriptor;

    public bool SupportsGpuTexture => false;

    public GpuApi GpuApi => GpuApi.None;

    public bool IsGdi => _gdi is not null;

    /// <summary>Time the last frame spent waiting for the desktop to present something new (milliseconds).</summary>
    public double LastWaitMs { get; private set; }

    /// <summary>Time the last frame spent being copied out of the GPU into system memory (milliseconds).</summary>
    public double LastReadbackMs { get; private set; }

    public (double WaitMs, double ReadbackMs) LastFrameTiming => (LastWaitMs, LastReadbackMs);

    /// <summary>
    /// The three parts of <see cref="LastReadbackMs"/>. A bare staging copy of a texture we own costs about
    /// 2.3 ms at 1440p, so when a live session reports four times that, the difference is in duplication's
    /// own bookkeeping rather than in the transfer. Split it out so that is measurable rather than assumed.
    /// </summary>
    public (double CopyMs, double MetadataMs, double MapMs) LastReadbackParts { get; private set; }

    public ValueTask<CaptureResult> AcquireFrameAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_gdi is not null)
        {
            return _gdi.AcquireFrameAsync(timeout, ct);
        }

        try
        {
            return ValueTask.FromResult(AcquireDxgi(timeout));
        }
        catch (SharpGenException e) when (e.ResultCode == Vortice.DXGI.ResultCode.AccessLost || e.ResultCode == Vortice.DXGI.ResultCode.AccessDenied)
        {
            // Mode change, UAC prompt or secure desktop: the caller rebuilds the capturer on the new desktop.
            _log.LogInformation("Desktop duplication lost access ({Code}); desktop switched", e.ResultCode);
            return ValueTask.FromResult(CaptureResult.Switched);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "DXGI capture failed; switching to GDI");
            ForceFallbackPath();
            return ValueTask.FromResult(CaptureResult.TimedOut);
        }
    }

    private CaptureResult AcquireDxgi(TimeSpan timeout)
    {
        ReleaseFrame();
        long waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
        Result hr = _duplication!.AcquireNextFrame((uint)Math.Clamp(timeout.TotalMilliseconds, 0, 1000), out OutduplFrameInfo info, out IDXGIResource? resource);
        LastWaitMs = System.Diagnostics.Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
        long readbackStart = System.Diagnostics.Stopwatch.GetTimestamp();
        if (hr == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            // Duplication can succeed and then deliver nothing at all: it reports only what the compositor
            // presents, and there are desktops it never sees present — a session nobody is attached to, a
            // monitor that is asleep, an output the desktop is not actually being composited to. The viewer
            // then sits in front of a black window for as long as the session lasts, because the capture
            // loop is politely waiting for a frame that is never coming. GDI reads the same desktop the slow
            // way and does produce one, so a stream that never starts is worth falling back for. A stream
            // that has started is not: on an idle desktop, minutes of silence are the correct answer.
            if (!_everDelivered && System.Diagnostics.Stopwatch.GetElapsedTime(_duplicationStarted) > SilenceBeforeGdi)
            {
                _log.LogWarning(
                    "Desktop duplication on {Device} produced no frame in {Ms:F0} ms; the desktop is not presenting to it, switching to GDI",
                    _display.DeviceName,
                    SilenceBeforeGdi.TotalMilliseconds);
                ForceFallbackPath();
            }

            return CaptureResult.TimedOut;
        }

        hr.CheckError();
        long copyDone = readbackStart;
        _frameAcquired = true;
        ReadOnlyMemory<PixelRect> dirty = default;
        using (resource)
        {
            if (resource is null)
            {
                return CaptureResult.TimedOut;
            }

            // A present time of zero means nothing new has been presented since the last acquire, so what
            // came back is a picture the caller has already seen — except the first time, when the caller
            // has seen nothing at all and this is the desktop as it stands. Discarding that one is why
            // connecting to a still screen used to show a black window until somebody moved the mouse on
            // the far end: the first change was the first frame.
            if (info.LastPresentTime == 0 && _everDelivered)
            {
                return CaptureResult.TimedOut; // only cursor or metadata changed
            }

            using ID3D11Texture2D texture = resource.QueryInterface<ID3D11Texture2D>();
            Texture2DDescription desc = texture.Description;
            EnsureStaging(desc);
            _context!.CopyResource(_staging!, texture);
            copyDone = System.Diagnostics.Stopwatch.GetTimestamp();
            dirty = ReadChangedRects(in info, (int)desc.Width, (int)desc.Height);
        }

        ReleaseFrame();
        long metadataDone = System.Diagnostics.Stopwatch.GetTimestamp();
        MappedSubresource map = _context!.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int width = (int)_staging!.Description.Width;
            int height = (int)_staging.Description.Height;
            int stride = width * 4;
            int need = stride * height;
            if (_buffer.Length < need)
            {
                _buffer = new byte[need];
            }

            unsafe
            {
                byte* src = (byte*)map.DataPointer;
                for (int y = 0; y < height; y++)
                {
                    new ReadOnlySpan<byte>(src + (long)y * map.RowPitch, stride).CopyTo(_buffer.AsSpan(y * stride, stride));
                }
            }

            _everDelivered = true;
            return new CaptureResult(CaptureStatus.Frame, new CaptureFrame
            {
                Width = width,
                Height = height,
                Format = PixelFormat.Bgra32,
                Rotation = Display.Rotation,
                Cpu = new ReadOnlyMemory<byte>(_buffer, 0, need),
                Stride = stride,
                TimestampTicks = info.LastPresentTime,
                DirtyRects = dirty,
            });
        }
        finally
        {
            _context.Unmap(_staging!, 0);
            long readbackDone = System.Diagnostics.Stopwatch.GetTimestamp();
            LastReadbackMs = System.Diagnostics.Stopwatch.GetElapsedTime(readbackStart, readbackDone).TotalMilliseconds;
            LastReadbackParts = (
                System.Diagnostics.Stopwatch.GetElapsedTime(readbackStart, copyDone).TotalMilliseconds,
                System.Diagnostics.Stopwatch.GetElapsedTime(copyDone, metadataDone).TotalMilliseconds,
                System.Diagnostics.Stopwatch.GetElapsedTime(metadataDone, readbackDone).TotalMilliseconds);
        }
    }

    /// <summary>
    /// Desktop Duplication tells us which rectangles changed (dirty) and which were moved; both count as
    /// changed for the encoder. Empty means "unknown" and the caller rescans the whole frame.
    /// </summary>
    private ReadOnlyMemory<PixelRect> ReadChangedRects(in OutduplFrameInfo info, int width, int height)
    {
        int size = (int)info.TotalMetadataBufferSize;
        if (size <= 0)
        {
            return default;
        }

        int moveCapacity = size / Unsafe.SizeOf<OutduplMoveRect>() + 1;
        int dirtyCapacity = size / Unsafe.SizeOf<RawRect>() + 1;
        if (_moveScratch.Length < moveCapacity)
        {
            _moveScratch = new OutduplMoveRect[moveCapacity];
        }

        if (_rectScratch.Length < dirtyCapacity)
        {
            _rectScratch = new RawRect[dirtyCapacity];
        }

        int count = 0;
        Result hr = _duplication!.GetFrameMoveRects((uint)(_moveScratch.Length * Unsafe.SizeOf<OutduplMoveRect>()), _moveScratch, out uint moveBytes);
        if (hr.Failure)
        {
            return default;
        }

        int moves = (int)(moveBytes / Unsafe.SizeOf<OutduplMoveRect>());
        for (int i = 0; i < moves; i++)
        {
            Add(_moveScratch[i].DestinationRect);
        }

        hr = _duplication.GetFrameDirtyRects((uint)(_rectScratch.Length * Unsafe.SizeOf<RawRect>()), _rectScratch, out uint dirtyBytes);
        if (hr.Failure)
        {
            return default;
        }

        int dirties = (int)(dirtyBytes / Unsafe.SizeOf<RawRect>());
        for (int i = 0; i < dirties; i++)
        {
            Add(_rectScratch[i]);
        }

        return new ReadOnlyMemory<PixelRect>(_dirtyScratch, 0, count);

        void Add(RawRect r)
        {
            int x = Math.Clamp(r.Left, 0, width), y = Math.Clamp(r.Top, 0, height);
            int w = Math.Clamp(r.Right, 0, width) - x, h = Math.Clamp(r.Bottom, 0, height) - y;
            if (w <= 0 || h <= 0)
            {
                return;
            }

            if (count == _dirtyScratch.Length)
            {
                Array.Resize(ref _dirtyScratch, count * 2);
            }

            _dirtyScratch[count++] = new PixelRect(x, y, w, h);
        }
    }

    private void EnsureStaging(Texture2DDescription source)
    {
        if (_staging is not null && _staging.Description.Width == source.Width && _staging.Description.Height == source.Height && _staging.Description.Format == source.Format)
        {
            return;
        }

        _staging?.Dispose();
        if (source.Format != Format.B8G8R8A8_UNorm)
        {
            // HDR outputs (R16G16B16A16_FLOAT) need tone mapping which is not implemented; surface loudly.
            throw new NotSupportedException($"Unsupported desktop format {source.Format}; HDR capture is not supported yet.");
        }

        _staging = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = source.Width,
            Height = source.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = source.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
        });
    }

    private void ReleaseFrame()
    {
        if (_frameAcquired)
        {
            _frameAcquired = false;
            try
            {
                _duplication?.ReleaseFrame();
            }
            catch (SharpGenException)
            {
            }
        }
    }

    private bool TryInitDxgi()
    {
        if (_display.AdapterIndex < 0)
        {
            return false;
        }

        try
        {
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            factory.EnumAdapters1((uint)_display.AdapterIndex, out IDXGIAdapter1? adapter).CheckError();
            using (adapter)
            {
                FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
                D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels, out ID3D11Device? device).CheckError();
                _device = device!;
                _context = _device.ImmediateContext;
                adapter!.EnumOutputs((uint)_display.OutputIndex, out IDXGIOutput? output).CheckError();
                using (output)
                using (IDXGIOutput1 output1 = output!.QueryInterface<IDXGIOutput1>())
                {
                    _duplication = output1.DuplicateOutput(_device);
                }
            }

            _duplicationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            _everDelivered = false;
            _log.LogInformation("Desktop duplication active on {Device}", _display.DeviceName);
            return true;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Desktop duplication unavailable on {Device}", _display.DeviceName);
            DisposeDxgi();
            return false;
        }
    }

    public void ForceFallbackPath()
    {
        if (_gdi is not null)
        {
            return;
        }

        DisposeDxgi();
        _gdi = new GdiScreenCapturer(_display, _log);
        _log.LogInformation("Using GDI capture on {Device}", _display.DeviceName);
    }

    private void DisposeDxgi()
    {
        ReleaseFrame();
        _staging?.Dispose();
        _duplication?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _staging = null;
        _duplication = null;
        _context = null;
        _device = null;
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            Native.WinMm.EndPeriod();
        }

        DisposeDxgi();
        _gdi?.Dispose();
        return ValueTask.CompletedTask;
    }
}
