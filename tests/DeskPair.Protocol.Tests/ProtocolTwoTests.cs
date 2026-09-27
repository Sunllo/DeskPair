using Google.Protobuf;
using DeskPair.Protocol.Crypto;
using DeskPair.Protocol.Media;
using DeskPair.Protocol.Messages;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Protocol.Tests;

/// <summary>
/// What protocol 2 added: a version range in the hello, a derivation named in the challenge, and the
/// rendezvous server's ticket for the relay. Each is pinned here from the shipping code, because the
/// phone implements the same three things in another language.
/// </summary>
public class ProtocolTwoTests
{
    // ---------------------------------------------------------------- version negotiation

    [Fact]
    public void A_controller_hello_carries_the_range_and_the_legacy_version_for_old_hosts()
    {
        (ControllerHello hello, Handshake.ControllerState state) = Handshake.BeginController("t");
        using (state)
        {
            hello.ProtocolVersion.ShouldBe(ProtocolConstants.LegacyHelloVersion);
            hello.MinProtocolVersion.ShouldBe(ProtocolConstants.MinProtocolVersion);
            hello.MaxProtocolVersion.ShouldBe(ProtocolConstants.ProtocolVersion);
        }
    }

    [Fact]
    public void The_host_picks_the_highest_version_both_speak()
    {
        Handshake.Negotiate(new ControllerHello { ProtocolVersion = 1, MinProtocolVersion = 2, MaxProtocolVersion = 2 }, out _).ShouldBe(2u);
        Handshake.Negotiate(new ControllerHello { ProtocolVersion = 1, MinProtocolVersion = 2, MaxProtocolVersion = 9 }, out _).ShouldBe(ProtocolConstants.ProtocolVersion);
    }

