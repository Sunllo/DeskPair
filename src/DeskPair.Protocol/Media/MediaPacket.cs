using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DeskPair.Protocol.Media;

public enum MediaPacketType : byte
{
    Video = 0,
    Audio = 1,
    Feedback = 2,
    Ping = 3,
    Pong = 4,
    Bind = 5,
    BindAck = 6,
    Close = 7,

    /// <summary>Per-packet arrival report for bandwidth estimation (see TransportFeedback).</summary>
    TransportFeedback = 8,

    /// <summary>Bandwidth probe padding: carries no data, only measured for send/arrival timing.</summary>
    Padding = 9,
}

[Flags]
public enum MediaPacketFlags : ushort
{
    None = 0,
    KeyFrame = 1,
    Parity = 2,
    LastShard = 4,
}

/// <summary>
/// Plaintext prefix of every media datagram (12 bytes, little-endian): magic, type, flags and the per-direction
/// packet sequence that doubles as the AES-GCM nonce counter and the replay-window key.
/// </summary>
public readonly record struct MediaCommonHeader(MediaPacketType Type, MediaPacketFlags Flags, ulong PacketSeq)
{
    public const int Size = ProtocolConstants.MediaCommonHeaderBytes;

    public void Write(Span<byte> dest)
    {
        dest[0] = ProtocolConstants.MediaMagic;
        dest[1] = (byte)Type;
        BinaryPrimitives.WriteUInt16LittleEndian(dest[2..], (ushort)Flags);
        BinaryPrimitives.WriteUInt64LittleEndian(dest[4..], PacketSeq);
    }

    public static bool TryRead(ReadOnlySpan<byte> src, out MediaCommonHeader header)
    {
        header = default;
        if (src.Length < Size || src[0] != ProtocolConstants.MediaMagic || src[1] > (byte)MediaPacketType.Padding)
        {
            return false;
        }

        header = new MediaCommonHeader((MediaPacketType)src[1], (MediaPacketFlags)BinaryPrimitives.ReadUInt16LittleEndian(src[2..]), BinaryPrimitives.ReadUInt64LittleEndian(src[4..]));
        return true;
    }
}

