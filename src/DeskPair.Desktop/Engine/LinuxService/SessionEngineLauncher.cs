using System.Diagnostics;
using DeskPair.Platform.Linux.Capture.Drm;
using DeskPair.Platform.Linux.Hosting;
using DeskPair.Platform.Linux.Input;
using DeskPair.Platform.Linux.Native;
using DeskPair.Platform.Linux.Wayland.Agent;
using Microsoft.Extensions.Logging;

namespace DeskPair.Desktop.Engine.LinuxService;

/// <summary>
/// The world as the daemon actually finds it: logind for who is at the screen, a process for the engine,
/// the socketpair and the two device descriptors handed across at start.
///
/// The engine is started with what it needs in argv and inherited descriptors, and one thing in the
/// environment: the IPC token. Everything on a command line is in <c>/proc/&lt;pid&gt;/cmdline</c>, which
/// every account on the machine can read, and a token there let any local user drive the engine. The
/// environment of a process is readable only by its own account and root, and the engine drops the
/// variable as soon as it has read it, so nothing it starts inherits it either.
/// </summary>
internal sealed class SessionEngineLauncher : IEngineWorld
{
    private readonly string _executable;
    private readonly string _dataDir;
    private readonly DrmScanoutReader? _reader;
    private readonly UinputDevice? _input;
    private readonly (uint Uid, uint Gid) _account;
    private readonly ILoggerFactory _logs;
    private readonly ILogger _log;
    private readonly string _tokenHex;
    private readonly AgentHandoff? _agents;

    /// <param name="executable">This program, started again as the engine.</param>
    /// <param name="dataDir">The engine account's home.</param>
    /// <param name="reader">The card, or null on a machine with no display hardware: the engine then offers no picture.</param>
    /// <param name="input">The virtual devices, or null where <c>/dev/uinput</c> does not exist: the engine then ignores input.</param>
    /// <param name="account">The account the engine drops to.</param>
    /// <param name="tokenHex">The IPC token, handed over in the environment and never on the command line.</param>
    /// <param name="logs">Where the daemon's half logs.</param>
    /// <param name="agents">Where a session agent's socket waits for the engine; null to start no agents.</param>
    public SessionEngineLauncher(
        string executable, string dataDir, DrmScanoutReader? reader, UinputDevice? input, (uint Uid, uint Gid) account, string tokenHex, ILoggerFactory logs,
        AgentHandoff? agents = null)
    {
        _agents = agents;
        _executable = executable;
        _dataDir = dataDir;
        _reader = reader;
        _input = input;
        _account = account;
        _tokenHex = tokenHex;
        _logs = logs;
        _log = logs.CreateLogger("launcher");
    }

    public ActiveSession? ActiveSession() => LinuxSessions.Active();

