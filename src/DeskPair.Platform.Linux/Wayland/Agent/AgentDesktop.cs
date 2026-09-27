using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Cursor;
using DeskPair.Platform.Abstractions.Input;
using DeskPair.Platform.Abstractions.Security;
using Microsoft.Extensions.Logging;

namespace DeskPair.Platform.Linux.Wayland.Agent;

/// <summary>
/// A signed-in user's Wayland desktop, shared through the portal on the engine's behalf: what
/// <see cref="ScanoutPortalHost"/> switches to while it can.
/// </summary>
internal interface ISharedDesktop : IDisplayEnumerator, IScreenCapturerFactory, IInputInjector, ICursorProvider, IDisplaySession
{
    /// <summary>Whose desktop it is: the key its remembered permission is kept under.</summary>
    uint Uid { get; }

    /// <summary>The session's XDG_CURRENT_DESKTOP, once its agent has said; empty before that.</summary>
    string Desktop { get; }

    /// <summary>Completes when the agent is gone: the user signed out, or it stopped.</summary>
    Task Gone { get; }

    /// <summary>How the compositor lays out the monitors, as the agent last said; null when it has not (a desktop that is not GNOME).</summary>
    DesktopLayout? Layout { get; }

    /// <summary>
    /// Asks the user on their screen, whatever was remembered, and remembers the answer: a session is started
    /// through the agent without the remembered permission, which shows the dialog, and closed again at once.
    /// </summary>
    Task<(DesktopSharingOutcome Outcome, string? Detail)> AskAsync(CancellationToken ct);
}

/// <summary>
/// The portal host of <see cref="PortalHost"/>, opening its sessions through a session agent: the user's uid keys
/// the permission and is whose lock is watched, and every session is the agent's. Everything else is the portal
/// host's own, so capture, input, the pointer and the lock handling are those of an engine in the user's session.
/// </summary>
internal sealed class AgentDesktop : ISharedDesktop
{
    private readonly AgentPortal _agent;
    private readonly PortalHost _host;
    private readonly PortalTokens _tokens;
    private readonly ILogger _log;

    private AgentDesktop(AgentPortal agent, PortalHost host, uint uid, PortalTokens tokens, ILogger log)
    {
        _agent = agent;
        _host = host;
        _tokens = tokens;
        _log = log;
        Uid = uid;
    }

    /// <summary>A desktop served by the agent on <paramref name="socket"/>, which this now owns.</summary>
    public static AgentDesktop Connect(int socket, uint uid, ISecretStore store, ILoggerFactory logs)
    {
        var agent = new AgentPortal(socket, logs.CreateLogger<AgentPortal>());
        ILogger log = logs.CreateLogger<AgentDesktop>();
        return new AgentDesktop(agent, new PortalHost(store, logs, clipboard: null, uid, agent.OpenAsync), uid, new PortalTokens(store, uid, log), log);
    }

    public async Task<(DesktopSharingOutcome Outcome, string? Detail)> AskAsync(CancellationToken ct)
    {
        try
        {
            await using IPortalSession session = await _agent.OpenAsync(_tokens, offerRestoreToken: false, ct).ConfigureAwait(false);
            bool everything = PortalPermission.StateOf(session.Devices) == PortalPermissionState.Allowed;
            _log.LogInformation("uid {Uid} allowed sharing their desktop {How}", Uid, everything ? "with the keyboard and the pointer" : "to watch only");
            return (everything ? DesktopSharingOutcome.Allowed : DesktopSharingOutcome.WatchOnly, null);
        }
        catch (PortalException e)
        {
            _log.LogInformation("uid {Uid} did not allow sharing their desktop: {Reason}: {Message}", Uid, e.Reason, e.Message);
            (PortalAskOutcome outcome, string? detail) = PortalPermission.OutcomeOf(e);
            return ((DesktopSharingOutcome)(int)outcome, detail);
        }
    }

    public uint Uid { get; }

    public string Desktop => _agent.Hello.IsCompletedSuccessfully ? _agent.Hello.Result.Desktop : string.Empty;

    public Task Gone => _agent.Gone;

    public DesktopLayout? Layout => _agent.Layout;

    public event EventHandler? DisplaysChanged
    {
        add => _host.DisplaysChanged += value;
        remove => _host.DisplaysChanged -= value;
    }

    public event Action<string>? Closed
    {
        add => _host.Closed += value;
        remove => _host.Closed -= value;
    }

    public event Action? Reopenable
    {
        add => _host.Reopenable += value;
        remove => _host.Reopenable -= value;
    }

    public bool IsOpen => _host.IsOpen;

    public Task<string?> OpenAsync(Action<string> progress, CancellationToken ct) => _host.OpenAsync(progress, ct);

    public ValueTask CloseAsync() => _host.CloseAsync();

    public IReadOnlyList<DisplayDescriptor> GetDisplays() => _host.GetDisplays();

    public IScreenCapturer Create(DisplayDescriptor display, bool preferGpu) => _host.Create(display, preferGpu);

    public InputCapabilities Capabilities => _host.Capabilities;

    public void EnsureInputDesktop() => _host.EnsureInputDesktop();

    public void InjectMouse(in MouseInput input, in VirtualScreenRect virtualScreen) => _host.InjectMouse(input, virtualScreen);

    public void InjectKey(in KeyInput input) => _host.InjectKey(input);

    public LockKeyStates GetLockKeyStates() => _host.GetLockKeyStates();

    public void SetLockKeyStates(LockKeyStates states) => _host.SetLockKeyStates(states);

    public void ReleaseAll() => _host.ReleaseAll();

    public void SendCtrlAltDel() => _host.SendCtrlAltDel();

    public void LockWorkstation() => _host.LockWorkstation();

    public ulong GetCurrentCursorId() => _host.GetCurrentCursorId();

    public CursorImage? GetCursorImage(ulong id) => _host.GetCursorImage(id);

    public (int X, int Y)? GetCursorPosition() => _host.GetCursorPosition();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync().ConfigureAwait(false);
        await _agent.DisposeAsync().ConfigureAwait(false);
    }
}
