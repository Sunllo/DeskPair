using DeskPair.Platform.Abstractions.Capture;

namespace DeskPair.Platform.Abstractions.Codec;

public interface IVideoEncoder : IAsyncDisposable
{
    VideoCodec Codec { get; }

    /// <summary>
    /// Which implementation this actually is. Worth carrying across the interface: "the picture is soft" and
    /// "the CPU is pinned" are answered by knowing whether the frame went to NVENC or to a software encoder,
    /// and until now that was decided inside the platform layer and only ever mentioned in one log line.
    /// </summary>
    EncoderDescriptor Descriptor { get; }

    bool IsHardware { get; }

    /// <summary>
    /// False for encoders that need a steady input feed (most hardware encoders); the caller
    /// then re-submits the last frame while the screen is static.
    /// </summary>
    bool IsLatencyFree { get; }

    PixelFormat RequiredInputFormat { get; }

    void SetBitrate(int kbps);

    /// <summary>Tells the rate control the new frame cadence after QoS changes it; encoders that cannot adjust ignore it.</summary>
    void SetFrameRate(int fps)
    {
    }

    /// <summary>
    /// Bitrate the link can actually carry (before any frame-rate compensation), so rate control can size its
    /// buffer and keep single frames inside a fraction of a second on the wire. Encoders without one ignore it.
    /// </summary>
    void SetLinkBitrate(int kbps)
    {
    }

    void RequestKeyFrame();

    /// <summary>Encodes a CPU frame in <see cref="RequiredInputFormat"/>. Returns false when no packet was produced.</summary>
    bool TryEncode(ReadOnlySpan<byte> input, int stride, long ptsTicks, out EncodedPacket packet);

    /// <summary>Encodes a GPU surface without a CPU round-trip. Only valid when the encoder was created for GPU input.</summary>
    bool TryEncode(in GpuSurfaceHandle texture, long ptsTicks, out EncodedPacket packet);

    /// <summary>
    /// Takes a packet whose frame went in on an earlier <c>TryEncode</c> but was not ready when that call returned
    /// (an asynchronous hardware encoder that took longer than it was waited for), without submitting anything.
    /// Otherwise that packet only comes out with the next submission, which on a still screen may be minutes away.
    /// Encoders that always answer within the call have nothing waiting.
    /// </summary>
    bool TryCollect(out EncodedPacket packet)
    {
        packet = default;
        return false;
    }
}

public interface IVideoEncoderFactory
{
    /// <summary>
    /// Every encoder this factory can offer on this machine, best first. The default folds down to
    /// <see cref="Probe"/>'s flags, so a factory need only implement one of the two.
    /// </summary>
    IReadOnlyList<EncoderDescriptor> Describe() => [];

    SupportedCodecs Probe() => Describe().ToFlags();

    IVideoEncoder Create(VideoEncoderConfig config);
}

public interface IVideoDecoder : IAsyncDisposable
{
    VideoCodec Codec { get; }

    bool OutputsGpuSurface { get; }

    /// <summary>
    /// Feeds one access unit. The returned frame (if any) is valid until the next call;
    /// callers copy or present it before decoding again.
    /// </summary>
    bool TryDecode(ReadOnlySpan<byte> packet, bool isKeyFrame, out DecodedFrame frame);
}

public interface IVideoDecoderFactory
{
    SupportedCodecs Probe();

    IVideoDecoder Create(VideoCodec codec, GpuApi preferredOutput, long adapterLuid);
}
