using System.Text;
using DeskPair.Platform.Linux.Native;

namespace DeskPair.Platform.Linux.Hosting;

/// <summary>
/// Everything worth knowing when the Linux host will not start, gathered in one line.
///
/// "X11 host unavailable" on its own has never been enough to act on: the answer is always one of a short
/// list -- DISPLAY unset, XAUTHORITY pointing somewhere this user cannot read, a Wayland session with no
/// Xwayland, or a process running as the wrong user entirely -- and telling them apart takes exactly these
/// values. Getting them out of a machine that cannot be reached remotely is the expensive part, so the host
/// says them at the moment it gives up rather than waiting to be asked.
///
/// The root daemon makes this sharper still. It runs with none of a session's environment, so the failure
/// it produces is the same "cannot open the X display" a headless server produces, from a completely
/// different cause. <c>euid</c> is in here to tell those two apart at a glance.
/// </summary>
public static class LinuxSessionDiagnostics
{
    /// <summary>The state of this process, read from the real environment.</summary>
    public static string Describe() =>
        Describe(Environment.GetEnvironmentVariable, LibC.geteuid(), IsReadable);

    /// <summary>The same, with the machine passed in, so the formatting can be tested anywhere.</summary>
    internal static string Describe(Func<string, string?> env, uint euid, Func<string, bool> readable)
    {
        var text = new StringBuilder();

        foreach (string name in (string[])["DISPLAY", "WAYLAND_DISPLAY", "XDG_SESSION_TYPE", "XDG_CURRENT_DESKTOP", "XDG_RUNTIME_DIR"])
        {
            Append(text, name, env(name));
        }

        // Readability, not just the path: an XAUTHORITY naming a file owned by the session user is the
        // ordinary way a daemon fails, and the path alone looks perfectly correct in a log.
        string? auth = env("XAUTHORITY");
        Append(text, "XAUTHORITY", auth is { Length: > 0 } ? auth + (readable(auth) ? " (readable)" : " (NOT readable)") : auth);

        text.Append(" euid=").Append(euid);
        return text.ToString().TrimStart();
    }

    private static void Append(StringBuilder text, string name, string? value)
    {
        text.Append(' ').Append(name).Append('=').Append(value is { Length: > 0 } ? value : "(unset)");
    }

    private static bool IsReadable(string path)
    {
        try
        {
            using FileStream f = File.OpenRead(path);
            return true;
        }
        catch (Exception)
        {
            // Missing, or ours to read but not this user's. Either way the answer to the question is no.
            return false;
        }
    }

    /// <summary>
    /// Whether an X display can be opened at all, asked once before anything is built.
    ///
    /// Capture, input, cursor and the clipboard each open their own connection, so one missing display used
    /// to produce whichever of their four messages happened to be constructed first. Asking here means the
    /// common case has one answer, in one place, with <see cref="Describe()"/> beside it -- and the four
    /// original messages are left to mean what they say, which is that something opened a display after
    /// this probe had already succeeded.
    /// </summary>
    public static bool CanOpenDisplay(out string failure)
    {
        try
        {
            Xlib.EnsureThreadSafe();
            nint dpy = Xlib.XOpenDisplay(null);
            if (dpy == 0)
            {
                failure = "no X display could be opened";
                return false;
            }

            Xlib.XCloseDisplay(dpy);
            failure = "";
            return true;
        }
        catch (DllNotFoundException)
        {
            // Deliberately not the exception's own message. The loader lists every path it tried, eleven
            // of them, and prints it on one line -- so the one fact worth having disappears into the
            // middle of it. The paths would matter if libX11 could be anywhere; it cannot.
            failure = "libX11.so.6 is not installed, or one of its dependencies is missing";
            return false;
        }
        catch (EntryPointNotFoundException e)
        {
            failure = "libX11 is present but does not have " + e.Message;
            return false;
        }
    }
}
