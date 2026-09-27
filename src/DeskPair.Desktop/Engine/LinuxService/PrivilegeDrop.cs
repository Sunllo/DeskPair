using System.Runtime.InteropServices;

namespace DeskPair.Desktop.Engine.LinuxService;

/// <summary>
/// Becomes the session's user, irreversibly, before this process does anything else.
///
/// This is the half of the Linux arrangement that Windows has no counterpart for. There, the service is
/// LocalSystem and the engine it starts is LocalSystem too: it borrows a token to reach the right desktop
/// and keeps its own privileges. Here the engine has to *be* the user whose desktop it is, because XTest
/// reads that user's XAUTHORITY and the portal talks to their session bus, and neither can be borrowed.
///
/// So a supervisor re-executes this program and the child drops itself, rather than the supervisor running
/// it through sudo the way the reference implementation does. sudo is not on every distribution, its policy
/// belongs to the administrator -- a "Defaults requiretty" or a strict sudoers file would break the product
/// in a way nobody would connect to DeskPair -- and it forces the unit to keep NoNewPrivileges off for ever.
///
/// The order is not a preference. initgroups first, because it needs to be root to read the group database
/// and set the list; then the group, because setting it needs the privilege the next line gives away; then
/// the user. setresuid rather than setuid, so the saved id goes too and the drop cannot be undone.
///
/// One thing here was worth proving rather than assuming: Linux's setuid is per-thread at the system-call
/// layer, and the CLR has already started threads before Main runs. glibc hides that with its setxid
/// broadcast, so going through libc -- which a DllImport does -- changes the whole process. Measured on
/// .NET 10: twelve threads, all of them, real, effective and saved. A raw syscall would not have.
/// </summary>
internal static class PrivilegeDrop
{
    /// <summary>
    /// Applies <c>--drop-uid</c>, <c>--drop-gid</c> and <c>--drop-user</c>, or does nothing when they are
    /// absent. Returns null when the process is now that user, or the reason it is not.
    /// </summary>
    /// <param name="args">The command line.</param>
    /// <param name="handOverLibraries">
    /// Whether the native libraries the single-file host unpacked go to the account first (see
    /// <see cref="HandOverLibraries"/>). The engine loads them; the session agent loads none, and shares the
    /// engine's unpacked copy, which must stay the engine's.
    /// </param>
    public static string? Apply(string[] args, bool handOverLibraries = true)
    {
        if (ServerRole.Arg(args, "--drop-uid") is not { } uidText)
        {
            return null;
        }

        // Linux only, and not for want of generality: setresuid is a Linux and BSD call that macOS does
        // not have, and macOS does not need it -- launchd runs the agent as the session's own user, which
        // is the thing this is reaching for by hand.
        if (!OperatingSystem.IsLinux())
        {
            return "--drop-uid is a Linux arrangement and means nothing on this system";
        }

        if (!uint.TryParse(uidText, out uint uid) || uid == 0)
        {
            return $"--drop-uid {uidText} is not a user to become";
        }

        // Required, not optional. Without a name there is no group list to look up, and a process that
        // kept root's supplementary groups while wearing the user's uid is the worst of both: it would
        // pass every check below and still be in groups that user is not in.
        if (ServerRole.Arg(args, "--drop-user") is not { Length: > 0 } user)
        {
            return "--drop-uid needs --drop-user; the group list is looked up by name";
        }

        uint gid = ServerRole.Arg(args, "--drop-gid") is { } gidText && uint.TryParse(gidText, out uint parsed) ? parsed : uid;

        if (handOverLibraries)
        {
            HandOverLibraries(uid, gid);
        }

        // Not optional either: without it the child loses video and render, and the DRM capture path
        // cannot open a render node. Failure is reported rather than fatal -- a machine whose group
        // database is unreachable can still run a session -- but it is never skipped.
        if (initgroups(user, gid) != 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            if (errno != 0)
            {
                return $"initgroups({user}, {gid}) failed with errno {errno}";
            }
        }

        if (setresgid(gid, gid, gid) != 0)
        {
            return $"setresgid({gid}) failed with errno {Marshal.GetLastPInvokeError()}";
        }

        if (setresuid(uid, uid, uid) != 0)
        {
            return $"setresuid({uid}) failed with errno {Marshal.GetLastPInvokeError()}";
        }

        // Asked, not assumed. A drop that half happened leaves a process that believes it is the user and
        // can still become root, which is the one outcome worth crashing over: everything after this point
        // reads and writes that user's files on the strength of it.
        if (getresuid(out uint ruid, out uint euid, out uint suid) != 0 || ruid != uid || euid != uid || suid != uid)
        {
            return $"still uid {ruid}/{euid}/{suid} after asking to become {uid}";
        }

        if (getresgid(out uint rgid, out uint egid, out uint sgid) != 0 || rgid != gid || egid != gid || sgid != gid)
        {
            return $"still gid {rgid}/{egid}/{sgid} after asking to become {gid}";
        }

        return null;
    }

    /// <summary>
    /// Gives the native libraries this process's single-file host unpacked to the account it is about to become.
    ///
    /// The host unpacks them before Main runs -- as root, in a child of the daemon -- into a directory only its owner
    /// can open (0700), so after the drop not one of them could be loaded: the first the engine needed, the Wayland
    /// shim, failed with a missing library. Only a directory under DOTNET_BUNDLE_EXTRACT_BASE_DIR is touched, and
    /// only its own entries, never following a link.
    /// </summary>
    private static void HandOverLibraries(uint uid, uint gid)
    {
        if (Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR") is not { Length: > 0 } baseDir ||
            AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is not string searched)
        {
            return;
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDir)) + Path.DirectorySeparatorChar;
        foreach (string entry in searched.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry));
            if (!dir.StartsWith(root, StringComparison.Ordinal) || !Directory.Exists(dir))
            {
                continue;
            }

            _ = lchown(dir, uid, gid);
            foreach (string file in Directory.EnumerateFileSystemEntries(dir))
            {
                _ = lchown(file, uid, gid);
            }
        }
    }

    /// <summary>
    /// Makes the process an ordinary one of the user it became, which the drop undid: the kernel marks a process whose
    /// ids changed as not dumpable, and its /proc files then belong to root. xdg-desktop-portal opens /proc/PID/root of
    /// whoever calls it, as the user it serves, to tell a host program from a sandboxed one, and refused the session
    /// agent with "Unable to open /proc/PID/root". For the agent only: from here on it is the user's own process, as
    /// open to them as any other they run. The engine stays as it is.
    /// </summary>
    public static void BecomeOrdinaryProcess()
    {
        const int PR_SET_DUMPABLE = 4;
        _ = prctl(PR_SET_DUMPABLE, 1, 0, 0, 0);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int lchown(string path, uint owner, uint group);

    [DllImport("libc", SetLastError = true)]
    private static extern int setresuid(uint real, uint effective, uint saved);

    [DllImport("libc", SetLastError = true)]
    private static extern int setresgid(uint real, uint effective, uint saved);

    [DllImport("libc", SetLastError = true)]
    private static extern int getresuid(out uint real, out uint effective, out uint saved);

    [DllImport("libc", SetLastError = true)]
    private static extern int getresgid(out uint real, out uint effective, out uint saved);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int initgroups(string user, uint group);
}
