using System.Runtime.InteropServices;

namespace DeskPair.Platform.Linux.Native;

/// <summary>Mapping a dma-buf into this process, and telling the kernel when it is being read.</summary>
internal static unsafe partial class Mman
{
    private const string Lib = "libc";

    public const int ProtRead = 1;
    public const int MapShared = 1;
    public static readonly void* MapFailed = (void*)-1;

    /// <summary><c>_IOW('b', 0, struct dma_buf_sync { u64 flags })</c>.</summary>
    public const uint DmaBufIoctlSync = 0x40086200;
    public const ulong DmaBufSyncRead = 1;
    public const ulong DmaBufSyncStart = 0;
    public const ulong DmaBufSyncEnd = 4;

    [LibraryImport(Lib, SetLastError = true)]
    public static partial void* mmap(void* address, nuint length, int protection, int flags, int fd, nint offset); // off_t is a C long

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int munmap(void* address, nuint length);

    [LibraryImport(Lib, SetLastError = true)]
    private static partial int ioctl(int fd, nuint request, ref ulong argument);

    /// <summary>
    /// Brackets a CPU read of a dma-buf. On coherent hardware it is a no-op; on the rest it is the
    /// difference between the frame and a stale cache of it. Failure is ignored: some exporters do not
    /// implement it, and a read still works there.
    /// </summary>
    public static void SyncReadStart(int fd)
    {
        ulong flags = DmaBufSyncStart | DmaBufSyncRead;
        _ = ioctl(fd, DmaBufIoctlSync, ref flags);
    }

    public static void SyncReadEnd(int fd)
    {
        ulong flags = DmaBufSyncEnd | DmaBufSyncRead;
        _ = ioctl(fd, DmaBufIoctlSync, ref flags);
    }
}
