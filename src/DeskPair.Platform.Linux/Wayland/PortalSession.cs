using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace DeskPair.Platform.Linux.Wayland;

/// <summary>
/// Screen sharing on Wayland, as xdg-desktop-portal grants it: one RemoteDesktop session with the monitors in it,
/// asked for over the signed-in user's session bus. A compositor lets no client read the screen or inject input
/// by itself; the portal asks the person at the machine, once, and hands back PipeWire streams and the right to
/// send pointer and keyboard events.
///
/// The conversation is <c>CreateSession</c>, <c>SelectSources</c> (monitors, the cursor beside the picture when
/// the portal can), <c>SelectDevices</c> (keyboard and pointer, remember the answer), <c>Start</c> -- the one that
/// may show a dialog -- and then <c>OpenPipeWireRemote</c> for the socket the streams are read through. Each of
/// the first four answers through a <c>Response</c> signal on a request object, not through the method's reply.
///
/// "Remember the answer" is <c>persist_mode</c> with the previous session's <c>restore_token</c>
/// (<see cref="PortalTokens"/>): with it, <c>Start</c> returns without asking anybody, which is the difference
/// between a machine that can be reached and one that needs a person every time. It is still not unattended
/// access -- there is no portal without a signed-in session, so the login screen, the lock screen and a cold boot
/// belong to the scanout daemon, not to this.
///
/// The session lives as long as this object and its bus connection. The portal also ends it by itself -- the
/// person stops sharing from the top bar, the compositor restarts -- and <see cref="Closed"/> says so.
/// </summary>
public sealed class PortalSession : IPortalSession
{
    /// <summary>
    /// How long <c>Start</c> may wait for the person at the machine. Nobody answering a dialog is the usual way
    /// this fails, and a viewer should hear that rather than watch a black window until they give up.
    /// </summary>
    public static readonly TimeSpan ConsentTimeout = TimeSpan.FromSeconds(45);

    /// <summary>How long the calls that never show anything may take.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly Connection _bus;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDisposable? _closedWatch;
    private string? _handle;
    private int _tokens;
    private int _disposed;
    private int _notifyRefused;
    private readonly Channel<InputEvent> _input = Channel.CreateUnbounded<InputEvent>(new UnboundedChannelOptions { SingleReader = true });
    private Task? _inputPump;

    private PortalSession(Connection bus, ILogger log, TimeProvider time)
    {
        _bus = bus;
        _log = log;
        _time = time;
    }

    /// <summary>One stream per monitor the person shared, in the portal's order.</summary>
    public IReadOnlyList<PortalStream> Streams { get; private set; } = [];

    /// <summary>The input devices granted: <c>1</c> keyboard, <c>2</c> pointer.</summary>
    public uint Devices { get; private set; }

    /// <summary>
    /// False when the portal has ScreenCast but no RemoteDesktop -- a wlroots desktop through
    /// xdg-desktop-portal-wlr -- and the screen is shared to be watched only. <see cref="Devices"/> is then 0, as
    /// it is when a person leaves "allow remote interaction" off, and every consumer already handles that.
    /// </summary>
    public bool RemoteControl { get; private set; } = true;

    /// <summary>Whether the portal lets this session use its clipboard.</summary>
    public bool ClipboardEnabled { get; private set; }

    /// <summary>Whether a token from an earlier session was offered, so the portal could start without asking.</summary>
    public bool OfferedRestoreToken { get; private set; }

    /// <summary>
    /// How long <c>Start</c> took. The portal does not say whether it asked anybody; a start nobody had to answer
    /// takes well under a second, and one with a dialog takes as long as the person does.
    /// </summary>
    public TimeSpan StartTook { get; private set; }

    /// <summary>Completes when the session is over: ended by the portal, or disposed here.</summary>
    public Task Closed => _closed.Task;

