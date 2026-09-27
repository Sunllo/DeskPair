using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using DeskPair.Core.Services;
using DeskPair.Core.Session;
using DeskPair.Platform.Abstractions.Cursor;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Tests;

/// <summary>
/// A viewer that does not draw the remote pointer still wears its shape — that is what replaced compositing
/// the cursor into the picture — so switching the overlay off must drop the positions and nothing else.
/// </summary>
public class CursorServiceTests
{
    private sealed class FakeCursor : ICursorProvider
    {
        public ulong Id = 7;
        public (int X, int Y)? Position = (10, 10);

        public ulong GetCurrentCursorId() => Id;

        public CursorImage? GetCursorImage(ulong id) => new(id, 1, 2, 2, 2, new byte[2 * 2 * 4]);

        public (int X, int Y)? GetCursorPosition() => Position;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Sub(int id) : IServiceSubscriber
    {
        public readonly List<Message> Messages = [];

        public int ConnectionId => id;

        public ValueTask PublishAsync(Message message, MessagePriority priority, CancellationToken ct)
        {
            lock (Messages)
            {
                Messages.Add(message);
            }

            return ValueTask.CompletedTask;
        }

        public bool TryPublishVideo(Message frame) => true;

        public int Count(Message.UnionOneofCase kind)
        {
            lock (Messages)
            {
                return Messages.Count(m => m.UnionCase == kind);
            }
        }
    }

    private static async Task<(CursorService Service, FakeCursor Cursor, FakeTimeProvider Time)> StartAsync()
    {
        var cursor = new FakeCursor();
        var time = new FakeTimeProvider();
        var service = new CursorService(cursor, time, NullLogger.Instance);
        await Task.Yield();
        return (service, cursor, time);
    }

    [Fact]
    public async Task A_viewer_that_wants_no_positions_still_gets_the_shape()
    {
        (CursorService service, FakeCursor cursor, FakeTimeProvider time) = await StartAsync();
        await using (service)
        {
            var sub = new Sub(1);
            service.SetWantsPosition(1, wants: false);
            service.Subscribe(sub);
            await WaitAsync(() => sub.Count(Message.UnionOneofCase.CursorData) == 1);

            cursor.Position = (99, 99);
            cursor.Id = 8;
            await AdvanceAsync(time, sub, () => sub.Count(Message.UnionOneofCase.CursorData) == 2);

            sub.Count(Message.UnionOneofCase.CursorPosition).ShouldBe(0);
        }
    }

    [Fact]
    public async Task A_viewer_that_wants_positions_gets_them()
    {
        (CursorService service, FakeCursor cursor, FakeTimeProvider time) = await StartAsync();
        await using (service)
        {
            var sub = new Sub(1);
            service.Subscribe(sub);
            await WaitAsync(() => sub.Count(Message.UnionOneofCase.CursorPosition) == 1);

            cursor.Position = (99, 99);
            await AdvanceAsync(time, sub, () => sub.Count(Message.UnionOneofCase.CursorPosition) == 2);
        }
    }

    /// <summary>Turning the overlay back on must not leave the viewer pointerless until the mouse next moves.</summary>
    [Fact]
    public async Task Asking_for_positions_again_sends_one_without_waiting_for_a_move()
    {
        (CursorService service, FakeCursor cursor, FakeTimeProvider time) = await StartAsync();
        await using (service)
        {
            var sub = new Sub(1);
            service.SetWantsPosition(1, wants: false);
            service.Subscribe(sub);
            await WaitAsync(() => sub.Count(Message.UnionOneofCase.CursorData) == 1);

            service.SetWantsPosition(1, wants: true);
            await AdvanceAsync(time, sub, () => sub.Count(Message.UnionOneofCase.CursorPosition) == 1);

            _ = cursor;
        }
    }

    private static async Task AdvanceAsync(FakeTimeProvider time, Sub sub, Func<bool> until)
    {
        for (int i = 0; i < 100 && !until(); i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(33));
            await Task.Delay(5);
        }

        until().ShouldBeTrue($"the service never published what was expected; saw {sub.Messages.Count} messages");
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
