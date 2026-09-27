using System.Runtime.InteropServices;

namespace DeskPair.Platform.Linux.Native;

/// <summary>
/// XFixes, for the cursor. X does not composite the pointer into the framebuffer the capture path reads, so
/// the cursor has to be fetched and sent separately — the same split the Windows path makes. XFixes returns
/// the current shape, its hotspot, and a serial that changes when the shape changes, which is exactly the
/// "cursor id" the cursor contract wants.
/// </summary>
internal static partial class XFixes
{
    private const string Lib = "libXfixes.so.3";

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool XFixesQueryExtension(nint display, out int eventBase, out int errorBase);

    /// <summary>
    /// Returns a malloc'd <c>XFixesCursorImage</c> the caller must <c>XFree</c>. The pixels follow the struct
    /// as an array of <c>unsigned long</c> (one per pixel, ARGB in the low 32 bits, even on 64-bit).
    /// </summary>
    [LibraryImport(Lib)]
    public static partial nint XFixesGetCursorImage(nint display);

    /// <summary>
    /// Asks to be told when a selection changes owner or content. This is how clipboard change detection
    /// works without polling: the server sends an XFixesSelectionNotify event (at <c>eventBase</c> + 0)
    /// whenever the CLIPBOARD selection changes, so a paste from any application is noticed at once.
    /// </summary>
    [LibraryImport(Lib)]
    public static partial void XFixesSelectSelectionInput(nint display, nint window, nint selection, ulong eventMask);

    /// <summary>SetSelectionOwner | SelectionWindowDestroy | SelectionClientClose.</summary>
    public const ulong SelectionEventMask = 1 | 2 | 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct XFixesCursorImage
    {
        public short X;
        public short Y;
        public ushort Width;
        public ushort Height;
        public ushort XHot;
        public ushort YHot;
        public nuint CursorSerial;

        /// <summary>Points at the pixel array (unsigned long per pixel) that follows the struct in memory.</summary>
        public nint Pixels;
    }
}
