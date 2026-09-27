using System.Runtime.InteropServices;

namespace DeskPair.Platform.Linux.Native;

/// <summary>
/// The MIT-SHM extension, which is what makes X11 capture fast enough to be a remote desktop: the server
/// writes the screen straight into a shared-memory segment both processes map, so <c>XShmGetImage</c> is a
/// GPU-to-RAM copy with no round trip over the socket. Without it, reading a 1080p frame means marshalling
/// eight megabytes through the X protocol every time, which is unusable.
///
/// The shared segment itself is System V IPC, not X, so the sibling <see cref="LibC"/> supplies shmget/shmat.
/// </summary>
internal static partial class XShm
{
    private const string Lib = "libXext.so.6";

    /// <summary>ZPixmap: pixels packed as the server's native format, which on a modern display is 32-bit BGRA.</summary>
    public const int ZPixmap = 2;

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool XShmQueryExtension(nint display);

    /// <summary>
    /// Allocates an <c>XImage</c> backed by a shared segment. The returned image keeps the <em>address</em> of
    /// the <c>XShmSegmentInfo</c> in its <c>obdata</c> field, and <see cref="XShmGetImage"/> reads the server
    /// segment id back out of it, so that struct must live at a stable address for the image's whole life —
    /// hence <paramref name="shmInfo"/> is a raw pointer to pinned unmanaged memory, not a by-ref copy that the
    /// marshaller frees when the call returns.
    /// </summary>
    [LibraryImport(Lib)]
    public static partial nint XShmCreateImage(nint display, nint visual, uint depth, int format, nint data, nint shmInfo, uint width, uint height);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool XShmAttach(nint display, nint shmInfo);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool XShmDetach(nint display, nint shmInfo);

    /// <summary>Copies the current contents of a drawable (the root window) into the shared image.</summary>
    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool XShmGetImage(nint display, nint drawable, nint image, int x, int y, ulong planeMask);

    [StructLayout(LayoutKind.Sequential)]
    public struct XShmSegmentInfo
    {
        /// <summary>The server's handle to the segment; the server fills this in on attach.</summary>
        public nuint Shmseg;

        /// <summary>The System V shared-memory id from shmget.</summary>
        public int Shmid;

        /// <summary>The address the segment is mapped at in this process (shmat's return).</summary>
        public nint Shmaddr;

        /// <summary>
        /// Non-zero when the server may only read the segment. X's <c>Bool</c> is a C <c>int</c>, and the
        /// struct is passed by ref through source-generated marshalling, which needs every field blittable,
        /// so it is an int here rather than a marshalled bool.
        /// </summary>
        public int ReadOnly;
    }
}

/// <summary>System V shared memory, for the segment MIT-SHM hands to the X server.</summary>
internal static partial class LibC
{
    private const string Lib = "libc.so.6";

    /// <summary>IPC_CREAT | 0600: create a new segment readable and writable by this user only.</summary>
    public const int CreateOwnerReadWrite = 0x200 | 0x180;

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int shmget(int key, nuint size, int flags);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint shmat(int shmid, nint addr, int flags);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int shmdt(nint addr);

    /// <summary>cmd 0 is IPC_RMID: mark the segment for removal once nobody has it mapped.</summary>
    [LibraryImport(Lib, SetLastError = true)]
    public static partial int shmctl(int shmid, int cmd, nint buf);

    /// <summary>The effective user id; 0 means root, which the system service runs as.</summary>
    [LibraryImport(Lib)]
    public static partial uint geteuid();
}
