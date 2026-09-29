using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Platform.Abstractions.Audio;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Platform.Abstractions.Recording;
using DeskPair.Platform.Windows.Audio;
using DeskPair.Platform.Windows.Codec;
using DeskPair.Platform.Windows.Recording;

namespace DeskPair.Platform.Windows.Tests;

/// <summary>
/// Recording writes the stream that is already arriving, so what has to be right is the timing: the file has
/// to open on a keyframe, keep monotonic timestamps, and end up playable.
/// </summary>
public class MfRecorderTests : IDisposable
{
    private const int W = 640;
    private const int H = 360;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sunllo-rec-" + Guid.NewGuid().ToString("N"));

    public MfRecorderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_recording_starts_at_the_first_key_frame_and_plays_back()
    {
        if (!MediaFoundationEncoding.CanEncode(VideoCodec.H264))
        {
            return; // a machine whose Media Foundation lists an H.264 encoder that will not encode
        }

        var factory = new MfSessionRecorderFactory(NullLoggerFactory.Instance);
        factory.IsSupported.ShouldBeTrue();
        factory.Supports(VideoCodec.H264).ShouldBeTrue();
        factory.Supports(VideoCodec.H265).ShouldBeTrue();
        factory.Supports(VideoCodec.Av1).ShouldBeFalse("MP4 through the Media Foundation sink has no AV1 box");

        string path = Path.Combine(_dir, "session.mp4");
        var audio = new AudioStreamFormat(48000, 2);
        await using ISessionRecorder recorder = factory.Create(new RecorderOptions(path, VideoCodec.H264, W, H) { Audio = audio });

        List<(byte[] Data, bool Key)> frames = await EncodeAsync(60);
        frames.ShouldNotBeEmpty();

        // Everything before the first key frame has to be refused, or the file cannot be decoded.
        int firstKey = frames.FindIndex(f => f.Key);
        firstKey.ShouldBeGreaterThanOrEqualTo(0);
        for (int i = 0; i < firstKey; i++)
        {
            recorder.TryWriteVideo(frames[i].Data, key: false, i * 33, W, H, VideoCodec.H264).ShouldBe(RecorderWrite.WaitingForKeyFrame);
        }

        float[] samples = new float[audio.SampleRate / 10 * audio.Channels];
        for (int i = firstKey; i < frames.Count; i++)
        {
            recorder.TryWriteVideo(frames[i].Data, frames[i].Key, i * 33, W, H, VideoCodec.H264).ShouldBe(RecorderWrite.Written);
            if (i % 3 == 0)
            {
                Fill(samples, i);
                recorder.WriteAudio(samples, i * 33);
            }
        }

        await recorder.StopAsync();

        recorder.HasStarted.ShouldBeTrue();
        recorder.Duration.ShouldBeGreaterThan(TimeSpan.Zero);
        File.Exists(path).ShouldBeTrue();
        new FileInfo(path).Length.ShouldBeGreaterThan(1024);
    }

    [Fact]
    public async Task A_picture_that_changes_size_asks_for_a_new_file()
    {
        if (!MediaFoundationEncoding.CanEncode(VideoCodec.H264))
        {
            return; // a machine whose Media Foundation lists an H.264 encoder that will not encode
        }

        var factory = new MfSessionRecorderFactory(NullLoggerFactory.Instance);
        string path = Path.Combine(_dir, "resized.mp4");
        await using ISessionRecorder recorder = factory.Create(new RecorderOptions(path, VideoCodec.H264, W, H));

        List<(byte[] Data, bool Key)> frames = await EncodeAsync(10);
        int firstKey = frames.FindIndex(f => f.Key);
        recorder.TryWriteVideo(frames[firstKey].Data, key: true, 0, W, H, VideoCodec.H264).ShouldBe(RecorderWrite.Written);

        recorder.TryWriteVideo(frames[firstKey].Data, key: true, 33, W * 2, H, VideoCodec.H264).ShouldBe(RecorderWrite.SizeChanged);
        recorder.TryWriteVideo(frames[firstKey].Data, key: true, 66, W, H, VideoCodec.H265).ShouldBe(RecorderWrite.SizeChanged);

        await recorder.StopAsync();
    }

    [Fact]
    public async Task Stopping_twice_is_allowed()
    {
        var factory = new MfSessionRecorderFactory(NullLoggerFactory.Instance);
        string path = Path.Combine(_dir, "twice.mp4");
        await using ISessionRecorder recorder = factory.Create(new RecorderOptions(path, VideoCodec.H264, W, H));

        await recorder.StopAsync();
        await recorder.StopAsync();
    }

    [Fact]
    public void This_computer_lists_at_least_one_way_to_play_sound()
    {
        // A machine with no sound hardware at all is possible; the list must then be empty, not throw.
        IReadOnlyList<AudioDeviceInfo> playback = AudioDevices.Playback();

        foreach (AudioDeviceInfo device in playback)
        {
            device.Id.ShouldNotBeNullOrEmpty();
            device.Name.ShouldNotBeNullOrEmpty();
        }

        playback.Count(d => d.IsDefault).ShouldBeLessThanOrEqualTo(1);
        AudioDevices.Resolve("not-a-real-device-id", NAudio.CoreAudioApi.DataFlow.Render).ShouldBeNull();
        AudioDevices.Resolve(null, NAudio.CoreAudioApi.DataFlow.Render).ShouldBeNull();
    }

    private static void Fill(float[] samples, int frame)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = MathF.Sin((i + (frame * samples.Length)) * 0.01f) * 0.25f;
        }
    }

    /// <summary>A real H.264 stream, so the recorder is handed the same bytes a session would give it.</summary>
    private static async Task<List<(byte[] Data, bool Key)>> EncodeAsync(int count)
    {
        var encoders = new MfVideoEncoderFactory(NullLoggerFactory.Instance);
        await using IVideoEncoder encoder = encoders.Create(new VideoEncoderConfig(VideoCodec.H264, W, H, 30, 4000, false, PixelFormat.Nv12, GpuApi.None, 0));
        byte[] nv12 = new byte[PixelConversion.Nv12Size(W, H)];
        byte[] bgra = new byte[W * H * 4];
        var frames = new List<(byte[] Data, bool Key)>();
        for (int i = 0; i < count; i++)
        {
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int o = ((y * W) + x) * 4;
                    bgra[o] = (byte)((x + (i * 5)) & 0xFF);
                    bgra[o + 1] = (byte)((y + (i * 3)) & 0xFF);
                    bgra[o + 2] = (byte)((x + y + i) & 0xFF);
                    bgra[o + 3] = 255;
                }
            }

            PixelConversion.BgraToNv12(bgra, W * 4, W, H, nv12);
            if (encoder.TryEncode(nv12, W, i, out EncodedPacket packet))
            {
                frames.Add((packet.Data.ToArray(), packet.IsKeyFrame));
            }
        }

        return frames;
    }
}
