using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using DeskPair.Rendezvous.Core;
using DeskPair.Rendezvous.Persistence;

namespace DeskPair.Rendezvous.Tests;

/// <summary>
/// What this server posts once a minute, and how it goes out.
/// </summary>
public class StatsReporterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Answers a relay's stats poll from a string, and keeps whatever the reporter posts.</summary>
    private sealed class Wire : HttpMessageHandler
    {
        public string RelayStats { get; set; } =
            """{"activeSessions":3,"maxSessions":100,"bytesPerSecond":2500,"maxBitrateKbps":0,"totalSessions":41,"totalBytes":900000}""";

        public HttpStatusCode Answer { get; set; } = HttpStatusCode.NoContent;

        public List<(Uri Url, string? Authorization, string Body)> Posted { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RelayStats, Encoding.UTF8, "application/json") };
            }

            Posted.Add((request.RequestUri!, request.Headers.Authorization?.ToString(), await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(Answer);
        }
    }

    private sealed record Bench(StatsReporter Reporter, PeerTable Peers, RendezvousStats Stats, RelayHealthMonitor Relays, FakeTimeProvider Time);

    private static async Task<Bench> StageAsync(Wire wire, string url = "https://dashboard.test/report")
    {
        var time = new FakeTimeProvider(T0);
        var options = Options.Create(new RendezvousOptions
        {
            Relays = [new RelayEndpoint { Address = "relay.test:21117", StatsAddress = "relay.test:21124", Region = "tw" }],
            Report = new StatsReportOptions { Url = url, ApiKey = "the-report-key" },
        });
        var peers = new PeerTable(new NullPeerStore(), time, NullLogger<PeerTable>.Instance);
        var stats = new RendezvousStats();
        var relays = new RelayHealthMonitor(options, time, NullLogger<RelayHealthMonitor>.Instance, new HttpClient(wire));
        await relays.PollAllAsync(CancellationToken.None);
        var reporter = new StatsReporter(options, peers, stats, relays, time, NullLogger<StatsReporter>.Instance, new HttpClient(wire));
        return new Bench(reporter, peers, stats, relays, time);
    }

    private static void Online(PeerTable peers, string id, string version, DateTimeOffset seen) =>
        peers.TryAdd(new PeerEntry(id, Guid.NewGuid().ToByteArray(), [1], T0, seen, new IPEndPoint(IPAddress.Loopback, 5000), version, null, default))
            .ShouldBeTrue();

    [Fact]
    public async Task A_report_carries_counts_versions_and_relays_and_no_ids()
    {
        var wire = new Wire();
        Bench bench = await StageAsync(wire);
        Online(bench.Peers, "100000001", "0.4.1+9cf1691", T0);
        Online(bench.Peers, "100000002", "0.4.1+7bd1fe9", T0);
        Online(bench.Peers, "100000003", "0.4.0", T0);
        Online(bench.Peers, "100000004", "0.4.0", T0.AddHours(-2));
        bench.Stats.PunchRequest();
        bench.Stats.PunchRequest();
        bench.Stats.RelayBrokered();

        StatsReport report = bench.Reporter.Build();

        report.PeersTotal.ShouldBe(4);
        report.PeersOnline.ShouldBe(3);
        report.Connections.ShouldBe(2);
        report.Relayed.ShouldBe(1);
        report.StartedUtc.ShouldBe(T0);

        // By release, not by build: the two 0.4.1 commits are one bar.
        report.Versions.Select(v => (v.Version, v.Online)).ShouldBe([("0.4.1", 2), ("0.4.0", 1)]);

        StatsReportRelay relay = report.Relays.ShouldHaveSingleItem();
        relay.Address.ShouldBe("relay.test:21117");
        relay.Region.ShouldBe("tw");
        relay.Healthy.ShouldBeTrue();
        relay.TotalSessions.ShouldBe(41);
        relay.TotalBytes.ShouldBe(900_000);

        string json = JsonSerializer.Serialize(report, ReportJson.Default.StatsReport);
        json.ShouldNotContain("100000001", Case.Sensitive, "a report is counts; no peer's ID is in it");
    }

    [Fact]
    public async Task A_report_is_posted_with_its_key()
    {
        var wire = new Wire();
        Bench bench = await StageAsync(wire);

        (await bench.Reporter.SendAsync(CancellationToken.None)).ShouldBeTrue();

        (Uri url, string? authorization, string body) = wire.Posted.ShouldHaveSingleItem();
        url.ShouldBe(new Uri("https://dashboard.test/report"));
        authorization.ShouldBe("Bearer the-report-key");
        body.ShouldContain("\"peersOnline\"");
    }

    [Fact]
    public async Task A_receiver_that_refuses_is_a_failed_report_not_an_exception()
    {
        var wire = new Wire { Answer = HttpStatusCode.Unauthorized };
        Bench bench = await StageAsync(wire);
        (await bench.Reporter.SendAsync(CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task With_no_address_nothing_is_ever_sent()
    {
        var wire = new Wire();
        Bench bench = await StageAsync(wire, url: string.Empty);

        await bench.Reporter.StartAsync(CancellationToken.None);
        bench.Time.Advance(TimeSpan.FromMinutes(5));
        await bench.Reporter.StopAsync(CancellationToken.None);

        wire.Posted.ShouldBeEmpty();
    }

    [Fact]
    public void Versions_past_the_most_share_one_line()
    {
        var time = new FakeTimeProvider(T0);
        var peers = new PeerTable(new NullPeerStore(), time, NullLogger<PeerTable>.Instance);
        for (int i = 0; i < 15; i++)
        {
            for (int n = 0; n <= i; n++)
            {
                Online(peers, $"{i:D3}{n:D6}", $"0.{i}.0", T0);
            }
        }

        IReadOnlyList<(string Version, int Online)> versions = peers.OnlineVersions(TimeSpan.FromMinutes(1), most: 12);

        versions.Count.ShouldBe(12);
        versions[0].ShouldBe(("0.14.0", 15));
        versions[^1].Version.ShouldBe("other");
        versions.Sum(v => v.Online).ShouldBe(Enumerable.Range(1, 15).Sum(), "folding loses no host");
    }
}
