using System.Security.Cryptography;

namespace DeskPair.Protocol.Crypto;

/// <summary>Per-session ECDH P-256 key; disposed as soon as the shared secret is derived.</summary>
public sealed class EphemeralKey : IDisposable
{
    private readonly ECDiffieHellman _key;

    private EphemeralKey(ECDiffieHellman key)
    {
        _key = key;
        PublicKeySpki = key.ExportSubjectPublicKeyInfo();
    }

    public byte[] PublicKeySpki { get; }

    public static EphemeralKey Create() => new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>
    /// A key with a known private part, so a handshake can be reproduced exactly. Used to generate the
    /// conformance vectors the Kotlin client is checked against; a real session always uses Create().
    /// </summary>
    internal static EphemeralKey FromPkcs8(ReadOnlySpan<byte> pkcs8)
    {
        var key = ECDiffieHellman.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            return new EphemeralKey(key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    internal byte[] ExportPkcs8()
    {
        using ECDiffieHellman clone = ECDiffieHellman.Create(_key.ExportParameters(includePrivateParameters: true));
        return clone.ExportPkcs8PrivateKey();
    }

    /// <summary>Raw x-coordinate shared secret (32 bytes); caller zeroes it after use.</summary>
    public byte[] DeriveRawSecret(ReadOnlySpan<byte> peerPublicKeySpki)
    {
        using var peer = ECDiffieHellman.Create();
        try
        {
            peer.ImportSubjectPublicKeyInfo(peerPublicKeySpki, out _);
        }
        catch (CryptographicException e)
        {
            throw new HandshakeException("Peer ephemeral key is malformed.", e);
        }

        using ECDiffieHellmanPublicKey peerPublic = peer.PublicKey;
        return _key.DeriveRawSecretAgreement(peerPublic);
    }

    public void Dispose() => _key.Dispose();
}
