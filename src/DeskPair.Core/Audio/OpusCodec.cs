using Concentus;
using Concentus.Enums;
using DeskPair.Platform.Abstractions.Audio;

namespace DeskPair.Core.Audio;

/// <summary>Opus encoder for 10 ms low-delay packets (pure managed implementation).</summary>
public sealed class OpusEncoderWrapper : IDisposable
{
    private readonly IOpusEncoder _encoder;
    private readonly byte[] _buffer = new byte[4000];

    public OpusEncoderWrapper(AudioStreamFormat format, int bitrateBps = 96_000)
    {
        Format = format;
        _encoder = OpusCodecFactory.CreateEncoder(format.SampleRate, format.Channels, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        _encoder.Bitrate = bitrateBps;
        _encoder.Complexity = 5;
        FrameSamples = format.SampleRate / 100;
    }

    public AudioStreamFormat Format { get; }

    /// <summary>Samples per channel in one 10 ms packet.</summary>
    public int FrameSamples { get; }

    /// <summary>Encodes exactly one 10 ms interleaved float frame; returns the packet bytes (valid until the next call).</summary>
    public ReadOnlySpan<byte> Encode(ReadOnlySpan<float> interleaved)
    {
        if (interleaved.Length != FrameSamples * Format.Channels)
        {
            throw new ArgumentException($"Expected {FrameSamples * Format.Channels} samples.", nameof(interleaved));
        }

        int n = _encoder.Encode(interleaved, FrameSamples, _buffer, _buffer.Length);
        return _buffer.AsSpan(0, n);
    }

    public void Dispose() => _encoder.Dispose();
}

public sealed class OpusDecoderWrapper : IDisposable
{
    private readonly IOpusDecoder _decoder;
    private readonly float[] _pcm;

    public OpusDecoderWrapper(AudioStreamFormat format)
    {
        Format = format;
        _decoder = OpusCodecFactory.CreateDecoder(format.SampleRate, format.Channels);
        FrameSamples = format.SampleRate / 100;
        _pcm = new float[FrameSamples * 6 * format.Channels]; // room for up to 60 ms
    }

    public AudioStreamFormat Format { get; }

    public int FrameSamples { get; }

    /// <summary>Decodes one packet (or conceals a lost one when <paramref name="packet"/> is empty).</summary>
    public ReadOnlySpan<float> Decode(ReadOnlySpan<byte> packet)
    {
        int samples = packet.IsEmpty
            ? _decoder.Decode(ReadOnlySpan<byte>.Empty, _pcm, FrameSamples, false)
            : _decoder.Decode(packet, _pcm, _pcm.Length / Format.Channels, false);
        return _pcm.AsSpan(0, samples * Format.Channels);
    }

    public void Dispose() => _decoder.Dispose();
}
