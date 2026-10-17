using DeskPair.Platform.Windows.Input;

namespace DeskPair.Platform.Windows.Tests;

/// <summary>
/// How a monochrome (AND/XOR mask) cursor becomes a picture a viewer can paint. The text I-beam is made entirely of
/// "invert what is underneath" pixels; painted black they vanish over a dark editor, which is where people type.
/// Pure arithmetic over the mask bits, so it needs no desktop.
/// </summary>
public class MonochromeCursorTests
{
    /// <summary>Builds the mask bitmap as GetDIBits hands it over: 32-bit pixels, AND rows then XOR rows, 0 or 255.</summary>
    private static byte[] Mask(string[] and, string[] xor)
    {
        int width = and[0].Length, height = and.Length;
        byte[] bits = new byte[width * height * 2 * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bits[(y * width + x) * 4] = (byte)(and[y][x] == '1' ? 255 : 0);
                bits[((y + height) * width + x) * 4] = (byte)(xor[y][x] == '1' ? 255 : 0);
            }
        }

        return bits;
    }

    private static (byte B, byte G, byte R, byte A) Pixel(byte[] bgra, int width, int x, int y)
    {
        int o = (y * width + x) * 4;
        return (bgra[o], bgra[o + 1], bgra[o + 2], bgra[o + 3]);
    }

    [Fact]
    public void An_inverting_stroke_is_white_with_a_black_edge_so_it_shows_on_either_background()
    {
        // A one-pixel vertical stroke of inverting pixels (AND=1, XOR=1) in a transparent field: the I-beam's stem.
        string[] and = ["11111", "11111", "11111", "11111", "11111"];
        string[] xor = ["00000", "00100", "00100", "00100", "00000"];
        byte[] bgra = new byte[5 * 5 * 4];

        WindowsCursorProvider.MonochromeToBgra(Mask(and, xor), 5, 5, bgra);

        Pixel(bgra, 5, 2, 2).ShouldBe(((byte)255, (byte)255, (byte)255, (byte)255), "the stroke itself is white");
        Pixel(bgra, 5, 1, 2).ShouldBe(((byte)0, (byte)0, (byte)0, (byte)255), "left of it is a black edge");
        Pixel(bgra, 5, 3, 2).ShouldBe(((byte)0, (byte)0, (byte)0, (byte)255), "right of it is a black edge");
        Pixel(bgra, 5, 2, 0).ShouldBe(((byte)0, (byte)0, (byte)0, (byte)255), "and above and below");
        Pixel(bgra, 5, 2, 4).ShouldBe(((byte)0, (byte)0, (byte)0, (byte)255));
        Pixel(bgra, 5, 0, 0).A.ShouldBe((byte)0, "away from the stroke stays transparent");
        Pixel(bgra, 5, 0, 2).A.ShouldBe((byte)0, "the edge is one pixel, not two");
    }

    [Fact]
    public void Black_white_and_transparent_pixels_are_what_they_say()
    {
        // AND/XOR: 1/0 transparent, 0/0 black, 0/1 white -- the arrow's outline and fill, unchanged.
        string[] and = ["100"];
        string[] xor = ["001"];
        byte[] bgra = new byte[3 * 4];

        WindowsCursorProvider.MonochromeToBgra(Mask(and, xor), 3, 1, bgra);

        Pixel(bgra, 3, 0, 0).A.ShouldBe((byte)0);
        Pixel(bgra, 3, 1, 0).ShouldBe(((byte)0, (byte)0, (byte)0, (byte)255));
        Pixel(bgra, 3, 2, 0).ShouldBe(((byte)255, (byte)255, (byte)255, (byte)255));
    }

    [Fact]
    public void The_edge_never_overwrites_a_drawn_pixel()
    {
        // An inverting pixel beside a black one: the black pixel is the cursor's own, not an edge, and stays as it is;
        // a white pixel beside it stays white too.
        string[] and = ["010"];
        string[] xor = ["011"];
        byte[] bgra = new byte[3 * 4];

        WindowsCursorProvider.MonochromeToBgra(Mask(and, xor), 3, 1, bgra);

        Pixel(bgra, 3, 0, 0).ShouldBe(((byte)0, (byte)0, (byte)0, (byte)255), "cursor black, drawn first");
        Pixel(bgra, 3, 1, 0).ShouldBe(((byte)255, (byte)255, (byte)255, (byte)255), "the inverting pixel, white");
        Pixel(bgra, 3, 2, 0).ShouldBe(((byte)255, (byte)255, (byte)255, (byte)255), "cursor white, untouched");
    }
}
