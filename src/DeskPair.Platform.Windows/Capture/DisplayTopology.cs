using System.Runtime.Versioning;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Capture;

/// <summary>
/// Which displays are on: the one change a private session screen makes to the desktop, and putting it back.
///
/// "Only this one" is saved to Windows' display database, for the set of displays connected at the time -- the
/// physical ones and the session screen. Unsaved, it lasts only until Windows next reconsiders the desktop, and a
/// virtual display changing size makes it do that: it applies what it remembers for that set, the physical displays
/// light up again, and the private screen is private no more (measured on Windows 10 22H2). Saved, it holds; and it
/// touches nothing else, because the entry is for a set that exists only while the session screen is plugged in.
/// Unplugging it leaves the physical displays alone again, and Windows applies what it remembers for them: the
/// desktop as it was.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DisplayTopology
{
    /// <summary>
    /// Makes the display on the target the only one on, at the desktop's origin, and has Windows keep that for the
    /// displays connected now. Returns SetDisplayConfig's result (0 when it was so already).
    /// </summary>
    public static unsafe int OnlyThis(User32.LUID adapter, uint targetId)
    {
        DisplayConfiguration? now = DisplayConfiguration.Query();
        if (now is null)
        {
            return ErrorGenFailure;
        }

        int index = now.PathTo(adapter, targetId);
        if (index < 0)
        {
            return ErrorNotFound;
        }

        if (now.Paths.Length == 1)
        {
            return User32.ERROR_SUCCESS; // Windows remembered it that way
        }

        User32.DISPLAYCONFIG_PATH_INFO path = now.Paths[index];
        User32.DISPLAYCONFIG_MODE_INFO[] modes = now.Modes;
        uint source = path.sourceInfo.modeInfoIdx;
        if (source < modes.Length && modes[source].infoType == User32.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
        {
            modes[source].sourceMode.x = 0;
            modes[source].sourceMode.y = 0;
        }

        fixed (User32.DISPLAYCONFIG_MODE_INFO* m = modes)
        {
            return User32.SetDisplayConfig(1, &path, (uint)modes.Length, m,
                User32.SDC_USE_SUPPLIED_DISPLAY_CONFIG | User32.SDC_APPLY | User32.SDC_SAVE_TO_DATABASE | User32.SDC_ALLOW_CHANGES);
        }
    }

    /// <summary>Whether the display on the target is on and nothing else is.</summary>
    public static bool IsOnlyOne(User32.LUID adapter, uint targetId) =>
        DisplayConfiguration.Query() is { } now && now.Paths.Length == 1 && now.PathTo(adapter, targetId) == 0;

    /// <summary>Whether any display is on.</summary>
    public static bool AnyOn() => DisplayConfiguration.Query() is { Paths.Length: > 0 };

    /// <summary>
    /// Applies what Windows remembers for the displays connected now -- for when it did not do so itself and left none
    /// on. Returns SetDisplayConfig's result.
    /// </summary>
    public static unsafe int RestoreRemembered() =>
        User32.SetDisplayConfig(0, null, 0, null, User32.SDC_APPLY | User32.SDC_USE_DATABASE_CURRENT);

    private const int ErrorNotFound = 1168;
    private const int ErrorGenFailure = 31;
}
