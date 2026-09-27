using DeskPair.Platform.Linux.Hosting;
using Microsoft.Extensions.Logging;

namespace DeskPair.Desktop.Engine.LinuxService;

/// <summary>A running engine, as much of it as the supervisor needs to know.</summary>
internal interface IEngineHandle : IDisposable
{
    bool IsAlive { get; }

    int ExitCode { get; }

    int ProcessId { get; }

    void Stop();
}

/// <summary>A running session agent: which session it serves, and whether it still does.</summary>
internal interface IAgentHandle : IDisposable
{
    bool IsAlive { get; }

    int ExitCode { get; }

    int ProcessId { get; }

    /// <summary>logind's id of the session the agent runs in.</summary>
    string Session { get; }

    void Stop();
}

/// <summary>What the supervisor asks of the world, so a test can be the world.</summary>
internal interface IEngineWorld
{
    ActiveSession? ActiveSession();

    /// <summary>Starts an engine; null when it could not be started, which is retried after a pause.</summary>
    IEngineHandle? Launch();

    /// <summary>
    /// Whether <paramref name="session"/>'s desktop has started: one of its processes knows the display. A session is on
    /// the screen a moment before that, and an agent started then carries none of the desktop's environment.
    /// </summary>
    bool DesktopUp(ActiveSession session);

    /// <summary>
    /// Starts a session agent as <paramref name="session"/>'s user and leaves its socket for the engine; null when it
    /// could not be started, which is retried after a pause.
    /// </summary>
    IAgentHandle? LaunchAgent(ActiveSession session);

    /// <summary>Puts the IPC token where this user's copy of the app will look for it.</summary>
    void PublishToken(uint uid);

    /// <summary>Lifts whatever the dead engine may have left pressed on the virtual keyboard.</summary>
    void ReleaseInput();
}

/// <summary>
/// Keeps one engine running, and tells it nothing it does not need to know.
///
/// The shape is <c>EngineSupervisor</c>'s on Windows: one look at the world per tick, at most one action,
/// and a branch that does nothing must be able to say why. What is different is how little there is to
/// do. The Windows engine had to be moved into whichever session held the screen; this one does not
/// follow the session at all -- it reads the screen through a descriptor and types through another, both
/// from the daemon -- so a lock, a sign-out or a user switch changes nothing about the engine. The one
/// thing that does follow the session is the IPC token: the signed-in user's copy of the app reads it out
/// of a directory that is theirs, so it is written again for whoever arrives at the screen.
///
/// And, for a signed-in Wayland desktop, a session agent: a small process running as that user, which the
/// engine uses to share the desktop through the portal while it is unlocked -- every monitor, input the way
/// the compositor means it -- falling back to the descriptor whenever the portal cannot (locked, signed out).
/// The agent follows the session, and is replaced with the engine, since its socket belongs to the engine
/// that took it.
/// </summary>
internal sealed class SessionEngineSupervisor : IDisposable
{
    /// <summary>How often the world is looked at. Nothing here needs to be faster than a person.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    /// <summary>The pause after an engine dies, so a failing one does not pin a core.</summary>
    public static readonly TimeSpan AfterFailure = TimeSpan.FromSeconds(5);

    /// <summary>How often a session whose desktop has not started yet is looked at again: each look reads every process.</summary>
    public static readonly TimeSpan DesktopWait = TimeSpan.FromSeconds(1);

    private readonly IEngineWorld _world;
    private readonly ILogger _log;
    private IEngineHandle? _engine;
    private IAgentHandle? _agent;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextAgentAttempt = DateTimeOffset.MinValue;
    private uint? _screenUid;
    private string? _idleBecause;
    private string? _desktopAwaited;

    public SessionEngineSupervisor(IEngineWorld world, ILogger log)
    {
        _world = world;
        _log = log;
    }

    /// <summary>Injectable, so a test can run the backoff without waiting for it.</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;

    public int? EnginePid => _engine?.ProcessId;

    public int? AgentPid => _agent?.ProcessId;

