using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Platform.MacOS.Native;

namespace DeskPair.Platform.MacOS.Codec;

/// <summary>
/// VideoToolbox H.264/HEVC encoding, the Apple-silicon hardware path. Offers H.264 first because every
/// controller decodes it (the Windows side through Media Foundation); HEVC is advertised too for a
/// controller that prefers it. The shim converts VideoToolbox's AVCC output to the Annex B the decoders
/// elsewhere expect, so the wire format matches the software encoders.
/// </summary>
public sealed class MacVideoEncoderFactory : IVideoEncoderFactory
{
    private readonly ILoggerFactory _logs;

    public MacVideoEncoderFactory(ILoggerFactory logs)
    {
        _logs = logs;
    }

    public IReadOnlyList<EncoderDescriptor> Describe() => MacShim.IsAvailable
        ?
        [
            Descriptor(VideoCodec.H264),
            Descriptor(VideoCodec.H265),
        ]
        : [];

    public IVideoEncoder Create(VideoEncoderConfig config)
    {
        if (config.Codec is not (VideoCodec.H264 or VideoCodec.H265))
        {
            throw new NotSupportedException($"VideoToolbox encoder does not support {config.Codec}.");
        }

        return new MacVideoEncoder(config, _logs.CreateLogger<MacVideoEncoder>());
    }

    internal static EncoderDescriptor Descriptor(VideoCodec codec) => new()
    {
        Codec = codec,
        Backend = CodecBackend.VideoToolbox,
        Vendor = GpuVendor.Apple,
        Name = $"VideoToolbox {(codec == VideoCodec.H265 ? "HEVC" : "H.264")}",
        IsHardware = true,
    };
}

public sealed unsafe class MacVideoEncoder : IVideoEncoder
{
    private readonly ILogger _log;
    private nint _handle;
    private byte[] _packet = [];
    private bool _forceKey;
    private bool _disposed;

    public MacVideoEncoder(VideoEncoderConfig config, ILogger log)
    {
        _log = log;
        Codec = config.Codec;
        int codecId = config.Codec == VideoCodec.H265 ? 1 : 0;
        _handle = MacShim.fd_encoder_create(config.Width, config.Height, config.Fps, Math.Max(100, config.BitrateKbps), codecId);
        if (_handle == 0)
        {
            throw new InvalidOperationException("VideoToolbox could not create a compression session.");
        }

        IsHardware = MacShim.fd_encoder_is_hardware(_handle) != 0;
        _log.LogInformation("Encoder: {Descriptor} {W}x{H} @ {Fps} fps, {Kbps} kbps",
            Descriptor, config.Width, config.Height, config.Fps, config.BitrateKbps);
    }

    public VideoCodec Codec { get; }

    public EncoderDescriptor Descriptor => field ??= MacVideoEncoderFactory.Descriptor(Codec) with { IsHardware = IsHardware };

    public bool IsHardware { get; } = true;

    // Each encode completes its own frame synchronously, so a static screen needs no re-feed.
    public bool IsLatencyFree => true;

    public PixelFormat RequiredInputFormat => PixelFormat.Bgra32;

    public void SetBitrate(int kbps)
    {
        if (!_disposed && _handle != 0)
        {
            MacShim.fd_encoder_set_bitrate(_handle, Math.Max(100, kbps));
        }
    }

    public void RequestKeyFrame() => _forceKey = true;

    public bool TryEncode(ReadOnlySpan<byte> input, int stride, long ptsTicks, out EncodedPacket packet)
    {
        packet = default;
        if (_disposed || _handle == 0 || input.IsEmpty)
        {
            return false;
        }

        int rc;
        MacShim.FdPacket p;
        fixed (byte* src = input)
        {
            rc = MacShim.fd_encoder_encode(_handle, src, stride, ptsTicks, _forceKey ? 1 : 0, out p);
        }

        if (rc != 1 || p.Data == 0 || p.Length <= 0)
        {
            return false;
        }

        _forceKey = false;
        if (_packet.Length < p.Length)
        {
            _packet = new byte[p.Length];
        }

        // The shim's buffer is reused on the next encode, so copy before returning.
        new ReadOnlySpan<byte>((void*)p.Data, p.Length).CopyTo(_packet);
        MacShim.fd_encoder_release(_handle);

        packet = new EncodedPacket
        {
            Data = _packet.AsMemory(0, p.Length),
            IsKeyFrame = p.IsKeyframe != 0,
            PtsTicks = ptsTicks,
        };
        return true;
    }

    public bool TryEncode(in GpuSurfaceHandle texture, long ptsTicks, out EncodedPacket packet)
    {
        packet = default;
        return false; // GPU-surface input is a later optimisation; the CPU BGRA path is used for now.
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_handle != 0)
            {
                MacShim.fd_encoder_destroy(_handle);
                _handle = 0;
            }
        }

        return ValueTask.CompletedTask;
    }
}