    /// <summary>
    /// Asks the portal for the monitors, keyboard and pointer of the session this process's session bus belongs to.
    /// </summary>
    /// <param name="tokens">Where the permission is remembered between sessions.</param>
    /// <param name="log">Where each step and its outcome is written.</param>
    /// <param name="time">The clock for the timeouts; the system clock when null.</param>
    /// <param name="ct">Abandons the attempt, taking down any dialog it put up.</param>
    /// <param name="offerRestoreToken">
    /// False to ask the person whatever was remembered: the settings page asking again, so that a permission given
    /// without "allow remote interaction" can be given again with it. The new answer is remembered as usual.
    /// </param>
    /// <exception cref="PortalException">No portal, the person said no, nobody answered, or the portal failed.</exception>
    public static async Task<PortalSession> OpenAsync(
        PortalTokens tokens, ILogger log, TimeProvider? time = null, CancellationToken ct = default, bool offerRestoreToken = true)
    {
        string address = Address.Session ?? throw new PortalException(
            PortalFailure.Unavailable,
            "There is no session bus (DBUS_SESSION_BUS_ADDRESS is not set). Screen sharing on Wayland is asked for in the signed-in user's session.");

        var bus = new Connection(address);
        var session = new PortalSession(bus, log, time ?? TimeProvider.System);
        try
        {
            try
            {
                await LeaveReaderAsync(bus.ConnectAsync().AsTask()).ConfigureAwait(false);
            }
            catch (Exception e) when (e is ConnectException or DisconnectedException)
            {
                throw new PortalException(PortalFailure.Unavailable, $"The session bus at {address} cannot be reached: {e.Message}", e);
            }

            await session.StartAsync(tokens, offerRestoreToken, ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The socket this session's streams are read through: a PipeWire connection the portal opened, which can see
    /// the shared monitors' nodes and nothing else. The caller owns it.
    /// </summary>
    public async Task<SafeFileHandle> OpenPipeWireRemoteAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        MessageBuffer message = Method(Portal.ScreenCast, "OpenPipeWireRemote", _handle, parentWindow: null, []);
        SafeFileHandle? fd = await CallAsync(message, static (m, _) => m.GetBodyReader().ReadHandle<SafeFileHandle>(), "OpenPipeWireRemote", ct)
            .ConfigureAwait(false);
        return fd ?? throw new PortalException(PortalFailure.Failed, "The portal answered OpenPipeWireRemote without a file descriptor.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Whatever input is queued (a release of every key still down, typically) goes before the session does.
        _input.Writer.TryComplete();
        if (_inputPump is not null)
        {
            try
            {
                await _inputPump.WaitAsync(RequestTimeout, _time).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _log.LogDebug("Queued input was still being sent when the portal session closed");
            }
        }

        if (_handle is not null && _bus.UniqueName is not null)
        {
            // Dropping the connection would end the session as well; saying so is quicker for the compositor,
            // which takes the "being shared" indicator down on Close rather than when it notices the peer went.
            try
            {
                await LeaveReaderAsync(_bus.CallMethodAsync(NoArguments(_handle, Portal.Session, "Close")).WaitAsync(RequestTimeout, _time)).ConfigureAwait(false);
            }
            catch (Exception e) when (IsBusFailure(e) || e is TimeoutException)
            {
                _log.LogDebug("Closing the portal session: {Error}", e.Message);
            }
        }

        _closedWatch?.Dispose();
        _bus.Dispose();
        _closed.TrySetResult();
    }

    private async Task StartAsync(PortalTokens tokens, bool offerRestoreToken, CancellationToken ct)
    {
        // RemoteDesktop is the screen and the input together. A portal whose backend offers only ScreenCast --
        // sway, Hyprland and the other wlroots desktops, through xdg-desktop-portal-wlr -- can still show the screen,
        // so the session falls back to that and is watched only, rather than refusing a desktop it could share.
        uint? remoteVersion = await VersionOfAsync(Portal.RemoteDesktop, ct).ConfigureAwait(false);
        uint screenCastVersion = await GetUInt32Async(Portal.ScreenCast, "version", ct).ConfigureAwait(false);
        RemoteControl = remoteVersion is not null;
        string owner = RemoteControl ? Portal.RemoteDesktop : Portal.ScreenCast;
        uint cursorModes = await GetUInt32Async(Portal.ScreenCast, "AvailableCursorModes", ct).ConfigureAwait(false);
        uint deviceTypes = RemoteControl ? await GetUInt32Async(Portal.RemoteDesktop, "AvailableDeviceTypes", ct).ConfigureAwait(false) : 0;
        _log.LogDebug(
            "Portal: RemoteDesktop {Remote}, ScreenCast {ScreenCast}, cursor modes {Cursor}, device types {Devices}",
            remoteVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "absent", screenCastVersion, cursorModes, deviceTypes);
        if (!RemoteControl)
        {
            _log.LogInformation("This desktop's portal shares the screen but offers no remote control (no RemoteDesktop backend); viewers can watch only");
        }

        Dictionary<string, VariantValue> created = await RequestAsync(
            owner, "CreateSession", session: null, parentWindow: null,
            new() { ["session_handle_token"] = NextToken() }, RequestTimeout, ct).ConfigureAwait(false);
        _handle = created.TryGetValue("session_handle", out VariantValue handle) ? Portal.Unwrap(handle) switch
        {
            { Type: VariantValueType.ObjectPath } path => path.GetObjectPathAsString(),
            { Type: VariantValueType.String } text => text.GetString(),
            _ => null,
        } : null;
        if (_handle is null)
        {
            throw new PortalException(PortalFailure.Failed, "The portal created a session without saying which.");
        }

        _closedWatch = await LeaveReaderAsync(WatchClosedAsync(_handle).AsTask()).ConfigureAwait(false);

        var sources = new Dictionary<string, VariantValue>
        {
            ["types"] = Portal.SourceMonitor,
            ["multiple"] = true,
            ["cursor_mode"] = Portal.ChooseCursorMode(cursorModes),
        };
        var devices = new Dictionary<string, VariantValue> { ["types"] = deviceTypes & (Portal.DeviceKeyboard | Portal.DevicePointer) };

        // "Remember the answer" goes with the request that owns the session: SelectDevices for RemoteDesktop, and
        // SelectSources when there is only ScreenCast.
        bool persists = RemoteControl ? remoteVersion >= Portal.RemoteDesktopPersistsFrom : screenCastVersion >= Portal.ScreenCastPersistsFrom;
        if (persists)
        {
            Dictionary<string, VariantValue> remembered = RemoteControl ? devices : sources;
            remembered["persist_mode"] = Portal.PersistUntilRevoked;
            string? restore = offerRestoreToken ? await tokens.LoadAsync(ct).ConfigureAwait(false) : null;
            if (restore is not null)
            {
                remembered["restore_token"] = restore;
                OfferedRestoreToken = true;
            }
        }
        else
        {
            _log.LogInformation(
                "This portal is {Interface} version {Version}, which cannot remember the permission; it will ask every time",
                owner, RemoteControl ? remoteVersion : screenCastVersion);
        }

        await RequestAsync(Portal.ScreenCast, "SelectSources", _handle, parentWindow: null, sources, RequestTimeout, ct).ConfigureAwait(false);
        if (RemoteControl)
        {
            await RequestAsync(Portal.RemoteDesktop, "SelectDevices", _handle, parentWindow: null, devices, RequestTimeout, ct).ConfigureAwait(false);
        }

        long started = _time.GetTimestamp();
        Dictionary<string, VariantValue> results = await RequestAsync(
            owner, "Start", _handle, parentWindow: string.Empty, [], ConsentTimeout, ct).ConfigureAwait(false);
        StartTook = _time.GetElapsedTime(started);

        PortalStart start = Portal.ParseStart(results);
        if (persists)
        {
            // Whatever token was offered has been spent; the next session can only use the one handed back now.
            await tokens.SaveAsync(start.RestoreToken, start.Devices, CancellationToken.None).ConfigureAwait(false);
        }

        if (start.Streams.Count == 0)
        {
            throw new PortalException(PortalFailure.Failed, "The portal started a session with no screen in it.");
        }

        Streams = start.Streams;
        Devices = RemoteControl ? start.Devices : 0;
        ClipboardEnabled = start.ClipboardEnabled;
        if (RemoteControl)
        {
            _inputPump = Task.Run(PumpInputAsync);
        }
        _log.LogInformation(
            "Portal session started in {Ms} ms ({Restore}): {Count} screen(s) [{Streams}], devices {Devices}, clipboard {Clipboard}, next time {Next}",
            (int)StartTook.TotalMilliseconds,
            OfferedRestoreToken ? "restore token offered" : "no restore token",
            start.Streams.Count,
            string.Join("; ", start.Streams.Select(s => $"node {s.NodeId} {s.Width}x{s.Height}@{s.X},{s.Y}")),
            start.Devices,
            start.ClipboardEnabled,
            start.RestoreToken is null ? "asks again" : "remembered");
    }

    /// <summary>
    /// One portal request: subscribe to its <c>Response</c>, call, wait for the answer. Only a success comes back;
    /// a refusal, a failure or no answer in <paramref name="timeout"/> throws, and a request left unanswered is
    /// closed first so its dialog does not stay up for whoever sits down next.
    /// </summary>
    private async Task<Dictionary<string, VariantValue>> RequestAsync(
        string iface, string member, string? session, string? parentWindow, Dictionary<string, VariantValue> options, TimeSpan timeout, CancellationToken ct)
    {
        string token = NextToken();
        options["handle_token"] = token;
        string path = Portal.RequestPath(_bus.UniqueName!, token);
        var answer = new TaskCompletionSource<(uint Code, Dictionary<string, VariantValue> Results)>(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable watch = await LeaveReaderAsync(WatchResponseAsync(path, answer).AsTask()).ConfigureAwait(false);
        try
        {
            string returned = await CallAsync(
                Method(iface, member, session, parentWindow, options), static (m, _) => m.GetBodyReader().ReadObjectPathAsString(), member, ct).ConfigureAwait(false);
            if (returned != path)
            {
                // A portal from before the path convention picks its own; the answer may have gone by already,
                // which is the race the convention exists to close. Watch where it said and hope.
                _log.LogDebug("Portal {Member} answers at {Returned}, not {Expected}", member, returned, path);
                watch.Dispose();
                path = returned;
                watch = await LeaveReaderAsync(WatchResponseAsync(path, answer).AsTask()).ConfigureAwait(false);
            }

            (uint code, Dictionary<string, VariantValue> results) = await answer.Task.WaitAsync(timeout, _time, ct).ConfigureAwait(false);
            return code switch
            {
                Portal.ResponseSuccess => results,
                Portal.ResponseCancelled => throw new PortalException(PortalFailure.Refused, "The person at the machine did not allow screen sharing."),
                _ => throw new PortalException(PortalFailure.Failed, $"The portal ended {member} without sharing the screen (response {code})."),
            };
        }
        catch (TimeoutException)
        {
            await CloseRequestAsync(path).ConfigureAwait(false);
            throw member == "Start"
                ? new PortalException(PortalFailure.TimedOut, $"The machine is asking on its own screen whether to allow sharing it, and nobody answered in {(int)timeout.TotalSeconds} seconds.")
                : new PortalException(PortalFailure.Failed, $"The portal did not answer {member} in {(int)timeout.TotalSeconds} seconds.");
        }
        catch (OperationCanceledException)
        {
            await CloseRequestAsync(path).ConfigureAwait(false);
            throw;
        }
        finally
        {
            watch.Dispose();
        }
    }

    private async Task CloseRequestAsync(string path)
    {
        try
        {
            await LeaveReaderAsync(_bus.CallMethodAsync(NoArguments(path, Portal.Request, "Close")).WaitAsync(RequestTimeout, _time)).ConfigureAwait(false);
        }
        catch (Exception e) when (IsBusFailure(e) || e is TimeoutException)
        {
            _log.LogDebug("Closing the portal request {Path}: {Error}", path, e.Message);
        }
    }

    private async Task<uint> GetUInt32Async(string iface, string property, CancellationToken ct)
    {
        try
        {
            return await CallAsync(PropertyGet(iface, property), static (m, _) => Portal.Unwrap(m.GetBodyReader().ReadVariantValue()).GetUInt32(), property, ct)
                .ConfigureAwait(false);
        }
        catch (PortalException e) when (IsMissingInterface(e))
        {
            // xdg-desktop-portal answers for interfaces only its backend implements, and each desktop's backend is a
            // package of its own; without one the property is simply not there.
            throw new PortalException(
                PortalFailure.Unavailable,
                $"xdg-desktop-portal has no {iface} here: the desktop's portal backend is missing (xdg-desktop-portal-gnome on GNOME, xdg-desktop-portal-kde on KDE, xdg-desktop-portal-wlr on sway and other wlroots desktops).",
                e.InnerException);
        }
    }

    /// <summary>The interface's version, or null when this portal has no backend for it at all.</summary>
    private async Task<uint?> VersionOfAsync(string iface, CancellationToken ct)
    {
        try
        {
            return await CallAsync(PropertyGet(iface, "version"), static (m, _) => Portal.Unwrap(m.GetBodyReader().ReadVariantValue()).GetUInt32(), "version", ct)
                .ConfigureAwait(false);
        }
        catch (PortalException e) when (IsMissingInterface(e))
        {
            return null;
        }
    }

    /// <summary>What the bus answers for a property of an interface nobody implements here.</summary>
    private static bool IsMissingInterface(PortalException e) =>
        e.InnerException is DBusException { ErrorName: "org.freedesktop.DBus.Error.InvalidArgs" or "org.freedesktop.DBus.Error.UnknownInterface" or "org.freedesktop.DBus.Error.UnknownProperty" };

    private async Task<T> CallAsync<T>(MessageBuffer message, MessageValueReader<T> reader, string what, CancellationToken ct)
    {
        try
        {
            return await LeaveReaderAsync(_bus.CallMethodAsync(message, reader).WaitAsync(RequestTimeout, _time, ct)).ConfigureAwait(false);
        }
        catch (DBusException e) when (e.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner")
        {
            throw new PortalException(PortalFailure.Unavailable, "xdg-desktop-portal is not running in this session (package xdg-desktop-portal).", e);
        }
        catch (DBusException e)
        {
            throw new PortalException(PortalFailure.Failed, $"The portal refused {what}: {e.ErrorName}: {e.ErrorMessage}", e);
        }
        catch (TimeoutException e)
        {
            throw new PortalException(PortalFailure.Failed, $"The portal did not answer {what} in {(int)RequestTimeout.TotalSeconds} seconds.", e);
        }
        catch (Exception e) when (e is ConnectException or DisconnectedException or ProtocolException)
        {
            throw new PortalException(PortalFailure.Unavailable, $"The session bus went away during {what}: {e.Message}", e);
        }
    }

    /// <summary>
    /// Waits for <paramref name="task"/>, then carries on from the thread pool. Tmds completes its tasks on the thread
    /// that reads the bus, and a continuation already registered runs right there, <c>ConfigureAwait</c> or not
    /// (<c>ForceYielding</c> only yields when the task was finished before the await). Without this the caller's code
    /// ran on the reader; a caller that then waited for another reply -- a capturer factory, which has to be
    /// synchronous -- waited for itself, until the timeout. Every await on the bus in this class goes through here.
    /// </summary>
    internal static async Task<T> LeaveReaderAsync<T>(Task<T> task)
    {
        await ((Task)task).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await Task.Yield();
        return await task.ConfigureAwait(false);
    }

    /// <inheritdoc cref="LeaveReaderAsync{T}(Task{T})"/>
    internal static async Task LeaveReaderAsync(Task task)
    {
        await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await Task.Yield();
        await task.ConfigureAwait(false);
    }

    private ValueTask<IDisposable> WatchResponseAsync(string path, TaskCompletionSource<(uint Code, Dictionary<string, VariantValue> Results)> answer) =>
        _bus.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Sender = Portal.BusName, Path = path, Interface = Portal.Request, Member = "Response" },
            static (m, _) =>
            {
                Reader reader = m.GetBodyReader();
                return (reader.ReadUInt32(), reader.ReadDictionaryOfStringToVariantValue());
            },
            static (e, value, _, state) =>
            {
                var answer = (TaskCompletionSource<(uint, Dictionary<string, VariantValue>)>)state!;
                if (e is null)
                {
                    answer.TrySetResult(value);
                }
                else
                {
                    // The bus went, or the answer made no sense.
                    answer.TrySetException(new PortalException(PortalFailure.Failed, $"The portal stopped answering: {e.Message}", e));
                }
            },
            ObserverFlags.EmitOnConnectionDispose,
            null,
            answer,
            false);

    private ValueTask<IDisposable> WatchClosedAsync(string handle) =>
        _bus.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Sender = Portal.BusName, Path = handle, Interface = Portal.Session, Member = "Closed" },
            static (_, _) => true,
            static (_, _, _, state) => ((TaskCompletionSource)state!).TrySetResult(),
            ObserverFlags.EmitOnConnectionDispose,
            null,
            _closed,
            false);

    /// <summary>Everything the bus library throws when the bus, the connection or a reply is not what it should be.</summary>
    private static bool IsBusFailure(Exception e) => e is DBusException or ConnectException or DisconnectedException or ProtocolException or ObjectDisposedException;

    bool IPortalInput.Pointer => (Devices & Portal.DevicePointer) != 0;

    bool IPortalInput.Keyboard => (Devices & Portal.DeviceKeyboard) != 0;

    void IPortalInput.PointerMotionAbsolute(uint stream, double x, double y) => Enqueue(new InputEvent(InputKind.MotionAbsolute, stream, 0, x, y));

    void IPortalInput.PointerMotion(double dx, double dy) => Enqueue(new InputEvent(InputKind.Motion, 0, 0, dx, dy));

    void IPortalInput.PointerButton(int button, bool pressed) => Enqueue(new InputEvent(InputKind.Button, pressed ? 1u : 0u, button, 0, 0));

    void IPortalInput.PointerAxisDiscrete(uint axis, int steps) => Enqueue(new InputEvent(InputKind.Axis, axis, steps, 0, 0));

    void IPortalInput.KeyboardKeycode(int keycode, bool pressed) => Enqueue(new InputEvent(InputKind.Keycode, pressed ? 1u : 0u, keycode, 0, 0));

    void IPortalInput.KeyboardKeysym(int keysym, bool pressed) => Enqueue(new InputEvent(InputKind.Keysym, pressed ? 1u : 0u, keysym, 0, 0));

    /// <summary>
    /// Input goes to the portal one call at a time, each sent once the one before it was answered.
    ///
    /// Sent back to back, calls arrive out of order: xdg-desktop-portal handles method calls on a pool of threads,
    /// so a key's release can overtake its press on the way to the compositor, which then ignores the "second"
    /// release and leaves the key down (GNOME logs "Received multiple virtual key presses (ignoring)"). Typing
    /// <c>echo "portal</c> came out as <c>e"CPORh otal</c>. The portal answers once it has passed a call on, so
    /// waiting for each answer is what keeps the order, at the price of a local round trip per event.
    ///
    /// A backlog of pointer motion is only worth where it ends: consecutive absolute moves collapse to the last one
    /// and relative ones add up, so a slow portal makes the pointer late rather than making it replay its path.
    /// </summary>
    private void Enqueue(in InputEvent e)
    {
        if (_handle is not null && Volatile.Read(ref _disposed) == 0)
        {
            _input.Writer.TryWrite(e);
        }
    }

    private async Task PumpInputAsync()
    {
        ChannelReader<InputEvent> reader = _input.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out InputEvent current))
            {
                while (reader.TryPeek(out InputEvent next) && Merge(current, next) is { } merged)
                {
                    reader.TryRead(out _);
                    current = merged;
                }

                try
                {
                    await LeaveReaderAsync(_bus.CallMethodAsync(Build(current)).WaitAsync(RequestTimeout, _time)).ConfigureAwait(false);
                }
                catch (Exception e) when (IsBusFailure(e) || e is TimeoutException)
                {
                    if (Interlocked.Exchange(ref _notifyRefused, 1) == 0)
                    {
                        _log.LogWarning("The portal refused {Kind}: {Error}. Input from viewers is not reaching this desktop", current.Kind, e.Message);
                    }
                    else
                    {
                        _log.LogDebug("The portal refused {Kind}: {Error}", current.Kind, e.Message);
                    }
                }
            }
        }
    }

    /// <summary>
    /// <paramref name="next"/> folded into <paramref name="current"/> when nothing is lost by sending one call for
    /// both: the later of two absolute moves, the sum of two relative ones. Null for anything else -- a button, a key,
    /// a wheel step, or a move of the other kind, each of which has to arrive as itself and in its place.
    /// </summary>
    internal static InputEvent? Merge(in InputEvent current, in InputEvent next) => (current.Kind, next.Kind) switch
    {
        (InputKind.MotionAbsolute, InputKind.MotionAbsolute) => next,
        (InputKind.Motion, InputKind.Motion) => current with { X = current.X + next.X, Y = current.Y + next.Y },
        _ => null,
    };

    private MessageBuffer Build(in InputEvent e)
    {
        (string member, string signature) = e.Kind switch
        {
            InputKind.MotionAbsolute => ("NotifyPointerMotionAbsolute", "oa{sv}udd"),
            InputKind.Motion => ("NotifyPointerMotion", "oa{sv}dd"),
            InputKind.Button => ("NotifyPointerButton", "oa{sv}iu"),
            InputKind.Axis => ("NotifyPointerAxisDiscrete", "oa{sv}ui"),
            InputKind.Keycode => ("NotifyKeyboardKeycode", "oa{sv}iu"),
            _ => ("NotifyKeyboardKeysym", "oa{sv}iu"),
        };

        MessageWriter writer = _bus.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Portal.BusName, Portal.ObjectPath, Portal.RemoteDesktop, member, signature);
            writer.WriteObjectPath(_handle!);
            writer.WriteDictionary(NoOptions);
            switch (e.Kind)
            {
                case InputKind.MotionAbsolute:
                    writer.WriteUInt32(e.A);
                    writer.WriteDouble(e.X);
                    writer.WriteDouble(e.Y);
                    break;
                case InputKind.Motion:
                    writer.WriteDouble(e.X);
                    writer.WriteDouble(e.Y);
                    break;
                case InputKind.Axis:
                    writer.WriteUInt32(e.A);
                    writer.WriteInt32(e.B);
                    break;
                default:
                    // Button, keycode and keysym are all (i code, u state).
                    writer.WriteInt32(e.B);
                    writer.WriteUInt32(e.A);
                    break;
            }

            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static readonly Dictionary<string, VariantValue> NoOptions = [];

    internal enum InputKind
    {
        MotionAbsolute,
        Motion,
        Button,
        Axis,
        Keycode,
        Keysym,
    }

    /// <summary>One queued event: <c>A</c> is the stream, axis or key state; <c>B</c> the code or steps; X and Y a position or delta.</summary>
    internal readonly record struct InputEvent(InputKind Kind, uint A, int B, double X, double Y);

    /// <summary>A handle token: unique on this connection, and made of the characters an object path allows.</summary>
    private string NextToken() => $"deskpair{Interlocked.Increment(ref _tokens)}";

    // MessageWriter is a mutable ref struct: written through a plain local and disposed by hand, never a `using`
    // variable, whose read-only-ness would invite a defensive copy of a writer that is half-way through a message.
    private MessageBuffer Method(string iface, string member, string? session, string? parentWindow, Dictionary<string, VariantValue> options)
    {
        string signature = (session is null ? string.Empty : "o") + (parentWindow is null ? string.Empty : "s") + "a{sv}";
        MessageWriter writer = _bus.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Portal.BusName, Portal.ObjectPath, iface, member, signature);
            if (session is not null)
            {
                writer.WriteObjectPath(session);
            }

            if (parentWindow is not null)
            {
                writer.WriteString(parentWindow);
            }

            writer.WriteDictionary(options);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private MessageBuffer PropertyGet(string iface, string property)
    {
        MessageWriter writer = _bus.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Portal.BusName, Portal.ObjectPath, Portal.Properties, "Get", "ss");
            writer.WriteString(iface);
            writer.WriteString(property);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private MessageBuffer NoArguments(string path, string iface, string member)
    {
        MessageWriter writer = _bus.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Portal.BusName, path, iface, member);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }
}
