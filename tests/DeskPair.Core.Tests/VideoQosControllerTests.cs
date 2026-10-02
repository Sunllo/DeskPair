using Microsoft.Extensions.Time.Testing;
using DeskPair.Core.Qos;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Tests;

public class VideoQosControllerTests
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
    public void Starts_at_default_fps_and_full_ratio()
    {
        (VideoQosController qos, _) = Create();
        qos.AddUser(1);
        qos.Fps.ShouldBe(VideoQosController.DefaultFps);
        qos.Ratio.ShouldBe(1.0);
        qos.FrameInterval.ShouldBe(TimeSpan.FromSeconds(1.0 / VideoQosController.DefaultFps));
    }

    [Fact]
    public void Sustained_delay_reduces_bitrate_before_fps()
    {
        (VideoQosController qos, FakeTimeProvider time) = Create();
        qos.AddUser(1);
        Report(qos, time, 1, 800, 4);
        qos.Ratio.ShouldBeLessThan(1.0);
        qos.Fps.ShouldBe(VideoQosController.DefaultFps);

        Report(qos, time, 1, 800, 40);
        qos.Ratio.ShouldBe(VideoQosController.MinRatio);
        qos.Fps.ShouldBeLessThan(VideoQosController.DefaultFps);
        qos.Fps.ShouldBeGreaterThanOrEqualTo(VideoQosController.MinFps);
    }

    /// <summary>Severe round trips that keep coming for <see cref="VideoQosController.SevereFor"/>: the link, not an absent viewer.</summary>
    internal static void Severe(VideoQosController qos, FakeTimeProvider time)
    {
        qos.ReportDelay(1, TimeSpan.FromSeconds(2));
        time.Advance(VideoQosController.SevereFor);
        qos.ReportDelay(1, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Severe_delay_that_lasts_halves_at_once()
    {
        (VideoQosController qos, FakeTimeProvider time) = Create();
        qos.AddUser(1);
        Severe(qos, time);
        qos.Fps.ShouldBe(VideoQosController.DefaultFps / 2);
        qos.Ratio.ShouldBe(0.5);
    }

    /// <summary>What a viewer reported just before it went quiet is stale once it is back: a phone being suspended drops packets, and that is not the link.</summary>
    [Fact]
    public void Loss_reported_before_a_silence_is_forgotten_when_the_viewer_returns()
    {
        (VideoQosController qos, FakeTimeProvider time) = Create();
        qos.AddUser(1);
        Report(qos, time, 1, 20, 5);
        qos.ReportLoss(1, 0.25);
        Report(qos, time, 1, 20, 2);
        qos.Tier.ShouldBe(LinkTier.Poor, "a quarter of the packets lost is a bad link while the viewer is here");

        time.Advance(VideoQosController.SilenceResets + TimeSpan.FromSeconds(1));
        Report(qos, time, 1, 20, 6);

        qos.Tier.ShouldNotBe(LinkTier.Poor, "the loss belonged to the viewer's absence");
    }

    /// <summary>
    /// A phone back from the background answers every heartbeat it missed at once, each a round trip of
    /// seconds. That burst measures its absence, not the link, and must not cost the picture its quality.
    /// </summary>
    [Fact]
    public void A_burst_of_late_answers_after_a_viewer_was_away_is_not_congestion()
    {
        (VideoQosController qos, FakeTimeProvider time) = Create();
        qos.AddUser(1);
        Report(qos, time, 1, 20, 10);

        for (int i = 0; i < 8; i++)
        {
            qos.ReportDelay(1, TimeSpan.FromSeconds(8 - i));
            time.Advance(TimeSpan.FromMilliseconds(50));
        }

        qos.Fps.ShouldBe(VideoQosController.DefaultFps);
        qos.Ratio.ShouldBe(1.0);

        // And the burst leaves no trace in the averages: ordinary samples afterwards keep the link "good".
        Report(qos, time, 1, 20, 10);
        qos.Ratio.ShouldBe(1.0);
        qos.Tier.ShouldNotBe(LinkTier.Poor);
    }

    [Fact]
    public void Good_network_restores_gradually()
    {
        (VideoQosController qos, FakeTimeProvider time) = Create();
        qos.AddUser(1);
        Severe(qos, time);
        int reducedFps = qos.Fps;
        Report(qos, time, 1, 20, 60);
        qos.Fps.ShouldBeGreaterThan(reducedFps);
        qos.Fps.ShouldBe(VideoQosController.DefaultFps);
        qos.Ratio.ShouldBe(1.0);
    }

    [Fact]
    public void Slowest_user_caps_fps_and_leaving_lifts_the_cap()
    {
        (VideoQosController qos, _) = Create();
        qos.AddUser(1);
        qos.AddUser(2, new SessionOptions { CustomFps = 10 });
        qos.Fps.ShouldBe(10);
        qos.RemoveUser(2);
        qos.Fps.ShouldBe(10); // does not jump back until good samples arrive
        qos.RemoveUser(1);
        qos.Fps.ShouldBe(VideoQosController.DefaultFps);
    }

    /// <summary>
    /// A viewer's frame rate caps the host whatever quality it chose -- the desktop's toolbar sends one with any of
    /// them -- and on a good link a higher one is reached within a few seconds, a lower one at once.
    /// </summary>
    [Fact]
    public void A_frame_rate_asked_with_any_quality_caps_the_host()
    {
        (VideoQosController qos, FakeTimeProvider time) = Create();
        qos.AddUser(1, new SessionOptions { ImageQuality = ImageQuality.IqBalanced, CustomFps = 30 });
        qos.Fps.ShouldBe(30);

        qos.UpdateOptions(1, new SessionOptions { ImageQuality = ImageQuality.IqBest, CustomFps = 15 });
        qos.Fps.ShouldBe(15, "lowered at once");

        qos.UpdateOptions(1, new SessionOptions { ImageQuality = ImageQuality.IqBest, CustomFps = 90 });
        Report(qos, time, 1, 20, 20);
        qos.Fps.ShouldBe(90, "raised as far as asked, over a few good samples");

        qos.UpdateOptions(1, new SessionOptions { ImageQuality = ImageQuality.IqLow });
        qos.Fps.ShouldBe(VideoQosController.DefaultFps, "no frame rate asked: the host's own cap");
    }

    [Fact]
    public void Congestion_is_tracked_per_user_from_acks()
    {
        (VideoQosController qos, _) = Create();
        qos.AddUser(1);
        for (uint s = 1; s <= 10; s++)
        {
            qos.FrameSent(1, 0, s);
        }

        qos.IsCongested(1, 0).ShouldBeTrue();
        qos.IsCongested(1, 1).ShouldBeFalse();
        qos.FrameAcked(1, 0, 9);
        qos.IsCongested(1, 0).ShouldBeFalse();

        // A fresh stream on another display starts its own sequence space without looking congested.
        qos.FrameSent(1, 1, 1);
        qos.IsCongested(1, 1).ShouldBeFalse();
    }

    [Theory]
    [InlineData(640, 480, 800)]
    [InlineData(1920, 1080, 4000)]
    [InlineData(3840, 2160, 11000)]
    [InlineData(10000, 10000, 24000)]
    public void Base_bitrate_follows_the_table(int w, int h, int kbps)
    {
        VideoQosController.BaseBitrateKbps(w, h).ShouldBe(kbps);
    }

    [Fact]
    public void Target_bitrate_honours_quality_and_ratio()
    {
        (VideoQosController qos, FakeTimeProvider time) = Create();
        qos.AddUser(1, new SessionOptions { ImageQuality = ImageQuality.IqBest });
        qos.TargetBitrateKbps(1920, 1080).ShouldBe((int)(4000 * 1.5));
        qos.UpdateOptions(1, new SessionOptions { ImageQuality = ImageQuality.IqCustom, CustomBitrateKbps = 5000 });
        qos.TargetBitrateKbps(1920, 1080).ShouldBe(5000);
        Severe(qos, time);
        qos.TargetBitrateKbps(1920, 1080).ShouldBe(2500);
    }

    [Fact]
    public void Gcc_estimate_replaces_the_probe_for_udp_viewers_and_is_capped_by_resolution_and_quality()
    {
        (VideoQosController qos, _) = Create();
        qos.AddUser(1, new SessionOptions { ImageQuality = ImageQuality.IqBalanced });
        qos.ReportBandwidth(1, 9_000_000);
        qos.TargetBitrateKbps(2560, 1440).ShouldBe(9000);
        qos.BandwidthEstimateBps.ShouldBe(9_000_000);

        qos.ReportBandwidth(1, 1_000_000_000);
        qos.TargetBitrateKbps(2560, 1440).ShouldBe(VideoQosController.MaxBitrateKbps(2560, 1440, qos.Fps));
        // pixels x fps x 0.35 bits x 10 headroom: the standard is the same at every size and frame rate.
        VideoQosController.MaxBitrateKbps(2560, 1440, 60).ShouldBeInRange(760_000, 785_000);
        VideoQosController.MaxBitrateKbps(2560, 1440, 30).ShouldBeInRange(380_000, 393_000);
        VideoQosController.MaxBitrateKbps(1920, 1080, 30).ShouldBeInRange(210_000, 225_000);
        VideoQosController.MaxBitrateKbps(3840, 2160, 60).ShouldBeInRange(1_720_000, 1_760_000);
        VideoQosController.MaxBitrateKbps(3840, 2160, 120).ShouldBe(VideoQosController.AbsoluteMaxKbps); // clamped

        qos.UpdateOptions(1, new SessionOptions { ImageQuality = ImageQuality.IqLow });
        Math.Abs(qos.TargetBitrateKbps(2560, 1440) - (VideoQosController.MaxBitrateKbps(2560, 1440, qos.Fps) / 2)).ShouldBeLessThanOrEqualTo(1);

        // A second viewer still on TCP holds the target to the probed table.
        qos.AddUser(2, new SessionOptions { ImageQuality = ImageQuality.IqBalanced });
        qos.UpdateOptions(1, new SessionOptions { ImageQuality = ImageQuality.IqBalanced });
        qos.TargetBitrateKbps(2560, 1440).ShouldBe(VideoQosController.BaseBitrateKbps(2560, 1440));

        qos.RemoveUser(2);
        qos.ReportBandwidth(1, null);
        qos.BandwidthEstimateBps.ShouldBeNull();
        qos.TargetBitrateKbps(2560, 1440).ShouldBe(VideoQosController.BaseBitrateKbps(2560, 1440));
    }

    /// <summary>
    /// One link, two displays: the GCC estimate is split by pixels with the focus weighted, where before each
    /// stream was handed the whole of it and together they asked for twice what the link had.
    /// </summary>
    [Fact]
    public void Two_displays_share_one_links_estimate_and_the_focus_gets_more()
    {
        (VideoQosController qos, _) = Create();
        qos.AddUser(1, new SessionOptions { ImageQuality = ImageQuality.IqBalanced });
        qos.SetSubscription(1, [0, 1], focus: 0);
        qos.ReportBandwidth(1, 10_000_000);

        // Equal sizes: 1.5 : 1 of ten megabits.
        qos.TargetBitrateKbps(1, 1920, 1080);
        int focused = qos.TargetBitrateKbps(0, 1920, 1080);
        int other = qos.TargetBitrateKbps(1, 1920, 1080);
        focused.ShouldBe(6000);
        other.ShouldBe(4000);

        // Moving the focus moves the larger share.
        qos.SetSubscription(1, [0, 1], focus: 1);
        qos.TargetBitrateKbps(0, 1920, 1080).ShouldBe(4000);
        qos.TargetBitrateKbps(1, 1920, 1080).ShouldBe(6000);

        // Asked without a display, the answer is the old single-stream one: the whole estimate.
        qos.TargetBitrateKbps(1920, 1080).ShouldBe(10_000);
    }

    /// <summary>What a picture needs is the picture's own: the balanced table is not divided between displays.</summary>
    [Fact]
    public void The_table_a_tcp_viewer_gets_is_not_divided()
    {
        (VideoQosController qos, _) = Create();
        qos.AddUser(1, new SessionOptions { ImageQuality = ImageQuality.IqBalanced });
        qos.SetSubscription(1, [0, 1], focus: 0);
        qos.TargetBitrateKbps(0, 1920, 1080).ShouldBe(VideoQosController.BaseBitrateKbps(1920, 1080));
        qos.TargetBitrateKbps(1, 1920, 1080).ShouldBe(VideoQosController.BaseBitrateKbps(1920, 1080));
    }

    /// <summary>The probe ceiling for a link carrying two displays is what both can use, not whichever asked last.</summary>
    [Fact]
    public void A_links_probe_ceiling_is_the_sum_of_its_displays()
    {
        (VideoQosController qos, _) = Create();
        qos.AddUser(1, new SessionOptions { ImageQuality = ImageQuality.IqBalanced });
        qos.SetSubscription(1, [0, 1], focus: 0);
        qos.TargetBitrateKbps(0, 1920, 1080);
        qos.TargetBitrateKbps(1, 1280, 720);

        int both = VideoQosController.MaxBitrateKbps(1920, 1080, qos.Fps) + VideoQosController.MaxBitrateKbps(1280, 720, qos.Fps);
        Math.Abs(qos.BitrateCeilingKbpsFor(1) - both).ShouldBeLessThanOrEqualTo(1);
    }
}
