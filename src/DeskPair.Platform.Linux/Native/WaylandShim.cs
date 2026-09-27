using System.Reflection;
using System.Runtime.InteropServices;

namespace DeskPair.Platform.Linux.Native;

/// <summary>
/// The Sunllo DeskPair Wayland shim (<c>native/linux/SunlloWaylandShim</c>): one PipeWire video stream behind a
/// pull-shaped C surface, because PipeWire's format negotiation is static inline code in headers with nothing to
/// P/Invoke. Found like the macOS shim and libvpx: an explicit path, then beside the executable and in the runtimes
/// folder, then the loader's own search (which is where a single-file build extracts it to).
///
/// <see cref="IsAvailable"/> is false when the library is missing, when libpipewire it depends on is missing, and
/// when it was built from a different <c>shim.h</c> -- the structs below would then be read at the wrong offsets.
/// </summary>
internal static partial class WaylandShim
{
    public const string LibraryName = "SunlloWaylandShim";
    public const string FileName = "libSunlloWaylandShim.so";

    /// <summary><c>DP_PW_ABI</c> in shim.h.</summary>
    public const int Abi = 1;

    public const int FormatBgrx = 1;
    public const int FormatRgbx = 2;
    public const int FormatBgra = 3;
    public const int FormatRgba = 4;

    public const int StateConnecting = 0;
    public const int StatePaused = 1;
    public const int StateStreaming = 2;
    public const int StateError = -1;
    public const int StateClosed = -2;

    public const int FrameBorrowable = 1;

    private static nint _handle;

    private static readonly Lazy<string?> Unavailable = new(Probe);

    static WaylandShim() => NativeLibrary.SetDllImportResolver(typeof(WaylandShim).Assembly, Resolve);

    public static bool IsAvailable => Unavailable.Value is null;

    /// <summary>Why the shim cannot be used, in words for a log line; null when it can.</summary>
    public static string? UnavailableReason => Unavailable.Value;

    private static unsafe string? Probe()
    {
        if (!OperatingSystem.IsLinux())
        {
            return "not Linux";
        }

        try
        {
            int abi = dp_pw_abi(out int frameSize, out int cursorSize);
            if (abi != Abi || frameSize != sizeof(DpFrame) || cursorSize != sizeof(DpCursor))
            {
                return $"{FileName} was built from another shim.h (ABI {abi}, frame {frameSize} bytes, cursor {cursorSize}; " +
                       $"this build expects ABI {Abi}, {sizeof(DpFrame)} and {sizeof(DpCursor)})";
            }

            return null;
        }
        catch (DllNotFoundException e)
        {
            // Also what a present shim reports when libpipewire-0.3.so.0 is not installed.
            return $"{FileName} or libpipewire-0.3 cannot be loaded: {e.Message}";
        }
        catch (Exception e) when (e is EntryPointNotFoundException or BadImageFormatException)
        {
            return $"{FileName} is not usable: {e.Message}";
        }
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != LibraryName)
        {
            return 0;
        }

        if (_handle != 0)
        {
            return _handle;
        }

        foreach (string candidate in CandidatePaths())
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out nint h))
            {
                return _handle = h;
            }
        }

        return NativeLibrary.TryLoad(FileName, assembly, searchPath, out nint bare) ? _handle = bare : 0;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        string? explicitPath = Environment.GetEnvironmentVariable("SUNLLO_WAYLANDSHIM_PATH");
        if (!string.IsNullOrEmpty(explicitPath))
        {
            yield return explicitPath;
        }

        string baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, FileName);
        yield return Path.Combine(baseDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", FileName);
        yield return Path.Combine(baseDir, "runtimes", $"linux-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}", "native", FileName);
    }

    /// <summary><c>dp_frame</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DpFrame
    {
        public nint Data;
        public int Width;
        public int Height;
        public int Stride;
        public int Format;
        public ulong Sequence;
        public long PtsNs;
        public int Buffers;
        public int Flags;
        public long Readable;
    }

    /// <summary><c>dp_cursor</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DpCursor
    {
        public int Visible;
        public int X;
        public int Y;
        public int HotX;
        public int HotY;
        public int Width;
        public int Height;
        public int Format;
        public ulong PositionSerial;
        public ulong ShapeSerial;
    }

    [LibraryImport(LibraryName)]
    public static partial int dp_pw_abi(out int frameSize, out int cursorSize);

    [LibraryImport(LibraryName)]
    public static unsafe partial int dp_pw_open(int fd, uint node, out nint pw, byte* error, int errorLength);

    [LibraryImport(LibraryName)]
    public static partial int dp_pw_wait(nint pw, int timeoutMs);

    [LibraryImport(LibraryName)]
    public static partial int dp_pw_lock_frame(nint pw, out DpFrame frame);

    [LibraryImport(LibraryName)]
    public static partial void dp_pw_release_frame(nint pw);

    [LibraryImport(LibraryName)]
    public static unsafe partial int dp_pw_cursor(nint pw, out DpCursor cursor, byte* bitmap, int capacity);

    [LibraryImport(LibraryName)]
    public static unsafe partial int dp_pw_state(nint pw, byte* error, int errorLength);

    [LibraryImport(LibraryName)]
    public static partial void dp_pw_close(nint pw);
}
