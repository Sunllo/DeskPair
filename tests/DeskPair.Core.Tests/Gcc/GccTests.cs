using DeskPair.Core.Qos.Gcc;

namespace DeskPair.Core.Tests.Gcc;

public class GccTests
{
    [Fact]
    public void Trendline_flags_growing_queueing_delay_as_overuse_and_steady_delay_as_normal()
    {
        var steady = new TrendlineEstimator();
        var growing = new TrendlineEstimator();
        var rng = new Random(3);
        bool overuse = false;
        for (int i = 0; i < 100; i++)
        {
            long at = i * 16_000L;
            steady.Update(16 + (rng.NextDouble() - 0.5), 16, at);
            steady.State.ShouldNotBe(BandwidthUsage.Overusing);

            growing.Update(18, 16, at); // each group arrives 2 ms later than it was sent: the queue grows
            overuse |= growing.State == BandwidthUsage.Overusing;
        }

        overuse.ShouldBeTrue();
    }

    [Fact]
    public void Trendline_flags_a_draining_queue_as_underuse()
    {
        var t = new TrendlineEstimator();
        for (int i = 0; i < 40; i++)
        {
            t.Update(13, 16, i * 16_000L);
        }

        t.State.ShouldBe(BandwidthUsage.Underusing);
    }

    [Fact]
    public void Aimd_grows_about_eight_percent_a_second_and_drops_to_085_of_throughput_on_overuse()
    {
        var aimd = new AimdRateControl(5_000_000, 1_000_000, 50_000_000);
        long now = 0;
        for (int i = 0; i <= 100; i++, now += 10_000)
        {
            aimd.Update(BandwidthUsage.Normal, 5_000_000, now);
        }

        aimd.TargetBps.ShouldBeInRange(5_000_000 * 1.07, 5_000_000 * 1.09);

        aimd.Update(BandwidthUsage.Overusing, 4_000_000, now + 10_000);
        aimd.TargetBps.ShouldBe(0.85 * 4_000_000, 1);

        // Holding while the queue drains.
        aimd.Update(BandwidthUsage.Underusing, 3_000_000, now + 500_000);
        aimd.TargetBps.ShouldBe(0.85 * 4_000_000, 1);
    }

    [Fact]
    public void Aimd_does_not_grow_past_what_an_idle_sender_proves_but_does_not_collapse_either()
    {
        var aimd = new AimdRateControl(6_500_000, 1_000_000, 50_000_000);
        long now = 0;
        for (int i = 0; i < 1000; i++, now += 10_000)
        {
            aimd.Update(BandwidthUsage.Normal, 300_000, now);
        }

        aimd.TargetBps.ShouldBe(6_500_000, 1);
    }

    [Fact]
    public void Loss_based_estimate_grows_below_two_percent_holds_up_to_ten_and_drops_above()
    {
        var loss = new LossBasedBwe(8_000_000, 8_000_000);
        loss.Update(1, 99, 0, 8_000_000);
        loss.EstimateBps.ShouldBe(8_000_000); // at the cap

        loss = new LossBasedBwe(8_000_000, 30_000_000);
        loss.Update(5, 95, 0, 8_000_000);
        double held = loss.EstimateBps;
        loss.Update(5, 95, 1_000_000, 8_000_000);
        loss.EstimateBps.ShouldBe(held);

        loss.Update(20, 80, 2_000_000, 8_000_000);
        loss.EstimateBps.ShouldBe(8_000_000 * (1 - 0.5 * 0.2), 1);
    }

    [Fact]
    public void A_clean_probe_clears_a_loss_limit_left_over_from_a_burst_at_start_up()
    {
        var loss = new LossBasedBwe(6_500_000, 50_000_000);
        loss.Update(30, 70, 0, 8_000_000, 8_000_000);
        loss.EstimateBps.ShouldBeLessThan(8_000_000); // a 30 % burst cut the limit

        loss.RaiseTo(45_000_000);
        loss.EstimateBps.ShouldBe(45_000_000);
        loss.RaiseTo(80_000_000);
        loss.EstimateBps.ShouldBe(50_000_000); // never past the ceiling
    }

    [Fact]
    public void Gcc_converges_to_a_bottleneck_and_follows_it_down()
    {
        var gcc = new GccController(maxBps: 50_000_000);
        var link = new SimulatedLink(8_000_000);
        var samples = new List<(double T, double Bps)>();
        link.Run(gcc, 0, 20_000_000, samples);

        double settled = samples.Where(s => s.T >= 10).Average(s => s.Bps);
        settled.ShouldBeInRange(0.6 * 8_000_000, 1.0 * 8_000_000);
        samples.Where(s => s.T >= 10).Max(s => s.Bps).ShouldBeLessThan(1.2 * 8_000_000);

        link.CapacityBps = 4_000_000;
        samples.Clear();
        link.Run(gcc, 20_000_000, 26_000_000, samples);
        samples.Where(s => s.T >= 22).Max(s => s.Bps).ShouldBeLessThan(1.1 * 4_000_000);
        samples.Where(s => s.T >= 23).Average(s => s.Bps).ShouldBeGreaterThan(0.6 * 4_000_000);
    }

