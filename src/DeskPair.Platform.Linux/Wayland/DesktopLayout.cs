using System.Globalization;
using DeskPair.Platform.Abstractions.Input;
using DeskPair.Platform.Linux.Native;

namespace DeskPair.Platform.Linux.Wayland;

/// <summary>
/// One monitor where the compositor has put it: the connector it is on, as the compositor names it, and its rectangle
/// on the desktop in the units the compositor's pointer moves in (GNOME's logical pixels, which are physical ones
/// unless the desktop is scaled).
/// </summary>
internal readonly record struct LayoutMonitor(string Connector, int X, int Y, int Width, int Height)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Connector} {Width}x{Height}+{X}+{Y}");
}

/// <summary>
/// The compositor's desktop: every monitor on it, and where.
///
/// The unattended engine sees the lock screen through the display hardware, which is one monitor's scanout, and
/// moves the pointer through the daemon's virtual device, whose axes the compositor spreads over the whole desktop:
/// mutter scales libinput's absolute axes to the extent of all its monitors together. With two monitors side by side a
/// click at x on the picture of the first landed at 2x, on the second. What is missing is where the scanout's monitor
/// is on that desktop, which the hardware does not know and the compositor does, so the session agent reads it from
/// the compositor and this places a point of the picture on the desktop.
/// </summary>
internal sealed record DesktopLayout(IReadOnlyList<LayoutMonitor> Monitors)
{
    /// <summary>How far the picture's shape may be from the monitor's before it is taken to be some other picture (a rotated one).</summary>
    private const double ShapeTolerance = 0.02;

    /// <summary>
    /// The monitor on the connector the kernel numbers <paramref name="type"/> and <paramref name="typeId"/>, or null
    /// when the compositor has none there. Compositors name connectors after the kernel's types, but not all alike:
    /// the kernel's HDMI-A-1 is mutter's HDMI-1.
    /// </summary>
    public LayoutMonitor? Find(uint type, uint typeId)
    {
        foreach (LayoutMonitor monitor in Monitors)
        {
            if (IsConnector(monitor.Connector, type, typeId))
            {
                return monitor;
            }
        }

        return null;
    }

    /// <summary>
    /// A point of the picture of <paramref name="monitor"/> as the same point of the whole desktop, with the desktop as
    /// the virtual screen to place it in: what a pointer spread over the desktop is given. Both are in the picture's
    /// own pixels, so a monitor the compositor scales keeps every one of them. Null when the picture is not the
    /// monitor's shape -- a rotated monitor whose scanout is sideways -- since it cannot then be mapped by proportion.
    /// </summary>
    /// <param name="monitor">The monitor the picture is of, one of <see cref="Monitors"/>.</param>
    /// <param name="x">A point of the picture, in the virtual screen's coordinates.</param>
    /// <param name="y">Likewise.</param>
    /// <param name="picture">The virtual screen, which on the hardware is the one monitor's picture.</param>
    public (int X, int Y, VirtualScreenRect Desktop)? Place(LayoutMonitor monitor, int x, int y, in VirtualScreenRect picture)
    {
        if (Monitors.Count == 0 || monitor.Width <= 0 || monitor.Height <= 0 || picture.Width <= 0 || picture.Height <= 0)
        {
            return null;
        }

        // Picture pixels per desktop unit, across and down: 1 unscaled, 2 on a monitor scaled by two.
        double across = picture.Width / (double)monitor.Width;
        double down = picture.Height / (double)monitor.Height;
        if (Math.Abs(across - down) > ShapeTolerance * Math.Max(across, down))
        {
            return null;
        }

        // As mutter measures it: from the leftmost edge to the rightmost, and the absolute axes spread over that.
        int left = Monitors.Min(m => m.X);
        int top = Monitors.Min(m => m.Y);
        int width = Monitors.Max(m => m.X + m.Width) - left;
        int height = Monitors.Max(m => m.Y + m.Height) - top;
        var desktop = new VirtualScreenRect(0, 0, (int)Math.Round(width * across), (int)Math.Round(height * down));
        return (
            (int)Math.Round(monitor.X * across) + Math.Clamp(x - picture.X, 0, picture.Width - 1),
            (int)Math.Round(monitor.Y * down) + Math.Clamp(y - picture.Y, 0, picture.Height - 1),
            desktop);
    }

    public override string ToString() => Monitors.Count == 0 ? "no monitors" : string.Join(", ", Monitors);

    /// <summary>Whether <paramref name="name"/> ("Virtual-2", "HDMI-1") is the connector of that type and number.</summary>
    internal static bool IsConnector(string name, uint type, uint typeId)
    {
        int dash = name.LastIndexOf('-');
        if (dash <= 0 || !uint.TryParse(name.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out uint number) || number != typeId)
        {
            return false;
        }

        ReadOnlySpan<char> kind = name.AsSpan(0, dash);
        return kind.Equals(Drm.ConnectorTypeName(type), StringComparison.OrdinalIgnoreCase) ||
               kind.Equals(MutterTypeName(type), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where mutter's names for connector types differ from the kernel's; otherwise the same name.</summary>
    private static string MutterTypeName(uint type) => type switch
    {
        0 => "None",
        11 => "HDMI",
        _ => Drm.ConnectorTypeName(type),
    };
}
