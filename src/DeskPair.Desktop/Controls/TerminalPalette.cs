using Avalonia.Media;
using DeskPair.Core.Terminal;

namespace DeskPair.Desktop.Controls;

/// <summary>
/// Cell colours as the eye sees them: the sixteen named colours, xterm's 6x6x6 cube and grey ramp for
/// the rest of the 256, true colour as given, and the attributes that change colour rather than shape --
/// inverse, faint, invisible, and bold drawing the first eight in their bright form, as xterm does.
/// </summary>
public static class TerminalPalette
{
    public static readonly Color Background = Color.FromRgb(0x14, 0x17, 0x1C);
    public static readonly Color Foreground = Color.FromRgb(0xD8, 0xDE, 0xE6);
    public static readonly Color Cursor = Color.FromRgb(0x7F, 0xC8, 0xF8);
    public static readonly Color Selection = Color.FromRgb(0x2F, 0x4E, 0x6E);

    /// <summary>A dark theme's sixteen: readable on the background, and red still reads as red.</summary>
    private static readonly Color[] Named =
    [
        Color.FromRgb(0x1C, 0x1F, 0x24), Color.FromRgb(0xE0, 0x6C, 0x75), Color.FromRgb(0x98, 0xC3, 0x79), Color.FromRgb(0xE5, 0xC0, 0x7B),
        Color.FromRgb(0x61, 0xAF, 0xEF), Color.FromRgb(0xC6, 0x78, 0xDD), Color.FromRgb(0x56, 0xB6, 0xC2), Color.FromRgb(0xAB, 0xB2, 0xBF),
        Color.FromRgb(0x5C, 0x63, 0x70), Color.FromRgb(0xFF, 0x7A, 0x85), Color.FromRgb(0xB5, 0xE0, 0x90), Color.FromRgb(0xFF, 0xD8, 0x8F),
        Color.FromRgb(0x82, 0xC4, 0xFF), Color.FromRgb(0xDD, 0x92, 0xF2), Color.FromRgb(0x76, 0xD4, 0xE0), Color.FromRgb(0xF0, 0xF3, 0xF6),
    ];

    /// <summary>A palette entry, 0..255.</summary>
    public static Color Indexed(int index)
    {
        if (index < 16)
        {
            return Named[Math.Max(0, index)];
        }

        if (index < 232)
        {
            int i = index - 16;
            return Color.FromRgb(Level(i / 36), Level(i / 6 % 6), Level(i % 6));
        }

        byte grey = (byte)(8 + ((Math.Min(index, 255) - 232) * 10));
        return Color.FromRgb(grey, grey, grey);

        static byte Level(int n) => (byte)(n == 0 ? 0 : 55 + (n * 40));
    }

    /// <summary>The foreground and background a cell is drawn with, attributes applied.</summary>
    public static (Color Foreground, Color Background) Resolve(CellColors colors)
    {
        bool bold = colors.Attributes.HasFlag(CellAttributes.Bold);
        Color fg = Pick(colors.Foreground, Foreground, bold);
        Color bg = Pick(colors.Background, Background, bright: false);
        if (colors.Attributes.HasFlag(CellAttributes.Inverse))
        {
            (fg, bg) = (bg, fg);
        }

        if (colors.Attributes.HasFlag(CellAttributes.Faint))
        {
            fg = Blend(fg, bg, 0.5);
        }

        if (colors.Attributes.HasFlag(CellAttributes.Invisible))
        {
            fg = bg;
        }

        return (fg, bg);
    }

    private static Color Pick(uint color, Color fallback, bool bright)
    {
        if (CellColors.TryPalette(color, out int index))
        {
            return Indexed(bright && index < 8 ? index + 8 : index);
        }

        return CellColors.TryRgb(color, out byte r, out byte g, out byte b) ? Color.FromRgb(r, g, b) : fallback;
    }

    public static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round((a.R * (1 - t)) + (b.R * t)),
        (byte)Math.Round((a.G * (1 - t)) + (b.G * t)),
        (byte)Math.Round((a.B * (1 - t)) + (b.B * t)));
}
