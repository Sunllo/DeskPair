using System.Net;
using Microsoft.Extensions.Options;
using DeskPair.Server.Shared;

namespace DeskPair.Rendezvous.Core;

/// <summary>
/// Picks the relay to hand a connection, by region first and load second.
///
/// It used to return <c>RelayServers[0]</c> unconditionally — no health, no load, no rotation — so a second
/// entry in that list was never chosen, a relay that had been switched off went on being handed out, and a
/// busy one went on taking everything. The list looked like it supported several relays and did not.
///
/// Region before load, because a relay on the wrong continent is worse for the user than a busy one nearby:
/// load costs picture quality, distance costs responsiveness, and a remote desktop is judged on the second.
/// Within a region, the least loaded wins.
/// </summary>
public sealed class RelaySelector(IOptions<RendezvousOptions> options, RelayHealthMonitor health, TimeProvider time, ILogger<RelaySelector> log)
{
    private readonly RendezvousOptions _options = options.Value;

    /// <summary>Why no relay was returned, so the caller can say something true about it.</summary>
    public enum Outcome
    {
        Ok = 0,

        /// <summary>None configured. Not necessarily a problem: a punched connection needs no relay.</summary>
        NoneConfigured,

        /// <summary>Configured, but none has answered recently.</summary>
        NoneHealthy,

        /// <summary>All healthy relays are at or over their configured ceiling.</summary>
        AllFull,
    }

    /// <summary>
    /// The relays to offer, best first.
    ///
    /// More than one, because the client can try the next when the first does not answer — a relay can die
    /// between being chosen and being dialled, and the alternative is a failed connection over a machine
    /// that was already known to be replaceable.
    /// </summary>
    public (Outcome Result, IReadOnlyList<string> Relays) Select(IPAddress? controller)
    {
        IReadOnlyList<RelayHealth> all = health.All();
        if (all.Count == 0)
        {
            return (Outcome.NoneConfigured, []);
        }

        DateTimeOffset now = time.GetUtcNow();
        List<RelayHealth> healthy = [.. all.Where(h => h.IsHealthy(now, _options.RelayHealthInterval, assumeUnpolled: !health.HasPolled))];

        // Nothing answered. That is far more likely to be a monitoring problem than every relay being down
        // at once -- the usual cause is a StatsAddress that does not point at the relay's admin port -- and
        // an upgrade that silently refused every connection would be worse than the bug this replaced.
        // So: fall back to all of them, and say why, loudly enough that somebody fixes the configuration.
        if (healthy.Count == 0 && all.Any(h => h.LastSeenUtc == default))
        {
            log.LogWarning(
                "No relay has ever answered a health poll; using all {Count} regardless. Check Relays[].StatsAddress and Relays[].AdminKey.",
                all.Count);
            healthy = [.. all];
        }

        if (healthy.Count == 0)
        {
            return (Outcome.NoneHealthy, []);
        }

        // Region matching is best-effort by design: no country table, an address it does not cover, or a
        // region nobody is tagged with all fall through to "consider everything", which is worse than the
        // ideal and much better than refusing.
        string? country = controller is null ? null : GeoCountry.Lookup(controller);
        List<RelayHealth> candidates = healthy;
        if (country is not null)
        {
            List<RelayHealth> local = [.. healthy.Where(h => string.Equals(h.Endpoint.Region, country, StringComparison.OrdinalIgnoreCase))];
            if (local.Count > 0)
            {
                candidates = local;
            }
        }

        List<RelayHealth> ordered = [.. candidates.OrderBy(h => h.Load)];
        if (ordered[0].Load >= 1.0)
        {
            // Refusing is the point of having a ceiling. Accepting anyway would saturate the link and
            // collapse the quality of every session already running, which nobody can see from a session
            // count, and which punishes the people already connected for the arrival of one more.
            log.LogWarning(
                "Every healthy relay is at capacity ({Relays}); refusing a new session",
                string.Join(", ", ordered.Select(h => $"{h.Endpoint.Address} {h.Load:P0}")));
            return (Outcome.AllFull, []);
        }

        return (Outcome.Ok, [.. ordered.Where(h => h.Load < 1.0).Select(h => h.Endpoint.Address)]);
    }

    /// <summary>
    /// Whether an address is one this rendezvous configured.
    ///
    /// A host behind a symmetric NAT answers with a relay of its own, and that address was forwarded to the
    /// controller unchecked. A host that had been told to could have pointed every connection it took part
    /// in at a machine of its choosing.
    /// </summary>
    public bool IsConfigured(string address) =>
        address.Length > 0 && health.All().Any(h => string.Equals(h.Endpoint.Address, address, StringComparison.OrdinalIgnoreCase));
}
