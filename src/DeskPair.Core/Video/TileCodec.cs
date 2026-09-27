using System.Buffers;
using System.IO.Compression;

namespace DeskPair.Core.Video;

/// <summary>
/// Lossless codec for screen tiles. The BGRA tile is split into B, G and R planes (alpha dropped),
/// each row is left-delta filtered (PNG "Sub"), and the result is Brotli-compressed. Desktop text and
/// UI compress 10-30x; flat colour a few dozen bytes. Pixels round-trip exactly.
/// </summary>
public static class TileCodec
{
    public const int DefaultTileSize = 64;

    /// <summary>Brotli quality: 0-11; 2-4 is the sweet spot for real-time screen content.</summary>
    public static int Quality { get; set; } = 3;

    private const int Window = 16;

    public static int MaxEncodedLength(int width, int height) => BrotliEncoder.GetMaxCompressedLength(width * height * 3) + 4;

    /// <summary>Encodes a <paramref name="width"/>×<paramref name="height"/> BGRA tile read from <paramref name="bgra"/> with <paramref name="stride"/> bytes per row.</summary>
    public static int Encode(ReadOnlySpan<byte> bgra, int stride, int width, int height, Span<byte> destination)
    {
        int planeSize = width * height;
        byte[] rented = ArrayPool<byte>.Shared.Rent(planeSize * 3);
        try
        {
            Span<byte> planar = rented.AsSpan(0, planeSize * 3);
            Planarize(bgra, stride, width, height, planar);
            using var encoder = new BrotliEncoder(Quality, Window);
            OperationStatus status = encoder.Compress(planar, destination, out _, out int written, isFinalBlock: true);
            if (status != OperationStatus.Done)
            {
                throw new InvalidOperationException($"Tile compression failed: {status}.");
            }

            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Decodes into <paramref name="bgra"/> (alpha set to 255).</summary>
    public static void Decode(ReadOnlySpan<byte> data, int width, int height, Span<byte> bgra, int stride)
    {
        int planeSize = width * height;
        byte[] rented = ArrayPool<byte>.Shared.Rent(planeSize * 3);
        try
        {
            Span<byte> planar = rented.AsSpan(0, planeSize * 3);
            using var decoder = new BrotliDecoder();
            OperationStatus status = decoder.Decompress(data, planar, out int consumed, out int written);
            if (status != OperationStatus.Done || written != planar.Length)
            {
                throw new InvalidDataException($"Tile decompression failed ({status}, {written}/{planar.Length} bytes).");
            }

            Deplanarize(planar, width, height, bgra, stride);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void Planarize(ReadOnlySpan<byte> bgra, int stride, int width, int height, Span<byte> planar)
    {
        int planeSize = width * height;
        Span<byte> b = planar[..planeSize];
        Span<byte> g = planar.Slice(planeSize, planeSize);
        Span<byte> r = planar.Slice(planeSize * 2, planeSize);
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = bgra.Slice(y * stride, width * 4);
            int o = y * width;
            byte pb = 0, pg = 0, pr = 0;
            for (int x = 0; x < width; x++)
            {
                byte cb = row[x * 4], cg = row[x * 4 + 1], cr = row[x * 4 + 2];
                b[o + x] = (byte)(cb - pb);
                g[o + x] = (byte)(cg - pg);
                r[o + x] = (byte)(cr - pr);
                pb = cb;
                pg = cg;
                pr = cr;
            }
        }
    }

    private static void Deplanarize(ReadOnlySpan<byte> planar, int width, int height, Span<byte> bgra, int stride)
    {
        int planeSize = width * height;
        ReadOnlySpan<byte> b = planar[..planeSize];
        ReadOnlySpan<byte> g = planar.Slice(planeSize, planeSize);
        ReadOnlySpan<byte> r = planar.Slice(planeSize * 2, planeSize);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = bgra.Slice(y * stride, width * 4);
            int o = y * width;
            byte pb = 0, pg = 0, pr = 0;
            for (int x = 0; x < width; x++)
            {
                pb = (byte)(pb + b[o + x]);
                pg = (byte)(pg + g[o + x]);
                pr = (byte)(pr + r[o + x]);
                row[x * 4] = pb;
                row[x * 4 + 1] = pg;
                row[x * 4 + 2] = pr;
                row[x * 4 + 3] = 255;
            }
        }
    }
}
