using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.MediaFoundation;
using NAudio.Wave;
using DeskPair.Platform.Abstractions.Audio;

namespace DeskPair.Platform.Windows.Audio;

/// <summary>
/// "What you hear" capture through WASAPI loopback on the default render endpoint. Delivers 10 ms
/// interleaved float frames at an Opus rate (resampling with NAudio's WDL resampler when the mix
/// format rate is not one), downmixed to at most two channels.
/// </summary>
public sealed class WasapiAudioCapture : IAudioCapture
{
    private readonly ILogger _log;
    private readonly Channel<ReadOnlyMemory<float>> _frames = Channel.CreateBounded<ReadOnlyMemory<float>>(new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly WasapiRecorder _capture;
    private readonly WaveFormat _mix;
    private readonly bool _mixIsFloat;
    private readonly WdlResampler? _resampler;
    private readonly int _frameSamples;
    private float[] _pending = [];
    private int _pendingLength;
    private float[] _scratch = [];
    private float[] _resampled = [];
    private bool _disposed;

    public WasapiAudioCapture(ILogger log, string? deviceId = null)
    {
        _log = log;
        var builder = new WasapiRecorderBuilder().WithLoopbackCapture().WithBufferLength(20);
        // A chosen output device captures what plays on it; without one we follow the system default.
        MMDevice? device = AudioDevices.Resolve(deviceId, DataFlow.Render);
        if (device is not null)
        {
            builder = builder.WithDevice(device);
            _log.LogInformation("Audio capture device: {Name}", device.FriendlyName);
        }

        _capture = builder.Build();
        _mix = _capture.WaveFormat;
        _mixIsFloat = _mix.Encoding == WaveFormatEncoding.IeeeFloat || (_mix is WaveFormatExtensible ext && ext.SubFormat == AudioSubtypes.MFAudioFormat_Float);
        int channels = Math.Min(2, _mix.Channels);
        int rate = AudioStreamFormat.SnapToOpusRate(_mix.SampleRate);
        Format = new AudioStreamFormat(rate, channels);
        _frameSamples = rate / 100;
        if (rate != _mix.SampleRate)
        {
            _resampler = new WdlResampler();
            _resampler.SetMode(true, 2, false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(true);
            _resampler.SetRates(_mix.SampleRate, rate);
        }

        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
            {
                _log.LogWarning(e.Exception, "WASAPI loopback stopped");
            }

            _frames.Writer.TryComplete();
        };
    }

    public AudioStreamFormat Format { get; }

    public ChannelReader<ReadOnlyMemory<float>> Frames => _frames.Reader;

    public ValueTask StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _capture.StartRecording();
        _log.LogInformation("WASAPI loopback: {Mix} -> {Rate} Hz {Channels} ch", _mix, Format.SampleRate, Format.Channels);
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync()
    {
        if (!_disposed)
        {
            _capture.StopRecording();
        }

        return ValueTask.CompletedTask;
    }

    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        int mixChannels = _mix.Channels;
        int outChannels = Format.Channels;
        int frames = buffer.Length / _mix.BlockAlign;
        if (frames == 0)
        {
            return;
        }

        // Decode to float and downmix to the output channel count.
        if (_scratch.Length < frames * outChannels)
        {
            _scratch = new float[frames * outChannels];
        }

        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < outChannels; c++)
            {
                float v;
                if (mixChannels == 1)
                {
                    v = Sample(buffer, f);
                }
                else if (outChannels == 1)
                {
                    v = 0;
                    for (int m = 0; m < mixChannels; m++)
                    {
                        v += Sample(buffer, f * mixChannels + m);
                    }

                    v /= mixChannels;
                }
                else
                {
                    v = Sample(buffer, f * mixChannels + c);
                }

                _scratch[f * outChannels + c] = v;
            }
        }

        if (_resampler is null)
        {
            Append(_scratch.AsSpan(0, frames * outChannels));
            return;
        }

        int accepted = _resampler.ResamplePrepare(frames, outChannels, out Span<float> inBuffer);
        int fed = Math.Min(accepted, frames);
        _scratch.AsSpan(0, fed * outChannels).CopyTo(inBuffer);
        int outFrames = (int)Math.Ceiling(frames * (double)Format.SampleRate / _mix.SampleRate) + 16;
        if (_resampled.Length < outFrames * outChannels)
        {
            _resampled = new float[outFrames * outChannels];
        }

        int produced = _resampler.ResampleOut(_resampled, fed, outFrames, outChannels);
        Append(_resampled.AsSpan(0, produced * outChannels));
    }

    private float Sample(ReadOnlySpan<byte> buffer, int index)
    {
        if (_mixIsFloat)
        {
            return BitConverter.ToSingle(buffer[(index * 4)..]);
        }

        return _mix.BitsPerSample switch
        {
            16 => BitConverter.ToInt16(buffer[(index * 2)..]) / 32768f,
            32 => BitConverter.ToInt32(buffer[(index * 4)..]) / 2147483648f,
            _ => 0,
        };
    }

    private void Append(ReadOnlySpan<float> samples)
    {
        int frameLength = _frameSamples * Format.Channels;
        if (_pending.Length < _pendingLength + samples.Length)
        {
            Array.Resize(ref _pending, Math.Max(_pending.Length * 2, _pendingLength + samples.Length));
        }

        samples.CopyTo(_pending.AsSpan(_pendingLength));
        _pendingLength += samples.Length;
        int offset = 0;
        while (_pendingLength - offset >= frameLength)
        {
            _frames.Writer.TryWrite(_pending.AsSpan(offset, frameLength).ToArray());
            offset += frameLength;
        }

        if (offset > 0)
        {
            Array.Copy(_pending, offset, _pending, 0, _pendingLength - offset);
            _pendingLength -= offset;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        try
        {
            _capture.StopRecording();
        }
        catch (Exception)
        {
        }

        _capture.Dispose();
        _frames.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
