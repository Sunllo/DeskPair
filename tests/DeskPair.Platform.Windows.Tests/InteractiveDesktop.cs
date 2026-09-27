using System.Runtime.InteropServices;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Tests;

/// <summary>Tests that need the interactive desktop (clipboard, input, capture) skip themselves when it is not reachable.</summary>
internal static class InteractiveDesktop
{
    public static bool IsAvailable { get; } = Probe();

    /// <summary>
    /// A window station is not enough for the tests that capture: a machine can have one and still refuse to
    /// give up its pixels, which is what a locked screen, the secure desktop and a disconnected session all
    /// do. Those tests ask this instead, so a locked machine skips them rather than failing them.
    /// </summary>
    public static bool IsReadable { get; } = IsAvailable && CanRead();

    private static bool Probe()
    {
        if (User32.OpenClipboard(0) == 0)
        {
            return Marshal.GetLastPInvokeError() != 5; // ERROR_ACCESS_DENIED: no interactive window station
        }

        User32.CloseClipboard();
        return true;
    }

    /// <summary>
    /// One pixel off the screen. BitBlt is what the GDI capturer uses and what fails on a desktop this
    /// process may not read, so asking it is asking the same question the capturer will ask.
    /// </summary>
    private static bool CanRead()
    {
        nint screen = Gdi32.CreateDC("DISPLAY", null, null, 0);
        if (screen == 0)
        {
            return false;
        }

        nint memory = 0;
        nint bitmap = 0;
        try
        {
            memory = Gdi32.CreateCompatibleDC(screen);
            bitmap = Gdi32.CreateCompatibleBitmap(screen, 1, 1);
            if (memory == 0 || bitmap == 0)
            {
                return false;
            }

            nint previous = Gdi32.SelectObject(memory, bitmap);
            bool ok = Gdi32.BitBlt(memory, 0, 0, 1, 1, screen, 0, 0, Gdi32.SRCCOPY) != 0;
            Gdi32.SelectObject(memory, previous);
            return ok;
        }
        finally
        {
            if (bitmap != 0)
            {
                Gdi32.DeleteObject(bitmap);
            }

            if (memory != 0)
            {
                Gdi32.DeleteDC(memory);
            }

            Gdi32.DeleteDC(screen);
        }
    }
}
