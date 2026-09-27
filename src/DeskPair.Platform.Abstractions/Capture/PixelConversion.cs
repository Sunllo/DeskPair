using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace DeskPair.Platform.Abstractions.Capture;

/// <summary>
/// CPU colour conversions (BT.601 limited range) for encoders/decoders that do not take BGRA.
/// The hot paths are vectorised (8 pixels per <see cref="Vector256{T}"/> of ints, AVX2) and fall back to the
/// scalar kernels on hardware without 256-bit vectors; both produce bit-identical output.
/// </summary>
public static class PixelConversion
{
    public static int Nv12Size(int width, int height) => width * height + width * ((height + 1) / 2);

    /// <summary>Tightly packed I420: Y (width*height) followed by U and V planes at half resolution.</summary>
    public static int I420Size(int width, int height) => width * height + 2 * ((width + 1) / 2) * ((height + 1) / 2);

    /// <summary>True when the vector kernels are in use (diagnostics/tests).</summary>
    public static bool IsVectorized => Avx2.IsSupported;

    // ---------------------------------------------------------------- BGRA -> NV12

    /// <summary>BGRA32 (top-down) → NV12 with Y plane followed by interleaved UV; width/height should be even.</summary>
    public static void BgraToNv12(ReadOnlySpan<byte> bgra, int stride, int width, int height, Span<byte> nv12)
    {
        int ySize = width * height;
        Span<byte> y = nv12[..ySize];
        Span<byte> uv = nv12.Slice(ySize, width * ((height + 1) / 2));
        for (int row = 0; row < height; row++)
        {
            ReadOnlySpan<byte> src = bgra.Slice(row * stride, width * 4);
            Span<byte> yRow = y.Slice(row * width, width);
            int col = 0;
            if (Avx2.IsSupported)
            {
                col = LumaRowVector(src, yRow, width);
            }

            LumaRowScalar(src, yRow, col, width);
            if ((row & 1) != 0)
            {
                continue;
            }

            Span<byte> uvRow = uv.Slice((row / 2) * width, width);
            col = 0;
            if (Avx2.IsSupported && row + 1 < height)
            {
                col = ChromaRowVectorNv12(src, bgra.Slice((row + 1) * stride, width * 4), uvRow, width);
            }

            ChromaRowScalarNv12(bgra, stride, row, width, height, col, uvRow);
        }
    }

