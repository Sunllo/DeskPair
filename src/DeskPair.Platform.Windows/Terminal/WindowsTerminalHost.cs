using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Terminal;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Terminal;

/// <summary>
/// Shells behind ConPTY. "Highest" is the account the engine runs as: LocalSystem under the service, the
/// signed-in user when the app runs the engine itself. "User" under the service is the signed-in user,
/// started with their token (<c>WTSQueryUserToken</c>, which only SYSTEM may call); run from the app the
/// engine already is that user. <see cref="DescribeIdentity"/> reports the account the shell will really
/// get, because the approval card repeats it to a person deciding whether to allow it.
/// </summary>
public sealed class WindowsTerminalHost(ILogger log) : ITerminalHost
{
    public bool IsAvailable => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    public string? UnavailableReason => IsAvailable ? null : "This version of Windows has no pseudo console; Windows 10 1809 or later is needed.";

    public string DescribeIdentity(TerminalRunAs runAs)
    {
        (string engine, bool system) = Engine();
        return WindowsShellIdentity.Plan(runAs, system, engine, system && runAs == TerminalRunAs.User ? SignedInUserName() : null).Identity;
    }

    public ValueTask<ITerminal> StartAsync(TerminalRunAs runAs, int columns, int rows, CancellationToken ct)
    {
        if (!IsAvailable)
        {
            throw new TerminalStartException(UnavailableReason!);
        }

        (string path, string commandLine) = WindowsShell.Choose(Environment.GetEnvironmentVariable("SystemRoot"), Environment.GetEnvironmentVariable("ComSpec"), File.Exists);
        (string engine, bool system) = Engine();
        if (!(system && runAs == TerminalRunAs.User))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return ValueTask.FromResult<ITerminal>(ConPtyTerminal.Start(commandLine, Directory.Exists(home) ? home : null, columns, rows, engine, path, log));
        }

        // The signed-in user, from a SYSTEM engine: their token, their environment, their profile folder.
        nint token = UserSessions.SignedInUserToken();
        if (token == 0)
        {
            throw new TerminalStartException(WindowsShellIdentity.NobodySignedIn);
        }

        nint environment = 0;
        try
        {
            using var user = new WindowsIdentity(token);
            _ = UserSessions.CreateEnvironmentBlock(out environment, token, 0);
            string? profile = UserSessions.ProfileDirectory(token);
            return ValueTask.FromResult<ITerminal>(ConPtyTerminal.Start(commandLine, profile, columns, rows, user.Name, path, log, token, environment));
        }
        finally
        {
            if (environment != 0)
            {
                _ = UserSessions.DestroyEnvironmentBlock(environment);
            }

            _ = ConPty.CloseHandle(token);
        }
    }

    private static (string Name, bool System) Engine()
    {
        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        return (me.Name, me.IsSystem);
    }

    private static string? SignedInUserName()
    {
        nint token = UserSessions.SignedInUserToken();
        if (token == 0)
        {
            return null;
        }

        try
        {
            using var user = new WindowsIdentity(token);
            return user.Name;
        }
        finally
        {
            _ = ConPty.CloseHandle(token);
        }
    }
}

/// <summary>Whose shell a request gets on Windows, decided without touching a token so it can be tested anywhere.</summary>
public static class WindowsShellIdentity
{
    public const string NobodySignedIn = "Nobody is signed in to this computer, so there is no user to start a shell as. The owner can choose the highest identity instead in Settings.";

    /// <summary>
    /// "Highest" is always the engine's own account. "User" is the signed-in user when the engine is SYSTEM
    /// and somebody is signed in, nothing (and a refusal to start) when nobody is, and the engine's own
    /// account when the engine already is a user.
    /// </summary>
    public static (string Identity, bool UsesUserToken) Plan(TerminalRunAs runAs, bool engineIsSystem, string engineAccount, string? signedInUser)
    {
        if (runAs != TerminalRunAs.User || !engineIsSystem)
        {
            return (engineAccount, false);
        }

        return (signedInUser ?? string.Empty, true);
    }
}

/// <summary>Which program a Windows shell is. Pure, so the choice is testable without starting anything.</summary>
public static class WindowsShell
{
    /// <summary>
    /// Windows PowerShell when it is there -- it is on every supported Windows and it is what an
    /// administrator reaching a machine remotely expects -- otherwise the command interpreter.
    /// </summary>
    public static (string Path, string CommandLine) Choose(string? systemRoot, string? comSpec, Func<string, bool> exists)
    {
        string root = string.IsNullOrEmpty(systemRoot) ? @"C:\Windows" : systemRoot;
        string powershell = Path.Combine(root, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (exists(powershell))
        {
            return (powershell, $"\"{powershell}\" -NoLogo");
        }

        string cmd = !string.IsNullOrEmpty(comSpec) && exists(comSpec) ? comSpec : Path.Combine(root, "System32", "cmd.exe");
        return (cmd, $"\"{cmd}\"");
    }
}

/// <summary>
/// One shell behind a pseudo console. The shell and everything it starts live in a job object that kills
/// them when its handle closes, so a shell whose viewer has gone cannot leave a SYSTEM process behind,
/// and nor can the `start` it ran.
///
/// Three threads, each doing one blocking thing: reading the console's output, writing the viewer's
/// input (so a program that is not reading its input never blocks the session that feeds it), and waiting
/// for the shell to exit. A pseudo console does not end its output when the shell exits -- only closing
/// the console does -- so the exit waiter closes it, a moment later, once the last output has had time to
/// be read.
/// </summary>
internal sealed unsafe class ConPtyTerminal : ITerminal
{
    private static readonly TimeSpan DrainAfterExit = TimeSpan.FromMilliseconds(200);

