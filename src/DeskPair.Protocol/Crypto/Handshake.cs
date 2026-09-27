using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using DeskPair.Protocol.Messages;

namespace DeskPair.Protocol.Crypto;

/// <summary>
/// Peer handshake: ControllerHello (plaintext) -> HostHello (plaintext, signed by the host identity)
/// -> both derive directional AES-GCM keys from ephemeral ECDH + HKDF. The signed transcript binds the
/// host identity to this session's ephemeral keys and nonces, so replays and impersonation fail.
/// </summary>
public static class Handshake
{
    private static readonly byte[] TranscriptLabel = "SunlloHS1"u8.ToArray();
    private static readonly byte[] InfoC2H = "sunllo/v1/c2h"u8.ToArray();
    private static readonly byte[] InfoH2C = "sunllo/v1/h2c"u8.ToArray();
    private static readonly byte[] InfoIvC2H = "sunllo/v1/iv/c2h"u8.ToArray();
    private static readonly byte[] InfoIvH2C = "sunllo/v1/iv/h2c"u8.ToArray();
    private static readonly byte[] InfoMediaC2H = "sunllo/v1/media/c2h"u8.ToArray();
    private static readonly byte[] InfoMediaH2C = "sunllo/v1/media/h2c"u8.ToArray();
    private static readonly byte[] InfoMediaIvC2H = "sunllo/v1/media/iv/c2h"u8.ToArray();
    private static readonly byte[] InfoMediaIvH2C = "sunllo/v1/media/iv/h2c"u8.ToArray();

    public sealed class ControllerState : IDisposable
    {
        internal ControllerState(EphemeralKey ephemeral, byte[] nonce)
        {
            Ephemeral = ephemeral;
            Nonce = nonce;
        }

        internal EphemeralKey Ephemeral { get; }

        internal byte[] Nonce { get; }

        public void Dispose() => Ephemeral.Dispose();
    }

    public static (ControllerHello Hello, ControllerState State) BeginController(string version)
    {
        var eph = EphemeralKey.Create();
        byte[] nonce = RandomNumberGenerator.GetBytes(ProtocolConstants.NonceBytes);
        var hello = new ControllerHello
        {
            ProtocolVersion = ProtocolConstants.LegacyHelloVersion,
            MinProtocolVersion = ProtocolConstants.MinProtocolVersion,
            MaxProtocolVersion = ProtocolConstants.ProtocolVersion,
            EphemeralPk = ByteString.CopyFrom(eph.PublicKeySpki),
            Nonce = ByteString.CopyFrom(nonce),
            Version = version,
        };
        return (hello, new ControllerState(eph, nonce));
    }

    /// <summary>
    /// The version a host runs a session at, given what the controller offered: the highest both ranges
    /// contain. A protocol-1 controller sent no range, only <c>protocol_version</c>, and is read as [1, 1].
    /// Null means there is none, and <paramref name="refusal"/> says which side has to update.
    /// </summary>
    public static uint? Negotiate(ControllerHello hello, out string refusal)
    {
        uint theirMin = hello.MinProtocolVersion != 0 ? hello.MinProtocolVersion : hello.ProtocolVersion;
        uint theirMax = hello.MaxProtocolVersion != 0 ? hello.MaxProtocolVersion : hello.ProtocolVersion;
        uint chosen = Math.Min(theirMax, ProtocolConstants.ProtocolVersion);
        uint floor = Math.Max(theirMin, ProtocolConstants.MinProtocolVersion);
        if (theirMin <= theirMax && chosen >= floor)
        {
            refusal = string.Empty;
            return chosen;
        }

        refusal = theirMax < ProtocolConstants.MinProtocolVersion
            ? $"This computer needs DeskPair protocol {ProtocolConstants.MinProtocolVersion} or newer and the app connecting to it speaks up to {theirMax}. Update DeskPair on the device you are connecting from."
            : $"The app connecting speaks DeskPair protocol {theirMin} or newer and this computer speaks up to {ProtocolConstants.ProtocolVersion}. Update DeskPair on this computer.";
        return null;
    }

