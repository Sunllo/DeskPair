using DeskPair.Desktop.Localization;
using DeskPair.Protocol.Messages;

namespace DeskPair.Desktop.ViewModels;

/// <summary>
/// The resolution drop-down for one remote display: item 0 is "original", the rest are the modes the host
/// advertised, largest first, as the host ordered them. Pure so the labels and the selected item can be
/// tested without a window.
/// </summary>
internal static class ResolutionChoices
{
    /// <summary>The items to show and which one is the display's current mode (0 when nothing matches).</summary>
    public static (IReadOnlyList<string> Labels, int Selected) Build(DisplayInfo display)
    {
        var labels = new List<string>(display.Modes.Count + 1)
        {
            display.Original is { } original
                ? $"{Strings.Get("session.resolution.original")} ({Label(original)})"
                : Strings.Get("session.resolution.original"),
        };

        int selected = 0;
        for (int i = 0; i < display.Modes.Count; i++)
        {
            Resolution mode = display.Modes[i];
            labels.Add(Label(mode));
            if (selected == 0 && Matches(mode, display))
            {
                selected = i + 1;
            }
        }

        // A display sitting at its original mode is best described as "original", not as one of the list.
        if (display.Original is { } o && Matches(o, display))
        {
            selected = 0;
        }

        return (labels, selected);
    }

    /// <summary>The mode behind item <paramref name="index"/>, or null for "original".</summary>
    public static Resolution? ModeAt(DisplayInfo display, int index) =>
        index >= 1 && index <= display.Modes.Count ? display.Modes[index - 1] : null;

    /// <summary>
    /// Pixels, or points with the scale on a HiDPI mode: a Retina Mac's "3840×2160 (2x)" is what macOS
    /// calls 1920×1080 and what the person expects to pick.
    /// </summary>
    public static string Label(Resolution mode) =>
        mode.Scale > 1.01
            ? $"{Math.Round(mode.Width / mode.Scale)}×{Math.Round(mode.Height / mode.Scale)} ({mode.Scale:0.#}x)"
            : $"{mode.Width}×{mode.Height}";

    public static bool Matches(Resolution mode, DisplayInfo display) =>
        mode.Width == display.Width && mode.Height == display.Height
        && (mode.Scale == 0 || display.Scale == 0 || Math.Abs(mode.Scale - display.Scale) < 0.01);

    public static bool Same(Resolution a, Resolution b) =>
        a.Width == b.Width && a.Height == b.Height && (a.Scale == 0 || b.Scale == 0 || Math.Abs(a.Scale - b.Scale) < 0.01);
}
