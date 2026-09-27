using System.Security.Cryptography;
using DeskPair.Protocol;
using DeskPair.Protocol.Crypto;
using DeskPair.Protocol.Media;
using DeskPair.Protocol.Messages;

namespace DeskPair.Protocol.Tests;

public class MediaPacketTests
{
    private static (MediaCipher A, MediaCipher B) Pair()
    {
        byte[] k1 = RandomNumberGenerator.GetBytes(32), k2 = RandomNumberGenerator.GetBytes(32);
        byte[] iv1 = RandomNumberGenerator.GetBytes(4), iv2 = RandomNumberGenerator.GetBytes(4);
        return (new MediaCipher(k1, iv1, k2, iv2), new MediaCipher(k2, iv2, k1, iv1));
    }

    [Fact]
    public void Shard_header_round_trips()
    {
        var h = new MediaShardHeader(1, 2, 3, 4, 123456u, 9876543210L, 77, 100, 20, 1140, 200000u, 2560, 1440);
        Span<byte> buf = stackalloc byte[MediaShardHeader.Size];
        h.Write(buf);
        MediaShardHeader.TryRead(buf, out MediaShardHeader back).ShouldBeTrue();
        back.ShouldBe(h);
        back.IsParity.ShouldBeFalse();

        var bad = new MediaShardHeader(1, 2, 5, 4, 1, 0, 0, 10, 2, 100, 100, 1, 1); // block index out of range
        bad.Write(buf);
        MediaShardHeader.TryRead(buf, out _).ShouldBeFalse();
    }

    [Fact]
    public void Feedback_round_trips()
    {
        var f = new MediaFeedback(0, 25, 1000, 1003, 555555555555UL, 120, 123456789UL, 987654321UL, 3, 42);
        Span<byte> buf = stackalloc byte[MediaFeedback.Size];
        f.Write(buf);
        MediaFeedback.TryRead(buf, out MediaFeedback back).ShouldBeTrue();
        back.ShouldBe(f);
    }

    /// <summary>
    /// The second byte was always written as zero, so a report from a viewer that predates the flag reads as
    /// carrying the link's fields -- which it does. Only a report that says otherwise is set aside.
    /// </summary>
    [Fact]
    public void Feedback_flags_ride_in_the_byte_that_used_to_be_zero()
    {
        Span<byte> buf = stackalloc byte[MediaFeedback.Size];
        new MediaFeedback(1, 25, 7, 7, 0, 0, 1, 2, 0, 0).Write(buf);
        buf[1].ShouldBe((byte)0);
        MediaFeedback.TryRead(buf, out MediaFeedback old).ShouldBeTrue();
        old.CarriesLinkFields.ShouldBeTrue();

        var copy = new MediaFeedback(2, 25, 9, 9, 0, 0, 1, 2, 0, 0, MediaFeedbackFlags.LinkFieldsIgnored);
        copy.Write(buf);
        buf[1].ShouldBe((byte)1);
        MediaFeedback.TryRead(buf, out MediaFeedback back).ShouldBeTrue();
        back.ShouldBe(copy);
        back.CarriesLinkFields.ShouldBeFalse();
    }

