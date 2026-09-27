using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Core.Services;
using DeskPair.Core.Session;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Tests;

public class PublisherServiceTests
{
    private sealed class CountingService() : PublisherService("counting", NullLogger.Instance)
    {
        public int Starts;
        public int Stops;
        public int Snapshots;
        public int Ticks;

        protected override async Task RunAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Starts);
            try
            {
                while (true)
                {
                    await Task.Delay(5, ct);
                    Interlocked.Increment(ref Ticks);
                    await BroadcastAsync(new Message { Misc = new Misc { Chat = new ChatMessage { Text = "tick" } } }, MessagePriority.Control, ct);
                }
            }
            finally
            {
                Interlocked.Increment(ref Stops);
            }
        }

        protected override ValueTask OnSubscribedAsync(IServiceSubscriber subscriber, CancellationToken ct)
        {
            Interlocked.Increment(ref Snapshots);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Sub(int id) : IServiceSubscriber
    {
        public int Received;
        public int VideoQueued;
        public bool DropVideo { get; set; }

        public int ConnectionId => id;

        public ValueTask PublishAsync(Message message, MessagePriority priority, CancellationToken ct)
        {
            Interlocked.Increment(ref Received);
            return ValueTask.CompletedTask;
        }

        public bool TryPublishVideo(Message frame)
        {
            Interlocked.Increment(ref VideoQueued);
            return !DropVideo;
        }
    }

    [Fact]
    public async Task Loop_runs_only_while_there_are_subscribers()
    {
        await using var svc = new CountingService();
        svc.IsRunning.ShouldBeFalse();

        var a = new Sub(1);
        svc.Subscribe(a);
        await WaitAsync(() => a.Received >= 3);
        svc.IsRunning.ShouldBeTrue();
        svc.Snapshots.ShouldBe(1);

        var b = new Sub(2);
        svc.Subscribe(b);
        await WaitAsync(() => b.Received >= 3);
        svc.Starts.ShouldBe(1);

        await svc.UnsubscribeAsync(1);
        svc.IsRunning.ShouldBeTrue();
        await svc.UnsubscribeAsync(2);
        svc.IsRunning.ShouldBeFalse();
        svc.Stops.ShouldBe(1);

        int before = a.Received;
        await Task.Delay(30);
        a.Received.ShouldBe(before);

        svc.Subscribe(a);
        await WaitAsync(() => svc.Starts == 2);
    }

    [Fact]
    public async Task Unsubscribing_an_unknown_id_is_harmless()
    {
        await using var svc = new CountingService();
        await svc.UnsubscribeAsync(42);
        svc.IsRunning.ShouldBeFalse();
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException();
            }

            await Task.Delay(5);
        }
    }
}
