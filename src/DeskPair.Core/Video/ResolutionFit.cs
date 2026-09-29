using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Video;

/// <summary>What <see cref="ResolutionFit.Choose"/> decided: leave the display, set a mode, or give it back its original.</summary>
public enum FitAction
{
    Keep,
    Set,
    Restore,
}

/// <summary>A decision and, for <see cref="FitAction.Set"/>, the mode to ask for.</summary>
public readonly record struct FitChoice(FitAction Action, Resolution? Mode)
{
    public static FitChoice Keep => new(FitAction.Keep, null);

    public static FitChoice Restore => new(FitAction.Restore, null);

    public static FitChoice Set(Resolution mode) => new(FitAction.Set, mode);
}

/// <summary>
/// The size to ask a host's display for, so that it matches a viewer's window and is shown there 1:1 -- the way a
/// Windows remote desktop session takes the size of its window. Pure: the desktop app, PeerCli and the phones can
/// share it, and every rule is tested without a host.
///
/// A display that offers any size gets the window's size exactly. One with a list of modes gets the largest that
/// fits in the window whole, preferring the window's shape; it is then shown 1:1 with a margin rather than shrunk.
/// Changing mode is not free -- a physical monitor goes dark while it resynchronises -- so a mode barely larger
/// than the one the display is already in is not worth asking for.
/// </summary>
public static class ResolutionFit
{
    /// <summary>Windows smaller than this in either direction (minimised, or being dragged to nothing) ask for nothing.</summary>
    public const int MinimumWindow = 64;

    /// <summary>Areas within this fraction of each other count as the same size, and the shape decides between them.</summary>
    private const double SameArea = 0.02;

    /// <summary>A fitting mode must be at least this much larger than the current one to be worth a change.</summary>
    private const double WorthwhileGain = 0.03;

    /// <summary>
    /// What to ask for. <paramref name="width"/> x <paramref name="height"/> is the window in the display's own
    /// pixels, what fills it at 1:1. <paramref name="uiScale"/> is the scale at which the host's own interface would
    /// look the size of the viewer's: on a Mac, a mode's pixels come at several scales, and this picks among them.
    /// </summary>
    public static FitChoice Choose(int width, int height, DisplayInfo display, double uiScale = 1)
    {
        if (width < MinimumWindow || height < MinimumWindow)
        {
            return FitChoice.Keep;
        }

        Resolution? wanted = display.AnySize is { } range ? Exactly(width, height, range) : Nearest(width, height, display, uiScale);
        if (wanted is null || IsCurrent(wanted, display))
        {
            return FitChoice.Keep;
        }

        if (display.AnySize is null && Fits(display.Width, display.Height, width, height)
            && Area(wanted) < (long)display.Width * display.Height * (1 + WorthwhileGain))
        {
            return FitChoice.Keep;
        }

        // Back to where it started is the host's "original": it then forgets the change instead of keeping a mode
        // that only happens to equal it.
        return display.Original is { } original && Same(wanted, original) ? FitChoice.Restore : FitChoice.Set(wanted);
    }

    /// <summary>The window's size, within the range and on its step. The step is at least 2: encoders want even sizes.</summary>
    private static Resolution? Exactly(int width, int height, SizeRange range)
    {
        int step = Math.Max(2, range.Step);
        int w = Align(width, range.MinWidth, range.MaxWidth, step);
        int h = Align(height, range.MinHeight, range.MaxHeight, step);
        return w > 0 && h > 0 ? new Resolution { Width = w, Height = h } : null;
    }

    private static int Align(int value, int min, int max, int step)
    {
        int top = max > 0 ? max - max % step : int.MaxValue;
        int bottom = min > 0 ? min + (step - min % step) % step : step;
        if (top < bottom)
        {
            return 0;
        }

        return Math.Clamp(value - value % step, bottom, top);
    }

    /// <summary>
    /// The largest listed mode that fits in the window; of those about as large, the one closest to the window's
    /// shape, then to <paramref name="uiScale"/>. When none fits, the smallest there is: it is shrunk the least.
    /// </summary>
    private static Resolution? Nearest(int width, int height, DisplayInfo display, double uiScale)
    {
        List<Resolution> fitting = [.. display.Modes.Where(m => m.Width > 0 && m.Height > 0 && Fits(m.Width, m.Height, width, height))];
        if (fitting.Count == 0)
        {
            return display.Modes.Where(m => m.Width > 0 && m.Height > 0).MinBy(Area);
        }

        long largest = fitting.Max(Area);
        double shape = (double)width / height;
        return fitting
            .Where(m => Area(m) >= largest * (1 - SameArea))
            .OrderBy(m => Math.Round(Math.Abs(Math.Log((double)m.Width / m.Height / shape)), 3))
            .ThenBy(m => Math.Abs(ScaleOf(m) - uiScale))
            .ThenByDescending(ScaleOf) // a tie between 1x and 2x on a 150 % screen: the larger interface is the readable one
            .ThenByDescending(Area)
            .First();
    }

    private static bool Fits(int w, int h, int width, int height) => w <= width && h <= height;

    private static long Area(Resolution m) => (long)m.Width * m.Height;

    private static double ScaleOf(Resolution m) => m.Scale > 0 ? m.Scale : 1;

    private static bool IsCurrent(Resolution mode, DisplayInfo display) =>
        mode.Width == display.Width && mode.Height == display.Height
        && (mode.Scale == 0 || display.Scale == 0 || Math.Abs(mode.Scale - display.Scale) < 0.01);

    private static bool Same(Resolution a, Resolution b) =>
        a.Width == b.Width && a.Height == b.Height && (a.Scale == 0 || b.Scale == 0 || Math.Abs(a.Scale - b.Scale) < 0.01);
}
