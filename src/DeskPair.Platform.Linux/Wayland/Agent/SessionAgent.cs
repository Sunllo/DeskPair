using DeskPair.Platform.Abstractions.Security;
using DeskPair.Platform.Linux.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace DeskPair.Platform.Linux.Wayland.Agent;

/// <summary>Opens a portal session for the agent: on the user's session bus, or a fake in a test.</summary>
internal delegate Task<IPortalSession> AgentPortalOpener(PortalTokens tokens, bool offerRestoreToken, ILogger log, CancellationToken ct);

/// <summary>Reports how the compositor lays out its monitors, when read and whenever it changes, until cancelled: mutter's, or a fake in a test.</summary>
internal delegate Task LayoutWatcher(Action<DesktopLayout> report, ILogger log, CancellationToken ct);

/// <summary>
/// The session agent: the one part of the unattended host that runs as the signed-in user, in their session.
///
/// The engine runs as its own account and cannot call the portal on somebody else's session bus. So the daemon starts
/// this, as the user, with one end of a socketpair whose other end the engine holds, and it does what only the user
/// can: it opens the portal when the engine asks, hands the engine a PipeWire connection for each stream (the engine
/// reads the pictures itself), passes the engine's input on to the portal, and says when the portal ended the session.
///
/// It keeps nothing: the permission to remember comes with each request and goes back with each answer, and the
/// engine stores it. One session at a time; the engine's socket closing ends the agent.
///
/// Unasked, it also tells the engine how the compositor lays out the monitors, and again whenever that changes: the
/// engine reads the lock screen from one monitor's scanout, and places the pointer on the whole desktop.
/// </summary>
public sealed class SessionAgent
{
    private readonly int _socket;
    private readonly AgentPortalOpener _open;
    private readonly LayoutWatcher _layouts;
    private readonly ILogger _log;
    private readonly string _version;
    private readonly object _gate = new();
    private readonly object _sending = new();
    private IPortalSession? _session;
    private CancellationTokenSource? _opening;
    private DesktopLayout? _layout;

    internal SessionAgent(int socket, AgentPortalOpener open, ILogger log, string version, LayoutWatcher? layouts = null)
    {
        _socket = socket;
        _open = open;
        _layouts = layouts ?? (static (_, _, _) => Task.CompletedTask);
        _log = log;
        _version = version;
    }

    /// <summary>The <c>--session-agent</c> role: serves the engine on <paramref name="socket"/> until it goes.</summary>
    public static Task RunAsync(int socket, ILoggerFactory logs, string version, CancellationToken ct) =>
        new SessionAgent(socket, OpenOnSessionBus, logs.CreateLogger("agent"), version, MutterDisplayConfig.WatchAsync).RunAsync(ct);

    private static async Task<IPortalSession> OpenOnSessionBus(PortalTokens tokens, bool offerRestoreToken, ILogger log, CancellationToken ct) =>
        await PortalSession.OpenAsync(tokens, log, null, ct, offerRestoreToken).ConfigureAwait(false);

    internal async Task RunAsync(CancellationToken ct)
    {
        var hello = new AgentHello(LibC.geteuid(), Environment.ProcessId, Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty, _version);
        Send(buffer => AgentWire.WriteHello(buffer, hello), []);
        _log.LogInformation("Session agent {Version} for uid {Uid} ({Desktop}) serving the engine", _version, hello.Uid, hello.Desktop.Length > 0 ? hello.Desktop : "desktop unknown");

        // recvmsg blocks, so it has a thread of its own; everything it hands out runs on the pool.
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Thread(() =>
        {
            try
            {
                ReceiveAll();
            }
            finally
            {
                ended.TrySetResult();
            }
        })
        {
            IsBackground = true,
            Name = "agent-receive",
        };
        reader.Start();

        using var watching = new CancellationTokenSource();
        Task layouts = WatchLayoutAsync(watching.Token);
        using (ct.Register(() => UnixSocketMsg.shutdown(_socket, UnixSocketMsg.ShutRdWr)))
        {
            await ended.Task.ConfigureAwait(false);
        }

        await watching.CancelAsync().ConfigureAwait(false);
        await layouts.ConfigureAwait(false);
        await CloseAsync().ConfigureAwait(false);
        _log.LogInformation("Session agent stopping: the engine is gone");
    }

