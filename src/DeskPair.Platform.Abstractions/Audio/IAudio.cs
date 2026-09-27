using System.Threading.Channels;

namespace DeskPair.Platform.Abstractions.Audio;

/// <summary>PCM stream format; sample rate is one of the Opus rates (8k/12k/16k/24k/48k).</summary>
public readonly record struct AudioStreamFormat(int SampleRate, int Channels)
{
    public static int SnapToOpusRate(int rate) => rate switch
    {
        < 12000 => 8000,
        < 16000 => 12000,
        < 24000 => 16000,
        < 48000 => 24000,
        _ => 48000,
    };
}

/// <summary>Captures system output ("what you hear") as 10 ms interleaved float32 packets.</summary>
/// <summary>One sound device as the settings screen shows it.</summary>
public readonly record struct AudioDeviceInfo(string Id, string Name, bool IsDefault);

public interface IAudioCapture : IAsyncDisposable
{
    AudioStreamFormat Format { get; }

    ChannelReader<ReadOnlyMemory<float>> Frames { get; }

    ValueTask StartAsync(CancellationToken ct);

    ValueTask StopAsync();
}

public interface IAudioPlayback : IAsyncDisposable
{
    /// <summary>Device chosen in settings; empty means whatever Windows is using. Applies from the next configure.</summary>
    string DeviceId { get => string.Empty; set { } }

    ValueTask ConfigureAsync(AudioStreamFormat format, CancellationToken ct);

    void Enqueue(ReadOnlySpan<float> interleavedPcm);
}
