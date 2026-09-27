using System.Threading.Channels;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using DeskPair.Core.Session;
using DeskPair.Platform.Abstractions.Terminal;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Terminal;

/// <summary>
/// One connection's terminals on the host: the id table, the limits, the credit window and the read
/// loops. Everything the protocol allows a viewer to do to a shell goes through <see cref="HandleAsync"/>;
/// everything that ends one goes through <see cref="CloseAllAsync"/>, which is what a withdrawn permission
/// and a dropped connection both call, because a root shell with nobody attached is worse than no shell.
///
/// Output crosses the session at <see cref="MessagePriority.Bulk"/>, which is bounded and waits: a slow
/// link becomes TCP backpressure, the pty's buffer fills, the shell's <c>write(2)</c> blocks -- rather than
/// the host's memory growing without bound. The exit notice takes the same queue so it cannot overtake the
/// last byte. Above that sits a credit window the viewer refills with acks, so that a viewer that has
/// stopped reading stops the flow before the queue does.
/// </summary>
public sealed class TerminalSession : IAsyncDisposable
{
    /// <summary>Shells one connection may have open at once. Four is more than a person uses and less than a loop can hurt with.</summary>
    public const int MaxTerminals = 4;

    /// <summary>The largest single output message.</summary>
    public const int OutputChunk = 64 * 1024;

    /// <summary>Output the viewer has not acknowledged before the host stops reading the shell.</summary>
    public const int CreditWindow = 256 * 1024;

    /// <summary>How long a chunk waits for company before it is sent, so a burst of single bytes is one message.</summary>
    public static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(8);

    private readonly ITerminalHost _host;
    private readonly TerminalRunAs _runAs;
    private readonly Func<Message, MessagePriority, CancellationToken, ValueTask> _send;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Dictionary<int, Running> _live = new();
    private readonly object _lock = new();
    private bool _disposed;

