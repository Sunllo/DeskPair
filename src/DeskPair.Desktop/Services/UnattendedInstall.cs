namespace DeskPair.Desktop.Services;

/// <summary>
/// The names and places the unattended install uses, on all three systems, in one file.
///
/// Every one of them is chosen to miss something that already exists. <see cref="LegacyInstall"/> deletes
/// a launch agent called <c>com.sunllo.deskpair</c> on sight, and <c>com.sunllo.deskpair.login</c> is what
/// "start at sign-in" uses; <c>deskpair.service</c> is a unit an older version installed and this one
/// warns about; and <c>deskpair-host.service</c> would sit in the middle of the <c>deskpair-rendezvous</c>
/// family that deploy/install.sh creates for the network servers, which are unrelated programs. Renaming
/// any of these later means walking back into whichever of those it collides with, so they are written
/// down together rather than spelled out at each use.
///
/// The question this answers for the app is one question: is something other than me responsible for the
/// engine on this machine? Two engines share a data directory, so they take the same identity and displace
/// each other at the rendezvous server, want the same direct-access port, and race for the same socket.
/// All three were watched happening on Windows before any of this was written.
/// </summary>
internal static class UnattendedInstall
{
    /// <summary>The Windows service; see <c>HostServiceInstaller</c>, which owns it.</summary>
    public const string WindowsService = "SunlloDeskPair";

    /// <summary>The systemd unit: prefixed, so it is neither the old one nor one of the servers'.</summary>
    public const string SystemdUnit = "sunllo-deskpair.service";

    public const string SystemdUnitPath = "/etc/systemd/system/" + SystemdUnit;

    /// <summary>
    /// The job with a screen. It is a LaunchAgent rather than a daemon because only an agent can be
    /// limited to a session type, and <c>LimitLoadToSessionType = [LoginWindow, Aqua]</c> is the entire
    /// mechanism by which anything of ours ends up in the session that has the login window in it.
    /// </summary>
    public const string MacAgentLabel = "com.sunllo.deskpair.agent";

    /// <summary>
    /// The app's own bundle identifier, which the two jobs name so that macOS files them under DeskPair in
    /// Login Items &amp; Extensions rather than showing an unexplained background item. It is attribution,
    /// not authorisation -- the screen-recording consent is keyed to the executable, not to this.
    /// </summary>
    public const string MacBundleIdentifier = "com.sunllo.deskpair";

    /// <summary>Where a machine-wide install keeps the identity, the config and the engine's log.</summary>
    public const string MacDataDirectory = "/Library/Application Support/Sunllo/DeskPair";

    public const string MacAgentPlist = "/Library/LaunchAgents/" + MacAgentLabel + ".plist";

    /// <summary>
    /// Whether the unattended install is present on this machine.
    ///
    /// Asked on every start rather than remembered: it can be installed or removed while the app is
    /// closed, and on Windows the answer is one cheap call to the service control manager. On the other
    /// two it is the presence of a file this program wrote, which is a weaker question than "and is it
    /// healthy" -- but the healthy answer does not help. If the daemon is installed and broken, starting a
    /// second engine beside it makes a bad state into a confusing one.
    /// </summary>
    /// <summary>
    /// Whether the install is there and, where that can be asked, running: on Windows a stopped service owns no
    /// engine, and treating it as the owner is what left an app attached to nothing. Elsewhere the same as
    /// <see cref="IsInstalled"/>, for the reason given there.
    /// </summary>
    public static bool IsActive()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return Engine.Service.HostServiceInstaller.IsRunning();
        }
#endif

        return IsInstalled();
    }

    public static bool IsInstalled()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return Engine.Service.HostServiceInstaller.IsInstalled();
        }
#endif

        if (OperatingSystem.IsMacOS())
        {
            // The plist, not a running process. The agent comes and goes with sessions by design -- that
            // is what LimitLoadToSessionType means -- so "is one running right now" answers a different
            // question from "has this Mac been set up".
            return File.Exists(MacAgentPlist);
        }

        return OperatingSystem.IsLinux() && File.Exists(SystemdUnitPath);
    }
}
