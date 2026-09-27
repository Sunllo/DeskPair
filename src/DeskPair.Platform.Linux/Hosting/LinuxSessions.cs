using System.Diagnostics;
using DeskPair.Platform.Linux.Native;

namespace DeskPair.Platform.Linux.Hosting;

/// <summary>How the session draws: the one fact that decides which capturer can see it.</summary>
public enum SessionKind
{
    Unknown,
    X11,
    Wayland,

    /// <summary>A text console. Nothing graphical to capture, and worth saying so rather than failing later.</summary>
    Tty,
}

/// <summary>What the session is for.</summary>
public enum SessionRole
{
    Unknown,

    /// <summary>Somebody signed in.</summary>
    User,

    /// <summary>The login screen. A different user account, not a different desktop.</summary>
    Greeter,

    /// <summary>A lock screen shown over a user session.</summary>
    LockScreen,
}

/// <summary>
/// What a session's own processes are using to reach the screen.
///
/// Not what logind says. On a real GNOME-on-Xorg desktop <c>loginctl show-session -p Display</c> comes
/// back empty while Xorg is plainly running on :0, so the property cannot be relied on -- and the two
/// obvious substitutes are worse. The session leader is <c>gdm-session-worker</c>, which runs as root
/// through PAM and has no DISPLAY in its environment at all; and the session's own cgroup does not
/// contain the desktop either, because GNOME's shell lives under user@1000.service rather than under
/// session-2.scope.
///
/// What does have it is any ordinary process of that user's desktop, which is why this is found by
/// looking rather than by asking.
/// </summary>
/// <param name="Display">The X display, as DISPLAY would be set for a client of it.</param>
/// <param name="XAuthority">The cookie file. On GDM this is under the user's runtime directory, not their home.</param>
/// <param name="WaylandDisplay">The Wayland socket name, when the session has one.</param>
/// <param name="RuntimeDir">XDG_RUNTIME_DIR, which the portal and the session bus are both under.</param>
public sealed record SessionEnvironment(string? Display, string? XAuthority, string? WaylandDisplay, string? RuntimeDir)
{
    /// <summary>DBUS_SESSION_BUS_ADDRESS, which the portal is reached on; usually a socket under <see cref="RuntimeDir"/>.</summary>
    public string? SessionBus { get; init; }

    /// <summary>XDG_CURRENT_DESKTOP ("ubuntu:GNOME", "KDE"), which decides what the portal will do without asking.</summary>
    public string? CurrentDesktop { get; init; }
}

/// <summary>The session a supervisor would put an engine into.</summary>
/// <param name="Id">logind's own id, for the log and for asking it further questions.</param>
/// <param name="Uid">Whose session it is. The engine has to run as this user; see the notes on <see cref="LinuxSessions"/>.</param>
/// <param name="Kind">X11 or Wayland, which decides the capturer.</param>
/// <param name="Role">Whether this is a signed-in desktop or the login screen.</param>
/// <param name="Display">The X display, when there is one.</param>
public sealed record ActiveSession(string Id, uint Uid, SessionKind Kind, SessionRole Role, string? Display);

/// <summary>
/// Who is at the screen, from logind.
///
/// The Linux shape is not the Windows one, and this is where that starts. On Windows the service runs as
/// LocalSystem and puts an engine into the console session with a borrowed token; the engine is still
/// LocalSystem. On Linux an engine that has to see a desktop must *be* that desktop's user -- XTest needs
/// that user's XAUTHORITY and the portal needs their session bus -- so what a supervisor needs from this
/// is not "which session" but "which session and whose", and the login screen is a different uid rather
/// than a different desktop.
///
/// libsystemd first, because it answers without forking and because <c>sd_session_get_class</c> names the
/// greeter outright. Falling back to loginctl costs three process launches per poll, which is why the
/// caller is told to slow down when it lands there.
/// </summary>
public static class LinuxSessions
{
    /// <summary>Whether the last answer came from libsystemd or from running loginctl.</summary>
    public static bool UsingLibrary { get; private set; } = true;

