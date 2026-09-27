using System.IO.Compression;
using DeskPair.Platform.Abstractions.Imaging;

namespace DeskPair.Core.Tests;

public class PngCodecTests
{
    [Fact]
    public void Encode_then_decode_is_lossless_including_alpha()
    {
        const int W = 37, H = 11;
        byte[] bgra = new byte[W * H * 4];
        var rng = new Random(7);
        rng.NextBytes(bgra);
        byte[] png = PngCodec.Encode(bgra, W * 4, W, H);
        PngCodec.IsPng(png).ShouldBeTrue();
        BgraImage image = PngCodec.Decode(png);
        image.Width.ShouldBe(W);
        image.Height.ShouldBe(H);
        image.Pixels.ShouldBe(bgra);
    }

    [Fact]
    public void Encode_honours_a_padded_stride()
    {
        const int W = 5, H = 3, Stride = 32;
        byte[] bgra = new byte[Stride * H];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W * 4; x++)
            {
                bgra[y * Stride + x] = (byte)(y * 50 + x);
            }
        }

        BgraImage image = PngCodec.Decode(PngCodec.Encode(bgra, Stride, W, H));
        for (int y = 0; y < H; y++)
        {
            image.Pixels.AsSpan(y * W * 4, W * 4).ToArray().ShouldBe(bgra.AsSpan(y * Stride, W * 4).ToArray());
        }
    }

    [Theory]
    [InlineData(0, 1)] // grayscale
    [InlineData(2, 3)] // RGB
    [InlineData(4, 2)] // gray + alpha
    public void Decodes_other_colour_types_with_every_filter(int colorType, int channels)
    {
        const int W = 6, H = 5;
        byte[] raw = new byte[(W * channels + 1) * H];
        var rng = new Random(colorType);
        for (int y = 0; y < H; y++)
        {
            raw[y * (W * channels + 1)] = (byte)(y % 5); // exercise every filter type on encode side
            for (int i = 1; i <= W * channels; i++)
            {
                raw[y * (W * channels + 1) + i] = (byte)rng.Next(256);
            }
        }

        // Filtered bytes as stored; compute the expected reconstruction independently.
        byte[] expected = Reconstruct(raw, W * channels, H, channels);
        byte[] png = Build(W, H, colorType, raw);
        BgraImage image = PngCodec.Decode(png);
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int s = y * W * channels + x * channels;
                int d = (y * W + x) * 4;
                (byte r, byte g, byte b, byte a) = colorType switch
                {
                    0 => (expected[s], expected[s], expected[s], (byte)255),
                    2 => (expected[s], expected[s + 1], expected[s + 2], (byte)255),
                    _ => (expected[s], expected[s], expected[s], expected[s + 1]),
                };
                image.Pixels[d].ShouldBe(b);
                image.Pixels[d + 1].ShouldBe(g);
                image.Pixels[d + 2].ShouldBe(r);
                image.Pixels[d + 3].ShouldBe(a);
            }
        }
    }

    private static byte[] Reconstruct(byte[] raw, int rowBytes, int height, int bpp)
    {
        byte[] output = new byte[rowBytes * height];
        for (int y = 0; y < height; y++)
        {
            byte filter = raw[y * (rowBytes + 1)];
            for (int i = 0; i < rowBytes; i++)
            {
                int value = raw[y * (rowBytes + 1) + 1 + i];
                int a = i >= bpp ? output[y * rowBytes + i - bpp] : 0;
                int b = y > 0 ? output[(y - 1) * rowBytes + i] : 0;
                int c = y > 0 && i >= bpp ? output[(y - 1) * rowBytes + i - bpp] : 0;
                int p = a + b - c;
                int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                int paeth = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                int predictor = filter switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) >> 1, _ => paeth };
                output[y * rowBytes + i] = (byte)(value + predictor);
            }
        }

        return output;
    }

    private static byte[] Build(int width, int height, int colorType, byte[] filteredRows)
    {
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(filteredRows);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", [.. BigEndian(width), .. BigEndian(height), 8, (byte)colorType, 0, 0, 0]);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static byte[] BigEndian(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static void WriteChunk(Stream s, string type, byte[] body)
    {
        s.Write(BigEndian(body.Length));
        s.Write(System.Text.Encoding.ASCII.GetBytes(type));
        s.Write(body);
        s.Write(new byte[4]); // CRC is not verified by the decoder
    }
}
