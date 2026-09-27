using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using DeskPair.Rendezvous;
using DeskPair.Rendezvous.Core;

namespace DeskPair.Rendezvous.Tests;

/// <summary>
/// Which relay a connection is sent to.
///
/// The first tests this server has ever had, and they start here because this is where the damage was: the
/// old selector returned the first configured entry unconditionally, so a second relay was never used, a
/// relay that had been switched off went on being handed out, and a full one went on taking everything. Each
/// of those looks like a working system from the outside, which is why none of them was noticed.
/// </summary>
public class RelaySelectorTests
{
    private static readonly IPAddress Anywhere = IPAddress.Parse("198.51.100.7");

    /// <summary>Answers a relay's <c>/api/stats</c> from a table, or refuses, without a socket.</summary>
    private sealed class StubRelays : HttpMessageHandler
    {
        private readonly Dictionary<string, string?> _byUrl = new(StringComparer.OrdinalIgnoreCase);

        public void Answer(string statsAddress, int sessions, int maxSessions, long bytesPerSecond = 0, int maxBitrateKbps = 0) =>
            _byUrl[$"http://{statsAddress}/api/stats"] =
                $$"""
                {"activeSessions":{{sessions}},"maxSessions":{{maxSessions}},"bytesPerSecond":{{bytesPerSecond}},"maxBitrateKbps":{{maxBitrateKbps}}}
                """;