    private readonly ILogger _log;
    private readonly TerminalOutputStream _output = new();
    private readonly BlockingCollection<byte[]> _input = [];
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _lock = new();
    private nint _console;
    private nint _inputWrite;
    private nint _outputRead;
    private nint _process;
    private nint _job;
    private Thread? _reader;
    private Thread? _writer;
    private Thread? _waiter;
    private bool _consoleClosed;
    private bool _disposed;

    private ConPtyTerminal(ILogger log, string identity, string shell)
    {
        _log = log;
        Identity = identity;
        Shell = shell;
    }

    public string Identity { get; }

    public string Shell { get; }

    public Stream Output => _output;

    public Task<int> Exited => _exited.Task;

    /// <summary>Starts a shell; as the owner of <paramref name="userToken"/> with <paramref name="environment"/> when one is given.</summary>
    public static ConPtyTerminal Start(string commandLine, string? directory, int columns, int rows, string identity, string shell, ILogger log, nint userToken = 0, nint environment = 0)
    {
        var t = new ConPtyTerminal(log, identity, shell);
        nint inputRead = 0;
        nint outputWrite = 0;
        nint attributes = 0;
        try
        {
            if (ConPty.CreatePipe(out inputRead, out t._inputWrite, 0, 0) == 0 || ConPty.CreatePipe(out t._outputRead, out outputWrite, 0, 0) == 0)
            {
                throw Failure("CreatePipe");
            }

            int hr = ConPty.CreatePseudoConsole(Size(columns, rows), inputRead, outputWrite, 0, out t._console);
            if (hr != 0)
            {
                throw new TerminalStartException($"This computer could not create a pseudo console (0x{hr:X8}).");
            }

            // The console holds its own references; ours would keep the pipes open past its end.
            Close(ref inputRead);
            Close(ref outputWrite);

            nint size = 0;
            _ = ConPty.InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (ConPty.InitializeProcThreadAttributeList(attributes, 1, 0, ref size) == 0
                || ConPty.UpdateProcThreadAttribute(attributes, 0, ConPty.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, t._console, sizeof(nint), 0, 0) == 0)
            {
                throw Failure("the pseudo console attribute");
            }

            t._job = ConPty.CreateJobObjectW(0, 0);
            var limits = new ConPty.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = ConPty.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (t._job == 0 || ConPty.SetInformationJobObject(t._job, ConPty.JobObjectExtendedLimitInformation, ref limits, (uint)sizeof(ConPty.JOBOBJECT_EXTENDED_LIMIT_INFORMATION)) == 0)
            {
                throw Failure("the job object");
            }

            var startup = new ConPty.STARTUPINFOEXW { lpAttributeList = attributes };
            startup.StartupInfo.cb = (uint)sizeof(ConPty.STARTUPINFOEXW);

            // Standard handles set, and empty: without this a child of an engine whose own handles are
            // redirected (the service, a test host) writes to those instead of the console.
            startup.StartupInfo.dwFlags = ConPty.STARTF_USESTDHANDLES;

            // "Ignore Ctrl+C" is a per-process flag that children inherit, and a process started without a
            // console -- a service, a test host, anything created with CREATE_NEW_PROCESS_GROUP -- usually
            // has it set. The shell would inherit it, and so would everything run from the shell: a Ctrl+C
            // typed at a runaway ping would do nothing. Clearing it here is harmless for this process, which
            // has no console of its own to be interrupted from in the roles that host a terminal.
            _ = ConPty.SetConsoleCtrlHandler(0, 0);

            char[] line = (commandLine + '\0').ToCharArray();
            ConPty.PROCESS_INFORMATION process;
            fixed (char* cmd = line)
            {
                // Suspended until it is in the job, so nothing it starts in its first instant escapes.
                const uint flags = ConPty.EXTENDED_STARTUPINFO_PRESENT | ConPty.CREATE_UNICODE_ENVIRONMENT | ConPty.CREATE_SUSPENDED;
                int created = userToken != 0
                    ? UserSessions.CreateProcessAsUserW(userToken, null, cmd, 0, 0, 0, flags, environment, directory, ref startup, out process)
                    : ConPty.CreateProcessW(null, cmd, 0, 0, 0, flags, 0, directory, ref startup, out process);
                if (created == 0)
                {
                    throw Failure(userToken != 0 ? "CreateProcessAsUser" : "CreateProcess");
                }
            }

            t._process = process.hProcess;
            if (ConPty.AssignProcessToJobObject(t._job, process.hProcess) == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                _ = ConPty.TerminateProcess(process.hProcess, 1);
                _ = ConPty.CloseHandle(process.hThread);
                throw new TerminalStartException($"The shell could not be put in a job object (error {error}), so it was not started.");
            }

            _ = ConPty.ResumeThread(process.hThread);
            _ = ConPty.CloseHandle(process.hThread);
            log.LogInformation("Shell {Shell} started as {Identity}, pid {Pid}", shell, identity, process.dwProcessId);
            t.Run();
            return t;
        }
        catch
        {
            Close(ref inputRead);
            Close(ref outputWrite);
            t.Release();
            throw;
        }
        finally
        {
            if (attributes != 0)
            {
                ConPty.DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (_disposed || _input.IsAddingCompleted)
        {
            throw new IOException("The shell has ended.");
        }

        _input.Add(data.ToArray(), ct);
        return ValueTask.CompletedTask;
    }

    public void Resize(int columns, int rows)
    {
        lock (_lock)
        {
            if (!_consoleClosed && _console != 0)
            {
                _ = ConPty.ResizePseudoConsole(_console, Size(columns, rows));
            }
        }
    }

    /// <summary>Windows has no signals. Interrupt is what Ctrl+C types; the rest end the whole job.</summary>
    public void Signal(TerminalSignalKind signal)
    {
        if (signal == TerminalSignalKind.Interrupt)
        {
            try
            {
                _input.Add([0x03]);
            }
            catch (InvalidOperationException)
            {
            }

            return;
        }

        lock (_lock)
        {
            if (_job != 0)
            {
                _ = ConPty.TerminateJobObject(_job, signal == TerminalSignalKind.Kill ? 137u : 143u);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        lock (_lock)
        {
            if (_job != 0)
            {
                _ = ConPty.TerminateJobObject(_job, 1);
            }
        }

        CloseConsole();
        _input.CompleteAdding();
        _writer?.Join(TimeSpan.FromSeconds(2));
        _reader?.Join(TimeSpan.FromSeconds(2));
        _waiter?.Join(TimeSpan.FromSeconds(2));
        Release();
        _exited.TrySetResult(-1);
        _output.Complete();
        return ValueTask.CompletedTask;
    }

    private void Run()
    {
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "conpty-read" };
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "conpty-write" };
        _waiter = new Thread(WaitLoop) { IsBackground = true, Name = "conpty-wait" };
        _reader.Start();
        _writer.Start();
        _waiter.Start();
    }

    private void ReadLoop()
    {
        byte[] buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                uint read;
                fixed (byte* p = buffer)
                {
                    if (ConPty.ReadFile(_outputRead, p, (uint)buffer.Length, out read, 0) == 0 || read == 0)
                    {
                        break; // ERROR_BROKEN_PIPE once the console is closed: end of output
                    }
                }

                _output.Post(buffer.AsSpan(0, (int)read));
            }
        }
        finally
        {
            _output.Complete();
        }
    }

