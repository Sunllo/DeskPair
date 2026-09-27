using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.MacOS.Native;

namespace DeskPair.Platform.MacOS.Capture;

/// <summary>
/// CoreGraphics display modes through the shim. The descriptor's <c>AdapterLuid</c> is the CGDirectDisplayID
/// (see <see cref="MacDisplayEnumerator"/>). A change lasts for the login session: nothing is written into
/// the person's settings, and a host that dies is back to normal at the next login.
/// </summary>
public sealed class MacDisplayModes : IDisplayModeSwitcher
{
    public IReadOnlyList<DisplayMode> GetModes(DisplayDescriptor display)
    {
        if (!MacShim.IsAvailable)
        {
            return [];
        }

        uint id = (uint)display.AdapterLuid;
        int count = MacShim.fd_display_modes_get(id, null, 0);
        if (count <= 0)
        {
            return [];
        }

        var modes = new MacShim.FdDisplayMode[count];
        int got = MacShim.fd_display_modes_get(id, modes, count);
        return modes.Take(got).Select(m => new DisplayMode(m.Width, m.Height, m.Scale)).ToList();
    }

    public bool TrySetMode(DisplayDescriptor display, DisplayMode mode, out string? failure)
    {
        if (!MacShim.IsAvailable)
        {
            failure = "The macOS shim is not available.";
            return false;
        }

        int result = MacShim.fd_display_mode_set((uint)display.AdapterLuid, mode.Width, mode.Height, mode.Scale);
        failure = result switch
        {
            0 => null,
            -1 => $"{mode.Width}x{mode.Height} is not a size this display can show.",
            _ => $"macOS refused the mode (CGError {result}).",
        };
        return failure is null;
    }
}
