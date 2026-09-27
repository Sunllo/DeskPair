using System.Security.Cryptography;
using DeskPair.Protocol.Crypto;
using DeskPair.Protocol.Messages;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Protocol.Tests;

public class CryptoPrimitiveTests
{
    [Fact]
    public void Aes_gcm_is_supported_on_this_platform()
    {
        AesGcm.IsSupported.ShouldBeTrue();
    }

    [Fact]
    public void Password_proof_verifies_only_for_matching_password_and_challenge()
    {
        byte[] salt = PasswordProof.NewSalt();
        byte[] challenge = PasswordProof.NewChallenge();
        byte[] h1 = PasswordProof.ComputeH1("correct horse", salt);

        byte[] good = PasswordProof.ComputeProof("correct horse", salt, challenge, PasswordKdf.KdfSha256, 0);
        byte[] wrongPw = PasswordProof.ComputeProof("wrong", salt, challenge, PasswordKdf.KdfSha256, 0);
        byte[] wrongChallenge = PasswordProof.ComputeProof("correct horse", salt, PasswordProof.NewChallenge(), PasswordKdf.KdfSha256, 0);

        PasswordProof.Verify(h1, challenge, good).ShouldBeTrue();
        PasswordProof.Verify(h1, challenge, wrongPw).ShouldBeFalse();
        PasswordProof.Verify(h1, challenge, wrongChallenge).ShouldBeFalse();
        PasswordProof.Verify(h1, challenge, ReadOnlySpan<byte>.Empty).ShouldBeFalse();
    }

    [Fact]
    public void Password_proof_is_deterministic()
    {
        byte[] salt = new byte[16];
        byte[] challenge = new byte[32];
        PasswordProof.ComputeProof("pw", salt, challenge, PasswordKdf.KdfSha256, 0).ShouldBe(PasswordProof.ComputeProof("pw", salt, challenge, PasswordKdf.KdfSha256, 0));
        PasswordProof.ComputeH1("pw", salt).ShouldBe(SHA256.HashData("pw"u8.ToArray().Concat(salt).ToArray()));
    }

    [Fact]
    public void Identity_key_round_trips_through_pkcs8_and_signs_verifiably()
    {
        using IdentityKey key = IdentityKey.Create();
        using IdentityKey reloaded = IdentityKey.FromPkcs8(key.ExportPkcs8());
        reloaded.PublicKeySpki.ShouldBe(key.PublicKeySpki);

        byte[] data = "payload"u8.ToArray();
        byte[] sig = key.Sign(data);
        sig.Length.ShouldBe(IdentityKey.SignatureBytes);
        IdentityKey.Verify(reloaded.PublicKeySpki, data, sig).ShouldBeTrue();
        IdentityKey.Verify(reloaded.PublicKeySpki, "other"u8, sig).ShouldBeFalse();
        IdentityKey.Verify(IdentityKey.Create().PublicKeySpki, data, sig).ShouldBeFalse();
        IdentityKey.Verify(new byte[] { 1, 2, 3 }, data, sig).ShouldBeFalse();
    }

    [Fact]
    public void Signed_identity_verifies_only_under_the_issuing_server_key()
    {
        using IdentityKey server = IdentityKey.Create();
        using IdentityKey other = IdentityKey.Create();
        using IdentityKey host = IdentityKey.Create();

        SignedPeerIdentity signed = SignedIdentity.Sign(server, "987654321", host.PublicKeySpki, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

        SignedIdentity.TryVerify(server.PublicKeySpki, signed, out PeerIdentity identity).ShouldBeTrue();
        identity.Id.ShouldBe("987654321");
        identity.IdentityPk.ToByteArray().ShouldBe(host.PublicKeySpki);
        identity.IssuedAtUnix.ShouldBe(1_700_000_000);

        SignedIdentity.TryVerify(other.PublicKeySpki, signed, out _).ShouldBeFalse();
    }

    [Fact]
    public void Ephemeral_keys_agree_on_the_shared_secret()
    {
        using EphemeralKey a = EphemeralKey.Create();
        using EphemeralKey b = EphemeralKey.Create();
        a.DeriveRawSecret(b.PublicKeySpki).ShouldBe(b.DeriveRawSecret(a.PublicKeySpki));
        Should.Throw<HandshakeException>(() => a.DeriveRawSecret(new byte[] { 0x30, 0x00 }));
    }
}
