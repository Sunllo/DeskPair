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
                        // Top half = AND mask, bottom half = XOR mask. Transparent where AND=1 & XOR=0,
                        // black where AND=0 & XOR=0, white where AND=0 & XOR=1, inverted (drawn as black outline) where AND=1 & XOR=1.
                        for (int y = 0; y < height; y++)
                        {
                            for (int x = 0; x < width; x++)
                            {
                                int and = maskBits[(y * width + x) * 4];
                                int xor = maskBits[((y + height) * width + x) * 4];
                                int o = (y * width + x) * 4;
                                if (and != 0 && xor == 0)
                                {
                                    continue; // transparent
                                }

                                byte v = (byte)(xor != 0 ? 255 : 0);
                                if (and != 0 && xor != 0)
                                {
                                    v = 0;
                                }

                                bgra[o] = v;
                                bgra[o + 1] = v;
                                bgra[o + 2] = v;
                                bgra[o + 3] = 255;
                            }
                        }
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
