using System.Collections.Concurrent;
using DeskPair.Platform.Linux.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace DeskPair.Platform.Linux.Wayland.Agent;

/// <summary>
/// The engine's end of a session agent: portal sessions opened in the signed-in user's session, for an engine that
/// runs as another account (<see cref="SessionAgent"/> is the other end).
///
/// <see cref="OpenAsync(PortalTokens, ILogger, CancellationToken)"/> is a <see cref="PortalOpener"/>, so the portal
/// host, the capture and the input are the ones a session on this process's own bus uses. The permission to remember
/// travels with the request and is saved here, in the engine's store under the user's uid, when the answer comes. One
/// session at a time, as the host opens them.
///
/// Everything the agent says is read as if anybody could have said it -- it runs as the user -- so a message that does
/// not parse is dropped, and a descriptor that answers nothing is closed.
/// </summary>
internal sealed class AgentPortal : IAsyncDisposable
{
    private readonly int _socket;
    private readonly ILogger _log;
    private readonly object _sending = new();
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<Reply>> _pending = new();
    private readonly TaskCompletionSource<AgentHello> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _gone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AgentPortalSession? _current;
    private DesktopLayout? _layout;
    private int _nextId;
    private int _disposed;

    /// <param name="socket">The engine's end of the agent's socketpair; this owns it from now on.</param>
    /// <param name="log">Where the agent's comings and goings are said.</param>
    public AgentPortal(int socket, ILogger log)
    {
        _socket = socket;
        _log = log;
        var reader = new Thread(ReceiveAll) { IsBackground = true, Name = "agent-portal" };
        reader.Start();
    }

    /// <summary>What the agent said first: whose session it is in, and on which desktop.</summary>
    public Task<AgentHello> Hello => _hello.Task;

    /// <summary>Completes when the agent's socket closes: it exited, or its session ended.</summary>
    public Task Gone => _gone.Task;

    /// <summary>How the user's compositor lays out the monitors, as the agent last said; null until it has, and on a desktop whose layout it cannot read.</summary>
    public DesktopLayout? Layout => Volatile.Read(ref _layout);

    /// <summary>A <see cref="PortalOpener"/>: a session in the agent's session, offering the remembered permission.</summary>
    public Task<IPortalSession> OpenAsync(PortalTokens tokens, ILogger log, CancellationToken ct) => OpenAsync(tokens, offerRestoreToken: true, ct);

    /// <summary>
    /// A session in the agent's session. <paramref name="offerRestoreToken"/> false asks the person whatever was
    /// remembered. Calling it off takes the agent's dialog down with it.
    /// </summary>
    /// <exception cref="PortalException">The portal's refusal, or the agent's absence.</exception>
    public async Task<IPortalSession> OpenAsync(PortalTokens tokens, bool offerRestoreToken, CancellationToken ct)
    {
        string? token = offerRestoreToken ? await tokens.LoadAsync(ct).ConfigureAwait(false) : null;
        Reply reply;
        using (ct.Register(SendClose))
        {
            reply = await RequestAsync((buffer, id) => AgentWire.WriteOpen(buffer, id, token, offerRestoreToken), ct).ConfigureAwait(false);
        }

        using (reply.Descriptor)
        {
            if (reply.Kind != AgentWire.KindOpened || !AgentWire.TryReadOpened(reply.Message, out AgentOpened opened))
            {
                throw FailureOf(reply, "open a screen-sharing session");
            }

            await tokens.SaveAsync(opened.Token, opened.Devices, CancellationToken.None).ConfigureAwait(false);
            var session = new AgentPortalSession(this, opened);
            lock (_gate)
            {
                _current = session;
            }

            if (_gone.Task.IsCompleted)
            {
                session.Ended();
            }

            return session;
        }
    }

    /// <summary>A new PipeWire connection for the open session, which the caller owns.</summary>
    internal async Task<SafeFileHandle> OpenRemoteAsync(CancellationToken ct)
    {
        Reply reply = await RequestAsync(AgentWire.WriteOpenRemote, ct).ConfigureAwait(false);
        if (reply.Kind == AgentWire.KindRemote && reply.Descriptor is { } remote)
        {
            return remote;
        }

        reply.Descriptor?.Dispose();
        throw FailureOf(reply, "open a PipeWire connection");
    }

