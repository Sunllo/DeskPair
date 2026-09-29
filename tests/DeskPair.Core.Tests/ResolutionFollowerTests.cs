using DeskPair.Core.Session.Controller;
using DeskPair.Protocol.Messages;
using Microsoft.Extensions.Time.Testing;

namespace DeskPair.Core.Tests;

/// <summary>A remote display kept the size of the window showing it, the way a Windows remote desktop session is.</summary>
public sealed class ResolutionFollowerTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly List<(int Display, Resolution? Mode)> _asked = [];
    private readonly ResolutionFollower _follower;

    public ResolutionFollowerTests()
    {
        _follower = new ResolutionFollower(_time, (display, mode) =>
        {
            lock (_asked)
            {
                _asked.Add((display, mode));
            }

            return Task.CompletedTask;
        });
    }

    public void Dispose() => _follower.Dispose();

    /// <summary>A Linux output: any size in range, currently <paramref name="w"/>x<paramref name="h"/>.</summary>
    private static DisplayInfo AnySize(int w, int h, string name = "HDMI-1", (int W, int H)? original = null) => new()
    {
        Name = name,
        Width = w,
        Height = h,
        AnySize = new SizeRange { MinWidth = 640, MinHeight = 480, MaxWidth = 4096, MaxHeight = 4096, Step = 2 },
        Original = original is { } o ? new Resolution { Width = o.W, Height = o.H } : null,
    };

    private void Rest() => _time.Advance(ResolutionFollower.Settle);

    private (int W, int H)? LastAsked
    {
        get
        {
            lock (_asked)
            {
                return _asked.Count == 0 ? null : _asked[^1].Mode is { } m ? (m.Width, m.Height) : (0, 0);
            }
        }
    }

    [Fact]
    public void The_host_is_asked_once_the_window_has_rested()
    {
        _follower.Start(0, AnySize(1920, 1080));
        _follower.Window(1234, 776, 1);
        _time.Advance(TimeSpan.FromMilliseconds(200));
        _follower.Window(1300, 800, 1);
        _time.Advance(TimeSpan.FromMilliseconds(299));
        _asked.ShouldBeEmpty("still being resized");

        _time.Advance(TimeSpan.FromMilliseconds(1));

        _asked.ShouldHaveSingleItem();
        LastAsked.ShouldBe((1300, 800));
        _follower.State.ShouldBe(FollowState.Waiting);
    }

    [Fact]
    public void One_request_at_a_time_and_the_latest_size_once_it_is_answered()
    {
        _follower.Start(0, AnySize(1920, 1080));
        _follower.Window(1000, 700, 1);
        Rest();
        _follower.Window(1100, 760, 1);
        _follower.Window(1200, 800, 1);
        Rest();
        _asked.Count.ShouldBe(1, "the first is still unanswered");

        _follower.Heard(0, AnySize(1000, 700, original: (1920, 1080)), changed: 0);

        _asked.Count.ShouldBe(2);
        LastAsked.ShouldBe((1200, 800));
    }

    [Fact]
    public void An_answer_that_the_display_is_the_size_asked_for_means_matched()
    {
        _follower.Start(0, AnySize(1920, 1080));
        _follower.Window(1000, 700, 1);
        Rest();

        _follower.Heard(0, AnySize(1000, 700, original: (1920, 1080)), changed: 0);

        _follower.State.ShouldBe(FollowState.Matched);
        _follower.IsWaiting.ShouldBeFalse();
    }

    [Fact]
    public void A_refusal_is_not_asked_again_until_the_window_changes_size()
    {
        _follower.Start(0, AnySize(1920, 1080));
        _follower.Window(1000, 700, 1);
        Rest();

        _follower.Heard(0, AnySize(1920, 1080), changed: 0, failure: "No.");
        _follower.State.ShouldBe(FollowState.Failed);
        _follower.Failure.ShouldBe("No.");
        _follower.Window(1000, 700, 1);
        Rest();
        _asked.Count.ShouldBe(1);

        _follower.Window(1010, 700, 1);
        Rest();
        _asked.Count.ShouldBe(2);
    }

    [Fact]
    public void No_answer_counts_as_a_refusal()
    {
        _follower.Start(0, AnySize(1920, 1080));
        _follower.Window(1000, 700, 1);
        Rest();

        _time.Advance(ResolutionFollower.AnswerTimeout);

        _follower.State.ShouldBe(FollowState.Failed);
        _follower.Failure.ShouldBeEmpty();
        _follower.IsWaiting.ShouldBeFalse();
    }

    /// <summary>Two viewers following their windows would otherwise take the display from each other for ever.</summary>
    [Fact]
    public void Somebody_elses_change_is_not_fought()
    {
        _follower.Start(0, AnySize(1920, 1080));
        _follower.Window(1000, 700, 1);
        Rest();
        _follower.Heard(0, AnySize(1000, 700, original: (1920, 1080)), changed: 0);

        _follower.Heard(0, AnySize(1600, 900, original: (1920, 1080)), changed: 0);
        _time.Advance(TimeSpan.FromSeconds(10));

        _follower.State.ShouldBe(FollowState.Overridden);
        _asked.Count.ShouldBe(1);

        _follower.Window(1020, 700, 1);
        Rest();
        _asked.Count.ShouldBe(2, "this window changing size is this viewer speaking again");
    }

    [Fact]
    public void A_request_overtaken_by_somebody_elses_is_let_go()
    {
        _follower.Start(0, AnySize(1920, 1080));
        _follower.Window(1000, 700, 1);
        Rest();

        _follower.Heard(0, AnySize(1600, 900, original: (1920, 1080)), changed: 0);

        _follower.State.ShouldBe(FollowState.Overridden);
        _follower.IsWaiting.ShouldBeFalse();
    }

    [Fact]
    public async Task Turning_off_gives_the_display_back_while_it_is_still_the_size_set_here()
    {
        _follower.Start(0, AnySize(1920, 1080));
        _follower.Window(1000, 700, 1);
        Rest();
        _follower.Heard(0, AnySize(1000, 700, original: (1920, 1080)), changed: 0);

        await _follower.StopAsync();

        _asked.Count.ShouldBe(2);
        _asked[^1].ShouldBe((0, (Resolution?)null));
        _follower.State.ShouldBe(FollowState.Off);
    }

    [Fact]
    public async Task Turning_off_leaves_somebody_elses_size_alone()
    {
        _follower.Start(0, AnySize(1920, 1080));
        _follower.Window(1000, 700, 1);
        Rest();
        _follower.Heard(0, AnySize(1000, 700, original: (1920, 1080)), changed: 0);
        _follower.Heard(0, AnySize(1600, 900, original: (1920, 1080)), changed: 0);

        await _follower.StopAsync();

        _asked.Count.ShouldBe(1);
    }

    [Fact]
    public void Moving_to_another_display_gives_the_first_one_back_and_follows_the_second()
    {
        _follower.Start(0, AnySize(1920, 1080, "HDMI-1"));
        _follower.Window(1000, 700, 1);
        Rest();
        _follower.Heard(0, AnySize(1000, 700, "HDMI-1", original: (1920, 1080)), changed: 0);

        _follower.Heard(1, AnySize(2560, 1440, "DP-2"));
        _asked[^1].ShouldBe((0, (Resolution?)null), "the display left behind goes back to its own size");
        Rest();

        _asked[^1].Display.ShouldBe(1);
        LastAsked.ShouldBe((1000, 700));
    }

    /// <summary>
    /// Back after a dropped connection, the host has put its screen back as the last viewer left: started again, the
    /// follower asks for the window's size anew rather than taking the restored size for somebody else's change.
    /// </summary>
    [Fact]
    public void Started_again_after_a_reconnection_it_asks_again()
    {
        _follower.Start(0, AnySize(1920, 1080));
        _follower.Window(1000, 700, 1);
        Rest();
        _follower.Heard(0, AnySize(1000, 700, original: (1920, 1080)), changed: 0);

        _follower.Start(0, AnySize(1920, 1080));
        _follower.Heard(0, AnySize(1920, 1080));
        Rest();

        _follower.State.ShouldBe(FollowState.Waiting);
        _asked.Count.ShouldBe(2);
        LastAsked.ShouldBe((1000, 700));
    }

    [Fact]
    public void A_display_that_cannot_change_is_said_to_be_so_and_never_asked()
    {
        _follower.Start(0, new DisplayInfo { Name = "eDP-1", Width = 1920, Height = 1080 });
        _follower.Window(1000, 700, 1);
        Rest();

        _follower.State.ShouldBe(FollowState.Unsupported);
        _asked.ShouldBeEmpty();
    }

    [Fact]
    public void A_window_waiting_for_the_first_description_is_asked_for_once_it_comes()
    {
        _follower.Start(0, null);
        _follower.Window(1000, 700, 1);
        Rest();
        _asked.ShouldBeEmpty();

        _follower.Heard(0, AnySize(1920, 1080));
        Rest();

        LastAsked.ShouldBe((1000, 700));
    }

    /// <summary>A window as large as the display's own size asks for the original back rather than a copy of it.</summary>
    [Fact]
    public void A_window_the_size_of_the_original_asks_for_the_original()
    {
        _follower.Start(0, AnySize(1000, 700, original: (1920, 1080)));
        _follower.Window(1920, 1080, 1);
        Rest();

        _asked[^1].ShouldBe((0, (Resolution?)null));
        _follower.Heard(0, AnySize(1920, 1080), changed: 0);
        _follower.State.ShouldBe(FollowState.Matched);
    }

    [Fact]
    public void A_request_that_cannot_be_sent_is_answered_by_the_timeout()
    {
        using var follower = new ResolutionFollower(_time, (_, _) => throw new ObjectDisposedException("session"));
        follower.Start(0, AnySize(1920, 1080));
        follower.Window(1000, 700, 1);
        Rest();

        _time.Advance(ResolutionFollower.AnswerTimeout);

        follower.State.ShouldBe(FollowState.Failed);
    }
}
