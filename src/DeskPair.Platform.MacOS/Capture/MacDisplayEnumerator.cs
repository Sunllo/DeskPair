using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.MacOS.Native;

namespace DeskPair.Platform.MacOS.Capture;

/// <summary>
/// Lists displays through CoreGraphics (CGGetActiveDisplayList in the shim). Sizes are backing pixels, which
/// is what the capturer produces and the controller draws; the scale is carried so input can convert a pixel
/// coordinate back to the points CGEvent expects. The primary display is ordered first.
/// </summary>
public sealed class MacDisplayEnumerator : IDisplayEnumerator
{
    private readonly ILogger _log;

    public MacDisplayEnumerator(ILogger log)
    {
        _log = log;
    }

    public event EventHandler? DisplaysChanged
    {
        add { }
        remove { }
    }

    public IReadOnlyList<DisplayDescriptor> GetDisplays()
    {
        int count = MacShim.fd_displays_get(null, 0);
        if (count <= 0)
        {
            _log.LogWarning("No active displays reported by CoreGraphics");
            return [];
        }

        var arr = new MacShim.FdDisplay[count];
        int got = MacShim.fd_displays_get(arr, count);
        var result = new List<DisplayDescriptor>(got);
        for (int i = 0; i < got; i++)
        {
            MacShim.FdDisplay d = arr[i];
            result.Add(new DisplayDescriptor(
                i,
                $"Display {d.Id}",
                d.X,
                d.Y,
                d.Width,
                d.Height,
                d.Scale > 0 ? d.Scale : 1.0,
                FrameRotation.None,
                d.IsPrimary != 0,
                d.Id));
        }

        // Primary first, and the index is the position (see DisplayOrdering); the CGDirectDisplayID stays in AdapterLuid.
        return DisplayOrdering.PrimaryFirst(result);
    }
}
