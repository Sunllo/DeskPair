using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace DeskPair.Core.Video;

/// <summary>
/// Cheap content hash for change detection: eight parallel multiply-rotate lanes over 32-byte
/// chunks (AVX2), folded at the end. Not a cryptographic or even a general-purpose hash; it only has to
/// change when a tile changes, and it has to read 15 MB per frame in a millisecond or two.
/// </summary>
public static class TileHash
{
    private const uint Prime = 0x9E3779B1u;
    private const uint Prime2 = 0x85EBCA77u;

    /// <summary>Hashes a rectangle of BGRA pixels (<paramref name="width"/> pixels per row, <paramref name="height"/> rows) at <paramref name="stride"/> bytes per row.</summary>
    public static ulong Compute(ReadOnlySpan<byte> bgra, int stride, int width, int height)
    {
        int rowBytes = width * 4;
        if (Avx2.IsSupported && rowBytes % 32 == 0)
        {
            return ComputeVector(bgra, stride, rowBytes, height);
        }

        return ComputeScalar(bgra, stride, rowBytes, height);
    }

    private static ulong ComputeVector(ReadOnlySpan<byte> bgra, int stride, int rowBytes, int height)
    {
        ref byte s = ref MemoryMarshal.GetReference(bgra);
        Vector256<uint> acc = Vector256.Create(0x243F6A88u, 0x85A308D3u, 0x13198A2Eu, 0x03707344u, 0xA4093822u, 0x299F31D0u, 0x082EFA98u, 0xEC4E6C89u);
        Vector256<uint> prime = Vector256.Create(Prime);
        for (int y = 0; y < height; y++)
        {
            nuint row = (nuint)(y * stride);
            for (int x = 0; x < rowBytes; x += 32)
            {
                Vector256<uint> v = Vector256.LoadUnsafe(ref s, row + (nuint)x).AsUInt32();
                acc = (acc ^ v) * prime;
                acc = Avx2.Or(Avx2.ShiftLeftLogical(acc, 13), Avx2.ShiftRightLogical(acc, 19));
            }

            acc += Vector256.Create((uint)y + 1);
        }

        return Fold(acc);
    }

    private static ulong ComputeScalar(ReadOnlySpan<byte> bgra, int stride, int rowBytes, int height)
    {
        Span<uint> lanes = stackalloc uint[8] { 0x243F6A88u, 0x85A308D3u, 0x13198A2Eu, 0x03707344u, 0xA4093822u, 0x299F31D0u, 0x082EFA98u, 0xEC4E6C89u };
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = bgra.Slice(y * stride, rowBytes);
            int lane = 0;
            int x = 0;
            for (; x + 4 <= rowBytes; x += 4)
            {
                uint v = BitConverter.ToUInt32(row.Slice(x, 4));
                uint a = (lanes[lane] ^ v) * Prime;
                lanes[lane] = uint.RotateLeft(a, 13);
                lane = (lane + 1) & 7;
            }

            if (x < rowBytes)
            {
                uint tail = 0;
                for (int i = 0; x + i < rowBytes; i++)
                {
                    tail |= (uint)row[x + i] << (8 * i);
                }

                lanes[lane] = uint.RotateLeft((lanes[lane] ^ tail) * Prime, 13);
            }

            for (int i = 0; i < 8; i++)
            {
                lanes[i] += (uint)y + 1;
            }
        }

        ulong h = 0;
        for (int i = 0; i < 8; i++)
        {
            h = (h ^ lanes[i]) * 0xFF51AFD7ED558CCDUL;
            h ^= h >> 29;
        }

        return Mix(h);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Fold(Vector256<uint> acc)
    {
        ulong h = 0;
        for (int i = 0; i < 8; i++)
        {
            h = (h ^ acc.GetElement(i)) * 0xFF51AFD7ED558CCDUL;
            h ^= h >> 29;
        }

        return Mix(h);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong h)
    {
        h ^= h >> 33;
        h *= 0xC4CEB9FE1A85EC53UL;
        h ^= h >> 33;
        return h ^ Prime2;
    }
}