    [Fact]
    public void A_protocol_one_controller_is_refused_and_told_to_update_its_own_device()
    {
        // Exactly what a 0.2.x app sends: protocol_version 1 and no range.
        var old = new ControllerHello { ProtocolVersion = 1 };
        Handshake.Negotiate(old, out string refusal).ShouldBeNull();
        refusal.ShouldContain("Update DeskPair on the device you are connecting from");

        using IdentityKey host = IdentityKey.Create();
        HandshakeVersionException e = Should.Throw<HandshakeVersionException>(() => Handshake.RespondHost(old, "id", host, "t"));
        e.Refusal.Refusal.ShouldBe(refusal);
        e.Refusal.ProtocolVersion.ShouldBe(ProtocolConstants.ProtocolVersion, "an old controller reads this as 'unsupported protocol version 2', which is the same instruction");
        e.Refusal.Signature.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void A_controller_from_the_future_is_told_this_computer_must_update()
    {
        var future = new ControllerHello { ProtocolVersion = 1, MinProtocolVersion = ProtocolConstants.ProtocolVersion + 1, MaxProtocolVersion = ProtocolConstants.ProtocolVersion + 5 };
        Handshake.Negotiate(future, out string refusal).ShouldBeNull();
        refusal.ShouldContain("Update DeskPair on this computer");
    }

    [Fact]
    public void The_controller_shows_a_refusal_as_the_error_and_names_the_host_when_the_host_is_old()
    {
        (ControllerHello _, Handshake.ControllerState state) = Handshake.BeginController("t");
        using (state)
        {
            var refusal = new HostHello { Refusal = "who must update", ProtocolVersion = 2 };
            Should.Throw<HandshakeException>(() => Handshake.FinishController(refusal, state, AcceptAnyIdentityVerifier.Instance))
                .Message.ShouldBe("who must update");

            // A protocol-1 host accepts our hello (it looks like one of its own) and answers with 1.
            var oldHost = new HostHello { ProtocolVersion = 1, Id = "id" };
            Should.Throw<HandshakeException>(() => Handshake.FinishController(oldHost, state, AcceptAnyIdentityVerifier.Instance))
                .Message.ShouldContain("Update DeskPair on the computer you are connecting to");
        }
    }

    [Fact]
    public void A_full_handshake_settles_on_protocol_two()
    {
        using IdentityKey host = IdentityKey.Create();
        (ControllerHello ch, Handshake.ControllerState state) = Handshake.BeginController("t");
        using (state)
        {
            (HostHello hh, SessionKeys hostKeys) = Handshake.RespondHost(ch, "id", host, "t");
            using (hostKeys)
            {
                hh.ProtocolVersion.ShouldBe(2u);
                hh.Refusal.ShouldBeEmpty();
                using SessionKeys keys = Handshake.FinishController(hh, state, AcceptAnyIdentityVerifier.Instance);
                keys.IsCounterpartOf(hostKeys).ShouldBeTrue();
            }
        }
    }

    // ---------------------------------------------------------------- password derivation

    [Fact]
    public void Pbkdf2_h1_differs_from_the_single_hash_and_verifies_with_the_same_proof()
    {
        byte[] salt = PasswordProof.NewSalt();
        byte[] challenge = PasswordProof.NewChallenge();
        byte[] legacy = PasswordProof.ComputeH1("correct horse", salt, PasswordKdf.KdfSha256, 0);
        byte[] stretched = PasswordProof.ComputeH1("correct horse", salt, PasswordKdf.KdfPbkdf2Sha256, PasswordProof.DefaultIterations);

        stretched.ShouldNotBe(legacy);
        stretched.Length.ShouldBe(PasswordProof.HashBytes);
        PasswordProof.Verify(stretched, challenge, PasswordProof.ComputeProof("correct horse", salt, challenge, PasswordKdf.KdfPbkdf2Sha256, PasswordProof.DefaultIterations)).ShouldBeTrue();
        PasswordProof.Verify(stretched, challenge, PasswordProof.ComputeProof("correct horse", salt, challenge, PasswordKdf.KdfSha256, 0)).ShouldBeFalse("the derivation is part of the proof");
    }

    [Fact]
    public void Pbkdf2_matches_the_standard_and_refuses_absurd_rounds()
    {
        byte[] salt = new byte[16];
        byte[] expected = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2("pw", salt, 4096, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        PasswordProof.ComputeH1("pw", salt, PasswordKdf.KdfPbkdf2Sha256, 4096).ShouldBe(expected);

        Should.Throw<ArgumentOutOfRangeException>(() => PasswordProof.ComputeH1("pw", salt, PasswordKdf.KdfPbkdf2Sha256, PasswordProof.MaxIterations + 1));
        Should.Throw<ArgumentOutOfRangeException>(() => PasswordProof.ComputeH1("pw", salt, PasswordKdf.KdfPbkdf2Sha256, 10));
    }

    // ---------------------------------------------------------------- relay tickets

    [Fact]
    public void A_ticket_verifies_for_its_uuid_within_its_lifetime_and_for_nothing_else()
    {
        using IdentityKey server = IdentityKey.Create();
        using IdentityKey other = IdentityKey.Create();
        DateTimeOffset now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        RelayTicket ticket = RelayTickets.Issue(server, "abc123", string.Empty, now.AddMinutes(2));

        RelayTickets.TryVerify(server.PublicKeySpki, ticket, now, "abc123", out RelayTicketPayload payload, out _).ShouldBeTrue();
        payload.Uuid.ShouldBe("abc123");
        RelayTickets.UuidOf(ticket).ShouldBe("abc123");

        RelayTickets.TryVerify(server.PublicKeySpki, ticket, now.AddMinutes(3), "abc123", out _, out string why).ShouldBeFalse();
        why.ShouldContain("expired");
        RelayTickets.TryVerify(server.PublicKeySpki, ticket, now, "zzz", out _, out why).ShouldBeFalse();
        why.ShouldContain("another pairing");
        RelayTickets.TryVerify(other.PublicKeySpki, ticket, now, "abc123", out _, out why).ShouldBeFalse();
        why.ShouldContain("signature");
        RelayTickets.TryVerify(server.PublicKeySpki, null, now, "abc123", out _, out why).ShouldBeFalse();
        why.ShouldContain("no ticket");

        byte[] tampered = ticket.Payload.ToByteArray();
        tampered[^1] ^= 1;
        RelayTickets.TryVerify(server.PublicKeySpki, new RelayTicket { Payload = ByteString.CopyFrom(tampered), Signature = ticket.Signature }, now, "abc123", out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void The_udp_token_is_derived_from_the_ticket_and_is_the_same_on_both_sides()
    {
        using IdentityKey server = IdentityKey.Create();
        RelayTicket ticket = RelayTickets.Issue(server, "abc123", string.Empty, DateTimeOffset.UtcNow.AddMinutes(2));
        byte[] token = RelayTickets.UdpToken(ticket);
        token.Length.ShouldBe(RelayDatagram.TokenBytes);
        RelayTickets.UdpToken(RelayTicket.Parser.ParseFrom(ticket.ToByteArray())).ShouldBe(token);
        RelayTickets.UdpToken(RelayTickets.Issue(server, "abc124", string.Empty, DateTimeOffset.UtcNow.AddMinutes(2))).ShouldNotBe(token);
    }

    // ---------------------------------------------------------------- bind datagram

    [Fact]
    public void A_bind_with_a_ticket_round_trips_and_a_plain_bind_still_reads()
    {
        using IdentityKey server = IdentityKey.Create();
        RelayTicket ticket = RelayTickets.Issue(server, "abc123", string.Empty, DateTimeOffset.UtcNow.AddMinutes(2));
        byte[] token = RelayTickets.UdpToken(ticket);
        byte[] ticketBytes = ticket.ToByteArray();

        byte[] buffer = new byte[RelayDatagram.MaxSize];
        int n = RelayDatagram.WriteBindTicket(buffer, token, ticketBytes);
        n.ShouldBe(RelayDatagram.Size + ticketBytes.Length);
        RelayDatagram.IsRelayDatagram(buffer.AsSpan(0, n)).ShouldBeTrue();
        RelayDatagram.TryRead(buffer.AsSpan(0, n), out RelayDatagramType type, out ReadOnlySpan<byte> readToken, out ReadOnlySpan<byte> readTicket).ShouldBeTrue();
        type.ShouldBe(RelayDatagramType.BindTicket);
        readToken.ToArray().ShouldBe(token);
        readTicket.ToArray().ShouldBe(ticketBytes);

        // A byte short or long is not a bind.
        RelayDatagram.TryRead(buffer.AsSpan(0, n - 1), out _, out _, out _).ShouldBeFalse();
        RelayDatagram.TryRead(buffer.AsSpan(0, n + 1), out _, out _, out _).ShouldBeFalse();

        byte[] plain = new byte[RelayDatagram.Size];
        RelayDatagram.Write(plain, RelayDatagramType.Bind, token);
        RelayDatagram.TryRead(plain, out type, out readToken, out readTicket).ShouldBeTrue();
        type.ShouldBe(RelayDatagramType.Bind);
        readTicket.IsEmpty.ShouldBeTrue();

        // Media datagrams start with another magic byte and are never mistaken for a bind.
        RelayDatagram.IsRelayDatagram(new byte[] { ProtocolConstants.MediaMagic, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }).ShouldBeFalse();
    }
}
