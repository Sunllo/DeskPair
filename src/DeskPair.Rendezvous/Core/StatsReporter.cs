using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace DeskPair.Rendezvous.Core;

/// <summary>
/// Posts this server's numbers once a minute to <c>Rendezvous:Report:Url</c>, for a dashboard to draw. Does nothing until
/// <c>Rendezvous:Report:Url</c> is set.
///
/// The counters go as running totals with the moment this process started, and the receiving end works out how many
/// happened in between. A report that is lost costs nothing -- the next one carries it -- and a restart is told
/// apart from a quiet minute by the start time changing, not guessed from a total going down.
///
/// A failure is logged once, and once more when reports are getting through again: the signalling must not fill
/// its own log because the receiving end is being redeployed.
/// </summary>
public sealed class StatsReporter : BackgroundService
{
    /// <summary>Versions past this many share one line, so a server with every build ever made online sends a short list.</summary>
    public const int MostVersions = 12;

    private readonly RendezvousOptions _options;
    private readonly PeerTable _peers;
    private readonly RendezvousStats _stats;
    private readonly RelayHealthMonitor _relays;
    private readonly TimeProvider _time;
    private readonly ILogger<StatsReporter> _log;
    private readonly HttpClient _http;
    private readonly DateTimeOffset _started;
    private bool _failing;

    public StatsReporter(
        IOptions<RendezvousOptions> options,
        PeerTable peers,
        RendezvousStats stats,
        RelayHealthMonitor relays,
        TimeProvider time,
        ILogger<StatsReporter> log,
        HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _peers = peers;
        _stats = stats;
        _relays = relays;
        _time = time;
        _log = log;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // Made once, when the host starts, which is also when the counters it reports began at zero.
        _started = time.GetUtcNow();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_options.Report.Url.Length == 0)
        {
            return;
        }

        _log.LogInformation("Reporting to {Url} every {Interval}", _options.Report.Url, _options.Report.Interval);
        using var timer = new PeriodicTimer(_options.Report.Interval, _time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            await SendAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>One report, now. Internal so a test can drive it without waiting a minute.</summary>
    internal async Task<bool> SendAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Report.Url)
            {
                Content = JsonContent.Create(Build(), ReportJson.Default.StatsReport),
            };
            if (_options.Report.ApiKey.Length > 0)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Report.ApiKey);
            }

            using HttpResponseMessage response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Failed($"the report endpoint answered {(int)response.StatusCode}", null);
                return false;
            }

            if (_failing)
            {
                _failing = false;
                _log.LogInformation("Reports are reaching {Url} again", _options.Report.Url);
            }

            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Failed("the report endpoint did not answer", e);
            return false;
        }
    }

    /// <summary>What is reported: counts, and the relays as the chooser last saw them.</summary>
    internal StatsReport Build()
    {
        DateTimeOffset now = _time.GetUtcNow();
        return new StatsReport(
            _started,
            DeskPair.Server.Shared.ServerHosting.ServiceVersion,
            _peers.Count,
            _peers.OnlineCount(_options.PeerOfflineAfter),
            _stats.PunchRequests,
            _stats.RelaysBrokered,
            _stats.IdentityRegistrations,
            _stats.Rejected,
            [.. _relays.All().Select(h => new StatsReportRelay(
                h.Endpoint.Address,
                h.Endpoint.Region,
                h.IsHealthy(now, _options.RelayHealthInterval),
                h.LastSeenUtc == default ? null : h.LastSeenUtc,
                h.ActiveSessions,
                h.MaxSessions,
                h.BytesPerSecond,
                h.MaxBitrateKbps,
                h.TotalSessions,
                h.TotalBytes))],
            [.. _peers.OnlineVersions(_options.PeerOfflineAfter, MostVersions).Select(v => new StatsReportVersion(v.Version, v.Online))]);
    }

    private void Failed(string why, Exception? e)
    {
        if (_failing)
        {
            _log.LogDebug(e, "A report to {Url} failed: {Why}", _options.Report.Url, why);
            return;
        }

        _failing = true;
        _log.LogWarning(e, "A report to {Url} failed: {Why}; the next success will be logged", _options.Report.Url, why);
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}

// The report as it goes on the wire. Its names are the contract with whatever reads it.

internal sealed record StatsReport(
    DateTimeOffset StartedUtc,
    string Version,
    int PeersTotal,
    int PeersOnline,
    long Connections,
    long Relayed,
    long Identities,
    long Rejected,
    IReadOnlyList<StatsReportRelay> Relays,
    IReadOnlyList<StatsReportVersion> Versions);

internal sealed record StatsReportRelay(
    string Address,
    string Region,
    bool Healthy,
    DateTimeOffset? LastSeenUtc,
    int ActiveSessions,
    int MaxSessions,
    long BytesPerSecond,
    int MaxBitrateKbps,
    long TotalSessions,
    long TotalBytes);

internal sealed record StatsReportVersion(string Version, int Online);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StatsReport))]
internal sealed partial class ReportJson : JsonSerializerContext;
