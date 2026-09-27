using Microsoft.Extensions.Time.Testing;
using DeskPair.Core.Qos;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Tests;

public class LinkTierTests
{
    private static (VideoQosController Qos, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        return (new VideoQosController(time), time);
    }

    private static void Report(VideoQosController qos, FakeTimeProvider time, int conn, int delayMs, int samples)
    {
        for (int i = 0; i < samples; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            qos.ReportDelay(conn, TimeSpan.FromMilliseconds(delayMs));
        }
    }

    [Fact]
    public void Starts_fair_and_climbs_one_tier_per_evaluation_on_a_clean_link()
    {
        (VideoQosController qos, FakeTimeProvider time) = Create();
        qos.AddUser(1);
        qos.Tier.ShouldBe(LinkTier.Fair);

        Report(qos, time, 1, 20, 1);
        qos.Tier.ShouldBe(LinkTier.Good);
        Report(qos, time, 1, 20, 1);
        qos.Tier.ShouldBe(LinkTier.Excellent);
    }

    [Fact]
    public void Probe_climbs_without_congestion_up_to_the_tier_cap_and_raises_the_bitrate()
    {
        (VideoQosController qos, FakeTimeProvider time) = Create();
        qos.AddUser(1);
        int baseline = qos.TargetBitrateKbps(1920, 1080);

        Report(qos, time, 1, 20, 60);
        qos.Tier.ShouldBe(LinkTier.Excellent);
        qos.Probe.ShouldBeGreaterThan(1.0);
        qos.Probe.ShouldBeLessThanOrEqualTo(VideoQosController.MaxProbe(LinkTier.Excellent));
        qos.TargetBitrateKbps(1920, 1080).ShouldBeGreaterThan(baseline);
        qos.BytesPerSecondBudget(1920, 1080).ShouldBe(qos.TargetBitrateKbps(1920, 1080) * 1000L / 8);
    }

    [Fact]
    public void A_long_but_stable_round_trip_is_still_an_excellent_link()
    {
        // Distance is not congestion: a steady 120 ms WAN path has headroom as long as the delay does not inflate.
        (VideoQosController qos, FakeTimeProvider time) = Create();
        qos.AddUser(1);
        Report(qos, time, 1, 120, 60);
        qos.Tier.ShouldBe(LinkTier.Excellent);
        qos.Probe.ShouldBeGreaterThan(1.0);
    }

    [Fact]
    public void Round_trip_inflation_drops_the_tier_and_the_probe()
    {
        (VideoQosController qos, FakeTimeProvider time) = Create();
        qos.AddUser(1);
        Report(qos, time, 1, 120, 60);
        double probe = qos.Probe;

        Report(qos, time, 1, 200, 5); // +80 ms of queueing on the same path
        ((int)qos.Tier).ShouldBeLessThan((int)LinkTier.Good);
        qos.Probe.ShouldBeLessThan(probe);
    }

    [Fact]
    public void In_flight_limit_grows_with_fps_and_round_trip()
    {
        (VideoQosController qos, FakeTimeProvider time) = Create();
        qos.AddUser(1);
        for (uint seq = 1; seq <= 8; seq++)
        {
            qos.FrameSent(1, 0, seq);
        }

        qos.IsCongested(1, 0).ShouldBeTrue(); // no samples yet: the floor of 6 applies

        Report(qos, time, 1, 150, 5); // 60 fps × 150 ms = 9 frames legitimately in flight
        qos.IsCongested(1, 0).ShouldBeFalse();
        qos.Tier.ShouldNotBe(LinkTier.Poor);
    }

    [Fact]
    public void Bad_link_drops_the_tier_and_gives_back_probed_headroom()
    {
        (VideoQosController qos, FakeTimeProvider time) = Create();
        qos.AddUser(1);
        Report(qos, time, 1, 20, 60);
        qos.Probe.ShouldBeGreaterThan(1.0);

        Report(qos, time, 1, 500, 5); // inflated by 480 ms and above the pathological ceiling
        qos.Tier.ShouldBe(LinkTier.Poor);
        qos.Probe.ShouldBe(1.0);
        VideoQosController.TileThresholdFraction(qos.Tier).ShouldBe(0.0);
    }

    [Fact]
    public void Severe_delay_resets_to_poor_immediately()
    {
        (VideoQosController qos, FakeTimeProvider time) = Create();
        qos.AddUser(1);
        Report(qos, time, 1, 20, 3);
        VideoQosControllerTests.Severe(qos, time);
        qos.Tier.ShouldBe(LinkTier.Poor);
        qos.Probe.ShouldBe(1.0);
    }

