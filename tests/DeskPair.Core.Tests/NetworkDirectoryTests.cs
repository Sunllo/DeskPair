using System.Net;
using System.Text;
using DeskPair.Core.Config;
using DeskPair.Core.Portal;

namespace DeskPair.Core.Tests;

/// <summary>
/// Asking the portal where to signal.
///
/// The directory exists so a server can move without every installation needing an update. It is not a
/// secret and not a gate -- a running app connects to the address, so anyone who wants it has it -- and
/// these tests are about the two things that actually matter: a portal that answers badly must not be able
/// to steer this app, and an address somebody typed must never be overruled by one that was fetched.
/// </summary>
public class NetworkDirectoryTests
{
    private sealed class Canned(HttpStatusCode code, string body) : HttpMessageHandler
    {
        public Uri? Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked = request.RequestUri;
            request.Headers.Authorization.ShouldBeNull("an install with no account still has to be able to connect");
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static async Task<NetworkDirectory> AnswerAsync(string body, HttpStatusCode code = HttpStatusCode.OK)
    {
        using var handler = new Canned(code, body);
        using var client = new NetworkDirectoryClient(handler);
        return await client.FetchAsync("https://portal.test");
    }

    [Fact]
    public async Task A_portal_that_names_a_server_is_believed()
    {
        using var handler = new Canned(HttpStatusCode.OK, """{"rendezvous":"rdv.example.com:21116","publicKey":"AAAA"}""");
        using var client = new NetworkDirectoryClient(handler);

        NetworkDirectory directory = await client.FetchAsync("https://portal.test");

        directory.Rendezvous.ShouldBe("rdv.example.com:21116");
        directory.PublicKey.ShouldBe("AAAA");
        handler.Asked!.AbsoluteUri.ShouldBe("https://portal.test/api/v1/network");
    }

    [Theory]
    [InlineData("""{"rendezvous":"rdv.example.com\nevil.example.com","publicKey":""}""", "a newline in a host")]
    [InlineData("""{"rendezvous":"rdv example.com","publicKey":""}""", "a space in a host")]
    [InlineData("""{"rendezvous":"","publicKey":"AAAA"}""", "no host at all")]
    [InlineData("""{"rendezvous":"rdv.example.com","publicKey":"not base64!!"}""", "a key that is not a key")]
    [InlineData("not json", "a body that is not JSON")]
    [InlineData("""{"rendezvous":null,"publicKey":null}""", "nulls")]
    public async Task A_portal_that_answers_nonsense_is_treated_as_one_that_answered_nothing(string body, string why)
    {
        // The address goes into a socket and the key into a signature check. Neither is a place to discover
        // that a string was not what it claimed to be.
        (await AnswerAsync(body)).IsEmpty.ShouldBeTrue(why);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_portal_that_publishes_no_directory_is_not_an_error(HttpStatusCode code)
    {
        // Direct connections only, which is the state this ran in before there was a directory. The host
        // still starts, still has an id, and still works on a local network.
        (await AnswerAsync("""{"rendezvous":"rdv.example.com"}""", code)).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task An_unreachable_portal_does_not_throw()
    {
        // This runs on the path that opens the window.
        using var client = new NetworkDirectoryClient(timeout: TimeSpan.FromMilliseconds(200));

        (await client.FetchAsync("http://127.0.0.1:1")).IsEmpty.ShouldBeTrue();
    }

    // ---- what wins ----

    [Fact]
    public void An_address_somebody_typed_beats_the_one_the_portal_named()
    {
        // Somebody who typed an address meant it. Quietly preferring a fetched one would move a
        // self-hosted installation onto our servers without telling anybody.
        var config = new HostConfig { RendezvousServer = "mine.example.com", ServerPublicKeyBase64 = "TUlORQ==" };

        PeerSettings settings = config.ToPeerSettings("0.2.0", "rdv.example.com:21116", "T1VSUw==");

        settings.RendezvousServer.ShouldBe("mine.example.com");
        settings.ServerPublicKeyBase64.ShouldBe("TUlORQ==");
    }

    [Fact]
    public void With_nothing_configured_the_directory_is_used()
    {
        var config = new HostConfig();

        PeerSettings settings = config.ToPeerSettings("0.2.0", "rdv.example.com:21116", "T1VSUw==");

        settings.RendezvousServer.ShouldBe("rdv.example.com:21116");
        settings.ServerPublicKeyBase64.ShouldBe("T1VSUw==");
    }

    [Fact]
    public void With_nothing_configured_and_no_directory_there_is_no_server()
    {
        PeerSettings settings = new HostConfig().ToPeerSettings("0.2.0");

        settings.RendezvousServer.ShouldBeEmpty();
    }

    [Fact]
    public void The_directory_is_on_by_default_and_survives_a_round_trip()
    {
        // The flag decides whether this installation phones the portal at all, so a serialisation bug here
        // is an installation that silently stops connecting, or one that starts.
        new HostConfig().UseDirectoryServers.ShouldBeTrue();

        var selfHosted = new HostConfig { UseDirectoryServers = false, RendezvousServer = "mine.example.com" };
        HostConfig back = HostConfig.FromJson(selfHosted.ToJson()) ?? throw new InvalidOperationException("did not parse");

        back.UseDirectoryServers.ShouldBeFalse();
        back.RendezvousServer.ShouldBe("mine.example.com");
    }
}
