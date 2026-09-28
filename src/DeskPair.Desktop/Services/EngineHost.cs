using Microsoft.Extensions.Logging;
using DeskPair.Core.Ipc;
using DeskPair.Desktop.Engine;

namespace DeskPair.Desktop.Services;

/// <summary>
/// Makes "open the app" enough to be controllable: the host engine runs inside this process, for as long as
/// the app is open. Closing DeskPair stops it, which is what the user expects of one application.
///
/// It used to be a child process. macOS ended that: Screen Recording and Accessibility are granted per
/// executable, so a spawned engine was a second consent the user never knew to grant, and until they did,
/// capture returned nothing and the viewer saw a black screen. One process means one identity and one set of
/// permissions, and there is nothing left that can outlive the app or fight it for the port.
///
/// The Windows host service is the one exception, and it is the opposite arrangement: when it is running
/// it owns the engine and this app attaches to it. That is not a preference. Two engines share a data
/// directory, so they take the same identity and displace each other at the rendezvous server; they want
/// the same direct-access port; and they race for the same named pipe, where the loser logs an access
/// denial several times a second for as long as it runs. All three were watched happening before this was
/// written.
///
/// Which one it is gets looked at again while the app is open (<see cref="StartWatching"/>). A service turned on
/// takes the engine over from this app at once -- it used to wait for the app to be restarted, and until then the
/// two ran side by side, a setting saved in the app landing in the engine about to go. A service that stops is
/// replaced by an engine in this app once it has stayed stopped for a while -- it used to leave the app attached
/// to nothing, and the computer offline, until unattended access was turned off and on again.
/// </summary>
public sealed class EngineHost : IDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How often <see cref="StartWatching"/> looks.</summary>
    public static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Looks in a row the service must have been absent before this app runs the engine instead. The service's
    /// own recovery restarts it five seconds after a crash; taking over inside that gap would only hand back.
    /// </summary>
    public const int TakeOverAfterChecks = 4;

    private readonly ILogger _log;
    private readonly ILoggerFactory _logs;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;
    private readonly Func<CancellationToken, Task>? _connectionManager;
    private readonly Func<bool> _serviceOwnsEngine;
    private readonly Func<string, CancellationToken, Task> _runEngine;
    private CancellationTokenSource? _engineCts;
    private Task? _engine;
    private Task? _watch;
    private string? _token;
    private bool _decides;
    private int _serviceAbsent;

    /// <param name="connectionManager">Shows the connection manager when a connection needs approving.</param>
    /// <param name="serviceOwnsEngine">
    /// Overrides the check for the Windows host service. A seam for tests, which must be able to assert
    /// that this app declines to start an engine without installing a service to prove it.
    /// </param>
    /// <param name="runEngine">Runs an engine with a token until cancelled; the real one by default. A seam for tests.</param>
    public EngineHost(
        ILoggerFactory logs,
        Func<CancellationToken, Task>? connectionManager = null,
        Func<bool>? serviceOwnsEngine = null,
        Func<string, CancellationToken, Task>? runEngine = null)
    {
        _logs = logs;
        _log = logs.CreateLogger("engine");
        _connectionManager = connectionManager;
        _serviceOwnsEngine = serviceOwnsEngine ?? ServiceIsRunning;
        _runEngine = runEngine ?? ((token, ct) => ServerRole.RunAsync(["--server", "--ipc-token", token], _logs, ct, _connectionManager));
    }

    /// <summary>Token the UI must present: the running engine's, or the one this app's engine was given.</summary>
    public string? Token => _token;

    public bool StartedEngine => _engine is not null;

    /// <summary>The engine moved between this app and the service; <see cref="Token"/> has changed with it.</summary>
    public event Action? EngineMoved;

    /// <summary>
    /// Whether something other than this app is responsible for the engine.
    ///
    /// The unattended install, when it is there, is. It puts an engine in the session that has the screen
    /// and follows that screen to the login window and back, which is the whole reason somebody installed
    /// it -- and there can only be one: two would take the same identity, want the same direct-access
    /// port, and race for the same pipe or socket, with whichever started first winning and the other
    /// failing in a loop.
    ///
    /// This used to answer false on everything but Windows, which was true while Windows was the only
    /// system with one. It is about to stop being true on both of the others, and an app that keeps
    /// starting its own engine beside a daemon is the three-way fight above, on a machine where nobody
    /// thinks to look for a second engine.
    ///
    /// Measured, not assumed, and on every look: it can be installed, removed, started or stopped at any time.
    /// Running, on Windows, rather than installed: a service that is installed and stopped owns nothing.
    /// </summary>
    private static bool ServiceIsRunning() => UnattendedInstall.IsActive();

    /// <summary>Uses an engine that is already listening; otherwise starts one in this process.</summary>
    public async Task EnsureEngineAsync()
    {
        // Before the probe, never after: an engine an older version left running would otherwise be adopted
        // below as "one is already listening".
        LegacyInstall.Sweep(_log);

        if (_serviceOwnsEngine())
        {
            _decides = true;
            // Its token may not be on disk yet -- the service starts at boot and the app can open first --
            // and that is fine. HostLink resolves the token on every reconnection attempt, so an app that
            // opens a moment too early attaches a moment later rather than never. That only holds while
            // no token is pinned here: the service's engine mints a new one every time it starts, and an
            // app that had captured the old one knocked with it every two seconds for as long as it ran.
            _token = null;
            _log.LogInformation(
                HostLink.ResolveToken(null) is null
                    ? "The host service owns the engine; waiting for it to say where to attach"
                    : "The host service owns the engine; attaching to it rather than starting another");
            return;
        }

        byte[]? existing = HostLink.ResolveToken(null);
        if (existing is not null && await ProbeAsync(existing).ConfigureAwait(false))
        {
            // Only another copy of this same program can be answering. Sharing it beats two engines fighting
            // over the direct-access port, but it is a different process: on macOS its screen-recording
            // consent is its own, so say so here rather than leave a black screen unexplained.
            _token = Convert.ToHexString(existing);
            _log.LogInformation("A host engine is already running; using it rather than starting another. Screen capture follows that process's permissions, not this app's");
            return;
        }

        _decides = true;
        _log.LogInformation("Hosting the engine in this process");
        StartHere();
    }

    /// <summary>
    /// Keeps looking at who should have the engine, every <see cref="WatchInterval"/>, for as long as the app is
    /// open. Nothing to do when the app was told which engine to use (a token, or another copy's engine).
    /// </summary>
    public void StartWatching()
    {
        if (!_decides || _watch is not null)
        {
            return;
        }

        CancellationToken ct = _cts.Token;
        _watch = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(WatchInterval, ct).ConfigureAwait(false);
                    await CheckAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    _log.LogWarning(e, "Could not check who runs the engine");
                }
            }
        });
    }

    /// <summary>One look: hands the engine to a service that is running, or takes it from one that has gone. True when it moved.</summary>
    internal async Task<bool> CheckAsync()
    {
        if (!_decides)
        {
            return false;
        }

        bool service = _serviceOwnsEngine();
        if (_engine is not null)
        {
            if (!service)
            {
                return false;
            }

            _log.LogInformation("The host service is running now; handing the engine over to it");
            await StopHereAsync().ConfigureAwait(false);
            _token = null;
            _serviceAbsent = 0;
            EngineMoved?.Invoke();
            return true;
        }

        _serviceAbsent = service ? 0 : _serviceAbsent + 1;
        if (_serviceAbsent < TakeOverAfterChecks)
        {
            return false;
        }

        _log.LogWarning("The host service is not running; this app runs the engine until it is back");
        _serviceAbsent = 0;
        StartHere();
        EngineMoved?.Invoke();
        return true;
    }

    private void StartHere()
    {
        string token = Convert.ToHexString(IpcServer.NewToken());
        _token = token;
        _engineCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        CancellationToken ct = _engineCts.Token;
        _engine = Task.Run(() => _runEngine(token, ct));

        // Nothing restarts the engine any more, so a fault has to be visible: the UI already shows the host
        // as disconnected, and this says why.
        _ = _engine.ContinueWith(
            t => _log.LogCritical(t.Exception, "The host engine stopped; this computer cannot be controlled until DeskPair is restarted"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private async Task StopHereAsync()
    {
        _engineCts?.Cancel();
        try
        {
            if (_engine is { } engine)
            {
                await engine.WaitAsync(ShutdownTimeout).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Cancelling the engine surfaces here, and one slow to go is going anyway.
        }

        _engineCts?.Dispose();
        _engineCts = null;
        _engine = null;
    }

    private static async Task<bool> ProbeAsync(byte[] token)
    {
        try
        {
            await using IpcClient client = await IpcClient.ConnectAsync(IpcEndpoint.Default, token, IpcRoles.Ui, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, ProbeTimeout).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Stops the engine. A second call does nothing: an app told to end by the system can hear it twice.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            _engine?.Wait(ShutdownTimeout);
        }
        catch (Exception)
        {
            // Cancelling the engine surfaces here; nothing worth reporting while shutting down.
        }

        _cts.Dispose();
    }
}
