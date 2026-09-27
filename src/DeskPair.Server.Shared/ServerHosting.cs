using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeskPair.Server.Shared;

public sealed class AdminOptions
{
    public const string Section = "Admin";

    /// <summary>The value the example configurations ship with, which is not a key.</summary>
    public const string Placeholder = "CHANGE-ME";

    /// <summary>
    /// Bearer token required for <c>/api/*</c>.
    ///
    /// Empty (or left at the placeholder) does not switch authentication off: outside a Development
    /// environment the admin API answers 401 to everything until a key is set. The one thing an
    /// unconfigured server must not do is answer an operator's endpoints to whoever asks.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// The address the HTTP listener binds. Empty is every interface, which the rendezvous needs because
    /// <c>/key</c> is what clients fetch; a relay that shares its machine with the rendezvous can bind
    /// <c>127.0.0.1</c>, since the only caller of its admin API is next door.
    /// </summary>
    public string BindAddress { get; set; } = string.Empty;

    public bool HasApiKey => ApiKey.Length > 0 && ApiKey != Placeholder;
}

public static class ServerHosting
{
    public static string ServiceVersion { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>
    /// Shared start-up for every server here. <paramref name="bindAddress"/> is null for the ones that face
    /// the world and an address for the ones that must not: the data tier answers on its own LAN address
    /// only, so a firewall rule going missing does not by itself put the accounts on the internet. When
    /// the caller has no opinion, <c>Admin:BindAddress</c> from the configuration decides.
    /// </summary>
    public static WebApplicationBuilder AddSunlloServerShared(
        this WebApplicationBuilder builder, int httpPort, string? bindAddress = null)
    {
        builder.Logging.ClearProviders();
        if (builder.Configuration.GetValue<bool>("Logging:Json"))
        {
            builder.Logging.AddJsonConsole(o => o.UseUtcTimestamp = true);
        }
        else
        {
            builder.Logging.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss.fff ";
                o.UseUtcTimestamp = true;
            });
        }

        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting", LogLevel.Warning);
        builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.Section));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddHostedService<AdminApiStartupCheck>();

        string? bind = string.IsNullOrWhiteSpace(bindAddress)
            ? builder.Configuration[$"{AdminOptions.Section}:{nameof(AdminOptions.BindAddress)}"]
            : bindAddress;
        builder.WebHost.ConfigureKestrel(k =>
        {
            if (string.IsNullOrWhiteSpace(bind))
            {
                k.ListenAnyIP(httpPort);
            }
            else
            {
                k.Listen(System.Net.IPAddress.Parse(bind), httpPort);
            }
        });
        return builder;
    }

    public static IEndpointConventionBuilder MapSunlloHealth(this IEndpointRouteBuilder endpoints, string serviceName)
    {
        long started = Stopwatch.GetTimestamp();
        return endpoints.MapGet("/healthz", () => Results.Ok(new
        {
            service = serviceName,
            version = ServiceVersion,
            uptimeSeconds = (long)Stopwatch.GetElapsedTime(started).TotalSeconds,
        }));
    }

    /// <summary>
    /// Endpoint filter enforcing <see cref="AdminOptions.ApiKey"/> as a bearer token.
    ///
    /// Fails closed. A missing key used to mean "no authentication", which is the wrong default for a
    /// group of endpoints that exists to be looked at by one person: a server deployed from the example
    /// configuration with the placeholder still in it answered its stats, its peers and its relays to
    /// the internet. Now it answers 401 until somebody sets a key, and only a Development environment --
    /// a developer's own machine -- is let through without one.
    /// </summary>
    public static RouteGroupBuilder RequireAdminApiKey(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (ctx, next) =>
        {
            AdminOptions options = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<AdminOptions>>().Value;
            if (!options.HasApiKey)
            {
                return ctx.HttpContext.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment()
                    ? await next(ctx)
                    : Results.Unauthorized();
            }

            string? header = ctx.HttpContext.Request.Headers.Authorization;
            if (header is null || !header.StartsWith("Bearer ", StringComparison.Ordinal) ||
                !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(header["Bearer ".Length..]),
                    System.Text.Encoding.UTF8.GetBytes(options.ApiKey)))
            {
                return Results.Unauthorized();
            }

            return await next(ctx);
        });
        return group;
    }

    /// <summary>Says at start-up, once, what the admin API will do about a missing key. A log line, not a refusal to start: the signalling must not go down over an operator's endpoint.</summary>
    private sealed class AdminApiStartupCheck(IOptions<AdminOptions> options, IHostEnvironment environment, ILogger<AdminApiStartupCheck> log) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (!options.Value.HasApiKey)
            {
                if (environment.IsDevelopment())
                {
                    log.LogWarning("Admin:ApiKey is not set; the admin API is open because this is a Development environment");
                }
                else
                {
                    log.LogError("Admin:ApiKey is not set (or is still the placeholder); the admin API will answer 401 to everything until it is");
                }
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
