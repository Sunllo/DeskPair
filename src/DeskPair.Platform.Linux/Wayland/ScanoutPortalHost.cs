using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Cursor;
using DeskPair.Platform.Abstractions.Input;
using DeskPair.Platform.Abstractions.Security;
using DeskPair.Platform.Linux.Capture.Drm;
using DeskPair.Platform.Linux.Native;
using DeskPair.Platform.Linux.Wayland.Agent;
using Microsoft.Extensions.Logging;

namespace DeskPair.Platform.Linux.Wayland;

/// <summary>
/// The unattended engine's screen on Linux: the display hardware always, and a signed-in user's Wayland desktop
/// through the portal whenever it can be had.
///
/// The hardware -- the daemon's scanout and virtual devices -- sees everything, the login screen and the lock screen
/// included, but only the first monitor, no pointer, and nothing a compositor keeps on other planes (KDE's panels).
/// The portal sees every monitor whole, with the pointer, and takes input the way the compositor means it, but only
/// in an unlocked session and only through the session agent the daemon starts as that user. So the portal is used
/// while it is open and the hardware the rest of the time, and a viewer who stays connected across a lock sees the
/// display list change, not their connection end: GNOME ends every share when it locks, the hardware shows the lock
/// screen and types the password into it, and on unlock the portal opens again.
///
/// The portal is only opened when it will not ask: with the permission the user gave once (remembered as a token
/// under their uid), or on a desktop that never asks (KDE Plasma 5). An unattended machine must not put a dialog in
/// front of whoever sits at it because somebody connected -- the hardware already shows the screen -- so an attempt
/// that starts asking anyway (a token the portal no longer honours) is called off at once, its dialog taken down,
/// the token forgotten, and the hardware kept until the user allows sharing again.
///
/// On the hardware a click needs placing: the picture is one monitor's scanout, but the daemon's virtual pointer is
/// spread by the compositor over the whole desktop. The session agent says how GNOME lays the monitors out, the
/// daemon says which connector the scanout is, and a point of the picture becomes that point of the desktop.
/// </summary>
public sealed class ScanoutPortalHost : IDisplayEnumerator, IScreenCapturerFactory, IInputInjector, ICursorProvider, IDisplaySession, IDesktopSharingConsent
{
    /// <summary>The scanout's displays are named <c>scanout-N</c>; any other is the portal's.</summary>
    private const string ScanoutPrefix = "scanout-";

    private readonly IDisplayEnumerator _scanout;
    private readonly IScreenCapturerFactory _scanoutCapture;
    private readonly IInputInjector _uinput;
    private readonly ICursorProvider _arrow;
    private readonly ISecretStore _store;
    private readonly ILogger _log;
    private readonly Func<(int Socket, uint Uid, string Session)?> _takeAgent;
    private readonly Func<int, uint, ISharedDesktop> _connect;
    private readonly Func<(uint Type, uint TypeId)?> _scanoutConnector;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private readonly Task _polling;
    private ISharedDesktop? _desktop;
    private bool _declined;
    private (DesktopLayout? Layout, (uint Type, uint TypeId)? Connector) _placing;

    /// <param name="channel">The daemon's channel, which session agents' sockets arrive on.</param>
    /// <param name="displays">The scanout's displays.</param>
    /// <param name="capturers">The scanout's capture.</param>
    /// <param name="input">The daemon's virtual keyboard and pointer.</param>
    /// <param name="cursor">The pointer as the hardware shows it: an arrow, nowhere.</param>
    /// <param name="store">Where a user's permission to share without asking is remembered, under their uid.</param>
    /// <param name="logs">Loggers.</param>
    public ScanoutPortalHost(
        DrmCaptureChannel channel, IDisplayEnumerator displays, IScreenCapturerFactory capturers, IInputInjector input, ICursorProvider cursor,
        ISecretStore store, ILoggerFactory logs)
        : this(displays, capturers, input, cursor, store, logs.CreateLogger<ScanoutPortalHost>(), channel.TakeAgent,
            (socket, uid) => AgentDesktop.Connect(socket, uid, store, logs), TimeSpan.FromSeconds(1), () => channel.Connector)
    {
    }

