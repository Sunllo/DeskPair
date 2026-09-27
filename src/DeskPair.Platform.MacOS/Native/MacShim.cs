using System.Reflection;
using System.Runtime.InteropServices;

namespace DeskPair.Platform.MacOS.Native;

/// <summary>
/// The Sunllo DeskPair macOS shim, loaded at run time. The native side is a small Objective-C dylib that
/// wraps the AppKit / CoreGraphics / ScreenCaptureKit / VideoToolbox primitives which are unsafe to reach
/// through raw objc_msgSend from .NET; this binds its plain-C surface. The loader mirrors the libvpx one:
/// an explicit path wins, then the app directory and the runtimes folder, then the bare name.
/// </summary>
internal static partial class MacShim
{
    public const string LibraryName = "SunlloMacShim";

    private static nint _handle;

    static MacShim() => NativeLibrary.SetDllImportResolver(typeof(MacShim).Assembly, Resolve);

    public static bool IsAvailable => Available.Value;

    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            // Any exported call proves the dylib resolved and its symbols are present.
            return fd_displays_get(null, 0) >= 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    });

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

        return NativeLibrary.TryLoad("libSunlloMacShim.dylib", out nint bare) ? _handle = bare : 0;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        string? explicitPath = Environment.GetEnvironmentVariable("SUNLLO_MACSHIM_PATH");
        if (!string.IsNullOrEmpty(explicitPath))
        {
            yield return explicitPath;
        }

        string baseDir = AppContext.BaseDirectory;
        string rid = RuntimeInformation.RuntimeIdentifier;
        string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        const string file = "libSunlloMacShim.dylib";
        yield return Path.Combine(baseDir, file);
        yield return Path.Combine(baseDir, "runtimes", rid, "native", file);
        yield return Path.Combine(baseDir, "runtimes", $"osx-{arch}", "native", file);
    }

    // ---- Memory / clipboard / identity / keychain ----

    [LibraryImport(LibraryName)]
    public static partial void fd_free(nint p);

    [LibraryImport(LibraryName)]
    public static unsafe partial int fd_pty_spawn(byte* path, nint* argv, nint* envp, byte* cwd, int columns, int rows, out int master, out int pid);

    [LibraryImport(LibraryName)]
    public static partial int fd_pty_resize(int master, int columns, int rows);

    [LibraryImport(LibraryName)]
    public static partial long fd_clipboard_change_count();

    [LibraryImport(LibraryName)]
    public static partial nint fd_clipboard_read_text();

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void fd_clipboard_write_text(string utf8);

    /// <summary>1 when the pasteboard holds an image, without copying any of it.</summary>
    [LibraryImport(LibraryName)]
    public static partial int fd_clipboard_has_image();

    /// <summary>The image as PNG, malloc'd; free with <see cref="fd_free"/>. Returns 1 on success.</summary>
    [LibraryImport(LibraryName)]
    public static partial int fd_clipboard_read_png(out nint data, out int len);

    /// <summary>Writes a PNG, its TIFF rendering and optionally text, in one declaration.</summary>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial int fd_clipboard_write_image(byte* png, int len, string? textOrNull);

    // ---- Clipboard file promises ----

    /// <summary>Copied file paths as one NUL-separated UTF-8 block; returns the count and sets <paramref name="paths"/>.</summary>
    [LibraryImport(LibraryName)]
    public static partial int fd_clipboard_read_file_paths(out nint paths);

    /// <summary>Names are NUL-separated UTF-8, one per promised item, in the order they will be asked for.</summary>
    [LibraryImport(LibraryName)]
    public static partial int fd_clipboard_write_file_promises([In] byte[] names, int count, [In] byte[]? textUtf8OrNull);

    /// <summary>The index of the item being pasted, or -1 if nothing was asked for within the timeout.</summary>
    [LibraryImport(LibraryName)]
    public static partial int fd_promise_next(int timeoutMs, out long requestId);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial void fd_promise_complete(long requestId, string? stagedPath);

    [LibraryImport(LibraryName)]
    public static partial void fd_promise_shutdown();

    [LibraryImport(LibraryName)]
    public static partial int fd_machine_uuid([Out] byte[] outBuf, int max);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static unsafe partial int fd_keychain_set(string account, byte* data, int len);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int fd_keychain_get(string account, out nint data, out int len);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int fd_keychain_delete(string account);

    /// <summary>
    /// Runs a tool as root after asking, with a dialog naming this application rather than osascript.
    /// </summary>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int fd_authorize_run(string tool, nint argv, string? prompt, [Out] byte[] outBuf, int outMax);

    /// <summary>Reads from another service name; only for carrying an identity across the rename.</summary>
    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int fd_keychain_get_from(string service, string account, out nint data, out int len);

    // ---- Permissions (TCC) and identity ----

    [LibraryImport(LibraryName)]
    public static partial int fd_tcc_screen_granted();

    [LibraryImport(LibraryName)]
    public static partial int fd_tcc_screen_request();

    [LibraryImport(LibraryName)]
    public static partial int fd_tcc_accessibility_granted();

    [LibraryImport(LibraryName)]
    public static partial int fd_tcc_accessibility_request();

    [LibraryImport(LibraryName)]
    public static partial int fd_is_root();

    [LibraryImport(LibraryName)]
    public static partial int fd_console_user([Out] byte[] nameOut, int max, out uint uid);

    // ---- Displays ----

    [StructLayout(LayoutKind.Sequential)]
    public struct FdDisplay
    {
        public uint Id;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public double Scale;
        public int IsPrimary;
    }

    [LibraryImport(LibraryName)]
    public static partial int fd_displays_get([Out] FdDisplay[]? outDisplays, int max);

    [StructLayout(LayoutKind.Sequential)]
    public struct FdDisplayMode
    {
        public int Width;
        public int Height;
        public double Scale;
    }

    [LibraryImport(LibraryName)]
    public static partial int fd_display_modes_get(uint displayId, [Out] FdDisplayMode[]? outModes, int max);

    [LibraryImport(LibraryName)]
    public static partial int fd_display_mode_set(uint displayId, int width, int height, double scale);

    // ---- Input ----

    [LibraryImport(LibraryName)]
    public static partial nint fd_input_create();

    [LibraryImport(LibraryName)]
    public static partial void fd_input_destroy(nint handle);

    [LibraryImport(LibraryName)]
    public static partial void fd_input_mouse_move(nint handle, double x, double y, ulong flags, int held);

    [LibraryImport(LibraryName)]
    public static partial void fd_input_mouse_button(nint handle, int button, int down, double x, double y, ulong flags);

    [LibraryImport(LibraryName)]
    public static partial void fd_input_scroll(nint handle, int dx, int dy);

    [LibraryImport(LibraryName)]
    public static partial void fd_input_key(nint handle, ushort keycode, int down, ulong flags);

    [LibraryImport(LibraryName)]
    public static unsafe partial void fd_input_text(nint handle, ushort* utf16, int len);

    // ---- Cursor ----

    [LibraryImport(LibraryName)]
    public static partial int fd_cursor_position(out double x, out double y);

    /// <summary>Changes whenever the system cursor does; 0 when the SPI behind it is unavailable.</summary>
    [LibraryImport(LibraryName)]
    public static partial ulong fd_cursor_seed();

    /// <summary>Size and hotspot, plus the pixels when <paramref name="bgra"/> has room. Returns non-zero on success.</summary>
    [LibraryImport(LibraryName)]
    public static partial int fd_cursor_image(out int width, out int height, out int hotX, out int hotY, Span<byte> bgra, int bgraLen);

    // ---- Capture ----

    [StructLayout(LayoutKind.Sequential)]
    public struct FdFrame
    {
        public int Width;
        public int Height;
        public int Stride;
        public nint Data;
    }

    [LibraryImport(LibraryName)]
    public static partial nint fd_capture_create(uint displayId, int width, int height, int showCursor);

    [LibraryImport(LibraryName)]
    public static partial int fd_capture_acquire(nint handle, int timeoutMs, out FdFrame frame);

    [LibraryImport(LibraryName)]
    public static partial void fd_capture_release(nint handle);

    [LibraryImport(LibraryName)]
    public static partial void fd_capture_destroy(nint handle);

    // ---- Encode ----

    [StructLayout(LayoutKind.Sequential)]
    public struct FdPacket
    {
        public nint Data;
        public int Length;
        public int IsKeyframe;
    }

    [LibraryImport(LibraryName)]
    public static partial nint fd_encoder_create(int width, int height, int fps, int bitrateKbps, int codec);

    [LibraryImport(LibraryName)]
    public static unsafe partial int fd_encoder_encode(nint handle, byte* bgra, int stride, long ptsTicks, int forceKeyframe, out FdPacket packet);

    [LibraryImport(LibraryName)]
    public static partial void fd_encoder_release(nint handle);

    [LibraryImport(LibraryName)]
    public static partial void fd_encoder_set_bitrate(nint handle, int bitrateKbps);

    [LibraryImport(LibraryName)]
    public static partial int fd_encoder_is_hardware(nint handle);

    [LibraryImport(LibraryName)]
    public static partial void fd_encoder_destroy(nint handle);

    // ---- Decode ----

    [StructLayout(LayoutKind.Sequential)]
    public struct FdDecodedFrame
    {
        public int Width;
        public int Height;
        public int Stride;
        public nint Data;
    }

    [LibraryImport(LibraryName)]
    public static partial nint fd_decoder_create(int codec);

    [LibraryImport(LibraryName)]
    public static unsafe partial int fd_decoder_decode(nint handle, byte* annexb, int len, out FdDecodedFrame frame);

    [LibraryImport(LibraryName)]
    public static partial void fd_decoder_release(nint handle);

    [LibraryImport(LibraryName)]
    public static partial void fd_decoder_destroy(nint handle);

    // ---- Audio ----

    [LibraryImport(LibraryName)]
    public static partial nint fd_audio_capture_create();

    [LibraryImport(LibraryName)]
    public static unsafe partial int fd_audio_capture_read(nint handle, float* outBuf, int maxSamples, int timeoutMs);

    [LibraryImport(LibraryName)]
    public static partial void fd_audio_capture_destroy(nint handle);

    [LibraryImport(LibraryName)]
    public static partial nint fd_audio_playback_create(int sampleRate, int channels);

    [LibraryImport(LibraryName)]
    public static unsafe partial void fd_audio_playback_enqueue(nint handle, float* interleaved, int samples);

    [LibraryImport(LibraryName)]
    public static partial void fd_audio_playback_destroy(nint handle);
}
