using System.Runtime.InteropServices;
using DeskPair.Platform.Abstractions.Hosting;
using DeskPair.Platform.MacOS.Native;

namespace DeskPair.Platform.MacOS.Hosting;

/// <summary>
/// What the app asks about the Mac: which OS, whether it is root, and — the part that matters on macOS — the
/// two consents a remote-desktop host needs. Screen Recording and Accessibility cannot be granted in code;
/// they are checked with TCC preflight and prompted for, which is what the permission onboarding drives.
/// </summary>
public sealed class MacPlatformInfo : IPlatformInfo
{
    public OsPlatform Platform => OsPlatform.MacOS;

    public string Description => $"macOS {Environment.OSVersion.Version} ({RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()})";

    public bool IsElevated => MacShim.fd_is_root() != 0;

    public bool IsWayland => false;

    public PlatformPermissions RequiredPermissions => PlatformPermissions.ScreenRecording | PlatformPermissions.Accessibility;

    public PlatformPermissions GrantedPermissions()
    {
        PlatformPermissions granted = PlatformPermissions.None;
        if (MacShim.fd_tcc_screen_granted() != 0)
        {
            granted |= PlatformPermissions.ScreenRecording;
        }

        if (MacShim.fd_tcc_accessibility_granted() != 0)
        {
            granted |= PlatformPermissions.Accessibility | PlatformPermissions.InputInjection;
        }

        return granted;
    }

    public void RequestPermissions(PlatformPermissions permissions)
    {
        if (permissions.HasFlag(PlatformPermissions.ScreenRecording))
        {
            MacShim.fd_tcc_screen_request();
        }

        if (permissions.HasFlag(PlatformPermissions.Accessibility) || permissions.HasFlag(PlatformPermissions.InputInjection))
        {
            MacShim.fd_tcc_accessibility_request();
        }
    }
}