    /// <summary>
    /// The daemon's parts as functions, for tests: taking an agent's socket, connecting to it, and saying which
    /// connector the hardware's picture is of (<paramref name="scanoutConnector"/>, null before the first picture).
    /// </summary>
    internal ScanoutPortalHost(
        IDisplayEnumerator displays, IScreenCapturerFactory capturers, IInputInjector input, ICursorProvider cursor, ISecretStore store, ILogger log,
        Func<(int Socket, uint Uid, string Session)?> takeAgent, Func<int, uint, ISharedDesktop> connect, TimeSpan agentPoll,
        Func<(uint Type, uint TypeId)?>? scanoutConnector = null)
    {
        _scanout = displays;
        _scanoutCapture = capturers;
        _uinput = input;
        _arrow = cursor;
        _store = store;
        _log = log;
        _takeAgent = takeAgent;
        _connect = connect;
        _scanoutConnector = scanoutConnector ?? (static () => null);
        _scanout.DisplaysChanged += OnScanoutDisplaysChanged;
        _polling = PollAgentsAsync(agentPoll, _stop.Token);
    }

    public event EventHandler? DisplaysChanged;

    /// <summary>Never raised: when the portal closes by itself the hardware takes over, and nothing has stopped.</summary>
    public event Action<string>? Closed
    {
        add { }
        remove { }
    }

    public event Action? Reopenable;

    /// <summary>Whether the portal is open; the hardware needs no opening.</summary>
    public bool IsOpen => OpenDesktop() is not null;

    // ---- IDisplaySession ----

    /// <summary>
    /// Opens the portal when there is an agent and it will not ask; otherwise, and whatever happens, the hardware
    /// serves. Never a failure for the viewers: they are looking at the screen either way.
    /// </summary>
    public async Task<string?> OpenAsync(Action<string> progress, CancellationToken ct)
    {
        ISharedDesktop? desktop;
        bool declined;
        lock (_lock)
        {
            desktop = _desktop;
            declined = _declined;
        }

        if (desktop is null || desktop.IsOpen || declined ||
            (!NeverAsks(desktop) && await new PortalTokens(_store, desktop.Uid, _log).LoadAsync(ct).ConfigureAwait(false) is null))
        {
            return null;
        }

        bool asking = false;
        string? why = await desktop.OpenAsync(
            notice =>
            {
                if (notice == PortalHost.Asking)
                {
                    // A dialog is up on the machine's screen: take it down; the hardware shows the screen anyway.
                    asking = true;
                    _ = desktop.CloseAsync();
                }
            },
            ct).ConfigureAwait(false);

        if (asking)
        {
            lock (_lock)
            {
                _declined = ReferenceEquals(_desktop, desktop) || _declined;
            }

            await new PortalTokens(_store, desktop.Uid, _log).SaveAsync(null).ConfigureAwait(false);
            _log.LogWarning(
                "The portal started asking uid {Uid} whether to share the screen, so it was called off: the remembered permission no longer holds. " +
                "The display hardware is used until sharing is allowed again (Settings › Security).", desktop.Uid);
            return null;
        }

        if (why is null)
        {
            _log.LogInformation("Sharing uid {Uid}'s desktop through the portal", desktop.Uid);
        }
        else
        {
            _log.LogInformation("The portal did not open ({Why}); the display hardware serves", why);
        }

        return null;
    }

