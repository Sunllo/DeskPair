using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace DeskPair.Desktop.Services;

/// <summary>
/// Asks Windows to start the app again after an installer has closed it to replace its files.
///
/// Windows Installer closes a running DeskPair through the Restart Manager before an upgrade, and afterwards starts
/// only the programs that asked to be started. Without this, an upgrade -- the MSI run by hand, or pushed with /qn --
/// left the computer unreachable until somebody opened DeskPair again. It comes back in the notification area with the
/// engine running, as it does at sign-in. Not after a crash or a hang, where the crash dialog says what happened and a
/// loop of restarts would bury it; and not after a reboot, where "start at sign-in" is the setting that decides.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class RestartAfterUpdate
{
    private const int RestartNoCrash = 1;
    private const int RestartNoHang = 2;
    private const int RestartNoReboot = 8;

    public static void Register(ILogger log)
    {
        int result = RegisterApplicationRestart("--minimised", RestartNoCrash | RestartNoHang | RestartNoReboot);
        if (result != 0)
        {
            log.LogDebug("Could not ask to be restarted after an update (0x{Result:X8})", result);
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegisterApplicationRestart(string? commandLine, int flags);
}
