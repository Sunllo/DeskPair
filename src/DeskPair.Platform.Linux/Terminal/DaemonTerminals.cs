using System.Diagnostics;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Terminal;
using DeskPair.Platform.Linux.Capture.Drm;
using DeskPair.Platform.Linux.Hosting;
using DeskPair.Platform.Unix.Terminal;

namespace DeskPair.Platform.Linux.Terminal;

/// <summary>
/// The gate: whether this daemon gives root (and signed-in-user) shells at all. It lives in a file root
/// owns under /etc, which the engine -- running as <c>deskpair</c> -- cannot write, and it is never read
/// from the engine's data directory or from anything the engine sends. That is the whole security story
/// of the root terminal: if this ever came from something the engine can change, the deskpair account
/// would be root on every machine.
/// </summary>
public static class DaemonTerminalConfig
{
    public const string Path = "/etc/deskpair/daemon.conf";

    /// <summary>What <c>--install-service</c> writes when there is no file yet.</summary>
    public const string Default = """
        # DeskPair daemon settings. Read by the root daemon only; the engine, which runs as the
        # deskpair account, cannot write here.
        #
        # terminal-root: whether a viewer who is allowed a terminal gets a root shell (or one as the
        # signed-in user) on this machine. "yes" makes the deskpair account equivalent to root here.
        # "no" limits terminals to the deskpair account itself.
        terminal-root = yes

        """;

