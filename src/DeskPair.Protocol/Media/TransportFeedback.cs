using System.Buffers.Binary;

namespace DeskPair.Protocol.Media;

/// <summary>
/// Per-packet arrival report (in the spirit of WebRTC transport-cc): which packets in a sequence range arrived and
/// when, on the receiver's clock. The sender matches them with its own send times to estimate queueing delay trends.
/// Layout: base packet seq (8), count (2), first arrival µs (8), receive bitmap (ceil(count/8)), then for every
/// received packet after the first a zig-zag varint arrival delta in 250 µs steps.
/// </summary>
public static class TransportFeedback
{
    public const int MaxPacketsPerReport = 256;
    public const int HeaderSize = 18;
    private const long StepUs = 250;

    public readonly record struct Arrival(ulong PacketSeq, long ArrivalUs);

    /// <summary>Worst-case encoded size for a report covering <paramref name="count"/> sequence numbers.</summary>
    public static int MaxSize(int count) => HeaderSize + (count + 7) / 8 + count * 10;

    /// <summary>Encodes <paramref name="arrivals"/> (sorted by sequence, spanning at most <see cref="MaxPacketsPerReport"/>) and returns the length.</summary>
    public static int Write(ReadOnlySpan<Arrival> arrivals, Span<byte> dest)
    {
        if (arrivals.IsEmpty)
        {
            return 0;
        }

        ulong baseSeq = arrivals[0].PacketSeq;
        ulong span = arrivals[^1].PacketSeq - baseSeq + 1;
        if (span > MaxPacketsPerReport)
        {
            throw new ArgumentException($"A report covers at most {MaxPacketsPerReport} sequence numbers.", nameof(arrivals));
        }

        int count = (int)span;
        BinaryPrimitives.WriteUInt64LittleEndian(dest, baseSeq);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[8..], (ushort)count);
        long reference = arrivals[0].ArrivalUs;
        BinaryPrimitives.WriteInt64LittleEndian(dest[10..], reference);
        int bitmapBytes = (count + 7) / 8;
        Span<byte> bitmap = dest.Slice(HeaderSize, bitmapBytes);
        bitmap.Clear();
        int offset = HeaderSize + bitmapBytes;
        long previousSteps = 0;
        for (int i = 0; i < arrivals.Length; i++)
        {
            int index = (int)(arrivals[i].PacketSeq - baseSeq);
            bitmap[index / 8] |= (byte)(1 << (index % 8));
            long steps = (long)Math.Round((arrivals[i].ArrivalUs - reference) / (double)StepUs);
            if (i > 0)
            {
                offset += WriteVarint(dest[offset..], ZigZag(steps - previousSteps));
            }

            previousSteps = steps;
        }

        return offset;
    }

    /// <summary>Decodes a report; <paramref name="lost"/> counts sequence numbers in the range that did not arrive.</summary>
    public static bool TryRead(ReadOnlySpan<byte> src, List<Arrival> received, out int lost)
    {
        received.Clear();
        lost = 0;
        if (src.Length < HeaderSize)
        {
            return false;
        }

        ulong baseSeq = BinaryPrimitives.ReadUInt64LittleEndian(src);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(src[8..]);
        long reference = BinaryPrimitives.ReadInt64LittleEndian(src[10..]);
        int bitmapBytes = (count + 7) / 8;
        if (count == 0 || count > MaxPacketsPerReport || src.Length < HeaderSize + bitmapBytes)
        {
            return false;
        }

        ReadOnlySpan<byte> bitmap = src.Slice(HeaderSize, bitmapBytes);
        int offset = HeaderSize + bitmapBytes;
        long steps = 0;
        bool first = true;
        for (int index = 0; index < count; index++)
        {
            if ((bitmap[index / 8] & (1 << (index % 8))) == 0)
            {
                lost++;
                continue;
            }

            if (!first)
            {
                if (!TryReadVarint(src, ref offset, out ulong raw))
                {
                    return false;
                }

                steps += UnZigZag(raw);
            }

            first = false;
            received.Add(new Arrival(baseSeq + (ulong)index, reference + steps * StepUs));
        }

        return true;
    }

    private static ulong ZigZag(long v) => (ulong)((v << 1) ^ (v >> 63));

    private static long UnZigZag(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);

    private static int WriteVarint(Span<byte> dest, ulong value)
    {
        int n = 0;
        while (value >= 0x80)
        {
            dest[n++] = (byte)(value | 0x80);
            value >>= 7;
        }

        dest[n++] = (byte)value;
        return n;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> src, ref int offset, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (offset >= src.Length)
            {
                return false;
            }

            byte b = src[offset++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }
}