    [Fact]
    public void Backlog_keeps_the_tier_down_even_with_a_low_round_trip()
    {
        (VideoQosController qos, FakeTimeProvider time) = Create();
        qos.AddUser(1);
        for (uint seq = 1; seq <= 4; seq++)
        {
            qos.FrameSent(1, 0, seq); // nothing acked
        }

        Report(qos, time, 1, 20, 5);
        qos.Tier.ShouldBe(LinkTier.Fair);
    }

    [Fact]
    public void Override_pins_the_tier()
    {
        (VideoQosController qos, FakeTimeProvider time) = Create();
        qos.AddUser(1);
        qos.TierOverride = LinkTier.Poor;
        Report(qos, time, 1, 20, 30);
        qos.Tier.ShouldBe(LinkTier.Poor);
        qos.Probe.ShouldBe(1.0);
    }

    [Fact]
    public void Frame_acks_supply_round_trip_samples_at_frame_rate()
    {
        (VideoQosController qos, FakeTimeProvider time) = Create();
        qos.AddUser(1);
        uint seq = 0;
        for (int i = 0; i < 240; i++)
        {
            qos.FrameSent(1, 0, ++seq);
            time.Advance(TimeSpan.FromMilliseconds(25)); // 25 ms round trip, 40 fps
            qos.FrameAcked(1, 0, seq);
        }

        qos.LastRoundTrip(1).ShouldBe(TimeSpan.FromMilliseconds(25));
        qos.Tier.ShouldBe(LinkTier.Excellent); // 6 s of clean acks: climbed and probing without a single ping
        qos.Probe.ShouldBeGreaterThan(1.0);
        qos.IsCongested(1, 0).ShouldBeFalse();
    }

    [Fact]
    public void Ack_round_trips_react_within_a_second_to_inflation()
    {
        (VideoQosController qos, FakeTimeProvider time) = Create();
        qos.AddUser(1);
        uint seq = 0;
        for (int i = 0; i < 240; i++)
        {
            qos.FrameSent(1, 0, ++seq);
            time.Advance(TimeSpan.FromMilliseconds(16));
            qos.FrameAcked(1, 0, seq);
        }

        double probe = qos.Probe;
        for (int i = 0; i < 60; i++)
        {
            qos.FrameSent(1, 0, ++seq);
            time.Advance(TimeSpan.FromMilliseconds(120)); // +104 ms of queueing
            qos.FrameAcked(1, 0, seq);
            if (qos.Probe < probe)
            {
                break;
            }
        }

        qos.Probe.ShouldBeLessThan(probe);
        ((int)qos.Tier).ShouldBeLessThan((int)LinkTier.Excellent);
    }

    [Fact]
    public void Tier_tables_are_monotonic()
    {
        VideoQosController.MaxProbe(LinkTier.Excellent).ShouldBeGreaterThanOrEqualTo(VideoQosController.MaxProbe(LinkTier.Good));
        VideoQosController.MaxProbe(LinkTier.Good).ShouldBeGreaterThan(VideoQosController.MaxProbe(LinkTier.Fair));
        VideoQosController.MaxProbe(LinkTier.Excellent).ShouldBeLessThanOrEqualTo(1.5); // the TCP path no longer probes to 4.5x
        VideoQosController.TileThresholdFraction(LinkTier.Excellent).ShouldBeGreaterThan(VideoQosController.TileThresholdFraction(LinkTier.Good));
        VideoQosController.TileThresholdFraction(LinkTier.Good).ShouldBeGreaterThan(VideoQosController.TileThresholdFraction(LinkTier.Fair));
        VideoQosController.RefinementDelay(LinkTier.Excellent).ShouldBeLessThan(VideoQosController.RefinementDelay(LinkTier.Good));
        VideoQosController.RefinementDelay(LinkTier.Fair).ShouldBeLessThan(VideoQosController.RefinementDelay(LinkTier.Poor));
    }

    [Fact]
    public void Refinement_is_off_for_low_quality_or_when_opted_out()
    {
        (VideoQosController qos, _) = Create();
        qos.AddUser(1, new SessionOptions { ImageQuality = ImageQuality.IqBalanced });
        qos.LosslessRefinementEnabled.ShouldBeTrue();

        qos.UpdateOptions(1, new SessionOptions { ImageQuality = ImageQuality.IqLow });
        qos.LosslessRefinementEnabled.ShouldBeFalse();

        qos.UpdateOptions(1, new SessionOptions { ImageQuality = ImageQuality.IqBest, LosslessRefinement = BoolOption.BoNo });
        qos.LosslessRefinementEnabled.ShouldBeFalse();

        qos.AddUser(2, new SessionOptions { ImageQuality = ImageQuality.IqBalanced });
        qos.UpdateOptions(1, new SessionOptions { ImageQuality = ImageQuality.IqBalanced, LosslessRefinement = BoolOption.BoYes });
        qos.LosslessRefinementEnabled.ShouldBeTrue();
    }
}