    [Fact]
    public void Datagrams_are_sealed_opened_and_rejected_when_tampered_or_replayed()
    {
        (MediaCipher a, MediaCipher b) = Pair();
        byte[] payload = RandomNumberGenerator.GetBytes(1140);
        byte[] datagram = new byte[ProtocolConstants.MaxUdpDatagramBytes];
        int n = a.Seal(MediaPacketType.Video, MediaPacketFlags.KeyFrame | MediaPacketFlags.LastShard, payload, datagram, out ulong seq);
        n.ShouldBe(MediaCommonHeader.Size + payload.Length + MediaCipher.TagBytes);
        n.ShouldBeLessThanOrEqualTo(ProtocolConstants.MaxUdpDatagramBytes);
        seq.ShouldBe(1UL);

        byte[] plain = new byte[1200];
        b.TryOpen(datagram.AsSpan(0, n), out MediaCommonHeader header, plain, out int len).ShouldBeTrue();
        header.Type.ShouldBe(MediaPacketType.Video);
        header.Flags.ShouldBe(MediaPacketFlags.KeyFrame | MediaPacketFlags.LastShard);
        header.PacketSeq.ShouldBe(1UL);
        plain.AsSpan(0, len).ToArray().ShouldBe(payload);

        // Replay of the same datagram is refused.
        b.TryOpen(datagram.AsSpan(0, n), out _, plain, out _).ShouldBeFalse();

        // A flipped payload byte fails authentication.
        int m = a.Seal(MediaPacketType.Video, MediaPacketFlags.None, payload, datagram, out _);
        datagram[MediaCommonHeader.Size + 5] ^= 1;
        b.TryOpen(datagram.AsSpan(0, m), out _, plain, out _).ShouldBeFalse();

        // A flipped header flag is covered by the tag too.
        m = a.Seal(MediaPacketType.Video, MediaPacketFlags.None, payload, datagram, out _);
        datagram[2] ^= 1;
        b.TryOpen(datagram.AsSpan(0, m), out _, plain, out _).ShouldBeFalse();

        // Out-of-order arrival within the window is fine; ancient sequences are not.
        byte[][] batch = new byte[5][];
        int[] lengths = new int[5];
        for (int i = 0; i < 5; i++)
        {
            batch[i] = new byte[1200];
            lengths[i] = a.Seal(MediaPacketType.Ping, MediaPacketFlags.None, [(byte)i], batch[i], out _);
        }

        b.TryOpen(batch[4].AsSpan(0, lengths[4]), out _, plain, out _).ShouldBeTrue();
        b.TryOpen(batch[1].AsSpan(0, lengths[1]), out _, plain, out _).ShouldBeTrue();
        b.TryOpen(batch[1].AsSpan(0, lengths[1]), out _, plain, out _).ShouldBeFalse();
        b.TryOpen(batch[3].AsSpan(0, lengths[3]), out _, plain, out _).ShouldBeTrue();
    }

    [Fact]
    public void The_other_direction_uses_its_own_key()
    {
        (MediaCipher a, MediaCipher b) = Pair();
        byte[] datagram = new byte[200];
        int n = a.Seal(MediaPacketType.Ping, MediaPacketFlags.None, [1, 2, 3], datagram, out _);
        byte[] plain = new byte[200];
        a.TryOpen(datagram.AsSpan(0, n), out _, plain, out _).ShouldBeFalse(); // sealed with a's tx key, a's rx key differs
        b.TryOpen(datagram.AsSpan(0, n), out _, plain, out _).ShouldBeTrue();
    }

    [Fact]
    public void Handshake_derives_matching_media_keys_on_both_sides()
    {
        IdentityKey hostKey = IdentityKey.Create();
        (ControllerHello hello, Handshake.ControllerState state) = Handshake.BeginController("test");
        using (state)
        {
            (HostHello reply, SessionKeys hostKeys) = Handshake.RespondHost(hello, "host", hostKey, "test");
            using SessionKeys controllerKeys = Handshake.FinishController(reply, state, new AcceptAnyVerifier());
            using MediaKeys hostMedia = hostKeys.ExportMediaKeys();
            using MediaKeys controllerMedia = controllerKeys.ExportMediaKeys();
            hostMedia.IsCounterpartOf(controllerMedia).ShouldBeTrue();
            hostMedia.TxKey.SequenceEqual(hostKeys.TxKey).ShouldBeFalse();
            hostKeys.Dispose();
        }
    }

    [Fact]
    public void Relay_datagram_round_trips_and_rejects_media_bytes()
    {
        byte[] token = RandomNumberGenerator.GetBytes(RelayDatagram.TokenBytes);
        byte[] buf = new byte[RelayDatagram.Size];
        RelayDatagram.Write(buf, RelayDatagramType.BindAck, token);
        RelayDatagram.TryRead(buf, out RelayDatagramType type, out ReadOnlySpan<byte> back).ShouldBeTrue();
        type.ShouldBe(RelayDatagramType.BindAck);
        back.ToArray().ShouldBe(token);

        byte[] media = new byte[RelayDatagram.Size];
        media[0] = ProtocolConstants.MediaMagic;
        RelayDatagram.IsRelayDatagram(media).ShouldBeFalse();
    }

    private sealed class AcceptAnyVerifier : IHostIdentityVerifier
    {
        public void Verify(string hostId, ReadOnlySpan<byte> hostPublicKey)
        {
        }
    }
}
