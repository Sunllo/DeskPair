using System.Runtime.Versioning;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Capture;

/// <summary>
/// One reading of the display configuration (<c>QueryDisplayConfig</c>): the paths from sources (desktops) to targets
/// (monitors) and their modes. What finds a display by the adapter and target its driver was given, where GDI and
/// DXGI know it only by its source's name (<c>\\.\DISPLAYn</c>).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DisplayConfiguration
{
    private DisplayConfiguration(User32.DISPLAYCONFIG_PATH_INFO[] paths, User32.DISPLAYCONFIG_MODE_INFO[] modes)
    {
        Paths = paths;
        Modes = modes;
    }

    public User32.DISPLAYCONFIG_PATH_INFO[] Paths { get; }

    public User32.DISPLAYCONFIG_MODE_INFO[] Modes { get; }

    /// <summary>The configuration now, or null when Windows will not say (no desktop to ask about, say).</summary>
    public static unsafe DisplayConfiguration? Query(uint flags = User32.QDC_ONLY_ACTIVE_PATHS)
    {
        // A display coming or going between the two calls makes the buffers too small; ask again.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (User32.GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount) != User32.ERROR_SUCCESS)
            {
                return null;
            }

            var paths = new User32.DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new User32.DISPLAYCONFIG_MODE_INFO[modeCount];
            int result;
            fixed (User32.DISPLAYCONFIG_PATH_INFO* p = paths)
            fixed (User32.DISPLAYCONFIG_MODE_INFO* m = modes)
            {
                result = User32.QueryDisplayConfig(flags, ref pathCount, p, ref modeCount, m, 0);
            }

            if (result == User32.ERROR_INSUFFICIENT_BUFFER)
            {
                continue;
            }

            return result == User32.ERROR_SUCCESS ? new DisplayConfiguration(paths[..(int)pathCount], modes[..(int)modeCount]) : null;
        }

        return null;
    }

    /// <summary>The GDI name of a source, or null when Windows has none for it.</summary>
    public static unsafe string? SourceName(User32.LUID adapter, uint sourceId)
    {
        var name = new User32.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
        name.header.type = User32.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
        name.header.size = (uint)sizeof(User32.DISPLAYCONFIG_SOURCE_DEVICE_NAME);
        name.header.adapterId = adapter;
        name.header.id = sourceId;
        return User32.DisplayConfigGetDeviceInfo(&name.header) == User32.ERROR_SUCCESS ? new string(name.viewGdiDeviceName) : null;
    }

    /// <summary>The index of the active path to the target, or -1 when the target is not on the desktop.</summary>
    public int PathTo(User32.LUID adapter, uint targetId)
    {
        for (int i = 0; i < Paths.Length; i++)
        {
            ref readonly User32.DISPLAYCONFIG_PATH_INFO path = ref Paths[i];
            if ((path.flags & User32.DISPLAYCONFIG_PATH_ACTIVE) != 0 && Same(path.targetInfo.adapterId, adapter) && path.targetInfo.id == targetId)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The GDI name of the display on the target, or null when it is not on the desktop.</summary>
    public string? NameOfTarget(User32.LUID adapter, uint targetId) =>
        PathTo(adapter, targetId) is var i and >= 0 ? SourceName(Paths[i].sourceInfo.adapterId, Paths[i].sourceInfo.id) : null;

    public static bool Same(User32.LUID a, User32.LUID b) => a.LowPart == b.LowPart && a.HighPart == b.HighPart;
}
