using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DeskPair.Core.Ipc;

/// <summary>
/// Who is on the other end of a Unix domain socket, according to the kernel.
///
/// The kernel records the connecting process's credentials at the moment of connect, out of reach of both
/// ends. A client cannot choose them, cannot decline to supply them, and cannot change them afterwards --
/// which is what makes this worth asking, and what separates it from the role and the token, both of which
/// are things the client said.
///
/// The two systems answer the same question through different options. Linux hands back a ucred with the
/// pid, uid and gid in one call. macOS splits it: LOCAL_PEERCRED gives a much larger structure whose first
/// useful field is the uid, and the pid needs a second call. Neither is wrapped by .NET.
/// </summary>
internal static class UnixPeer
{
    /// <summary>Whether this process is root, which is what decides where the engine listens.</summary>
    public static bool IsRoot => !OperatingSystem.IsWindows() && geteuid() == 0;

    /// <summary>This process's own effective user, used to recognise the engine's own clients.</summary>
    public static uint Self => OperatingSystem.IsWindows() ? 0 : geteuid();

    /// <summary>The credentials of whoever opened <paramref name="socket"/>, or null when the kernel will not say.</summary>
    public static (uint Uid, int Pid)? Of(Socket socket)
    {
        try
        {
            nint fd = socket.Handle;
            return OperatingSystem.IsMacOS() ? Mac(fd) : Linux(fd);
        }
        catch (Exception)
        {
            // A socket already closed by a client that connected and left, or an option this kernel does
            // not carry. Either way there is no answer, and an unanswered question is not an owner.
            return null;
        }
    }

    /// <summary>
    /// The account name for that uid, for the log only.
    ///
    /// getpwuid returns a pointer into a static buffer, so concurrent callers would overwrite each other's
    /// answer. Connections are rare and this is one lock around a read, which is cheaper than carrying
    /// getpwuid_r's caller-allocated buffer dance for a string that decides nothing.
    /// </summary>
    public static string NameOf(uint uid)
    {
        lock (PasswdLock)
        {
            try
            {
                nint pw = getpwuid(uid);

                // pw_name is the first field of struct passwd on both systems.
                if (pw != 0 && Marshal.ReadIntPtr(pw) is var name && name != 0)
                {
                    return Marshal.PtrToStringUTF8(name) ?? $"uid {uid}";
                }
            }
            catch (Exception)
            {
                // No passwd database reachable (a container, a directory service that is down). The
                // number is the fact; the name was only ever the convenience.
            }

            return $"uid {uid}";
        }
    }

    private static readonly object PasswdLock = new();

    private const int SolSocket = 1;
    private const int SoPeercred = 17;

    /// <summary>struct ucred: pid, uid, gid, three 32-bit fields.</summary>
    private static (uint Uid, int Pid)? Linux(nint fd)
    {
        nint buffer = Marshal.AllocHGlobal(12);
        try
        {
            int size = 12;
            if (getsockopt(fd, SolSocket, SoPeercred, buffer, ref size) != 0 || size < 12)
            {
                return null;
            }

            return ((uint)Marshal.ReadInt32(buffer, 4), Marshal.ReadInt32(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const int SolLocal = 0;
    private const int LocalPeercred = 1;
    private const int LocalPeerpid = 2;

    /// <summary>
    /// struct xucred: version, uid, ngroups, then sixteen group ids -- 76 bytes, of which two fields are
    /// wanted. The version is checked because a kernel that filled in a different layout would otherwise
    /// be read as a uid.
    /// </summary>
    private static (uint Uid, int Pid)? Mac(nint fd)
    {
        nint buffer = Marshal.AllocHGlobal(128);
        try
        {
            int size = 128;
            if (getsockopt(fd, SolLocal, LocalPeercred, buffer, ref size) != 0 || size < 8)
            {
                return null;
            }

            if (Marshal.ReadInt32(buffer) != 0)
            {
                return null; // XUCRED_VERSION is 0; anything else is a structure this does not know.
            }

            uint uid = (uint)Marshal.ReadInt32(buffer, 4);

            // Not fatal if this one fails: the pid is for the log, the uid is the decision.
            int pidSize = sizeof(int);
            int pid = getsockopt(fd, SolLocal, LocalPeerpid, buffer, ref pidSize) == 0 ? Marshal.ReadInt32(buffer) : 0;
            return (uid, pid);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int getsockopt(nint fd, int level, int option, nint value, ref int length);

    [DllImport("libc")]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern nint getpwuid(uint uid);
}
