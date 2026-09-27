using System.Security.Principal;
using DeskPair.Platform.Abstractions.Hosting;

namespace DeskPair.Platform.Windows.Hosting;

public sealed class WindowsPlatformInfo : IPlatformInfo
{
    public OsPlatform Platform => OsPlatform.Windows;

    public string Description => $"Windows {Environment.OSVersion.Version} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})";

    public bool IsElevated
    {
        get
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public bool IsSystem
    {
        get
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity.IsSystem;
        }
    }

    public bool IsWayland => false;

    /// <summary>Windows grants capture and injection to any interactive process; nothing to request.</summary>
    public PlatformPermissions RequiredPermissions => PlatformPermissions.None;

    public PlatformPermissions GrantedPermissions() => PlatformPermissions.ScreenRecording | PlatformPermissions.InputInjection;

    public void RequestPermissions(PlatformPermissions permissions)
    {
    }
}
