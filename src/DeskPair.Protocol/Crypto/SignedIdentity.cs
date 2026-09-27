using Google.Protobuf;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Protocol.Crypto;

/// <summary>Creates and verifies rendezvous-server-signed peer identities.</summary>
public static class SignedIdentity
{
    public static SignedPeerIdentity Sign(IdentityKey serverKey, string id, ReadOnlySpan<byte> identityPk, DateTimeOffset issuedAt)
    {
        var payload = new PeerIdentity
        {
            Id = id,
            IdentityPk = ByteString.CopyFrom(identityPk),
            IssuedAtUnix = issuedAt.ToUnixTimeSeconds(),
        };
        byte[] bytes = payload.ToByteArray();
        return new SignedPeerIdentity
        {
            Payload = ByteString.CopyFrom(bytes),
            ServerSignature = ByteString.CopyFrom(serverKey.Sign(bytes)),
        };
    }

    public static bool TryVerify(ReadOnlySpan<byte> serverPublicKeySpki, SignedPeerIdentity signed, out PeerIdentity identity)
    {
        identity = new PeerIdentity();
        if (signed.Payload.IsEmpty || !IdentityKey.Verify(serverPublicKeySpki, signed.Payload.Span, signed.ServerSignature.Span))
        {
            return false;
        }

        try
        {
            identity = PeerIdentity.Parser.ParseFrom(signed.Payload);
            return identity.Id.Length > 0 && !identity.IdentityPk.IsEmpty;
        }
        catch (InvalidProtocolBufferException)
        {
            return false;
        }
    }
}

/// <summary>Decides whether a host's claimed identity key is trusted before the handshake completes.</summary>
public interface IHostIdentityVerifier
{
    /// <exception cref="HandshakeException">The identity is not trusted.</exception>
    void Verify(string hostId, ReadOnlySpan<byte> identityPk);
}

/// <summary>Trusts only the key inside a rendezvous-signed identity for the expected id.</summary>
public sealed class ServerSignedIdentityVerifier : IHostIdentityVerifier
{
    private readonly string _expectedId;
    private readonly byte[] _identityPk;

    public ServerSignedIdentityVerifier(ReadOnlySpan<byte> serverPublicKeySpki, string expectedId, SignedPeerIdentity signed)
    {
        if (!SignedIdentity.TryVerify(serverPublicKeySpki, signed, out PeerIdentity identity))
        {
            // Seen in practice as a rotated server key held against a pinned old one, not as an attack;
            // the refusal is right either way, and the sentence says what a person can do about it.
            throw new HandshakeException(
                "The rendezvous server's signature over the host identity does not verify. The server's key "
                + "has probably changed since it was saved: update the server public key in Settings, or clear "
                + "the server address to use the portal's directory.");
        }

        if (!string.Equals(identity.Id, expectedId, StringComparison.Ordinal))
        {
            throw new HandshakeException($"Rendezvous identity is for '{identity.Id}', expected '{expectedId}'.");
        }

        _expectedId = expectedId;
        _identityPk = identity.IdentityPk.ToByteArray();
    }

    public void Verify(string hostId, ReadOnlySpan<byte> identityPk)
    {
        if (!string.Equals(hostId, _expectedId, StringComparison.Ordinal))
        {
            throw new HandshakeException($"Host claims id '{hostId}', expected '{_expectedId}'.");
        }

        if (!identityPk.SequenceEqual(_identityPk))
        {
            throw new HandshakeException("Host identity key does not match the rendezvous-signed key.");
        }
    }
}

/// <summary>Accepts any key; for tests and for callers that pin keys themselves after the fact.</summary>
/// <summary>
/// Accepts whatever key the host presents. For handshake tests that are not about identity; the product
/// never uses it -- a connection by id without the server's key is refused (<c>PeerConnector.VerifierFor</c>).
/// </summary>
public sealed class AcceptAnyIdentityVerifier : IHostIdentityVerifier
{
    public static AcceptAnyIdentityVerifier Instance { get; } = new();

    public void Verify(string hostId, ReadOnlySpan<byte> identityPk)
    {
    }
}