    /// <summary>
    /// True only for an explicit "terminal-root = yes". A missing file, a missing key, anything else:
    /// no. A machine installed before terminals existed has no file, and gets the safe answer.
    /// </summary>
    public static bool AllowsRoot(string? text)
    {
        if (text is null)
        {
            return false;
        }

        bool allowed = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim() == "terminal-root")
            {
                allowed = line[(eq + 1)..].Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);
            }
        }

        return allowed;
    }

    public static bool Read()
    {
        try
        {
            return AllowsRoot(File.Exists(Path) ? File.ReadAllText(Path) : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// How a shell is started from the daemon: a transient systemd unit whose terminal is our pty.
///
/// Not a child of the daemon, on purpose. The daemon runs inside its unit's sandbox --
/// ProtectSystem=full makes /usr and /etc read-only in its mount namespace, RestrictSUIDSGID is a seccomp
/// filter -- and a child inherits both: measured, a root shell started by the daemon could not write
/// /usr, so `apt install` would fail, which is the thing the terminal is for. A seccomp filter cannot be
/// dropped, and a multi-threaded .NET child cannot change mount namespace. systemd starts the unit in a
/// clean context, makes the pty the controlling terminal (StandardInput=tty), sets the user and groups,
/// and puts the shell and everything it starts in one cgroup, so `systemctl stop` ends all of it --
/// including anything that made a session of its own, which killing a session by id would miss.
/// </summary>
public static class TerminalUnit
{
    /// <summary>The systemd-run command line. Every element is a constant, a number, or a value from the passwd database.</summary>
    public static string[] Arguments(string unit, string ttyPath, string user, string home, string shell, string? lang) =>
    [
        "--quiet",
        "--unit=" + unit,
        "--description=DeskPair terminal for a remote viewer",
        "--property=TTYPath=" + ttyPath,
        "--property=StandardInput=tty",
        "--property=StandardOutput=tty",
        "--property=StandardError=tty",
        "--property=TTYVHangup=yes",
        "--property=KillMode=control-group",
        "--property=TimeoutStopSec=2",
        "--uid=" + user,
        "--working-directory=" + home,
        "--setenv=TERM=" + UnixShell.Term,
        "--setenv=COLORTERM=truecolor",
        "--setenv=LANG=" + (string.IsNullOrEmpty(lang) ? "C.UTF-8" : lang),
        "--",
        shell,
        "-l",
    ];

    /// <summary>Reads `systemctl show` output: whether the unit has finished and with what code.</summary>
    public static (bool Exited, int Code) ParseState(string show)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in show.Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq > 0)
            {
                values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        // A unit that finished successfully is collected at once: gone means it exited, cleanly.
        if (values.GetValueOrDefault("LoadState") == "not-found")
        {
            return (true, 0);
        }

        string active = values.GetValueOrDefault("ActiveState") ?? string.Empty;
        if (active is "inactive" or "failed")
        {
            return (true, int.TryParse(values.GetValueOrDefault("ExecMainStatus"), out int code) ? code : -1);
        }

        return (false, 0);
    }
}

/// <summary>
/// The daemon's side of terminals for one engine: answers its requests over the capture channel, keeps
/// track of the units it started for it, and stops every one of them when that engine goes away -- a
/// root shell with no engine to watch it is the one outcome worse than none.
/// </summary>
public sealed class DaemonTerminals(ILogger log, Func<bool>? gate = null, Func<ActiveSession?>? seat = null)
{
    /// <summary>Shells one engine may have running at once.</summary>
    public const int MaxLive = 8;

    /// <summary>Opens per minute: a loop asking for shells is a fault or an attack, and either way is stopped here.</summary>
    public const int MaxOpensPerMinute = 20;

    private readonly Func<bool> _gate = gate ?? DaemonTerminalConfig.Read;
    private readonly Func<ActiveSession?> _seat = seat ?? (() => LinuxSessions.Active());
    private readonly Dictionary<int, string> _units = [];
    private readonly Queue<DateTime> _recentOpens = new();
    private readonly object _lock = new();
    private int _next;

    /// <summary>Answers one request; <paramref name="fd"/> is a descriptor to attach to the reply and then close, or -1.</summary>
    public int Handle(ReadOnlySpan<byte> request, Span<byte> reply, out int fd)
    {
        fd = -1;
        if (DrmWire.TryReadOpenTerminal(request, out byte runAs, out int columns, out int rows, out bool describe))
        {
            return Open(runAs == 1 ? TerminalRunAs.User : TerminalRunAs.Highest, columns, rows, describe, reply, out fd);
        }

        if (DrmWire.TryReadTerminalSignal(request, out int handle, out int signal))
        {
            return SignalOrAsk(handle, signal, reply);
        }

        return DrmWire.WriteError(reply, "an unreadable terminal request");
    }

    private int Open(TerminalRunAs runAs, int columns, int rows, bool describe, Span<byte> reply, out int fd)
    {
        fd = -1;
        if (!_gate())
        {
            return DrmWire.WriteRefusal(reply, $"terminal-root is not yes in {DaemonTerminalConfig.Path}");
        }

        UnixAccount account;
        if (runAs == TerminalRunAs.User)
        {
            if (_seat() is not { Role: SessionRole.User or SessionRole.LockScreen } active)
            {
                return DrmWire.WriteError(reply, "Nobody is signed in on this computer, so there is no user to start a shell as.");
            }

            account = UnixAccount.Of(active.Uid);
        }
        else
        {
            account = UnixAccount.Of(0);
        }

        string shell = UnixShell.Choose(account.Shell, File.Exists);
        if (describe)
        {
            return DrmWire.WriteTerminal(reply, 0, account.Name + "\n" + shell, fdAttached: false);
        }

        lock (_lock)
        {
            DateTime now = DateTime.UtcNow;
            while (_recentOpens.Count > 0 && now - _recentOpens.Peek() > TimeSpan.FromMinutes(1))
            {
                _recentOpens.Dequeue();
            }

            if (_units.Count >= MaxLive || _recentOpens.Count >= MaxOpensPerMinute)
            {
                log.LogWarning("Refused a terminal: {Live} running, {Recent} opened in the last minute", _units.Count, _recentOpens.Count);
                return DrmWire.WriteError(reply, "Too many terminals have been opened on this computer just now.");
            }

            _recentOpens.Enqueue(now);
        }

        (int master, string tty) = LinuxPtySpawner.OpenPty(columns, rows);
        int handle = Interlocked.Increment(ref _next);
        string unit = $"deskpair-shell-{Environment.ProcessId}-{handle}";
        string home = Directory.Exists(account.Home) ? account.Home : "/";
        (int code, string output) = Systemd("systemd-run", TerminalUnit.Arguments(unit, tty, account.Name, home, shell, Environment.GetEnvironmentVariable("LANG")));
        if (code != 0)
        {
            LinuxPtySpawner.CloseFd(master);
            log.LogWarning("systemd-run for a terminal failed ({Code}): {Output}", code, output);
            return DrmWire.WriteError(reply, "The shell could not be started: " + (output.Length > 0 ? output : $"systemd-run exited {code}"));
        }

        lock (_lock)
        {
            _units[handle] = unit;
        }

        log.LogInformation("Terminal {Unit} started as {Account} ({Shell}) on {Tty}", unit, account.Name, shell, tty);
        fd = master;
        return DrmWire.WriteTerminal(reply, handle, account.Name + "\n" + shell, fdAttached: true);
    }

    private int SignalOrAsk(int handle, int signal, Span<byte> reply)
    {
        string? unit;
        lock (_lock)
        {
            unit = _units.GetValueOrDefault(handle);
        }

        if (unit is null)
        {
            // Not one of this engine's, or already reported as ended.
            return DrmWire.WriteTerminalState(reply, handle, exited: true, code: -1);
        }

        switch (signal)
        {
            case 9:
                _ = Systemd("systemctl", ["stop", unit]);
                break;
            case 1 or 15:
                _ = Systemd("systemctl", ["kill", "--signal=" + (signal == 1 ? "SIGHUP" : "SIGTERM"), unit]);
                break;
        }

        (bool exited, int code) = TerminalUnit.ParseState(Systemd("systemctl", ["show", unit, "-p", "LoadState", "-p", "ActiveState", "-p", "ExecMainStatus"]).Output);
        if (exited)
        {
            _ = Systemd("systemctl", ["reset-failed", unit]);
            lock (_lock)
            {
                _units.Remove(handle);
            }

            log.LogInformation("Terminal {Unit} ended with code {Code}", unit, code);
        }

        return DrmWire.WriteTerminalState(reply, handle, exited, code);
    }

    /// <summary>The engine went away: every shell it had goes too.</summary>
    public void StopAll()
    {
        List<string> units;
        lock (_lock)
        {
            units = [.. _units.Values];
            _units.Clear();
        }

        foreach (string unit in units)
        {
            _ = Systemd("systemctl", ["stop", unit]);
            _ = Systemd("systemctl", ["reset-failed", unit]);
            log.LogInformation("Terminal {Unit} stopped: its engine is gone", unit);
        }
    }

    /// <summary>Runs systemd-run or systemctl with an argument list -- no shell, so no quoting to get wrong.</summary>
    private static (int Code, string Output) Systemd(string program, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in arguments)
        {
            start.ArgumentList.Add(a);
        }

        try
        {
            using Process? p = Process.Start(start);
            if (p is null)
            {
                return (-1, "could not start " + program);
            }

            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            Task<string> stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10_000))
            {
                p.Kill();
                return (-1, program + " did not finish");
            }

            return (p.ExitCode, (stdout.Result + stderr.Result).Trim());
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, e.Message);
        }
    }
}
