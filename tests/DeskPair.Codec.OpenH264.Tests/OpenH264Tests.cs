using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Codec.OpenH264.Native;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Codec;

namespace DeskPair.Codec.OpenH264.Tests;

public class OpenH264Tests
{
    private const int W = 320;
    private const int H = 240;

    /// <summary>Sizes and offsets printed by an MSVC x64 probe against codec_api.h (OpenH264 2.6).</summary>
    [Fact]
    public unsafe void Struct_layouts_match_the_c_headers()
    {
        sizeof(SEncParamExt).ShouldBe(924);
        sizeof(SSpatialLayerConfig).ShouldBe(200);
        sizeof(SSliceArgument).ShouldBe(152);
        sizeof(SFrameBSInfo).ShouldBe(7192);
        sizeof(SLayerBSInfo).ShouldBe(56);
        sizeof(SSourcePicture).ShouldBe(80);
        sizeof(SDecodingParam).ShouldBe(32);
        sizeof(SBufferInfo).ShouldBe(72);
        sizeof(SSysMEMBuffer).ShouldBe(20);
        sizeof(SBitrateInfo).ShouldBe(8);
        ((int)System.Runtime.InteropServices.Marshal.OffsetOf<SEncParamExt>(nameof(SEncParamExt.iComplexityMode))).ShouldBe(832);
        ((int)System.Runtime.InteropServices.Marshal.OffsetOf<SEncParamExt>(nameof(SEncParamExt.iMultipleThreadIdc))).ShouldBe(892);
        ((int)System.Runtime.InteropServices.Marshal.OffsetOf<SEncParamExt>(nameof(SEncParamExt.iIdrBitrateRatio))).ShouldBe(916);
        ((int)System.Runtime.InteropServices.Marshal.OffsetOf<SFrameBSInfo>(nameof(SFrameBSInfo.eFrameType))).ShouldBe(7176);
        ((int)System.Runtime.InteropServices.Marshal.OffsetOf<SLayerBSInfo>(nameof(SLayerBSInfo.pBsBuf))).ShouldBe(32);
        ((int)System.Runtime.InteropServices.Marshal.OffsetOf<SSourcePicture>(nameof(SSourcePicture.pData0))).ShouldBe(24);
        ((int)System.Runtime.InteropServices.Marshal.OffsetOf<SBufferInfo>(nameof(SBufferInfo.pDst0))).ShouldBe(48);
    }

    /// <summary>
    /// No OpenH264 binary ships with this repository (see native/openh264/README.md), so the tests that need
    /// one run only where somebody supplied it -- through SUNLLO_OPENH264_PATH or beside the test binary.
    /// </summary>
    [Fact]
    public void Library_loads_and_reports_its_version()
    {
        if (!OpenH264EncoderFactory.IsAvailable)
        {
            return; // no library supplied; see native/openh264/README.md
        }

        var version = new OpenH264Version();
        unsafe
        {
            Native.OpenH264.WelsGetCodecVersionEx(&version);
        }

        version.Major.ShouldBe(2u);
    }

    [Fact]
    public async Task Encode_then_decode_reproduces_the_picture_within_tolerance()
    {
        if (!OpenH264EncoderFactory.IsAvailable)
        {
            return; // no library supplied; see native/openh264/README.md
        }

        var encoders = new OpenH264EncoderFactory(NullLoggerFactory.Instance);
        var decoders = new OpenH264DecoderFactory(NullLoggerFactory.Instance);
        encoders.Probe().ShouldBe(SupportedCodecs.H264Software);
        await using IVideoEncoder encoder = encoders.Create(new VideoEncoderConfig(VideoCodec.H264, W, H, 30, 1500, false, PixelFormat.I420, GpuApi.None, 0));
        await using IVideoDecoder decoder = decoders.Create(VideoCodec.H264, GpuApi.None, 0);
        encoder.RequiredInputFormat.ShouldBe(PixelFormat.I420);

        byte[] i420 = new byte[PixelConversion.I420Size(W, H)];
        int packets = 0, decoded = 0, keyframes = 0;
        double worstError = 0;
        for (int i = 0; i < 30; i++)
        {
            byte[] bgra = Gradient(i);
            PixelConversion.BgraToI420(bgra, W * 4, W, H, i420);
            if (i == 15)
            {
                encoder.RequestKeyFrame();
            }

            if (!encoder.TryEncode(i420, W, i, out EncodedPacket packet))
            {
                continue;
            }

            packets++;
            if (packet.IsKeyFrame)
            {
                keyframes++;
            }

            if (i == 0)
            {
                packet.IsKeyFrame.ShouldBeTrue();
                LooksLikeSps(packet.Data.Span).ShouldBeTrue();
            }

            if (decoder.TryDecode(packet.Data.Span, packet.IsKeyFrame, out DecodedFrame frame))
            {
                decoded++;
                frame.Width.ShouldBe(W);
                frame.Height.ShouldBe(H);
                worstError = Math.Max(worstError, MeanAbsError(bgra, frame.Cpu.Span, frame.Stride));
            }
        }

        packets.ShouldBeGreaterThanOrEqualTo(28);
        decoded.ShouldBeGreaterThanOrEqualTo(25);
        keyframes.ShouldBeGreaterThanOrEqualTo(2);
        worstError.ShouldBeLessThan(12, "lossy but the picture must survive");

        encoder.SetBitrate(600);
        ((OpenH264Encoder)encoder).Bitrate.ShouldBe(600);
    }

    /// <summary>
    /// A rise past twice the starting rate is taken, and so is the way back down. OpenH264 checks a target against the
    /// layer's own ceiling, which was set once at the start, so every such rise used to be refused and the encoder
    /// stayed at the rate it began with.
    /// </summary>
    [Fact]
    public async Task The_bitrate_rises_past_twice_the_start_and_comes_back_down()
    {
        if (!OpenH264EncoderFactory.IsAvailable)
        {
            return; // no library supplied; see native/openh264/README.md
        }

        var encoders = new OpenH264EncoderFactory(NullLoggerFactory.Instance);
        await using IVideoEncoder encoder = encoders.Create(new VideoEncoderConfig(VideoCodec.H264, W, H, 30, 1500, false, PixelFormat.I420, GpuApi.None, 0));
        var openH264 = (OpenH264Encoder)encoder;

        foreach (int kbps in (int[])[10_000, 600, 25_000, 3_000])
        {
            encoder.SetBitrate(kbps);
            openH264.BitrateAccepted.ShouldBeTrue($"{kbps} kbps");
            openH264.Bitrate.ShouldBe(kbps);
        }
    }

    [Fact]
    public void I420_conversion_round_trips()
    {
        byte[] bgra = Gradient(3);
        byte[] i420 = new byte[PixelConversion.I420Size(W, H)];
        PixelConversion.BgraToI420(bgra, W * 4, W, H, i420);
        byte[] back = new byte[W * H * 4];
        int cw = W / 2;
        PixelConversion.I420ToBgra(i420.AsSpan(0, W * H), W, i420.AsSpan(W * H, cw * (H / 2)), i420.AsSpan(W * H + cw * (H / 2), cw * (H / 2)), cw, W, H, back, W * 4);
        MeanAbsError(bgra, back, W * 4).ShouldBeLessThan(6);
    }

    private static bool LooksLikeSps(ReadOnlySpan<byte> annexB)
    {
        for (int i = 0; i + 3 < annexB.Length; i++)
        {
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1 && (annexB[i + 3] & 0x1F) == 7)
            {
                return true;
            }
        }

        return false;
    }

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
