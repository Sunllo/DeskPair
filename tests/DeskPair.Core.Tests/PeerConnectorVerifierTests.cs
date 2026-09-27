using DeskPair.Core.Transport;
using DeskPair.Protocol;
using DeskPair.Protocol.Crypto;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Core.Tests;

/// <summary>
/// A host reached by id is verified against the rendezvous server's signature or not connected to at all.
///
/// The fallback this replaces accepted any key when no server key was configured. The signalling channel
/// is plaintext, so that was a man-in-the-middle with a log line, on the first run of every install that
/// had not fetched the key yet.
/// </summary>
public class PeerConnectorVerifierTests
{
    [Fact]
    public void Without_a_server_key_the_connection_is_refused_and_the_message_says_where_to_get_one()
    {
        var identity = new SignedPeerIdentity();

        PeerConnectException refused = Should.Throw<PeerConnectException>(() => PeerConnector.VerifierFor(null, "123456789", identity));

        refused.Message.ShouldContain("public key is not configured");
        refused.Message.ShouldContain("Settings");
    }

    [Fact]
    public void With_a_server_key_the_host_is_verified_against_its_signature()
    {
        using IdentityKey server = IdentityKey.Create();
        using IdentityKey host = IdentityKey.Create();
        SignedPeerIdentity identity = SignedIdentity.Sign(server, "123456789", host.PublicKeySpki, DateTimeOffset.UtcNow);

        IHostIdentityVerifier verifier = PeerConnector.VerifierFor(server.PublicKeySpki, "123456789", identity);

        verifier.ShouldBeOfType<ServerSignedIdentityVerifier>();
        Should.NotThrow(() => verifier.Verify("123456789", host.PublicKeySpki));

        using IdentityKey impostor = IdentityKey.Create();
        Should.Throw<HandshakeException>(() => verifier.Verify("123456789", impostor.PublicKeySpki));
    }

    [Fact]
    public void A_server_that_sends_no_identity_is_not_worked_around()
    {
        using IdentityKey server = IdentityKey.Create();

        Should.Throw<PeerConnectException>(() => PeerConnector.VerifierFor(server.PublicKeySpki, "123456789", null))
            .Message.ShouldContain("signed host identity");
    }
}
