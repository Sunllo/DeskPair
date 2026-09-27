using System.Buffers.Binary;
using System.IO.Compression;

namespace DeskPair.Platform.Abstractions.Imaging;

/// <summary>A decoded raster in 32-bit BGRA with a tightly packed stride.</summary>
public sealed record BgraImage(int Width, int Height, byte[] Pixels)
{
    public int Stride => Width * 4;
}

/// <summary>
/// Minimal PNG codec for clipboard images: writes 8-bit RGBA, reads non-interlaced 8/16-bit images of
/// every standard colour type. Enough for the images the platforms exchange without an imaging library.
/// </summary>
public static class PngCodec
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static bool IsPng(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..8].SequenceEqual(Signature);

    /// <summary>Encodes a BGRA raster (any stride) as an RGBA PNG.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> bgra, int stride, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (bgra.Length < stride * (height - 1) + width * 4)
        {
            throw new ArgumentException("Pixel buffer is smaller than the described image.", nameof(bgra));
        }

        byte[] raw = new byte[(width * 4 + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int o = y * (width * 4 + 1);
            raw[o++] = 0; // filter: none
            ReadOnlySpan<byte> row = bgra.Slice(y * stride, width * 4);
            for (int x = 0; x < width; x++)
            {
                raw[o + x * 4] = row[x * 4 + 2];
                raw[o + x * 4 + 1] = row[x * 4 + 1];
                raw[o + x * 4 + 2] = row[x * 4];
                raw[o + x * 4 + 3] = row[x * 4 + 3];
            }
        }

        using var idat = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(raw);
        }

        using var output = new MemoryStream(idat.Position.ToInt32() + 64);
        output.Write(Signature);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "IDAT", idat.GetBuffer().AsSpan(0, (int)idat.Position));
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    /// <summary>Decodes a PNG into BGRA; throws <see cref="NotSupportedException"/> for interlaced images.</summary>
    public static BgraImage Decode(ReadOnlySpan<byte> png)
    {
        if (!IsPng(png))
        {
            throw new InvalidDataException("Not a PNG stream.");
        }

        int width = 0, height = 0, bitDepth = 0, colorType = 0;
        byte[]? palette = null;
        byte[]? paletteAlpha = null;
        using var compressed = new MemoryStream();
        int pos = 8;
        while (pos + 8 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png[pos..]);
            string type = System.Text.Encoding.ASCII.GetString(png.Slice(pos + 4, 4));
            if (length < 0 || pos + 12 + length > png.Length)
            {
                throw new InvalidDataException("Truncated PNG chunk.");
            }

            ReadOnlySpan<byte> body = png.Slice(pos + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(body);
                    height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    bitDepth = body[8];
                    colorType = body[9];
                    if (body[12] != 0)
                    {
                        throw new NotSupportedException("Interlaced PNG is not supported.");
                    }

                    break;
                case "PLTE":
                    palette = body.ToArray();
                    break;
                case "tRNS":
                    paletteAlpha = body.ToArray();
                    break;
                case "IDAT":
                    compressed.Write(body);
                    break;
                case "IEND":
                    pos = png.Length;
                    continue;
            }

            pos += 12 + length;
        }

        if (width <= 0 || height <= 0 || compressed.Length == 0)
        {
            throw new InvalidDataException("PNG has no image data.");
        }

        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new NotSupportedException($"PNG colour type {colorType}.") };
        if (bitDepth != 8 && !(bitDepth == 16 && colorType != 3) && !(colorType == 3 && bitDepth is 1 or 2 or 4))
        {
            throw new NotSupportedException($"PNG bit depth {bitDepth} with colour type {colorType}.");
        }

        int bitsPerPixel = channels * bitDepth;
        int bytesPerPixel = Math.Max(1, bitsPerPixel / 8);
        int rowBytes = (width * bitsPerPixel + 7) / 8;
        byte[] raw = new byte[(rowBytes + 1) * height];
        compressed.Position = 0;
        using (var z = new ZLibStream(compressed, CompressionMode.Decompress))
        {
            z.ReadExactly(raw);
        }

        Unfilter(raw, rowBytes, height, bytesPerPixel);

        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = raw.AsSpan(y * (rowBytes + 1) + 1, rowBytes);
            Span<byte> outRow = pixels.AsSpan(y * width * 4, width * 4);
            for (int x = 0; x < width; x++)
            {
                byte r, g, b, a = 255;
                switch (colorType)
                {
                    case 0:
                        r = g = b = Sample(row, x, bitDepth);
                        break;
                    case 2:
                        r = Sample(row, x * 3, bitDepth);
                        g = Sample(row, x * 3 + 1, bitDepth);
                        b = Sample(row, x * 3 + 2, bitDepth);
                        break;
                    case 3:
                        int index = PaletteIndex(row, x, bitDepth);
                        if (palette is null || index * 3 + 2 >= palette.Length)
                        {
                            throw new InvalidDataException("PNG palette entry out of range.");
                        }

                        r = palette[index * 3];
                        g = palette[index * 3 + 1];
                        b = palette[index * 3 + 2];
                        if (paletteAlpha is not null && index < paletteAlpha.Length)
                        {
                            a = paletteAlpha[index];
                        }

                        break;
                    case 4:
                        r = g = b = Sample(row, x * 2, bitDepth);
                        a = Sample(row, x * 2 + 1, bitDepth);
                        break;
                    default:
                        r = Sample(row, x * 4, bitDepth);
                        g = Sample(row, x * 4 + 1, bitDepth);
                        b = Sample(row, x * 4 + 2, bitDepth);
                        a = Sample(row, x * 4 + 3, bitDepth);
                        break;
                }

                outRow[x * 4] = b;
                outRow[x * 4 + 1] = g;
                outRow[x * 4 + 2] = r;
                outRow[x * 4 + 3] = a;
            }
        }

        return new BgraImage(width, height, pixels);
    }

    private static byte Sample(ReadOnlySpan<byte> row, int sampleIndex, int bitDepth) => bitDepth == 16 ? row[sampleIndex * 2] : row[sampleIndex];

    private static int PaletteIndex(ReadOnlySpan<byte> row, int x, int bitDepth)
    {
        if (bitDepth == 8)
        {
            return row[x];
        }

        int bit = x * bitDepth;
        int shift = 8 - bitDepth - (bit & 7);
        return (row[bit >> 3] >> shift) & ((1 << bitDepth) - 1);
    }

    private static void Unfilter(byte[] raw, int rowBytes, int height, int bpp)
    {
        int lineLength = rowBytes + 1;
        for (int y = 0; y < height; y++)
        {
            int o = y * lineLength;
            byte filter = raw[o];
            Span<byte> row = raw.AsSpan(o + 1, rowBytes);
            ReadOnlySpan<byte> prior = y > 0 ? raw.AsSpan(o - lineLength + 1, rowBytes) : default;
            for (int i = 0; i < rowBytes; i++)
            {
                int a = i >= bpp ? row[i - bpp] : 0;
                int b = prior.Length > 0 ? prior[i] : 0;
                int c = i >= bpp && prior.Length > 0 ? prior[i - bpp] : 0;
                int predictor = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException($"PNG filter {filter}."),
                };
                row[i] = (byte)(row[i] + predictor);
            }
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> body)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        System.Text.Encoding.ASCII.GetBytes(type, header[4..]);
        s.Write(header);
        s.Write(body);
        uint crc = Crc(header[4..]);
        crc = Crc(body, crc ^ 0xFFFFFFFF) ;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        s.Write(crcBytes);
    }

    private static uint Crc(ReadOnlySpan<byte> data, uint seed = 0xFFFFFFFF)
    {
        uint crc = seed;
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}

file static class Int64Extensions
{
    public static int ToInt32(this long value) => checked((int)value);
}