    /// <summary>
    /// Ticks until <paramref name="ct"/>, on one thread of its own for the supervisor's whole life.
    ///
    /// Not the pool's: every child asks the kernel for SIGTERM when its parent goes (ParentDeath), and to Linux the
    /// parent is the thread that forked it, not the process. Ticking on the pool, the first engine happened to be
    /// started on the main thread and lived; a session agent started from a later tick was killed 45 seconds after
    /// it started, when the pool retired the thread that had started it -- as an engine restarted after a crash
    /// could have been.
    /// </summary>
    public Task RunAsync(CancellationToken ct) =>
        Task.Factory.StartNew(() => Run(ct), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Tick();
                ct.WaitHandle.WaitOne(Interval);
            }
        }
        finally
        {
            StopEngine("the daemon is stopping");
        }
    }

    /// <summary>One look at the world, at most one action.</summary>
    internal void Tick()
    {
        ActiveSession? session = _world.ActiveSession();
        if (session is not null && session.Uid != _screenUid)
        {
            _screenUid = session.Uid;
            _log.LogInformation(
                "The screen is now session {Session} (uid {Uid}, {Kind}, {Role}); the engine does not follow it",
                session.Id, session.Uid, session.Kind, session.Role);

            // Not for the greeter: nothing of ours runs as gdm, and a token in its directory would only be
            // something for anything able to run as gdm to find.
            if (session.Role is SessionRole.User or SessionRole.LockScreen)
            {
                _world.PublishToken(session.Uid);
            }
        }

        if (_engine is { IsAlive: false } dead)
        {
            _log.LogWarning("Engine {Pid} exited with code {Code}", dead.ProcessId, dead.ExitCode);
            dead.Dispose();
            _engine = null;
            _world.ReleaseInput();
            _nextAttempt = Time.GetUtcNow() + AfterFailure;

            // Its agent's socket went with it; the next engine gets an agent of its own.
            StopAgent("its engine exited");
        }

        if (_engine is not null)
        {
            Working();
            TickAgent(session);
            return;
        }

        if (Time.GetUtcNow() < _nextAttempt)
        {
            Idle("waiting before starting the engine again");
            return;
        }

        _engine = _world.Launch();
        if (_engine is null)
        {
            _nextAttempt = Time.GetUtcNow() + AfterFailure;
            Idle("the engine could not be started");
            return;
        }

        Working();
        _log.LogInformation("Engine {Pid} started", _engine.ProcessId);
    }

    /// <summary>
    /// The agent's share of a tick, only while an engine runs: at most one action. Wanted for a signed-in Wayland
    /// desktop -- the lock screen is part of the same session, so a lock keeps it -- and for that session only.
    /// </summary>
    private void TickAgent(ActiveSession? session)
    {
        bool wanted = session is { Role: SessionRole.User, Kind: SessionKind.Wayland };
        if (_agent is { IsAlive: false } gone)
        {
            _log.LogWarning("Session agent {Pid} (session {Session}) exited with code {Code}", gone.ProcessId, gone.Session, gone.ExitCode);
            gone.Dispose();
            _agent = null;
            _nextAgentAttempt = Time.GetUtcNow() + AfterFailure;
            return;
        }

        if (_agent is not null && (!wanted || _agent.Session != session!.Id))
        {
            StopAgent(wanted ? $"session {session!.Id} is on the screen now" : "no signed-in Wayland desktop is on the screen");
            return;
        }

        if (!wanted || _agent is not null || Time.GetUtcNow() < _nextAgentAttempt)
        {
            return;
        }

        if (!_world.DesktopUp(session!))
        {
            // Started at the login, the agent said "desktop unknown", and a KDE desktop was then not known to never ask.
            _nextAgentAttempt = Time.GetUtcNow() + DesktopWait;
            if (_desktopAwaited != session!.Id)
            {
                _desktopAwaited = session.Id;
                _log.LogInformation("Session {Session} is on the screen; its agent starts once its desktop has", session.Id);
            }

            return;
        }

        _agent = _world.LaunchAgent(session!);
        if (_agent is null)
        {
            _nextAgentAttempt = Time.GetUtcNow() + AfterFailure;
            _log.LogWarning("Could not start a session agent for session {Session} (uid {Uid})", session!.Id, session.Uid);
            return;
        }

        _log.LogInformation("Session agent {Pid} started for session {Session} (uid {Uid})", _agent.ProcessId, session!.Id, session.Uid);
    }

    private void StopAgent(string why)
    {
        if (_agent is null)
        {
            return;
        }

        _log.LogInformation("Stopping session agent {Pid}: {Why}", _agent.ProcessId, why);
        _agent.Stop();
        _agent.Dispose();
        _agent = null;
    }

    private void Idle(string because)
    {
        if (_idleBecause != because)
        {
            _idleBecause = because;
            _log.LogInformation("Not running an engine: {Because}", because);
        }
    }

    private void Working()
    {
        if (_idleBecause is not null)
        {
            _log.LogInformation("The obstacle is gone: {Because}", _idleBecause);
            _idleBecause = null;
        }
    }

    private void StopEngine(string why)
    {
        if (_engine is null)
        {
            return;
        }

        StopAgent(why);
        _log.LogInformation("Stopping engine {Pid}: {Why}", _engine.ProcessId, why);
        _engine.Stop();
        _engine.Dispose();
        _engine = null;
        _world.ReleaseInput();
    }

    public void Dispose() => StopEngine("the supervisor is being disposed");
}