    /// <summary>One input event for the open session; lost, without a word, when the agent has gone.</summary>
    internal void SendInput(in PortalSession.InputEvent e)
    {
        Span<byte> buffer = stackalloc byte[64];
        int length = AgentWire.WriteInput(buffer, e);
        Send(buffer[..length], []);
    }

    /// <summary>Closes the session this ends, or calls off one being opened.</summary>
    internal void Release(AgentPortalSession session)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, session))
            {
                _current = null;
            }
        }

        SendClose();
    }

    private void SendClose()
    {
        Span<byte> buffer = stackalloc byte[AgentWire.HeaderSize];
        int length = AgentWire.WriteClose(buffer);
        Send(buffer[..length], []);
    }

    private delegate int ComposeRequest(Span<byte> buffer, uint id);

    private async Task<Reply> RequestAsync(ComposeRequest compose, CancellationToken ct)
    {
        uint id = (uint)Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            if (_gone.Task.IsCompleted)
            {
                throw Absent();
            }

            byte[] buffer = new byte[AgentWire.MaxMessage];
            int length = compose(buffer, id);
            Send(buffer.AsSpan(0, length), []);
            return await answer.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // An answer that comes after the caller gave up finds nobody, and its descriptor is closed.
            _pending.TryRemove(id, out _);
        }
    }

    private void Send(ReadOnlySpan<byte> message, ReadOnlySpan<int> fds)
    {
        lock (_sending)
        {
            try
            {
                UnixSocketMsg.Send(_socket, message, fds);
            }
            catch (IOException e)
            {
                // Gone, or going: the reader sees the socket close and says so.
                _log.LogDebug("Could not reach the session agent: {Error}", e.Message);
            }
        }
    }

    private void ReceiveAll()
    {
        byte[] message = new byte[AgentWire.MaxMessage];
        Span<int> fds = stackalloc int[UnixSocketMsg.MaxFds];
        try
        {
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
                    _log.LogInformation("The session agent's socket failed: {Error}", e.Message);
                    return;
                }

                // One descriptor is all any answer carries; more is closed.
                for (int k = 1; k < fdCount; k++)
                {
                    _ = UnixSocketMsg.close(fds[k]);
                }

                SafeFileHandle? descriptor = fdCount > 0 ? new SafeFileHandle(fds[0], ownsHandle: true) : null;
                if (length == 0)
                {
                    descriptor?.Dispose();
                    return;
                }

                Dispatch(message.AsSpan(0, length), descriptor);
            }
        }
        finally
        {
            EndAll(new PortalException(PortalFailure.Unavailable, "The session agent has gone: the signed-in user's session ended, or the agent stopped."));
        }
    }

    private void Dispatch(ReadOnlySpan<byte> message, SafeFileHandle? descriptor)
    {
        if (!AgentWire.TryReadHeader(message, out byte kind, out _, out uint id))
        {
            descriptor?.Dispose();
            _log.LogWarning("An unreadable {Length}-byte message from the session agent", message.Length);
            return;
        }

        if (id != 0)
        {
            if (_pending.TryGetValue(id, out TaskCompletionSource<Reply>? waiting) &&
                waiting.TrySetResult(new Reply(kind, message.ToArray(), descriptor)))
            {
                return;
            }

            descriptor?.Dispose();
            return;
        }

        descriptor?.Dispose();
        switch (kind)
        {
            case AgentWire.KindHello when AgentWire.TryReadHello(message, out AgentHello hello):
                _log.LogInformation("Session agent {Version} (pid {Pid}) serving uid {Uid} on {Desktop}", hello.Version, hello.Pid, hello.Uid, hello.Desktop.Length > 0 ? hello.Desktop : "an unnamed desktop");
                _hello.TrySetResult(hello);
                break;

            case AgentWire.KindLayout when AgentWire.TryReadLayout(message, out DesktopLayout layout):
                Volatile.Write(ref _layout, layout);
                _log.LogInformation("The session agent says the desktop's monitors are: {Layout}", layout);
                break;

            case AgentWire.KindClosed when AgentWire.TryReadClosed(message, out string reason):
                AgentPortalSession? ended;
                lock (_gate)
                {
                    ended = _current;
                    _current = null;
                }

                _log.LogInformation("The session agent says: {Reason}", reason);
                ended?.Ended();
                break;

            default:
                _log.LogWarning("An unreadable {Length}-byte message of kind {Kind} from the session agent", message.Length, kind);
                break;
        }
    }

    private void EndAll(Exception why)
    {
        _gone.TrySetResult();
        _hello.TrySetException(why);
        foreach (TaskCompletionSource<Reply> waiting in _pending.Values)
        {
            waiting.TrySetException(why);
        }

        AgentPortalSession? ended;
        lock (_gate)
        {
            ended = _current;
            _current = null;
        }

        ended?.Ended();
    }

    private static PortalException Absent() =>
        new(PortalFailure.Unavailable, "The session agent has gone: the signed-in user's session ended, or the agent stopped.");

    private static PortalException FailureOf(Reply reply, string doing) =>
        reply.Kind == AgentWire.KindFailed && AgentWire.TryReadFailed(reply.Message, out PortalFailure reason, out string text)
            ? new PortalException(reason, text)
            : new PortalException(PortalFailure.Failed, $"The session agent answered a request to {doing} with a message of kind {reply.Kind}.");

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Shut down, not closed: the reader may be inside recvmsg on this descriptor, and a closed number can be reused.
        _ = UnixSocketMsg.shutdown(_socket, UnixSocketMsg.ShutRdWr);
        await _gone.Task.ConfigureAwait(false);
        _ = UnixSocketMsg.close(_socket);
    }

    /// <summary>An answer: its kind, the whole message, and the descriptor that came with it.</summary>
    private sealed record Reply(byte Kind, byte[] Message, SafeFileHandle? Descriptor);
}