        /// <summary>A relay that is configured but will not answer, which is what "switched off" looks like.</summary>
        public void Silent(string statsAddress) => _byUrl[$"http://{statsAddress}/api/stats"] = null;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.ToString();
            if (!_byUrl.TryGetValue(url, out string? body) || body is null)
            {
                throw new HttpRequestException("unreachable");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed record Bench(RelaySelector Selector, RelayHealthMonitor Monitor, FakeTimeProvider Time);

    private static async Task<Bench> StageAsync(StubRelays relays, params RelayEndpoint[] configured)
    {
        var options = new RendezvousOptions
        {
            Relays = [.. configured],
            RelayHealthInterval = TimeSpan.FromSeconds(30),
        };
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var monitor = new RelayHealthMonitor(
            Options.Create(options), time, NullLogger<RelayHealthMonitor>.Instance, new HttpClient(relays));
        await monitor.PollAllAsync(CancellationToken.None);

        return new Bench(
            new RelaySelector(Options.Create(options), monitor, time, NullLogger<RelaySelector>.Instance),
            monitor,
            time);
    }

    private static RelayEndpoint Relay(string name, string region = "") =>
        new() { Address = $"{name}:21117", StatsAddress = $"{name}:21114", Region = region };

    [Fact]
    public async Task With_nothing_configured_it_says_so_rather_than_failing()
    {
        Bench bench = await StageAsync(new StubRelays());

        // "None configured" has to be distinguishable from "all broken", because the caller treats them
        // differently: a punched connection needs no relay and must not be refused for the want of one.
        (RelaySelector.Outcome outcome, IReadOnlyList<string> relays) = bench.Selector.Select(Anywhere);
        outcome.ShouldBe(RelaySelector.Outcome.NoneConfigured);
        relays.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_least_loaded_relay_is_chosen()
    {
        var stub = new StubRelays();
        stub.Answer("busy:21114", sessions: 80, maxSessions: 100);
        stub.Answer("quiet:21114", sessions: 5, maxSessions: 100);
        Bench bench = await StageAsync(stub, Relay("busy"), Relay("quiet"));

        (RelaySelector.Outcome outcome, IReadOnlyList<string> relays) = bench.Selector.Select(Anywhere);

        outcome.ShouldBe(RelaySelector.Outcome.Ok);
        relays[0].ShouldBe("quiet:21117");

        // Both are offered, worst last: the client tries the next when the first does not answer.
        relays.ShouldBe(["quiet:21117", "busy:21117"]);
    }

    [Fact]
    public async Task Bandwidth_counts_even_when_the_session_count_looks_empty()
    {
        var stub = new StubRelays();

        // Ten sessions out of a thousand, but they are dragging 4K windows: 90 MB/s against a 1 Gb/s
        // ceiling. A chooser looking only at sessions would call this the emptiest machine it had.
        stub.Answer("saturated:21114", sessions: 10, maxSessions: 1000, bytesPerSecond: 90_000_000, maxBitrateKbps: 1_000_000);
        stub.Answer("halffull:21114", sessions: 500, maxSessions: 1000, bytesPerSecond: 1_000_000, maxBitrateKbps: 1_000_000);
        Bench bench = await StageAsync(stub, Relay("saturated"), Relay("halffull"));

        bench.Selector.Select(Anywhere).Relays[0].ShouldBe("halffull:21117");
    }

    [Fact]
    public async Task A_relay_that_stops_answering_stops_being_chosen()
    {
        var stub = new StubRelays();
        stub.Answer("alive:21114", sessions: 90, maxSessions: 100);
        stub.Answer("dying:21114", sessions: 1, maxSessions: 100);
        Bench bench = await StageAsync(stub, Relay("alive"), Relay("dying"));

        // The emptier one wins while it is answering.
        bench.Selector.Select(Anywhere).Relays[0].ShouldBe("dying:21117");

        stub.Silent("dying:21114");
        bench.Time.Advance(TimeSpan.FromSeconds(30));
        await bench.Monitor.PollAllAsync(CancellationToken.None);

        // Still inside the grace of two intervals: its last answer is recent enough to trust.
        bench.Selector.Select(Anywhere).Relays.ShouldContain("dying:21117");

        bench.Time.Advance(TimeSpan.FromSeconds(60));
        await bench.Monitor.PollAllAsync(CancellationToken.None);

        // Now it is stale, and the busier machine that is actually there is the better answer.
        bench.Selector.Select(Anywhere).Relays.ShouldBe(["alive:21117"]);
    }

    [Fact]
    public async Task A_relay_that_comes_back_is_used_again()
    {
        var stub = new StubRelays();
        stub.Answer("steady:21114", sessions: 90, maxSessions: 100);
        stub.Silent("restarting:21114");
        Bench bench = await StageAsync(stub, Relay("steady"), Relay("restarting"));

        bench.Selector.Select(Anywhere).Relays.ShouldBe(["steady:21117"]);

        stub.Answer("restarting:21114", sessions: 0, maxSessions: 100);
        bench.Time.Advance(TimeSpan.FromSeconds(30));
        await bench.Monitor.PollAllAsync(CancellationToken.None);

        // A restart must not take a relay out of rotation permanently, which is the other half of dropping
        // one that went away.
        bench.Selector.Select(Anywhere).Relays[0].ShouldBe("restarting:21117");
    }

    [Fact]
    public async Task Every_relay_being_full_refuses_rather_than_overloading_one()
    {
        var stub = new StubRelays();
        stub.Answer("full1:21114", sessions: 100, maxSessions: 100);
        stub.Answer("full2:21114", sessions: 120, maxSessions: 100);
        Bench bench = await StageAsync(stub, Relay("full1"), Relay("full2"));

        // The point of a ceiling. Accepting anyway would saturate the link and collapse the picture quality
        // of every session already running -- invisible from a session count, and it punishes the people
        // already connected for the arrival of one more.
        (RelaySelector.Outcome outcome, IReadOnlyList<string> relays) = bench.Selector.Select(Anywhere);
        outcome.ShouldBe(RelaySelector.Outcome.AllFull);
        relays.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_full_relay_is_not_offered_as_a_fallback()
    {
        var stub = new StubRelays();
        stub.Answer("full:21114", sessions: 100, maxSessions: 100);
        stub.Answer("room:21114", sessions: 10, maxSessions: 100);
        Bench bench = await StageAsync(stub, Relay("full"), Relay("room"));

        // Offering it as second choice would send the client there the moment the first hiccuped, which is
        // the situation the ceiling exists to prevent.
        bench.Selector.Select(Anywhere).Relays.ShouldBe(["room:21117"]);
    }

    [Fact]
    public async Task A_relay_with_no_ceilings_is_never_considered_full()
    {
        var stub = new StubRelays();
        stub.Answer("unmetered:21114", sessions: 9999, maxSessions: 0, bytesPerSecond: 999_000_000, maxBitrateKbps: 0);
        Bench bench = await StageAsync(stub, Relay("unmetered"));

        // Zero means "unmetered", which is only honest on a link nobody is paying for -- but it must not
        // read as zero capacity, or an unconfigured relay would refuse everything.
        bench.Selector.Select(Anywhere).Result.ShouldBe(RelaySelector.Outcome.Ok);
    }

    [Fact]
    public async Task Monitoring_that_never_worked_does_not_refuse_every_connection()
    {
        var stub = new StubRelays();
        stub.Silent("wrongport:21114");
        Bench bench = await StageAsync(stub, Relay("wrongport"));

        // The usual cause is a StatsAddress that does not point at the relay's admin port. An upgrade that
        // silently refused every connection over a monitoring mistake would be worse than the bug this
        // replaced, so a relay nobody has ever reached is used anyway -- loudly.
        (RelaySelector.Outcome outcome, IReadOnlyList<string> relays) = bench.Selector.Select(Anywhere);
        outcome.ShouldBe(RelaySelector.Outcome.Ok);
        relays.ShouldBe(["wrongport:21117"]);
    }

    [Fact]
    public async Task A_relay_that_answered_once_and_died_is_not_covered_by_that_fallback()
    {
        var stub = new StubRelays();
        stub.Answer("known:21114", sessions: 1, maxSessions: 100);
        Bench bench = await StageAsync(stub, Relay("known"));

        stub.Silent("known:21114");
        bench.Time.Advance(TimeSpan.FromMinutes(5));
        await bench.Monitor.PollAllAsync(CancellationToken.None);

        // It has answered before, so silence now means it is down rather than unreachable by us. Falling
        // back to it would undo the whole point of checking.
        bench.Selector.Select(Anywhere).Result.ShouldBe(RelaySelector.Outcome.NoneHealthy);
    }

    [Fact]
    public async Task An_address_the_server_did_not_configure_is_not_accepted_from_a_host()
    {
        var stub = new StubRelays();
        stub.Answer("ours:21114", sessions: 1, maxSessions: 100);
        Bench bench = await StageAsync(stub, Relay("ours"));

        bench.Selector.IsConfigured("ours:21117").ShouldBeTrue();

        // A host behind a symmetric NAT answers with a relay of its own, and that address used to be
        // forwarded to the controller unchecked.
        bench.Selector.IsConfigured("attacker.example:21117").ShouldBeFalse();
        bench.Selector.IsConfigured(string.Empty).ShouldBeFalse();
    }

    [Fact]
    public async Task The_old_string_list_still_configures_a_relay()
    {
        var options = new RendezvousOptions { RelayServers = ["legacy:21117"] };
        IReadOnlyList<RelayEndpoint> configured = RelayHealthMonitor.Configured(options);

        // Configurations written before relays had regions must keep working; they simply have no region
        // and no admin key, so the relay is chosen on health alone.
        configured.ShouldHaveSingleItem().Address.ShouldBe("legacy:21117");
        configured[0].Region.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task An_address_in_both_lists_keeps_its_region()
    {
        var options = new RendezvousOptions
        {
            RelayServers = ["shared:21117", "old-only:21117"],
            Relays = [new RelayEndpoint { Address = "shared:21117", Region = "tw" }],
        };

        IReadOnlyList<RelayEndpoint> configured = RelayHealthMonitor.Configured(options);

        configured.Count.ShouldBe(2);
        configured.Single(r => r.Address == "shared:21117").Region.ShouldBe("tw");
        configured.Single(r => r.Address == "old-only:21117").Region.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task An_unroutable_address_falls_through_to_load_alone()
    {
        var stub = new StubRelays();
        stub.Answer("tw:21114", sessions: 90, maxSessions: 100);
        stub.Answer("jp:21114", sessions: 1, maxSessions: 100);
        Bench bench = await StageAsync(stub, Relay("tw", region: "tw"), Relay("jp", region: "jp"));

        // A loopback address belongs to no country. Region matching is best effort by design: an address
        // the table does not cover must not mean "no relay", it means "choose on load".
        (RelaySelector.Outcome outcome, IReadOnlyList<string> relays) =
            bench.Selector.Select(new IPAddress([127, 0, 0, 1]));

        outcome.ShouldBe(RelaySelector.Outcome.Ok);
        relays[0].ShouldBe("jp:21117");
    }

    [Fact]
    public async Task A_null_address_is_allowed_and_chooses_on_load()
    {
        var stub = new StubRelays();
        stub.Answer("only:21114", sessions: 1, maxSessions: 100);
        Bench bench = await StageAsync(stub, Relay("only", region: "tw"));

        bench.Selector.Select(null).Result.ShouldBe(RelaySelector.Outcome.Ok);
    }

    [Fact]
    public void An_address_family_the_table_cannot_hold_is_not_a_crash()
    {
        // Whatever comes off a socket is what this gets. Returning "unknown" is the only useful answer.
        Server.Shared.GeoCountry.Lookup(IPAddress.IPv6Loopback).ShouldBeNull();
        Server.Shared.GeoCountry.Lookup(new IPAddress(0L)).ShouldBeNull();
        Should.NotThrow(() => Server.Shared.GeoCountry.Lookup(IPAddress.Parse("::ffff:8.8.8.8")));
    }

    [Fact]
    public void The_stub_handler_is_not_hiding_a_real_socket()
    {
        // Guards the tests above: if StubRelays ever stopped intercepting, they would quietly start
        // testing whatever happened to be listening on the machine running them.
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Should.Throw<SocketException>(() => probe.Connect("quiet", 21114));
    }
}