    /// <summary>The active session on a seat, or null when that seat has none.</summary>
    public static ActiveSession? Active(string seat = "seat0")
    {
        if (UsingLibrary)
        {
            try
            {
                return FromLibrary(seat);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                // No libsystemd. Said once, by flipping the flag the caller reads to decide its poll rate.
                UsingLibrary = false;
            }
        }

        return FromLoginctl(seat);
    }

    private static ActiveSession? FromLibrary(string seat)
    {
        // No seat, or no session on it: a machine that has booted past the display manager and has
        // nothing on the screen. Nothing is allocated on that path, so there is nothing to free.
        if (SdLogin.sd_seat_get_active(seat, out nint sessionPtr, out uint uid) < 0)
        {
            return null;
        }

        string? id = SdLogin.Consume(sessionPtr);
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        string? type = null;
        if (SdLogin.sd_session_get_type(id, out nint typePtr) >= 0)
        {
            type = SdLogin.Consume(typePtr);
        }

        string? role = null;
        if (SdLogin.sd_session_get_class(id, out nint rolePtr) >= 0)
        {
            role = SdLogin.Consume(rolePtr);
        }

        // Absent on Wayland, and that is not a failure -- it is how a Wayland session says so.
        string? display = null;
        if (SdLogin.sd_session_get_display(id, out nint displayPtr) >= 0)
        {
            display = SdLogin.Consume(displayPtr);
        }

        return new ActiveSession(id, uid, KindOf(type), RoleOf(role), Empty(display));
    }

    /// <summary>
    /// The same answer from loginctl, for a machine with systemd but no libsystemd to link against.
    ///
    /// Property output rather than <c>list-sessions</c>: the table has a heading, a trailing "N sessions
    /// listed." and column widths that move, and it does not carry the class at all. Key=value cannot
    /// drift in any of those ways.
    /// </summary>
    private static ActiveSession? FromLoginctl(string seat)
    {
        string? id = ParseProperty(Run("show-seat", seat, "-p", "ActiveSession"), "ActiveSession");
        return string.IsNullOrEmpty(id)
            ? null
            : ParseSession(Run("show-session", id, "-p", "Id", "-p", "User", "-p", "Type", "-p", "Class", "-p", "Display"));
    }

    /// <summary>The parse without the process, so every shape of it can be tested on any machine.</summary>
    internal static ActiveSession? ParseSession(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string? id = ParseProperty(text, "Id");
        string? uid = ParseProperty(text, "User");
        if (string.IsNullOrEmpty(id) || !uint.TryParse(uid, out uint parsed))
        {
            return null;
        }

        return new ActiveSession(
            id,
            parsed,
            KindOf(ParseProperty(text, "Type")),
            RoleOf(ParseProperty(text, "Class")),
            Empty(ParseProperty(text, "Display")));
    }

    /// <summary>
    /// One property out of loginctl's key=value output.
    ///
    /// An unset property comes back as the key with nothing after it, which is a different answer from the
    /// key being absent and has to read as empty rather than as the literal rest of the line.
    /// </summary>
    internal static string? ParseProperty(string? text, string key)
    {
        if (text is null)
        {
            return null;
        }

        foreach (string line in text.Split('\n'))
        {
            ReadOnlySpan<char> trimmed = line.AsSpan().Trim();
            if (trimmed.Length > key.Length && trimmed[key.Length] == '=' && trimmed.StartsWith(key, StringComparison.Ordinal))
            {
                return trimmed[(key.Length + 1)..].ToString();
            }
        }

        return null;
    }

    internal static SessionKind KindOf(string? type) => type switch
    {
        "x11" => SessionKind.X11,
        "wayland" => SessionKind.Wayland,
        "tty" => SessionKind.Tty,
        _ => SessionKind.Unknown,
    };

    /// <summary>
    /// logind's own word for what the session is.
    ///
    /// This is the whole reason for preferring libsystemd, and it is worth being explicit about what it
    /// replaces: matching the session's user name against "gdm" and "sddm", which is what the reference
    /// implementation does. That misses gdm-greeter, LightDM and GreetD -- three display managers whose
    /// login screens would then look like ordinary user sessions belonging to nobody in particular.
    /// </summary>
    internal static SessionRole RoleOf(string? klass) => klass switch
    {
        "user" => SessionRole.User,
        "greeter" => SessionRole.Greeter,
        "lock-screen" => SessionRole.LockScreen,
        _ => SessionRole.Unknown,
    };

