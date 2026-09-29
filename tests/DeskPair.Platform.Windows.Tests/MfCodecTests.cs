using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Platform.Windows.Codec;

namespace DeskPair.Platform.Windows.Tests;

public class MfCodecTests
{
    private const int W = 320;
    private const int H = 240;

    private static byte[] Gradient(int frame)
    {
        byte[] bgra = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int o = (y * W + x) * 4;
                bgra[o] = (byte)((x + frame * 3) & 0xFF);
                bgra[o + 1] = (byte)((y + frame * 2) & 0xFF);
                bgra[o + 2] = (byte)(((x + y) / 4) & 0xFF);
                bgra[o + 3] = 255;
            }
        }

        return bgra;
    }

    /// <summary>
    /// Which codecs this machine can both encode and decode. H.264 is everywhere; H.265 needs a vendor MFT or
    /// the HEVC Video Extensions, and AV1 needs silicon that encodes it, so a machine without them is not a
    /// failure. Probing rather than skipping on a hardcoded list is the point: it is the same call the host
    /// makes when it decides what to offer.
    /// </summary>
    private static bool Available(VideoCodec codec) =>
        new MfVideoEncoderFactory(NullLoggerFactory.Instance).Probe().Supports(codec)
        && new MfVideoDecoderFactory(NullLoggerFactory.Instance).Probe().Supports(codec)
        && MediaFoundationEncoding.CanEncode(codec);

    [Theory]
    [InlineData(VideoCodec.H264)]
    [InlineData(VideoCodec.H265)]
    [InlineData(VideoCodec.Av1)]
    public async Task Encode_then_decode_reproduces_the_picture_within_tolerance(VideoCodec codec)
    {
        if (!Available(codec))
        {
            return;
        }

        var encoders = new MfVideoEncoderFactory(NullLoggerFactory.Instance);
        var decoders = new MfVideoDecoderFactory(NullLoggerFactory.Instance);
        await using IVideoEncoder encoder = encoders.Create(new VideoEncoderConfig(codec, W, H, 30, 2000, false, PixelFormat.Nv12, GpuApi.None, 0));
        await using IVideoDecoder decoder = decoders.Create(codec, GpuApi.None, 0);
        encoder.Codec.ShouldBe(codec);
        encoder.Descriptor.Codec.ShouldBe(codec);
        decoder.Codec.ShouldBe(codec);
        encoder.RequiredInputFormat.ShouldBe(PixelFormat.Nv12);

        byte[] nv12 = new byte[PixelConversion.Nv12Size(W, H)];
        int packets = 0, decoded = 0, keyframes = 0;
        double worstError = 0;
        for (int i = 0; i < 30; i++)
        {
            byte[] bgra = Gradient(i);
            PixelConversion.BgraToNv12(bgra, W * 4, W, H, nv12);
            if (i == 15)
            {
                encoder.RequestKeyFrame();
            }

            if (!encoder.TryEncode(nv12, W, i, out EncodedPacket packet))
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

        packets.ShouldBeGreaterThanOrEqualTo(25);
        decoded.ShouldBeGreaterThanOrEqualTo(20);
        keyframes.ShouldBeGreaterThanOrEqualTo(2); // first frame + requested
        worstError.ShouldBeLessThan(12, "lossy but the picture must survive");
    }

    [Theory]
    [InlineData(VideoCodec.H264)]
    [InlineData(VideoCodec.H265)]
    public async Task First_packet_is_a_keyframe_and_carries_parameter_sets(VideoCodec codec)
    {
        if (!Available(codec))
        {
            return;
        }

        var encoders = new MfVideoEncoderFactory(NullLoggerFactory.Instance);
        await using IVideoEncoder encoder = encoders.Create(new VideoEncoderConfig(codec, W, H, 30, 1000, false, PixelFormat.Nv12, GpuApi.None, 0));
        byte[] nv12 = new byte[PixelConversion.Nv12Size(W, H)];
        PixelConversion.BgraToNv12(Gradient(0), W * 4, W, H, nv12);
        EncodedPacket first = default;
        for (int i = 0; i < 5 && !encoder.TryEncode(nv12, W, i, out first); i++)
        {
        }

        first.Data.Length.ShouldBeGreaterThan(0);
        first.IsKeyFrame.ShouldBeTrue();
        MfVideoEncoder.LooksLikeKeyframe(codec, first.Data.Span).ShouldBeTrue();
    }

    /// <summary>
    /// H.265 puts nal_unit_type in bits 1..6 of the first header byte, H.264 in the low 5 bits of its only
    /// one. Reading one with the other mask does not fail, it answers wrongly, so both readings are pinned to
    /// hand-built NAL units rather than left to whatever the hardware encoder happens to emit.
    /// </summary>
    [Theory]
    [InlineData(VideoCodec.H264, 0x65, true)]   // IDR (type 5)
    [InlineData(VideoCodec.H264, 0x67, true)]   // SPS (type 7)
    [InlineData(VideoCodec.H264, 0x41, false)]  // non-IDR slice (type 1)
    [InlineData(VideoCodec.H265, 0x26, true)]   // IDR_W_RADL (type 19)
    [InlineData(VideoCodec.H265, 0x40, true)]   // VPS (type 32)
    [InlineData(VideoCodec.H265, 0x42, true)]   // SPS (type 33)
    [InlineData(VideoCodec.H265, 0x02, false)]  // TRAIL_R (type 1)
    [InlineData(VideoCodec.H265, 0x4E, false)]  // SEI suffix (type 39)
    public void Keyframe_detection_reads_each_codecs_nal_header(VideoCodec codec, byte header, bool expected)
    {
        byte[] annexB = [0, 0, 0, 1, header, 0x00, 0x11, 0x22, 0x33];
        MfVideoEncoder.LooksLikeKeyframe(codec, annexB).ShouldBe(expected);
    }

    /// <summary>AV1 carries no start codes, so the scan must decline rather than match a byte by accident.</summary>
    [Fact]
    public void Keyframe_detection_declines_for_av1()
    {
        byte[] notAnnexB = [0, 0, 1, 0x67, 0, 0, 1, 0x65];
        MfVideoEncoder.LooksLikeKeyframe(VideoCodec.Av1, notAnnexB).ShouldBeFalse();
    }

    [Fact]
    public void Colour_conversion_round_trips()
    {
        byte[] bgra = Gradient(0);
        byte[] nv12 = new byte[PixelConversion.Nv12Size(W, H)];
        PixelConversion.BgraToNv12(bgra, W * 4, W, H, nv12);
        byte[] back = new byte[W * H * 4];
        PixelConversion.Nv12ToBgra(nv12, W, W, H, back, W * 4);
        MeanAbsError(bgra, back, W * 4).ShouldBeLessThan(6);
    }

    private static double MeanAbsError(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int bStride)
    {
        long sum = 0;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W * 4; x++)
            {
                if ((x & 3) == 3)
                {
                    continue;
                }

                sum += Math.Abs(a[y * W * 4 + x] - b[y * bStride + x]);
            }
        }

        return sum / (double)(W * H * 3);
    }

}