    /// <summary>Host side: validates the controller hello, produces the signed host hello and this side's session keys.</summary>
    /// <exception cref="HandshakeVersionException">No common version; the host should send <see cref="HandshakeVersionException.Refusal"/> before closing.</exception>
    public static (HostHello Hello, SessionKeys Keys) RespondHost(ControllerHello controllerHello, string hostId, IdentityKey identity, string version)
    {
        uint? negotiated = Negotiate(controllerHello, out string refusal);
        if (negotiated is not { } chosen)
        {
            throw new HandshakeVersionException(refusal, new HostHello
            {
                ProtocolVersion = ProtocolConstants.ProtocolVersion,
                MinProtocolVersion = ProtocolConstants.MinProtocolVersion,
                MaxProtocolVersion = ProtocolConstants.ProtocolVersion,
                Id = hostId,
                Version = version,
                Refusal = refusal,
            });
        }

        if (controllerHello.Nonce.Length != ProtocolConstants.NonceBytes)
        {
            throw new HandshakeException("Controller nonce has the wrong length.");
        }

        using var eph = EphemeralKey.Create();
        byte[] nonceH = RandomNumberGenerator.GetBytes(ProtocolConstants.NonceBytes);
        byte[] transcript = Transcript(hostId, identity.PublicKeySpki, eph.PublicKeySpki, nonceH, controllerHello.EphemeralPk.Span, controllerHello.Nonce.Span);
        byte[] signature = identity.Sign(transcript);
        SessionKeys keys = Derive(eph, controllerHello.EphemeralPk.Span, controllerHello.Nonce.Span, nonceH, isHost: true);

        var hello = new HostHello
        {
            ProtocolVersion = chosen,
            MinProtocolVersion = ProtocolConstants.MinProtocolVersion,
            MaxProtocolVersion = ProtocolConstants.ProtocolVersion,
            Id = hostId,
            IdentityPk = ByteString.CopyFrom(identity.PublicKeySpki),
            EphemeralPk = ByteString.CopyFrom(eph.PublicKeySpki),
            Nonce = ByteString.CopyFrom(nonceH),
            Signature = ByteString.CopyFrom(signature),
            Version = version,
        };
        return (hello, keys);
    }

    /// <summary>Controller side: verifies the host identity and signature, then derives this side's session keys.</summary>
    public static SessionKeys FinishController(HostHello hostHello, ControllerState state, IHostIdentityVerifier verifier)
    {
        if (hostHello.Refusal.Length > 0)
        {
            throw new HandshakeException(hostHello.Refusal);
        }

        if (hostHello.ProtocolVersion < ProtocolConstants.MinProtocolVersion || hostHello.ProtocolVersion > ProtocolConstants.ProtocolVersion)
        {
            // A protocol-1 host answers our hello (it looks like one of its own) with protocol 1, which
            // this side no longer speaks. The person is told which machine to update, not "unsupported".
            throw new HandshakeException(hostHello.ProtocolVersion < ProtocolConstants.MinProtocolVersion
                ? $"This computer's DeskPair speaks protocol {hostHello.ProtocolVersion} and this app needs {ProtocolConstants.MinProtocolVersion} or newer. Update DeskPair on the computer you are connecting to."
                : $"This computer's DeskPair chose protocol {hostHello.ProtocolVersion}, which this app does not speak (up to {ProtocolConstants.ProtocolVersion}). Update DeskPair on this device.");
        }

        if (hostHello.Nonce.Length != ProtocolConstants.NonceBytes)
        {
            throw new HandshakeException("Host nonce has the wrong length.");
        }

        if (hostHello.Id.Length == 0 || hostHello.IdentityPk.IsEmpty)
        {
            throw new HandshakeException("Host hello is missing its identity.");
        }

        verifier.Verify(hostHello.Id, hostHello.IdentityPk.Span);

        byte[] transcript = Transcript(
            hostHello.Id, hostHello.IdentityPk.Span, hostHello.EphemeralPk.Span, hostHello.Nonce.Span,
            state.Ephemeral.PublicKeySpki, state.Nonce);
        if (!IdentityKey.Verify(hostHello.IdentityPk.Span, transcript, hostHello.Signature.Span))
        {
            throw new HandshakeException("Host signature does not verify.");
        }

        return Derive(state.Ephemeral, hostHello.EphemeralPk.Span, state.Nonce, hostHello.Nonce.Span, isHost: false);
    }

