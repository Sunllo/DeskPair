using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Audio;
using DeskPair.Platform.Linux.Native;

namespace DeskPair.Platform.Linux.Audio;

/// <summary>
/// Controller-side playback through PulseAudio's simple API: the far end's audio arrives as interleaved
/// float32 and is written to the default sink. The simple write blocks until the server accepts the samples,
/// so enqueue hands frames to a bounded channel and a dedicated thread drains it — the caller (the decode /
/// playout path) never blocks, and an overrun drops the oldest frame rather than stalling the session.
///
/// The stream is opened on the first configure; a format change (different rate or channel count) reopens it.
/// PulseAudio resamples to the sink's native rate itself, so any Opus rate the session negotiates works.
/// </summary>
public sealed class PulseAudioPlayback : IAudioPlayback
{
    private readonly ILogger _log;
    private readonly object _gate = new();

    private Channel<float[]>? _frames;
    private Thread? _writer;
    private CancellationTokenSource? _cts;
    private nint _stream;
    private AudioStreamFormat _format;
    private bool _disposed;

    public PulseAudioPlayback(ILogger log)
    {
        _log = log;
    }

    public string DeviceId { get; set; } = string.Empty;

    public ValueTask ConfigureAsync(AudioStreamFormat format, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_stream != 0 && _format == format)
            {
                return ValueTask.CompletedTask;
            }

            TeardownStream();
            _format = format;

            var spec = new PulseSimple.PaSampleSpec
            {
                Format = PulseSimple.SampleFloat32Le,
                Rate = (uint)format.SampleRate,
                Channels = (byte)format.Channels,
            };

            string? device = string.IsNullOrEmpty(DeviceId) ? null : DeviceId;
            _stream = PulseSimple.pa_simple_new(null, "Sunllo DeskPair", PulseSimple.StreamPlayback, device, "remote audio", ref spec, 0, 0, out int error);
            if (_stream == 0)
            {
                _log.LogWarning("PulseAudio playback unavailable: {Error}", Marshal.PtrToStringUTF8(PulseSimple.pa_strerror(error)));
                return ValueTask.CompletedTask;
            }

            _frames = Channel.CreateBounded<float[]>(new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropOldest });
            _cts = new CancellationTokenSource();
            nint stream = _stream;
            _writer = new Thread(() => WriteLoop(stream)) { IsBackground = true, Name = "pulse-playback" };
            _writer.Start();
            _log.LogInformation("PulseAudio playback: {Rate} Hz {Channels} ch", format.SampleRate, format.Channels);
        }

        return ValueTask.CompletedTask;
    }

    public void Enqueue(ReadOnlySpan<float> interleavedPcm)
    {
        Channel<float[]>? frames = _frames;
        if (frames is null || interleavedPcm.IsEmpty)
        {
            return;
        }

        frames.Writer.TryWrite(interleavedPcm.ToArray());
    }

    private void WriteLoop(nint stream)
    {
        CancellationToken ct = _cts!.Token;
        ChannelReader<float[]> reader = _frames!.Reader;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!reader.TryRead(out float[]? frame))
                {
                    // A blocking wait keeps the thread idle between frames without spinning.
                    frame = reader.ReadAsync(ct).AsTask().GetAwaiter().GetResult();
                }

                unsafe
                {
                    fixed (float* p = frame)
                    {
                        if (PulseSimple.pa_simple_write(stream, (byte*)p, (nuint)(frame.Length * sizeof(float)), out int error) < 0)
                        {
                            _log.LogWarning("PulseAudio write failed: {Error}", Marshal.PtrToStringUTF8(PulseSimple.pa_strerror(error)));
                            break;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "PulseAudio playback loop ended");
        }
        finally
        {
            // Free on the same thread that writes, so pa_simple_free never races an in-flight pa_simple_write
            // on another thread (which makes libpulse abort the process).
            PulseSimple.pa_simple_free(stream);
        }
    }

    private void TeardownStream()
    {
        _cts?.Cancel();
        _frames?.Writer.TryComplete();
        if (_writer is not null)
        {
            // The writer frees its own stream as it exits; only free here if it never started.
            _writer.Join(TimeSpan.FromSeconds(2));
        }
        else if (_stream != 0)
        {
            PulseSimple.pa_simple_free(_stream);
        }

        _stream = 0;
        _cts?.Dispose();
        _cts = null;
        _frames = null;
        _writer = null;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                TeardownStream();
            }
        }

        return ValueTask.CompletedTask;
    }
}
