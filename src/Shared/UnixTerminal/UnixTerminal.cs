using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Terminal;

namespace DeskPair.Platform.Unix.Terminal;

/// <summary>How a platform starts a process on a new pseudo-terminal; the one part Linux and macOS do differently.</summary>
internal interface IPtySpawner
{
    /// <summary>
    /// Opens a pty, starts <paramref name="path"/> as the leader of a new session with the pty as its
    /// controlling terminal and standard streams, and returns the master side and the pid. Throws
    /// <see cref="TerminalStartException"/> with a reason when it cannot.
    /// </summary>
    (int Master, int Pid) Spawn(string path, string[] argv, string[] env, string directory, int columns, int rows);

    void Resize(int master, int columns, int rows);
}

/// <summary>
/// The process behind a pty: signalled, waited for and killed. A shell this process started is its own
/// child and all three are system calls; a shell the Linux daemon started for it (as root, or as the
/// signed-in user) is nobody this process may signal or reap, and each goes back through the daemon.
/// </summary>
internal interface IPtyProcess
{
    /// <summary>For the log: "pid 1234", or the daemon's name for it.</summary>
    string Description { get; }

    /// <summary>SIGHUP, SIGTERM or SIGKILL to the shell's process group.</summary>
    void Signal(int signal);

    /// <summary>Blocks until the shell has exited and returns how, as a shell would report it; -1 when that cannot be known. Returns early once <paramref name="stop"/> says so.</summary>
    int WaitForExit(Func<bool> stop);

    /// <summary>Kills everything the shell left behind; how many, or -1 when the count is not known.</summary>
    int KillEverything();
}

/// <summary>A shell this process started itself: its child, in a session of its own.</summary>
internal sealed class LocalPtyProcess(int pid) : IPtyProcess
{
    private const int SIGHUP = 1;

    public string Description => $"pid {pid}";

    public void Signal(int signal)
    {
        _ = UnixNative.killpg(pid, signal);
        if (signal == SIGHUP)
        {
            _ = UnixNative.kill(pid, signal);
        }
    }

    public int WaitForExit(Func<bool> stop)
    {
        int status = 0;
        while (true)
        {
            int r = UnixNative.waitpid(pid, ref status, 0);
            if (r == pid)
            {
                return UnixShell.ExitCode(status);
            }

            if (r < 0 && Marshal.GetLastPInvokeError() == UnixNative.EINTR)
            {
                continue;
            }

            return -1; // ECHILD: somebody else reaped it; the code is lost, the exit is not
        }
    }

    public int KillEverything() => UnixSession.KillAll(pid);
}

/// <summary>A shell on a pseudo-terminal, as the engine's own account. Root comes through the daemon (Linux).</summary>
internal sealed class UnixTerminalHost(IPtySpawner spawner, ILogger log) : ITerminalHost
{
    public bool IsAvailable => true;

    public string? UnavailableReason => null;

    public string DescribeIdentity(TerminalRunAs runAs) => UnixAccount.Current().Name;

    public ValueTask<ITerminal> StartAsync(TerminalRunAs runAs, int columns, int rows, CancellationToken ct)
    {
        UnixAccount me = UnixAccount.Current();
        string shell = UnixShell.Choose(me.Shell, File.Exists);
        string home = Directory.Exists(me.Home) ? me.Home : "/";
        string[] env = UnixShell.Environment(me.Name, home, shell, System.Environment.GetEnvironmentVariable("LANG"), OperatingSystem.IsMacOS());
        (int master, int pid) = spawner.Spawn(shell, UnixShell.Arguments(shell), env, home, columns, rows);
        log.LogInformation("Shell {Shell} started as {Identity}, pid {Pid}", shell, me.Name, pid);
        return ValueTask.FromResult<ITerminal>(new UnixPtyTerminal(spawner, master, new LocalPtyProcess(pid), me.Name, shell, log));
    }
}

/// <summary>The account this process runs as, from the passwd database.</summary>
internal readonly record struct UnixAccount(string Name, string Home, string Shell)
{
    private static readonly object PasswdLock = new();

    public static UnixAccount Current() => Of(UnixNative.geteuid());

    /// <summary>The account with that uid; its name is "uid N" when the passwd database does not know it.</summary>
    public static UnixAccount Of(uint uid)
    {
        // getpwuid answers from a static buffer; the lock keeps two callers from reading each other's answer.
        lock (PasswdLock)
        {
            nint pw = UnixNative.getpwuid(uid);
            if (pw == 0)
            {
                return new UnixAccount($"uid {uid}", "/", string.Empty);
            }

            // struct passwd: name first on both; home and shell sit after macOS's extra change/class fields.
            (int dir, int shell) = OperatingSystem.IsMacOS() ? (48, 56) : (32, 40);
            return new UnixAccount(
                Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(pw)) ?? $"uid {uid}",
                Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(pw, dir)) ?? "/",
                Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(pw, shell)) ?? string.Empty);
        }
    }
}