    public async ValueTask CloseAsync()
    {
        if (Current() is { } desktop)
        {
            await desktop.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A desktop that never asks: KDE Plasma 5, which only says that it is sharing.</summary>
    private static bool NeverAsks(ISharedDesktop desktop) => desktop.Desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase);

    // ---- IDesktopSharingConsent: the permission, from the settings page of the user's own app ----

    public async Task<DesktopSharingState> SharingStateAsync(uint uid, CancellationToken ct)
    {
        if (Current() is not { } desktop || desktop.Uid != uid)
        {
            return DesktopSharingState.Unavailable;
        }

        var tokens = new PortalTokens(_store, uid, _log);
        if (await tokens.LoadAsync(ct).ConfigureAwait(false) is null)
        {
            return NeverAsks(desktop) ? DesktopSharingState.Allowed : DesktopSharingState.NotYet;
        }

        uint devices = await tokens.LoadDevicesAsync(ct).ConfigureAwait(false) ?? (Portal.DeviceKeyboard | Portal.DevicePointer);
        return PortalPermission.StateOf(devices) == PortalPermissionState.Allowed ? DesktopSharingState.Allowed : DesktopSharingState.WatchOnly;
    }

    /// <summary>
    /// Asks the user on their own screen, through their session agent, and remembers the answer. Not while a viewer is
    /// connected through the portal: the agent holds one session at a time, and that one already has the permission.
    /// </summary>
    public async Task<(DesktopSharingOutcome Outcome, string? Detail)> AskSharingAsync(uint uid, CancellationToken ct)
    {
        if (Current() is not { } desktop || desktop.Uid != uid)
        {
            return (DesktopSharingOutcome.Failed, "DeskPair's service is not serving this desktop yet. Wait a few seconds after signing in, and try again.");
        }

        if (desktop.IsOpen)
        {
            return (DesktopSharingOutcome.Failed, "Somebody is connected through it right now. Try again when nobody is.");
        }

        (DesktopSharingOutcome outcome, string? detail) = await desktop.AskAsync(ct).ConfigureAwait(false);
        if (outcome is DesktopSharingOutcome.Allowed or DesktopSharingOutcome.WatchOnly)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_desktop, desktop))
                {
                    _declined = false;
                }
            }

            // Anybody watching the hardware meanwhile moves to the portal.
            Reopenable?.Invoke();
        }

        return (outcome, detail);
    }

    // ---- the session agent ----

    private async Task PollAgentsAsync(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            (int Socket, uint Uid, string Session)? offer;
            try
            {
                offer = _takeAgent();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                _log.LogDebug(e, "Asking the daemon for a session agent failed");
                continue;
            }

            if (offer is { } agent)
            {
                await AdoptAsync(agent.Socket, agent.Uid, agent.Session).ConfigureAwait(false);
            }
        }
    }

    /// <summary>A new agent's desktop replaces the last one; viewers already connected are moved to it if it opens.</summary>
    private async Task AdoptAsync(int socket, uint uid, string session)
    {
        ISharedDesktop next = _connect(socket, uid);
        next.Closed += OnPortalClosed;
        next.Reopenable += OnPortalReopenable;
        next.DisplaysChanged += OnPortalDisplaysChanged;
        ISharedDesktop? previous;
        lock (_lock)
        {
            previous = _desktop;
            _desktop = next;
            _declined = false;
        }

        _log.LogInformation("A session agent serves uid {Uid}'s session {Session}", uid, session);
        _ = next.Gone.ContinueWith(_ => Forget(next), TaskScheduler.Default);
        if (previous is not null)
        {
            await RetireAsync(previous).ConfigureAwait(false);
        }

        Reopenable?.Invoke();
    }

    /// <summary>The agent went (the user signed out): its desktop is no longer something to open.</summary>
    private void Forget(ISharedDesktop gone)
    {
        bool current;
        lock (_lock)
        {
            current = ReferenceEquals(_desktop, gone);
            if (current)
            {
                _desktop = null;
            }
        }

        if (current)
        {
            _log.LogInformation("The session agent for uid {Uid} has gone; the display hardware serves", gone.Uid);
            _ = RetireAsync(gone);
        }
    }

    private async Task RetireAsync(ISharedDesktop desktop)
    {
        desktop.Closed -= OnPortalClosed;
        desktop.Reopenable -= OnPortalReopenable;
        desktop.DisplaysChanged -= OnPortalDisplaysChanged;
        bool wasOpen = desktop.IsOpen;
        await desktop.DisposeAsync().ConfigureAwait(false);
        if (wasOpen)
        {
            DisplaysChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The portal ended by itself -- GNOME locked, the person stopped sharing, the user signed out: the hardware takes over.</summary>
    private void OnPortalClosed(string reason)
    {
        _log.LogInformation("The portal closed ({Reason}); the display hardware serves", reason);
        DisplaysChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPortalReopenable() => Reopenable?.Invoke();

    private void OnPortalDisplaysChanged(object? sender, EventArgs e)
    {
        if (IsOpen)
        {
            DisplaysChanged?.Invoke(this, e);
        }
    }

    private void OnScanoutDisplaysChanged(object? sender, EventArgs e)
    {
        if (!IsOpen)
        {
            DisplaysChanged?.Invoke(this, e);
        }
    }

    // ---- IDisplayEnumerator and IScreenCapturerFactory ----

    public IReadOnlyList<DisplayDescriptor> GetDisplays() => OpenDesktop()?.GetDisplays() ?? _scanout.GetDisplays();

    public IScreenCapturer Create(DisplayDescriptor display, bool preferGpu)
    {
        if (display.Name.StartsWith(ScanoutPrefix, StringComparison.Ordinal))
        {
            return _scanoutCapture.Create(display, preferGpu);
        }

        return (OpenDesktop() ?? throw new InvalidOperationException($"{display.Name} is a portal display, and the portal is closed"))
            .Create(display, preferGpu);
    }

    // ---- IInputInjector ----

    public InputCapabilities Capabilities => OpenDesktop()?.Capabilities ?? _uinput.Capabilities;

    public void EnsureInputDesktop() => Input().EnsureInputDesktop();

    public void InjectMouse(in MouseInput input, in VirtualScreenRect virtualScreen)
    {
        if (OpenDesktop() is { } desktop)
        {
            desktop.InjectMouse(input, virtualScreen);
        }
        else if (input.Action is MouseAction.Move or MouseAction.Down or MouseAction.Up && OnDesktop(input.X, input.Y, virtualScreen) is { } placed)
        {
            _uinput.InjectMouse(input with { X = placed.X, Y = placed.Y }, placed.Desktop);
        }
        else
        {
            _uinput.InjectMouse(input, virtualScreen);
        }
    }

    /// <summary>
    /// A point of the hardware's picture as that point of the whole desktop the virtual pointer spans, with the desktop
    /// as the virtual screen: when the user's session agent has said how the compositor lays the monitors out and the
    /// scanout's monitor is among them. Null otherwise -- the login screen, a desktop that is not GNOME -- and the
    /// picture is then taken to be the whole desktop, which it is with one monitor.
    /// </summary>
    private (int X, int Y, VirtualScreenRect Desktop)? OnDesktop(int x, int y, in VirtualScreenRect picture)
    {
        DesktopLayout? layout = Current()?.Layout;
        (uint Type, uint TypeId)? connector = layout is null ? null : _scanoutConnector();
        LayoutMonitor? monitor = connector is { } c ? layout!.Find(c.Type, c.TypeId) : null;
        (int X, int Y, VirtualScreenRect Desktop)? placed = monitor is { } m ? layout!.Place(m, x, y, picture) : null;
        if (layout is not null && _placing != (layout, connector))
        {
            // Said once for each layout, so the log shows where clicks on the lock screen are being put.
            _placing = (layout, connector);
            if (placed is { } p)
            {
                _log.LogInformation("Clicks on the hardware's picture go to {Monitor} on a desktop of {Width}x{Height}", monitor, p.Desktop.Width, p.Desktop.Height);
            }
            else
            {
                _log.LogInformation(
                    "Clicks on the hardware's picture cannot be placed on the desktop ({Layout}): the scanout is connector {Connector} at {PictureWidth}x{PictureHeight}",
                    layout, connector is { } k ? $"{Drm.ConnectorTypeName(k.Type)}-{k.TypeId}" : "unknown", picture.Width, picture.Height);
            }
        }

        return placed;
    }

    public void InjectKey(in KeyInput input) => Input().InjectKey(input);

    public LockKeyStates GetLockKeyStates() => Input().GetLockKeyStates();

    public void SetLockKeyStates(LockKeyStates states) => Input().SetLockKeyStates(states);

    /// <summary>Both: a key pressed through one and released after the switch would otherwise stay down in the other.</summary>
    public void ReleaseAll()
    {
        OpenDesktop()?.ReleaseAll();
        _uinput.ReleaseAll();
    }

    public void SendCtrlAltDel() => Input().SendCtrlAltDel();

    /// <summary>Always the hardware's: the daemon locks the seat as root, which the portal cannot, and it works whatever is shown.</summary>
    public void LockWorkstation() => _uinput.LockWorkstation();

    // ---- ICursorProvider ----

    public ulong GetCurrentCursorId() => Pointer().GetCurrentCursorId();

    public CursorImage? GetCursorImage(ulong id) => Pointer().GetCursorImage(id);

    public (int X, int Y)? GetCursorPosition() => Pointer().GetCursorPosition();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _polling.ConfigureAwait(false);
        _scanout.DisplaysChanged -= OnScanoutDisplaysChanged;
        ISharedDesktop? desktop;
        lock (_lock)
        {
            desktop = _desktop;
            _desktop = null;
        }

        if (desktop is not null)
        {
            await RetireAsync(desktop).ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    private ISharedDesktop? Current()
    {
        lock (_lock)
        {
            return _desktop;
        }
    }

    /// <summary>The user's desktop while the portal is open; null while the hardware serves.</summary>
    private ISharedDesktop? OpenDesktop() => Current() is { IsOpen: true } open ? open : null;

    private IInputInjector Input() => OpenDesktop() ?? _uinput;

    private ICursorProvider Pointer() => OpenDesktop() ?? _arrow;
}
