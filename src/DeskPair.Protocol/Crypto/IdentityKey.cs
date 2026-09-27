using System.Security.Cryptography;

namespace DeskPair.Protocol.Crypto;

/// <summary>
/// Long-term ECDSA P-256 signing key. Used both for device identities and for the rendezvous server key.
/// Signatures are 64-byte r||s over SHA-256.
/// </summary>
public sealed class IdentityKey : IDisposable
{
    public const int SignatureBytes = 64;

    private readonly ECDsa _key;

    private IdentityKey(ECDsa key)
    {
        _key = key;
        PublicKeySpki = key.ExportSubjectPublicKeyInfo();
    }

    /// <summary>SPKI DER encoding of the public key (91 bytes for P-256).</summary>
    public byte[] PublicKeySpki { get; }

    public static IdentityKey Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static IdentityKey FromPkcs8(ReadOnlySpan<byte> pkcs8)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            return new IdentityKey(key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public byte[] ExportPkcs8() => _key.ExportPkcs8PrivateKey();

    public byte[] Sign(ReadOnlySpan<byte> data) =>
        _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public static bool Verify(ReadOnlySpan<byte> publicKeySpki, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != SignatureBytes)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>SHA-256 of the SPKI, the value users compare when pinning a key.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> publicKeySpki) =>
        Convert.ToHexString(SHA256.HashData(publicKeySpki));

    public string Fingerprint() => Fingerprint(PublicKeySpki);

    public void Dispose() => _key.Dispose();
}
