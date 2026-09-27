using Microsoft.Extensions.Options;
using DeskPair.Protocol;

namespace DeskPair.Rendezvous.Core;

/// <summary>What a relay said about itself the last time it was asked.</summary>
public sealed record RelayHealth
{
    public required RelayEndpoint Endpoint { get; init; }

    /// <summary>When the last successful poll came back. Default means it has never answered.</summary>
    public DateTimeOffset LastSeenUtc { get; init; }

    public int ActiveSessions { get; init; }

    public int MaxSessions { get; init; }

    public long BytesPerSecond { get; init; }

    public int MaxBitrateKbps { get; init; }

    /// <summary>Sessions the relay has paired since it started: its own running total, passed on in this server's reports.</summary>
    public long TotalSessions { get; init; }

    /// <summary>Bytes the relay has carried since it started, likewise.</summary>
    public long TotalBytes { get; init; }

    /// <summary>
    /// Whether this relay answered recently enough to send somebody to.
    ///
    /// <paramref name="assumeUnpolled"/> is true until the first round of polling has finished. A relay
    /// nobody has asked yet is not a relay known to be broken, and refusing every connection for the first
    /// moments after a restart would be a worse answer than the one this replaced.
    /// </summary>
    public bool IsHealthy(DateTimeOffset now, TimeSpan interval, bool assumeUnpolled = false) =>
        LastSeenUtc == default ? assumeUnpolled : now - LastSeenUtc <= interval * 2;

    /// <summary>
    /// How full this relay is, as the larger of its two ceilings.
    ///
    /// Sessions and bandwidth both matter and neither implies the other: a thousand idle sessions cost
    /// almost nothing, and ten people dragging 4K windows will saturate a link the session count calls
    /// empty. A ceiling left at zero is "unmetered" and contributes nothing to the score.
    /// </summary>
    public double Load
    {
        get
        {
            double bySessions = MaxSessions > 0 ? (double)ActiveSessions / MaxSessions : 0;
            double byBitrate = MaxBitrateKbps > 0 ? BytesPerSecond * 8.0 / 1000 / MaxBitrateKbps : 0;
            return Math.Max(bySessions, byBitrate);
        }
    }
}

/// <summary>
/// Asks every configured relay how it is doing, on a timer.
///
/// <c>RendezvousOptions.RelayHealthInterval</c> has existed since the beginning with no reader at all, and
/// <c>docs/architecture.md</c> described this behaviour as though it were implemented. Until now
/// <c>RelaySelector</c> returned the first configured entry whatever had happened to it, so a relay that had
/// been switched off went on being handed to every caller.
/// </summary>
public sealed class RelayHealthMonitor : BackgroundService
{
    private readonly RendezvousOptions _options;
    private readonly ILogger<RelayHealthMonitor> _log;
    private readonly TimeProvider _time;
    private readonly HttpClient _http;
    private readonly Dictionary<string, RelayHealth> _health = [];
    private readonly Lock _gate = new();
    private volatile bool _polledOnce;

    public RelayHealthMonitor(IOptions<RendezvousOptions> options, TimeProvider time, ILogger<RelayHealthMonitor> log, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _time = time;
        _log = log;

        // Short: a relay that cannot answer in two seconds is not a relay to send somebody to, and the
        // whole point of the timeout is to move on to the next one rather than to wait.
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        foreach (RelayEndpoint endpoint in Configured(_options))
        {
            _health[endpoint.Address] = new RelayHealth { Endpoint = endpoint };
        }
    }

    /// <summary>
    /// The configured relays, new-style entries first so an address in both lists keeps its region and key.
    /// </summary>
    public static IReadOnlyList<RelayEndpoint> Configured(RendezvousOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var byAddress = new Dictionary<string, RelayEndpoint>(StringComparer.OrdinalIgnoreCase);
        foreach (RelayEndpoint relay in options.Relays.Where(r => r.Address.Length > 0))
        {
            byAddress[relay.Address] = relay;
        }

        foreach (string address in options.RelayServers.Where(a => a.Length > 0))
        {
            if (!byAddress.ContainsKey(address))
            {
                byAddress[address] = new RelayEndpoint { Address = address };
            }
        }

        return [.. byAddress.Values];
    }

