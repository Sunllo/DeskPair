using System.Runtime.InteropServices;

namespace DeskPair.Platform.Linux.Native;

/// <summary>
/// The XTest extension: synthesise input as if it came from a real device. This is the X11 equivalent of
/// SendInput — the events go through the server's normal input path, so the focused window cannot tell them
/// from the physical mouse and keyboard. On a Wayland session XTest reaches only Xwayland clients, which is
/// why the Wayland input path uses uinput instead.
/// </summary>
internal static partial class XTest
{
    private const string Lib = "libXtst.so.6";

    /// <summary>Whether the server has XTest at all. The injector's constructor otherwise only finds out by throwing.</summary>
    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool XTestQueryExtension(nint display, out int eventBase, out int errorBase, out int majorVersion, out int minorVersion);

    /// <summary>Moves the pointer to an absolute position on the given screen (-1 = the default screen).</summary>
    [LibraryImport(Lib)]
    public static partial int XTestFakeMotionEvent(nint display, int screen, int x, int y, ulong delay);

    /// <summary>Moves by a delta rather than to a point; screen -1 means "the one the pointer is on".</summary>
    [LibraryImport(Lib)]
    public static partial int XTestFakeRelativeMotionEvent(nint display, int dx, int dy, ulong delay);

    /// <summary>Presses (isPress=true) or releases a mouse button. Buttons: 1 left, 2 middle, 3 right, 4/5 wheel.</summary>
    [LibraryImport(Lib)]
    public static partial int XTestFakeButtonEvent(nint display, uint button, [MarshalAs(UnmanagedType.Bool)] bool isPress, ulong delay);

    /// <summary>Presses or releases a key by X keycode (not keysym — the caller maps first).</summary>
    [LibraryImport(Lib)]
    public static partial int XTestFakeKeyEvent(nint display, uint keycode, [MarshalAs(UnmanagedType.Bool)] bool isPress, ulong delay);

    // ---- keysym / keycode mapping, from Xlib proper ----

    private const string X11 = "libX11.so.6";

    /// <summary>The X keycode that currently produces a keysym, or 0. Layout-dependent, so it is read live.</summary>
    [LibraryImport(X11)]
    public static partial byte XKeysymToKeycode(nint display, nuint keysym);

    /// <summary>The keysym for a Latin-1 or Unicode character name; the input path turns typed text into these.</summary>
    [LibraryImport(X11, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nuint XStringToKeysym(string name);

    /// <summary>Remaps a spare keycode to an arbitrary keysym, so characters with no key on the layout can still be typed.</summary>
    [LibraryImport(X11)]
    public static partial int XChangeKeyboardMapping(nint display, int firstKeycode, int keysymsPerKeycode, nuint[] keysyms, int numCodes);

    [LibraryImport(X11)]
    public static partial nint XGetKeyboardMapping(nint display, byte firstKeycode, int keycodeCount, out int keysymsPerKeycode);

    [LibraryImport(X11)]
    public static partial void XDisplayKeycodes(nint display, out int minKeycode, out int maxKeycode);
}