    [Fact]
    public void Gcc_with_random_loss_under_ten_percent_and_no_queue_keeps_growing()
    {
        var gcc = new GccController(maxBps: 20_000_000);
        var link = new SimulatedLink(100_000_000) { RandomLoss = 0.03 };
        var samples = new List<(double T, double Bps)>();
        link.Run(gcc, 0, 15_000_000, samples);
        samples[^1].Bps.ShouldBeGreaterThan(12_000_000);
    }

    [Fact]
    public void Probe_estimate_is_the_send_rate_when_the_link_keeps_up_and_the_receive_rate_when_it_saturates()
    {
        // 20 packets of 1200 B sent 1 ms apart: 9.6 Mb/s.
        var fast = Enumerable.Range(0, 20).Select(i => new PacketResult(i * 1000L, 50_000 + i * 1000L, 1200)).ToList();
        ProbeBitrateEstimator.Estimate(fast, 20)!.Value.ShouldBe(9_600_000, 1);

        // Same burst through a link that spaces arrivals 2 ms apart: about 4.8 Mb/s, discounted to 95 %.
        var slow = Enumerable.Range(0, 20).Select(i => new PacketResult(i * 1000L, 50_000 + i * 2000L, 1200)).ToList();
        ProbeBitrateEstimator.Estimate(slow, 20)!.Value.ShouldBe(0.95 * 4_800_000, 1);

        ProbeBitrateEstimator.Estimate(fast.Take(12).ToList(), 20).ShouldNotBeNull(); // a truncated cluster still measures the link
        ProbeBitrateEstimator.Estimate(fast.Take(8).ToList(), 20).ShouldBeNull(); // too few to say anything
        var bunched = Enumerable.Range(0, 20).Select(i => new PacketResult(i * 1000L, 50_000 + i * 100L, 1200)).ToList();
        ProbeBitrateEstimator.Estimate(bunched, 20).ShouldBeNull();
    }

    [Fact]
    public void A_probe_raises_the_estimate_and_only_a_saturated_one_lowers_it()
    {
        var gcc = new GccController(maxBps: 25_000_000);
        gcc.OnProbeResult(18_000_000).ShouldBe(18_000_000);
        gcc.OnProbeResult(9_000_000).ShouldBe(18_000_000);
        gcc.OnProbeResult(90_000_000).ShouldBe(25_000_000);
        gcc.ProbesApplied.ShouldBe(2);

        // The link dropped most of the burst: what got through is what it carries.
        gcc.OnProbeResult(4_000_000, saturated: true).ShouldBe(4_000_000);
        gcc.ProbesApplied.ShouldBe(3);
    }

    /// <summary>
    /// A sender that transmits exactly at the GCC target into a fixed-capacity link with a 100 ms drop-tail queue,
    /// and a receiver that reports arrivals every 50 ms.
    /// </summary>
    private sealed class SimulatedLink(double capacityBps)
    {
        private const int PacketBytes = 1200;
        private const long PropagationUs = 20_000;
        private const long MaxQueueUs = 100_000;
        private const long ReportIntervalUs = 50_000;

        private readonly Random _rng = new(11);
        private readonly List<(ulong Seq, long SendUs, long ArrivalUs)> _inFlight = new();
        private long _linkFreeUs;
        private long _nextSendUs;
        private long _nextReportUs;
        private ulong _seq;
        private ulong _reportedUpTo;

        public double CapacityBps { get; set; } = capacityBps;

        public double RandomLoss { get; init; }

        public void Run(GccController gcc, long fromUs, long toUs, List<(double T, double Bps)> samples)
        {
            _nextSendUs = Math.Max(_nextSendUs, fromUs);
            _nextReportUs = Math.Max(_nextReportUs, fromUs + ReportIntervalUs);
            while (_nextReportUs <= toUs)
            {
                while (_nextSendUs < _nextReportUs)
                {
                    Send(_nextSendUs);
                    _nextSendUs += (long)(PacketBytes * 8 * 1_000_000.0 / gcc.TargetBps);
                }

                Report(gcc, _nextReportUs);
                samples.Add((_nextReportUs / 1e6, gcc.TargetBps));

                _nextReportUs += ReportIntervalUs;
            }
        }

        private void Send(long nowUs)
        {
            ulong seq = ++_seq;
            long start = Math.Max(nowUs, _linkFreeUs);
            if (start - nowUs > MaxQueueUs || _rng.NextDouble() < RandomLoss)
            {
                return; // dropped
            }

            _linkFreeUs = start + (long)(PacketBytes * 8 * 1_000_000.0 / CapacityBps);
            _inFlight.Add((seq, nowUs, _linkFreeUs + PropagationUs));
        }

        private void Report(GccController gcc, long nowUs)
        {
            List<(ulong Seq, long SendUs, long ArrivalUs)> arrived = _inFlight.Where(p => p.ArrivalUs <= nowUs).OrderBy(p => p.Seq).ToList();
            _inFlight.RemoveAll(p => p.ArrivalUs <= nowUs);
            if (arrived.Count == 0)
            {
                return;
            }

            ulong last = arrived[^1].Seq;
            int lost = (int)(last - _reportedUpTo) - arrived.Count;
            _reportedUpTo = last;
            var results = arrived.Select(p => new PacketResult(p.SendUs, p.ArrivalUs, PacketBytes)).ToList();
            gcc.OnFeedback(results, Math.Max(0, lost), nowUs);
        }
    }
}
