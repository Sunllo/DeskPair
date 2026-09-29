using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Platform.Windows.Codec;

namespace DeskPair.Platform.Windows.Tests;

/// <summary>
/// A desktop-sized stream decoded for a long stretch: the controller crashed roughly 600 frames into a
/// 2560x1440 NVENC stream, so this keeps the decoder busy well past that point.
/// </summary>
public class MfDecoderStressTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task Decodes_900_desktop_sized_frames_without_dying()
    {
        if (!MediaFoundationEncoding.CanEncode(VideoCodec.H264))
        {
            return; // a machine whose Media Foundation lists an H.264 encoder that will not encode
        }

        const int W = 2560, H = 1440;
        var encoders = new MfVideoEncoderFactory(NullLoggerFactory.Instance);
        var decoders = new MfVideoDecoderFactory(NullLoggerFactory.Instance);
        await using IVideoEncoder encoder = encoders.Create(new VideoEncoderConfig(VideoCodec.H264, W, H, 30, 3000, true, PixelFormat.Nv12, GpuApi.None, 0));
        await using IVideoDecoder decoder = decoders.Create(VideoCodec.H264, GpuApi.None, 0);

        byte[] bgra = new byte[W * H * 4];
        byte[] nv12 = new byte[PixelConversion.Nv12Size(W, H)];
        int decoded = 0, encoded = 0;
        for (int i = 0; i < 900; i++)
        {
            // Moving diagonal bands: cheap to generate, enough motion to keep the encoder emitting P-frames.
            int shift = i * 7;
            for (int y = 0; y < H; y += 8)
            {
                byte v = (byte)(((y + shift) / 64 % 2 == 0) ? 40 : 200);
                bgra.AsSpan(y * W * 4, W * 4 * 8).Fill(v);
            }

            PixelConversion.BgraToNv12(bgra, W * 4, W, H, nv12);
            if (!encoder.TryEncode(nv12, W, i * 33, out EncodedPacket packet))
            {
                continue;
            }

            encoded++;
            if (decoder.TryDecode(packet.Data.Span, packet.IsKeyFrame, out DecodedFrame frame))
            {
                decoded++;
                frame.Width.ShouldBe(W);
                frame.Height.ShouldBe(H);
            }
        }

        output.WriteLine($"encoded {encoded}, decoded {decoded}");
        encoded.ShouldBeGreaterThan(600);
        decoded.ShouldBeGreaterThan(encoded * 3 / 4);
    }
}
