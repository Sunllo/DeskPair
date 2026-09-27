using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Linux.Capture.Drm;
using DeskPair.Platform.Linux.Native;
using Microsoft.Extensions.Logging;

namespace DeskPair.Platform.Linux.Capture;

/// <summary>
/// A scanout format this build cannot read, named exactly. Thrown from <c>Create</c>, never after a
/// frame has been promised, so the caller can fall back to X11 or tell the viewer in words.
/// </summary>
public sealed class DrmCaptureUnsupportedException(string message) : Exception(message);

/// <summary>
/// The screen as the display hardware sees it, read from the daemon's dma-buf.
///
/// This is the one Linux capture path that sees the lock screen and the login screen, because it reads
/// what the CRTC is scanning out rather than what an X server was told. It plugs into the same contract
/// as <see cref="X11ScreenCapturer"/> and keeps the same buffer rule: the picture returned stays valid
/// until the next one is returned, and a timeout leaves it alone. Rows are copied out tight, cropped to
/// the part of the framebuffer this CRTC shows, so nothing downstream learns about pitch.
///
/// CPU only, on purpose. The only caller passes <c>preferGpu: false</c>, and the abstraction has no
/// honest way to say "a dma-buf" yet; filling <c>GpuSurfaceHandle</c> with something that is not a VA
/// surface would be the kind of technically-populated field that fails two milestones later.
/// </summary>
public sealed class DrmScreenCapturerFactory(DrmCaptureChannel channel, ILoggerFactory logs) : IScreenCapturerFactory
{
    public IScreenCapturer Create(DisplayDescriptor display, bool preferGpu) =>
        new DrmScreenCapturer(channel, display, logs.CreateLogger<DrmScreenCapturer>());
}

public sealed class DrmScreenCapturer : IScreenCapturer
{
    /// <summary>How often the daemon is asked while nothing is changing: sixty times a second is enough for anyone's cursor.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(16);

    private readonly DrmCaptureChannel _channel;
    private readonly ILogger _log;
    private readonly uint _generation;
    private readonly PixelFormat _format;

    // Bound once: a method group turned into a delegate on every poll would be sixty allocations a second.
    private readonly DrmFrameReader _read;
    private byte[] _buffer;
    private byte[] _previous;
    private bool _havePrevious;
    private bool _loggedBlank;
    private double _lastWait;
    private double _lastCopy;

    public DrmScreenCapturer(DrmCaptureChannel channel, DisplayDescriptor display, ILogger log)
    {
        _channel = channel;
        _log = log;
        Display = display;

        // The shape is decided on the first answer, and refused there if it cannot be read. Later polls
        // only ever confirm it or report a new generation.
        DrmPollResult first = channel.Poll();
        if (first.Kind == DrmPollKind.Error)
        {
            throw new DrmCaptureUnsupportedException(first.Error ?? "the daemon refused the first poll");
        }

        DrmFrameInfo shape = first.Frame;
        _generation = first.Generation;
        if (first.Kind == DrmPollKind.Frame)
        {
            string description = DrmFormatSupport.Describe(display.Name, "?", shape.Fourcc, shape.Modifier, shape.PlaneCount);
            DrmReadPath path = DrmFormatSupport.Classify(DrmDisplayEnumerator.DriverFor(display), shape.Fourcc, shape.Modifier, shape.PlaneCount);
            if (path != DrmReadPath.LinearPacked32)
            {
                throw new DrmCaptureUnsupportedException(
                    $"{description}. This build reads linear packed 32-bit scanout directly and has no GPU import path yet; " +
                    $"the lock screen and login screen cannot be shown on this display.");
            }

            _format = shape.Fourcc == DrmFormat.Xbgr8888 || shape.Fourcc == DrmFormat.Abgr8888 ? PixelFormat.Rgba32 : PixelFormat.Bgra32;
        }

        _buffer = [];
        _previous = [];
        _read = CopyIfCurrent;
    }

    public DisplayDescriptor Display { get; }

    public bool SupportsGpuTexture => false;

    public GpuApi GpuApi => GpuApi.None;

    public (double WaitMs, double ReadbackMs) LastFrameTiming => (_lastWait, _lastCopy);

