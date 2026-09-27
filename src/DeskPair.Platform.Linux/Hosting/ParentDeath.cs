using System.Runtime.InteropServices;

namespace DeskPair.Platform.Linux.Hosting;

/// <summary>
/// Ties an engine's life to the daemon that started it.
///
/// A daemon that dies -- crashes, is killed, is replaced -- leaves its engine running otherwise, and
/// that orphan keeps tcp/21118 and the IPC socket, so the daemon that comes next starts an engine that
/// can bind neither and says so every five seconds. Under systemd the unit's control group takes both
/// down together; this is for every other way a daemon can end, and it costs one line at start-up.
/// </summary>
public static partial class ParentDeath
{
    private const int PR_SET_PDEATHSIG = 1;
    private const int SIGTERM = 15;

    /// <summary>
    /// Asks the kernel for SIGTERM when the parent thread exits. Linux only; elsewhere a no-op. The
    /// parent may have gone between fork and this call, so a parent that is already init is treated as
    /// gone too: false, and the caller should not carry on.
    /// </summary>
    public static bool FollowParent()
    {
        if (!OperatingSystem.IsLinux())
        {
            return true;
        }

        _ = prctl(PR_SET_PDEATHSIG, SIGTERM, 0, 0, 0);
        return getppid() != 1;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);

    [LibraryImport("libc")]
    private static partial int getppid();
}