    public IEngineHandle? Launch()
    {
        (int mine, int theirs) = UnixSocketMsg.Pair();
        try
        {
            UnixSocketMsg.Inherit(theirs);
            if (_input is not null)
            {
                UnixSocketMsg.Inherit(_input.Keyboard);
                UnixSocketMsg.Inherit(_input.Pointer);
                UnixSocketMsg.Inherit(_input.Relative);
            }

            string arguments = string.Join(' ', EngineArguments(_dataDir, _account, theirs, _input?.Keyboard ?? -1, _input?.Pointer ?? -1, _input?.Relative ?? -1));

            // A single-file publish is the executable; a development run is `dotnet DeskPair.dll`.
            string exe = _executable;
            if (Path.GetFileName(exe) == "dotnet")
            {
                arguments = Quote(Path.Combine(AppContext.BaseDirectory, "DeskPair.dll")) + " " + arguments;
            }

            var start = new ProcessStartInfo(exe, arguments) { UseShellExecute = false };

            // A clean environment, as the engine's own account would have it. Inheriting the daemon's
            // was the first thing that broke on a real machine: HOME=/root reached a process that had
            // become 'deskpair', and a single-file build unpacks itself under $HOME, so the engine died
            // in LoggerFactory.Create with an assembly it could not find. Nothing of root's belongs in
            // a process that has given root up.
            start.Environment.Clear();
            start.Environment["HOME"] = _dataDir;
            start.Environment["USER"] = LinuxAccounts.EngineUser;
            start.Environment["LOGNAME"] = LinuxAccounts.EngineUser;
            start.Environment["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
            start.Environment["LANG"] = "C.UTF-8";
            start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(_dataDir, ".net");
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            start.Environment[PlatformServices.IpcTokenVariable] = _tokenHex;
            Process? process = Process.Start(start);
            if (process is null)
            {
                _log.LogError("Process.Start returned nothing for {Exe}", exe);
                return null;
            }

            // The daemon's own end serves this engine until it goes; the engine's end is not the daemon's to keep.
            _ = UnixSocketMsg.close(theirs);
            theirs = -1;
            // Each engine gets its own set of terminals, stopped when its channel closes: a shell never outlives the engine that asked for it.
            var terminals = new Platform.Linux.Terminal.DaemonTerminals(_logs.CreateLogger("terminal"));
            var thread = new Thread(() => DrmCaptureServer.Run(mine, _reader, _logs.CreateLogger("scanout"), CancellationToken.None, terminals, _agents))
            {
                IsBackground = true,
                Name = "deskpair-scanout",
            };
            thread.Start();
            return new Handle(process, mine);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            _log.LogError(e, "Could not start the engine");
            if (theirs >= 0)
            {
                _ = UnixSocketMsg.close(theirs);
            }

            _ = UnixSocketMsg.close(mine);
            return null;
        }
        finally
        {
            // Back to close-on-exec, so the next process the daemon starts does not get the devices too.
            if (_input is not null)
            {
                _ = UnixSocketMsg.fcntl(_input.Keyboard, UnixSocketMsg.FSetFd, 1);
                _ = UnixSocketMsg.fcntl(_input.Pointer, UnixSocketMsg.FSetFd, 1);
                _ = UnixSocketMsg.fcntl(_input.Relative, UnixSocketMsg.FSetFd, 1);
            }
        }
    }

    /// <summary>
    /// /run/deskpair/&lt;uid&gt;/ipc.token, owned by that user and readable only by them: the first place
    /// <c>HostLink.TokenDirectories()</c> looks.
    /// </summary>
    public void PublishToken(uint uid)
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // this class is built into the portable target too, and the mode calls are Unix-only
        }

        string dir = Path.Combine("/run/deskpair", uid.ToString());
        string path = Path.Combine(dir, "ipc.token");
        try
        {
            Directory.CreateDirectory(dir);
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            _ = LinuxAccounts.Chown(dir, uid, uid);
            File.Delete(path);
            File.WriteAllText(path, _tokenHex);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _ = LinuxAccounts.Chown(path, uid, uid);
            _log.LogInformation("IPC token published for uid {Uid} at {Path}", uid, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogError(e, "Could not publish the IPC token for uid {Uid}; that user's app will not find the engine", uid);
        }
    }

    public void ReleaseInput() => _input?.PanicRelease();

