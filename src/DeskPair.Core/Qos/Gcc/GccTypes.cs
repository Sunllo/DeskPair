namespace DeskPair.Core.Qos.Gcc;

/// <summary>What the delay-based detector concludes about the path.</summary>
public enum BandwidthUsage
{
    Normal,
    Underusing,
    Overusing,
}

/// <summary>One packet the receiver reported: when we sent it (sender clock), when it arrived (receiver clock), and its size.</summary>
public readonly record struct PacketResult(long SendUs, long ArrivalUs, int SizeBytes);
