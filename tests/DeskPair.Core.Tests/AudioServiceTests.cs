using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Core.Services;
using DeskPair.Core.Session;
using DeskPair.Platform.Abstractions.Audio;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Tests;

/// <summary>
/// The service encodes 10 ms frames and nothing else, because that is the one frame size the Opus encoder is
/// configured for. A capture that hands it anything shorter has its packets dropped — which is correct, and
/// was also completely silent, so a platform whose capture published partial reads produced a host that
/// announced its audio format and then never sent a sound. It has to say so.
/// </summary>
public class AudioServiceTests
{
    private sealed class ScriptedCapture(int samplesPerPacket) : IAudioCapture
    {
        private readonly Channel<ReadOnlyMemory<float>> _frames = Channel.CreateUnbounded<ReadOnlyMemory<float>>();

        public AudioStreamFormat Format { get; } = new(48000, 2);

        public ChannelReader<ReadOnlyMemory<float>> Frames => _frames.Reader;

        public ValueTask StartAsync(CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask StopAsync() => ValueTask.CompletedTask;

        /// <summary>A tone rather than silence: the service stops sending silence after a few seconds.</summary>
        public void Emit(int packets)
        {
            for (int p = 0; p < packets; p++)
            {
                float[] pcm = new float[samplesPerPacket];
                for (int i = 0; i < pcm.Length; i++)
                {
                    pcm[i] = (float)Math.Sin(2 * Math.PI * 440 * ((p * pcm.Length) + i) / 48000.0) * 0.2f;
                }

                _frames.Writer.TryWrite(pcm);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Sub(int id) : IServiceSubscriber
    {
        private readonly List<Message> _messages = [];

        public int ConnectionId => id;

        public ValueTask PublishAsync(Message message, MessagePriority priority, CancellationToken ct)
        {
            lock (_messages)
            {
                _messages.Add(message);
            }

            return ValueTask.CompletedTask;
        }

        public bool TryPublishVideo(Message frame) => true;

        public int AudioFrames
        {
            get
            {
                lock (_messages)
                {
                    return _messages.Count(m => m.UnionCase == Message.UnionOneofCase.AudioFrame);
                }
            }
        }
    }

    [Fact]
    public async Task Ten_millisecond_packets_are_encoded_and_sent()
    {
        // 480 samples per channel at 48 kHz, interleaved across two channels.
        var capture = new ScriptedCapture(480 * 2);
        await using var service = new AudioService(() => capture, TimeProvider.System, NullLogger.Instance);

        var sub = new Sub(1);
        service.Subscribe(sub);
        await Task.Delay(50);
        capture.Emit(20);

        await WaitAsync(() => sub.AudioFrames >= 20);
    }

    [Fact]
    public async Task A_capture_that_ignores_the_frame_size_sends_nothing()
    {
        // What a partial read looks like: the packets are the right shape but the wrong length.
        var capture = new ScriptedCapture(200);
        await using var service = new AudioService(() => capture, TimeProvider.System, NullLogger.Instance);

        var sub = new Sub(1);
        service.Subscribe(sub);
        await Task.Delay(50);
        capture.Emit(50);
        await Task.Delay(200);

        sub.AudioFrames.ShouldBe(0);
        service.FramesSent.ShouldBe(0);
    }

    private static async Task WaitAsync(Func<bool> until)
    {
        for (int i = 0; i < 200 && !until(); i++)
        {
            await Task.Delay(5);
        }

        until().ShouldBeTrue("the service never published what was expected");
    }
}