    private async Task WatchLayoutAsync(CancellationToken ct)
    {
        try
        {
            await _layouts(Report, _log, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Only the pointer on a lock screen of several monitors depends on it; the agent's work goes on without.
            _log.LogWarning(e, "The monitor layout is no longer watched");
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>A layout for the engine, when it is not the one it already has.</summary>
    private void Report(DesktopLayout layout)
    {
        lock (_gate)
        {
            if (_layout is not null && _layout.Monitors.SequenceEqual(layout.Monitors))
            {
                return;
            }

            _layout = layout;
        }

        _log.LogInformation("The desktop's monitors: {Layout}", layout);
        Send(buffer => AgentWire.WriteLayout(buffer, layout), []);
    }

    private void ReceiveAll()
    {
        byte[] message = new byte[AgentWire.MaxMessage];
        Span<int> fds = stackalloc int[UnixSocketMsg.MaxFds];
        while (true)
        {
            int length;
            int fdCount;
            try
            {
                length = UnixSocketMsg.Receive(_socket, message, fds, out fdCount);
            }
            catch (IOException e)
            {
                _log.LogInformation("The engine's socket failed: {Error}", e.Message);
                return;
            }

            // The engine sends none; one that arrives anyway is closed rather than kept.
            for (int k = 0; k < fdCount; k++)
            {
                _ = UnixSocketMsg.close(fds[k]);
            }

            if (length == 0)
            {
                return;
            }

            Handle(message.AsSpan(0, length));
        }
    }

    private void Handle(ReadOnlySpan<byte> message)
    {
        if (!AgentWire.TryReadHeader(message, out byte kind, out _, out uint id))
        {
            _log.LogWarning("An unreadable {Length}-byte message from the engine", message.Length);
            return;
        }

        switch (kind)
        {
            case AgentWire.KindOpen when AgentWire.TryReadOpen(message, out string? token, out bool offer):
                _ = OpenAsync(id, token, offer);
                break;

            case AgentWire.KindOpenRemote:
                _ = OpenRemoteAsync(id);
                break;

            case AgentWire.KindInput when AgentWire.TryReadInput(message, out PortalSession.InputEvent e):
                Deliver(e);
                break;

            case AgentWire.KindClose:
                _ = CloseAsync();
                break;

            default:
                _log.LogWarning("An unreadable {Length}-byte message of kind {Kind} from the engine", message.Length, kind);
                break;
        }
    }

    private async Task OpenAsync(uint id, string? token, bool offerRestoreToken)
    {
        var cancel = new CancellationTokenSource();
        lock (_gate)
        {
            if (_session is not null || _opening is not null)
            {
                cancel.Dispose();
                Failed(id, PortalFailure.Failed, "A screen-sharing session is already open here.");
                return;
            }

            _opening = cancel;
        }

        // The permission is the engine's; it lives here only for as long as the portal needs to read and replace it.
        var tokens = new PortalTokens(new MemorySecretStore(), 0, _log);
        await tokens.SaveAsync(token).ConfigureAwait(false);
        IPortalSession? session = null;
        PortalFailure reason = PortalFailure.Failed;
        string? failure = null;
        try
        {
            session = await _open(tokens, offerRestoreToken, _log, cancel.Token).ConfigureAwait(false);
        }
        catch (PortalException e)
        {
            (reason, failure) = (e.Reason, e.Message);
        }
        catch (OperationCanceledException)
        {
            failure = "Screen sharing was called off.";
        }

        bool keep;
        lock (_gate)
        {
            keep = session is not null && !cancel.IsCancellationRequested;
            if (keep)
            {
                _session = session;
            }

            _opening = null;
        }

        cancel.Dispose();
        if (session is not null && !keep)
        {
            // Called off while the person was saying yes.
            await session.DisposeAsync().ConfigureAwait(false);
            failure = "Screen sharing was called off.";
        }

        if (failure is not null || session is null)
        {
            _log.LogInformation("Screen sharing did not open: {Reason}: {Failure}", reason, failure);
            Failed(id, reason, failure ?? "The portal did not open a session.");
            return;
        }

        var opened = new AgentOpened(session.Devices, session.RemoteControl, session.ClipboardEnabled, session.StartTook,
            await tokens.LoadAsync().ConfigureAwait(false), session.Streams);
        Send(buffer => AgentWire.WriteOpened(buffer, id, opened), []);
        _ = WatchAsync(session);
    }

    /// <summary>The portal ended the session by itself -- the screen was locked, the person stopped sharing -- and the engine hears it.</summary>
    private async Task WatchAsync(IPortalSession session)
    {
        await session.Closed.ConfigureAwait(false);
        bool ours;
        lock (_gate)
        {
            ours = ReferenceEquals(_session, session);
            if (ours)
            {
                _session = null;
            }
        }

        if (ours)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            _log.LogInformation("The portal ended screen sharing");
            Send(buffer => AgentWire.WriteClosed(buffer, "The portal ended screen sharing."), []);
        }
    }

    private async Task OpenRemoteAsync(uint id)
    {
        IPortalSession? session;
        lock (_gate)
        {
            session = _session;
        }

        if (session is null)
        {
            Failed(id, PortalFailure.Failed, "No screen-sharing session is open here.");
            return;
        }

        try
        {
            // Sent, then closed: the engine gets its own copy of the descriptor with the message.
            using SafeFileHandle remote = await session.OpenPipeWireRemoteAsync().ConfigureAwait(false);
            Send(buffer => AgentWire.WriteRemote(buffer, id), [(int)remote.DangerousGetHandle()]);
        }
        catch (Exception e) when (e is PortalException or IOException or ObjectDisposedException)
        {
            Failed(id, e is PortalException p ? p.Reason : PortalFailure.Failed, e.Message);
        }
    }

    /// <summary>One of the engine's input events, to the portal; dropped when there is no session to take it.</summary>
    private void Deliver(in PortalSession.InputEvent e)
    {
        IPortalSession? session;
        lock (_gate)
        {
            session = _session;
        }

        if (session is not null)
        {
            Deliver(session, e);
        }
    }

    /// <summary>The event back into the call it was made from (<see cref="AgentPortalSession"/> encodes it the way <see cref="PortalSession"/> queues it).</summary>
    internal static void Deliver(IPortalInput input, in PortalSession.InputEvent e)
    {
        switch (e.Kind)
        {
            case PortalSession.InputKind.MotionAbsolute:
                input.PointerMotionAbsolute(e.A, e.X, e.Y);
                break;
            case PortalSession.InputKind.Motion:
                input.PointerMotion(e.X, e.Y);
                break;
            case PortalSession.InputKind.Button:
                input.PointerButton(e.B, e.A != 0);
                break;
            case PortalSession.InputKind.Axis:
                input.PointerAxisDiscrete(e.A, e.B);
                break;
            case PortalSession.InputKind.Keycode:
                input.KeyboardKeycode(e.B, e.A != 0);
                break;
            case PortalSession.InputKind.Keysym:
                input.KeyboardKeysym(e.B, e.A != 0);
                break;
        }
    }

    /// <summary>Calls off an open under way, and closes the open session.</summary>
    private async Task CloseAsync()
    {
        IPortalSession? session;
        lock (_gate)
        {
            _opening?.Cancel();
            session = _session;
            _session = null;
        }

        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            _log.LogInformation("Screen sharing closed at the engine's request");
        }
    }

    private void Failed(uint id, PortalFailure reason, string message) =>
        Send(buffer => AgentWire.WriteFailed(buffer, id, reason, message), []);

    private delegate int Compose(Span<byte> buffer);

    private void Send(Compose compose, ReadOnlySpan<int> fds)
    {
        byte[] buffer = new byte[AgentWire.MaxMessage];
        int length = compose(buffer);
        lock (_sending)
        {
            try
            {
                UnixSocketMsg.Send(_socket, buffer.AsSpan(0, length), fds);
            }
            catch (IOException e)
            {
                // The engine is going; its socket closing ends this agent in a moment.
                _log.LogDebug("Could not answer the engine: {Error}", e.Message);
            }
        }
    }

    /// <summary>The secret store a token passes through on its way from the engine to the portal and back.</summary>
    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, byte[]> _values = [];

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default)
        {
            lock (_values)
            {
                return ValueTask.FromResult(_values.TryGetValue(key, out byte[]? value) ? value : null);
            }
        }

        public ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct = default)
        {
            lock (_values)
            {
                _values[key] = value.ToArray();
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(string key, CancellationToken ct = default)
        {
            lock (_values)
            {
                _values.Remove(key);
            }

            return ValueTask.CompletedTask;
        }
    }
}