    /// <summary>BGRA32 (top-down) → tightly packed I420 (Y stride = width, chroma stride = width/2 rounded up).</summary>
    public static void BgraToI420(ReadOnlySpan<byte> bgra, int stride, int width, int height, Span<byte> i420)
    {
        int chromaWidth = (width + 1) / 2;
        int chromaHeight = (height + 1) / 2;
        int ySize = width * height;
        Span<byte> y = i420[..ySize];
        Span<byte> u = i420.Slice(ySize, chromaWidth * chromaHeight);
        Span<byte> v = i420.Slice(ySize + chromaWidth * chromaHeight, chromaWidth * chromaHeight);
        byte[]? rented = null;
        Span<byte> uvRow = width <= 2048 ? stackalloc byte[2048] : (rented = System.Buffers.ArrayPool<byte>.Shared.Rent(width));
        uvRow = uvRow[..width];
        try
        {
            for (int row = 0; row < height; row++)
            {
                ReadOnlySpan<byte> src = bgra.Slice(row * stride, width * 4);
                Span<byte> yRow = y.Slice(row * width, width);
                int col = 0;
                if (Avx2.IsSupported)
                {
                    col = LumaRowVector(src, yRow, width);
                }

                LumaRowScalar(src, yRow, col, width);
                if ((row & 1) != 0)
                {
                    continue;
                }

                // Same chroma maths as NV12 into an interleaved scratch row, then split into the two planes.
                col = 0;
                if (Avx2.IsSupported && row + 1 < height)
                {
                    col = ChromaRowVectorNv12(src, bgra.Slice((row + 1) * stride, width * 4), uvRow, width);
                }

                ChromaRowScalarNv12(bgra, stride, row, width, height, col, uvRow);
                int cRow = (row / 2) * chromaWidth;
                for (int c = 0; c < chromaWidth; c++)
                {
                    u[cRow + c] = uvRow[c * 2];
                    v[cRow + c] = uvRow[c * 2 + 1 < width ? c * 2 + 1 : c * 2];
                }
            }
        }
        finally
        {
            if (rented is not null)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <summary>Y for full 8-pixel blocks; returns the first column not handled.</summary>
    private static int LumaRowVector(ReadOnlySpan<byte> src, Span<byte> yRow, int width)
    {
        ref byte s = ref MemoryMarshal.GetReference(src);
        ref byte d = ref MemoryMarshal.GetReference(yRow);
        int col = 0;
        for (; col + 32 <= width; col += 32)
        {
            Vector256<int> y0 = Luma8(Vector256.LoadUnsafe(ref s, (nuint)(col * 4)).AsInt32());
            Vector256<int> y1 = Luma8(Vector256.LoadUnsafe(ref s, (nuint)(col * 4 + 32)).AsInt32());
            Vector256<int> y2 = Luma8(Vector256.LoadUnsafe(ref s, (nuint)(col * 4 + 64)).AsInt32());
            Vector256<int> y3 = Luma8(Vector256.LoadUnsafe(ref s, (nuint)(col * 4 + 96)).AsInt32());
            Vector256<byte> packed = Vector256.Narrow(Vector256.Narrow(y0, y1).AsUInt16(), Vector256.Narrow(y2, y3).AsUInt16());
            packed.StoreUnsafe(ref d, (nuint)col);
        }

        for (; col + 8 <= width; col += 8)
        {
            Vector256<int> y0 = Luma8(Vector256.LoadUnsafe(ref s, (nuint)(col * 4)).AsInt32());
            Vector128<ushort> narrow = Vector128.Narrow(y0.GetLower().AsUInt32(), y0.GetUpper().AsUInt32());
            Vector128<byte> bytes = Vector128.Narrow(narrow, narrow);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, col), bytes.AsUInt64().ToScalar());
        }

        return col;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Luma8(Vector256<int> px)
    {
        Vector256<int> mask = Vector256.Create(0xFF);
        Vector256<int> round = Vector256.Create(128);
        Vector256<int> bias = Vector256.Create(16);
        Vector256<int> b = px & mask;
        Vector256<int> g = Vector256.ShiftRightLogical(px, 8) & mask;
        Vector256<int> r = Vector256.ShiftRightLogical(px, 16) & mask;
        Vector256<int> y = r * 66 + g * 129 + b * 25 + round;
        return Vector256.ShiftRightArithmetic(y, 8) + bias;
    }

    private static void LumaRowScalar(ReadOnlySpan<byte> src, Span<byte> yRow, int col, int width)
    {
        for (; col < width; col++)
        {
            int b = src[col * 4];
            int g = src[col * 4 + 1];
            int r = src[col * 4 + 2];
            yRow[col] = (byte)(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
        }
    }

    /// <summary>Interleaved UV for full 2x8-pixel blocks (needs the next row); returns the first column not handled.</summary>
    private static int ChromaRowVectorNv12(ReadOnlySpan<byte> src, ReadOnlySpan<byte> next, Span<byte> uvRow, int width)
    {
        ref byte s0 = ref MemoryMarshal.GetReference(src);
        ref byte s1 = ref MemoryMarshal.GetReference(next);
        ref byte d = ref MemoryMarshal.GetReference(uvRow);
        int col = 0;
        for (; col + 32 <= width; col += 32)
        {
            Vector256<int> c0 = Chroma8(ref s0, ref s1, col);
            Vector256<int> c1 = Chroma8(ref s0, ref s1, col + 8);
            Vector256<int> c2 = Chroma8(ref s0, ref s1, col + 16);
            Vector256<int> c3 = Chroma8(ref s0, ref s1, col + 24);
            Vector256<byte> packed = Vector256.Narrow(Vector256.Narrow(c0, c1).AsUInt16(), Vector256.Narrow(c2, c3).AsUInt16());
            packed.StoreUnsafe(ref d, (nuint)col);
        }

        for (; col + 8 <= width; col += 8)
        {
            Vector256<int> c0 = Chroma8(ref s0, ref s1, col);
            Vector128<ushort> narrow = Vector128.Narrow(c0.GetLower().AsUInt32(), c0.GetUpper().AsUInt32());
            Vector128<byte> bytes = Vector128.Narrow(narrow, narrow);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, col), bytes.AsUInt64().ToScalar());
        }

        return col;
    }

