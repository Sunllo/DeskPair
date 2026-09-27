using System.Buffers.Binary;

namespace DeskPair.Protocol.Media;

public enum RelayDatagramType : byte
{
    Bind = 1,
    BindAck = 2,
    Reject = 3,

    /// <summary>A bind that carries the rendezvous server's relay ticket after the token; protocol 2.</summary>
    BindTicket = 4,
}

/// <summary>
/// The only thing the UDP relay understands: a plaintext pairing message. Two endpoints that present the
/// same token become a pair and every other datagram between them is forwarded untouched (media datagrams
/// start with a different magic byte, so the relay never confuses the two).
///
/// Bind, BindAck and Reject are 20 bytes. BindTicket is 20 bytes plus the serialised ticket, whose length
/// sits in the two bytes protocol 1 left as padding; a relay that checks tickets answers a plain Bind
/// with Reject.
/// </summary>
public static class RelayDatagram
{
    public const byte Magic = 0x52; // 'R'
    public const int Size = 20;
    public const int TokenBytes = 16;

    /// <summary>The largest bind datagram a relay reads: the fixed part plus a ticket.</summary>
    public const int MaxSize = Size + Crypto.RelayTickets.MaxBytes;

    public static bool IsRelayDatagram(ReadOnlySpan<byte> datagram) => datagram.Length >= Size && datagram[0] == Magic;

    public static void Write(Span<byte> dest, RelayDatagramType type, ReadOnlySpan<byte> token)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(token.Length, TokenBytes);
        ArgumentOutOfRangeException.ThrowIfEqual((byte)type, (byte)RelayDatagramType.BindTicket);
        dest[0] = Magic;
        dest[1] = (byte)type;
        dest[2] = 0;
        dest[3] = 0;
        token.CopyTo(dest[4..]);
    }

    /// <summary>Writes a BindTicket and returns its length.</summary>
    public static int WriteBindTicket(Span<byte> dest, ReadOnlySpan<byte> token, ReadOnlySpan<byte> ticket)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(token.Length, TokenBytes);
        ArgumentOutOfRangeException.ThrowIfZero(ticket.Length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ticket.Length, Crypto.RelayTickets.MaxBytes);
        dest[0] = Magic;
        dest[1] = (byte)RelayDatagramType.BindTicket;
        BinaryPrimitives.WriteUInt16LittleEndian(dest[2..], (ushort)ticket.Length);
        token.CopyTo(dest[4..]);
        ticket.CopyTo(dest[Size..]);
        return Size + ticket.Length;
    }

    public static bool TryRead(ReadOnlySpan<byte> src, out RelayDatagramType type, out ReadOnlySpan<byte> token) =>
        TryRead(src, out type, out token, out _);

    /// <summary><paramref name="ticket"/> is the serialised ticket of a BindTicket and empty for every other type.</summary>
    public static bool TryRead(ReadOnlySpan<byte> src, out RelayDatagramType type, out ReadOnlySpan<byte> token, out ReadOnlySpan<byte> ticket)
    {
        type = default;
        token = default;
        ticket = default;
        if (!IsRelayDatagram(src) || src[1] is 0 or > (byte)RelayDatagramType.BindTicket)
        {
            return false;
        }

        type = (RelayDatagramType)src[1];
        if (type == RelayDatagramType.BindTicket)
        {
            int length = BinaryPrimitives.ReadUInt16LittleEndian(src[2..]);
            if (length == 0 || length > Crypto.RelayTickets.MaxBytes || src.Length != Size + length)
            {
                return false;
            }

            ticket = src.Slice(Size, length);
        }
        else if (src.Length != Size)
        {
            return false;
        }

        token = src.Slice(4, TokenBytes);
        return true;
    }
}
