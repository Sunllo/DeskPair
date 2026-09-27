using System.Net;
using System.Text;
using System.Text.Json;
using DeskPair.Core.Config;
using DeskPair.Core.Portal;
using DeskPair.Platform.Abstractions.Security;

namespace DeskPair.Core.Tests;

/// <summary>
/// Signing in with an email address and a password.
///
/// The app used to ask for a linking code, which meant opening the website, signing in there and copying
/// eight characters across. The code is still the mechanism -- the portal checks a device's signature over
/// it in exactly one place, and that has not moved -- but the app now buys the code with the password
/// instead of asking somebody to fetch it.
///
/// What that adds is a password crossing the wire, so these tests are as much about where it does not go
/// as about whether signing in works: it is sent to sign in, and to nothing else.
/// </summary>
public class AccountSignInTests
{
    private const string Portal = "https://portal.test";

    private const string Password = "correct-horse-battery-staple";

    /// <summary>A portal, as far as the client can tell: answers by path, and remembers what it was asked.</summary>
    private sealed class FakePortal : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _answers = new(StringComparer.Ordinal);

        public List<(string Path, string Body)> Calls { get; } = [];

        public FakePortal Answer(string path, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _answers[path] = (status, body);
            return this;
        }

        public HttpClient Client() => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath;
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((path, body));

