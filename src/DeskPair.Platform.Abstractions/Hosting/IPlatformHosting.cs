namespace DeskPair.Platform.Abstractions.Hosting;

public enum OsPlatform
{
    Windows,
    MacOS,
    Linux,
}

[Flags]
public enum PlatformPermissions
{
    None = 0,
    ScreenRecording = 1 << 0,
    Accessibility = 1 << 1,
    InputInjection = 1 << 2,
}

public interface IPlatformInfo
{
    OsPlatform Platform { get; }

    string Description { get; }

    bool IsElevated { get; }

    bool IsWayland { get; }

    PlatformPermissions RequiredPermissions { get; }

    PlatformPermissions GrantedPermissions();

    void RequestPermissions(PlatformPermissions permissions);
}