    private void WriteLoop()
    {
        try
        {
            foreach (byte[] data in _input.GetConsumingEnumerable())
            {
                fixed (byte* p = data)
                {
                    uint done = 0;
                    while (done < data.Length)
                    {
                        if (ConPty.WriteFile(_inputWrite, p + done, (uint)data.Length - done, out uint wrote, 0) == 0)
                        {
                            return;
                        }

                        done += wrote;
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
        _ = ConPty.WaitForSingleObject(_process, ConPty.INFINITE);
        int code = ConPty.GetExitCodeProcess(_process, out uint exit) != 0 ? unchecked((int)exit) : -1;
        _log.LogInformation("Shell {Shell} exited with code {Code}", Shell, code);
        Thread.Sleep(DrainAfterExit);
        CloseConsole();
        _exited.TrySetResult(code);
    }

    /// <summary>
    /// Ends the console, and with it the output. The reader is still reading when this runs, which is what
    /// lets ClosePseudoConsole return: on some Windows builds it waits for the last output to be taken.
    /// </summary>
    private void CloseConsole()
    {
        nint console;
        lock (_lock)
        {
            if (_consoleClosed || _console == 0)
            {
                _consoleClosed = true;
                return;
            }

            _consoleClosed = true;
            console = _console;
        }

        ConPty.ClosePseudoConsole(console);
    }

    private void Release()
    {
        lock (_lock)
        {
            if (_console != 0 && !_consoleClosed)
            {
                ConPty.ClosePseudoConsole(_console);
                _consoleClosed = true;
            }

            Close(ref _inputWrite);
            Close(ref _outputRead);
            Close(ref _process);

            // Last: closing the job kills anything still inside it.
            Close(ref _job);
        }
    }

    private static ConPty.COORD Size(int columns, int rows) =>
        new() { X = (short)Math.Clamp(columns, 1, 1000), Y = (short)Math.Clamp(rows, 1, 1000) };

    private static void Close(ref nint handle)
    {
        if (handle != 0)
        {
            _ = ConPty.CloseHandle(handle);
            handle = 0;
        }
    }

    private static TerminalStartException Failure(string what) =>
        new($"The shell could not be started: {what} failed (error {Marshal.GetLastPInvokeError()}).");
}
