namespace DeskPair.Core.Qos.Gcc;

/// <summary>
/// Groups packets sent within a short burst (5 ms) and yields, for each completed group, how much longer (or shorter)
/// the group took to arrive than to be sent after the previous group: the raw signal of a queue growing or draining.
/// </summary>
public sealed class InterArrival
{
    public const long BurstUs = 5_000;

    private Group? _current;
    private Group? _previous;

    /// <summary>Adds a packet (in send order). Returns true when a group completed and the deltas are valid.</summary>
    public bool TryAdd(in PacketResult packet, out double sendDeltaMs, out double arrivalDeltaMs, out long groupArrivalUs)
    {
        sendDeltaMs = arrivalDeltaMs = 0;
        groupArrivalUs = 0;
        if (_current is null)
        {
            _current = new Group(packet);
            return false;
        }

        if (packet.SendUs < _current.FirstSendUs)
        {
            return false; // belongs to an older group; ignore
        }

        bool sameBurst = packet.SendUs - _current.FirstSendUs <= BurstUs;

        // Packets arriving in a tight burst right after the group (a queue releasing) belong to it as well.
        long arrivalGap = packet.ArrivalUs - _current.LastArrivalUs;
        long sendGap = packet.SendUs - _current.LastSendUs;
        bool arrivalBurst = arrivalGap < BurstUs && arrivalGap - sendGap < 0;
        if (sameBurst || arrivalBurst)
        {
            _current.Add(packet);
            return false;
        }

        bool produced = false;
        if (_previous is not null)
        {
            sendDeltaMs = (_current.LastSendUs - _previous.LastSendUs) / 1000.0;
            arrivalDeltaMs = (_current.LastArrivalUs - _previous.LastArrivalUs) / 1000.0;
            groupArrivalUs = _current.LastArrivalUs;
            produced = arrivalDeltaMs >= 0; // a negative arrival delta means reordering; skip the sample
        }

        _previous = _current;
        _current = new Group(packet);
        return produced;
    }

    private sealed class Group
    {
        public Group(in PacketResult p)
        {
            FirstSendUs = LastSendUs = p.SendUs;
            LastArrivalUs = p.ArrivalUs;
        }

        public long FirstSendUs { get; }

        public long LastSendUs { get; private set; }

        public long LastArrivalUs { get; private set; }

        public void Add(in PacketResult p)
        {
            LastSendUs = Math.Max(LastSendUs, p.SendUs);
            LastArrivalUs = Math.Max(LastArrivalUs, p.ArrivalUs);
        }
    }
}
