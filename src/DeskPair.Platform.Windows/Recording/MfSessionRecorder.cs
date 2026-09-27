using System.Buffers;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Platform.Abstractions.Recording;
using DeskPair.Platform.Windows.Codec;
using Vortice.MediaFoundation;

namespace DeskPair.Platform.Windows.Recording;

/// <summary>Records a session to MP4 with Media Foundation; Windows is the only platform that can today.</summary>
[SupportedOSPlatform("windows")]
public sealed class MfSessionRecorderFactory(ILoggerFactory logs) : ISessionRecorderFactory
{
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// MP4 stores H.264 and H.265 natively, so both record without re-encoding. AV1 in MP4 is a different
    /// container box that the Media Foundation MP4 sink does not write, so an AV1 session cannot be recorded
    /// this way and says so up front rather than opening a file and rejecting every frame.
    /// </summary>
    public bool Supports(VideoCodec codec) => codec is VideoCodec.H264 or VideoCodec.H265;

    public ISessionRecorder Create(RecorderOptions options) => new MfSessionRecorder(options, logs.CreateLogger<MfSessionRecorder>());
}

/// <summary>
/// Writes the received video straight into an MP4 (no re-encoding) and the decoded audio as AAC. Callers hand
/// frames over on the decode thread, so everything here is a copy onto a queue; one background thread owns the
/// sink writer. Timestamps are rebased on the first keyframe, and each frame's length is known only when the
/// next one arrives, so one frame is always held back.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MfSessionRecorder : ISessionRecorder
{
    private const long HnsPerMs = 10_000;
    private const long MaxHeldFrameHns = 10_000_000; // a still desktop sends nothing; do not stretch a frame past a second
    private static readonly TimeSpan AudioGapThreshold = TimeSpan.FromMilliseconds(200);

    private readonly RecorderOptions _options;
    private readonly ILogger _log;
    private readonly Channel<Item> _queue = Channel.CreateBounded<Item>(new BoundedChannelOptions(240)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite,
    });

    private readonly Task _worker;
    private IMFSinkWriter? _writer;
    private int _videoStream = -1;
    private int _audioStream = -1;
    private long _baseMs = -1;
    private long _lastVideoHns = -1;
    private Item? _held;
    private long _audioSamplesWritten;
    private long _lastAudioEndHns;
    private bool _faulted;
    private bool _stopped;

    public MfSessionRecorder(RecorderOptions options, ILogger log)
    {
        _options = options;
        _log = log;
        Path = options.Path;
        _worker = Task.Factory.StartNew(WriteLoop, TaskCreationOptions.LongRunning);
    }

    public string Path { get; }

    public bool HasStarted { get; private set; }

    public TimeSpan Duration => TimeSpan.FromTicks(Math.Max(0, _lastVideoHns));

    public long BytesWritten { get; private set; }

    public long DroppedFrames { get; private set; }

    public RecorderWrite TryWriteVideo(ReadOnlySpan<byte> frame, bool key, long ptsMs, int width, int height, VideoCodec codec)
    {
        if (_faulted || _stopped)
        {
            return RecorderWrite.Faulted;
        }

        if (codec != _options.Codec || width != _options.Width || height != _options.Height)
        {
            return RecorderWrite.SizeChanged;
        }

        if (!HasStarted && !key)
        {
            return RecorderWrite.WaitingForKeyFrame;
        }

        HasStarted = true;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(frame.Length);
        frame.CopyTo(buffer);
        if (_queue.Writer.TryWrite(new Item(buffer, frame.Length, key, ptsMs, IsAudio: false)))
        {
            return RecorderWrite.Written;
        }

        // The writer is behind; drop this one and let the next keyframe resynchronise the file.
        ArrayPool<byte>.Shared.Return(buffer);
        DroppedFrames++;
        return RecorderWrite.Written;
    }

    public void WriteAudio(ReadOnlySpan<float> interleaved, long ptsMs)
    {
        if (_faulted || _stopped || _audioStream < 0 || !HasStarted || interleaved.IsEmpty)
        {
            return; // audio before the first keyframe would land before the file's zero point
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(interleaved.Length * sizeof(short));
        Span<short> pcm = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, interleaved.Length * sizeof(short)));
        for (int i = 0; i < interleaved.Length; i++)
        {
            pcm[i] = (short)Math.Clamp((int)(interleaved[i] * short.MaxValue), short.MinValue, short.MaxValue);
        }

        if (!_queue.Writer.TryWrite(new Item(buffer, interleaved.Length * sizeof(short), Key: false, ptsMs, IsAudio: true)))
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask StopAsync()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        _queue.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void WriteLoop()
    {
        MediaFoundation.AddRef();
        try
        {
            OpenWriter();
            while (_queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (_queue.Reader.TryRead(out Item item))
                {
                    try
                    {
                        if (item.IsAudio)
                        {
                            WriteAudioItem(item);
                        }
                        else
                        {
                            WriteVideoItem(item);
                        }
                    }
                    catch (Exception e)
                    {
                        Fault(e);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(item.Buffer);
                    }
                }
            }

            FlushHeld();
            _writer?.Finalize();
        }
        catch (Exception e)
        {
            Fault(e);
        }
        finally
        {
            _writer?.Dispose();
            _writer = null;
            MediaFoundation.Release();
        }
    }

    private void OpenWriter()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using IMFAttributes attributes = MediaFactory.MFCreateAttributes(2);
        attributes.Set(MediaFoundation.TranscodeContainerType, MediaFoundation.ContainerTypeMpeg4);
        _writer = MediaFactory.MFCreateSinkWriterFromURL(Path, null, attributes);

        using (IMFMediaType video = MediaFactory.MFCreateMediaType())
        {
            video.Set(MediaFoundation.MtMajorType, MediaFoundation.MediaTypeVideo);
            video.Set(MediaFoundation.MtSubtype, MediaFoundation.SubtypeOf(_options.Codec)!.Value);
            video.Set(MediaFoundation.MtFrameSize, MediaFoundation.Pack((uint)_options.Width, (uint)_options.Height));
            video.Set(MediaFoundation.MtFrameRate, MediaFoundation.Pack((uint)Math.Max(1, _options.Fps), 1));
            video.Set(MediaFoundation.MtInterlaceMode, MediaFoundation.InterlaceProgressive);
            video.Set(MediaFoundation.MtPixelAspectRatio, MediaFoundation.Pack(1, 1));
            if (MediaFoundation.ProfileOf(_options.Codec) is { } profile)
            {
                video.Set(MediaFoundation.MtMpeg2Profile, profile);
            }

            _videoStream = _writer.AddStream(video);

            // The same type in and out means "store these samples as they are": no decoding, no encoding.
            _writer.SetInputMediaType(_videoStream, video, null);
        }

        if (_options.Audio is { } format)
        {
            try
            {
                using IMFMediaType output = MediaFactory.MFCreateMediaType();
                output.Set(MediaFoundation.MtMajorType, MediaFoundation.MediaTypeAudio);
                output.Set(MediaFoundation.MtSubtype, MediaFoundation.FormatAac);
                output.Set(MediaFoundation.MtAudioSamplesPerSecond, (uint)format.SampleRate);
                output.Set(MediaFoundation.MtAudioNumChannels, (uint)format.Channels);
                output.Set(MediaFoundation.MtAudioBitsPerSample, 16u);
                output.Set(MediaFoundation.MtAudioAvgBytesPerSecond, (uint)(_options.AudioBitrateBps / 8));
                _audioStream = _writer.AddStream(output);

                using IMFMediaType input = MediaFactory.MFCreateMediaType();
                input.Set(MediaFoundation.MtMajorType, MediaFoundation.MediaTypeAudio);
                input.Set(MediaFoundation.MtSubtype, MediaFoundation.FormatPcm);
                input.Set(MediaFoundation.MtAudioSamplesPerSecond, (uint)format.SampleRate);
                input.Set(MediaFoundation.MtAudioNumChannels, (uint)format.Channels);
                input.Set(MediaFoundation.MtAudioBitsPerSample, 16u);
                input.Set(MediaFoundation.MtAudioBlockAlignment, (uint)(2 * format.Channels));
                input.Set(MediaFoundation.MtAudioAvgBytesPerSecond, (uint)(format.SampleRate * 2 * format.Channels));
                _writer.SetInputMediaType(_audioStream, input, null);
            }
            catch (Exception e)
            {
                // An exotic rate or channel count: keep the picture rather than losing the whole recording.
                _log.LogWarning(e, "Recording without audio: the encoder refused {Rate} Hz / {Channels} ch", format.SampleRate, format.Channels);
                _audioStream = -1;
            }
        }

        _writer.BeginWriting();
    }

    private void WriteVideoItem(Item item)
    {
        if (_writer is null)
        {
            return;
        }

        _baseMs = _baseMs < 0 ? item.PtsMs : _baseMs;
        long hns = Math.Max((item.PtsMs - _baseMs) * HnsPerMs, _lastVideoHns + 1);
        if (_held is { } previous)
        {
            long duration = Math.Clamp(hns - previous.Hns, HnsPerMs, MaxHeldFrameHns);
            Emit(previous, duration);
        }

        _held = item with { Hns = hns, Buffer = Copy(item) };
    }

    private void FlushHeld()
    {
        if (_held is { } last)
        {
            Emit(last, HnsPerMs * 33);
            _held = null;
        }
    }

    private void Emit(Item item, long durationHns)
    {
        using IMFSample sample = MediaFoundation.CreateSample(item.Buffer.AsSpan(0, item.Length), item.Hns, durationHns);
        if (item.Key)
        {
            sample.Set(MediaFoundation.SampleCleanPoint, 1u);
        }

        _writer!.WriteSample(_videoStream, sample);
        BytesWritten += item.Length;
        _lastVideoHns = item.Hns + durationHns;
        ArrayPool<byte>.Shared.Return(item.Buffer);
    }

    private void WriteAudioItem(Item item)
    {
        if (_writer is null || _audioStream < 0 || _options.Audio is not { } format)
        {
            return;
        }

        int samples = item.Length / (2 * format.Channels);
        long hns = _audioSamplesWritten * 10_000_000 / format.SampleRate;
        if (_baseMs >= 0 && item.PtsMs > 0)
        {
            long fromPts = (item.PtsMs - _baseMs) * HnsPerMs;
            // The host stops sending while the desk is silent; mark the hole instead of sliding the sound forward.
            if (fromPts - _lastAudioEndHns > AudioGapThreshold.Ticks)
            {
                _writer.SendStreamTick(_audioStream, fromPts);
                hns = fromPts;
                _audioSamplesWritten = fromPts * format.SampleRate / 10_000_000;
            }
        }

        using IMFSample sample = MediaFoundation.CreateSample(item.Buffer.AsSpan(0, item.Length), hns, samples * 10_000_000L / format.SampleRate);
        _writer.WriteSample(_audioStream, sample);
        _audioSamplesWritten += samples;
        _lastAudioEndHns = hns + (samples * 10_000_000L / format.SampleRate);
    }

    private static byte[] Copy(Item item)
    {
        byte[] copy = ArrayPool<byte>.Shared.Rent(item.Length);
        item.Buffer.AsSpan(0, item.Length).CopyTo(copy);
        return copy;
    }

    private void Fault(Exception e)
    {
        if (_faulted)
        {
            return;
        }

        _faulted = true;
        _log.LogWarning(e, "Recording to {Path} stopped", Path);
    }

    private readonly record struct Item(byte[] Buffer, int Length, bool Key, long PtsMs, bool IsAudio)
    {
        public long Hns { get; init; }
    }
}
