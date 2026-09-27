using System.Runtime.InteropServices;

namespace DeskPair.Desktop.Engine.LinuxService;

/// <summary>
/// The account the daemon's engine runs as, and how files are handed to a user.
///
/// A dedicated system account, fixed for the life of the machine, rather than whoever is signed in. With
/// the picture and the keyboard both arriving as descriptors from the daemon, the engine needs nothing
/// from the session -- no X cookie, no session bus -- and so nothing about it has to change when the
/// session does. Lock the screen, sign out, switch users: the engine's pid stays the same and the
/// connection stays up. That is the strongest available form of the lesson Windows taught, where the
/// engine had to be moved between sessions and the move kept landing on somebody typing a password.
/// </summary>
internal static partial class LinuxAccounts
{
    /// <summary>The account the installer creates and the daemon drops its engine to.</summary>
    public const string EngineUser = "deskpair";

    /// <summary>uid and gid for a name, or null when there is no such account.</summary>
    public static unsafe (uint Uid, uint Gid)? Lookup(string name)
    {
        nint entry = getpwnam(name);
        if (entry == 0)
        {
            return null;
        }

        // struct passwd { char *pw_name; char *pw_passwd; uid_t pw_uid; gid_t pw_gid; ... } on LP64.
        var fields = (byte*)entry;
        uint uid = *(uint*)(fields + 16);
        uint gid = *(uint*)(fields + 20);
        return (uid, gid);
    }

    /// <summary>The account with <paramref name="uid"/>: its name, primary group and home; null when passwd does not know it.</summary>
    public static unsafe (string Name, uint Gid, string Home)? Of(uint uid)
    {
        lock (Passwd)
        {
            nint entry = getpwuid(uid);
            if (entry == 0)
            {
                return null;
            }

            // struct passwd on LP64: pw_name 0, pw_passwd 8, pw_uid 16, pw_gid 20, pw_gecos 24, pw_dir 32, pw_shell 40.
            var fields = (byte*)entry;
            string? name = Marshal.PtrToStringUTF8(*(nint*)fields);
            string? home = Marshal.PtrToStringUTF8(*(nint*)(fields + 32));
            return string.IsNullOrEmpty(name) ? null : (name, *(uint*)(fields + 20), string.IsNullOrEmpty(home) ? "/" : home);
        }
    }

    public static int Chown(string path, uint uid, uint gid) => chown(path, uid, gid);

    // getpwuid answers from a static buffer; one caller at a time reads it.
    private static readonly object Passwd = new();

    [LibraryImport("libc")]
    private static partial nint getpwuid(uint uid);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint getpwnam(string name);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int chown(string path, uint owner, uint group);
}
