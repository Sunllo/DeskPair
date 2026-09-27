using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Capture;

/// <summary>
/// The modes a display driver enumerates, and switching between them with <c>ChangeDisplaySettingsEx</c>.
///
/// The change is made without <c>CDS_UPDATEREGISTRY</c> on purpose: the registry keeps the mode the person
/// chose, so a host that dies mid-session comes back to it at the next sign-in, and nothing this program
/// did outlives the session it did it for. A remote desktop session has no modes to offer -- the display
/// there is the client's window -- and says so with an empty list.
/// </summary>
public sealed class WindowsDisplayModes : IDisplayModeSwitcher
{
    public IReadOnlyList<DisplayMode> GetModes(DisplayDescriptor display)
    {
        if (User32.GetSystemMetrics(User32.SM_REMOTESESSION) != 0)
        {
            return [];
        }

        User32.DEVMODEW current = User32.DEVMODEW.Create();
        if (User32.EnumDisplaySettingsEx(display.Name, User32.ENUM_CURRENT_SETTINGS, ref current, 0) == 0)
        {
            return [];
        }

        var modes = new HashSet<(uint W, uint H)>();
        User32.DEVMODEW mode = User32.DEVMODEW.Create();
        for (uint i = 0; User32.EnumDisplaySettingsEx(display.Name, i, ref mode, 0) != 0; i++)
        {
            // The colour depth is not on offer: a 16-bit mode is a different picture, not a different size.
            if (mode.dmBitsPerPel == current.dmBitsPerPel && mode.dmPelsWidth >= 640 && mode.dmPelsHeight >= 480)
            {
                modes.Add((mode.dmPelsWidth, mode.dmPelsHeight));
            }

            mode = User32.DEVMODEW.Create();
        }

        return modes
            .OrderByDescending(m => (long)m.W * m.H)
            .ThenByDescending(m => m.W)
            .Select(m => new DisplayMode((int)m.W, (int)m.H))
            .ToList();
    }

    public bool TrySetMode(DisplayDescriptor display, DisplayMode wanted, out string? failure)
    {
        User32.DEVMODEW current = User32.DEVMODEW.Create();
        if (User32.EnumDisplaySettingsEx(display.Name, User32.ENUM_CURRENT_SETTINGS, ref current, 0) == 0)
        {
            failure = "The display's current mode could not be read.";
            return false;
        }

        // The driver's own entry for that size, at the refresh rate in use if it offers one, else its
        // highest; a DEVMODE built from scratch can name a combination the driver never listed.
        User32.DEVMODEW? best = null;
        User32.DEVMODEW candidate = User32.DEVMODEW.Create();
        for (uint i = 0; User32.EnumDisplaySettingsEx(display.Name, i, ref candidate, 0) != 0; i++)
        {
            if (candidate.dmPelsWidth == (uint)wanted.Width && candidate.dmPelsHeight == (uint)wanted.Height && candidate.dmBitsPerPel == current.dmBitsPerPel)
            {
                if (best is null
                    || candidate.dmDisplayFrequency == current.dmDisplayFrequency
                    || (best.Value.dmDisplayFrequency != current.dmDisplayFrequency && candidate.dmDisplayFrequency > best.Value.dmDisplayFrequency))
                {
                    best = candidate;
                }
            }

            candidate = User32.DEVMODEW.Create();
        }

        if (best is null)
        {
            failure = $"{wanted.Width}x{wanted.Height} is not a size this display can show.";
            return false;
        }

        User32.DEVMODEW target = best.Value;
        target.dmFields = User32.DM_PELSWIDTH | User32.DM_PELSHEIGHT | User32.DM_BITSPERPEL | User32.DM_DISPLAYFREQUENCY;

        int tested = User32.ChangeDisplaySettingsEx(display.Name, ref target, 0, User32.CDS_TEST, 0);
        if (tested != User32.DISP_CHANGE_SUCCESSFUL)
        {
            failure = Describe(tested);
            return false;
        }

        int result = User32.ChangeDisplaySettingsEx(display.Name, ref target, 0, 0, 0);
        if (result != User32.DISP_CHANGE_SUCCESSFUL)
        {
            failure = Describe(result);
            return false;
        }

        failure = null;
        return true;
    }

    private static string Describe(int result) => result switch
    {
        User32.DISP_CHANGE_RESTART => "The computer would have to restart for that mode.",
        User32.DISP_CHANGE_BADMODE => "The display does not support that mode.",
        User32.DISP_CHANGE_BADFLAGS or User32.DISP_CHANGE_BADPARAM => "The mode request was malformed.",
        User32.DISP_CHANGE_NOTUPDATED => "The display settings could not be written.",
        User32.DISP_CHANGE_BADDUALVIEW => "The display is in a dual-view arrangement that refuses the change.",
        _ => "The display driver refused the change.",
    };
}
