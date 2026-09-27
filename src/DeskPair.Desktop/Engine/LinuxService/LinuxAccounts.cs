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

        var pw = (PasswdEntry*)entry;
        return (pw->Uid, pw->Gid);
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

            var pw = (PasswdEntry*)entry;
            string? name = Marshal.PtrToStringUTF8(pw->Name);
            string? home = Marshal.PtrToStringUTF8(pw->Dir);
            return string.IsNullOrEmpty(name) ? null : (name, pw->Gid, string.IsNullOrEmpty(home) ? "/" : home);
        }
    }

    public static int Chown(string path, uint uid, uint gid) => chown(path, uid, gid);

    // getpwuid answers from a static buffer; one caller at a time reads it.
    private static readonly object Passwd = new();

    /// <summary>
    /// glibc's struct passwd. Its ids are two 4-byte fields between pointers, so their offsets depend on the pointer
    /// size -- uid at 16 on 64-bit, at 8 on 32-bit ARM. Reading through the struct lets the runtime lay it out as C
    /// does; 64-bit offsets there would read a pointer as the engine's uid.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PasswdEntry
    {
        public nint Name;
        public nint Password;
        public uint Uid;
        public uint Gid;
        public nint Gecos;
        public nint Dir;
        public nint Shell;
    }

    [LibraryImport("libc")]
    private static partial nint getpwuid(uint uid);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint getpwnam(string name);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int chown(string path, uint owner, uint group);
}
