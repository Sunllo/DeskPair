using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Audio;
using DeskPair.Platform.MacOS.Native;

namespace DeskPair.Platform.MacOS.Audio;

/// <summary>
/// System-audio capture ("what you hear") through ScreenCaptureKit, delivered as 48 kHz stereo float in 10 ms
/// frames — the cadence the encoder and the other platforms use. The shim's blocking read runs on a dedicated
/// thread pushing onto a bounded channel. Capture needs Screen Recording consent; without it the shim fails to
/// start and the constructor throws, which the audio publisher treats as "no audio on this host".
/// </summary>
public sealed class MacAudioCapture : IAudioCapture
{
    private readonly ILogger _log;
    private readonly Channel<ReadOnlyMemory<float>> _frames =
        Channel.CreateBounded<ReadOnlyMemory<float>>(new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly int _frameSamples;
    private nint _handle;
    private Thread? _reader;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public MacAudioCapture(ILogger log)
    {
        _log = log;
        Format = new AudioStreamFormat(48000, 2);
        _frameSamples = Format.SampleRate / 100 * Format.Channels; // 10 ms interleaved
        _handle = MacShim.fd_audio_capture_create();
        if (_handle == 0)
        {
            throw new InvalidOperationException(
                "ScreenCaptureKit audio could not start; grant Screen Recording permission to DeskPair.");
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
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "mac-audio-capture" };
        _reader.Start();
        _log.LogInformation("ScreenCaptureKit audio: {Rate} Hz {Channels} ch", Format.SampleRate, Format.Channels);
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync()
    {
        _cts?.Cancel();
        return ValueTask.CompletedTask;
    }

    private unsafe void ReadLoop()
    {
        CancellationToken ct = _cts!.Token;

        // The shim drains whatever its ring holds, which is hardly ever a whole 10 ms. Subscribers are
        // promised exact frames — Opus encodes one frame size and nothing else — so the remainder is kept
        // and completed by the next read rather than published short. Publishing short is what this used to
        // do, and every packet was then dropped one layer up, silently, so a Mac host sent no audio at all.
        var pending = new float[_frameSamples];
        int have = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int got;
                fixed (float* p = &pending[have])
                {
                    got = MacShim.fd_audio_capture_read(_handle, p, _frameSamples - have, 100);
                }

                if (got <= 0)
                {
                    continue;
                }

                have += got;
                if (have < _frameSamples)
                {
                    continue;
                }

                _frames.Writer.TryWrite(pending);
                pending = new float[_frameSamples];
                have = 0;
            }
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "macOS audio capture loop ended");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _cts?.Cancel();
            _reader?.Join(TimeSpan.FromSeconds(1));
            if (_handle != 0)
            {
                MacShim.fd_audio_capture_destroy(_handle);
                _handle = 0;
            }

            _frames.Writer.TryComplete();
            _cts?.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>Controller-side playback through an AudioQueue. Reopens the queue on a format change.</summary>
public sealed class MacAudioPlayback : IAudioPlayback
{
    private readonly ILogger _log;
    private readonly object _gate = new();
    private nint _handle;
    private AudioStreamFormat _format;
    private bool _disposed;

    public MacAudioPlayback(ILogger log)
    {
        _log = log;
    }

    public string DeviceId { get; set; } = string.Empty;

    public ValueTask ConfigureAsync(AudioStreamFormat format, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_handle != 0 && _format == format)
            {
                return ValueTask.CompletedTask;
            }

            if (_handle != 0)
            {
                MacShim.fd_audio_playback_destroy(_handle);
                _handle = 0;
            }

            _format = format;
            _handle = MacShim.fd_audio_playback_create(format.SampleRate, format.Channels);
            if (_handle == 0)
            {
                _log.LogWarning("AudioQueue playback could not be created");
            }
            else
            {
                _log.LogInformation("AudioQueue playback: {Rate} Hz {Channels} ch", format.SampleRate, format.Channels);
            }
        }

        return ValueTask.CompletedTask;
    }

    public unsafe void Enqueue(ReadOnlySpan<float> interleavedPcm)
    {
        if (_handle == 0 || interleavedPcm.IsEmpty)
        {
            return;
        }

        fixed (float* p = interleavedPcm)
        {
            MacShim.fd_audio_playback_enqueue(_handle, p, interleavedPcm.Length);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (_handle != 0)
                {
                    MacShim.fd_audio_playback_destroy(_handle);
                    _handle = 0;
                }
            }
        }

        return ValueTask.CompletedTask;
    }
}
