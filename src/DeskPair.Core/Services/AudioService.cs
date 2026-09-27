using Google.Protobuf;
using Microsoft.Extensions.Logging;
using DeskPair.Core.Audio;
using DeskPair.Core.Session;
using DeskPair.Platform.Abstractions.Audio;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Services;

/// <summary>Encodes captured system audio as Opus and publishes 10 ms frames; silence is suppressed after a short tail.</summary>
public sealed class AudioService : PublisherService
{
    private const int SilentPacketsBeforeMute = 300; // 3 s

    private readonly Func<IAudioCapture> _captureFactory;
    private readonly TimeProvider _time;
    private AudioStreamFormat _format;

    public AudioService(Func<IAudioCapture> captureFactory, TimeProvider time, ILogger log)
        : base("audio", log)
    {
        _captureFactory = captureFactory;
        _time = time;
    }

    public long FramesSent { get; private set; }

    protected override async ValueTask OnSubscribedAsync(IServiceSubscriber subscriber, CancellationToken ct)
    {
        if (_format.SampleRate > 0)
        {
            await subscriber.PublishAsync(FormatMessage(_format), MessagePriority.Control, ct).ConfigureAwait(false);
        }
    }

    protected override async Task RunAsync(CancellationToken ct)
    {
        await using IAudioCapture capture = _captureFactory();
        _format = capture.Format;
        using var encoder = new OpusEncoderWrapper(_format);
        await BroadcastAsync(FormatMessage(_format), MessagePriority.Control, ct).ConfigureAwait(false);
        await capture.StartAsync(ct).ConfigureAwait(false);
        Log.LogInformation("{Service}: {Rate} Hz, {Channels} ch", Name, _format.SampleRate, _format.Channels);

        int silent = 0;
        int wrongLength = 0;
        await foreach (ReadOnlyMemory<float> pcm in capture.Frames.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (pcm.Length != encoder.FrameSamples * _format.Channels)
            {
                // A capture that does not honour the 10 ms contract used to disappear here without a word,
                // which reads from the outside as a host whose sound simply does not work: the service
                // starts, announces its format and then sends nothing at all. Said once, because if it is
                // wrong it is wrong for every packet.
                if (wrongLength++ == 0)
                {
                    Log.LogWarning(
                        "{Service}: dropping capture packets of {Got} samples; this capture must deliver {Want} (10 ms interleaved), and nothing will be sent until it does",
                        Name,
                        pcm.Length,
                        encoder.FrameSamples * _format.Channels);
                }

                continue;
            }

            if (IsSilent(pcm.Span))
            {
                if (++silent > SilentPacketsBeforeMute)
                {
                    continue;
                }
            }
            else
            {
                silent = 0;
            }

            ReadOnlySpan<byte> packet = encoder.Encode(pcm.Span);
            var msg = new Message { AudioFrame = new AudioFrame { Opus = ByteString.CopyFrom(packet), PtsMs = _time.GetUtcNow().ToUnixTimeMilliseconds() } };
            await BroadcastAsync(msg, MessagePriority.Input, ct).ConfigureAwait(false);
            FramesSent++;
        }
    }

    private static bool IsSilent(ReadOnlySpan<float> pcm)
    {
        foreach (float s in pcm)
        {
            if (Math.Abs(s) > 1e-4f)
            {
                return false;
            }
        }

        return true;
    }

    private static Message FormatMessage(AudioStreamFormat f) =>
        new() { Misc = new Misc { AudioFormat = new AudioFormat { SampleRate = (uint)f.SampleRate, Channels = (uint)f.Channels } } };
}
