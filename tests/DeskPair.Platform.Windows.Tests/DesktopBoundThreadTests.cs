using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Platform.Windows.Input;

namespace DeskPair.Platform.Windows.Tests;

/// <summary>
/// Input injection is bound to a thread, not to a process: SendInput reaches the desktop the calling
/// thread is attached to, and SetThreadDesktop attaches exactly one thread.
///
/// That was being done on whichever thread the message arrived on, which is a thread-pool thread -- so
/// the next keystroke could run on a different one, still attached to the ordinary desktop, and the ones
/// that had been switched carried an attachment to the lock screen's desktop into whatever unrelated work
/// the pool handed them next. Typing into a locked machine would have worked intermittently, which is the
/// worst way for it to fail.
///
/// What these pin is the property that makes it reliable: one thread, never the caller's, the same one
/// every time.
/// </summary>
public class DesktopBoundThreadTests
{
    [Fact]
    public void Work_runs_on_one_thread_that_is_never_the_caller_s()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // desktops are a Windows arrangement
        }

        using var thread = new DesktopBoundThread(NullLogger.Instance);
        var seen = new System.Collections.Concurrent.ConcurrentBag<int>();
        using var done = new CountdownEvent(20);

        // Posted from several pool threads, which is how the real caller arrives.
        Parallel.For(0, 20, _ =>
        {
            thread.Post(() =>
            {
                seen.Add(Environment.CurrentManagedThreadId);
                done.Signal();
            });
        });

        done.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();

        seen.Count.ShouldBe(20);
        seen.Distinct().Count().ShouldBe(1, "every batch must run on the same thread");
        seen.ShouldNotContain(Environment.CurrentManagedThreadId);
    }

    /// <summary>Work keeps the order it was posted in: a keystroke stream is not a set.</summary>
    [Fact]
    public void Work_runs_in_the_order_it_was_posted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var thread = new DesktopBoundThread(NullLogger.Instance);
        var order = new List<int>();
        using var done = new CountdownEvent(50);

        for (int i = 0; i < 50; i++)
        {
            int n = i;
            thread.Post(() =>
            {
                order.Add(n);
                done.Signal();
            });
        }

        done.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        order.ShouldBe(Enumerable.Range(0, 50).ToList());
    }

    /// <summary>
    /// The blocking form has to answer from that same thread, and has to come back. It is called from a
    /// session's message pump, where waiting for ever is the same as the session ending.
    /// </summary>
    [Fact]
    public void An_answer_comes_back_from_the_bound_thread()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var thread = new DesktopBoundThread(NullLogger.Instance);

        int first = thread.Call(() => Environment.CurrentManagedThreadId);
        int second = thread.Call(() => Environment.CurrentManagedThreadId);

        first.ShouldNotBe(Environment.CurrentManagedThreadId);
        second.ShouldBe(first);
    }

    /// <summary>
    /// One posted action throwing must not take the thread down with it -- a single bad event would
    /// otherwise end injection for the rest of the session, silently.
    /// </summary>
    [Fact]
    public void A_failure_does_not_end_the_thread()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var thread = new DesktopBoundThread(NullLogger.Instance);
        thread.Post(() => throw new InvalidOperationException("bad event"));

        thread.Call(() => 42).ShouldBe(42);
    }
}
