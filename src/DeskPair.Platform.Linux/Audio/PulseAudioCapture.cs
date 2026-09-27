using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Audio;
using DeskPair.Platform.Linux.Native;

namespace DeskPair.Platform.Linux.Audio;

/// <summary>
/// "What you hear" on Linux: a PulseAudio record stream against the default sink's monitor source, which is
/// the mix the user is listening to — the counterpart of WASAPI loopback. A monitor source is named
/// "&lt;sink&gt;.monitor"; passing the device as null lets the server pick, and the server routes a record
/// stream with no explicit source to the default source, so this asks for the monitor explicitly via the
/// well-known "@DEFAULT_MONITOR@" token that PulseAudio resolves to the current sink's monitor.
///
/// The simple API blocks, so the read loop owns a thread and pushes 10 ms frames onto a channel, the same
/// cadence the encoder and the other platforms use.
/// </summary>
public sealed class PulseAudioCapture : IAudioCapture
{
    private const string DefaultMonitor = "@DEFAULT_MONITOR@";

    private readonly ILogger _log;
    private readonly Channel<ReadOnlyMemory<float>> _frames =
        Channel.CreateBounded<ReadOnlyMemory<float>>(new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly int _frameSamples;
    private readonly int _bytesPerFrame;
    private nint _stream;
    private Thread? _reader;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public PulseAudioCapture(ILogger log, string? device)
    {
        _log = log;

        // 48 kHz stereo float, an Opus rate, matching the other capturers. Ten milliseconds per frame.
        Format = new AudioStreamFormat(48000, 2);
        _frameSamples = Format.SampleRate / 100;
        _bytesPerFrame = _frameSamples * Format.Channels * sizeof(float);

        var spec = new PulseSimple.PaSampleSpec
        {
            Format = PulseSimple.SampleFloat32Le,
            Rate = (uint)Format.SampleRate,
            Channels = (byte)Format.Channels,
        };

        string source = string.IsNullOrEmpty(device) ? DefaultMonitor : device;
        _stream = PulseSimple.pa_simple_new(null, "Sunllo DeskPair", PulseSimple.StreamRecord, source, "desktop audio", ref spec, 0, 0, out int error);
        if (_stream == 0)
        {
            throw new InvalidOperationException($"PulseAudio capture unavailable: {Marshal.PtrToStringUTF8(PulseSimple.pa_strerror(error))}.");
        }
    }

    public AudioStreamFormat Format { get; }

    public ChannelReader<ReadOnlyMemory<float>> Frames => _frames.Reader;

    public ValueTask StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_reader is not null)
        {
            return ValueTask.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "pulse-capture" };
        _reader.Start();
        _log.LogInformation("PulseAudio loopback: {Rate} Hz {Channels} ch from the default monitor", Format.SampleRate, Format.Channels);
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync()
    {
        _cts?.Cancel();
        return ValueTask.CompletedTask;
    }

    private void ReadLoop()
    {
        CancellationToken ct = _cts!.Token;
        byte[] buffer = new byte[_bytesPerFrame];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read;
                unsafe
                {
                    fixed (byte* p = buffer)
                    {
                        read = PulseSimple.pa_simple_read(_stream, p, (nuint)buffer.Length, out int error);
                        if (read < 0)
                        {
                            _log.LogWarning("PulseAudio read failed: {Error}", Marshal.PtrToStringUTF8(PulseSimple.pa_strerror(error)));
                            return;
                        }
                    }
                }

                var frame = new float[_frameSamples * Format.Channels];
                Buffer.BlockCopy(buffer, 0, frame, 0, buffer.Length);
                _frames.Writer.TryWrite(frame);
            }
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "PulseAudio capture loop ended");
        }
        finally
        {
            // Free the stream on the same thread that reads it. pa_simple_read blocks and libpulse aborts the
            // whole process if pa_simple_free runs on another thread while a read is in flight, so ownership of
            // the free stays here; Dispose only signals and waits.
            FreeStream();
        }
    }

    private void FreeStream()
    {
        lock (_streamLock)
        {
            if (_stream != 0)
            {
                PulseSimple.pa_simple_free(_stream);
                _stream = 0;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _cts?.Cancel();
            if (_reader is not null)
            {
                // The reader frees the stream as it exits (monitor sources deliver every ~10 ms, so a read in
                // flight returns almost at once). Only if it fails to exit do we free here rather than leak.
                if (!_reader.Join(TimeSpan.FromSeconds(2)))
                {
                    _log.LogWarning("PulseAudio reader did not exit; leaking the stream to avoid an abort");
                }
            }
            else
            {
                // StartAsync was never called, so no reader owns the stream; free it here.
                FreeStream();
            }

            _frames.Writer.TryComplete();
            _cts?.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private readonly object _streamLock = new();
}