            (HttpStatusCode status, string answer) = _answers.TryGetValue(path, out (HttpStatusCode, string) found)
                ? found
                : (HttpStatusCode.NotFound, "{\"error\":\"no_route\",\"message\":\"Nothing here.\"}");

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(answer, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class NoMachineId : IMachineIdProvider
    {
        public byte[] GetStableMachineId() => [];
    }

    private sealed class Memory : ISecretStore
    {
        public Dictionary<string, byte[]> Items { get; } = new(StringComparer.Ordinal);

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default) =>
            ValueTask.FromResult(Items.TryGetValue(key, out byte[]? value) ? value : null);

        public ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct = default)
        {
            Items[key] = value.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(string key, CancellationToken ct = default)
        {
            Items.Remove(key);
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<(AccountLink Link, Memory Secrets)> CreateAsync(FakePortal portal)
    {
        var secrets = new Memory();
        PeerIdentityStore identity = await PeerIdentityStore.LoadOrCreateAsync(secrets, new NoMachineId());
        return (new AccountLink(identity, secrets, portal.Client()), secrets);
    }

    private static FakePortal WithCode(string code) => new FakePortal()
        .Answer(
            "/api/v1/account/signin",
            "{\"code\":\"" + code + "\",\"expires\":\"2026-01-01T00:00:00+00:00\",\"verify\":false}")
        .Answer(
            "/api/v1/device/link",
            "{\"deviceId\":\"dev-1\",\"token\":\"a-device-token\",\"expires\":\"2027-01-01T00:00:00+00:00\"," +
            "\"account\":\"reader@example.test\",\"alias\":\"Front desk\"}");

    [Fact]
    public async Task An_email_and_a_password_link_this_device()
    {
        FakePortal portal = WithCode("ABCD-EFGH");
        (AccountLink link, Memory secrets) = await CreateAsync(portal);

        AccountSignIn result = await link.SignInAsync(Portal, "reader@example.test", Password, "Front desk");

        result.NeedsVerification.ShouldBeFalse();
        result.State.IsLinked.ShouldBeTrue();
        result.State.Account.ShouldBe("reader@example.test");

        // Two calls, in that order: the password buys a code, the code is spent with a signature.
        portal.Calls.Select(c => c.Path).ShouldBe(["/api/v1/account/signin", "/api/v1/device/link"]);

        // And the device token is what is kept, which is the whole point: the password is not.
        secrets.Items.ShouldContainKey("portal-link");
        Encoding.UTF8.GetString(secrets.Items["portal-link"]).ShouldContain("a-device-token");
    }

    /// <summary>
    /// The password goes to the sign-in call and nowhere else.
    ///
    /// Worth pinning rather than reading off the code: the second call is built from the first's answer,
    /// and the obvious way to write that -- pass the whole request along -- would carry the password into
    /// a second request body without anybody meaning it to.
    /// </summary>
    [Fact]
    public async Task The_password_is_sent_once_and_nowhere_else()
    {
        FakePortal portal = WithCode("ABCD-EFGH");
        (AccountLink link, _) = await CreateAsync(portal);

        await link.SignInAsync(Portal, "reader@example.test", Password, "Front desk");

        portal.Calls.Count(c => c.Body.Contains(Password, StringComparison.Ordinal)).ShouldBe(1);
        portal.Calls.Single(c => c.Path == "/api/v1/device/link").Body.ShouldNotContain(Password);
    }

    /// <summary>
    /// A new account that has to confirm its address is a success with nothing to spend.
    ///
    /// The portal answers a registration with verify set and no code. Reporting that as a failure would be
    /// a lie -- the account exists -- and the obvious wrong thing after it is to go on and call the link
    /// endpoint with an empty code, which comes back as "that code is not valid any more".
    /// </summary>
    [Fact]
    public async Task An_account_awaiting_confirmation_is_not_a_failure_and_does_not_try_to_link()
    {
        FakePortal portal = new FakePortal().Answer(
            "/api/v1/account/register",
            "{\"code\":\"\",\"expires\":\"0001-01-01T00:00:00+00:00\",\"verify\":true}");
        (AccountLink link, Memory secrets) = await CreateAsync(portal);

        AccountSignIn result = await link.RegisterAsync(Portal, "new@example.test", Password, "zh-TW", "Front desk");

        result.NeedsVerification.ShouldBeTrue();
        result.State.IsLinked.ShouldBeFalse();
        portal.Calls.Select(c => c.Path).ShouldBe(["/api/v1/account/register"]);
        secrets.Items.ShouldNotContainKey("portal-link");
    }

    /// <summary>The portal's own sentence reaches the caller, because that is what the window shows.</summary>
    [Fact]
    public async Task A_wrong_password_is_reported_in_the_portals_own_words()
    {
        FakePortal portal = new FakePortal().Answer(
            "/api/v1/account/signin",
            "{\"error\":\"bad_credentials\",\"message\":\"That email address and password do not match.\"}",
            HttpStatusCode.Unauthorized);
        (AccountLink link, _) = await CreateAsync(portal);

        PortalException failure = await Should.ThrowAsync<PortalException>(
            () => link.SignInAsync(Portal, "reader@example.test", "wrong", "Front desk"));

        failure.Code.ShouldBe("bad_credentials");
        failure.Message.ShouldBe("That email address and password do not match.");
    }

    /// <summary>
    /// An install with nothing configured signs in to the official portal.
    ///
    /// Linking used to refuse an empty address with "No portal address is set", which was right while the
    /// address was a field on the sign-in screen and is wrong now that it is a setting almost nobody
    /// touches.
    /// </summary>
    [Fact]
    public void No_configured_portal_means_the_official_one()
    {
        Update.UpdateEndpoints.PortalFor(string.Empty).ShouldBe(Update.UpdateEndpoints.OfficialPortal);
        Update.UpdateEndpoints.PortalFor("portal.example:21120").ShouldBe("http://portal.example:21120");
    }

    /// <summary>The device is registered under the name it was given, not whatever the OS calls the machine.</summary>
    [Fact]
    public async Task The_device_is_registered_under_the_name_it_was_given()
    {
        FakePortal portal = WithCode("ABCD-EFGH");
        (AccountLink link, _) = await CreateAsync(portal);

        await link.SignInAsync(Portal, "reader@example.test", Password, "Front desk");

        using JsonDocument body = JsonDocument.Parse(portal.Calls.Single(c => c.Path == "/api/v1/device/link").Body);
        body.RootElement.GetProperty("alias").GetString().ShouldBe("Front desk");

        // Normalised on the way out, hyphen and all: the code is grouped for reading, and the signature is
        // over the normalised form, so sending it as written would fail as a bad signature.
        body.RootElement.GetProperty("code").GetString().ShouldBe("ABCDEFGH");
    }
}