    /// <summary>Eight pixels of two rows → eight bytes [U0 V0 U1 V1 U2 V2 U3 V3] for the four 2x2 blocks (as int lanes).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Chroma8(ref byte s0, ref byte s1, int col)
    {
        Vector256<int> mask = Vector256.Create(0xFF);
        Vector256<int> round = Vector256.Create(128);
        Vector256<int> swapPairs = Vector256.Create(1, 0, 3, 2, 5, 4, 7, 6);
        Vector256<int> vToOdd = Vector256.Create(0, 0, 2, 2, 4, 4, 6, 6);
        Vector256<int> evenLanes = Vector256.Create(-1, 0, -1, 0, -1, 0, -1, 0);
        Vector256<int> p0 = Vector256.LoadUnsafe(ref s0, (nuint)(col * 4)).AsInt32();
        Vector256<int> p1 = Vector256.LoadUnsafe(ref s1, (nuint)(col * 4)).AsInt32();
        Vector256<int> b = (p0 & mask) + (p1 & mask);
        Vector256<int> g = (Vector256.ShiftRightLogical(p0, 8) & mask) + (Vector256.ShiftRightLogical(p1, 8) & mask);
        Vector256<int> r = (Vector256.ShiftRightLogical(p0, 16) & mask) + (Vector256.ShiftRightLogical(p1, 16) & mask);
        // Add the horizontal neighbour, then /4 == the scalar kernel's sum / n with n == 4.
        b = Vector256.ShiftRightLogical(b + Avx2.PermuteVar8x32(b, swapPairs), 2);
        g = Vector256.ShiftRightLogical(g + Avx2.PermuteVar8x32(g, swapPairs), 2);
        r = Vector256.ShiftRightLogical(r + Avx2.PermuteVar8x32(r, swapPairs), 2);
        Vector256<int> u = Vector256.ShiftRightArithmetic(b * 112 - r * 38 - g * 74 + round, 8) + round;
        Vector256<int> v = Vector256.ShiftRightArithmetic(r * 112 - g * 94 - b * 18 + round, 8) + round;
        // Even lanes carry U of the block, odd lanes take V of the same block.
        return Vector256.ConditionalSelect(evenLanes, u, Avx2.PermuteVar8x32(v, vToOdd));
    }

    private static void ChromaRowScalarNv12(ReadOnlySpan<byte> bgra, int stride, int row, int width, int height, int col, Span<byte> uvRow)
    {
        ReadOnlySpan<byte> src = bgra.Slice(row * stride, width * 4);
        ReadOnlySpan<byte> next = row + 1 < height ? bgra.Slice((row + 1) * stride, width * 4) : default;
        for (col &= ~1; col < width; col += 2)
        {
            int b2 = src[col * 4], g2 = src[col * 4 + 1], r2 = src[col * 4 + 2], n = 1;
            if (col + 1 < width)
            {
                r2 += src[col * 4 + 6];
                g2 += src[col * 4 + 5];
                b2 += src[col * 4 + 4];
                n++;
            }

            if (!next.IsEmpty)
            {
                r2 += next[col * 4 + 2];
                g2 += next[col * 4 + 1];
                b2 += next[col * 4];
                n++;
                if (col + 1 < width)
                {
                    r2 += next[col * 4 + 6];
                    g2 += next[col * 4 + 5];
                    b2 += next[col * 4 + 4];
                    n++;
                }
            }

            r2 /= n;
            g2 /= n;
            b2 /= n;
            uvRow[col] = (byte)(((-38 * r2 - 74 * g2 + 112 * b2 + 128) >> 8) + 128);
            uvRow[col + 1 < width ? col + 1 : col] = (byte)(((112 * r2 - 94 * g2 - 18 * b2 + 128) >> 8) + 128);
        }
    }

    // ---------------------------------------------------------------- NV12 / I420 -> BGRA

    /// <summary>NV12 → BGRA32 (top-down, alpha 255). The UV plane starts at <c>yStride * height</c>.</summary>
    public static void Nv12ToBgra(ReadOnlySpan<byte> nv12, int yStride, int width, int height, Span<byte> bgra, int bgraStride) =>
        Nv12ToBgra(nv12, yStride, yStride * height, width, height, bgra, bgraStride);

