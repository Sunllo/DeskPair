using System.Runtime.InteropServices;

namespace DeskPair.Platform.Linux.Native;

/// <summary>
/// PulseAudio's synchronous "simple" API, which is all a one-way capture needs: open a stream against the
/// default sink's monitor source and read blocking. "What you hear" on PulseAudio (and PipeWire's Pulse
/// shim, which is what Ubuntu runs) is the monitor of the output device, so recording from it captures the
/// mix the user is hearing — the same thing WASAPI loopback gives on Windows.
///
/// The simple API blocks, so the capture loop runs on its own thread; the full asynchronous mainloop would
/// buy nothing here but complexity.
/// </summary>
internal static partial class PulseSimple
{
    private const string Lib = "libpulse-simple.so.0";

    /// <summary>PA_STREAM_PLAYBACK: this stream writes to a sink (controller-side audio playback).</summary>
    public const int StreamPlayback = 1;

    /// <summary>PA_STREAM_RECORD: this stream reads from the source rather than writing to a sink.</summary>
    public const int StreamRecord = 2;

    /// <summary>PA_SAMPLE_FLOAT32LE: 32-bit float samples, little-endian, which is what the pipeline wants.</summary>
    public const int SampleFloat32Le = 5;

    /// <summary>
    /// Opens a blocking stream. <paramref name="device"/> null means the default; for capture the caller
    /// passes the default sink's monitor source name. <paramref name="spec"/> fixes the format, rate and
    /// channel count. Returns a handle or 0, with the PA error in <paramref name="error"/>.
    /// </summary>
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint pa_simple_new(
        string? server,
        string appName,
        int direction,
        string? device,
        string streamName,
        ref PaSampleSpec spec,
        nint channelMap,
        nint bufferAttr,
        out int error);

    /// <summary>Blocks until <paramref name="bytes"/> have been read into <paramref name="data"/>.</summary>
    [LibraryImport(Lib)]
    public static unsafe partial int pa_simple_read(nint stream, byte* data, nuint bytes, out int error);

    /// <summary>Blocks until <paramref name="bytes"/> from <paramref name="data"/> are handed to the server.</summary>
    [LibraryImport(Lib)]
    public static unsafe partial int pa_simple_write(nint stream, byte* data, nuint bytes, out int error);

    /// <summary>Waits for all written data to finish playing; called before free on a clean stop.</summary>
    [LibraryImport(Lib)]
    public static partial int pa_simple_drain(nint stream, out int error);

    [LibraryImport(Lib)]
    public static partial void pa_simple_free(nint stream);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint pa_strerror(int error);

    [StructLayout(LayoutKind.Sequential)]
    public struct PaSampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }
}
