using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using DeskPair.Platform.Abstractions.Audio;

namespace DeskPair.Platform.Windows.Audio;

/// <summary>Plays decoded remote audio through shared-mode WASAPI with a small jitter buffer.</summary>
public sealed class WasapiAudioPlayback : IAudioPlayback
{
    private static readonly TimeSpan MaxBuffered = TimeSpan.FromMilliseconds(400);

    private readonly ILogger _log;
    private readonly object _lock = new();
    private WasapiPlayer? _output;
    private BufferedWaveProvider? _buffer;
    private byte[] _scratch = [];
    private bool _disposed;

    public WasapiAudioPlayback(ILogger log)
    {
        _log = log;
    }

    /// <summary>Device chosen in settings; empty follows the system default. Applies from the next configure.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Exclusive mode hands the device to this app alone: lower latency, nothing else can play.</summary>
    public bool Exclusive { get; init; }

    public ValueTask ConfigureAsync(AudioStreamFormat format, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            Teardown();
            WaveFormat wave = WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);
            _buffer = new BufferedWaveProvider(wave, TimeSpan.FromSeconds(2)) { DiscardOnBufferOverflow = true };
            var builder = new WasapiPlayerBuilder();
            NAudio.CoreAudioApi.MMDevice? device = AudioDevices.Resolve(DeviceId, NAudio.CoreAudioApi.DataFlow.Render);
            if (device is not null)
            {
                builder = builder.WithDevice(device);
            }

            builder = Exclusive ? builder.WithExclusiveMode() : builder.WithSharedMode();
            _output = builder.WithLatency(60).Build();
            _output.Init(_buffer);
            _output.Play();
            _log.LogInformation("WASAPI playback: {Rate} Hz {Channels} ch", format.SampleRate, format.Channels);
        }

        return ValueTask.CompletedTask;
    }

    public void Enqueue(ReadOnlySpan<float> interleavedPcm)
    {
        lock (_lock)
        {
            if (_buffer is null)
            {
                return;
            }

            if (_buffer.BufferedDuration > MaxBuffered)
            {
                _buffer.ClearBuffer(); // fell behind; resynchronise rather than drift
            }

            int bytes = interleavedPcm.Length * 4;
            if (_scratch.Length < bytes)
            {
                _scratch = new byte[bytes];
            }

            MemoryMarshal.AsBytes(interleavedPcm).CopyTo(_scratch);
            _buffer.AddSamples(_scratch, 0, bytes);
        }
    }

    private void Teardown()
    {
        _output?.Stop();
        _output?.Dispose();
        _output = null;
        _buffer = null;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        lock (_lock)
        {
            Teardown();
        }

        return ValueTask.CompletedTask;
    }
}