/// <summary>Encrypted header of a video/audio shard (32 bytes): which frame, which FEC block, which shard, and the frame's shape.</summary>
public readonly record struct MediaShardHeader(
    byte Stream,
    byte Codec,
    byte BlockIndex,
    byte BlockCount,
    uint FrameSeq,
    long PtsMs,
    ushort ShardIndex,
    byte K,
    byte M,
    ushort ShardLength,
    uint FrameLength,
    ushort Width,
    ushort Height)
{
    public const int Size = ProtocolConstants.MediaHeaderBytes;

    public bool IsParity => ShardIndex >= K;

    public void Write(Span<byte> dest)
    {
        dest[0] = Stream;
        dest[1] = Codec;
        dest[2] = BlockIndex;
        dest[3] = BlockCount;
        BinaryPrimitives.WriteUInt32LittleEndian(dest[4..], FrameSeq);
        BinaryPrimitives.WriteInt64LittleEndian(dest[8..], PtsMs);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[16..], ShardIndex);
        dest[18] = K;
        dest[19] = M;
        BinaryPrimitives.WriteUInt16LittleEndian(dest[20..], ShardLength);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[22..], FrameLength);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[26..], Width);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[28..], Height);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[30..], 0);
    }

    public static bool TryRead(ReadOnlySpan<byte> src, out MediaShardHeader header)
    {
        header = default;
        if (src.Length < Size)
        {
            return false;
        }

        header = new MediaShardHeader(
            src[0], src[1], src[2], src[3],
            BinaryPrimitives.ReadUInt32LittleEndian(src[4..]),
            BinaryPrimitives.ReadInt64LittleEndian(src[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(src[16..]),
            src[18], src[19],
            BinaryPrimitives.ReadUInt16LittleEndian(src[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(src[22..]),
            BinaryPrimitives.ReadUInt16LittleEndian(src[26..]),
            BinaryPrimitives.ReadUInt16LittleEndian(src[28..]));
        return header.K > 0 && header.BlockCount > 0 && header.BlockIndex < header.BlockCount && header.ShardIndex < header.K + header.M && header.ShardLength <= ProtocolConstants.MaxShardBytes;
    }
}

/// <summary>What a <see cref="MediaFeedback"/> report's second byte says about it.</summary>
[Flags]
public enum MediaFeedbackFlags : byte
{
    None = 0,

    /// <summary>
    /// The link-level fields (loss, received bytes, the echo) repeat another report of the same round and are to
    /// be ignored. A viewer watching several displays sends one report per stream, but the link is one link.
    /// Deliberately the inverted sense: a receiver that predates it sends 0, which reads as "these are yours".
    /// </summary>
    LinkFieldsIgnored = 1,
}

/// <summary>
/// Receiver → sender report (48 bytes, encrypted) that replaces the per-frame TCP ack on the UDP channel: one
/// per stream per round. The frame fields belong to <see cref="Stream"/>; the link fields belong to the path.
/// </summary>
public readonly record struct MediaFeedback(
    byte Stream,
    ushort LossPermille,
    uint HighestDecodableFrameSeq,
    uint LastFrameSeqReceived,
    ulong EchoPacketSeq,
    uint EchoDelayMicros,
    ulong ReceivedBytes,
    ulong ReceiverClockMicros,
    uint FramesGivenUp,
    uint ShardsRecovered,
    MediaFeedbackFlags Flags = MediaFeedbackFlags.None)
{
    public const int Size = 48;

    /// <summary>False when <see cref="MediaFeedbackFlags.LinkFieldsIgnored"/> says another report of this round carries them.</summary>
    public bool CarriesLinkFields => (Flags & MediaFeedbackFlags.LinkFieldsIgnored) == 0;

    public void Write(Span<byte> dest)
    {
        dest[0] = Stream;
        dest[1] = (byte)Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(dest[2..], LossPermille);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[4..], HighestDecodableFrameSeq);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[8..], LastFrameSeqReceived);
        BinaryPrimitives.WriteUInt64LittleEndian(dest[12..], EchoPacketSeq);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[20..], EchoDelayMicros);
        BinaryPrimitives.WriteUInt64LittleEndian(dest[24..], ReceivedBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(dest[32..], ReceiverClockMicros);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[40..], FramesGivenUp);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[44..], ShardsRecovered);
    }

    public static bool TryRead(ReadOnlySpan<byte> src, out MediaFeedback feedback)
    {
        feedback = default;
        if (src.Length < Size)
        {
            return false;
        }

        feedback = new MediaFeedback(
            src[0],
            BinaryPrimitives.ReadUInt16LittleEndian(src[2..]),
            BinaryPrimitives.ReadUInt32LittleEndian(src[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(src[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(src[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(src[20..]),
            BinaryPrimitives.ReadUInt64LittleEndian(src[24..]),
            BinaryPrimitives.ReadUInt64LittleEndian(src[32..]),
            BinaryPrimitives.ReadUInt32LittleEndian(src[40..]),
            BinaryPrimitives.ReadUInt32LittleEndian(src[44..]),
            (MediaFeedbackFlags)src[1]);
        return true;
    }
}

/// <summary>
/// AES-256-GCM for datagrams: the nonce is the 4-byte direction prefix followed by the packet sequence
/// carried in the clear, so packets can be opened in any order; the plaintext common header is the
/// associated data. A 1024-entry sliding window rejects replays.
/// </summary>
public sealed class MediaCipher : IDisposable
{
    public const int TagBytes = ProtocolConstants.MediaTagBytes;
    private const int WindowBits = 1024;

    private readonly AesGcm _tx;
    private readonly AesGcm _rx;
    private readonly byte[] _txNonce = new byte[12];
    private readonly byte[] _rxNonce = new byte[12];
    private readonly ulong[] _window = new ulong[WindowBits / 64];
    private ulong _txSeq;
    private ulong _rxHighest;
    private bool _disposed;

    public MediaCipher(ReadOnlySpan<byte> txKey, ReadOnlySpan<byte> txIvPrefix, ReadOnlySpan<byte> rxKey, ReadOnlySpan<byte> rxIvPrefix)
    {
        if (!AesGcm.IsSupported)
        {
            throw new PlatformNotSupportedException("AES-GCM is not available on this platform.");
        }

        _tx = new AesGcm(txKey, TagBytes);
        _rx = new AesGcm(rxKey, TagBytes);
        txIvPrefix.CopyTo(_txNonce);
        rxIvPrefix.CopyTo(_rxNonce);
    }

    public ulong NextPacketSeq => _txSeq + 1;

    /// <summary>Builds a complete datagram: header, ciphertext, tag. Returns its length. Not thread-safe.</summary>
    public int Seal(MediaPacketType type, MediaPacketFlags flags, ReadOnlySpan<byte> plaintext, Span<byte> datagram, out ulong packetSeq)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        packetSeq = ++_txSeq;
        var header = new MediaCommonHeader(type, flags, packetSeq);
        header.Write(datagram);
        BinaryPrimitives.WriteUInt64LittleEndian(_txNonce.AsSpan(4), packetSeq);
        int total = MediaCommonHeader.Size + plaintext.Length + TagBytes;
        _tx.Encrypt(_txNonce, plaintext, datagram.Slice(MediaCommonHeader.Size, plaintext.Length), datagram.Slice(MediaCommonHeader.Size + plaintext.Length, TagBytes), datagram[..MediaCommonHeader.Size]);
        return total;
    }

    /// <summary>Authenticates and decrypts a datagram; false for garbage, tampering, or a replayed sequence.</summary>
    public bool TryOpen(ReadOnlySpan<byte> datagram, out MediaCommonHeader header, Span<byte> plaintext, out int plaintextLength)
    {
        plaintextLength = 0;
        if (!MediaCommonHeader.TryRead(datagram, out header) || datagram.Length < MediaCommonHeader.Size + TagBytes)
        {
            return false;
        }

        if (!IsFresh(header.PacketSeq))
        {
            return false;
        }

        int cipherLength = datagram.Length - MediaCommonHeader.Size - TagBytes;
        if (plaintext.Length < cipherLength)
        {
            return false;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(_rxNonce.AsSpan(4), header.PacketSeq);
        try
        {
            _rx.Decrypt(_rxNonce, datagram.Slice(MediaCommonHeader.Size, cipherLength), datagram.Slice(MediaCommonHeader.Size + cipherLength, TagBytes), plaintext[..cipherLength], datagram[..MediaCommonHeader.Size]);
        }
        catch (CryptographicException)
        {
            return false;
        }

        Mark(header.PacketSeq);
        plaintextLength = cipherLength;
        return true;
    }

    private bool IsFresh(ulong seq)
    {
        if (seq == 0)
        {
            return false;
        }

        if (seq > _rxHighest)
        {
            return true;
        }

        ulong age = _rxHighest - seq;
        if (age >= WindowBits)
        {
            return false;
        }

        return (_window[(int)(seq % WindowBits) / 64] & (1UL << (int)(seq % 64))) == 0;
    }

    private void Mark(ulong seq)
    {
        if (seq > _rxHighest)
        {
            // Clear the bits the window slides over.
            ulong advance = Math.Min(seq - _rxHighest, WindowBits);
            for (ulong s = _rxHighest + 1; s <= _rxHighest + advance; s++)
            {
                _window[(int)(s % WindowBits) / 64] &= ~(1UL << (int)(s % 64));
            }

            _rxHighest = seq;
        }

        _window[(int)(seq % WindowBits) / 64] |= 1UL << (int)(seq % 64);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _tx.Dispose();
            _rx.Dispose();
        }
    }
}