    /// <summary>
    /// The bytes the host signs and the controller verifies. Internal rather than private so the conformance
    /// vectors are generated from this exact code: a second implementation is checked against what runs, not
    /// against a description of it.
    /// </summary>
    internal static byte[] Transcript(
        string hostId, ReadOnlySpan<byte> identityPk, ReadOnlySpan<byte> ephH, ReadOnlySpan<byte> nonceH,
        ReadOnlySpan<byte> ephC, ReadOnlySpan<byte> nonceC)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(TranscriptLabel);
        AppendField(sha, Encoding.UTF8.GetBytes(hostId));
        AppendField(sha, identityPk);
        AppendField(sha, ephH);
        AppendField(sha, nonceH);
        AppendField(sha, ephC);
        AppendField(sha, nonceC);
        return sha.GetHashAndReset();
    }

    // Length-prefix every field so variable-length values cannot be shifted between fields.
    private static void AppendField(IncrementalHash sha, ReadOnlySpan<byte> field)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)field.Length);
        sha.AppendData(len);
        sha.AppendData(field);
    }

    internal static SessionKeys Derive(EphemeralKey own, ReadOnlySpan<byte> peerSpki, ReadOnlySpan<byte> nonceC, ReadOnlySpan<byte> nonceH, bool isHost)
    {
        byte[] secret = own.DeriveRawSecret(peerSpki);
        byte[] salt = new byte[nonceC.Length + nonceH.Length];
        nonceC.CopyTo(salt);
        nonceH.CopyTo(salt.AsSpan(nonceC.Length));
        byte[] prk;
        try
        {
            prk = HKDF.Extract(HashAlgorithmName.SHA256, secret, salt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }

        try
        {
            byte[] kC2H = HKDF.Expand(HashAlgorithmName.SHA256, prk, SessionKeys.KeyBytes, InfoC2H);
            byte[] kH2C = HKDF.Expand(HashAlgorithmName.SHA256, prk, SessionKeys.KeyBytes, InfoH2C);
            byte[] ivC2H = HKDF.Expand(HashAlgorithmName.SHA256, prk, SessionKeys.IvPrefixBytes, InfoIvC2H);
            byte[] ivH2C = HKDF.Expand(HashAlgorithmName.SHA256, prk, SessionKeys.IvPrefixBytes, InfoIvH2C);
            // Media keys are independent HKDF outputs (distinct info), so adding them leaves the TCP keys unchanged.
            byte[] mC2H = HKDF.Expand(HashAlgorithmName.SHA256, prk, SessionKeys.KeyBytes, InfoMediaC2H);
            byte[] mH2C = HKDF.Expand(HashAlgorithmName.SHA256, prk, SessionKeys.KeyBytes, InfoMediaH2C);
            byte[] mIvC2H = HKDF.Expand(HashAlgorithmName.SHA256, prk, SessionKeys.IvPrefixBytes, InfoMediaIvC2H);
            byte[] mIvH2C = HKDF.Expand(HashAlgorithmName.SHA256, prk, SessionKeys.IvPrefixBytes, InfoMediaIvH2C);
            MediaKeys media = isHost
                ? new MediaKeys(txKey: mH2C, txIvPrefix: mIvH2C, rxKey: mC2H, rxIvPrefix: mIvC2H)
                : new MediaKeys(txKey: mC2H, txIvPrefix: mIvC2H, rxKey: mH2C, rxIvPrefix: mIvH2C);
            return isHost
                ? new SessionKeys(txKey: kH2C, txIvPrefix: ivH2C, rxKey: kC2H, rxIvPrefix: ivC2H, media)
                : new SessionKeys(txKey: kC2H, txIvPrefix: ivC2H, rxKey: kH2C, rxIvPrefix: ivH2C, media);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(prk);
        }
    }
}
