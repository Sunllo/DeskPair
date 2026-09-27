using Microsoft.Extensions.Options;
using DeskPair.Relay;
using DeskPair.Relay.Core;
using DeskPair.Relay.Net;
using DeskPair.Server.Shared;

WebApplication app = RelayServer.Build(args);
await app.RunAsync();

namespace DeskPair.Relay
{
    /// <summary>Composes the relay host; tests call this with port overrides.</summary>
    public static class RelayServer
    {
        public static WebApplication Build(string[] args, Action<RelayOptions>? configure = null)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection(RelayOptions.Section));
            if (configure is not null)
            {
                builder.Services.PostConfigure(configure);
            }

            var bootstrap = new RelayOptions();
            builder.Configuration.GetSection(RelayOptions.Section).Bind(bootstrap);
            configure?.Invoke(bootstrap);
            builder.AddSunlloServerShared(bootstrap.HttpPort);

            builder.Services.AddSingleton<RelayStats>();
            builder.Services.AddSingleton(sp => new RelayThrottle(sp.GetRequiredService<IOptions<RelayOptions>>().Value.MaxBitrateKbps, sp.GetRequiredService<TimeProvider>()));
            builder.Services.AddSingleton<RelayPairing>();
            builder.Services.AddSingleton<RelayListener>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<RelayListener>());
            builder.Services.AddSingleton<UdpRelayListener>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<UdpRelayListener>());

            WebApplication app = builder.Build();
            RelayOptions effective = app.Services.GetRequiredService<IOptions<RelayOptions>>().Value;
            if (effective.RequireTicket && effective.RendezvousPublicKeySpki is null)
            {
                // Fail closed, and say so once where an operator looks: every request will be refused until
                // the key is set, because a relay that cannot check tickets must not guess.
                app.Logger.LogError("Relay:RendezvousPublicKey is not set (or is not base64); every relay request will be refused until it is. Set it to the rendezvous server's /key, or Relay:RequireTicket=false while peers are being updated.");
            }
            else if (!effective.RequireTicket)
            {
                app.Logger.LogWarning("Relay:RequireTicket is off: any two sockets that agree on a uuid are paired. Turn it back on once every peer speaks protocol 2.");
            }

            app.MapSunlloHealth("relay");
            RouteGroupBuilder api = app.MapGroup("/api").RequireAdminApiKey();
            api.MapGet("/stats", (RelayStats stats, IOptions<RelayOptions> o) => Results.Ok(stats.Snapshot(o.Value)));
            return app;
        }
    }
}
