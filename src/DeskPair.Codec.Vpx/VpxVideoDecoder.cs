using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using DeskPair.Codec.Vpx.Native;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Codec;

namespace DeskPair.Codec.Vpx;

public sealed class VpxVideoDecoderFactory : IVideoDecoderFactory
{
    private readonly ILoggerFactory _logs;

    public VpxVideoDecoderFactory(ILoggerFactory logs)
    {
        _logs = logs;
    }

    public SupportedCodecs Probe() =>
        VpxInterop.IsAvailable ? SupportedCodecs.Vp9Software : SupportedCodecs.None;

    public IVideoDecoder Create(VideoCodec codec, GpuApi preferredOutput, long adapterLuid) => codec == VideoCodec.Vp9
        ? new VpxVideoDecoder(_logs.CreateLogger<VpxVideoDecoder>())
        : throw new NotSupportedException($"{codec} is not supported by libvpx here; only VP9 is wired up.");
}

/// <summary>
/// VP9 through libvpx, decoding into BGRA for the same CPU rendering path the other decoders feed. libvpx
/// hands back a picture it still owns, valid until the next decode, and the conversion copies out of it, so
/// the returned frame follows the same "valid until the next call" contract as the rest.
/// </summary>
public sealed unsafe class VpxVideoDecoder : IVideoDecoder
{
    private readonly ILogger _log;
    private VpxInterop.VpxCodecCtx _ctx;
    private byte[] _bgra = [];
    private int _width;
    private int _height;
    private bool _disposed;

    public VpxVideoDecoder(ILogger log)
    {
        if (!VpxInterop.IsAvailable)
        {
            throw new NotSupportedException("libvpx is not present; see native/libvpx/README.md.");
        }

        _log = log;
        nint iface = VpxInterop.vpx_codec_vp9_dx();
        if (iface == 0)
        {
            throw new NotSupportedException("This libvpx has no VP9 decoder.");
        }

        var cfg = new VpxInterop.VpxCodecDecCfg
        {
            Threads = (uint)Math.Clamp(Environment.ProcessorCount, 1, 16),
        };

        int last = 0;
        foreach (int abi in VpxVideoEncoder.AbiCandidates(VpxInterop.DecoderAbiVersion))
        {
            fixed (VpxInterop.VpxCodecCtx* ctx = &_ctx)
            {
                last = VpxInterop.vpx_codec_dec_init_ver(ctx, iface, &cfg, new CLong(0), abi);
            }

            if (last == VpxInterop.VpxCodecOk)
            {
                _log.LogInformation("VP9 decoder: libvpx {Version} (software)", VpxInterop.VersionString());
                return;
            }

            if (last != VpxInterop.VpxCodecAbiMismatch)
            {
                break;
            }
        }

        throw new NotSupportedException($"vpx_codec_dec_init_ver failed: {VpxInterop.Describe(last)} ({last}).");
    }

    public VideoCodec Codec => VideoCodec.Vp9;

    public bool OutputsGpuSurface => false;

    public bool TryDecode(ReadOnlySpan<byte> packet, bool isKeyFrame, out DecodedFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        frame = default;
        bool got = false;

        fixed (byte* data = packet)
        fixed (VpxInterop.VpxCodecCtx* ctx = &_ctx)
        {
            int err = VpxInterop.vpx_codec_decode(ctx, data, (uint)packet.Length, 0, new CLong(0));
            if (err != VpxInterop.VpxCodecOk)
            {
                // A corrupt or out-of-order packet is a dropped frame, not the end of the stream: the session
                // asks for a keyframe when the picture stops making sense.
                _log.LogDebug("VP9 decode refused a {Bytes}-byte packet: {Why}", packet.Length, VpxInterop.Describe(err));
                return false;
            }

            nint iter = 0;
            while (true)
            {
                byte* img = VpxInterop.vpx_codec_get_frame(ctx, &iter);
                if (img is null)
                {
                    break;
                }

                got = Convert(img);
            }
        }

        if (!got)
        {
            return false;
        }

        frame = new DecodedFrame
        {
            Width = _width,
            Height = _height,
            Format = PixelFormat.Bgra32,
            Cpu = new ReadOnlyMemory<byte>(_bgra, 0, _width * 4 * _height),
            Stride = _width * 4,
        };
        return true;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        fixed (VpxInterop.VpxCodecCtx* ctx = &_ctx)
        {
            VpxInterop.vpx_codec_destroy(ctx);
        }

        return ValueTask.CompletedTask;
    }

    private bool Convert(byte* img)
    {
        int width = (int)*(uint*)(img + VpxInterop.Img.DW);
        int height = (int)*(uint*)(img + VpxInterop.Img.DH);
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        _width = width;
        _height = height;
        int need = width * 4 * height;
        if (_bgra.Length < need)
        {
            _bgra = new byte[need];
        }

        byte** planes = (byte**)(img + VpxInterop.Img.Planes);
        int* strides = (int*)(img + VpxInterop.Img.Stride);
        int yStride = strides[0];
        int uvStride = strides[1];
        int uvHeight = (height + 1) / 2;

        // The planes are libvpx's, with padding libvpx chose, so each is described by its own stride rather
        // than assumed to be tightly packed.
        var y = new ReadOnlySpan<byte>(planes[0], yStride * height);
        var u = new ReadOnlySpan<byte>(planes[1], uvStride * uvHeight);
        var v = new ReadOnlySpan<byte>(planes[2], uvStride * uvHeight);
        PixelConversion.I420ToBgra(y, yStride, u, v, uvStride, width, height, _bgra, width * 4);
        return true;
    }
}
