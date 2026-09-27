namespace DeskPair.Core.Terminal;

[Flags]
public enum CellAttributes : ushort
{
    None = 0,
    Bold = 1,
    Faint = 2,
    Italic = 4,
    Underline = 8,
    Blink = 16,
    Inverse = 32,
    Invisible = 64,
    Strike = 128,
}

/// <summary>
/// How one cell is drawn. A colour is a single number: <see cref="Default"/> (0), a palette entry
/// (<see cref="Palette"/>, 1..256), or a 24-bit value tagged with <see cref="RgbTag"/>. One number instead
/// of a small class keeps a row a flat array the control can copy without allocating.
/// </summary>
public readonly record struct CellColors(uint Foreground, uint Background, CellAttributes Attributes)
{
    public const uint Default = 0;
    public const uint RgbTag = 0x0100_0000;

    public static readonly CellColors Plain = new(Default, Default, CellAttributes.None);

    public static uint Palette(int index) => (uint)(Math.Clamp(index, 0, 255) + 1);

    public static uint Rgb(int r, int g, int b) => RgbTag | ((uint)(r & 0xFF) << 16) | ((uint)(g & 0xFF) << 8) | (uint)(b & 0xFF);

    public static bool TryPalette(uint color, out int index)
    {
        index = (int)color - 1;
        return color is >= 1 and <= 256;
    }

    public static bool TryRgb(uint color, out byte r, out byte g, out byte b)
    {
        r = (byte)(color >> 16);
        g = (byte)(color >> 8);
        b = (byte)color;
        return (color & RgbTag) != 0;
    }

    /// <summary>What an erased cell keeps: the background only, as xterm does.</summary>
    public CellColors Erased => new(Default, Background, CellAttributes.None);
}

/// <summary>
/// One character position. A wide character occupies two: the first has <see cref="Width"/> 2, the
/// second is a spacer with width 0 that draws nothing.
/// </summary>
public readonly record struct TerminalCell(int Rune, CellColors Colors, byte Width)
{
    public static TerminalCell Space(CellColors colors) => new(' ', colors.Erased, 1);

    public bool IsSpacer => Width == 0;

    public string Text => Width == 0 ? string.Empty : char.ConvertFromUtf32(Rune == 0 ? ' ' : Rune);
}
