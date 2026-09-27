using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Audio;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Platform.Abstractions.Recording;

namespace DeskPair.Desktop.Services;

/// <summary>
/// Turns a session into files: names them, waits for the keyframe a file has to start on, and rolls over to a
/// new file when the remote picture changes size (a container cannot change size halfway through). The writing
/// itself is the platform recorder's job.
/// </summary>
public sealed class SessionRecordingController : IAsyncDisposable
{
    private readonly ISessionRecorderFactory _factory;
    private readonly string _folder;
    private readonly string _target;
    private readonly bool _recordAudio;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private ISessionRecorder? _recorder;
    private AudioStreamFormat? _audioFormat;
    private string _baseName = string.Empty;
    private int _fileIndex;

    public SessionRecordingController(ISessionRecorderFactory factory, string folder, string target, bool recordAudio, ILogger log, TimeProvider? time = null)
    {
        _factory = factory;
        _folder = folder.Length > 0 ? folder : DefaultFolder;
        _target = target;
        _recordAudio = recordAudio;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Sunllo DeskPair");

    public bool IsActive { get; private set; }

    /// <summary>False between pressing record and the first keyframe arriving.</summary>
    public bool HasStarted => _recorder?.HasStarted ?? false;

    public string? LastPath { get; private set; }

    public TimeSpan Duration => _recorder?.Duration ?? TimeSpan.Zero;

    public long Bytes => _recorder?.BytesWritten ?? 0;

    /// <summary>Files written so far; more than one means the picture changed size during the recording.</summary>
    public int FileCount { get; private set; }

    /// <summary>Raised when recording had to stop by itself, with a message for the user.</summary>
    public event Action<string>? Failed;

    public void SetAudioFormat(AudioStreamFormat format) => _audioFormat = format;

    /// <summary>Starts recording; false when this platform cannot record, or cannot record this codec.</summary>
    public bool Start(VideoCodec codec)
    {
        if (IsActive || !_factory.IsSupported || !_factory.Supports(codec))
        {
            return false;
        }

        _baseName = $"{Sanitise(_target)}_{_time.GetLocalNow():yyyyMMdd-HHmmss}";
        _fileIndex = 0;
        FileCount = 0;
        IsActive = true;
        return true;
    }

    public void WriteVideo(ReadOnlySpan<byte> frame, bool key, long ptsMs, int width, int height, VideoCodec codec)
    {
        if (!IsActive)
        {
            return;
        }

        lock (_lock)
        {
            _recorder ??= Open(codec, width, height);
            if (_recorder is null)
            {
                return;
            }

            switch (_recorder.TryWriteVideo(frame, key, ptsMs, width, height, codec))
            {
                case RecorderWrite.SizeChanged:
                    // Finish this file and continue in the next one; the caller asks for a keyframe.
                    Roll(codec, width, height);
                    _recorder?.TryWriteVideo(frame, key, ptsMs, width, height, codec);
                    break;
                case RecorderWrite.Faulted:
                    Stop("recorder faulted");
                    break;
            }
        }
    }

    public void WriteAudio(ReadOnlySpan<float> pcm, long ptsMs)
    {
        if (!IsActive || !_recordAudio)
        {
            return;
        }

        lock (_lock)
        {
            _recorder?.WriteAudio(pcm, ptsMs);
        }
    }

    public async Task StopAsync()
    {
        ISessionRecorder? recorder;
        lock (_lock)
        {
            IsActive = false;
            recorder = _recorder;
            _recorder = null;
        }

        if (recorder is not null)
        {
            await recorder.StopAsync().ConfigureAwait(false);
            LastPath = recorder.Path;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private ISessionRecorder? Open(VideoCodec codec, int width, int height)
    {
        try
        {
            _fileIndex++;
            string name = _fileIndex == 1 ? $"{_baseName}.mp4" : $"{_baseName}_{_fileIndex}.mp4";
            var options = new RecorderOptions(Path.Combine(_folder, name), codec, width, height)
            {
                Audio = _recordAudio ? _audioFormat : null,
            };
            FileCount++;
            LastPath = options.Path;
            return _factory.Create(options);
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Could not start recording");
            Stop(e.Message);
            return null;
        }
    }

    private void Roll(VideoCodec codec, int width, int height)
    {
        ISessionRecorder? previous = _recorder;
        _recorder = null;
        _ = previous?.StopAsync().AsTask();
        _recorder = Open(codec, width, height);
    }

    private void Stop(string reason)
    {
        IsActive = false;
        ISessionRecorder? recorder = _recorder;
        _recorder = null;
        _ = recorder?.StopAsync().AsTask();
        Failed?.Invoke(reason);
    }

    private static string Sanitise(string target)
    {
        Span<char> chars = stackalloc char[Math.Min(target.Length, 40)];
        int n = 0;
        foreach (char c in target)
        {
            if (n == chars.Length)
            {
                break;
            }

            chars[n++] = Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || c == ' ' ? '_' : c;
        }

        return n == 0 ? "session" : new string(chars[..n]);
    }
}
