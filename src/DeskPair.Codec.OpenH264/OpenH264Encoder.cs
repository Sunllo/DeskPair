using Microsoft.Extensions.Logging;
using DeskPair.Codec.OpenH264.Native;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Codec;

namespace DeskPair.Codec.OpenH264;

/// <summary>Software H.264 through OpenH264; available on any platform that ships the library.</summary>
public sealed class OpenH264EncoderFactory : IVideoEncoderFactory
{
    private readonly ILoggerFactory _logs;

    public OpenH264EncoderFactory(ILoggerFactory logs)
    {
        _logs = logs;
    }

    public static bool IsAvailable => Native.OpenH264.TryLoad();

    public IReadOnlyList<EncoderDescriptor> Describe() => IsAvailable
        ? [new EncoderDescriptor { Codec = VideoCodec.H264, Backend = CodecBackend.Software, Name = "OpenH264", IsHardware = false }]
        : [];

    public SupportedCodecs Probe() => IsAvailable ? SupportedCodecs.H264Software : SupportedCodecs.None;

    public IVideoEncoder Create(VideoEncoderConfig config)
    {
        if (config.Codec != VideoCodec.H264)
        {
            throw new NotSupportedException($"{config.Codec} is not supported by OpenH264.");
        }

        if (!IsAvailable)
        {
            throw new NotSupportedException(Native.OpenH264.LoadError ?? "OpenH264 is not available.");
        }

        return new OpenH264Encoder(config, _logs.CreateLogger<OpenH264Encoder>());
    }
}

/// <summary>
/// Single-layer Constrained Baseline stream tuned for screen content: no periodic IDR (keyframes on
/// request), no frame skipping, CBR-ish bitrate mode. Input is I420; output is Annex-B.
/// </summary>
public sealed unsafe class OpenH264Encoder : IVideoEncoder
{
    private readonly VideoEncoderConfig _config;
    private readonly ILogger _log;
    private readonly int _inputBytes;
    private nint _encoder;
    private byte[] _output = new byte[1 << 20];
    private bool _forceKey = true;
    private bool _disposed;

    public OpenH264Encoder(VideoEncoderConfig config, ILogger log)
    {
        _config = config;
        _log = log;
        _inputBytes = PixelConversion.I420Size(config.Width, config.Height);
        nint enc;
        int rv = Native.OpenH264.WelsCreateSVCEncoder(&enc);
        if (rv != 0 || enc == 0)
        {
            throw new NotSupportedException($"WelsCreateSVCEncoder failed ({rv}).");
        }

        _encoder = enc;
        try
        {
            SEncParamExt p = default;
            Check(Native.OpenH264.EncGetDefaultParams(enc, &p), "GetDefaultParams");
            p.iUsageType = Native.OpenH264.UsageScreenContentRealTime;
            p.iPicWidth = config.Width;
            p.iPicHeight = config.Height;
            p.iTargetBitrate = config.BitrateKbps * 1000;
            p.iMaxBitrate = config.BitrateKbps * 1000 * 2;
            p.iRCMode = Native.OpenH264.RcBitrateMode;
            p.fMaxFrameRate = Math.Max(1, config.Fps);
            p.iTemporalLayerNum = 1;
            p.iSpatialLayerNum = 1;
            p.uiIntraPeriod = 0;
            p.bEnableFrameSkip = 0;
            p.iEntropyCodingModeFlag = 0; // CAVLC keeps Constrained Baseline for the widest decoder support
            p.iComplexityMode = Native.OpenH264.ComplexityLow;
            p.iMultipleThreadIdc = 0; // auto
            p.bEnableSceneChangeDetect = 1; // the validator insists for screen content; detected changes just become I-frames
            p.bEnableFrameCroppingFlag = 1;
            ref SSpatialLayerConfig layer = ref p.sSpatialLayers[0];
            layer.iVideoWidth = config.Width;
            layer.iVideoHeight = config.Height;
            layer.fFrameRate = p.fMaxFrameRate;
            layer.iSpatialBitrate = p.iTargetBitrate;
            layer.iMaxSpatialBitrate = p.iMaxBitrate;
            layer.uiProfileIdc = Native.OpenH264.ProfileBaseline;
            Check(Native.OpenH264.EncInitializeExt(enc, &p), "InitializeExt");
            int format = Native.OpenH264.VideoFormatI420;
            Check(Native.OpenH264.EncSetOption(enc, Native.OpenH264.EncoderOptionDataFormat, &format), "SetOption(DATAFORMAT)");
            Bitrate = config.BitrateKbps;
            _log.LogInformation("OpenH264 encoder {W}x{H} @ {Fps} fps, {Kbps} kbps", config.Width, config.Height, config.Fps, config.BitrateKbps);
        }
        catch
        {
            Native.OpenH264.WelsDestroySVCEncoder(enc);
            _encoder = 0;
            throw;
        }
    }

    public VideoCodec Codec => VideoCodec.H264;

    public EncoderDescriptor Descriptor { get; } = new()
    {
        Codec = VideoCodec.H264,
        Backend = CodecBackend.Software,
        Name = "OpenH264",
        IsHardware = false,
    };

    public bool IsHardware => false;

