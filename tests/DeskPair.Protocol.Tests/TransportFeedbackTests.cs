using DeskPair.Protocol.Media;

namespace DeskPair.Protocol.Tests;

public class TransportFeedbackTests
{
    [Fact]
    public void Report_round_trips_arrivals_and_counts_gaps_as_lost()
    {
        var arrivals = new List<TransportFeedback.Arrival>
        {
            new(1000, 5_000_000),
            new(1001, 5_000_250),
            new(1004, 5_003_000),
            new(1005, 5_002_750), // arrived before its predecessor: negative delta
            new(1200, 9_000_000),
        };
        byte[] buffer = new byte[TransportFeedback.MaxSize(TransportFeedback.MaxPacketsPerReport)];
        int n = TransportFeedback.Write(arrivals.ToArray(), buffer);

        var back = new List<TransportFeedback.Arrival>();
        TransportFeedback.TryRead(buffer.AsSpan(0, n), back, out int lost).ShouldBeTrue();
        back.ShouldBe(arrivals);
        lost.ShouldBe(201 - 5);
    }

    [Fact]
    public void Arrival_times_are_quantised_to_quarter_milliseconds()
    {
        TransportFeedback.Arrival[] arrivals = [new(7, 1_000), new(8, 1_000 + 1_130)];
        byte[] buffer = new byte[64];
        int n = TransportFeedback.Write(arrivals, buffer);
        var back = new List<TransportFeedback.Arrival>();
        TransportFeedback.TryRead(buffer.AsSpan(0, n), back, out int lost).ShouldBeTrue();
        lost.ShouldBe(0);
        back[1].ArrivalUs.ShouldBe(1_000 + 1_250); // 4.52 steps round to 5
    }

    [Fact]
    public void A_full_report_fits_one_datagram()
    {
        var arrivals = new TransportFeedback.Arrival[200];
        for (int i = 0; i < arrivals.Length; i++)
        {
            arrivals[i] = new TransportFeedback.Arrival((ulong)(50 + i), 1_000_000 + i * 4_000L);
        }

        byte[] buffer = new byte[TransportFeedback.MaxSize(TransportFeedback.MaxPacketsPerReport)];
        int n = TransportFeedback.Write(arrivals, buffer);
        n.ShouldBeLessThan(ProtocolConstants.MaxUdpDatagramBytes - ProtocolConstants.MediaCommonHeaderBytes - ProtocolConstants.MediaTagBytes);
    }

    [Fact]
    public void Truncated_or_empty_reports_are_rejected()
    {
        var back = new List<TransportFeedback.Arrival>();
        TransportFeedback.TryRead(new byte[10], back, out _).ShouldBeFalse();
        TransportFeedback.TryRead(new byte[TransportFeedback.HeaderSize], back, out _).ShouldBeFalse(); // count 0

        TransportFeedback.Arrival[] arrivals = [new(1, 0), new(2, 10_000), new(3, 20_000)];
        byte[] buffer = new byte[64];
        int n = TransportFeedback.Write(arrivals, buffer);
        TransportFeedback.TryRead(buffer.AsSpan(0, n - 1), back, out _).ShouldBeFalse();
    }
}