/// <summary>A portal session held open by a session agent, as the capture and the input use it.</summary>
internal sealed class AgentPortalSession : IPortalSession
{
    private readonly AgentPortal _agent;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public AgentPortalSession(AgentPortal agent, AgentOpened opened)
    {
        _agent = agent;
        Streams = opened.Streams;
        Devices = opened.Devices;
        RemoteControl = opened.RemoteControl;
        ClipboardEnabled = opened.Clipboard;
        StartTook = opened.StartTook;
    }

    public IReadOnlyList<PortalStream> Streams { get; }

    public uint Devices { get; }

    public bool RemoteControl { get; }

    public bool ClipboardEnabled { get; }

    public TimeSpan StartTook { get; }

    public Task Closed => _closed.Task;

    public Task<SafeFileHandle> OpenPipeWireRemoteAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _agent.OpenRemoteAsync(ct);
    }

    /// <summary>The portal ended it, or the agent went.</summary>
    internal void Ended() => _closed.TrySetResult();

    // The events as PortalSession queues them; the agent turns each back into the call (SessionAgent.Deliver).
    bool IPortalInput.Pointer => (Devices & Portal.DevicePointer) != 0;

    bool IPortalInput.Keyboard => (Devices & Portal.DeviceKeyboard) != 0;

    void IPortalInput.PointerMotionAbsolute(uint stream, double x, double y) =>
        Send(new PortalSession.InputEvent(PortalSession.InputKind.MotionAbsolute, stream, 0, x, y));

    void IPortalInput.PointerMotion(double dx, double dy) => Send(new PortalSession.InputEvent(PortalSession.InputKind.Motion, 0, 0, dx, dy));

    void IPortalInput.PointerButton(int button, bool pressed) =>
        Send(new PortalSession.InputEvent(PortalSession.InputKind.Button, pressed ? 1u : 0u, button, 0, 0));

    void IPortalInput.PointerAxisDiscrete(uint axis, int steps) => Send(new PortalSession.InputEvent(PortalSession.InputKind.Axis, axis, steps, 0, 0));

    void IPortalInput.KeyboardKeycode(int keycode, bool pressed) =>
        Send(new PortalSession.InputEvent(PortalSession.InputKind.Keycode, pressed ? 1u : 0u, keycode, 0, 0));

    void IPortalInput.KeyboardKeysym(int keysym, bool pressed) =>
        Send(new PortalSession.InputEvent(PortalSession.InputKind.Keysym, pressed ? 1u : 0u, keysym, 0, 0));

    private void Send(in PortalSession.InputEvent e)
    {
        if (Volatile.Read(ref _disposed) == 0 && !_closed.Task.IsCompleted)
        {
            _agent.SendInput(e);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _closed.TrySetResult();
            _agent.Release(this);
        }

        return ValueTask.CompletedTask;
    }
}
