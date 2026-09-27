using System.Runtime.InteropServices;

namespace DeskPair.Platform.Linux.Native;

/// <summary>Opening device nodes: the other half of <see cref="LibC"/>, which XShm.cs began.</summary>
internal static partial class LibC
{
    public const int O_RDWR = 2;
    public const int O_CLOEXEC = 0x80000;
    public const int O_NONBLOCK = 0x800;
    public const int O_WRONLY = 1;

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int open(string path, int flags);

    /// <summary>
    /// The inode of an open descriptor, or 0 when it cannot be read. For anonymous files such as a dma-buf
    /// this is the one number that names the underlying object: two descriptors on the same buffer share
    /// it, two buffers never do, and it is not recycled while a descriptor keeps the buffer alive.
    /// </summary>
    public static unsafe ulong InodeOf(int fd)
    {
        // struct stat is 144 bytes on x86-64 and aarch64 glibc, st_ino at offset 8 on both; 256 leaves room.
        byte* buffer = stackalloc byte[256];
        int rc;
        try
        {
            rc = fstat(fd, buffer);
        }
        catch (EntryPointNotFoundException)
        {
            rc = __fxstat(1, fd, buffer); // glibc before 2.33
        }

        return rc == 0 ? *(ulong*)(buffer + 8) : 0;
    }

    [LibraryImport(Lib, SetLastError = true)]
    private static unsafe partial int fstat(int fd, byte* statbuf);

    [LibraryImport(Lib, SetLastError = true)]
    private static unsafe partial int __fxstat(int version, int fd, byte* statbuf);
}