    /// <summary>False until the first round of polling has finished; see <see cref="RelayHealth.IsHealthy"/>.</summary>
    public bool HasPolled => _polledOnce;

    /// <summary>Everything known about every configured relay, healthy or not.</summary>
    public IReadOnlyList<RelayHealth> All()
    {
        lock (_gate)
        {
            return [.. _health.Values];
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Once immediately, so the first connection after a restart is not sent to a relay nobody has
        // checked; the alternative is a whole interval of guessing.
        await PollAllAsync(ct).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_options.RelayHealthInterval, _time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            await PollAllAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>One round against every configured relay. Internal so a test can drive it deterministically.</summary>
    internal async Task PollAllAsync(CancellationToken ct)
    {
        RelayHealth[] current = [.. All()];
        await Task.WhenAll(current.Select(h => PollAsync(h.Endpoint, ct))).ConfigureAwait(false);
        _polledOnce = true;
    }

    private async Task PollAsync(RelayEndpoint endpoint, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, StatsUrl(endpoint));
            if (endpoint.AdminKey.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + endpoint.AdminKey);
            }

            using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // A 401 here is the usual cause and is worth its own sentence: the relay has an admin key
                // and this rendezvous was not told it, so the relay drops out of rotation for a reason
                // nobody would guess from "unhealthy".
                _log.LogWarning(
                    "Relay {Relay} answered {Status} to a health poll{Hint}",
                    endpoint.Address,
                    (int)response.StatusCode,
                    response.StatusCode == System.Net.HttpStatusCode.Unauthorized ? " (check Relays[].AdminKey)" : string.Empty);
                return;
            }

            RelayStatsResponse? stats = await response.Content
                .ReadFromJsonAsync(RelayJson.Default.RelayStatsResponse, ct).ConfigureAwait(false);
            if (stats is null)
            {
                return;
            }

            lock (_gate)
            {
                _health[endpoint.Address] = new RelayHealth
                {
                    Endpoint = endpoint,
                    LastSeenUtc = _time.GetUtcNow(),
                    ActiveSessions = stats.ActiveSessions,
                    MaxSessions = stats.MaxSessions,
                    BytesPerSecond = stats.BytesPerSecond,
                    MaxBitrateKbps = stats.MaxBitrateKbps,
                    TotalSessions = stats.TotalSessions,
                    TotalBytes = stats.TotalBytes,
                };
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Unreachable is the normal state of a relay being restarted. It simply stops being chosen once
            // its last answer is old enough, and starts again when it comes back.
            _log.LogDebug(e, "Relay {Relay} did not answer a health poll", endpoint.Address);
        }
    }

    /// <summary>
    /// Where to ask. Falls back to the relay's own address on the default HTTP port, which is right when the
    /// relay has a machine to itself and wrong when it shares one — hence <see cref="RelayEndpoint.StatsAddress"/>.
    /// </summary>
    private static string StatsUrl(RelayEndpoint endpoint)
    {
        if (endpoint.StatsAddress.Length > 0)
        {
            return Absolute(endpoint.StatsAddress) + "/api/stats";
        }

        // A relay with a machine to itself serves its admin API on the default port, which is the case this
        // guess is for. A relay sharing a host moves that port (the shipped deployment uses 21124 because
        // 21114 is the rendezvous's), and then the guess is wrong and StatsAddress has to be set.
        string host = endpoint.Address.Split(':')[0];
        return $"http://{host}:{ProtocolConstants.HttpApiPort}/api/stats";
    }

    private static string Absolute(string address) =>
        address.Contains("://", StringComparison.Ordinal) ? address.TrimEnd('/') : "http://" + address.TrimEnd('/');

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}

/// <summary>The part of a relay's <c>/api/stats</c> the chooser needs, and the two totals this server's reports pass on.</summary>
internal sealed record RelayStatsResponse
{
    public int ActiveSessions { get; init; }

    public int MaxSessions { get; init; }

    public long BytesPerSecond { get; init; }

    public int MaxBitrateKbps { get; init; }

    public long TotalSessions { get; init; }

    public long TotalBytes { get; init; }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(RelayStatsResponse))]
internal sealed partial class RelayJson : System.Text.Json.Serialization.JsonSerializerContext;