/// <summary>
/// One shell. Three threads, each doing one blocking thing: reading the master (with poll, so it can be
/// told to stop -- a process that kept the pty open would otherwise hold a read for ever), writing the
/// viewer's input (so a program that is not reading never blocks the session feeding it), and waiting for
/// the shell to exit.
///
/// Ending it is the part that matters, because under the daemon this may be a root shell: hang up the
/// terminal, give the session two seconds, then SIGKILL every process still in it -- not just the shell's
/// process group, which would miss the jobs it started.
/// </summary>
internal sealed class UnixPtyTerminal : ITerminal
{
    private const int SIGHUP = 1;
    private const int SIGKILL = 9;
    private const int SIGTERM = 15;
    private static readonly TimeSpan DrainAfterExit = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan HangupGrace = TimeSpan.FromSeconds(2);

    private readonly IPtySpawner _spawner;
    private readonly ILogger _log;
    private readonly TerminalOutputStream _output = new();
    private readonly BlockingCollection<byte[]> _input = [];
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _reader;
    private readonly Thread _writer;
    private readonly Thread _waiter;
    private readonly IPtyProcess _process;
    private volatile bool _stopWaiting;
    private int _master;
    private volatile bool _stopReading;
    private int _disposed;

    public UnixPtyTerminal(IPtySpawner spawner, int master, IPtyProcess process, string identity, string shell, ILogger log)
    {
        _spawner = spawner;
        _master = master;
        _process = process;
        _log = log;
        Identity = identity;
        Shell = shell;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "pty-read" };
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "pty-write" };
        _waiter = new Thread(WaitLoop) { IsBackground = true, Name = "pty-wait" };
        _reader.Start();
        _writer.Start();
        _waiter.Start();
    }

    public string Identity { get; }

    public string Shell { get; }

    public Stream Output => _output;

    public Task<int> Exited => _exited.Task;

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0 || _input.IsAddingCompleted)
        {
            throw new IOException("The shell has ended.");
        }

        _input.Add(data.ToArray(), ct);
        return ValueTask.CompletedTask;
    }

    public void Resize(int columns, int rows)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _spawner.Resize(_master, Math.Clamp(columns, 1, 1000), Math.Clamp(rows, 1, 1000));
        }
    }

    public void Signal(TerminalSignalKind signal)
    {
        switch (signal)
        {
            case TerminalSignalKind.Interrupt:
                // What typing Ctrl+C does: the line discipline turns it into SIGINT for the foreground job.
                try
                {
                    _input.Add([0x03]);
                }
                catch (InvalidOperationException)
                {
                }

                break;
            case TerminalSignalKind.Terminate:
                _process.Signal(SIGTERM);
                break;
            case TerminalSignalKind.Kill:
                _process.Signal(SIGKILL);
                break;
            case TerminalSignalKind.Hangup:
                _process.Signal(SIGHUP);
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _input.CompleteAdding();
        if (!_exited.Task.IsCompleted)
        {
            _process.Signal(SIGHUP);
            try
            {
                await _exited.Task.WaitAsync(HangupGrace).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }

        // Whatever is left in the session -- the shell if it ignored the hangup, the jobs it started if it
        // did not pass the hangup on -- is killed. A process that made its own session escapes, as it
        // would from an ssh logout.
        int killed = _process.KillEverything();
        if (killed != 0)
        {
            _log.LogInformation("Killed {Count} process(es) left behind by shell {Process}", killed < 0 ? "the" : killed.ToString(), _process.Description);

            // Let the waiter collect the shell, so what is reported is how it ended rather than "unknown".
            try
            {
                await _exited.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }

        _stopReading = true;
        _stopWaiting = true;
        _reader.Join(TimeSpan.FromSeconds(2));
        _writer.Join(TimeSpan.FromSeconds(2));
        int master = Interlocked.Exchange(ref _master, -1);
        if (master >= 0)
        {
            _ = UnixNative.close(master);
        }

        _exited.TrySetResult(-1);
        _output.Complete();
    }

    private unsafe void ReadLoop()
    {
        byte[] buffer = new byte[16 * 1024];
        try
        {
            var fds = new UnixNative.PollFd { Fd = _master, Events = UnixNative.POLLIN };
            while (!_stopReading)
            {
                fds.Revents = 0;
                int ready = UnixNative.poll(&fds, 1, 200);
                if (ready == 0 || (ready < 0 && Marshal.GetLastPInvokeError() == UnixNative.EINTR))
                {
                    continue;
                }

                if (ready < 0)
                {
                    break;
                }

                nint n;
                fixed (byte* p = buffer)
                {
                    n = UnixNative.read(_master, p, (nuint)buffer.Length);
                }

                if (n > 0)
                {
                    _output.Post(buffer.AsSpan(0, (int)n));
                    continue;
                }

                int errno = Marshal.GetLastPInvokeError();
                if (n < 0 && (errno == UnixNative.EINTR || errno == UnixNative.EAGAIN))
                {
                    continue;
                }

                break; // 0, or EIO: every process holding the slave has closed it
            }
        }
        finally
        {
            _output.Complete();
        }
    }

    private unsafe void WriteLoop()
    {
        try
        {
            foreach (byte[] data in _input.GetConsumingEnumerable())
            {
                fixed (byte* p = data)
                {
                    int done = 0;
                    while (done < data.Length)
                    {
                        nint n = UnixNative.write(_master, p + done, (nuint)(data.Length - done));
                        if (n < 0)
                        {
                            if (Marshal.GetLastPInvokeError() == UnixNative.EINTR)
                            {
                                continue;
                            }

                            return;
                        }

                        done += (int)n;
                    }
                }
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void WaitLoop()
    {
        int code = _process.WaitForExit(() => _stopWaiting);
        _log.LogInformation("Shell {Shell} ({Process}) exited with code {Code}", Shell, _process.Description, code);
        _exited.TrySetResult(code);

        // The output ends by itself when the last holder of the slave closes it; a background job that
        // kept it open would hold it for ever. Give the tail a moment, then stop reading regardless.
        Thread.Sleep(DrainAfterExit);
        _stopReading = true;
    }
}

/// <summary>Finding and killing every process in a session: /proc on Linux, libproc on macOS, getsid on both.</summary>
internal static class UnixSession
{
    private const int SIGKILL = 9;

    public static int KillAll(int session)
    {
        int killed = 0;
        foreach (int pid in AllPids())
        {
            if (pid > 1 && UnixNative.getsid(pid) == session && UnixNative.kill(pid, SIGKILL) == 0)
            {
                killed++;
            }
        }

        return killed;
    }

    private static IEnumerable<int> AllPids()
    {
        if (OperatingSystem.IsMacOS())
        {
            int[] pids = new int[16384];
            int count;
            unsafe
            {
                fixed (int* p = pids)
                {
                    count = UnixNative.proc_listallpids(p, pids.Length * sizeof(int));
                }
            }

            return count <= 0 ? [] : pids.Take(Math.Min(count, pids.Length)).Where(p => p > 0).ToArray();
        }

        try
        {
            return Directory.EnumerateDirectories("/proc")
                .Select(d => int.TryParse(Path.GetFileName(d), out int pid) ? pid : 0)
                .Where(p => p > 0)
                .ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>The libc calls both systems share with the same signature and no variadic arguments.</summary>
internal static unsafe partial class UnixNative
{
    private const string Lib = "libc";

    public const short POLLIN = 1;
    public const int EINTR = 4;
    public static int EAGAIN => OperatingSystem.IsMacOS() ? 35 : 11;

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport(Lib)]
    public static partial uint geteuid();

    [LibraryImport(Lib)]
    public static partial nint getpwuid(uint uid);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int poll(PollFd* fds, nuint count, int timeout);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint read(int fd, byte* buffer, nuint count);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint write(int fd, byte* buffer, nuint count);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int close(int fd);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int kill(int pid, int signal);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int killpg(int pgrp, int signal);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int getsid(int pid);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int waitpid(int pid, ref int status, int options);

    /// <summary>macOS only: every pid on the system, written into a buffer of that many bytes; returns how many pids.</summary>
    [LibraryImport("/usr/lib/libSystem.B.dylib")]
    public static partial int proc_listallpids(int* buffer, int bufferSize);
}
