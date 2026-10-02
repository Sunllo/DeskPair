using DeskPair.Core.Transport.Udp;

namespace DeskPair.Core.Tests.Udp;

/// <summary>
/// When a bandwidth probe measures this machine instead of the link. A 13 Mb/s start-up probe is 41 packets meant to
/// leave within 30 ms; what decides is how fast it actually left and whether the link kept up with that.
/// </summary>
public class ProbeSenderLimitTests
{
    private const double Rate = 13_000_000;
    private const int Count = 41;

    [Fact]
    public void A_probe_a_busy_machine_spread_over_three_times_its_span_measures_the_machine()
    {
        // Left at a third of its rate, all of it arrived at that rate: the link was never asked for 13 Mb/s.
        UdpMediaChannel.LeftTooSlowlyToMeasure(Rate, Count, sentBps: 4_300_000, received: 41, arrivedBps: 4_200_000).ShouldBeTrue();
    }

    [Fact]
    public void A_probe_sent_on_time_into_a_thin_link_measures_the_link()
    {
        UdpMediaChannel.LeftTooSlowlyToMeasure(Rate, Count, sentBps: 12_900_000, received: 33, arrivedBps: 2_400_000).ShouldBeFalse();
    }

    [Fact]
    public void A_link_that_falls_behind_even_a_slow_send_is_still_the_limit()
    {
        // Sent at 6 Mb/s, delivered at 2.4: whatever the machine managed, the link carried less.
        UdpMediaChannel.LeftTooSlowlyToMeasure(Rate, Count, sentBps: 6_000_000, received: 41, arrivedBps: 2_400_000).ShouldBeFalse();
    }

    [Fact]
    public void A_slow_send_that_lost_much_of_itself_is_judged_on_the_link()
    {
        UdpMediaChannel.LeftTooSlowlyToMeasure(Rate, Count, sentBps: 5_000_000, received: 20, arrivedBps: 4_900_000).ShouldBeFalse();
    }

    [Fact]
    public void A_probe_that_left_close_to_its_rate_is_used_as_measured()
    {
        UdpMediaChannel.LeftTooSlowlyToMeasure(Rate, Count, sentBps: 12_900_000, received: 41, arrivedBps: 12_000_000).ShouldBeFalse();
        UdpMediaChannel.LeftTooSlowlyToMeasure(Rate, Count, sentBps: 0.75 * Rate, received: 41, arrivedBps: 0.75 * Rate).ShouldBeFalse();
    }

    [Fact]
    public void Nothing_measured_is_left_to_the_unusable_path()
    {
        UdpMediaChannel.LeftTooSlowlyToMeasure(Rate, Count, sentBps: 4_000_000, received: 41, arrivedBps: null).ShouldBeFalse();
    }
}
