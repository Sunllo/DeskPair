using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using DeskPair.Codec.Vpx.Native;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Codec;

namespace DeskPair.Codec.Vpx;

/// <summary>
/// VP9 through libvpx. This is the royalty-free encoder: the source is BSD-3 and Google grants the patents,
/// so it is the one codec this project can offer anywhere without either a hardware encoder or somebody
/// else's patent licence. On Linux without VAAPI, and on Windows without a hardware encoder, it is the only
/// encoder there is.
/// </summary>
public sealed class VpxVideoEncoderFactory : IVideoEncoderFactory
{
    private readonly ILoggerFactory _logs;

    public VpxVideoEncoderFactory(ILoggerFactory logs)
    {
        _logs = logs;
    }

    public IReadOnlyList<EncoderDescriptor> Describe() => VpxInterop.IsAvailable
        ?
        [
            new EncoderDescriptor
            {
                Codec = VideoCodec.Vp9,
                Backend = CodecBackend.Software,
                Name = $"libvpx VP9 ({VpxInterop.VersionString()})",
                IsHardware = false,
            },
        ]
        : [];

    public IVideoEncoder Create(VideoEncoderConfig config) => config.Codec == VideoCodec.Vp9
        ? new VpxVideoEncoder(config, _logs.CreateLogger<VpxVideoEncoder>())
        : throw new NotSupportedException($"{config.Codec} is not supported by libvpx here; only VP9 is wired up.");
}

/// <summary>
/// One VP9 encoder. Configured for a desk rather than for a film: constant bitrate, a short buffer, no
/// lookahead and no B-frames, with keyframes only when asked for. Input is I420, which the capture pipeline
/// already knows how to produce, and each call produces exactly one packet because the lookahead is off.
/// </summary>
public sealed unsafe class VpxVideoEncoder : IVideoEncoder
{
    /// <summary>
    /// libvpx's "speed" control. 5-8 is the documented range for live encoding; 7 is what RustDesk and
    /// WebRTC both settle on, trading a little quality for keeping up with a moving screen on a CPU that is
    /// also compositing it.
    /// </summary>
    private const int CpuUsed = 7;

    /// <summary>Millisecond timebase, so the presentation timestamps the caller already has pass straight through.</summary>
    private const int TimebaseDen = 1000;

    private readonly VideoEncoderConfig _config;
    private readonly ILogger _log;
    private readonly byte[] _cfg = new byte[VpxInterop.EncCfg.Size];
    private readonly int _ySize;
    private readonly int _uvSize;
    private VpxInterop.VpxCodecCtx _ctx;
    private byte[] _output = new byte[1 << 20];
    private bool _forceKey = true;
    private bool _disposed;

    public VpxVideoEncoder(VideoEncoderConfig config, ILogger log)
    {
        if (!VpxInterop.IsAvailable)
        {
            throw new NotSupportedException("libvpx is not present; see native/libvpx/README.md.");
        }

        _config = config;
        _log = log;
        Bitrate = config.BitrateKbps;
        _ySize = config.Width * config.Height;
        _uvSize = ((config.Width + 1) / 2) * ((config.Height + 1) / 2);

        nint iface = VpxInterop.vpx_codec_vp9_cx();
        if (iface == 0)
        {
            throw new NotSupportedException("This libvpx has no VP9 encoder.");
        }

        fixed (byte* cfg = _cfg)
        {
            VpxInterop.Check(VpxInterop.vpx_codec_enc_config_default(iface, cfg, 0), "vpx_codec_enc_config_default");
            if (VpxInterop.EncCfg.Disagreement(_cfg) is { } where)
            {
                // Refusing here is the whole point of the check: writing the intended fields into a struct
                // that has a different shape configures something else entirely, and the failure would show
                // up as bad pictures rather than as an error.
                throw new NotSupportedException(
                    $"This libvpx ({VpxInterop.VersionString()}) lays out vpx_codec_enc_cfg_t differently than this binding expects: {where}.");
            }

            Configure(_cfg, config, Bitrate);
            Initialise(cfg, iface);
        }

        Control(VpxInterop.Vp8eSetCpuUsed, CpuUsed, "cpu-used");
        Control(VpxInterop.Vp9eSetRowMt, 1, "row-mt");
        Control(VpxInterop.Vp9eSetTileColumns, TileColumnsLog2(config.Width), "tile-columns");

        // A desk is screen content: large flat areas, hard edges, text. Telling the encoder so is worth more
        // than any rate-control tuning, and costs nothing when the picture happens to be a video instead.
        Control(VpxInterop.Vp9eSetTuneContent, VpxInterop.Vp9ContentScreen, "tune-content");

        // The picture was converted with BT.601 at studio range (PixelConversion), and the stream should say so.
        // Unsaid, a decoder that trusts the stream guesses, and phones guess BT.709 for anything HD, which shifts
        // every saturated colour. The desktop's own decoder converts back with PixelConversion either way.
        Control(VpxInterop.Vp9eSetColorSpace, VpxInterop.VpxCsBt601, "color-space");

        _log.LogInformation(
            "Encoder: {Descriptor} {W}x{H} @ {Fps} fps, {Kbps} kbps", Descriptor, config.Width, config.Height, config.Fps, Bitrate);
    }

