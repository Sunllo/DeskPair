using System.Collections.Concurrent;
using DeskPair.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace DeskPair.Core.Tests;

/// <summary>Display changes viewers ask for: one at a time, away from the session, the last word counting.</summary>
public sealed class DisplayChangeQueueTests
{
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(500);

    private readonly FakeTimeProvider _time = new();
    private readonly ConcurrentQueue<string> _ran = new();

    private Func<CancellationToken, Task> Record(string name, Task? until = null) => async _ =>
    {
        _ran.Enqueue(name);
        if (until is not null)
        {
            await until;
        }
    };

    private static async Task EventuallyAsync(Func<bool> condition, string what)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        condition().ShouldBeTrue(what);
    }

    /// <summary>Lets the pause after each change pass until <paramref name="condition"/> holds.</summary>
    private async Task PassPausesUntilAsync(Func<bool> condition, string what)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            _time.Advance(Gap);
            await Task.Delay(10);
        }

        condition().ShouldBeTrue(what);
    }

    [Fact]
    public async Task Changes_run_one_at_a_time_with_a_pause_after_each()
    {
        await using var queue = new DisplayChangeQueue(_time, NullLogger.Instance, Gap);
        var first = new TaskCompletionSource();

        queue.Enqueue(1, "A", Record("A", first.Task));
        queue.Enqueue(1, "B", Record("B"));
        await EventuallyAsync(() => _ran.Count == 1, "the first change started");
        await Task.Delay(50);
        _ran.ToArray().ShouldBe(["A"], customMessage: "the second waits for the first");

        first.SetResult();
        await Task.Delay(50);
        _ran.Count.ShouldBe(1, "and then for the pause");
        await PassPausesUntilAsync(() => _ran.Count == 2, "the second ran after the pause");
        _ran.ToArray().ShouldBe(["A", "B"]);
    }

    /// <summary>A window being resized asks at every pause; only the last size is worth making.</summary>
    [Fact]
    public async Task A_later_request_for_the_same_display_replaces_one_not_yet_started()
    {
        await using var queue = new DisplayChangeQueue(_time, NullLogger.Instance, Gap);
        var busy = new TaskCompletionSource();
        queue.Enqueue(1, "X", Record("X", busy.Task));
        await EventuallyAsync(() => _ran.Count == 1, "busy");

        queue.Enqueue(1, "A", Record("A1"));
        queue.Enqueue(2, "B", Record("B"));
        queue.Enqueue(2, "A", Record("A2")); // another viewer's word on the same display is the last one
        queue.Enqueue(1, "A", Record("A3"));
        queue.Pending.ShouldBe(2);

        busy.SetResult();
        await PassPausesUntilAsync(() => _ran.Count == 3, "the rest ran");
        _ran.ToArray().ShouldBe(["X", "A3", "B"], customMessage: "the replacement keeps the place of what it replaced");
    }

    [Fact]
    public async Task What_a_departed_session_asked_for_and_did_not_start_is_dropped()
    {
        await using var queue = new DisplayChangeQueue(_time, NullLogger.Instance, Gap);
        var busy = new TaskCompletionSource();
        queue.Enqueue(1, "X", Record("X", busy.Task));
        await EventuallyAsync(() => _ran.Count == 1, "busy");
        queue.Enqueue(2, "A", Record("A"));
        queue.Enqueue(3, null, Record("plug in"));

        queue.Drop(2);
        busy.SetResult();

        await PassPausesUntilAsync(() => _ran.Count == 2, "the others ran");
        _ran.ToArray().ShouldBe(["X", "plug in"]);
    }

    /// <summary>The last viewer leaving: nothing more starts, and the change under way lands before the screen is put back.</summary>
    [Fact]
    public async Task Quiescing_waits_for_the_change_under_way_and_forgets_the_rest()
    {
        await using var queue = new DisplayChangeQueue(_time, NullLogger.Instance, Gap);
        var busy = new TaskCompletionSource();
        queue.Enqueue(1, "X", Record("X", busy.Task));
        await EventuallyAsync(() => _ran.Count == 1, "busy");
        queue.Enqueue(1, "A", Record("A"));

        Task quiet = queue.QuiesceAsync();
        await Task.Delay(50);
        quiet.IsCompleted.ShouldBeFalse("a change is still under way");

        busy.SetResult();
        await PassPausesUntilAsync(() => quiet.IsCompleted, "quiet once it has landed");
        _ran.ToArray().ShouldBe(["X"]);
    }

    [Fact]
    public async Task A_change_that_fails_does_not_stop_the_ones_after_it()
    {
        await using var queue = new DisplayChangeQueue(_time, NullLogger.Instance, Gap);
        queue.Enqueue(1, "A", _ => throw new InvalidOperationException("the display said no"));
        queue.Enqueue(1, "B", Record("B"));

        await PassPausesUntilAsync(() => _ran.Count == 1, "the next one ran");
    }

    [Fact]
    public async Task Nothing_is_taken_once_the_queue_is_disposed()
    {
        var queue = new DisplayChangeQueue(_time, NullLogger.Instance, Gap);
        await queue.DisposeAsync();

        queue.Enqueue(1, "A", Record("A"));
        await Task.Delay(50);
        _ran.ShouldBeEmpty();
    }
}