    public async ValueTask<CaptureResult> AcquireFrameAsync(TimeSpan timeout, CancellationToken ct)
    {
        long started = Environment.TickCount64;
        while (true)
        {
            DrmPollResult result = _channel.Poll(_read);
            switch (result.Kind)
            {
                case DrmPollKind.Closed:
                    return CaptureResult.Failed(new IOException(result.Error ?? "the capture channel closed"));

                case DrmPollKind.Error:
                    return CaptureResult.Failed(new IOException(result.Error ?? "the daemon reported an error"));

                case DrmPollKind.NoScanout:
                    if (result.Generation != _generation)
                    {
                        return CaptureResult.Switched;
                    }

                    // The display is off. The last picture stays on the viewer's screen; nothing black is sent.
                    break;

                case DrmPollKind.Frame:
                    if (result.Generation != _generation)
                    {
                        return CaptureResult.Switched;
                    }

                    if (result.Read)
                    {
                        _lastWait = Environment.TickCount64 - started;
                        return new CaptureResult(CaptureStatus.Frame, new CaptureFrame
                        {
                            Width = (int)result.Frame.SrcWidth,
                            Height = (int)result.Frame.SrcHeight,
                            Format = _format,
                            Rotation = FrameRotation.None,
                            TimestampTicks = Environment.TickCount64 * TimeSpan.TicksPerMillisecond,
                            Cpu = _buffer,
                            Stride = (int)result.Frame.SrcWidth * 4,
                        });
                    }

                    break;
            }

            if (Environment.TickCount64 - started >= timeout.TotalMilliseconds)
            {
                return CaptureResult.TimedOut;
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The poll's reader: a frame of another generation is a switch, and is not read.</summary>
    private bool CopyIfCurrent(in DrmFrameInfo frame, DrmMappedBuffer map) => frame.Generation == _generation && Copy(frame, map);

    /// <summary>
    /// Copies the visible crop into the spare buffer and says whether it differs from the last picture
    /// returned. Runs inside the channel's poll, which is what keeps the map there for the whole copy. The
    /// compare is what X11 does too; here it is cheaper than it looks, because a static screen is the
    /// common case and the copy is one pass over four megabytes.
    /// </summary>
    private unsafe bool Copy(in DrmFrameInfo frame, DrmMappedBuffer map)
    {
        long t = Environment.TickCount64;
        int width = (int)frame.SrcWidth;
        int height = (int)frame.SrcHeight;
        int rowBytes = width * 4;
        int needed = rowBytes * height;
        if (_previous.Length != needed)
        {
            _previous = new byte[needed];
            _havePrevious = false;
        }

        nuint end = (nuint)frame.Offset + (nuint)(frame.SrcY + height - 1) * frame.Pitch + (nuint)(frame.SrcX + width) * 4;
        if (end > map.Length)
        {
            throw new IOException($"the scanout crop {width}x{height}+{frame.SrcX}+{frame.SrcY} at pitch {frame.Pitch} runs past the {map.Length}-byte dma-buf");
        }

        Mman.SyncReadStart(map.Fd);
        try
        {
            byte* source = map.Base + frame.Offset + (nuint)frame.SrcY * frame.Pitch + (nuint)frame.SrcX * 4;
            fixed (byte* destination = _previous)
            {
                for (int y = 0; y < height; y++)
                {
                    Buffer.MemoryCopy(source + (nuint)y * frame.Pitch, destination + (nint)y * rowBytes, rowBytes, rowBytes);
                }
            }
        }
        finally
        {
            Mman.SyncReadEnd(map.Fd);
        }

        _lastCopy = Environment.TickCount64 - t;

        if (_havePrevious && _buffer.Length == needed && _previous.AsSpan().SequenceEqual(_buffer))
        {
            return false;
        }

        // Swap: what was just copied becomes the picture handed out, and the old one is next time's spare.
        (_buffer, _previous) = (_previous, _buffer.Length == needed ? _buffer : new byte[needed]);
        _havePrevious = true;
        NoteIfBlank();
        return true;
    }

    /// <summary>
    /// A uniformly black first picture is what a wrongly read format looks like, and also what a screen
    /// that is off looks like. Said once, in the log, so the next person knows which two to tell apart.
    /// </summary>
    private void NoteIfBlank()
    {
        if (_loggedBlank || _buffer.Length < 4096)
        {
            return;
        }

        ReadOnlySpan<byte> px = _buffer;
        bool uniform = true;
        uint first = BitConverter.ToUInt32(px);
        for (int i = 4; i < px.Length && uniform; i += 4096)
        {
            uniform = BitConverter.ToUInt32(px[i..]) == first;
        }

        if (uniform)
        {
            _loggedBlank = true;
            _log.LogWarning("DRM-CAPTURE-BLANK: {Display} produced a uniform frame (0x{Value:x8}). The screen may be off, or this format may be read wrongly.", Display.Name, first);
        }
    }

    public void ForceFallbackPath()
    {
        // There is one path. The factory's caller falls back to X11 by catching the constructor's exception.
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
