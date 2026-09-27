using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Clipboard;
using DeskPair.Platform.Abstractions.Cursor;
using DeskPair.Platform.Abstractions.Input;
using DeskPair.Platform.Abstractions.Security;
using DeskPair.Platform.Linux.Capture;
using DeskPair.Platform.Linux.Native;
using Microsoft.Extensions.Logging;

namespace DeskPair.Platform.Linux.Wayland;

/// <summary>
/// A Wayland desktop as the engine's platform: displays, capture, input and the pointer, all through one portal
/// session that is open only while somebody is watching.
///
/// Held open for the engine's lifetime the session would keep GNOME's "being shared" indicator lit on a desk
/// nobody is connected to, and without a remembered permission it would ask the person at the machine the moment
/// they signed in. So it opens when the first viewer is let in (<see cref="IDisplaySession.OpenAsync"/>, from the
/// media module) and closes when the last one leaves; in between, the engine sees an empty display list, input
/// goes nowhere and the pointer has no position. When the person stops sharing from the top bar, the session ends
/// by itself and <see cref="Closed"/> says so, so viewers are told rather than left looking at a frozen picture.
/// </summary>
public sealed class PortalHost : IDisplayEnumerator, IScreenCapturerFactory, IInputInjector, ICursorProvider, IDisplaySession
{
    /// <summary>
    /// How long opening may take before it is worth telling viewers that somebody is being asked. A remembered
    /// permission opens in milliseconds (plus the streams' first pictures); a dialog takes as long as a person.
    /// </summary>
    private static readonly TimeSpan AskingAfter = TimeSpan.FromSeconds(1.5);

    private const int CachedShapes = 64;

    /// <summary>How often a share that a lock ended asks whether the screen is unlocked yet.</summary>
    private static readonly TimeSpan UnlockPoll = TimeSpan.FromSeconds(2);

    /// <summary>What viewers are told when a lock ended the share: nobody stopped it, and it comes back.</summary>
    internal const string Locked = "The remote computer's screen is locked. The picture comes back when it is unlocked there.";

    private readonly PortalTokens _tokens;
    private readonly ILoggerFactory _logs;
    private readonly ILogger _log;
    private readonly IClipboard? _clipboard;
    private readonly PortalOpener _opener;
    private readonly ScanoutCursorProvider _arrow = new();
    private readonly object _lock = new();
    private readonly Dictionary<ulong, CursorImage> _shapes = [];
    private Opened? _open;
    private Task<string?>? _opening;
    private CancellationTokenSource? _openingCancel;
    private CancellationTokenSource? _unlockWatch;
    private readonly uint _uid;
    private ulong _shapeId;
    private bool _pointerSeen;
    private bool _disposed;

    /// <param name="store">Where the portal's permission is remembered, keyed by the account this runs as.</param>
    /// <param name="logs">Loggers for the session, the capture and the input.</param>
    /// <param name="clipboard">The desktop's clipboard, for typing what the keyboard layout cannot.</param>
    public PortalHost(ISecretStore store, ILoggerFactory logs, IClipboard? clipboard = null)
        : this(store, logs, clipboard, LibC.geteuid(), OpenHere)
    {
    }

    /// <param name="store">Where the portal's permission is remembered, keyed by <paramref name="uid"/>.</param>
    /// <param name="logs">Loggers for the session, the capture and the input.</param>
    /// <param name="clipboard">The desktop's clipboard, for typing what the keyboard layout cannot.</param>
    /// <param name="uid">
    /// Whose desktop this is: whose permission is remembered, and whose session's lock is watched. The process's own
    /// account, except for the unattended engine, which runs as another account on the user's behalf.
    /// </param>
    /// <param name="opener">How a session is started: on this process's own session bus, or through the user's session agent.</param>
    internal PortalHost(ISecretStore store, ILoggerFactory logs, IClipboard? clipboard, uint uid, PortalOpener opener)
    {
        _logs = logs;
        _log = logs.CreateLogger<PortalHost>();
        _clipboard = clipboard;
        _uid = uid;
        _opener = opener;
        _tokens = new PortalTokens(store, _uid, _log);
    }

