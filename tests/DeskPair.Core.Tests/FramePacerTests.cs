using Microsoft.Extensions.Time.Testing;
using DeskPair.Core.Video;

namespace DeskPair.Core.Tests;

public class FramePacerTests
{
    private static (FramePacer Pacer, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        return (new FramePacer(time), time);
    }

    [Fact]
    public void Steady_arrivals_are_presented_at_the_same_cadence()
    {
        (FramePacer pacer, FakeTimeProvider time) = Create();
        long interval = time.TimestampFrequency / 60;
        long previous = 0;
        for (int i = 0; i < 20; i++)
        {
            long due = pacer.Schedule();
            if (i > 1)
            {
                Math.Abs((due - previous) - interval).ShouldBeLessThanOrEqualTo(interval / 20);
            }

            previous = due;
            time.Advance(TimeSpan.FromSeconds(1.0 / 60));
        }

        pacer.EstimatedFps.ShouldBe(60, tolerance: 1);
    }

    [Fact]
    public void A_burst_is_spread_out_but_never_more_than_the_lead_bound()
    {
        (FramePacer pacer, FakeTimeProvider time) = Create();
        for (int i = 0; i < 10; i++)
        {
            pacer.Schedule();
            time.Advance(TimeSpan.FromSeconds(1.0 / 60));
        }

        long now = time.GetTimestamp();
        long first = pacer.Schedule();
        time.Advance(TimeSpan.FromMilliseconds(1));
        long second = pacer.Schedule();
        time.Advance(TimeSpan.FromMilliseconds(1));
        long third = pacer.Schedule();
        long interval = (long)pacer.IntervalTicks;
        second.ShouldBeGreaterThan(first);
        third.ShouldBeGreaterThan(second);
        (third - time.GetTimestamp()).ShouldBeLessThanOrEqualTo((long)(pacer.MaxLead * interval) + 1);
        first.ShouldBeGreaterThanOrEqualTo(now);
    }

    [Fact]
    public void Lead_never_accumulates_when_frames_arrive_slightly_faster_than_estimated()
    {
        (FramePacer pacer, FakeTimeProvider time) = Create();
        // Estimate settles at ~60 fps, then the source runs at 62 fps for a long time.
        for (int i = 0; i < 30; i++)
        {
            pacer.Schedule();
            time.Advance(TimeSpan.FromSeconds(1.0 / 60));
        }

        long maxLead = 0;
        for (int i = 0; i < 600; i++)
        {
            long due = pacer.Schedule();
            maxLead = Math.Max(maxLead, due - time.GetTimestamp());
            time.Advance(TimeSpan.FromSeconds(1.0 / 62));
        }

        maxLead.ShouldBeLessThanOrEqualTo((long)(pacer.MaxLead * pacer.IntervalTicks) + 1);
    }

    [Fact]
    public void After_an_idle_gap_the_next_frame_is_immediate()
    {
        (FramePacer pacer, FakeTimeProvider time) = Create();
        for (int i = 0; i < 5; i++)
        {
            pacer.Schedule();
            time.Advance(TimeSpan.FromSeconds(1.0 / 60));
        }

        time.Advance(TimeSpan.FromSeconds(2));
        pacer.Schedule().ShouldBe(time.GetTimestamp());
    }

    [Fact]
    public void Smoothing_off_presents_immediately()
    {
        (FramePacer pacer, FakeTimeProvider time) = Create();
        pacer.Smooth = false;
        for (int i = 0; i < 5; i++)
        {
            pacer.Schedule().ShouldBe(time.GetTimestamp());
            time.Advance(TimeSpan.FromMilliseconds(3));
        }
    }
}