    /// <summary>NV12 → BGRA32 where the UV plane starts at <paramref name="uvOffset"/> (coded height may exceed the visible height).</summary>
    public static void Nv12ToBgra(ReadOnlySpan<byte> nv12, int yStride, int uvOffset, int width, int height, Span<byte> bgra, int bgraStride)
    {
        ReadOnlySpan<byte> uv = nv12[uvOffset..];
        for (int row = 0; row < height; row++)
        {
            ReadOnlySpan<byte> yRow = nv12.Slice(row * yStride, width);
            ReadOnlySpan<byte> uvRow = uv.Slice((row / 2) * yStride, width + (width & 1));
            Span<byte> dst = bgra.Slice(row * bgraStride, width * 4);
            int col = 0;
            if (Avx2.IsSupported)
            {
                col = ToBgraRowVector(yRow, uvRow, default, dst, width, interleaved: true);
            }

            for (; col < width; col++)
            {
                WritePixel(dst, col, yRow[col], uvRow[col & ~1], uvRow[(col & ~1) + 1]);
            }
        }
    }

    /// <summary>I420 planes (each with its own stride) → BGRA32 (top-down, alpha 255).</summary>
    public static void I420ToBgra(ReadOnlySpan<byte> y, int yStride, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, int uvStride, int width, int height, Span<byte> bgra, int bgraStride)
    {
        for (int row = 0; row < height; row++)
        {
            ReadOnlySpan<byte> yRow = y.Slice(row * yStride, width);
            ReadOnlySpan<byte> uRow = u.Slice((row / 2) * uvStride, (width + 1) / 2);
            ReadOnlySpan<byte> vRow = v.Slice((row / 2) * uvStride, (width + 1) / 2);
            Span<byte> dst = bgra.Slice(row * bgraStride, width * 4);
            int col = 0;
            if (Avx2.IsSupported)
            {
                col = ToBgraRowVector(yRow, uRow, vRow, dst, width, interleaved: false);
            }

            for (; col < width; col++)
            {
                WritePixel(dst, col, yRow[col], uRow[col / 2], vRow[col / 2]);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WritePixel(Span<byte> dst, int col, int yv, int uv, int vv)
    {
        int c = yv - 16;
        int d = uv - 128;
        int e = vv - 128;
        int r = (298 * c + 409 * e + 128) >> 8;
        int g = (298 * c - 100 * d - 208 * e + 128) >> 8;
        int b = (298 * c + 516 * d + 128) >> 8;
        dst[col * 4] = (byte)Math.Clamp(b, 0, 255);
        dst[col * 4 + 1] = (byte)Math.Clamp(g, 0, 255);
        dst[col * 4 + 2] = (byte)Math.Clamp(r, 0, 255);
        dst[col * 4 + 3] = 255;
    }

    /// <summary>Converts full 16-pixel blocks of one row; <paramref name="interleaved"/> selects NV12 (u = UV row) or I420 (u, v rows).</summary>
    private static unsafe int ToBgraRowVector(ReadOnlySpan<byte> yRow, ReadOnlySpan<byte> u, ReadOnlySpan<byte> v, Span<byte> dst, int width, bool interleaved)
    {
        var dupEven = Vector256.Create(0, 0, 2, 2, 4, 4, 6, 6);
        var dupOdd = Vector256.Create(1, 1, 3, 3, 5, 5, 7, 7);
        var dupLo = Vector256.Create(0, 0, 1, 1, 2, 2, 3, 3);
        var dupHi = Vector256.Create(4, 4, 5, 5, 6, 6, 7, 7);
        int col = 0;
        fixed (byte* ys = yRow)
        fixed (byte* us = u)
        fixed (byte* vs = v)
        fixed (byte* d = dst)
        {
            for (; col + 16 <= width; col += 16)
            {
                // vpmovzxbd: 8 luma bytes -> 8 ints, twice.
                Vector256<int> yLo = Avx2.ConvertToVector256Int32(ys + col);
                Vector256<int> yHi = Avx2.ConvertToVector256Int32(ys + col + 8);
                Vector256<int> dLo, eLo, dHi, eHi;
                if (interleaved)
                {
                    // 8 UV bytes = 4 pairs serve 8 pixels.
                    Vector256<int> uvLo = Avx2.ConvertToVector256Int32(us + col);
                    Vector256<int> uvHi = Avx2.ConvertToVector256Int32(us + col + 8);
                    dLo = Avx2.PermuteVar8x32(uvLo, dupEven);
                    eLo = Avx2.PermuteVar8x32(uvLo, dupOdd);
                    dHi = Avx2.PermuteVar8x32(uvHi, dupEven);
                    eHi = Avx2.PermuteVar8x32(uvHi, dupOdd);
                }
                else
                {
                    // 8 U and 8 V bytes serve 16 pixels.
                    Vector256<int> u8 = Avx2.ConvertToVector256Int32(us + col / 2);
                    Vector256<int> v8 = Avx2.ConvertToVector256Int32(vs + col / 2);
                    dLo = Avx2.PermuteVar8x32(u8, dupLo);
                    dHi = Avx2.PermuteVar8x32(u8, dupHi);
                    eLo = Avx2.PermuteVar8x32(v8, dupLo);
                    eHi = Avx2.PermuteVar8x32(v8, dupHi);
                }

                Avx.Store(d + col * 4, Pixels8(yLo, dLo, eLo).AsByte());
                Avx.Store(d + col * 4 + 32, Pixels8(yHi, dHi, eHi).AsByte());
            }
        }

        return col;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Pixels8(Vector256<int> y, Vector256<int> u, Vector256<int> v)
    {
        Vector256<int> c = (y - Vector256.Create(16)) * 298 + Vector256.Create(128);
        Vector256<int> dd = u - Vector256.Create(128);
        Vector256<int> ee = v - Vector256.Create(128);
        Vector256<int> r = Vector256.ShiftRightArithmetic(c + ee * 409, 8);
        Vector256<int> g = Vector256.ShiftRightArithmetic(c - dd * 100 - ee * 208, 8);
        Vector256<int> b = Vector256.ShiftRightArithmetic(c + dd * 516, 8);
        Vector256<int> zero = Vector256<int>.Zero;
        Vector256<int> max = Vector256.Create(255);
        r = Vector256.Min(Vector256.Max(r, zero), max);
        g = Vector256.Min(Vector256.Max(g, zero), max);
        b = Vector256.Min(Vector256.Max(b, zero), max);
        return b | Vector256.ShiftLeft(g, 8) | Vector256.ShiftLeft(r, 16) | Vector256.Create(unchecked((int)0xFF000000));
    }

    // ---------------------------------------------------------------- reference kernels (tests)

    /// <summary>Scalar reference implementation used by tests to check the vector kernels.</summary>
    public static void BgraToNv12Reference(ReadOnlySpan<byte> bgra, int stride, int width, int height, Span<byte> nv12)
    {
        int ySize = width * height;
        Span<byte> y = nv12[..ySize];
        Span<byte> uv = nv12.Slice(ySize, width * ((height + 1) / 2));
        for (int row = 0; row < height; row++)
        {
            LumaRowScalar(bgra.Slice(row * stride, width * 4), y.Slice(row * width, width), 0, width);
            if ((row & 1) == 0)
            {
                ChromaRowScalarNv12(bgra, stride, row, width, height, 0, uv.Slice((row / 2) * width, width));
            }
        }
    }

    /// <summary>Scalar reference implementation used by tests to check the vector kernels.</summary>
    public static void Nv12ToBgraReference(ReadOnlySpan<byte> nv12, int yStride, int uvOffset, int width, int height, Span<byte> bgra, int bgraStride)
    {
        ReadOnlySpan<byte> uv = nv12[uvOffset..];
        for (int row = 0; row < height; row++)
        {
            ReadOnlySpan<byte> yRow = nv12.Slice(row * yStride, width);
            ReadOnlySpan<byte> uvRow = uv.Slice((row / 2) * yStride, width + (width & 1));
            Span<byte> dst = bgra.Slice(row * bgraStride, width * 4);
            for (int col = 0; col < width; col++)
            {
                WritePixel(dst, col, yRow[col], uvRow[col & ~1], uvRow[(col & ~1) + 1]);
            }
        }
    }
}
