using Avalonia;

namespace DeskPair.Desktop.Controls;

/// <summary>
/// Where the remote picture goes in its control, decided in the screen's own pixels.
///
/// Avalonia lays out in device-independent units, and on a screen at 150 % a unit is one and a half pixels. The
/// picture used to be laid out one unit per remote pixel, so on such a screen even "1:1" reached the glass
/// stretched by half and blurred with it. Here the natural size gives every remote pixel a whole number of
/// screen pixels -- one below 200 %, two from 200 % (the size the picture had before on those screens, now
/// without the blur) -- Fit only ever shrinks from there, and the picture's corner sits on a pixel boundary.
/// Pure, so the arithmetic is tested without a window.
/// </summary>
public readonly record struct PictureLayout(Rect Destination, double ScreenPixelsPerRemotePixel)
{
    /// <summary>
    /// True when every remote pixel covers a whole number of screen pixels. Nearest-neighbour then copies them
    /// exactly, where any smoothing filter would only soften them.
    /// </summary>
    public bool IsWhole => ScreenPixelsPerRemotePixel >= 1 && ScreenPixelsPerRemotePixel == Math.Floor(ScreenPixelsPerRemotePixel);

    /// <summary>
    /// The picture's place in a control of <paramref name="bounds"/> units on a screen scaled by
    /// <paramref name="scaling"/>. Fit centres it and shrinks it when it does not fit; otherwise it sits at its
    /// natural size in the corner, for a scroller to move.
    /// </summary>
    public static PictureLayout Compute(Size bounds, double scaling, int remoteWidth, int remoteHeight, bool fit)
    {
        if (remoteWidth <= 0 || remoteHeight <= 0)
        {
            return default;
        }

        double s = Usable(scaling);
        double pixels = NaturalFactor(s);
        double boundsWidth = WholeIfNear(bounds.Width * s), boundsHeight = WholeIfNear(bounds.Height * s);
        if (fit)
        {
            pixels = Math.Min(pixels, Math.Min(boundsWidth / remoteWidth, boundsHeight / remoteHeight));
            if (!(pixels > 0))
            {
                return default;
            }

            pixels = WholeIfNear(pixels, 1e-6);
        }

        double width = remoteWidth * pixels, height = remoteHeight * pixels;
        double x = fit ? Math.Floor((boundsWidth - width) / 2) : 0;
        double y = fit ? Math.Floor((boundsHeight - height) / 2) : 0;
        return new PictureLayout(new Rect(x / s, y / s, width / s, height / s), pixels);
    }

    /// <summary>Screen pixels per remote pixel at the natural size.</summary>
    public static int NaturalFactor(double scaling) => Math.Max(1, (int)Math.Floor(Usable(scaling) + 0.01));

    /// <summary>A control's size in units when it shows a remote picture at its natural size: what a scroller scrolls.</summary>
    public static Size NaturalSize(double scaling, int remoteWidth, int remoteHeight)
    {
        double s = Usable(scaling);
        int k = NaturalFactor(s);
        return new Size(remoteWidth * k / s, remoteHeight * k / s);
    }

    /// <summary>
    /// The remote size that fills a control of <paramref name="bounds"/> units at the natural size -- what to ask
    /// the host for when the remote display is to follow the window.
    /// </summary>
    public static (int Width, int Height) RemoteSizeFilling(Size bounds, double scaling)
    {
        double s = Usable(scaling);
        int k = NaturalFactor(s);
        return ((int)Math.Floor(WholeIfNear(bounds.Width * s) / k), (int)Math.Floor(WholeIfNear(bounds.Height * s) / k));
    }

    private static double Usable(double scaling) => scaling > 0 && double.IsFinite(scaling) ? scaling : 1;

    /// <summary>
    /// Layout rounding leaves a width of 1920 pixels as 1919.9999…: close enough to whole is whole, or the one
    /// case that matters most -- a picture exactly the size of its window -- would be shrunk by a hair and smoothed.
    /// </summary>
    private static double WholeIfNear(double value, double tolerance = 0.01)
    {
        double whole = Math.Round(value);
        return Math.Abs(value - whole) < tolerance ? whole : value;
    }
}