    /// <summary>
    /// The display settings a session's processes are actually using, found by reading their environments.
    ///
    /// Only root can do this, and that is the point: the supervisor is root and the engine it is about to
    /// start is not, so this is one of the few things that has to happen before the privilege is given up.
    /// Even the session's own user cannot read these -- Ubuntu ships yama's ptrace_scope at 1, so a
    /// process may only read the environment of its own descendants.
    ///
    /// A whole scan of /proc, because there is no smaller set that reliably contains the answer. Measured
    /// on a GNOME 24.04 desktop with 311 processes: 0.12 seconds, and it is only done when the session
    /// changes rather than on every poll.
    ///
    /// A process carrying both DISPLAY and XAUTHORITY wins over one carrying only DISPLAY: a shell
    /// started from a terminal inherits the first and often not the second, and half an answer here is a
    /// capturer that opens a display it cannot authenticate to.
    ///
    /// With <paramref name="session"/>, only processes started since that session began count. Processes
    /// outlive their session -- a user service of the last desktop, still running after a switch from KDE
    /// to GNOME -- and one of those answered for a desktop that was no longer there; and a session is on
    /// the screen a moment before its desktop has started, when the only processes with a display are such
    /// leftovers or none. "Began" is when its leader started (logind records the leader, and the desktop is
    /// all started after it); when that cannot be read, every process counts, as without a session.
    /// </summary>
    public static SessionEnvironment? EnvironmentOf(uint uid, string? session = null)
    {
        SessionEnvironment? best = null;
        ulong? began = session is null ? null : SessionBegan(session);

        foreach (string dir in SafeProcesses())
        {
            if (OwnerOf(dir) != uid)
            {
                continue;
            }

            if (began is { } since && StartTicksOf(dir) is { } started && started < since)
            {
                continue;
            }

            byte[]? raw = ReadEnviron(Path.Combine(dir, "environ"));
            if (raw is null)
            {
                continue;
            }

            Dictionary<string, string> env = ParseEnviron(raw);

            env.TryGetValue("DISPLAY", out string? display);
            env.TryGetValue("WAYLAND_DISPLAY", out string? wayland);
            if (string.IsNullOrEmpty(display) && string.IsNullOrEmpty(wayland))
            {
                continue;
            }

            env.TryGetValue("XAUTHORITY", out string? auth);
            env.TryGetValue("XDG_RUNTIME_DIR", out string? runtime);
            env.TryGetValue("DBUS_SESSION_BUS_ADDRESS", out string? bus);
            env.TryGetValue("XDG_CURRENT_DESKTOP", out string? desktop);
            var found = new SessionEnvironment(Empty(display), Empty(auth), Empty(wayland), Empty(runtime))
            {
                SessionBus = Empty(bus),
                CurrentDesktop = Empty(desktop),
            };

            if (found.XAuthority is not null || string.IsNullOrEmpty(display))
            {
                return found;
            }

            best ??= found;
        }

        return best;
    }

