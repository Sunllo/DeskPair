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
        // statx has one layout on every architecture -- 256 bytes, stx_ino a u64 at 32 -- and glibc has had it since
        // 2.28. It can still be refused (a seccomp filter that predates it), so the fstat family stays as the fallback.
        byte* buffer = stackalloc byte[256];
        try
        {
            if (statx(fd, "", AtEmptyPath, StatxIno, buffer) == 0)
            {
                return *(ulong*)(buffer + 32);
            }
        }
        catch (EntryPointNotFoundException)
        {
            // glibc before 2.28
        }

        // struct stat differs by architecture: st_ino is a u64 at 8 on x86-64 and aarch64, while 32-bit ARM's struct
        // stat holds only its low half and struct stat64 keeps the whole of it at 96.
        bool arm32 = RuntimeInformation.ProcessArchitecture == Architecture.Arm;
        int rc;
        try
        {
            rc = arm32 ? fstat64(fd, buffer) : fstat(fd, buffer);
        }
        catch (EntryPointNotFoundException)
        {
            // glibc before 2.33 exports these only as __fxstat/__fxstat64, whose version number is per architecture.
            rc = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => __fxstat(1, fd, buffer),
                Architecture.Arm64 => __fxstat(0, fd, buffer),
                Architecture.Arm => __fxstat64(3, fd, buffer),
                _ => -1,
            };
        }

        return rc == 0 ? *(ulong*)(buffer + (arm32 ? 96 : 8)) : 0;
    }

    private const int AtEmptyPath = 0x1000;
    private const uint StatxIno = 0x100;

    [LibraryImport(Lib, SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int statx(int dirfd, string path, int flags, uint mask, byte* statxbuf);

    [LibraryImport(Lib, SetLastError = true)]
    private static unsafe partial int fstat(int fd, byte* statbuf);

    [LibraryImport(Lib, SetLastError = true)]
    private static unsafe partial int fstat64(int fd, byte* statbuf);

    [LibraryImport(Lib, SetLastError = true)]
    private static unsafe partial int __fxstat(int version, int fd, byte* statbuf);

    [LibraryImport(Lib, SetLastError = true)]
    private static unsafe partial int __fxstat64(int version, int fd, byte* statbuf);
}
