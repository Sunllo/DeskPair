using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Codec.Vpx.Native;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Codec;

namespace DeskPair.Codec.Vpx.Tests;

/// <summary>
/// These need a libvpx to load and return early without one, the way the rest of the suite treats a machine
/// that lacks a piece of hardware. A run with no libvpx present therefore proves nothing about the codec —
/// that is what <see cref="VpxLayoutTests"/> is for — but it does prove the absence is handled.
/// </summary>
public class VpxCodecTests
{
    private const int W = 320;
    private const int H = 240;

    private static bool Available => VpxInterop.IsAvailable;

    /// <summary>A moving picture with hard edges and flat areas: a desk, not a gradient.</summary>
    private static byte[] Screen(int frame)
    {
        byte[] bgra = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int o = ((y * W) + x) * 4;
                bool bar = ((x + (frame * 4)) / 32 % 2) == 0;
                bool row = (y / 24 % 2) == 0;
                byte v = (byte)(bar == row ? 235 : 20);
                bgra[o] = v;
                bgra[o + 1] = v;
                bgra[o + 2] = (byte)(bar ? 200 : 40);
                bgra[o + 3] = 255;
            }
        }

        return bgra;
    }

    [Fact]
    public void The_factory_only_offers_vp9_when_libvpx_is_there()
    {
        IVideoEncoderFactory encoders = new VpxVideoEncoderFactory(NullLoggerFactory.Instance);
        IVideoDecoderFactory decoders = new VpxVideoDecoderFactory(NullLoggerFactory.Instance);

        if (!Available)
        {
            encoders.Describe().ShouldBeEmpty();
            encoders.Probe().ShouldBe(SupportedCodecs.None);
            decoders.Probe().ShouldBe(SupportedCodecs.None);
            return;
        }

        encoders.Describe().ShouldHaveSingleItem();
        encoders.Describe()[0].Codec.ShouldBe(VideoCodec.Vp9);
        encoders.Describe()[0].Backend.ShouldBe(CodecBackend.Software);
        encoders.Describe()[0].IsHardware.ShouldBeFalse();
        encoders.Probe().ShouldBe(SupportedCodecs.Vp9Software);
        decoders.Probe().ShouldBe(SupportedCodecs.Vp9Software);
    }

    [Fact]
    public async Task Encode_then_decode_reproduces_the_picture_within_tolerance()
    {
        if (!Available)
        {
            return;
        }

        var encoders = new VpxVideoEncoderFactory(NullLoggerFactory.Instance);
        var decoders = new VpxVideoDecoderFactory(NullLoggerFactory.Instance);
        await using IVideoEncoder encoder = encoders.Create(
            new VideoEncoderConfig(VideoCodec.Vp9, W, H, 30, 2000, PreferHardware: false, PixelFormat.I420, GpuApi.None, 0));
        await using IVideoDecoder decoder = decoders.Create(VideoCodec.Vp9, GpuApi.None, 0);

        encoder.RequiredInputFormat.ShouldBe(PixelFormat.I420);
        encoder.Codec.ShouldBe(VideoCodec.Vp9);
        encoder.IsHardware.ShouldBeFalse();

        byte[] i420 = new byte[PixelConversion.I420Size(W, H)];
        int packets = 0, decoded = 0, keyframes = 0;
        double worstError = 0;
        for (int i = 0; i < 20; i++)
        {
            byte[] bgra = Screen(i);
            PixelConversion.BgraToI420(bgra, W * 4, W, H, i420);
            if (i == 10)
            {
                encoder.RequestKeyFrame();
            }

            if (!encoder.TryEncode(i420, W, i * 33, out EncodedPacket packet))
            {
                continue;
            }

            packets++;
            packet.Data.Length.ShouldBeGreaterThan(0);
            if (packet.IsKeyFrame)
            {
                keyframes++;
            }

            if (decoder.TryDecode(packet.Data.Span, packet.IsKeyFrame, out DecodedFrame frame))
            {
                decoded++;
                frame.Width.ShouldBe(W);
                frame.Height.ShouldBe(H);
                frame.Format.ShouldBe(PixelFormat.Bgra32);
                worstError = Math.Max(worstError, MeanAbsError(bgra, frame.Cpu.Span, frame.Stride));
            }
        }

        packets.ShouldBe(20, "no lookahead means one packet per frame submitted");
        decoded.ShouldBe(20);
        keyframes.ShouldBe(2, "the first frame, and the one that was asked for");
        worstError.ShouldBeLessThan(12, "lossy, but the picture must survive");
    }

    /// <summary>
    /// The first packet has to be a keyframe or the far end has nothing to start from, and the keyframe flag
    /// is read at a platform-dependent offset, so this is the test that catches that offset being wrong.
    /// </summary>
    [Fact]
    public async Task The_first_packet_is_a_keyframe_and_a_later_one_is_not()
    {
        if (!Available)
        {
            return;
        }

        var encoders = new VpxVideoEncoderFactory(NullLoggerFactory.Instance);
        await using IVideoEncoder encoder = encoders.Create(
            new VideoEncoderConfig(VideoCodec.Vp9, W, H, 30, 1000, PreferHardware: false, PixelFormat.I420, GpuApi.None, 0));
        byte[] i420 = new byte[PixelConversion.I420Size(W, H)];

        PixelConversion.BgraToI420(Screen(0), W * 4, W, H, i420);
        encoder.TryEncode(i420, W, 0, out EncodedPacket first).ShouldBeTrue();
        first.IsKeyFrame.ShouldBeTrue();

        PixelConversion.BgraToI420(Screen(1), W * 4, W, H, i420);
        encoder.TryEncode(i420, W, 33, out EncodedPacket second).ShouldBeTrue();
        second.IsKeyFrame.ShouldBeFalse("keyframes are disabled unless asked for, so a second one is waste");
        second.Data.Length.ShouldBeLessThan(first.Data.Length, "a delta frame of a nearly still screen");
    }

    [Fact]
    public async Task The_bitrate_can_be_changed_mid_stream()
    {
        if (!Available)
        {
            return;
        }

        var encoders = new VpxVideoEncoderFactory(NullLoggerFactory.Instance);
        await using IVideoEncoder encoder = encoders.Create(
            new VideoEncoderConfig(VideoCodec.Vp9, W, H, 30, 1000, PreferHardware: false, PixelFormat.I420, GpuApi.None, 0));

        encoder.SetBitrate(4000);

        byte[] i420 = new byte[PixelConversion.I420Size(W, H)];
        PixelConversion.BgraToI420(Screen(0), W * 4, W, H, i420);
        encoder.TryEncode(i420, W, 0, out _).ShouldBeTrue("the encoder still works after being reconfigured");
    }

    [Fact]
    public async Task A_packet_that_is_not_vp9_is_dropped_rather_than_fatal()
    {
        if (!Available)
        {
            return;
        }

        var decoders = new VpxVideoDecoderFactory(NullLoggerFactory.Instance);
        await using IVideoDecoder decoder = decoders.Create(VideoCodec.Vp9, GpuApi.None, 0);

        decoder.TryDecode(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, isKeyFrame: true, out _).ShouldBeFalse();
        decoder.TryDecode([], isKeyFrame: false, out _).ShouldBeFalse();
    }

    private static double MeanAbsError(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, int actualStride)
    {
        long sum = 0;
        long count = 0;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int a = ((y * W) + x) * 4;
                int b = (y * actualStride) + (x * 4);
                for (int c = 0; c < 3; c++)
                {
                    sum += Math.Abs(expected[a + c] - actual[b + c]);
                    count++;
                }
            }
        }

        return (double)sum / count;
    }
}
