using System.Runtime.InteropServices;
using DeskPair.Platform.Abstractions.Cursor;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Input;

/// <summary>Reads the current cursor handle, position and bitmap (color or monochrome mask cursors).</summary>
public sealed class WindowsCursorProvider : ICursorProvider
{
    private readonly Dictionary<ulong, CursorImage?> _cache = new();

    public ulong GetCurrentCursorId()
    {
        var info = new User32.CURSORINFO { cbSize = (uint)Marshal.SizeOf<User32.CURSORINFO>() };
        if (User32.GetCursorInfo(ref info) == 0 || (info.flags & User32.CURSOR_SHOWING) == 0)
        {
            return 0; // hidden
        }

        return (ulong)info.hCursor;
    }

    public (int X, int Y)? GetCursorPosition() => User32.GetCursorPos(out User32.POINT p) != 0 ? (p.X, p.Y) : null;

    public CursorImage? GetCursorImage(ulong id)
    {
        if (id == 0)
        {
            return null;
        }

        lock (_cache)
        {
            if (_cache.TryGetValue(id, out CursorImage? cached))
            {
                return cached;
            }
        }

        CursorImage? image = Extract((nint)id);
        lock (_cache)
        {
            if (_cache.Count > 64)
            {
                _cache.Clear();
            }

            _cache[id] = image;
        }

        return image;
    }

    private static unsafe CursorImage? Extract(nint hCursor)
    {
        nint icon = User32.CopyIcon(hCursor);
        if (icon == 0)
        {
            return null;
        }

        try
        {
            if (User32.GetIconInfo(icon, out User32.ICONINFO ii) == 0)
            {
                return null;
            }

            try
            {
                nint dc = Gdi32.GetDC(0);
                try
                {
                    Gdi32.BITMAP mask = default;
                    Gdi32.GetObject(ii.hbmMask, sizeof(Gdi32.BITMAP), &mask);
                    bool monochrome = ii.hbmColor == 0;
                    int width = mask.bmWidth;
                    int height = monochrome ? mask.bmHeight / 2 : mask.bmHeight;
                    if (width <= 0 || height <= 0 || width > 256 || height > 256)
                    {
                        return null;
                    }

                    byte[] maskBits = ReadBits(dc, ii.hbmMask, width, mask.bmHeight);
                    byte[] bgra = new byte[width * height * 4];
                    if (monochrome)
                    {
                        MonochromeToBgra(maskBits, width, height, bgra);
                    }
                    else
                    {
                        byte[] color = ReadBits(dc, ii.hbmColor, width, height);
                        bool anyAlpha = false;
                        for (int i = 3; i < color.Length; i += 4)
                        {
                            if (color[i] != 0)
                            {
                                anyAlpha = true;
                                break;
                            }
                        }

                        for (int i = 0; i < width * height; i++)
                        {
                            int o = i * 4;
                            bgra[o] = color[o];
                            bgra[o + 1] = color[o + 1];
                            bgra[o + 2] = color[o + 2];
                            // 32-bit cursors carry alpha; 24-bit ones use the AND mask for transparency.
                            bgra[o + 3] = anyAlpha ? color[o + 3] : (byte)(maskBits[o] != 0 ? 0 : 255);
                        }
                    }

                    return new CursorImage((ulong)hCursor, (int)ii.xHotspot, (int)ii.yHotspot, width, height, bgra);
                }
                finally
                {
                    Gdi32.ReleaseDC(0, dc);
                }
            }
            finally
            {
                if (ii.hbmColor != 0)
                {
                    Gdi32.DeleteObject(ii.hbmColor);
                }

                if (ii.hbmMask != 0)
                {
                    Gdi32.DeleteObject(ii.hbmMask);
                }
            }
        }
        finally
        {
            User32.DestroyIcon(icon);
        }
    }

    /// <summary>
    /// A monochrome cursor as BGRA. <paramref name="maskBits"/> is the cursor's mask bitmap read as 32-bit pixels, the
    /// AND plane on top of the XOR plane (so twice <paramref name="height"/> rows). Transparent where AND=1 and XOR=0,
    /// black where both are 0, white where AND=0 and XOR=1.
    ///
    /// Where both are 1 Windows inverts whatever is under the pixel -- the text I-beam is drawn entirely that way, so
    /// it reads black over a page and white over a dark editor. A viewer paints a bitmap and cannot invert, and this
    /// used to paint those pixels black: right over a light background and invisible over a dark one, which is where
    /// most people type. They are white now, with a one-pixel black shadow to their right and below, into the transparent
    /// pixels: the white shows over a dark background, the shadow over a light one. (0.5.2 drew a black edge on all four
    /// sides; around the I-beam's one-pixel strokes that made two black pixels for every white one, and the cursor still
    /// read as black. A shadow keeps the stroke two pixels wide, half of each colour.)
    /// </summary>
    internal static void MonochromeToBgra(byte[] maskBits, int width, int height, byte[] bgra)
    {
        bool[] inverted = new bool[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                int and = maskBits[i * 4];
                int xor = maskBits[(i + width * height) * 4];
                if (and != 0 && xor == 0)
                {
                    continue; // transparent
                }

                inverted[i] = and != 0 && xor != 0;
                byte v = (byte)(xor != 0 ? 255 : 0);
                int o = i * 4;
                bgra[o] = v;
                bgra[o + 1] = v;
                bgra[o + 2] = v;
                bgra[o + 3] = 255;
            }
        }

        // The shadow: a transparent pixel with an inverted one to its left, above it, or diagonally above-left turns black.
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                if (bgra[i * 4 + 3] != 0)
                {
                    continue; // already drawn
                }

                bool shadow = (x > 0 && inverted[i - 1]) || (y > 0 && inverted[i - width]) || (x > 0 && y > 0 && inverted[i - width - 1]);
                if (shadow)
                {
                    bgra[i * 4] = 0;
                    bgra[i * 4 + 1] = 0;
                    bgra[i * 4 + 2] = 0;
                    bgra[i * 4 + 3] = 255;
                }
            }
        }
    }

    private static unsafe byte[] ReadBits(nint dc, nint bitmap, int width, int height)
    {
        byte[] bits = new byte[width * height * 4];
        var info = new Gdi32.BITMAPINFO
        {
            bmiHeader = new Gdi32.BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(Gdi32.BITMAPINFOHEADER),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = Gdi32.BI_RGB,
            },
        };
        fixed (byte* p = bits)
        {
            Gdi32.GetDIBits(dc, bitmap, 0, (uint)height, p, ref info, Gdi32.DIB_RGB_COLORS);
        }

        return bits;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
