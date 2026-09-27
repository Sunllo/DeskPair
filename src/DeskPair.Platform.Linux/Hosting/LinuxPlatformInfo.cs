using System.Runtime.InteropServices;
using DeskPair.Platform.Abstractions.Hosting;
using DeskPair.Platform.Abstractions.Security;
using DeskPair.Platform.Linux.Native;
using DeskPair.Platform.Linux.Wayland;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeskPair.Platform.Linux.Hosting;

/// <summary>
/// What the rest of the app asks about the Linux box: which OS, whether we are root (the system service is),
/// and whether the graphical session is Wayland — the one flag that decides between the X11 capturer and the
/// portal one. X11 grants capture and injection to any client on the display, so there is nothing to grant; a
/// Wayland session asks its person through the portal, and remembers the answer when they let it
/// (<see cref="PortalPermission"/>), which is what <see cref="GrantedPermissions"/> reports when it is given the
/// store the answer is kept in.
/// </summary>
public sealed class LinuxPlatformInfo(ISecretStore? secrets = null) : IPlatformInfo
{
    public OsPlatform Platform => OsPlatform.Linux;

    public string Description => $"{PrettyName()} ({RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()})";

    public bool IsElevated => LibC.geteuid() == 0;

    public bool IsWayland => IsWaylandFrom(
        Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    /// <summary>The decision without the environment, so it can be tested on any machine.</summary>
    internal static bool IsWaylandFrom(string? sessionType, string? waylandDisplay) =>
        string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrEmpty(waylandDisplay);

    /// <summary>
    /// Nothing has to be granted before the engine starts: X11 gates nothing, and on Wayland the first viewer's
    /// connection asks the person if nothing was remembered.
    /// </summary>
    public PlatformPermissions RequiredPermissions => PlatformPermissions.None;

    /// <summary>
    /// X11: everything. Wayland: what the remembered portal permission covers -- screen only when the person left
    /// "allow remote interaction" off, nothing when nothing is remembered or there is no store to look in.
    /// </summary>
    public PlatformPermissions GrantedPermissions()
    {
        if (!IsWayland)
        {
            return PlatformPermissions.ScreenRecording | PlatformPermissions.InputInjection;
        }

        if (secrets is null)
        {
            return PlatformPermissions.None;
        }

        return PortalPermission.StateAsync(secrets, NullLogger.Instance).GetAwaiter().GetResult() switch
        {
            PortalPermissionState.Allowed => PlatformPermissions.ScreenRecording | PlatformPermissions.InputInjection,
            PortalPermissionState.WatchOnly => PlatformPermissions.ScreenRecording,
            _ => PlatformPermissions.None,
        };
    }

    /// <summary>
    /// On Wayland, asks the person now through the portal (the dialog appears on this screen) and remembers the
    /// answer; the settings page awaits <see cref="PortalPermission.AskAsync"/> instead, to say how it ended. On X11
    /// there is nothing to ask.
    /// </summary>
    public void RequestPermissions(PlatformPermissions permissions)
    {
        if (IsWayland && secrets is not null && permissions != PlatformPermissions.None)
        {
            _ = PortalPermission.AskAsync(secrets, NullLoggerFactory.Instance);
        }
    }

    private static string PrettyName()
    {
        try
        {
            return ParsePrettyName(File.ReadLines("/etc/os-release"));
        }
        catch (Exception)
        {
            // No os-release (a minimal container); fall back to the kernel description.
            return "Linux";
        }
    }

    /// <summary>The parse without the file, so it can be tested on any machine.</summary>
    internal static string ParsePrettyName(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
            {
                return line["PRETTY_NAME=".Length..].Trim('"');
            }
        }

        return "Linux";
    }
}