    public VideoCodec Codec => VideoCodec.Vp9;

    public EncoderDescriptor Descriptor => field ??= new EncoderDescriptor
    {
        Codec = VideoCodec.Vp9,
        Backend = CodecBackend.Software,
        Name = $"libvpx VP9 ({VpxInterop.VersionString()})",
        IsHardware = false,
    };

    public bool IsHardware => false;

    /// <summary>Software: nothing needs a steady feed, so the caller need not re-submit a still picture.</summary>
    public bool IsLatencyFree => true;

    public PixelFormat RequiredInputFormat => PixelFormat.I420;

    public int Bitrate { get; private set; }

    public void SetBitrate(int kbps)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (kbps == Bitrate)
        {
            return;
        }

        Bitrate = kbps;
        fixed (byte* cfg = _cfg)
        fixed (VpxInterop.VpxCodecCtx* ctx = &_ctx)
        {
            VpxInterop.EncCfg.Set(_cfg, VpxInterop.EncCfg.RcTargetBitrate, (uint)kbps);
            int err = VpxInterop.vpx_codec_enc_config_set(ctx, cfg);
            if (err != VpxInterop.VpxCodecOk)
            {
                _log.LogDebug("libvpx refused a bitrate change to {Kbps} kbps: {Why}", kbps, VpxInterop.Describe(err));
            }
        }
    }

    public void RequestKeyFrame() => _forceKey = true;

    public bool TryEncode(ReadOnlySpan<byte> input, int stride, long ptsTicks, out EncodedPacket packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        packet = default;
        int need = _ySize + (2 * _uvSize);
        if (input.Length < need)
        {
            throw new ArgumentException($"Expected {need} bytes of I420.", nameof(input));
        }

        bool forced = _forceKey;
        _forceKey = false;
        int total = 0;
        bool key = false;

        fixed (byte* data = input)
        fixed (VpxInterop.VpxCodecCtx* ctx = &_ctx)
        {
            byte* image = stackalloc byte[VpxInterop.Img.Size];
            new Span<byte>(image, VpxInterop.Img.Size).Clear();
            Describe(image, data);

            VpxInterop.Check(
                VpxInterop.vpx_codec_encode(
                    ctx,
                    image,
                    ptsTicks,
                    new CULong(1),
                    new CLong(forced ? 1 : 0), // VPX_EFLAG_FORCE_KF
                    new CULong(VpxInterop.VpxDlRealtime)),
                "vpx_codec_encode");

            nint iter = 0;
            while (true)
            {
                byte* pkt = VpxInterop.vpx_codec_get_cx_data(ctx, &iter);
                if (pkt is null)
                {
                    break;
                }

                if (*(int*)(pkt + VpxInterop.CxPkt.Kind) != VpxInterop.VpxCodecCxFramePkt)
                {
                    continue; // statistics or PSNR; not our business in one-pass realtime
                }

                nint buf = *(nint*)(pkt + VpxInterop.CxPkt.FrameBuf);
                int size = checked((int)*(nuint*)(pkt + VpxInterop.CxPkt.FrameSz));
                uint flags = *(uint*)(pkt + VpxInterop.CxPkt.FrameFlags);
                key |= (flags & VpxInterop.VpxFrameIsKey) != 0;
                if (_output.Length < total + size)
                {
                    Array.Resize(ref _output, Math.Max(_output.Length * 2, total + size));
                }

                new ReadOnlySpan<byte>((void*)buf, size).CopyTo(_output.AsSpan(total));
                total += size;
            }
        }

        if (total == 0)
        {
            // With no lookahead this means the rate control dropped the frame; the caller treats it as a
            // frame that produced nothing, which is exactly what it is.
            return false;
        }

        packet = new EncodedPacket { Data = new ReadOnlyMemory<byte>(_output, 0, total), IsKeyFrame = key, PtsTicks = ptsTicks };
        return true;
    }

    public bool TryEncode(in GpuSurfaceHandle texture, long ptsTicks, out EncodedPacket packet)
    {
        packet = default;
        return false;
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

    /// <summary>
    /// The settings that make this a desk encoder rather than a file encoder. Every one of them is a latency
    /// decision: no lookahead, a buffer measured in a few frames rather than seconds, and keyframes only when
    /// the session asks for one, because a periodic keyframe on a still screen is pure waste.
    /// </summary>
    private static void Configure(byte[] cfg, VideoEncoderConfig config, int kbps)
    {
        void Set(int offset, uint value) => VpxInterop.EncCfg.Set(cfg, offset, value);

        Set(VpxInterop.EncCfg.GW, (uint)config.Width);
        Set(VpxInterop.EncCfg.GH, (uint)config.Height);
        Set(VpxInterop.EncCfg.GTimebaseNum, 1);
        Set(VpxInterop.EncCfg.GTimebaseDen, TimebaseDen);
        Set(VpxInterop.EncCfg.GThreads, (uint)Math.Clamp(Environment.ProcessorCount, 1, 16));
        Set(VpxInterop.EncCfg.GProfile, 0); // 8-bit 4:2:0
        Set(VpxInterop.EncCfg.GErrorResilient, VpxInterop.VpxErrorResilientDefault);
        Set(VpxInterop.EncCfg.GPass, 0); // VPX_RC_ONE_PASS

        // No lookahead. The default is 25 frames, which for a screen means the picture on the far end is most
        // of a second behind the hand on the mouse. It also makes each encode produce exactly one packet.
        Set(VpxInterop.EncCfg.GLagInFrames, 0);

        Set(VpxInterop.EncCfg.RcEndUsage, VpxInterop.VpxCbr);
        Set(VpxInterop.EncCfg.RcTargetBitrate, (uint)kbps);
        Set(VpxInterop.EncCfg.RcUndershootPct, 95);
        Set(VpxInterop.EncCfg.RcOvershootPct, 15);

        // Never let the encoder drop a frame on its own: the session has its own congestion control that
        // decides which ticks to skip, and a silent drop here would leave it accounting for a frame that
        // never existed.
        Set(VpxInterop.EncCfg.RcDropframeThresh, 0);

        // A one-second buffer would let a single frame take a second to arrive. These are the values WebRTC
        // uses for realtime VP9, in milliseconds of the target bitrate.
        Set(VpxInterop.EncCfg.RcBufSz, 1000);
        Set(VpxInterop.EncCfg.RcBufInitialSz, 500);
        Set(VpxInterop.EncCfg.RcBufOptimalSz, 600);

        Set(VpxInterop.EncCfg.RcMinQuantizer, 4);
        Set(VpxInterop.EncCfg.RcMaxQuantizer, 56);

        // The session asks for keyframes when a viewer needs one; a fixed interval only spends bandwidth.
        Set(VpxInterop.EncCfg.KfMode, VpxInterop.VpxKfDisabled);
    }

    /// <summary>
    /// Points a <c>vpx_image_t</c> at the caller's I420 buffer. Nothing is copied: libvpx reads the planes
    /// through these pointers for the duration of the encode call, which is why the caller's span is pinned
    /// around it. Written by hand rather than through <c>vpx_img_wrap</c> so the chroma planes are placed the
    /// way <c>PixelConversion</c> writes them, at <c>(h + 1) / 2</c> rows each, which is also how the encoder
    /// itself sizes them; wrap steps by <c>h / 2</c> and so disagrees on every odd height.
    /// </summary>
    private void Describe(byte* image, byte* data)
    {
        int uvStride = (_config.Width + 1) / 2;
        void SetInt(int offset, int value) => *(int*)(image + offset) = value;

        SetInt(VpxInterop.Img.Fmt, VpxInterop.VpxImgFmtI420);
        SetInt(VpxInterop.Img.W, _config.Width);
        SetInt(VpxInterop.Img.H, _config.Height);
        SetInt(VpxInterop.Img.BitDepth, 8);
        SetInt(VpxInterop.Img.DW, _config.Width);
        SetInt(VpxInterop.Img.DH, _config.Height);
        SetInt(VpxInterop.Img.XChromaShift, 1);
        SetInt(VpxInterop.Img.YChromaShift, 1);
        SetInt(VpxInterop.Img.Bps, 12);
        SetInt(VpxInterop.Img.Stride, _config.Width);
        SetInt(VpxInterop.Img.Stride + 4, uvStride);
        SetInt(VpxInterop.Img.Stride + 8, uvStride);

        byte** planes = (byte**)(image + VpxInterop.Img.Planes);
        planes[0] = data;
        planes[1] = data + _ySize;
        planes[2] = data + _ySize + _uvSize;
        *(byte**)(image + VpxInterop.Img.ImgData) = data;
    }

    /// <summary>
    /// Tile columns are given as a log2 count and libvpx needs at least 256 pixels of width per tile, so a
    /// fixed number would be silently clamped on a small display and leave threads idle on a large one.
    /// </summary>
    private static int TileColumnsLog2(int width)
    {
        int log2 = 0;
        while (log2 < 6 && (width >> (log2 + 1)) >= 256)
        {
            log2++;
        }

        return log2;
    }

    /// <summary>
    /// Initialises the codec, walking a small range of ABI versions. VPX_ENCODER_ABI_VERSION is a
    /// compile-time constant that moves with the library, and a mismatch is reported cleanly rather than
    /// crashing, so trying the neighbours of the version this was written against accepts a libvpx a release
    /// or two either side instead of demanding one exact build.
    /// </summary>
    private void Initialise(byte* cfg, nint iface)
    {
        int last = 0;
        foreach (int abi in AbiCandidates(VpxInterop.EncoderAbiVersion))
        {
            fixed (VpxInterop.VpxCodecCtx* ctx = &_ctx)
            {
                last = VpxInterop.vpx_codec_enc_init_ver(ctx, iface, cfg, new CLong(0), abi);
            }

            if (last == VpxInterop.VpxCodecOk)
            {
                return;
            }

            if (last != VpxInterop.VpxCodecAbiMismatch)
            {
                break;
            }
        }

        throw new NotSupportedException($"vpx_codec_enc_init_ver failed: {VpxInterop.Describe(last)} ({last}).");
    }

    internal static IEnumerable<int> AbiCandidates(int expected)
    {
        yield return expected;
        for (int d = 1; d <= 4; d++)
        {
            yield return expected - d;
            yield return expected + d;
        }
    }

    private void Control(int id, int value, string what)
    {
        fixed (VpxInterop.VpxCodecCtx* ctx = &_ctx)
        {
            int err = VpxInterop.vpx_codec_control_(ctx, id, value);
            if (err != VpxInterop.VpxCodecOk)
            {
                // Controls are tuning, not correctness: an older libvpx that does not know one of these still
                // encodes a correct stream, just a slower or slightly larger one.
                _log.LogDebug("libvpx refused {What}={Value}: {Why}", what, value, VpxInterop.Describe(err));
            }
        }
    }
}
