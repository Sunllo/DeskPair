using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Codec.OpenH264;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Platform.Windows.Codec;

namespace DeskPair.Platform.Windows.Tests;

/// <summary>Streams produced by one implementation must decode on the other (Windows host ↔ Linux/macOS controller and back).</summary>
public class CodecInteropTests
{
    private const int W = 320;
    private const int H = 240;

    /// <summary>
    /// The only check that our Media Foundation stream is readable by something that is not Microsoft's own
    /// decoder -- the bug class a non-Windows viewer would hit. No OpenH264 binary ships here, so these run
    /// for whoever supplies one (SUNLLO_OPENH264_PATH) rather than being deleted.
    /// </summary>
    [Fact]
    public async Task Media_foundation_stream_decodes_with_openh264()
    {
        if (!OpenH264EncoderFactory.IsAvailable)
        {
            return; // no library supplied; see native/openh264/README.md
        }

        await RoundTripAsync(new MfVideoEncoderFactory(NullLoggerFactory.Instance), new OpenH264DecoderFactory(NullLoggerFactory.Instance));
    }

    [Fact]
    public async Task Openh264_stream_decodes_with_media_foundation()
    {
        if (!OpenH264EncoderFactory.IsAvailable)
        {
            return; // no library supplied; see native/openh264/README.md
        }

        await RoundTripAsync(new OpenH264EncoderFactory(NullLoggerFactory.Instance), new MfVideoDecoderFactory(NullLoggerFactory.Instance));
    }

    private static async Task RoundTripAsync(IVideoEncoderFactory encoders, IVideoDecoderFactory decoders)
    {
        await using IVideoEncoder encoder = encoders.Create(new VideoEncoderConfig(VideoCodec.H264, W, H, 30, 1500, false, PixelFormat.Bgra32, GpuApi.None, 0));
        await using IVideoDecoder decoder = decoders.Create(VideoCodec.H264, GpuApi.None, 0);
        byte[] planar = new byte[Math.Max(PixelConversion.Nv12Size(W, H), PixelConversion.I420Size(W, H))];
        int decoded = 0;
        double worst = 0;
        for (int i = 0; i < 20; i++)
        {
            byte[] bgra = Gradient(i);
            if (encoder.RequiredInputFormat == PixelFormat.Nv12)
            {
                PixelConversion.BgraToNv12(bgra, W * 4, W, H, planar);
            }
            else
            {
                PixelConversion.BgraToI420(bgra, W * 4, W, H, planar);
            }

            if (!encoder.TryEncode(planar, W, i, out EncodedPacket packet))
            {
                continue;
            }

            if (decoder.TryDecode(packet.Data.Span, packet.IsKeyFrame, out DecodedFrame frame))
            {
                decoded++;
                frame.Width.ShouldBe(W);
                frame.Height.ShouldBe(H);
                worst = Math.Max(worst, MeanAbsError(bgra, frame.Cpu.Span, frame.Stride));
            }
        }

        decoded.ShouldBeGreaterThanOrEqualTo(15);
        worst.ShouldBeLessThan(12);
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