    public bool IsLatencyFree => true;

    public PixelFormat RequiredInputFormat => PixelFormat.I420;

    public int Bitrate { get; private set; }

    /// <summary>Whether OpenH264 took the last <see cref="SetBitrate"/>: it refuses a rate it will not use rather than clamp it.</summary>
    internal bool BitrateAccepted { get; private set; } = true;

    /// <summary>
    /// Moves the target, and the ceiling to twice the target with it.
    ///
    /// OpenH264 checks a target against the layer's own ceiling, which the overall ceiling does not move. Set once at
    /// twice the starting rate, it refused every later rise past that ("MaxSpatialBitrate (3000000) should be larger
    /// than SpatialBitrate (10080000)") and the encoder stayed at the rate it began with, however good the link. So
    /// both ceilings move, before the target on the way up and after it on the way down: no step then leaves a
    /// target above its ceiling.
    /// </summary>
    public void SetBitrate(int kbps)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int target = kbps * 1000;
        bool up = kbps > Bitrate;
        bool accepted = true;
        if (up)
        {
            accepted &= SetCeiling(target * 2);
        }

        accepted &= SetBitrateOption(Native.OpenH264.EncoderOptionBitrate, Native.OpenH264.SpatialLayerAll, target);
        if (!up)
        {
            accepted &= SetCeiling(target * 2);
        }

        Bitrate = kbps;
        BitrateAccepted = accepted;
        if (!accepted)
        {
            _log.LogWarning("OpenH264 refused {Kbps} kbps", kbps);
        }
    }

    private bool SetCeiling(int bps) =>
        SetBitrateOption(Native.OpenH264.EncoderOptionMaxBitrate, Native.OpenH264.SpatialLayerAll, bps) &
        SetBitrateOption(Native.OpenH264.EncoderOptionMaxBitrate, Native.OpenH264.SpatialLayer0, bps);

    private bool SetBitrateOption(int option, int layer, int bps)
    {
        var info = new SBitrateInfo { iLayer = layer, iBitrate = bps };
        return Native.OpenH264.EncSetOption(_encoder, option, &info) == 0;
    }

    public void RequestKeyFrame() => _forceKey = true;

    public bool TryEncode(ReadOnlySpan<byte> input, int stride, long ptsTicks, out EncodedPacket packet)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (input.Length < PixelConversion.I420Size(stride, _config.Height))
        {
            throw new ArgumentException($"Expected at least {_inputBytes} bytes of I420.", nameof(input));
        }

        if (_forceKey)
        {
            _forceKey = false;
            Native.OpenH264.EncForceIntraFrame(_encoder, true);
        }

        int height = _config.Height;
        int chromaStride = stride / 2;
        int chromaHeight = (height + 1) / 2;
        var info = new SFrameBSInfo();
        fixed (byte* src = input)
        {
            var pic = new SSourcePicture
            {
                iColorFormat = Native.OpenH264.VideoFormatI420,
                iPicWidth = _config.Width,
                iPicHeight = height,
                uiTimeStamp = ptsTicks,
                pData0 = src,
                pData1 = src + stride * height,
                pData2 = src + stride * height + chromaStride * chromaHeight,
            };
            pic.iStride[0] = stride;
            pic.iStride[1] = chromaStride;
            pic.iStride[2] = chromaStride;
            Check(Native.OpenH264.EncEncodeFrame(_encoder, &pic, &info), "EncodeFrame");
        }

        if (info.eFrameType == Native.OpenH264.FrameTypeSkip || info.eFrameType == Native.OpenH264.FrameTypeInvalid || info.iLayerNum <= 0)
        {
            packet = default;
            return false;
        }

        int total = 0;
        for (int i = 0; i < info.iLayerNum; i++)
        {
            ref SLayerBSInfo layer = ref info.sLayerInfo[i];
            int bytes = 0;
            for (int n = 0; n < layer.iNalCount; n++)
            {
                bytes += layer.pNalLengthInByte[n];
            }

            if (_output.Length < total + bytes)
            {
                Array.Resize(ref _output, Math.Max(_output.Length * 2, total + bytes));
            }

            new ReadOnlySpan<byte>(layer.pBsBuf, bytes).CopyTo(_output.AsSpan(total));
            total += bytes;
        }

        if (total == 0)
        {
            packet = default;
            return false;
        }

        bool key = info.eFrameType is Native.OpenH264.FrameTypeIdr or Native.OpenH264.FrameTypeI;
        packet = new EncodedPacket { Data = new ReadOnlyMemory<byte>(_output, 0, total), IsKeyFrame = key, PtsTicks = ptsTicks };
        return true;
    }

    public bool TryEncode(in GpuSurfaceHandle texture, long ptsTicks, out EncodedPacket packet)
    {
        packet = default;
        return false;
    }

    private static void Check(int rv, string what)
    {
        if (rv != 0)
        {
            throw new InvalidOperationException($"OpenH264 {what} failed ({rv}).");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        if (_encoder != 0)
        {
            Native.OpenH264.EncUninitialize(_encoder);
            Native.OpenH264.WelsDestroySVCEncoder(_encoder);
            _encoder = 0;
        }

        return ValueTask.CompletedTask;
    }
}
