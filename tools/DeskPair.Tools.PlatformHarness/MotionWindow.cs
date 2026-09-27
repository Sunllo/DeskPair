using System.Runtime.InteropServices;

namespace DeskPair.Tools.PlatformHarness;

/// <summary>
/// A small window that moves itself in a circle, so a capture measurement has something to capture. An idle
/// desktop presents nothing, and Desktop Duplication then returns nothing but timeouts — which is a true
/// result about the desktop but tells us nothing about what a frame costs. Driving known motion makes the
/// capture path measurable on a machine nobody is sitting at.
/// </summary>
internal static partial class MotionWindow
{
    private const uint WsPopup = 0x80000000, WsVisible = 0x10000000;
    private const uint WsExToolWindow = 0x80, WsExTopmost = 0x8, WsExNoActivate = 0x8000000;
    private const uint SwpNoActivate = 0x10, SwpNoZOrder = 0x4, SwpNoSize = 0x1;
    private const uint PmRemove = 0x1;

    /// <summary>Runs until <paramref name="stop"/> is set. Must own the thread: this pumps messages.</summary>
    public static void RunUntil(CancellationToken stop, int centreX, int centreY, int radius)
    {
        nint window = CreateWindowExW(
            WsExToolWindow | WsExTopmost | WsExNoActivate,
            "STATIC",
            "sunllo-motion",
            WsPopup | WsVisible,
            centreX,
            centreY,
            240,
            160,
            0,
            0,
            0,
            0);
        if (window == 0)
        {
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()}).");
        }

        try
        {
            double angle = 0;
            while (!stop.IsCancellationRequested)
            {
                angle += 0.12;
                SetWindowPos(
                    window,
                    0,
                    centreX + (int)(Math.Cos(angle) * radius),
                    centreY + (int)(Math.Sin(angle) * radius),
                    0,
                    0,
                    SwpNoActivate | SwpNoZOrder | SwpNoSize);
                while (PeekMessageW(out Msg msg, 0, 0, 0, PmRemove))
                {
                    TranslateMessage(in msg);
                    DispatchMessageW(in msg);
                }

                Thread.Sleep(8); // ~120 Hz of movement, so the desktop presents faster than we can capture
            }
        }
        finally
        {
            DestroyWindow(window);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out Msg msg, nint window, uint filterMin, uint filterMax, uint remove);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in Msg msg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(in Msg msg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);
}