    /// <summary>A session on this process's own session bus.</summary>
    private static async Task<IPortalSession> OpenHere(PortalTokens tokens, ILogger log, CancellationToken ct) =>
        await PortalSession.OpenAsync(tokens, log, null, ct).ConfigureAwait(false);

    /// <summary>Why this build cannot read a Wayland screen at all (no shim, no libpipewire), or null when it can.</summary>
    public static string? Unavailable => WaylandShim.IsAvailable ? null : WaylandShim.UnavailableReason;

    public event EventHandler? DisplaysChanged;

    public event Action<string>? Closed;

    public event Action? Reopenable;

    public bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                return _open is not null;
            }
        }
    }

    // ---- IDisplaySession ----

    public async Task<string?> OpenAsync(Action<string> progress, CancellationToken ct)
    {
        Task<string?> attempt;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_open is not null)
            {
                return null;
            }

            if (_opening is null || _opening.IsCompleted)
            {
                // One attempt for everybody arriving meanwhile. Its own cancellation, not a caller's: one viewer
                // leaving must not take the dialog away from another still waiting; CloseAsync takes it away.
                _openingCancel = new CancellationTokenSource();
                _opening = OpenCoreAsync(progress, _openingCancel.Token);
            }

            attempt = _opening;
        }

        return await attempt.WaitAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask CloseAsync()
    {
        Opened? open;
        CancellationTokenSource? cancel;
        CancellationTokenSource? unlock;
        lock (_lock)
        {
            open = _open;
            _open = null;
            cancel = _openingCancel;
            _openingCancel = null;
            unlock = _unlockWatch;
            _unlockWatch = null;
        }

        // Nobody left to share an unlocked screen with.
        if (unlock is not null)
        {
            await unlock.CancelAsync().ConfigureAwait(false);
            unlock.Dispose();
        }

        // An attempt still asking the person is called off, which takes its dialog down.
        if (cancel is not null)
        {
            await cancel.CancelAsync().ConfigureAwait(false);
            cancel.Dispose();
        }

        if (open is not null)
        {
            await CloseAsync(open).ConfigureAwait(false);
            _log.LogInformation("Screen sharing closed: nobody is watching");
        }
    }

    private async Task<string?> OpenCoreAsync(Action<string> progress, CancellationToken ct)
    {
        if (!WaylandShim.IsAvailable)
        {
            return $"This computer's DeskPair cannot read a Wayland screen: {WaylandShim.UnavailableReason}.";
        }

        Task<IPortalSession> starting = _opener(_tokens, _logs.CreateLogger<PortalSession>(), ct);
        if (await Task.WhenAny(starting, Task.Delay(AskingAfter, CancellationToken.None)).ConfigureAwait(false) != starting)
        {
            progress(Asking);
        }

        IPortalSession session;
        try
        {
            session = await starting.ConfigureAwait(false);
        }
        catch (PortalException e)
        {
            _log.LogWarning("Screen sharing did not start: {Reason}: {Message}", e.Reason, e.Message);
            return Describe(e);
        }
        catch (OperationCanceledException)
        {
            return "Screen sharing was not started: nobody is connected any more.";
        }

        PortalCapture capture;
        try
        {
            capture = await PortalCapture.OpenAsync(session, _logs, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or PortalException)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            _log.LogWarning(e, "Screen sharing started but its picture cannot be read");
            return $"Screen sharing started, but its picture cannot be read here: {e.Message}";
        }

        var opened = new Opened(session, capture, new PortalInputInjector(session, capture, _logs.CreateLogger<PortalInputInjector>(), _clipboard));
        bool keep;
        lock (_lock)
        {
            keep = !ct.IsCancellationRequested && !_disposed;
            if (keep)
            {
                _open = opened;
            }
        }

        if (!keep)
        {
            // Everybody left while the machine was being asked: nothing is shared for nobody.
            await CloseAsync(opened).ConfigureAwait(false);
            return "Screen sharing was not started: nobody is connected any more.";
        }

        capture.DisplaysChanged += OnCaptureDisplaysChanged;
        _ = WatchAsync(opened);
        return null;
    }

    /// <summary>The portal ended the session without being asked: the person stopped sharing, or the compositor went.</summary>
    private async Task WatchAsync(Opened opened)
    {
        await opened.Session.Closed.ConfigureAwait(false);
        bool ours;
        lock (_lock)
        {
            ours = ReferenceEquals(_open, opened);
            if (ours)
            {
                _open = null;
            }
        }

        if (!ours)
        {
            return;
        }

        _log.LogInformation("The portal ended screen sharing");
        await CloseAsync(opened).ConfigureAwait(false);
        DisplaysChanged?.Invoke(this, EventArgs.Empty);
        if (await LockedAsync().ConfigureAwait(false))
        {
            // GNOME ends every screen share the moment it locks. Nobody stopped this one, and it can come back by
            // itself once the screen is unlocked -- with the permission remembered, without asking anybody.
            _log.LogInformation("The screen is locked; sharing resumes when it is unlocked");
            Closed?.Invoke(Locked);
            WatchForUnlock();
            return;
        }

        Closed?.Invoke("The person at the remote computer stopped sharing its screen.");
    }

    /// <summary>
    /// Whether the session is locked, asked a second time a moment later when the answer is no: the portal can end
    /// the share just before the lock screen is recorded as up.
    /// </summary>
    private async Task<bool> LockedAsync()
    {
        bool? locked = await SessionLock.IsLockedAsync(_uid, _log).ConfigureAwait(false);
        if (locked == false)
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            locked = await SessionLock.IsLockedAsync(_uid, _log).ConfigureAwait(false);
        }

        return locked == true;
    }

    private void WatchForUnlock()
    {
        var watch = new CancellationTokenSource();
        CancellationTokenSource? previous;
        lock (_lock)
        {
            if (_disposed)
            {
                watch.Dispose();
                return;
            }

            previous = _unlockWatch;
            _unlockWatch = watch;
        }

        previous?.Cancel();
        previous?.Dispose();
        _ = WaitForUnlockAsync(watch.Token);
    }

    /// <summary>Asks every <see cref="UnlockPoll"/> until the screen is unlocked, then says the share can resume.</summary>
    private async Task WaitForUnlockAsync(CancellationToken ct)
    {
        try
        {
            do
            {
                await Task.Delay(UnlockPoll, ct).ConfigureAwait(false);
            }
            while (await SessionLock.IsLockedAsync(_uid, _log, ct).ConfigureAwait(false) != false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _log.LogInformation("The screen is unlocked; sharing can resume");
        Reopenable?.Invoke();
    }

    private async Task CloseAsync(Opened opened)
    {
        opened.Capture.DisplaysChanged -= OnCaptureDisplaysChanged;
        await opened.Input.DisposeAsync().ConfigureAwait(false);
        await opened.Session.DisposeAsync().ConfigureAwait(false);
    }

    private void OnCaptureDisplaysChanged(object? sender, EventArgs e) => DisplaysChanged?.Invoke(this, e);

    /// <summary>What a viewer is told while the machine asks its person.</summary>
    internal const string Asking = "The remote computer is asking on its own screen whether to share it. Waiting for someone there to allow it.";

    /// <summary>A failed start in words for a viewer.</summary>
    internal static string Describe(PortalException e) => e.Reason switch
    {
        PortalFailure.Refused => "The person at the remote computer declined to share its screen.",
        PortalFailure.TimedOut => "The remote computer asked on its own screen whether to share it, and nobody answered.",
        PortalFailure.Unavailable => $"The remote computer cannot share its Wayland screen: {e.Message}",
        _ => $"The remote computer's screen sharing did not start: {e.Message}",
    };

    // ---- IDisplayEnumerator and IScreenCapturerFactory ----

    public IReadOnlyList<DisplayDescriptor> GetDisplays() => Current()?.Capture.GetDisplays() ?? [];

    public IScreenCapturer Create(DisplayDescriptor display, bool preferGpu) =>
        (Current() ?? throw new InvalidOperationException("Screen sharing is not open; there is no display to capture."))
            .Capture.Create(display, preferGpu);

    // ---- IInputInjector ----

    /// <summary>What the person allowed once open; before that, what a session will almost certainly allow.</summary>
    public InputCapabilities Capabilities =>
        Current()?.Input.Capabilities ?? (InputCapabilities.Mouse | InputCapabilities.Keyboard | InputCapabilities.LockScreen);

    public void EnsureInputDesktop()
    {
    }

    public void InjectMouse(in MouseInput input, in VirtualScreenRect virtualScreen) => Current()?.Input.InjectMouse(input, virtualScreen);

    public void InjectKey(in KeyInput input) => Current()?.Input.InjectKey(input);

    public LockKeyStates GetLockKeyStates() => default;

    public void SetLockKeyStates(LockKeyStates states)
    {
    }

    public void ReleaseAll() => Current()?.Input.ReleaseAll();

    public void SendCtrlAltDel() => Current()?.Input.SendCtrlAltDel();

    /// <summary>Locking needs no portal: logind locks the session for its owner whether or not it is being shared.</summary>
    public void LockWorkstation()
    {
        if (Current() is { } open)
        {
            open.Input.LockWorkstation();
            return;
        }

        if (Hosting.LinuxSessions.Active() is { Role: Hosting.SessionRole.User } active && !Hosting.LinuxSessions.Lock(active.Id))
        {
            _log.LogWarning("logind refused to lock session {Session}", active.Id);
        }
    }

    // ---- ICursorProvider ----

    /// <summary>The shape last seen on a shared monitor; the ordinary arrow until one has been.</summary>
    public ulong GetCurrentCursorId()
    {
        Refresh();
        lock (_lock)
        {
            return _shapeId != 0 ? _shapeId : ScanoutCursorProvider.ArrowId;
        }
    }

    public CursorImage? GetCursorImage(ulong id)
    {
        lock (_lock)
        {
            if (_shapes.TryGetValue(id, out CursorImage? image))
            {
                return image;
            }
        }

        return id == ScanoutCursorProvider.ArrowId ? _arrow.GetCursorImage(id) : null;
    }

    public (int X, int Y)? GetCursorPosition() => Refresh();

    private (int X, int Y)? Refresh()
    {
        if (Current()?.Capture.Cursor() is not { } cursor)
        {
            if (_pointerSeen)
            {
                _pointerSeen = false;
                _log.LogDebug("The pointer is on no shared screen");
            }

            return null;
        }

        if (!_pointerSeen)
        {
            _pointerSeen = true;
            _log.LogDebug("The pointer is on a shared screen at {X},{Y}", cursor.X, cursor.Y);
        }

        lock (_lock)
        {
            if (!_shapes.ContainsKey(cursor.Shape.Id))
            {
                if (_shapes.Count >= CachedShapes)
                {
                    _shapes.Clear();
                }

                _shapes[cursor.Shape.Id] = cursor.Shape;
            }

            if (_shapeId != cursor.Shape.Id)
            {
                _log.LogDebug("Pointer shape {Id:x}: {Width}x{Height}, hotspot {HotX},{HotY}", cursor.Shape.Id, cursor.Shape.Width, cursor.Shape.Height, cursor.Shape.HotX, cursor.Shape.HotY);
            }

            _shapeId = cursor.Shape.Id;
        }

        return (cursor.X, cursor.Y);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            _disposed = true;
        }

        await CloseAsync().ConfigureAwait(false);
        await _arrow.DisposeAsync().ConfigureAwait(false);
    }

    private Opened? Current()
    {
        lock (_lock)
        {
            return _open;
        }
    }

    /// <summary>An open portal session and everything that hangs off it.</summary>
    private sealed record Opened(IPortalSession Session, PortalCapture Capture, PortalInputInjector Input);
}