    /// <summary>When the session's leader started, in clock ticks since boot; null when logind's record or the leader cannot be read.</summary>
    private static ulong? SessionBegan(string session)
    {
        // logind's own record of the session, which sd-login itself reads; the id is logind's too, but it becomes a
        // path here, so anything but letters and digits is not believed.
        if (session.Length == 0 || !session.All(char.IsAsciiLetterOrDigit))
        {
            return null;
        }

        try
        {
            return ParseLeader(File.ReadAllText(Path.Combine("/run/systemd/sessions", session))) is { } leader
                ? StartTicksOf(Path.Combine("/proc", leader.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The <c>LEADER=</c> line of a logind session record: the process that opened the session.</summary>
    internal static int? ParseLeader(string record) =>
        int.TryParse(ParseProperty(record, "LEADER"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid) && pid > 0
            ? pid
            : null;

    private static ulong? StartTicksOf(string processDir)
    {
        try
        {
            return ParseStartTicks(File.ReadAllText(Path.Combine(processDir, "stat")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Gone between listing and reading.
            return null;
        }
    }

    /// <summary>
    /// When a process started, in clock ticks since boot: the 22nd field of /proc/PID/stat. Counted from the last
    /// ')' -- the second field is the command's name in parentheses, and a name may hold spaces and parentheses.
    /// </summary>
    internal static ulong? ParseStartTicks(string stat)
    {
        int close = stat.LastIndexOf(')');
        if (close < 0)
        {
            return null;
        }

        string[] fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        const int StartTime = 22 - 3; // the fields after the name begin with the third
        return fields.Length > StartTime && ulong.TryParse(fields[StartTime], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong ticks)
            ? ticks
            : null;
    }

    private static IEnumerable<string> SafeProcesses()
    {
        string[] entries;
        try
        {
            entries = Directory.GetDirectories("/proc");
        }
        catch (Exception)
        {
            // No /proc: a container built without one, or not Linux at all.
            return [];
        }

        return entries.Where(static d => uint.TryParse(Path.GetFileName(d), out _));
    }

    /// <summary>
    /// The owning user, from the process's own status file rather than from a stat(2) binding.
    ///
    /// .NET will not report a file's owner, and struct stat's layout is the sort of thing that differs
    /// between architectures and glibc versions. This is the kernel answering in text, and it is the line
    /// the kernel puts near the top of the file.
    /// </summary>
    private static uint? OwnerOf(string processDir)
    {
        try
        {
            foreach (string line in File.ReadLines(Path.Combine(processDir, "status")))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] parts = line.Split(['	', ' '], StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 1 && uint.TryParse(parts[1], out uint uid) ? uid : null;
            }
        }
        catch (Exception)
        {
            // Gone between listing and reading, which on a busy machine happens constantly.
        }

        return null;
    }

    private static byte[]? ReadEnviron(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception)
        {
            // Kernel threads have none, a process that ended mid-read has none either, and an ordinary
            // user reading another user's is refused. None of those is a desktop, so none is reported.
            return null;
        }
    }

    /// <summary>
    /// /proc/PID/environ as the kernel lays it out: NAME=VALUE entries separated by NUL bytes, usually
    /// with a trailing one.
    ///
    /// Values may contain '=' -- an XAUTHORITY path will not, but a DBUS address routinely does -- so the
    /// split is on the first one only. An entry with no '=' at all, or one starting with it, is not a
    /// variable and is dropped rather than guessed at.
    /// </summary>
    internal static Dictionary<string, string> ParseEnviron(byte[] raw)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string entry in System.Text.Encoding.UTF8.GetString(raw).Split('\0'))
        {
            int split = entry.IndexOf('=');
            if (split > 0)
            {
                env[entry[..split]] = entry[(split + 1)..];
            }
        }

        return env;
    }

    private static string? Empty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// Asks logind to lock a session: what the remote viewer's lock button does on Linux.
    ///
    /// Through loginctl rather than sd-bus, deliberately. <c>sd_bus_call_method</c> is variadic, and a
    /// variadic C call is not something LibraryImport can promise to get right on every ABI; loginctl
    /// is one process launch for something that happens a few times a day, and it was seen to work as an
    /// ordinary user over SSH on 2026-09-23, which is exactly the account the engine runs as.
    /// </summary>
    public static bool Lock(string sessionId) => Run("lock-session", sessionId) is not null;

    public static bool Unlock(string sessionId) => Run("unlock-session", sessionId) is not null;

    /// <summary>logind's LockedHint for a session: null when it could not be asked.</summary>
    public static bool? IsLocked(string sessionId) =>
        Run("show-session", sessionId, "-p", "LockedHint", "--value")?.Trim() switch
        {
            "yes" => true,
            "no" => false,
            _ => null,
        };

    /// <summary>Whether logind still knows this session, which a supervisor asks before moving anything.</summary>
    public static bool StillExists(string sessionId) =>
        Run("show-session", sessionId, "-p", "Id", "--value")?.Trim() == sessionId;

    private static string? Run(params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("loginctl") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using Process? process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            // A refusal is an exit code, not an exception: "Interactive authentication required" from polkit
            // leaves stdout empty and exits 1, and an answer of "" is not an answer.
            string output = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(3000) && process.ExitCode == 0 ? output : null;
        }
        catch (Exception)
        {
            // No loginctl, or no permission to ask it. There is no third way to find out, so the answer
            // is that there is no answer.
            return null;
        }
    }
}
