using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DeskPair.Desktop.Services;

/// <summary>
/// Keeps one main window per user. Starting DeskPair again -- from the desktop icon, from a link, from the
/// installer's finish page -- should bring the window that is already open to the front rather than open a
/// second copy that fights the first over the same engine, the same configuration file and the same tray
/// icon.
///
/// The mutex decides who is first; the pipe is how everyone after that says so, and hands over the arguments
/// they were started with, so <c>--connect</c> from a second launch still reaches the running window instead
/// of being lost with the process that carried it.
///
/// Only the app claims anything; the command-line roles come and go without touching this.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private static readonly TimeSpan HandoverTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The names this process holds. A named mutex is owned by a thread and is reentrant for it, so asking
    /// twice on one thread succeeds twice; across processes, which is the case that matters, it does not.
    /// Keeping the names makes the answer the same either way instead of depending on who is asking.
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> Held = new(StringComparer.Ordinal);

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();
    private readonly ILogger _log;
    private readonly string _pipe;
    private NamedPipeServerStream? _server;

    /// <summary>
    /// Per user, because two people signed in to the same machine each get their own DeskPair. The mutex
    /// lives in the session namespace for that reason; pipe names are machine-wide, so the name carries the
    /// user as well.
    /// </summary>
    private static string DefaultKey => $"DeskPair.{Environment.UserName}";

    private SingleInstance(Mutex mutex, string pipe, ILogger log)
    {
        _mutex = mutex;
        _pipe = pipe;
        _log = log;
    }

    /// <summary>A later launch, with the arguments it was started with.</summary>
    public event Action<string[]>? Launched;

    /// <summary>
    /// Takes ownership, or returns null when another process already holds it. The key is only given by
    /// tests, which need a name of their own so they do not answer for a DeskPair the user is running.
    /// </summary>
    public static SingleInstance? Claim(ILogger log, string? key = null)
    {
        key ??= DefaultKey;
        if (!Held.TryAdd(key, 0))
        {
            return null;
        }

        var mutex = new Mutex(initiallyOwned: false, $@"Local\{key}");
        bool owned;
        try
        {
            owned = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died without releasing it, which leaves the mutex ours.
            owned = true;
        }

        if (!owned)
        {
            Held.TryRemove(key, out _);
            mutex.Dispose();
            return null;
        }

        var instance = new SingleInstance(mutex, key, log);
        instance.Listen();
        return instance;
    }

    /// <summary>
    /// Tells the running instance that someone tried to start another, and what they asked for. False when
    /// nobody answered, which the caller treats as "start normally": a stale mutex with no listener must not
    /// leave the user unable to open the application at all.
    /// </summary>
    public static bool Signal(string[] args, ILogger log, string? key = null)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", key ?? DefaultKey, PipeDirection.InOut);
            pipe.Connect((int)HandoverTimeout.TotalMilliseconds);

            // Length first, then the bytes, then wait to be told they arrived.
            //
            // This used to write the arguments and close, leaving the far end to find the end of the
            // message by the pipe closing. Two things were wrong with that. A launch with no arguments
            // wrote nothing at all, so the whole exchange was a connect and a disconnect, which the
            // listener could miss if it had not reached its accept yet -- the window opened by starting
            // DeskPair and immediately starting it again. And "true" meant the bytes had been written,
            // not that anybody had them, so this could report a handover and exit while the launch went
            // nowhere.
            byte[] payload = Encoding.UTF8.GetBytes(string.Join('\n', args));
            pipe.Write(BitConverter.GetBytes(payload.Length));
            pipe.Write(payload);
            pipe.Flush();

            return pipe.ReadByte() == Received;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            log.LogWarning(e, "Another instance holds the lock but did not answer; starting anyway");
            return false;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();

        // The pipe goes here, not when the accept loop notices the cancellation. Cancelling only asks,
        // and the loop answers on a pool thread whenever it is next scheduled -- so the name stayed taken
        // after Dispose had returned, and the next DeskPair to start found every instance of it in use
        // and could not listen at all. There is one instance of this pipe by design, so letting go of it
        // has to be part of letting go.
        Interlocked.Exchange(ref _server, null)?.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (Exception e) when (e is ApplicationException or ObjectDisposedException)
        {
        }

        _mutex.Dispose();
        _stop.Dispose();
        Held.TryRemove(_pipe, out _);
    }

    /// <summary>
    /// Starts answering other launches, and does not return until it can.
    ///
    /// The first pipe instance is created here, on the caller's thread, rather than inside the task. It
    /// used to be inside, which left a window where Claim had returned -- so the mutex was held and this
    /// looked like the running copy -- while nothing was listening yet. A launch landing in that window
    /// got "another instance holds the lock but did not answer" and started a second DeskPair, which is
    /// the one thing this class exists to prevent. The window is small and opens exactly when it is most
    /// likely to matter: while the machine is busy starting the first copy.
    /// </summary>
    private void Listen()
    {
        _server = NewServer();
        _ = Task.Run(AcceptLoopAsync);
    }

    /// <summary>The one byte the listener sends back, so the other instance knows it was heard.</summary>
    private const byte Received = 1;

    /// <summary>
    /// Reads one handover: a four-byte length, then that many bytes of newline-separated arguments.
    /// </summary>
    private static async Task<string[]> ReadArgumentsAsync(Stream pipe, CancellationToken ct)
    {
        byte[] header = new byte[sizeof(int)];
        await pipe.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int length = BitConverter.ToInt32(header);

        // A length this side did not write. Refusing beats allocating whatever a stranger asked for.
        if (length is < 0 or > 64 * 1024)
        {
            throw new IOException($"A handover claimed to be {length} bytes.");
        }

        if (length == 0)
        {
            return [];
        }

        byte[] payload = new byte[length];
        await pipe.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(payload).Split('\n');
    }

    /// <summary>
    /// Two instances at most: the one waiting, and, for as long as it takes to hear a launch out, the one that
    /// launch connected to (see <see cref="AcceptLoopAsync"/>).
    /// </summary>
    private NamedPipeServerStream NewServer() =>
        new(_pipe, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private async Task AcceptLoopAsync()
    {
        while (Volatile.Read(ref _server) is { } server)
        {
            bool connected;
            try
            {
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                connected = true;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                _log.LogDebug(e, "Waiting for another instance failed");
                connected = false;
            }

            // The next instance is made before this one goes. On macOS and Linux every instance of a name shares one
            // listening socket, which closes with the last of them: this one going first closed it, and a launch
            // already waiting behind this one was dropped unheard -- told that nobody answered, it started a second
            // DeskPair. Three launches in a row did that on a Mac every time. And it is made before this launch is
            // answered, so the next one never finds no instance: on Windows it waited for one, two seconds at most,
            // and a stalled process (a GitHub runner, once) made none in time.
            bool listening = Renew(server);
            if (connected)
            {
                await AnswerAsync(server).ConfigureAwait(false);
            }

            server.Dispose();
            if (!listening)
            {
                return;
            }
        }
    }

    /// <summary>Hears one launch out, and tells it so.</summary>
    private async Task AnswerAsync(NamedPipeServerStream server)
    {
        try
        {
            string[] args = await ReadArgumentsAsync(server, _stop.Token).ConfigureAwait(false);
            _log.LogInformation("Another instance was started ({Args}); raising this one", string.Join(' ', args));
            Launched?.Invoke(args);

            // Only now, so the other instance exits knowing it was heard rather than knowing it
            // managed to write.
            server.WriteByte(Received);
            server.Flush();
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // One failed handover must not end the listener: the next launch should still be heard.
            _log.LogDebug(e, "A handover from another instance failed");
        }
    }

    /// <summary>
    /// Puts a new instance where <paramref name="current"/> was. False, and nothing more is listened for, when none can
    /// be made or when this is being disposed: Dispose has let go of the name, and it must not be taken again.
    /// </summary>
    private bool Renew(NamedPipeServerStream current)
    {
        NamedPipeServerStream next;
        try
        {
            next = NewServer();
        }
        catch (IOException e)
        {
            // Nothing left to listen on. Shutting down is the usual reason.
            _log.LogDebug(e, "Could not listen for the next handover");
            return false;
        }

        if (Interlocked.CompareExchange(ref _server, next, current) == current)
        {
            return true;
        }

        next.Dispose();
        return false;
    }
}
