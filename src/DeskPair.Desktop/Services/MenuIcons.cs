using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DeskPair.Desktop.Services;

/// <summary>
/// The little pictures beside the tray menu's entries. A native menu takes a bitmap and nothing else — it
/// will not take a vector, a brush or a colour for its text — so each icon is drawn once into a small
/// bitmap, in the colour it should be, and handed over.
///
/// Drawing rather than shipping images keeps them one file, scales them to whatever the menu asks for, and
/// lets the colour carry the meaning a native menu cannot carry any other way: the way out of the
/// application is red, and everything else is the same quiet grey.
/// </summary>
internal static class MenuIcons
{
    /// <summary>An application window: a frame with its title bar filled in.</summary>
    public const string Window =
        "M 3 4 h 18 a 2 2 0 0 1 2 2 v 12 a 2 2 0 0 1 -2 2 h -18 a 2 2 0 0 1 -2 -2 v -12 a 2 2 0 0 1 2 -2 z "
        + "M 3 6 v 3 h 18 v -3 z";

    /// <summary>A cog: a ring with eight teeth and a hole.</summary>
    public const string Settings =
        "M 12 8.4 a 3.6 3.6 0 1 0 0 7.2 a 3.6 3.6 0 0 0 0 -7.2 z "
        + "M 12 10 a 2 2 0 1 1 0 4 a 2 2 0 0 1 0 -4 z "
        + "M 10.6 1 h 2.8 l 0.45 2.6 l 1.9 0.8 l 2.15 -1.5 l 2 2 l -1.5 2.15 l 0.8 1.9 l 2.6 0.45 v 2.8 "
        + "l -2.6 0.45 l -0.8 1.9 l 1.5 2.15 l -2 2 l -2.15 -1.5 l -1.9 0.8 l -0.45 2.6 h -2.8 l -0.45 -2.6 "
        + "l -1.9 -0.8 l -2.15 1.5 l -2 -2 l 1.5 -2.15 l -0.8 -1.9 l -2.6 -0.45 v -2.8 l 2.6 -0.45 l 0.8 -1.9 "
        + "l -1.5 -2.15 l 2 -2 l 2.15 1.5 l 1.9 -0.8 z "
        + "M 12 6.6 a 5.4 5.4 0 1 0 0 10.8 a 5.4 5.4 0 0 0 0 -10.8 z";

    /// <summary>A door with an arrow leaving it: signing out.</summary>
    public const string SignOut =
        "M 3 3 h 9 v 2 h -7 v 14 h 7 v 2 h -9 z "
        + "M 14.3 6.3 l 1.4 -1.4 l 6.1 6.1 a 1.4 1.4 0 0 1 0 1.98 l -6.1 6.12 l -1.4 -1.4 l 4.3 -4.3 "
        + "h -9.6 v -2.8 h 9.6 z";

    /// <summary>A power symbol: quitting for real.</summary>
    public const string Quit =
        "M 10.6 2 h 2.8 v 9.5 h -2.8 z "
        + "M 6.6 4.7 l 1.9 2.05 a 7 7 0 1 0 7 0 l 1.9 -2.05 a 9.8 9.8 0 1 1 -10.8 0 z";

    /// <summary>
    /// One icon at the size a Windows menu wants. 0xAARRGGBB rather than a brush because the colour is a
    /// constant of the entry, not something the theme changes: a native menu is drawn by the operating
    /// system and never consults ours.
    /// </summary>
    public static Bitmap? Render(string pathData, uint argb, int size = 20)
    {
        try
        {
            Geometry geometry = StreamGeometry.Parse(pathData);
            Rect bounds = geometry.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return null;
            }

            // A little room around the shape, so the icon does not touch the text or the menu's edge.
            const double Padding = 2;
            double scale = Math.Min((size - (2 * Padding)) / bounds.Width, (size - (2 * Padding)) / bounds.Height);
            var target = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
            using (DrawingContext context = target.CreateDrawingContext())
            {
                var transform = Matrix.CreateTranslation(-bounds.X, -bounds.Y)
                    * Matrix.CreateScale(scale, scale)
                    * Matrix.CreateTranslation(
                        (size - (bounds.Width * scale)) / 2,
                        (size - (bounds.Height * scale)) / 2);
                using (context.PushTransform(transform))
                {
                    context.DrawGeometry(new SolidColorBrush(argb), null, geometry);
                }
            }

            return target;
        }
        catch (Exception)
        {
            // An icon is decoration; a menu without one still works, and a tray that fails to appear does not.
            return null;
        }
    }
}