    public TerminalSession(ITerminalHost host, TerminalRunAs runAs, Func<Message, MessagePriority, CancellationToken, ValueTask> send, ILogger log, TimeProvider? time = null)
    {
        _host = host;
        _runAs = runAs;
        _send = send;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>How many shells this connection has opened in its life; the journal records it.</summary>
    public int Opens { get; private set; }

    /// <summary>The account the last shell ran as, for the journal.</summary>
    public string Identity { get; private set; } = string.Empty;

    /// <summary>Shells open right now.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _live.Count;
            }
        }
    }

    /// <summary>Raised after a shell was started, with its identity; the module writes it to the session for the journal.</summary>
    public event Action<string>? Opened;

    public async ValueTask HandleAsync(TerminalAction action, CancellationToken ct)
    {
        switch (action.UnionCase)
        {
            case TerminalAction.UnionOneofCase.Open:
                await OpenAsync(action.Open, ct).ConfigureAwait(false);
                break;
            case TerminalAction.UnionOneofCase.Input:
                if (Find(action.Input.Id) is { } typing)
                {
                    try
                    {
                        await typing.Terminal.WriteAsync(action.Input.Data.Memory, ct).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
                    {
                        await SendErrorAsync(action.Input.Id, "The shell is not taking input any more: " + e.Message, ct).ConfigureAwait(false);
                    }
                }

                break;
            case TerminalAction.UnionOneofCase.Resize:
                if (Find(action.Resize.Id) is { } sized && action.Resize.Columns > 0 && action.Resize.Rows > 0)
                {
                    sized.Terminal.Resize((int)Math.Min(action.Resize.Columns, 1000), (int)Math.Min(action.Resize.Rows, 1000));
                }

                break;
            case TerminalAction.UnionOneofCase.Signal:
                if (Find(action.Signal.Id) is { } signalled)
                {
                    signalled.Terminal.Signal(action.Signal.Signal switch
                    {
                        TerminalSignal.Types.Kind.Terminate => TerminalSignalKind.Terminate,
                        TerminalSignal.Types.Kind.Kill => TerminalSignalKind.Kill,
                        TerminalSignal.Types.Kind.Hangup => TerminalSignalKind.Hangup,
                        _ => TerminalSignalKind.Interrupt,
                    });
                }

                break;
            case TerminalAction.UnionOneofCase.Close:
                if (Take(action.Close.Id) is { } closing)
                {
                    await closing.EndAsync("closed by the viewer").ConfigureAwait(false);
                }

                break;
            case TerminalAction.UnionOneofCase.Ack:
                Find(action.Ack.Id)?.Credit((int)Math.Min(action.Ack.Bytes, int.MaxValue));
                break;
        }
    }

    /// <summary>Ends every shell and tells the viewer why. Safe to call twice, and after the session has gone.</summary>
    public async Task CloseAllAsync(string reason)
    {
        List<Running> all;
        lock (_lock)
        {
            all = [.. _live.Values];
            _live.Clear();
        }

        foreach (Running r in all)
        {
            await r.EndAsync(reason).ConfigureAwait(false);
        }
    }

    private async Task OpenAsync(TerminalOpen open, CancellationToken ct)
    {
        if (!_host.IsAvailable)
        {
            await SendErrorAsync(open.Id, _host.UnavailableReason ?? "This computer cannot open a terminal.", ct).ConfigureAwait(false);
            return;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (_live.ContainsKey(open.Id))
            {
                _ = SendErrorAsync(open.Id, $"Terminal {open.Id} is already open.", ct);
                return;
            }

            if (_live.Count >= MaxTerminals)
            {
                _ = SendErrorAsync(open.Id, $"No more than {MaxTerminals} terminals at once.", ct);
                return;
            }

            // Reserve the slot before the shell exists, so two opens racing cannot both pass the count.
            _live[open.Id] = Running.Pending;
        }

        ITerminal terminal;
        try
        {
            terminal = await _host.StartAsync(
                _runAs,
                (int)Math.Clamp(open.Columns == 0 ? 80 : open.Columns, 1, 1000),
                (int)Math.Clamp(open.Rows == 0 ? 24 : open.Rows, 1, 1000),
                ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TerminalStartException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            lock (_lock)
            {
                _live.Remove(open.Id);
            }

            _log.LogWarning(e, "Terminal {Id} could not be started", open.Id);
            await SendErrorAsync(open.Id, e.Message, ct).ConfigureAwait(false);
            return;
        }

        // The reservation must still be there: a CloseAll that ran while the shell was starting -- the
        // permission withdrawn, the session gone -- cleared it, and a shell that arrives after that is
        // exactly the unattended root shell this class exists to prevent.
        var running = new Running(open.Id, terminal, this);
        bool keep;
        lock (_lock)
        {
            keep = !_disposed && _live.TryGetValue(open.Id, out Running? slot) && slot == Running.Pending;
            if (keep)
            {
                _live[open.Id] = running;
                Opens++;
                Identity = terminal.Identity;
            }
        }

        if (!keep)
        {
            _log.LogInformation("Terminal {Id} was closed before its shell had started; ending it", open.Id);
            await terminal.DisposeAsync().ConfigureAwait(false);
            return;
        }

        _log.LogInformation("Terminal {Id} opened as {Identity} ({Shell})", open.Id, terminal.Identity, terminal.Shell);
        Opened?.Invoke(terminal.Identity);
        await _send(new Message { TerminalResponse = new TerminalResponse { Opened = new TerminalOpened { Id = open.Id, Identity = terminal.Identity, Shell = terminal.Shell } } }, MessagePriority.Control, ct).ConfigureAwait(false);
        running.Start();
    }

    private Running? Find(int id)
    {
        lock (_lock)
        {
            return _live.TryGetValue(id, out Running? r) && r != Running.Pending ? r : null;
        }
    }

    private Running? Take(int id)
    {
        lock (_lock)
        {
            return _live.Remove(id, out Running? r) && r != Running.Pending ? r : null;
        }
    }

    private void Forget(Running running)
    {
        lock (_lock)
        {
            if (_live.TryGetValue(running.Id, out Running? r) && r == running)
            {
                _live.Remove(running.Id);
            }
        }
    }

    private ValueTask SendErrorAsync(int id, string message, CancellationToken ct) =>
        _send(new Message { TerminalResponse = new TerminalResponse { Error = new TerminalError { Id = id, Message = message } } }, MessagePriority.Control, ct);

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            _disposed = true;
        }

        await CloseAllAsync("the session ended").ConfigureAwait(false);
    }

    /// <summary>One open shell: its reader, its sender, and the credit the viewer has left it.</summary>
    private sealed class Running
    {
        public static readonly Running Pending = new();

        private readonly TerminalSession? _owner;
        private readonly Channel<byte[]>? _chunks;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _creditLock = new();
        private long _unacked;
        private TaskCompletionSource _creditChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _pump;
        private int _ended;

        private Running()
        {
            Id = -1;
            Terminal = null!;
        }

        public Running(int id, ITerminal terminal, TerminalSession owner)
        {
            Id = id;
            Terminal = terminal;
            _owner = owner;
            _chunks = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        }

        public int Id { get; }

        public ITerminal Terminal { get; }

        public void Start() => _pump = Task.Run(async () =>
        {
            Task reading = ReadAsync();
            await SendAsync().ConfigureAwait(false);
            await reading.ConfigureAwait(false);
        });

        public void Credit(int bytes)
        {
            lock (_creditLock)
            {
                _unacked = Math.Max(0, _unacked - bytes);
                TaskCompletionSource was = _creditChanged;
                _creditChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                was.TrySetResult();
            }
        }

        /// <summary>Ends the shell and, once its output has drained, tells the viewer. Idempotent.</summary>
        public async Task EndAsync(string reason)
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0 || _owner is null)
            {
                return;
            }

            _owner.Forget(this);
            _cts.Cancel();
            try
            {
                await Terminal.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException)
            {
                _owner._log.LogDebug(e, "Terminal {Id} did not end cleanly", Id);
            }

            if (_pump is not null)
            {
                try
                {
                    await _pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }
            }

            await SendExitAsync(Terminal.Exited.IsCompletedSuccessfully ? Terminal.Exited.Result : -1, reason).ConfigureAwait(false);
        }

        private async Task ReadAsync()
        {
            byte[] buffer = new byte[OutputChunk];
            try
            {
                while (true)
                {
                    int n = await Terminal.Output.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        break;
                    }

                    _chunks!.Writer.TryWrite(buffer.AsSpan(0, n).ToArray());
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // The shell is gone, or this terminal was closed under it; either way the output has ended.
            }

            _chunks!.Writer.TryComplete();
        }

        private async Task SendAsync()
        {
            ChannelReader<byte[]> reader = _chunks!.Reader;
            try
            {
                while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                {
                    // A shell writes a prompt as several small writes; wait a moment and send them as one.
                    await Task.Delay(Coalesce, _owner!._time, _cts.Token).ConfigureAwait(false);
                    var batch = new List<byte[]>();
                    int total = 0;
                    while (total < OutputChunk && reader.TryPeek(out byte[]? next) && total + next.Length <= OutputChunk && reader.TryRead(out next))
                    {
                        batch.Add(next);
                        total += next.Length;
                    }

                    if (batch.Count == 0 && reader.TryRead(out byte[]? big))
                    {
                        // One chunk larger than the limit on its own: split it.
                        for (int at = 0; at < big.Length; at += OutputChunk)
                        {
                            await DeliverAsync(big.AsMemory(at, Math.Min(OutputChunk, big.Length - at))).ConfigureAwait(false);
                        }

                        continue;
                    }

                    byte[] joined = batch.Count == 1 ? batch[0] : Join(batch, total);
                    await DeliverAsync(joined).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
            {
                return;
            }

            // Output ended on its own: the shell exited. Tell the viewer once the exit code is known.
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                _owner!.Forget(this);
                int code = -1;
                try
                {
                    code = await Terminal.Exited.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (Exception e) when (e is TimeoutException or IOException or InvalidOperationException)
                {
                }

                await SendExitAsync(code, "the shell exited").ConfigureAwait(false);
                await Terminal.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async Task DeliverAsync(ReadOnlyMemory<byte> data)
        {
            // The credit window: never more than CreditWindow bytes outstanding. A viewer that has stopped
            // acknowledging stops this loop, and through the pty's buffer, the shell.
            while (true)
            {
                Task wait;
                lock (_creditLock)
                {
                    if (_unacked + data.Length <= CreditWindow)
                    {
                        _unacked += data.Length;
                        break;
                    }

                    wait = _creditChanged.Task;
                }

                await wait.WaitAsync(_cts.Token).ConfigureAwait(false);
            }

            await _owner!._send(
                new Message { TerminalResponse = new TerminalResponse { Output = new TerminalOutput { Id = Id, Data = ByteString.CopyFrom(data.Span) } } },
                MessagePriority.Bulk,
                _cts.Token).ConfigureAwait(false);
        }

        private async Task SendExitAsync(int code, string reason)
        {
            try
            {
                await _owner!._send(
                    new Message { TerminalResponse = new TerminalResponse { Exit = new TerminalExit { Id = Id, Code = code, Reason = reason } } },
                    MessagePriority.Bulk,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
                // The session is gone too; nobody is left to tell.
            }

            _owner!._log.LogInformation("Terminal {Id} ended ({Reason}, exit code {Code})", Id, reason, code);
        }

        private static byte[] Join(List<byte[]> parts, int total)
        {
            byte[] joined = new byte[total];
            int at = 0;
            foreach (byte[] p in parts)
            {
                p.CopyTo(joined, at);
                at += p.Length;
            }

            return joined;
        }
    }
}
