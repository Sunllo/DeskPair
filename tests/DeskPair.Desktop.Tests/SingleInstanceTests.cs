using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// Real mutexes and real pipes, under a name of this test's own so the run never answers for, or blocks, a
/// DeskPair the developer has open.
/// </summary>
public class SingleInstanceTests
{
    /// <summary>
    /// A name of this test's own, kept short because on macOS the name is a path.
    ///
    /// A named pipe there is a Unix domain socket under $TMPDIR, which for a launchd user session is a
    /// 49-character sandbox directory. With "CoreFxPipe_" and a full 32-character GUID that came to 106,
    /// two over the 104-byte sun_path limit, and every test in this class failed on a Mac with an
    /// ArgumentOutOfRangeException about path length. Eight hex characters is 82, and is plenty to keep
    /// one test run's names apart.
    ///
    /// The product is not affected: its name is "DeskPair.&lt;user&gt;", which is 74 characters all told.
    /// </summary>
    private static string Key() => $"DeskPair.Test.{Guid.NewGuid().ToString("N")[..8]}";

    private static SingleInstance Claim(string key) =>
        SingleInstance.Claim(NullLogger.Instance, key) ?? throw new InvalidOperationException("nothing was holding it");

    [Fact]
    public void The_first_process_takes_the_name_and_the_second_does_not()
    {
        string key = Key();

        using SingleInstance first = Claim(key);

        SingleInstance.Claim(NullLogger.Instance, key).ShouldBeNull("a second DeskPair must not start");
    }

    [Fact]
    public void Letting_go_lets_the_next_one_start()
    {
        string key = Key();
        SingleInstance first = Claim(key);

        first.Dispose();

        using SingleInstance? next = SingleInstance.Claim(NullLogger.Instance, key);
        next.ShouldNotBeNull("closing DeskPair must not leave the machine unable to open it again");
    }

    /// <summary>
    /// A second launch is not thrown away: what it was started with reaches the window that is already open,
    /// so a link handler still connects instead of merely raising an empty window.
    /// </summary>
    [Fact]
    public async Task A_second_launch_hands_its_arguments_over()
    {
        string key = Key();
        using SingleInstance first = Claim(key);
        var handed = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Launched += args => handed.TrySetResult(args);

        SingleInstance.Signal(["--connect", "123456789", "--password", "secret"], NullLogger.Instance, key).ShouldBeTrue();

        string[] got = await handed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        got.ShouldBe(["--connect", "123456789", "--password", "secret"]);
    }

    [Fact]
    public async Task A_launch_with_no_arguments_still_raises_the_window()
    {
        string key = Key();
        using SingleInstance first = Claim(key);
        var handed = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Launched += args => handed.TrySetResult(args);

        SingleInstance.Signal([], NullLogger.Instance, key).ShouldBeTrue();

        (await handed.Task.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeEmpty();
    }

    /// <summary>
    /// Claim must not return until it can answer.
    ///
    /// The pipe used to be created inside the task Claim started, so there was a window where the mutex
    /// was held -- this looked like the running copy -- and nothing was listening. A launch landing in it
    /// was told the running copy would not answer, and started a second DeskPair, which is the one thing
    /// this class exists to prevent. The window opened exactly while the machine was busy starting the
    /// first copy, which is when a second launch is most likely.
    ///
    /// Signalling on the very next statement is the closest a test can stand to that window, and it fails
    /// reliably against the old arrangement under load.
    /// </summary>
    [Fact]
    public async Task A_launch_the_instant_after_claiming_is_still_heard()
    {
        string key = Key();
        using SingleInstance first = Claim(key);
        var handed = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Launched += args => handed.TrySetResult(args);

        SingleInstance.Signal(["--connect", "5"], NullLogger.Instance, key).ShouldBeTrue();

        (await handed.Task.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(["--connect", "5"]);
    }

    /// <summary>One handover must not be the only one: the listener keeps going.</summary>
    [Fact]
    public async Task Several_launches_in_a_row_are_all_heard()
    {
        string key = Key();
        using SingleInstance first = Claim(key);
        int heard = 0;
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Launched += _ =>
        {
            if (Interlocked.Increment(ref heard) == 3)
            {
                all.TrySetResult();
            }
        };

        for (int i = 0; i < 3; i++)
        {
            SingleInstance.Signal([$"--connect", $"{i}"], NullLogger.Instance, key).ShouldBeTrue();
        }

        await all.Task.WaitAsync(TimeSpan.FromSeconds(15));
        heard.ShouldBe(3);
    }

    /// <summary>
    /// Nobody is listening on that name, so the caller is told so and starts normally. A name left behind by
    /// something that is no longer there must never leave the user unable to open the application at all.
    /// </summary>
    [Fact]
    public void Signalling_nobody_says_so_rather_than_hanging()
    {
        SingleInstance.Signal(["--connect", "1"], NullLogger.Instance, Key()).ShouldBeFalse();
    }
}
