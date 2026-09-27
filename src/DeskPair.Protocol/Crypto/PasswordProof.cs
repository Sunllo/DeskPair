using System.Security.Cryptography;
using System.Text;
using DeskPair.Protocol.Messages;

namespace DeskPair.Protocol.Crypto;

/// <summary>
/// Two-stage password proof. The host stores h1, derived from the password and a per-install salt; the
/// controller sends proof = SHA256(h1 || challenge) for a per-connection challenge.
///
/// How h1 is derived is the host's choice and is announced in the challenge (<see cref="AuthChallenge.Kdf"/>).
/// Protocol 1 knew only <see cref="PasswordKdf.KdfSha256"/>, one hash, which makes a stolen h1 a password
/// guessable at hardware speed. Protocol 2 hosts derive new passwords with PBKDF2-HMAC-SHA256, and keep
/// verifying a permanent password stored the old way until it is set again, since the host has no
/// plaintext to re-derive from.
/// </summary>
public static class PasswordProof
{
    public const int HashBytes = 32;

    /// <summary>What a protocol-2 host uses for a password it derives today.</summary>
    public const PasswordKdf DefaultKdf = PasswordKdf.KdfPbkdf2Sha256;

    /// <summary>
    /// Enough to make an offline guess cost tens of milliseconds and a login on a phone unnoticeable.
    /// The host does this work once per password it holds; the controller once per login.
    /// </summary>
    public const int DefaultIterations = 100_000;

    /// <summary>
    /// The most a controller will do on a host's say-so. A challenge is not authenticated by anything the
    /// controller trusts yet, so a host (or whoever stands in for one) must not be able to ask for a
    /// billion rounds and pin the phone's CPU.
    /// </summary>
    public const int MaxIterations = 1_000_000;

    public const int MinPbkdf2Iterations = 1_000;

    public static byte[] ComputeH1(string password, ReadOnlySpan<byte> salt, PasswordKdf kdf, int iterations)
    {
        byte[] pw = Encoding.UTF8.GetBytes(password);
        try
        {
            switch (kdf)
            {
                case PasswordKdf.KdfSha256:
                {
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    sha.AppendData(pw);
                    sha.AppendData(salt);
                    return sha.GetHashAndReset();
                }

                case PasswordKdf.KdfPbkdf2Sha256:
                    if (iterations is < MinPbkdf2Iterations or > MaxIterations)
                    {
                        throw new ArgumentOutOfRangeException(nameof(iterations), iterations, $"PBKDF2 iterations must be between {MinPbkdf2Iterations} and {MaxIterations}.");
                    }

                    return Rfc2898DeriveBytes.Pbkdf2(pw, salt, iterations, HashAlgorithmName.SHA256, HashBytes);

                default:
                    throw new ArgumentOutOfRangeException(nameof(kdf), kdf, "Unknown password KDF.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pw);
        }
    }

    /// <summary>The protocol-1 derivation, for callers that know that is what they hold.</summary>
    public static byte[] ComputeH1(string password, ReadOnlySpan<byte> salt) => ComputeH1(password, salt, PasswordKdf.KdfSha256, 0);

    public static byte[] ComputeProof(ReadOnlySpan<byte> h1, ReadOnlySpan<byte> challenge)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(h1);
        sha.AppendData(challenge);
        return sha.GetHashAndReset();
    }

    public static byte[] ComputeProof(string password, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> challenge, PasswordKdf kdf, int iterations)
    {
        byte[] h1 = ComputeH1(password, salt, kdf, iterations);
        try
        {
            return ComputeProof(h1, challenge);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(h1);
        }
    }

    /// <summary>Constant-time check. Always computes the expected proof so timing does not depend on the stored value.</summary>
    public static bool Verify(ReadOnlySpan<byte> h1, ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> proof)
    {
        byte[] expected = ComputeProof(h1, challenge);
        try
        {
            return proof.Length == HashBytes && CryptographicOperations.FixedTimeEquals(expected, proof);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(ProtocolConstants.SaltBytes);

    public static byte[] NewChallenge() => RandomNumberGenerator.GetBytes(ProtocolConstants.ChallengeBytes);
}
