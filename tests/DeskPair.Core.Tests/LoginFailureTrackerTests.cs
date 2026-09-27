using System.Net;
using DeskPair.Core.Session.Host.Auth;
using Microsoft.Extensions.Time.Testing;

namespace DeskPair.Core.Tests;

/// <summary>
/// A wrong guess costs more each time. Before this, one address could try six passwords a minute for
/// as long as it liked, and the only thing that stopped it was the temporary password moving -- which
/// is a cost the person at the desk paid, not the guesser.
/// </summary>
public class LoginFailureTrackerTests
{
    private static readonly IPAddress Guesser = IPAddress.Parse("203.0.113.7");
    private static readonly IPAddress Neighbour = IPAddress.Parse("203.0.113.8");

    [Fact]
    public void Consecutive_failures_back_off_exponentially_and_the_fifth_attempt_waits_sixteen_seconds()
    {
        var time = new FakeTimeProvider();
        var tracker = new LoginFailureTracker(time);

        int[] expected = [2, 4, 8, 16];
        foreach (int seconds in expected)
        {
            tracker.CheckBlocked(Guesser).ShouldBe(TimeSpan.Zero);
            tracker.RecordFailure(Guesser);
            tracker.CheckBlocked(Guesser).ShouldBe(TimeSpan.FromSeconds(seconds));
            time.Advance(TimeSpan.FromSeconds(seconds));
        }

        // The fifth attempt is the one that had to wait sixteen seconds; from here on it is the cap.
        tracker.RecordFailure(Guesser);
        tracker.CheckBlocked(Guesser).ShouldBe(TimeSpan.FromSeconds(30));

        // The sixth failure inside a minute is where the short window takes over, and the longest wait wins.
        time.Advance(TimeSpan.FromSeconds(30));
        tracker.RecordFailure(Guesser);
        tracker.CheckBlocked(Guesser).ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void A_wait_is_the_remaining_part_of_it()
    {
        var time = new FakeTimeProvider();
        var tracker = new LoginFailureTracker(time);
        tracker.RecordFailure(Guesser);
        tracker.RecordFailure(Guesser);

        time.Advance(TimeSpan.FromSeconds(1));

        tracker.CheckBlocked(Guesser).ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void One_address_guessing_does_not_slow_another_down()
    {
        var time = new FakeTimeProvider();
        var tracker = new LoginFailureTracker(time);
        for (int i = 0; i < 5; i++)
        {
            tracker.RecordFailure(Guesser);
        }

        tracker.CheckBlocked(Neighbour).ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void A_success_forgets_the_failures_before_it()
    {
        var time = new FakeTimeProvider();
        var tracker = new LoginFailureTracker(time);
        tracker.RecordFailure(Guesser);
        tracker.RecordFailure(Guesser);
        tracker.RecordSuccess(Guesser);

        tracker.CheckBlocked(Guesser).ShouldBe(TimeSpan.Zero);
        tracker.RecordFailure(Guesser);
        tracker.CheckBlocked(Guesser).ShouldBe(TimeSpan.FromSeconds(2), "the count starts again");
    }

    [Fact]
    public void The_long_window_still_wins_over_the_backoff()
    {
        var time = new FakeTimeProvider();
        var tracker = new LoginFailureTracker(time) { LongWindowLimit = 3, LongWindow = TimeSpan.FromHours(1) };
        for (int i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromMinutes(5));
            tracker.RecordFailure(Guesser);
        }

        tracker.CheckBlocked(Guesser).ShouldBe(TimeSpan.FromHours(1));
    }

    [Fact]
    public void IPv6_sources_are_bucketed_by_their_64()
    {
        LoginFailureTracker.BucketOf(IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd"))
            .ShouldBe(LoginFailureTracker.BucketOf(IPAddress.Parse("2001:db8:1:2::1")));
        LoginFailureTracker.BucketOf(IPAddress.Parse("2001:db8:1:3::1"))
            .ShouldNotBe(LoginFailureTracker.BucketOf(IPAddress.Parse("2001:db8:1:2::1")));
        LoginFailureTracker.BucketOf(IPAddress.Parse("::ffff:203.0.113.7")).ShouldBe("203.0.113.7");
    }
}
