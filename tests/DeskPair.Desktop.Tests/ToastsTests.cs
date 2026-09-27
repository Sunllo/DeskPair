using Microsoft.Extensions.Time.Testing;
using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// A message that goes away again.
///
/// Every screen used to keep its own line of text, set when something happened and cleared when the next
/// thing happened. On a page somebody sets once and leaves, the next thing never happens -- so "the new
/// language applies to windows opened from now on" stayed under the recording settings indefinitely,
/// where it read as a warning about them rather than a receipt for something done ten minutes earlier.
/// </summary>
[Collection("ProcessState")] // touches process-wide state (Strings.Language, Toasts.Current), so never alongside another test that does
public class ToastsTests
{
    /// <summary>Runs the expiry straight away; the real one hops to the interface thread.</summary>
    private static Toasts Make(FakeTimeProvider time) => new(time, work => work());

    [Fact]
    public void A_message_is_shown_and_then_is_not()
    {
        var time = new FakeTimeProvider();
        Toasts toasts = Make(time);

        toasts.Show("Saved");

        toasts.IsVisible.ShouldBeTrue();
        toasts.Message.ShouldBe("Saved");

        time.Advance(Toasts.Lifetime + TimeSpan.FromSeconds(1));

        toasts.IsVisible.ShouldBeFalse();
        toasts.Message.ShouldBeEmpty();
    }

    /// <summary>Long enough to read a sentence in a second language, and not a moment less.</summary>
    [Fact]
    public void It_is_still_there_a_moment_before_its_time()
    {
        var time = new FakeTimeProvider();
        Toasts toasts = Make(time);
        toasts.Show("Saved");

        time.Advance(Toasts.Lifetime - TimeSpan.FromSeconds(1));

        toasts.IsVisible.ShouldBeTrue();
        Toasts.Lifetime.ShouldBe(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// The second message replaces the first, and gets its own ten seconds. Somebody who has just changed
    /// two settings wants to know the second one saved.
    /// </summary>
    [Fact]
    public void A_newer_message_replaces_the_one_before_it()
    {
        var time = new FakeTimeProvider();
        Toasts toasts = Make(time);
        toasts.Show("Saved");

        time.Advance(TimeSpan.FromSeconds(9));
        toasts.Show("Copied");

        toasts.Message.ShouldBe("Copied");

        time.Advance(TimeSpan.FromSeconds(9));
        toasts.IsVisible.ShouldBeTrue("the replacement starts its own ten seconds, it does not inherit what was left");

        time.Advance(TimeSpan.FromSeconds(2));
        toasts.IsVisible.ShouldBeFalse();
    }

    /// <summary>A screen clearing its own notice means "nothing to say", not "say nothing for ten seconds".</summary>
    [Fact]
    public void An_empty_message_takes_the_last_one_away()
    {
        var time = new FakeTimeProvider();
        Toasts toasts = Make(time);
        toasts.Show("Saved");

        toasts.Show("");

        toasts.IsVisible.ShouldBeFalse();
        toasts.Message.ShouldBeEmpty();
    }

    /// <summary>Bad news is shown differently, and must not carry over to the next message.</summary>
    [Fact]
    public void A_complaint_is_marked_as_one_and_the_mark_does_not_linger()
    {
        var time = new FakeTimeProvider();
        Toasts toasts = Make(time);

        toasts.Show("Could not save", problem: true);
        toasts.IsProblem.ShouldBeTrue();

        toasts.Show("Saved");
        toasts.IsProblem.ShouldBeFalse();
    }

    [Fact]
    public void Closing_it_by_hand_takes_it_away_now()
    {
        var time = new FakeTimeProvider();
        Toasts toasts = Make(time);
        toasts.Show("Saved");

        toasts.Hide();

        toasts.IsVisible.ShouldBeFalse();

        // And the timer that was running must not resurrect anything when it fires.
        time.Advance(Toasts.Lifetime + TimeSpan.FromSeconds(1));
        toasts.IsVisible.ShouldBeFalse();
    }
}
