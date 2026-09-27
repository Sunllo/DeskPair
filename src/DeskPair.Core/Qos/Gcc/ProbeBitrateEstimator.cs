namespace DeskPair.Core.Qos.Gcc;

/// <summary>
/// Turns one probe cluster (a short burst of padding sent at a chosen rate) into a capacity sample, the way
/// libwebrtc's ProbeBitrateEstimator does: the cluster's send rate and receive rate are measured separately, and
/// a receive rate clearly below the send rate means the link saturated at about that rate.
/// </summary>
public static class ProbeBitrateEstimator
{
    /// <summary>A cluster the link truncated still measures it: the survivors arrive spaced by the link rate.</summary>
    private const double MinReceivedFraction = 0.5;
    private const int MinPackets = 5;
    private const double MaxValidRatio = 2.0;
    private const double MinRatioForUnsaturatedLink = 0.9;
    private const double TargetUtilizationFraction = 0.95;
    private const long MaxIntervalUs = 1_000_000;

    /// <summary>Capacity estimate in bits per second, or null when the cluster is unusable (too much loss, bad timing).</summary>
    public static double? Estimate(IReadOnlyList<PacketResult> received, int sentCount)
    {
        if (received.Count < Math.Max(MinPackets, MinReceivedFraction * sentCount))
        {
            return null;
        }

        long firstSend = long.MaxValue, lastSend = long.MinValue, firstArrival = long.MaxValue, lastArrival = long.MinValue;
        int lastSendSize = 0, firstArrivalSize = 0;
        long bytes = 0;
        foreach (PacketResult p in received)
        {
            bytes += p.SizeBytes;
            firstSend = Math.Min(firstSend, p.SendUs);
            if (p.SendUs >= lastSend)
            {
                lastSend = p.SendUs;
                lastSendSize = p.SizeBytes;
            }

            if (p.ArrivalUs < firstArrival)
            {
                firstArrival = p.ArrivalUs;
                firstArrivalSize = p.SizeBytes;
            }

            lastArrival = Math.Max(lastArrival, p.ArrivalUs);
        }

        long sendInterval = lastSend - firstSend;
        long receiveInterval = lastArrival - firstArrival;
        if (sendInterval <= 0 || receiveInterval <= 0 || sendInterval > MaxIntervalUs || receiveInterval > MaxIntervalUs)
        {
            return null;
        }

        // The last packet's bytes were sent after the interval ended; the first packet's arrived before it started.
        double sendBps = (bytes - lastSendSize) * 8.0 * 1_000_000 / sendInterval;
        double receiveBps = (bytes - firstArrivalSize) * 8.0 * 1_000_000 / receiveInterval;
        if (receiveBps > MaxValidRatio * sendBps)
        {
            return null; // arrivals bunched up (receiver scheduling), the timing says nothing about the link
        }

        return receiveBps < MinRatioForUnsaturatedLink * sendBps
            ? TargetUtilizationFraction * receiveBps
            : Math.Min(sendBps, receiveBps);
    }
}
