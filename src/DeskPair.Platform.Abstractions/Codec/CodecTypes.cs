using DeskPair.Platform.Abstractions.Capture;

namespace DeskPair.Platform.Abstractions.Codec;

public enum VideoCodec
{
    H264,
    H265,
    Vp8,
    Vp9,
    Av1,
}

/// <summary>
/// How an encoder or decoder is reached. The distinction matters because the same GPU is reachable several
/// ways: NVENC answers both through its own SDK and as a Media Foundation transform, and the two differ in
/// what they will let the caller control.
/// </summary>
public enum CodecBackend
{
    /// <summary>A test double.</summary>
    Fake,

    /// <summary>Windows Media Foundation. Reaches every vendor's hardware, at the level Windows exposes.</summary>
    MediaFoundation,

    Nvenc,
    QuickSync,
    Amf,
    VideoToolbox,
    Vaapi,

    /// <summary>Software: OpenH264, libvpx, libaom.</summary>
    Software,
}

/// <summary>Who made the silicon, where that is known. <see cref="Unknown"/> covers software and anything unlabelled.</summary>
public enum GpuVendor
{
    Unknown,
    Nvidia,
    Intel,
    Amd,
    Apple,
}

/// <summary>
/// One encoder a machine can actually offer. This is what a factory advertises and what the host reports, so
/// a log line, the settings screen and a support question can all name the same thing.
/// </summary>
public sealed record EncoderDescriptor
{
    public required VideoCodec Codec { get; init; }

    public required CodecBackend Backend { get; init; }

    public GpuVendor Vendor { get; init; } = GpuVendor.Unknown;

    /// <summary>What the implementation calls itself, e.g. the MFT's friendly name.</summary>
    public required string Name { get; init; }

    public bool IsHardware { get; init; }

    /// <summary>True when it takes a GPU texture directly, so the frame never touches system memory.</summary>
    public bool AcceptsGpuSurface { get; init; }

    /// <summary>The adapter this encoder lives on; 0 when it is not tied to one. A texture cannot cross adapters.</summary>
    public long AdapterLuid { get; init; }

    public override string ToString() =>
        $"{Name} ({Codec}, {Backend}{(IsHardware ? ", hardware" : ", software")}{(AcceptsGpuSurface ? ", texture" : string.Empty)})";
}

[Flags]
public enum SupportedCodecs
{
    None = 0,
    H264Software = 1 << 0,
    H264Hardware = 1 << 1,
    H265Software = 1 << 2,
    H265Hardware = 1 << 3,
    Vp8Software = 1 << 4,
    Vp8Hardware = 1 << 5,
    Vp9Software = 1 << 6,
    Vp9Hardware = 1 << 7,
    Av1Software = 1 << 8,
    Av1Hardware = 1 << 9,
}

public static class SupportedCodecsExtensions
{
    public static bool Supports(this SupportedCodecs set, VideoCodec codec) => (set & Pair(codec)) != 0;

    /// <summary>The software and hardware bits for one codec.</summary>
    public static SupportedCodecs Pair(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => SupportedCodecs.H264Software | SupportedCodecs.H264Hardware,
        VideoCodec.H265 => SupportedCodecs.H265Software | SupportedCodecs.H265Hardware,
        VideoCodec.Vp8 => SupportedCodecs.Vp8Software | SupportedCodecs.Vp8Hardware,
        VideoCodec.Vp9 => SupportedCodecs.Vp9Software | SupportedCodecs.Vp9Hardware,
        VideoCodec.Av1 => SupportedCodecs.Av1Software | SupportedCodecs.Av1Hardware,
        _ => SupportedCodecs.None,
    };

    /// <summary>The single bit for one codec at one hardware-ness, which is what a descriptor contributes.</summary>
    public static SupportedCodecs Bit(VideoCodec codec, bool hardware) => (codec, hardware) switch
    {
        (VideoCodec.H264, false) => SupportedCodecs.H264Software,
        (VideoCodec.H264, true) => SupportedCodecs.H264Hardware,
        (VideoCodec.H265, false) => SupportedCodecs.H265Software,
        (VideoCodec.H265, true) => SupportedCodecs.H265Hardware,
        (VideoCodec.Vp8, false) => SupportedCodecs.Vp8Software,
        (VideoCodec.Vp8, true) => SupportedCodecs.Vp8Hardware,
        (VideoCodec.Vp9, false) => SupportedCodecs.Vp9Software,
        (VideoCodec.Vp9, true) => SupportedCodecs.Vp9Hardware,
        (VideoCodec.Av1, false) => SupportedCodecs.Av1Software,
        (VideoCodec.Av1, true) => SupportedCodecs.Av1Hardware,
        _ => SupportedCodecs.None,
    };

    /// <summary>Folds a set of descriptors into the older flag set, so existing callers keep working.</summary>
    public static SupportedCodecs ToFlags(this IEnumerable<EncoderDescriptor> descriptors)
    {
        SupportedCodecs all = SupportedCodecs.None;
        foreach (EncoderDescriptor d in descriptors)
        {
            all |= Bit(d.Codec, d.IsHardware);
        }

        return all;
    }
}

public sealed record VideoEncoderConfig(
    VideoCodec Codec,
    int Width,
    int Height,
    int Fps,
    int BitrateKbps,
    bool PreferHardware,
    PixelFormat InputFormat,
    GpuApi InputGpuApi,
    long AdapterLuid)
{
    /// <summary>
    /// Encoder names (as in <see cref="EncoderDescriptor.Name"/>) not to use. A vendor encoder that failed
    /// earlier in this session must not be handed back on the next attempt, or the caller retries the same
    /// broken driver forever. Excluding by name rather than by codec keeps the software encoder below it.
    /// </summary>
    public IReadOnlyCollection<string> Exclude { get; init; } = [];
}

public readonly struct EncodedPacket
{
    public required ReadOnlyMemory<byte> Data { get; init; }
    public required bool IsKeyFrame { get; init; }
    public long PtsTicks { get; init; }
}

public readonly struct DecodedFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required PixelFormat Format { get; init; }
    public long PtsTicks { get; init; }

    public ReadOnlyMemory<byte> Cpu { get; init; }
    public int Stride { get; init; }

    public GpuSurfaceHandle Gpu { get; init; }
    public bool IsGpuSurface => Gpu.IsValid;
}
