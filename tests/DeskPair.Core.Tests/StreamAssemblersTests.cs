using DeskPair.Core.Transport.Udp;
using DeskPair.Protocol.Media;

namespace DeskPair.Core.Tests;

/// <summary>
/// The UDP receiver's streams across a display switch, and with several displays at once. Each of the host's
/// displays is its own stream with its own sequence numbers starting at one, so "older than what I already
/// delivered" -- and every counter the viewer reports back -- must be judged per stream.
/// </summary>
public class StreamAssemblersTests
{
    private readonly StreamAssemblers _assembler = new(TimeProvider.System);
    private ulong _packetSeq;

    /// <summary>One frame in one shard (k = 1, no parity), so the assembler's ordering rules are all that is exercised.</summary>
    private void Feed(byte stream, uint frameSeq, bool keyFrame)
    {
        byte[] payload = [1, 2, 3, (byte)frameSeq];
        var header = new MediaShardHeader(stream, 0, 0, 1, frameSeq, frameSeq * 16, 0, 1, 0, (ushort)payload.Length, (uint)payload.Length, 640, 480);
        byte[] plaintext = new byte[MediaShardHeader.Size + payload.Length];
        header.Write(plaintext);
        payload.CopyTo(plaintext, MediaShardHeader.Size);
        var common = new MediaCommonHeader(MediaPacketType.Video, keyFrame ? MediaPacketFlags.KeyFrame : MediaPacketFlags.None, ++_packetSeq);
        _assembler.Accept(in common, plaintext);
    }

    private List<(byte Stream, uint Seq)> Drain()
    {
        var got = new List<(byte, uint)>();
        while (_assembler.TryDequeue(out AssembledFrame frame))
        {
            got.Add((frame.Stream, frame.FrameSeq));
            frame.Return();
        }

        return got;
    }

    /// <summary>The bug: after 600 frames of display 0, display 1's frame 1 looked "already delivered" and never came out.</summary>
    [Fact]
    public void A_keyframe_from_another_display_starts_its_own_sequence()
    {
        Feed(0, 600, keyFrame: true);
        Feed(0, 601, keyFrame: false);
        Drain().ShouldBe([(0, 600u), (0, 601u)]);

        Feed(1, 1, keyFrame: true);
        Feed(1, 2, keyFrame: false);

        Drain().ShouldBe([(1, 1u), (1, 2u)], "the new display's frames are numbered from one and must not be taken for old ones");
    }

    [Fact]
    public void A_delta_from_another_display_is_dropped_but_a_keyframe_switches()
    {
        Feed(0, 10, keyFrame: true);
        Drain().Count.ShouldBe(1);

        Feed(1, 5, keyFrame: false);
        Drain().ShouldBeEmpty("a delta without its keyframe has nothing to decode against");

        Feed(1, 6, keyFrame: true);
        Drain().ShouldBe([(1, 6u)]);
    }

    /// <summary>Once the session knows which display it is on, a late keyframe from the previous one must not flip it back.</summary>
    [Fact]
    public void After_the_switch_is_confirmed_stragglers_from_the_old_display_are_ignored()
    {
        Feed(0, 10, keyFrame: true);
        Drain().Count.ShouldBe(1);

        _assembler.Expect(1);
        Feed(0, 11, keyFrame: true); // in flight when the switch happened
        Drain().ShouldBeEmpty();
        _assembler.ReferenceBroken.ShouldBeTrue("nothing of the new display has arrived yet");

        Feed(1, 1, keyFrame: false);
        Drain().ShouldBeEmpty("a delta before the new display's first keyframe has nothing to decode against");

        Feed(1, 2, keyFrame: true);
        Feed(1, 3, keyFrame: false);
        Drain().ShouldBe([(1, 2u), (1, 3u)]);
        _assembler.ReferenceBroken.ShouldBeFalse();
    }

    /// <summary>The confirmation may arrive after the new display's first keyframe; that must not throw the keyframe away.</summary>
    [Fact]
    public void Confirming_the_display_already_being_shown_keeps_what_arrived()
    {
        Feed(0, 10, keyFrame: true);
        Drain().Count.ShouldBe(1);
        Feed(1, 1, keyFrame: true);

        _assembler.Expect(1);
        Feed(1, 2, keyFrame: false);

        Drain().ShouldBe([(1, 1u), (1, 2u)]);
    }

