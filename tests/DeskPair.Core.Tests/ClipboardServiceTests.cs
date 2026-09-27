using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using DeskPair.Core.Services;
using DeskPair.Core.Session;
using DeskPair.Core.Testing;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Tests;

/// <summary>
/// What a session is told about the host's clipboard, and — more to the point — what it is not.
///
/// The platform clipboards poll all the time, into a channel nothing drains while no one is connected. The
/// publisher loop only runs while somebody is subscribed, so everything copied in between is still sitting
/// there when the next session arrives. Replaying it would hand a viewer the last several things the host's
/// user copied, and leave them holding the oldest of them as if it were current.
/// </summary>
public class ClipboardServiceTests
{
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

        public IReadOnlyList<string> Texts
        {
            get
            {
                lock (Messages)
                {
                    return Messages
                        .Where(m => m.UnionCase == Message.UnionOneofCase.Clipboard)
                        .SelectMany(m => m.Clipboard.Items)
                        .Where(i => i.Format == ClipboardFormat.CfText)
                        .Select(i => i.Content.ToStringUtf8())
                        .ToList();
                }
            }
        }
    }

    [Fact]
    public async Task What_was_copied_before_anyone_connected_is_not_replayed()
    {
        var clipboard = new FakeClipboard();
        await using var service = new ClipboardService(clipboard, new FakeTimeProvider(), NullLogger.Instance);

        // Nobody is subscribed, so nothing drains the channel.
        clipboard.SetText("a bank password");
        clipboard.SetText("a private note");
        clipboard.SetText("what is on the clipboard now");

        var sub = new Sub(1);
        service.Subscribe(sub);

        await WaitAsync(() => sub.Texts.Count >= 1);
        await Task.Delay(100);

        // The current content still goes out — a viewer that connects should be able to paste what the
        // host is holding. The two before it were never this session's business.
        sub.Texts.ShouldBe(["what is on the clipboard now"]);
    }

    [Fact]
    public async Task A_copy_made_while_connected_still_arrives()
    {
        var clipboard = new FakeClipboard();
        await using var service = new ClipboardService(clipboard, new FakeTimeProvider(), NullLogger.Instance);

        clipboard.SetText("before");
        var sub = new Sub(1);
        service.Subscribe(sub);
        await WaitAsync(() => sub.Texts.Count >= 1);

        // Draining the backlog must not eat what happens next, which is the whole point of the service.
        clipboard.SetText("after");
        await WaitAsync(() => sub.Texts.Contains("after"));
    }

    /// <summary>
    /// Waits for the publisher.
    /// </summary>
    /// <remarks>
    /// This used to carry a long note calling the test a known flake, with the race set out and left
    /// alone. The race was real, and it was in the product rather than the test: the run loop threw away
    /// its backlog as its first act, on a pool thread, so a copy made in the instant somebody connected
    /// could reach the channel first and go out with it. It is fixed -- the discard happens at subscribe
    /// time now, before the loop exists -- so this is a plain wait again, and a failure here means
    /// something.
    /// </remarks>
    private static async Task WaitAsync(Func<bool> until)
    {
        for (int i = 0; i < 200 && !until(); i++)
        {
            await Task.Delay(5);
        }

        until().ShouldBeTrue("the service never published what was expected");
    }
}
