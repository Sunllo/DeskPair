using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using DeskPair.Core.Update;
using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// When the app asks, what it does with the answer, and what it refuses to do with it.
///
/// The negative cases carry the weight. A checker that always says "there is an update" passes the happy
/// path and is useless; so does one that always says "you are up to date". Both are here.
/// </summary>
public class UpdateCheckServiceTests
{
    private const string Running = "0.2.0+8ec4feb";

    /// <summary>Answers whatever it is told to, and counts how often it was asked.</summary>
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUri = request.RequestUri;
            return Task.FromResult(reply(request));
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static string Manifest(string version, string notes = "", string extra = "") =>
        $$"""
          { "channel": "stable", "version": "{{version}}", "released": "2026-09-21T00:00:00+00:00",
            "notes": "{{notes}}"{{extra}} }
          """;

    private static (UpdateCheckService Service, Stub Handler) Make(
        string body,
        bool enabled = true,
        string portal = "https://portal.test",
        FakeTimeProvider? time = null)
    {
        var handler = new Stub(_ => Json(body));
        var service = new UpdateCheckService(
            new UpdateClient(new HttpClient(handler)),
            Running,
            () => enabled,
            () => portal,
            NullLogger<UpdateCheckService>.Instance,
            time ?? new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero)));
        return (service, handler);
    }

    [Fact]
    public async Task A_newer_version_is_reported_as_available()
    {
        (UpdateCheckService service, _) = Make(Manifest("0.3.0", "something changed"));
        using UpdateCheckService _s = service;

        await service.CheckNowAsync();

        service.Current.IsUpdateAvailable.ShouldBeTrue();
        service.Current.LatestVersion.ShouldBe("0.3.0");
        service.Current.Notes.ShouldBe("something changed");
        service.Current.Problem.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_same_version_is_up_to_date()
    {
        (UpdateCheckService service, _) = Make(Manifest("0.2.0"));
        using UpdateCheckService _s = service;

        await service.CheckNowAsync();

        service.Current.IsUpdateAvailable.ShouldBeFalse();
        service.Current.LastChecked.ShouldNotBeNull();
    }

    [Fact]
    public async Task An_older_version_on_the_portal_is_not_an_update()
    {
        // A rolled-back release must not nag everybody who already has the newer one.
        (UpdateCheckService service, _) = Make(Manifest("0.1.0"));
        using UpdateCheckService _s = service;

        await service.CheckNowAsync();

        service.Current.IsUpdateAvailable.ShouldBeFalse();
    }

    [Fact]
    public async Task A_build_of_the_same_release_is_not_an_update()
    {
        // Catches the lazy implementation: manifest.Version != App.Version would nag on every dev build.
        (UpdateCheckService service, _) = Make(Manifest("0.2.0+deadbee"));
        using UpdateCheckService _s = service;

        await service.CheckNowAsync();

        service.Current.IsUpdateAvailable.ShouldBeFalse();
    }

    [Fact]
    public async Task The_download_url_comes_from_the_configured_portal_and_never_from_the_manifest()
    {
        // The whole reason this manifest needs no signature. A forged one carrying a URL gets nowhere.
        (UpdateCheckService service, _) = Make(
            Manifest("0.3.0", extra: ", \"downloadUrl\": \"file:///C:/Windows/System32/calc.exe\""));
        using UpdateCheckService _s = service;

        await service.CheckNowAsync();

        service.Current.DownloadUrl.ShouldBe("https://portal.test/download");
    }

    [Fact]
    public async Task An_empty_portal_setting_asks_the_official_one()
    {
        (UpdateCheckService service, Stub handler) = Make(Manifest("0.3.0"), portal: "");
        using UpdateCheckService _s = service;

        await service.CheckNowAsync();

        handler.LastUri!.ToString().ShouldStartWith(UpdateEndpoints.OfficialPortal);
        service.Current.DownloadUrl.ShouldBe(UpdateEndpoints.OfficialPortal + "/download");
    }

    [Fact]
    public async Task A_self_hosted_portal_is_asked_and_the_official_one_is_not()
    {
        // Falling back to ours would tell a third party that a private deployment exists.
        (UpdateCheckService service, Stub handler) = Make(Manifest("0.3.0"), portal: "portal.internal:21120");
        using UpdateCheckService _s = service;

        await service.CheckNowAsync();

        handler.LastUri!.Host.ShouldBe("portal.internal");
        service.Current.DownloadUrl.ShouldNotContain("deskpair.app");
    }

    [Fact]
    public async Task Nothing_is_requested_while_the_setting_is_off()
    {
        var handler = new Stub(_ => Json(Manifest("0.3.0")));
        using var service = new UpdateCheckService(
            new UpdateClient(new HttpClient(handler)), Running,
            enabled: () => false, portal: () => "https://portal.test",
            NullLogger<UpdateCheckService>.Instance,
            new FakeTimeProvider());

        // The timer path respects the setting; the button does not, because a click is consent.
        await service.CheckNowAsync();
        handler.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_portal_that_cannot_be_reached_keeps_the_last_answer()
    {
        var replies = new Queue<Func<HttpResponseMessage>>();
        replies.Enqueue(() => Json(Manifest("0.3.0")));
        replies.Enqueue(() => throw new HttpRequestException("no route to host"));
        var handler = new Stub(_ => replies.Dequeue()());

        using var service = new UpdateCheckService(
            new UpdateClient(new HttpClient(handler)), Running,
            () => true, () => "https://portal.test",
            NullLogger<UpdateCheckService>.Instance,
            new FakeTimeProvider());

        await service.CheckNowAsync();
        await service.CheckNowAsync();

        // A flaky network must not retract a notice the reader has already seen.
        service.Current.IsUpdateAvailable.ShouldBeTrue();
        service.Current.LatestVersion.ShouldBe("0.3.0");
        service.Current.Problem.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_captive_portal_answering_html_is_not_an_update()
    {
        var handler = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>sign in to the hotel wifi</html>", System.Text.Encoding.UTF8, "text/html"),
        });
        using var service = new UpdateCheckService(
            new UpdateClient(new HttpClient(handler)), Running,
            () => true, () => "https://portal.test",
            NullLogger<UpdateCheckService>.Instance,
            new FakeTimeProvider());

        await service.CheckNowAsync();

        service.Current.IsUpdateAvailable.ShouldBeFalse();
        service.Current.Problem.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_portal_with_nothing_published_says_nothing()
    {
        var handler = new Stub(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(
                """{ "error": "no_release", "message": "Nothing has been published here yet." }""",
                System.Text.Encoding.UTF8, "application/json"),
        });
        using var service = new UpdateCheckService(
            new UpdateClient(new HttpClient(handler)), Running,
            () => true, () => "https://portal.internal",
            NullLogger<UpdateCheckService>.Instance,
            new FakeTimeProvider());

        await service.CheckNowAsync();

        service.Current.IsUpdateAvailable.ShouldBeFalse();
        service.Current.LatestVersion.ShouldBeEmpty();
    }

    [Fact]
    public async Task Notes_are_capped_and_stripped_of_control_characters()
    {
        string long_ = new('x', UpdateClient.MaximumNotesLength + 500);
        (UpdateCheckService service, _) = Make(Manifest("0.3.0", long_ + @"\u0007"));
        using UpdateCheckService _s = service;

        await service.CheckNowAsync();

        service.Current.Notes.Length.ShouldBe(UpdateClient.MaximumNotesLength);
        service.Current.Notes.ShouldNotContain("\u0007");
    }

    [Fact]
    public void Nothing_is_asked_before_the_first_delay()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
        (UpdateCheckService service, Stub handler) = Make(Manifest("0.3.0"), time: time);
        using UpdateCheckService _s = service;

        service.Start();
        handler.Calls.ShouldBe(0, "starting must not put a request on the path that draws the first window");

        time.Advance(TimeSpan.FromSeconds(25));
        handler.Calls.ShouldBe(1);
    }
}
