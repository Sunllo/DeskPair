using DeskPair.Platform.Abstractions.Audio;
using DeskPair.Platform.Abstractions.Codec;

namespace DeskPair.Platform.Abstractions.Recording;

/// <summary>What one recording file is being asked to hold.</summary>
public sealed record RecorderOptions(string Path, VideoCodec Codec, int Width, int Height)
{
    /// <summary>Container hint only; the real timing comes from each frame's timestamp.</summary>
    public int Fps { get; init; } = 30;

    /// <summary>Null records a video-only file.</summary>
    public AudioStreamFormat? Audio { get; init; }

    public int AudioBitrateBps { get; init; } = 96_000;
}

/// <summary>What happened to a frame handed to the recorder.</summary>
public enum RecorderWrite
{
    Written,

    /// <summary>Discarded: a file has to open on a keyframe or it cannot be decoded.</summary>
    WaitingForKeyFrame,

    /// <summary>The picture size or codec changed; the caller should finish this file and start the next one.</summary>
    SizeChanged,

    Faulted,
}

/// <summary>
/// Writes the session to a file as it arrives. Video is stored exactly as it came off the wire, so recording
/// costs no encoding; audio is re-encoded because a container cannot hold raw float samples.
/// </summary>
public interface ISessionRecorder : IAsyncDisposable
{
    string Path { get; }

    /// <summary>False until the first keyframe landed and the file actually holds something.</summary>
    bool HasStarted { get; }

    TimeSpan Duration { get; }

    long BytesWritten { get; }

    /// <summary>Frames dropped because the writer could not keep up; the next keyframe recovers.</summary>
    long DroppedFrames { get; }

    /// <summary>One access unit, copied before returning. <paramref name="ptsMs"/> is the host clock; the recorder rebases it.</summary>
    RecorderWrite TryWriteVideo(ReadOnlySpan<byte> frame, bool key, long ptsMs, int width, int height, VideoCodec codec);

    /// <summary>Interleaved samples in the format the recorder was created with.</summary>
    void WriteAudio(ReadOnlySpan<float> interleaved, long ptsMs);

    /// <summary>Finishes the container so the file is playable; safe to call more than once.</summary>
    ValueTask StopAsync();
}

/// <summary>Creates recorders for whatever this platform can write.</summary>
public interface ISessionRecorderFactory
{
    bool IsSupported { get; }

    bool Supports(VideoCodec codec);

    ISessionRecorder Create(RecorderOptions options);
}
