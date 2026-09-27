using Google.Protobuf;
using DeskPair.Protocol.Crypto;
using DeskPair.Protocol.Messages;

namespace DeskPair.Protocol.Tests;

public class HandshakeTests
{
    [Fact]
    public void Both_sides_derive_matching_directional_keys()
    {
        using IdentityKey hostIdentity = IdentityKey.Create();
        (ControllerHello ch, Handshake.ControllerState state) = Handshake.BeginController("test");
        using (state)
        {
            (HostHello hh, SessionKeys hostKeys) = Handshake.RespondHost(ch, "123456789", hostIdentity, "test");
            using (hostKeys)
            using (SessionKeys controllerKeys = Handshake.FinishController(hh, state, AcceptAnyIdentityVerifier.Instance))
            {
                controllerKeys.IsCounterpartOf(hostKeys).ShouldBeTrue();
                controllerKeys.TxKey.SequenceEqual(controllerKeys.RxKey).ShouldBeFalse();
            }
        }
    }

    [Fact]
    public void Two_handshakes_never_share_keys()
    {
        using IdentityKey hostIdentity = IdentityKey.Create();
        (ControllerHello ch1, Handshake.ControllerState s1) = Handshake.BeginController("t");
        (ControllerHello ch2, Handshake.ControllerState s2) = Handshake.BeginController("t");
        using (s1)
        using (s2)
        {
            (HostHello hh1, SessionKeys k1) = Handshake.RespondHost(ch1, "id", hostIdentity, "t");
            (HostHello hh2, SessionKeys k2) = Handshake.RespondHost(ch2, "id", hostIdentity, "t");
            using (k1)
            using (k2)
            {
                k1.TxKey.SequenceEqual(k2.TxKey).ShouldBeFalse();
                hh1.Signature.ShouldNotBe(hh2.Signature);
            }
        }
    }

    [Fact]
    public void Tampered_host_signature_is_rejected()
    {
        using IdentityKey hostIdentity = IdentityKey.Create();
        (ControllerHello ch, Handshake.ControllerState state) = Handshake.BeginController("t");
        using (state)
        {
            (HostHello hh, SessionKeys hostKeys) = Handshake.RespondHost(ch, "id", hostIdentity, "t");
            hostKeys.Dispose();
            byte[] sig = hh.Signature.ToByteArray();
            sig[10] ^= 0xFF;
            hh.Signature = ByteString.CopyFrom(sig);
            Should.Throw<HandshakeException>(() => Handshake.FinishController(hh, state, AcceptAnyIdentityVerifier.Instance));
        }
    }

    [Fact]
    public void Host_hello_replayed_into_another_session_is_rejected()
    {
        using IdentityKey hostIdentity = IdentityKey.Create();
        (ControllerHello ch1, Handshake.ControllerState s1) = Handshake.BeginController("t");
        (_, Handshake.ControllerState s2) = Handshake.BeginController("t");
        using (s1)
        using (s2)
        {
            (HostHello hh, SessionKeys k) = Handshake.RespondHost(ch1, "id", hostIdentity, "t");
            k.Dispose();
            Should.Throw<HandshakeException>(() => Handshake.FinishController(hh, s2, AcceptAnyIdentityVerifier.Instance));
        }
    }

    [Fact]
    public void Impersonating_host_with_a_different_identity_key_is_rejected_by_signed_identity()
    {
        using IdentityKey serverKey = IdentityKey.Create();
        using IdentityKey realHost = IdentityKey.Create();
        using IdentityKey attacker = IdentityKey.Create();
        var signed = SignedIdentity.Sign(serverKey, "id", realHost.PublicKeySpki, DateTimeOffset.UnixEpoch);
        var verifier = new ServerSignedIdentityVerifier(serverKey.PublicKeySpki, "id", signed);

        (ControllerHello ch, Handshake.ControllerState state) = Handshake.BeginController("t");
        using (state)
        {
            (HostHello hh, SessionKeys k) = Handshake.RespondHost(ch, "id", attacker, "t");
            k.Dispose();
            Should.Throw<HandshakeException>(() => Handshake.FinishController(hh, state, verifier));

            (HostHello ok, SessionKeys k2) = Handshake.RespondHost(ch, "id", realHost, "t");
            k2.Dispose();
            using SessionKeys keys = Handshake.FinishController(ok, state, verifier);
            keys.ShouldNotBeNull();
        }
    }

    [Fact]
    public void Version_mismatch_is_rejected()
    {
        using IdentityKey hostIdentity = IdentityKey.Create();
        (ControllerHello ch, Handshake.ControllerState state) = Handshake.BeginController("t");
        using (state)
        {
            // A range this host does not reach. The legacy field alone no longer decides anything once a
            // range is present, so both ends of the range move.
            ch.MinProtocolVersion = 99;
            ch.MaxProtocolVersion = 99;
            Should.Throw<HandshakeVersionException>(() => Handshake.RespondHost(ch, "id", hostIdentity, "t"))
                .Message.ShouldContain("Update DeskPair on this computer");
        }
    }
}
