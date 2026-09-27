using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// There may be one engine on a machine, and only one.
///
/// Two of them share a data directory, so they take the same identity and displace each other at the
/// rendezvous server; they want the same direct-access port; and they race for the same named pipe, where
/// the loser logs an access denial several times a second for as long as it runs. All three were watched
/// happening on a real machine, which is why this is pinned rather than trusted.
/// </summary>
public class EngineHostTests
{
    [Fact]
    public async Task The_app_starts_no_engine_when_the_service_owns_it()
    {
        using var host = new EngineHost(NullLoggerFactory.Instance, serviceOwnsEngine: () => true);

        await host.EnsureEngineAsync();

        host.StartedEngine.ShouldBeFalse();
    }

    /// <summary>
    /// Seen on a real machine: the service stopped while the app was attached to it, and the computer stayed
    /// offline until unattended access was turned off and on again. The app runs the engine itself once the
    /// service has stayed away -- not at the first look, which may be the service's own five-second restart.
    /// </summary>
    [Fact]
    public async Task A_service_that_stays_stopped_is_replaced_by_an_engine_in_the_app()
    {
        bool running = true;
        var engines = new FakeEngines();
        using var host = new EngineHost(NullLoggerFactory.Instance, serviceOwnsEngine: () => running, runEngine: engines.RunAsync);
        await host.EnsureEngineAsync();
        int moves = 0;
        host.EngineMoved += () => moves++;

        running = false;
        for (int i = 1; i < EngineHost.TakeOverAfterChecks; i++)
        {
            (await host.CheckAsync()).ShouldBeFalse($"look {i}: the service may be restarting");
        }

        (await host.CheckAsync()).ShouldBeTrue();

        host.StartedEngine.ShouldBeTrue();
        host.Token.ShouldNotBeNull();
        await engines.Started.WaitAsync(TimeSpan.FromSeconds(5));
        moves.ShouldBe(1);
    }

    [Fact]
    public async Task A_service_back_in_time_keeps_the_engine()
    {
        bool running = true;
        var engines = new FakeEngines();
        using var host = new EngineHost(NullLoggerFactory.Instance, serviceOwnsEngine: () => running, runEngine: engines.RunAsync);
        await host.EnsureEngineAsync();

        running = false;
        await host.CheckAsync();
        await host.CheckAsync();
        running = true;
        await host.CheckAsync();
        running = false;
        for (int i = 1; i < EngineHost.TakeOverAfterChecks; i++)
        {
            (await host.CheckAsync()).ShouldBeFalse("the count starts again once the service has been seen");
        }

        host.StartedEngine.ShouldBeFalse();
    }

    /// <summary>
    /// Seen on a real machine: unattended access turned on, and until the app was restarted two engines ran side by
    /// side -- a setting saved in the app landed in the engine about to go. The app's engine now stops as soon as
    /// the service is running, and the links follow the token to the service's.
    /// </summary>
    [Fact]
    public async Task A_service_that_starts_takes_the_engine_from_the_app()
    {
        bool running = true;
        var engines = new FakeEngines();
        using var host = new EngineHost(NullLoggerFactory.Instance, serviceOwnsEngine: () => running, runEngine: engines.RunAsync);
        await host.EnsureEngineAsync();
        running = false;
        for (int i = 0; i < EngineHost.TakeOverAfterChecks; i++)
        {
            await host.CheckAsync();
        }

        await engines.Started.WaitAsync(TimeSpan.FromSeconds(5));
        running = true;

        (await host.CheckAsync()).ShouldBeTrue();

        host.StartedEngine.ShouldBeFalse();
        host.Token.ShouldBeNull("the links look for the service's token again");
        await engines.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Stands in for the engine: runs until cancelled, and says when it started and stopped.</summary>
    private sealed class FakeEngines
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public Task Stopped => _stopped.Task;

        public async Task RunAsync(string token, CancellationToken ct)
        {
            _started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                _stopped.TrySetResult();
            }
        }
    }
}