    /// <summary>
    /// A session agent, started as <paramref name="session"/>'s user in that session's environment: the same program,
    /// dropping to the user as the engine drops to its account, with one end of a socketpair; the other end waits in
    /// the hand-off for the engine to ask. Its environment is found by looking at the user's own desktop processes
    /// (<see cref="LinuxSessions.EnvironmentOf"/>), since logind does not say where the session bus or the Wayland
    /// socket is, and it is built from nothing else: nothing of root's belongs in a process that is about to be a user.
    /// </summary>
    public IAgentHandle? LaunchAgent(ActiveSession session)
    {
        if (_agents is null || !OperatingSystem.IsLinux())
        {
            return null;
        }

        if (LinuxAccounts.Of(session.Uid) is not { } account)
        {
            _log.LogWarning("uid {Uid} is not in the passwd database; no session agent for session {Session}", session.Uid, session.Id);
            return null;
        }

        SessionEnvironment? found = LinuxSessions.EnvironmentOf(session.Uid, session.Id);
        (int mine, int theirs) = UnixSocketMsg.Pair();
        try
        {
            UnixSocketMsg.Inherit(theirs);
            string arguments = string.Join(' ', AgentArguments(theirs, session.Uid, account.Gid, account.Name));
            string exe = _executable;
            if (Path.GetFileName(exe) == "dotnet")
            {
                arguments = Quote(Path.Combine(AppContext.BaseDirectory, "DeskPair.dll")) + " " + arguments;
            }

            string runtime = found?.RuntimeDir ?? $"/run/user/{session.Uid}";
            var start = new ProcessStartInfo(exe, arguments) { UseShellExecute = false };
            start.Environment.Clear();
            start.Environment["HOME"] = account.Home;
            start.Environment["USER"] = account.Name;
            start.Environment["LOGNAME"] = account.Name;
            start.Environment["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
            start.Environment["LANG"] = "C.UTF-8";
            start.Environment["XDG_RUNTIME_DIR"] = runtime;
            start.Environment["XDG_SESSION_TYPE"] = "wayland";
            start.Environment["DBUS_SESSION_BUS_ADDRESS"] = found?.SessionBus ?? $"unix:path={runtime}/bus";
            SetIfKnown(start, "WAYLAND_DISPLAY", found?.WaylandDisplay);
            SetIfKnown(start, "DISPLAY", found?.Display);
            SetIfKnown(start, "XAUTHORITY", found?.XAuthority);
            SetIfKnown(start, "XDG_CURRENT_DESKTOP", found?.CurrentDesktop);

            // The engine's unpacked copy of this program's native libraries, which is already there (the engine was
            // started first) and which the agent, loading none of them, only has to find.
            start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(_dataDir, ".net");
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            Process? process = Process.Start(start);
            if (process is null)
            {
                _log.LogError("Process.Start returned nothing for the session agent");
                _ = UnixSocketMsg.close(mine);
                return null;
            }

            _ = UnixSocketMsg.close(theirs);
            theirs = -1;
            _agents.Offer(mine, session.Uid, session.Id);
            return new AgentHandle(process, mine, session.Id, _agents);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            _log.LogError(e, "Could not start a session agent for session {Session}", session.Id);
            if (theirs >= 0)
            {
                _ = UnixSocketMsg.close(theirs);
            }

            _ = UnixSocketMsg.close(mine);
            return null;
        }
    }

    public bool DesktopUp(ActiveSession session) => !OperatingSystem.IsLinux() || LinuxSessions.EnvironmentOf(session.Uid, session.Id) is not null;

    private static void SetIfKnown(ProcessStartInfo start, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            start.Environment[name] = value;
        }
    }

    /// <summary>
    /// The agent's command line: the socket, and whom to become. Nothing secret, like the engine's. The name is quoted
    /// when it needs to be: a directory's users (SSSD, a domain) can have spaces in theirs.
    /// </summary>
    internal static string[] AgentArguments(int agentFd, uint uid, uint gid, string user) =>
    [
        "--session-agent",
        "--agent-fd", agentFd.ToString(),
        "--drop-uid", uid.ToString(), "--drop-gid", gid.ToString(), "--drop-user", Quote(user),
    ];

    /// <summary>
    /// The engine's command line. The token is not in it, and a test holds it to that. A device the
    /// daemon does not have is -1, which the engine reads as "ignore input".
    /// </summary>
    internal static string[] EngineArguments(string dataDir, (uint Uid, uint Gid) account, int supervisorFd, int keyboardFd, int pointerFd, int relativeFd) =>
    [
        "--server",
        "--data", Quote(dataDir),
        "--ipc-system",
        "--drop-uid", account.Uid.ToString(), "--drop-gid", account.Gid.ToString(), "--drop-user", LinuxAccounts.EngineUser,
        "--supervisor-fd", supervisorFd.ToString(),
        "--keyboard-fd", keyboardFd.ToString(),
        "--pointer-fd", pointerFd.ToString(),
        "--relative-fd", relativeFd.ToString(),
    ];

    private static string Quote(string value) => value.Contains(' ') ? "\"" + value + "\"" : value;

    private sealed class AgentHandle(Process process, int socket, string session, AgentHandoff agents) : IAgentHandle
    {
        public bool IsAlive => !process.HasExited;

        public int ExitCode => process.HasExited ? process.ExitCode : -1;

        public int ProcessId => process.Id;

        public string Session => session;

        public void Stop()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        /// <summary>A socket the engine never came for is closed; one it took is the engine's, and sees the agent go.</summary>
        public void Dispose()
        {
            agents.Withdraw(socket);
            process.Dispose();
        }
    }

    private sealed class Handle(Process process, int socket) : IEngineHandle
    {
        public bool IsAlive => !process.HasExited;

        public int ExitCode => process.HasExited ? process.ExitCode : -1;

        public int ProcessId => process.Id;

        public void Stop()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        public void Dispose()
        {
            _ = UnixSocketMsg.close(socket);
            process.Dispose();
        }
    }
}
