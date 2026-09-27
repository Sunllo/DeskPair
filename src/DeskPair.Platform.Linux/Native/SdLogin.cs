using System.Runtime.InteropServices;

namespace DeskPair.Platform.Linux.Native;

/// <summary>
/// The part of libsystemd that answers "who is at the screen right now".
///
/// Loaded by soname, like every other optional library here: libsystemd.so.0 is present on every machine
/// with systemd, and on one without it the first call raises DllNotFoundException and the caller falls
/// back to running loginctl. There is no probing step, because the answer to "is it there" is the same
/// call as "use it".
///
/// Every one of these returns a negative errno on failure and hands back a string the caller must free
/// with free(3). Forgetting that leaks a few bytes per poll, which over a machine's uptime is the kind of
/// thing nobody ever tracks down.
/// </summary>
internal static partial class SdLogin
{
    private const string Lib = "libsystemd.so.0";

    /// <summary>
    /// The active session on a seat, and whose it is, in one call.
    ///
    /// This is the whole question a supervisor asks, including the case that matters most: at the login
    /// screen the active session is the greeter's, so this answers rather than returning nothing.
    /// </summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int sd_seat_get_active(string seat, out nint session, out uint uid);

    /// <summary>"x11", "wayland", "tty" or "mir".</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int sd_session_get_type(string session, out nint type);

    /// <summary>
    /// "user", "greeter", "lock-screen", "background" or "manager".
    ///
    /// The one that makes this worth binding at all. The alternative is what the reference implementation
    /// does -- compare the session's user name against "gdm" and "sddm" -- which misses gdm-greeter,
    /// LightDM and GreetD, and it has the two bugs that follow from that written down in its own source.
    /// </summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int sd_session_get_class(string session, out nint klass);

    /// <summary>The X display, when the session has one; absent on Wayland.</summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int sd_session_get_display(string session, out nint display);

    [LibraryImport("libc")]
    public static partial void free(nint p);

    /// <summary>Takes ownership of a string libsystemd allocated, and gives it back as a managed one.</summary>
    public static string? Consume(nint p)
    {
        if (p == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(p);
        }
        finally
        {
            free(p);
        }
    }
}
