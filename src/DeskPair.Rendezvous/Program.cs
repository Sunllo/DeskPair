using Microsoft.Extensions.Options;
using DeskPair.Rendezvous;
using DeskPair.Rendezvous.Core;
using DeskPair.Rendezvous.Net;
using DeskPair.Rendezvous.Persistence;
using DeskPair.Server.Shared;

WebApplication app = RendezvousServer.Build(args);
await app.RunAsync();

namespace DeskPair.Rendezvous
{
    /// <summary>Composes the rendezvous host; tests call this with port and key overrides.</summary>
    public static class RendezvousServer
    {
        public static WebApplication Build(string[] args, Action<RendezvousOptions>? configure = null, ServerKeys? keys = null)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.Configure<RendezvousOptions>(builder.Configuration.GetSection(RendezvousOptions.Section));
            if (configure is not null)
            {
                builder.Services.PostConfigure(configure);
            }

            var bootstrap = new RendezvousOptions();
            builder.Configuration.GetSection(RendezvousOptions.Section).Bind(bootstrap);
            configure?.Invoke(bootstrap);
            builder.AddSunlloServerShared(bootstrap.HttpPort);

            builder.Services.AddSingleton(sp => keys ?? ServerKeys.LoadOrCreate(bootstrap.KeyPath, sp.GetRequiredService<ILogger<ServerKeys>>()));
            builder.Services.AddSingleton<IPeerStore>(_ => string.IsNullOrEmpty(bootstrap.DatabasePath) ? new NullPeerStore() : new SqlitePeerStore(bootstrap.DatabasePath));
            builder.Services.AddSingleton(sp => new IdAllocator(sp.GetRequiredService<IOptions<RendezvousOptions>>().Value.IdDigits));
            builder.Services.AddSingleton<PeerTable>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<PeerTable>());
            builder.Services.AddSingleton<RelayHealthMonitor>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<RelayHealthMonitor>());
            builder.Services.AddSingleton<RelaySelector>();
            builder.Services.AddSingleton<RendezvousStats>();
            builder.Services.AddSingleton<PunchRegistry>();
            builder.Services.AddSingleton<RendezvousHandler>();
            builder.Services.AddSingleton<UdpListener>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<UdpListener>());
            builder.Services.AddSingleton<TcpListenersService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<TcpListenersService>());

            // Idle unless Rendezvous:Report:Url is set.
            builder.Services.AddSingleton<StatsReporter>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<StatsReporter>());

            WebApplication app = builder.Build();
            ServerKeys serverKeys = app.Services.GetRequiredService<ServerKeys>();
            app.Logger.LogInformation("Server public key: {Key} (fingerprint {Fingerprint})", serverKeys.PublicKeyBase64, serverKeys.Fingerprint);

            if (!DeskPair.Server.Shared.GeoCountry.IsAvailable)
            {
                app.Logger.LogWarning(
                    "No country table is embedded: relays will be chosen on load alone, ignoring their Region.");
            }

            app.MapSunlloHealth("rendezvous");
            app.MapGet("/key", (ServerKeys k) => Results.Text(k.PublicKeyBase64));
            RouteGroupBuilder api = app.MapGroup("/api").RequireAdminApiKey();
            api.MapGet("/stats", (PeerTable peers, RendezvousStats stats, RelayHealthMonitor relays, TimeProvider time, IOptions<RendezvousOptions> o) => Results.Ok(new
            {
                peersTotal = peers.Count,
                peersOnline = peers.OnlineCount(o.Value.PeerOfflineAfter),
                registrations = stats.Registrations,
                identityRegistrations = stats.IdentityRegistrations,
                punchRequests = stats.PunchRequests,
                relaysBrokered = stats.RelaysBrokered,
                rejected = stats.Rejected,
                relays = relays.All().Select(h => new
                {
                    address = h.Endpoint.Address,
                    region = h.Endpoint.Region,
                    healthy = h.IsHealthy(time.GetUtcNow(), o.Value.RelayHealthInterval),
                    lastSeen = h.LastSeenUtc == default ? null : (DateTimeOffset?)h.LastSeenUtc,
                    activeSessions = h.ActiveSessions,
                    maxSessions = h.MaxSessions,
                    bytesPerSecond = h.BytesPerSecond,
                    maxBitrateKbps = h.MaxBitrateKbps,
                    load = h.Load,
                }),
            }));
            api.MapGet("/peers/{id}", (string id, PeerTable peers, IOptions<RendezvousOptions> o, TimeProvider time) =>
            {
                PeerEntry? p = peers.Get(id);
                return p is null
                    ? Results.NotFound()
                    : Results.Ok(new { id = p.Id, online = p.IsOnline(time.GetUtcNow(), o.Value.PeerOfflineAfter), signed = p.SignedIdentity is not null, lastSeen = p.LastSeenUtc, created = p.CreatedUtc, version = p.Version });
            });
            api.MapGet("/online", (string ids, PeerTable peers, IOptions<RendezvousOptions> o, TimeProvider time) =>
            {
                DateTimeOffset now = time.GetUtcNow();
                return Results.Ok(ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(100)
                    .ToDictionary(id => id, id => peers.Get(id)?.IsOnline(now, o.Value.PeerOfflineAfter) ?? false));
            });
            return app;
        }
    }
}