    /// <summary>The point of the split: two confirmed displays both come out, each in its own order.</summary>
    [Fact]
    public void Two_confirmed_displays_both_deliver()
    {
        _assembler.Expect([0, 1]);
        Feed(0, 1, keyFrame: true);
        Feed(1, 1, keyFrame: true);
        Feed(0, 2, keyFrame: false);
        Feed(1, 2, keyFrame: false);

        List<(byte Stream, uint Seq)> got = Drain();
        got.Where(f => f.Stream == 0).ShouldBe([(0, 1u), (0, 2u)]);
        got.Where(f => f.Stream == 1).ShouldBe([(1, 1u), (1, 2u)]);

        Feed(2, 1, keyFrame: true);
        Drain().ShouldBeEmpty("a display outside the set is a straggler, even a keyframe of it");
    }

    /// <summary>
    /// What each stream reports is its own. After six hundred frames of display 0 the report for display 1 said
    /// 600 was decodable, and the host took display 1 to be caught up long before it was.
    /// </summary>
    [Fact]
    public void A_new_display_reports_its_own_counts_not_the_last_ones()
    {
        Feed(0, 600, keyFrame: true);
        Drain();

        _assembler.Expect(1);
        Feed(1, 1, keyFrame: true);
        Drain().ShouldBe([(1, 1u)]);

        var live = new List<FrameAssembler>();
        _assembler.CopyStreams(live);
        FrameAssembler shown = live.ShouldHaveSingleItem();
        shown.Stream.ShouldBe((byte)1);
        shown.HighestDecodable.ShouldBe(1u);
        shown.LastFrameSeqReceived.ShouldBe(1u);
    }

    /// <summary>A lost frame on one display asks for a keyframe of that display and leaves the other alone.</summary>
    [Fact]
    public void A_broken_stream_is_broken_on_its_own()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var streams = new StreamAssemblers(time);
        streams.Expect([0, 1]);
        FeedTo(streams, 0, 1, keyFrame: true);
        FeedTo(streams, 1, 1, keyFrame: true);
        DrainFrom(streams);

        // Half of display 0's frame 2 arrives and the rest never does; frame 3 does, and after the reorder
        // wait frame 2 is given up.
        FeedTo(streams, 0, 2, keyFrame: false, k: 2);
        FeedTo(streams, 0, 3, keyFrame: false);
        time.Advance(TimeSpan.FromMilliseconds(200));
        streams.Tick();
        DrainFrom(streams);

        streams.IsBroken(0).ShouldBeTrue();
        streams.IsBroken(1).ShouldBeFalse();
        streams.IsBroken(7).ShouldBeFalse("a display not arriving over UDP has nothing to be broken");
    }

    /// <summary>The first of <paramref name="k"/> data shards of a frame; with k above one the frame stays incomplete.</summary>
    private void FeedTo(StreamAssemblers streams, byte stream, uint frameSeq, bool keyFrame, byte k = 1)
    {
        byte[] payload = [1, 2, 3, (byte)frameSeq];
        var header = new MediaShardHeader(stream, 0, 0, 1, frameSeq, frameSeq * 16, 0, k, 0, (ushort)payload.Length, (uint)(payload.Length * k), 640, 480);
        byte[] plaintext = new byte[MediaShardHeader.Size + payload.Length];
        header.Write(plaintext);
        payload.CopyTo(plaintext, MediaShardHeader.Size);
        var common = new MediaCommonHeader(MediaPacketType.Video, keyFrame ? MediaPacketFlags.KeyFrame : MediaPacketFlags.None, ++_packetSeq);
        streams.Accept(in common, plaintext);
    }

    private static void DrainFrom(StreamAssemblers streams)
    {
        while (streams.TryDequeue(out AssembledFrame frame))
        {
            frame.Return();
        }
    }
}

/// <summary>What the path delivered, counted across every stream and every control packet on it.</summary>
public class LinkStatsTests
{
    [Fact]
    public void Loss_is_read_from_gaps_in_the_one_packet_sequence()
    {
        var link = new LinkStats(new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
        foreach (ulong seq in new ulong[] { 1, 2, 3, 5, 6, 7, 8, 9, 10, 11 })
        {
            link.NotePacket(seq, 100);
        }

        link.LossPermille.ShouldBe(90, "one packet of eleven missing, truncated to whole per mille");
        link.ReceivedBytes.ShouldBe(1000ul);
    }
}
