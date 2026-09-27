using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DeskPair.Desktop.Services;

/// <summary>
/// Clears what earlier versions left behind, before this app looks for an engine to talk to.
///
/// The ordering matters. DeskPair used to ship a second program, <c>deskpair-service</c>, and an OS
/// service to keep it running. One of those still listening would be adopted by <see cref="EngineHost"/> as
/// "an engine is already running" — and on macOS it is a different executable, so the screen-recording
/// consent granted to this app would not cover it and capture would go back to producing a black screen.
/// Which is the bug the single executable exists to fix.
///
/// The names here are the ones being cleared away; the ones being installed are in
/// <see cref="UnattendedInstall"/>, and they were chosen to miss every name in this file. Renaming
/// either set later means walking back into the other, so change them together or not at all.
/// </summary>
internal static class LegacyInstall
{
    private const string OldEngineProcess = "deskpair-service";
    private const string LaunchAgentLabel = "com.sunllo.deskpair";
    private const string WindowsServiceName = "DeskPair";
    // The unit this version installs is UnattendedInstall.SystemdUnit (sunllo-deskpair.service); this is the
    // older, unprefixed one, which must never be reused or the warning below fires at our own install.
    private const string SystemdUnit = "/etc/systemd/system/deskpair.service";

    public static void Sweep(ILogger log)
    {
        StopOldEngine(log);

        if (OperatingSystem.IsMacOS())
        {
            RemoveLaunchAgent(log);
        }
        else if (OperatingSystem.IsLinux())
        {
            ReportSystemdUnit(log);
        }
        else
        {
            ReportWindowsService(log);
        }
    }

    /// <summary>The engine that used to be its own program. Its executable no longer ships, so nothing can restart it.</summary>
    private static void StopOldEngine(ILogger log)
    {
        Process[] stale;
        try
        {
            stale = Process.GetProcessesByName(OldEngineProcess);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
        {
            return;
        }

        foreach (Process process in stale)
        {
            try
            {
                log.LogInformation("Stopping the host engine left over from an older version (pid {Pid})", process.Id);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                log.LogWarning(e, "Could not stop the old host engine (pid {Pid}); it may hold the direct-access port", process.Id);
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>The user's own launch agent, so removing it needs no more rights than the user already has.</summary>
    private static void RemoveLaunchAgent(ILogger log)
    {
        string plist = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", $"{LaunchAgentLabel}.plist");
        if (!File.Exists(plist))
        {
            return;
        }

        log.LogInformation("Removing the launch agent an older version installed ({Plist})", plist);
        Run(log, "launchctl", $"bootout gui/{Engine.NativeUser.Geteuid()}/{LaunchAgentLabel}");
        try
        {
            File.Delete(plist);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(e, "Could not delete {Plist}; DeskPair may be started twice at sign-in", plist);
        }
    }

    /// <summary>
    /// Removing a machine-wide service needs administrator rights this app does not have, so it says what to
    /// run instead. Leaving it costs nothing: it points at an executable that no longer exists, so it fails
    /// to start and does nothing.
    /// </summary>
    private static void ReportWindowsService(ILogger log)
    {
#if WINDOWS
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{WindowsServiceName}");
        if (key is not null)
        {
            log.LogWarning(
                "An older version installed the \"{Service}\" service. It cannot start any more and is not used; to remove it, run as administrator: sc stop DeskPair && sc delete DeskPair",
                WindowsServiceName);
        }
#endif
    }

    private static void ReportSystemdUnit(ILogger log)
    {
        if (File.Exists(SystemdUnit))
        {
            log.LogWarning(
                "An older version installed {Unit}. It cannot start any more and is not used; to remove it: sudo systemctl disable --now deskpair, sudo rm that file, sudo systemctl daemon-reload",
                SystemdUnit);
        }
    }

    private static void Run(ILogger log, string file, string arguments)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true });
            process?.WaitForExit(5000);
        }
        catch (Exception e)
        {
            log.LogDebug(e, "{File} {Arguments} failed", file, arguments);
        }
    }
}
