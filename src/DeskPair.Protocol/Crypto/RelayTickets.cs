using System.Security.Cryptography;
using Google.Protobuf;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Protocol.Crypto;

/// <summary>
/// Issues and checks <see cref="RelayTicket"/>s: the rendezvous server's signed permission to open one
/// relay session.
///
/// A relay used to pair any two sockets that agreed on a uuid, which made every relay a free TCP proxy for
/// anyone who could reach its port. Now the rendezvous signs (uuid, expiry) with the same key that signs
/// host identities, the relay is configured with that key, and a request without a verifying ticket is
/// refused before a byte is forwarded. The UDP media relay is covered by the same ticket: the pairing
/// token both peers present is derived from it (<see cref="UdpToken"/>), and a bind carries the ticket.
/// </summary>
public static class RelayTickets
{
    private static readonly byte[] UdpTokenLabel = "sunllo/relay/udp"u8.ToArray();

    /// <summary>A serialised ticket is well under this; a bind datagram carrying more is not one.</summary>
    public const int MaxBytes = 512;

    public static RelayTicket Issue(IdentityKey serverKey, string uuid, string relayServer, DateTimeOffset expires)
    {
        byte[] payload = new RelayTicketPayload { Uuid = uuid, RelayServer = relayServer, ExpiresUnix = expires.ToUnixTimeSeconds() }.ToByteArray();
        return new RelayTicket
        {
            Payload = ByteString.CopyFrom(payload),
            Signature = ByteString.CopyFrom(serverKey.Sign(payload)),
        };
    }

    /// <summary>
    /// Checks the signature, the expiry and, when <paramref name="uuid"/> is given, that the ticket is for
    /// that pairing. <paramref name="reason"/> says which check failed, in words fit for a log line.
    /// </summary>
    public static bool TryVerify(ReadOnlySpan<byte> serverPublicKeySpki, RelayTicket? ticket, DateTimeOffset now, string? uuid, out RelayTicketPayload payload, out string reason)
    {
        payload = new RelayTicketPayload();
        if (ticket is null || ticket.Payload.IsEmpty)
        {
            reason = "no ticket";
            return false;
        }

        if (ticket.Payload.Length > MaxBytes || ticket.Signature.Length != IdentityKey.SignatureBytes)
        {
            reason = "malformed ticket";
            return false;
        }

        if (!IdentityKey.Verify(serverPublicKeySpki, ticket.Payload.Span, ticket.Signature.Span))
        {
            reason = "ticket signature does not verify";
            return false;
        }

        try
        {
            payload = RelayTicketPayload.Parser.ParseFrom(ticket.Payload);
        }
        catch (InvalidProtocolBufferException)
        {
            reason = "ticket payload does not parse";
            return false;
        }

        if (payload.ExpiresUnix < now.ToUnixTimeSeconds())
        {
            reason = "ticket expired";
            return false;
        }

        if (uuid is not null && !string.Equals(payload.Uuid, uuid, StringComparison.Ordinal))
        {
            reason = "ticket is for another pairing";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>The pairing uuid a ticket names, without verifying it; null when there is no ticket.</summary>
    public static string? UuidOf(RelayTicket? ticket)
    {
        if (ticket is null || ticket.Payload.IsEmpty)
        {
            return null;
        }

        try
        {
            return RelayTicketPayload.Parser.ParseFrom(ticket.Payload).Uuid;
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }

    /// <summary>
    /// The 16-byte token both peers present to the UDP relay for this ticket: the first half of
    /// SHA-256("sunllo/relay/udp" || payload). Derived rather than random so the relay can tie a bind to
    /// the ticket that came with it.
    /// </summary>
    public static byte[] UdpToken(RelayTicket ticket)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(UdpTokenLabel);
        sha.AppendData(ticket.Payload.Span);
        return sha.GetHashAndReset()[..Media.RelayDatagram.TokenBytes];
    }
}
