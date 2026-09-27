using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace DeskPair.Desktop.Engine.Service;

/// <summary>
/// Keeps exactly one engine running, in the session with the screen.
///
/// The console session changes when a user is switched or a machine comes back from sleep, and the engine
/// itself can die. A process cannot move between sessions, so the answer to either is to end this one and
/// start another.
///
/// It used to follow the input desktop as well, restarting the engine onto Winlogon when the screen locked
/// and back again afterwards. That is gone. The desktop changes at the moment the lock screen hands over
/// to the credential prompt, so the restart landed while somebody was typing a password and took the
/// session with it -- which made unlocking a machine remotely impossible, rather than costing the second
/// of black it had been written down as costing. Capture and injection each attach a thread of their own
/// to the input desktop instead, which is what the restart was standing in for.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class EngineSupervisor : IDisposable
{
    /// <summary>
    /// How often to look.
    ///
    /// There is a notification for session changes and none for desktop switches, so this polls. Half a
    /// second is under the time it takes a person to read a UAC prompt, and the check is two handle
    /// operations -- cheap enough that a slower poll would be saving nothing worth having.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long to leave a dead engine before starting another.
    ///
    /// An engine that exits immediately and is immediately restarted is a loop that pins a core and buries
    /// the reason in a log nobody can read. Waiting is not a fix, but it keeps the evidence legible.
    /// </summary>
    private static readonly TimeSpan AfterFailure = TimeSpan.FromSeconds(5);

    private readonly ILogger _log;
    private DesktopEngineLauncher.Engine? _engine;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    private uint? _session;
    private string _idleBecause = string.Empty;

    public EngineSupervisor(ILogger log, TimeProvider? time = null)
    {
        _log = log;
        Time = time ?? TimeProvider.System;
    }

    internal TimeProvider Time { get; }

    /// <summary>Runs until cancelled, then leaves no engine behind.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Tick();
                await Task.Delay(Interval, Time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            StopEngine("the service is stopping");
        }
    }

    /// <summary>
    /// One look at the world, and at most one action.
    ///
    /// Internal so a test can drive it without a clock: every decision this makes is a pure function of
    /// what the four probes return.
    /// </summary>
    internal void Tick()
    {
        uint? session = DesktopEngineLauncher.ConsoleSession();
        if (session is null)
        {
            // No physical console: a machine with nobody at it in the sense that there is no screen to
            // read, not merely nobody signed in. Nothing to capture, so nothing to run.
            StopEngine("there is no console session");
            _session = null;
            Idle("there is no console session");
            return;
        }

        Working();

        if (_engine is { } engine)
        {
            if (engine.IsAlive)
            {
                if (_session == session)
                {
                    return;
                }

                // The console session changed under it: a user switch, or a machine back from sleep. The
                // engine has no way to notice that, because from inside it nothing about its own session
                // changed -- the screen went somewhere else.
                _log.LogInformation(
                    "Moving the engine: session {WasSession} is now session {Session}",
                    _session,
                    session);
                StopEngine(null);
            }
            else
            {
                ReadExitCode(engine);
                StopEngine(null);
            }
        }

        if (Time.GetUtcNow() < _nextAttempt)
        {
            return;
        }

        _engine = DesktopEngineLauncher.Launch(session.Value, _log);
        _session = session;
        if (_engine is null)
        {
            _nextAttempt = Time.GetUtcNow() + AfterFailure;
        }
    }

    /// <summary>Says how the engine left, and makes the next attempt wait.</summary>
    private void ReadExitCode(DesktopEngineLauncher.Engine engine)
    {
        _log.LogWarning("Engine {Process} exited with code {Code}", engine.ProcessId, engine.ExitCode);
        _nextAttempt = Time.GetUtcNow() + AfterFailure;
    }

    /// <summary>
    /// Says why nothing is happening, once, and again only when the answer changes.
    ///
    /// Twice a second into a log file would bury everything else in it; never saying it at all leaves a
    /// service that appears to be running and is doing nothing.
    /// </summary>
    private void Idle(string because)
    {
        if (_idleBecause == because)
        {
            return;
        }

        _idleBecause = because;
        _log.LogWarning("Not running an engine: {Because}", because);
    }

    /// <summary>Clears the idle reason, so the next one is logged even if it repeats an earlier one.</summary>
    private void Working()
    {
        if (_idleBecause.Length == 0)
        {
            return;
        }

        _log.LogInformation("The obstacle is gone: {Because}", _idleBecause);
        _idleBecause = string.Empty;
    }

    private void StopEngine(string? why)
    {
        if (_engine is null)
        {
            return;
        }

        if (why is not null)
        {
            _log.LogInformation("Stopping engine {Process}: {Why}", _engine.ProcessId, why);
        }

        _engine.Stop();
        _engine.Dispose();
        _engine = null;
    }

    public void Dispose() => StopEngine(null);
}
