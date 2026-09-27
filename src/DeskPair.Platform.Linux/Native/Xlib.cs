using System.Runtime.InteropServices;

namespace DeskPair.Platform.Linux.Native;

/// <summary>
/// The slice of Xlib the capture, cursor and input paths need. Loaded by soname: distributions ship libX11
/// as libX11.so.6 and have for two decades, and the -dev symlink (libX11.so) is not present on a machine
/// that only runs the desktop. The .NET default resolver finds the versioned name here.
/// </summary>
internal static partial class Xlib
{
    private const string Lib = "libX11.so.6";

    /// <summary>An event mask value; the capture path opens the display read-only and asks for nothing.</summary>
    public const nint NoEventMask = 0;

    private static int _threadsafe;

    /// <summary>
    /// Makes Xlib safe to use from more than one thread, which this assembly does: capture, input, cursor and
    /// clipboard each hold their own display connection on their own thread, and the clipboard's own event
    /// loop and reads would otherwise race on one connection. XInitThreads must run before the first
    /// XOpenDisplay in the process, so every component that opens a display calls this first; the flag makes
    /// it happen exactly once.
    /// </summary>
    public static void EnsureThreadSafe()
    {
        if (Interlocked.Exchange(ref _threadsafe, 1) == 0)
        {
            XInitThreads();
            InstallErrorHandler();
        }
    }

    // Kept alive for the process lifetime so Xlib can call back into it.
    private static XErrorHandler? _errorHandler;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XErrorHandler(nint display, nint errorEvent);

    /// <summary>
    /// Replaces Xlib's default error handler, which prints the error and calls <c>exit()</c> — fatal for a
    /// host that must survive a transient protocol error (a stale drawable, a segment recreated under it).
    /// Returning normally tells Xlib to carry on; the capture path already treats a missing frame as a
    /// timeout, so swallowing the error keeps the session alive instead of killing the process.
    /// </summary>
    private static void InstallErrorHandler()
    {
        _errorHandler = static (_, _) => 0;
        XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(_errorHandler));
    }

    [LibraryImport(Lib)]
    public static partial nint XSetErrorHandler(nint handler);

    [LibraryImport(Lib)]
    public static partial int XInitThreads();

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint XOpenDisplay(string? name);

    [LibraryImport(Lib)]
    public static partial int XCloseDisplay(nint display);

    [LibraryImport(Lib)]
    public static partial nint XDefaultRootWindow(nint display);

    [LibraryImport(Lib)]
    public static partial int XDefaultScreen(nint display);

    [LibraryImport(Lib)]
    public static partial nint XRootWindow(nint display, int screen);

    [LibraryImport(Lib)]
    public static partial int XDisplayWidth(nint display, int screen);

    [LibraryImport(Lib)]
    public static partial int XDisplayHeight(nint display, int screen);

    [LibraryImport(Lib)]
    public static partial int XGetWindowAttributes(nint display, nint window, out XWindowAttributes attributes);

    /// <summary>
    /// Where the pointer is and which buttons are held, straight from the server. A second, independent read
    /// beside XFixes: when an injection test fails, two readbacks say whether the injection or the cursor
    /// provider is the one that is wrong.
    /// </summary>
    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool XQueryPointer(
        nint display, nint window, out nint root, out nint child,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);

    [LibraryImport(Lib)]
    public static partial int XFree(nint data);

    [LibraryImport(Lib)]
    public static partial int XSync(nint display, [MarshalAs(UnmanagedType.Bool)] bool discard);

    [LibraryImport(Lib)]
    public static partial int XFlush(nint display);

    [LibraryImport(Lib)]
    public static partial nint XInternAtom(nint display, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

    // ---- windows, properties, selections and events (for the clipboard) ----

    /// <summary>An unmapped 1x1 window that owns the clipboard selection and receives its events.</summary>
    [LibraryImport(Lib)]
    public static partial nint XCreateSimpleWindow(nint display, nint parent, int x, int y, uint width, uint height, uint borderWidth, nuint border, nuint background);

    [LibraryImport(Lib)]
    public static partial int XDestroyWindow(nint display, nint window);

    [LibraryImport(Lib)]
    public static partial int XSelectInput(nint display, nint window, nint eventMask);

    [LibraryImport(Lib)]
    public static partial int XConvertSelection(nint display, nint selection, nint target, nint property, nint requestor, nuint time);

    [LibraryImport(Lib)]
    public static partial int XSetSelectionOwner(nint display, nint selection, nint owner, nuint time);

    [LibraryImport(Lib)]
    public static partial nint XGetSelectionOwner(nint display, nint selection);

    [LibraryImport(Lib)]
    public static partial int XGetWindowProperty(nint display, nint window, nint property, nint offset, nint length, [MarshalAs(UnmanagedType.Bool)] bool delete, nint reqType, out nint actualType, out int actualFormat, out nuint nItems, out nuint bytesAfter, out nint prop);

    [LibraryImport(Lib)]
    public static partial int XChangeProperty(nint display, nint window, nint property, nint type, int format, int mode, byte[] data, int nElements);

    [LibraryImport(Lib)]
    public static partial int XDeleteProperty(nint display, nint window, nint property);

    [LibraryImport(Lib)]
    public static partial int XNextEvent(nint display, byte[] eventReturn);

    [LibraryImport(Lib)]
    public static partial int XPending(nint display);

    [LibraryImport(Lib)]
    public static partial int XSendEvent(nint display, nint window, [MarshalAs(UnmanagedType.Bool)] bool propagate, nint eventMask, byte[] eventSend);

    /// <summary>Reads the file descriptor of the display connection, so a thread can select() on it for events.</summary>
    [LibraryImport(Lib)]
    public static partial int XConnectionNumber(nint display);

    /// <summary>The server's per-request limit, in four-byte units. Zero when BIG-REQUESTS is absent.</summary>
    [LibraryImport(Lib)]
    public static partial nint XExtendedMaxRequestSize(nint display);

    [LibraryImport(Lib)]
    public static partial nint XMaxRequestSize(nint display);

    public const int PropModeReplace = 0;
    public const int PropModeAppend = 2;

    /// <summary>An owner watches for this on the requestor's window to pace an INCR transfer.</summary>
    public const int PropertyNotify = 28;

    /// <summary>XPropertyEvent.state: the requestor has consumed a chunk and wants the next one.</summary>
    public const int PropertyDelete = 1;
    public const nint PropertyChangeMask = 1 << 22;
    public const int SelectionNotify = 31;
    public const int SelectionRequest = 30;
    public const int SelectionClear = 29;

    /// <summary>The prefix of an <c>XImage</c> the capture path reads. The struct is larger; only the head is needed.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct XImage
    {
        public int Width;
        public int Height;
        public int XOffset;
        public int Format;
        public nint Data;
        public int ByteOrder;
        public int BitmapUnit;
        public int BitmapBitOrder;
        public int BitmapPad;
        public int Depth;
        public int BytesPerLine;
        public int BitsPerPixel;
        public nuint RedMask;
        public nuint GreenMask;
        public nuint BlueMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XWindowAttributes
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int BorderWidth;
        public int Depth;
        public nint Visual;
        public nint Root;
        public int Class;
        public int BitGravity;
        public int WinGravity;
        public int BackingStore;
        public nuint BackingPlanes;
        public nuint BackingPixel;
        public int SaveUnder;
        public nint Colormap;
        public int MapInstalled;
        public int MapState;
        public nint AllEventMasks;
        public nint YourEventMask;
        public nint DoNotPropagateMask;
        public int OverrideRedirect;
        public nint Screen;
    }
}
