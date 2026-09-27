using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using DeskPair.Server.Shared;

namespace DeskPair.Server.Shared.Tests;

/// <summary>
/// The admin API fails closed.
///
/// Every server here mounts its operator endpoints under one group with one filter, so this is the place
/// to pin what that filter does with no key, the wrong key and the right one -- and the one exception, a
/// Development environment, which is a developer's own machine and nobody else's.
/// </summary>
public class AdminApiTests
{
    /// <summary>A server with nothing but the shared hosting and one guarded endpoint, on an ephemeral loopback port.</summary>
    private sealed class Server : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private Server(WebApplication app, HttpClient client, string address)
        {
            _app = app;
            Client = client;
            Address = address;
        }

        public HttpClient Client { get; }

        /// <summary>What Kestrel says it bound.</summary>
        public string Address { get; }

        public static async Task<Server> StartAsync(params string[] args)
        {
            var builder = WebApplication.CreateBuilder(["--Logging:LogLevel:Default=Warning", .. args]);
            builder.AddSunlloServerShared(0, "127.0.0.1");
            WebApplication app = builder.Build();
            app.MapSunlloHealth("test");
            RouteGroupBuilder api = app.MapGroup("/api").RequireAdminApiKey();
            api.MapGet("/ping", () => Results.Text("pong"));
            await app.StartAsync();

            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            var client = new HttpClient { BaseAddress = new Uri(address) };
            return new Server(app, client, address);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Without_a_key_the_admin_api_answers_401_to_everything_and_health_still_answers()
    {
        await using Server server = await Server.StartAsync("--environment=Production");

        (await server.Client.GetAsync("/api/ping")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await server.Client.GetAsync("/healthz")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_placeholder_from_the_example_configuration_is_not_a_key()
    {
        await using Server server = await Server.StartAsync("--environment=Production", "--Admin:ApiKey=" + AdminOptions.Placeholder);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/ping");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminOptions.Placeholder);

        (await server.Client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_development_environment_may_run_without_one()
    {
        await using Server server = await Server.StartAsync("--environment=Development");

        (await server.Client.GetAsync("/api/ping")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_wrong_key_is_refused_and_the_right_one_is_let_through()
    {
        await using Server server = await Server.StartAsync("--environment=Production", "--Admin:ApiKey=correct-horse");

        using var wrong = new HttpRequestMessage(HttpMethod.Get, "/api/ping");
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "correct-hors");
        (await server.Client.SendAsync(wrong)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await server.Client.GetAsync("/api/ping")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "no header at all");

        using var right = new HttpRequestMessage(HttpMethod.Get, "/api/ping");
        right.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "correct-horse");
        HttpResponseMessage ok = await server.Client.SendAsync(right);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ok.Content.ReadAsStringAsync()).ShouldBe("pong");
    }

    [Fact]
    public async Task The_bind_address_comes_from_the_configuration_when_the_caller_has_no_opinion()
    {
        var builder = WebApplication.CreateBuilder(["--Logging:LogLevel:Default=Warning", "--Admin:BindAddress=127.0.0.1"]);
        builder.AddSunlloServerShared(0);
        WebApplication app = builder.Build();
        await app.StartAsync();
        try
        {
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            address.ShouldStartWith("http://127.0.0.1:");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public void The_placeholder_and_an_empty_string_are_both_no_key()
    {
        new AdminOptions().HasApiKey.ShouldBeFalse();
        new AdminOptions { ApiKey = AdminOptions.Placeholder }.HasApiKey.ShouldBeFalse();
        new AdminOptions { ApiKey = "k" }.HasApiKey.ShouldBeTrue();
    }
}
