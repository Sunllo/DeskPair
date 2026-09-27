using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Cursor;
using DeskPair.Platform.Linux.Native;

namespace DeskPair.Platform.Linux.Input;

/// <summary>
/// The pointer, through XFixes. X keeps the cursor out of the framebuffer, so it is fetched separately and
/// sent as its own shape; the serial XFixes reports is the stable id the viewer caches by, changing only
/// when the shape changes. Pixels come back as one <c>unsigned long</c> per pixel in ARGB, which becomes the
/// premultiplied BGRA the cursor contract asks for.
/// </summary>
public sealed class X11CursorProvider : ICursorProvider
{
    private readonly nint _dpy;
    private readonly bool _available;
    private bool _disposed;

    public X11CursorProvider(ILogger log)
    {
        Xlib.EnsureThreadSafe();
        _dpy = Xlib.XOpenDisplay(null);
        _available = _dpy != 0 && XFixes.XFixesQueryExtension(_dpy, out _, out _);
        if (!_available)
        {
            log.LogWarning("XFixes is unavailable; the remote cursor will not be shown");
        }
    }

    public ulong GetCurrentCursorId()
    {
        if (!_available)
        {
            return 0;
        }

        nint ptr = XFixes.XFixesGetCursorImage(_dpy);
        if (ptr == 0)
        {
            return 0;
        }

        try
        {
            return (ulong)Marshal.PtrToStructure<XFixes.XFixesCursorImage>(ptr).CursorSerial;
        }
        finally
        {
            Xlib.XFree(ptr);
        }
    }

    public CursorImage? GetCursorImage(ulong id)
    {
        if (!_available)
        {
            return null;
        }

        nint ptr = XFixes.XFixesGetCursorImage(_dpy);
        if (ptr == 0)
        {
            return null;
        }

        try
        {
            var image = Marshal.PtrToStructure<XFixes.XFixesCursorImage>(ptr);
            int width = image.Width;
            int height = image.Height;
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            byte[] bgra = new byte[width * height * 4];
            unsafe
            {
                // One unsigned long per pixel: 8 bytes on this platform, ARGB in the low 32 bits, already
                // premultiplied. Repack into BGRA byte order.
                var src = (nuint*)image.Pixels;
                for (int i = 0; i < width * height; i++)
                {
                    uint argb = (uint)src[i];
                    int o = i * 4;
                    bgra[o] = (byte)(argb & 0xFF);         // B
                    bgra[o + 1] = (byte)((argb >> 8) & 0xFF);  // G
                    bgra[o + 2] = (byte)((argb >> 16) & 0xFF); // R
                    bgra[o + 3] = (byte)((argb >> 24) & 0xFF); // A
                }
            }

            return new CursorImage((ulong)image.CursorSerial, image.XHot, image.YHot, width, height, bgra);
        }
        finally
        {
            Xlib.XFree(ptr);
        }
    }

    public (int X, int Y)? GetCursorPosition()
    {
        if (!_available)
        {
            return null;
        }

        nint ptr = XFixes.XFixesGetCursorImage(_dpy);
        if (ptr == 0)
        {
            return null;
        }

        try
        {
            var image = Marshal.PtrToStructure<XFixes.XFixesCursorImage>(ptr);
            return (image.X, image.Y);
        }
        finally
        {
            Xlib.XFree(ptr);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_dpy != 0)
            {
                Xlib.XCloseDisplay(_dpy);
            }
        }

        return ValueTask.CompletedTask;
    }
}
