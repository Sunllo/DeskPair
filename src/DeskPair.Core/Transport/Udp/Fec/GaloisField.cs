using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace DeskPair.Core.Transport.Udp.Fec;

/// <summary>
/// GF(2^8) with the 0x11D polynomial: log/exp tables for scalar maths and, per coefficient, split-nibble
/// lookup tables so <see cref="MultiplyAdd"/> can process 32 bytes per AVX2 shuffle pair.
/// </summary>
public static class GaloisField
{
    private const int Polynomial = 0x11D;
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];
    private static readonly byte[][] NibbleTables = new byte[256][];

    static GaloisField()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if (x >= 256)
            {
                x ^= Polynomial;
            }
        }

        for (int i = 255; i < 512; i++)
        {
            Exp[i] = Exp[i - 255];
        }
    }

    public static byte Multiply(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    public static byte Inverse(byte a) => a == 0 ? throw new DivideByZeroException("GF(256) has no inverse of 0.") : Exp[255 - Log[a]];

    public static byte Divide(byte a, byte b) => a == 0 ? (byte)0 : Exp[Log[a] + 255 - Log[b]];

    /// <summary>dst[i] ^= coef * src[i] over the whole span.</summary>
    public static void MultiplyAdd(Span<byte> dst, ReadOnlySpan<byte> src, byte coef)
    {
        if (coef == 0)
        {
            return;
        }

        int n = Math.Min(dst.Length, src.Length);
        int i = 0;
        if (coef == 1)
        {
            for (; i < n; i++)
            {
                dst[i] ^= src[i];
            }

            return;
        }

        if (Avx2.IsSupported && n >= 32)
        {
            byte[] table = Table(coef);
            Vector256<byte> lo = Vector256.Create(table, 0);
            Vector256<byte> hi = Vector256.Create(table, 32);
            Vector256<byte> mask = Vector256.Create((byte)0x0F);
            ref byte s = ref MemoryMarshal.GetReference(src);
            ref byte d = ref MemoryMarshal.GetReference(dst);
            for (; i + 32 <= n; i += 32)
            {
                Vector256<byte> v = Vector256.LoadUnsafe(ref s, (nuint)i);
                Vector256<byte> low = v & mask;
                Vector256<byte> high = Avx2.ShiftRightLogical(v.AsUInt16(), 4).AsByte() & mask;
                Vector256<byte> product = Avx2.Shuffle(lo, low) ^ Avx2.Shuffle(hi, high);
                (Vector256.LoadUnsafe(ref d, (nuint)i) ^ product).StoreUnsafe(ref d, (nuint)i);
            }
        }

        int logC = Log[coef];
        for (; i < n; i++)
        {
            byte v = src[i];
            if (v != 0)
            {
                dst[i] ^= Exp[Log[v] + logC];
            }
        }
    }

    /// <summary>64 bytes: products of coef with the 16 low nibbles (replicated per 128-bit lane), then the 16 high nibbles.</summary>
    private static byte[] Table(byte coef)
    {
        byte[]? t = NibbleTables[coef];
        if (t is null)
        {
            t = new byte[64];
            for (int nibble = 0; nibble < 16; nibble++)
            {
                byte low = Multiply(coef, (byte)nibble);
                byte high = Multiply(coef, (byte)(nibble << 4));
                t[nibble] = low;
                t[16 + nibble] = low;
                t[32 + nibble] = high;
                t[48 + nibble] = high;
            }

            NibbleTables[coef] = t;
        }

        return t;
    }
}
