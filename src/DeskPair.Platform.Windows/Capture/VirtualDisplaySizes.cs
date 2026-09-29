using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Capture;

/// <summary>
/// The sizes an added display is plugged in with. They are every size it can ever be switched to: Windows fixes a
/// display's list when it arrives, and takes at most 199 (native/idd/README.md). So they are chosen for the viewer who
/// asked for it, whose window the display is to fill:
/// <list type="number">
///   <item>the size it comes up at, then the other sizes the viewer said it would want (its screens, their work areas);</item>
///   <item>the common sizes, so the resolution menu has the usual choices;</item>
///   <item>the rest near the named sizes, <see cref="Step"/> apart and nearest first, taking turns: in rings around the
///     size it comes up at -- the window it was asked for, dragged a little either way -- and in a narrow column under
///     each of the others, since a window maximised on a screen is its work area less a title bar and a toolbar, and at
///     most a border less wide. None is larger than the largest the viewer named: no window of its is.</item>
/// </list>
/// A window of any other size gets the largest listed size that fits in it (<c>ResolutionFit</c>, on the viewer).
/// </summary>
internal static class VirtualDisplaySizes
{
    /// <summary>The largest width or height offered: a hardware H.264 encoder goes no further.</summary>
    public const int MaxSide = 4096;

    /// <summary>How far apart the sizes around the named ones are.</summary>
    public const int Step = 16;

    /// <summary>How many steps narrower than a named size its column goes.</summary>
    private const int ColumnSteps = 2;

    /// <summary>16:9, 16:10, 4:3, 5:4 and the two ultrawides.</summary>
    internal static readonly (int Width, int Height)[] CommonSizes =
    [
        (1920, 1080), (1280, 720), (1366, 768), (1600, 900), (2560, 1440), (3840, 2160),
        (1280, 800), (1440, 900), (1680, 1050), (1920, 1200), (2560, 1600),
        (1024, 768), (1600, 1200), (1280, 1024), (2560, 1080), (3440, 1440),
    ];

    /// <summary>The list, <paramref name="start"/> (made even and in range) first.</summary>
    /// <param name="start">What the display comes up at.</param>
    /// <param name="wanted">Other sizes the viewer expects its window to have, most likely first.</param>
    /// <param name="capacity">How many sizes the display may have.</param>
    public static List<DisplayMode> For(DisplayMode start, IReadOnlyList<DisplayMode> wanted, int capacity = IddControl.MaxModes)
    {
        var sizes = new List<DisplayMode>(capacity);
        var seen = new HashSet<(int, int)>();
        bool Add(int width, int height)
        {
            width = Fit(width);
            height = Fit(height);
            if (sizes.Count >= capacity || !seen.Add((width, height)))
            {
                return false;
            }

            sizes.Add(new DisplayMode(width, height));
            return true;
        }

        Add(start.Width, start.Height);
        foreach (DisplayMode size in wanted)
        {
            Add(size.Width, size.Height);
        }

        DisplayMode first = sizes[0];
        List<DisplayMode> others = [.. sizes.Skip(1)];
        int maxWidth = sizes.Max(a => a.Width);
        int maxHeight = sizes.Max(a => a.Height);
        foreach ((int width, int height) in CommonSizes)
        {
            Add(width, height);
        }

        void Near(DisplayMode anchor, int dx, int dy)
        {
            int width = anchor.Width + dx * Step;
            int height = anchor.Height + dy * Step;
            if (width >= IddControl.MinSide && height >= IddControl.MinSide && width <= maxWidth && height <= maxHeight)
            {
                Add(width, height);
            }
        }

        // The farthest any of them could still reach bounds the loop.
        int rings = Math.Max(maxWidth, maxHeight) / Step;
        for (int ring = 1; ring <= rings && sizes.Count < capacity; ring++)
        {
            foreach ((int dx, int dy) in Ring(ring))
            {
                Near(first, dx, dy);
            }

            foreach (DisplayMode other in others)
            {
                foreach ((int dx, int dy) in Column(ring))
                {
                    Near(other, dx, dy);
                }
            }
        }

        return sizes;
    }

    /// <summary>The steps r away: the top and bottom rows of the square, then its sides.</summary>
    private static IEnumerable<(int Dx, int Dy)> Ring(int r)
    {
        for (int d = -r; d <= r; d++)
        {
            yield return (d, -r);
            yield return (d, r);
        }

        for (int d = -r + 1; d < r; d++)
        {
            yield return (-r, d);
            yield return (r, d);
        }
    }

    /// <summary>
    /// The steps r away below a named size, in a column <see cref="ColumnSteps"/> steps wide: its row r steps down and,
    /// near the corner, its side r steps in.
    /// </summary>
    private static IEnumerable<(int Dx, int Dy)> Column(int r)
    {
        for (int dx = 0; dx >= -Math.Min(r, ColumnSteps); dx--)
        {
            yield return (dx, -r);
        }

        if (r <= ColumnSteps)
        {
            for (int dy = 0; dy > -r; dy--)
            {
                yield return (-r, dy);
            }
        }
    }

    /// <summary>In the driver's range and no larger than an encoder takes, and even: encoders work in pairs of pixels.</summary>
    private static int Fit(int side) => Math.Clamp(side, IddControl.MinSide, MaxSide) & ~1;
}
