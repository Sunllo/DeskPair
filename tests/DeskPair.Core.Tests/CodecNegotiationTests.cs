using DeskPair.Core.Services;
using DeskPair.Platform.Abstractions.Codec;
using WireCodec = DeskPair.Protocol.Messages.VideoCodec;
using WireDecoding = DeskPair.Protocol.Messages.SupportedDecoding;

namespace DeskPair.Core.Tests;

/// <summary>
/// The host used to encode whatever its configuration named and tell the viewer afterwards. That was safe
/// only while H.264 was the only codec either side could reach; now a wrong choice costs the viewer the
/// moving picture. None of this needs hardware: it is a decision about two sets.
/// </summary>
public class CodecNegotiationTests
{
    private static SupportedCodecs Set(params VideoCodec[] codecs)
    {
        SupportedCodecs set = SupportedCodecs.None;
        foreach (VideoCodec codec in codecs)
        {
            set |= SupportedCodecsExtensions.Pair(codec);
        }

        return set;
    }

    private static CodecNegotiation.Viewer Viewer(VideoCodec? prefers, params VideoCodec[] decodes) =>
        new(Set(decodes), prefers);

    [Fact]
    public void The_hosts_preference_wins_when_everyone_can_read_it()
    {
        VideoCodec chosen = CodecNegotiation.Choose(
            Set(VideoCodec.H264, VideoCodec.H265),
            [Viewer(null, VideoCodec.H264, VideoCodec.H265)],
            VideoCodec.H265);

        chosen.ShouldBe(VideoCodec.H265);
    }

    [Fact]
    public void A_viewer_that_cannot_decode_the_preference_overrules_it()
    {
        VideoCodec chosen = CodecNegotiation.Choose(
            Set(VideoCodec.H264, VideoCodec.H265),
            [Viewer(null, VideoCodec.H264)],
            VideoCodec.H265);

        chosen.ShouldBe(VideoCodec.H264, "a stream the viewer cannot read is worth less than one it can");
    }

    /// <summary>One stream, one encoder: the narrowest viewer sets the codec for everyone on that display.</summary>
    [Fact]
    public void The_narrowest_viewer_holds_the_stream()
    {
        VideoCodec chosen = CodecNegotiation.Choose(
            Set(VideoCodec.H264, VideoCodec.H265, VideoCodec.Av1),
            [
                Viewer(VideoCodec.Av1, VideoCodec.H264, VideoCodec.H265, VideoCodec.Av1),
                Viewer(null, VideoCodec.H264),
            ],
            null);

        chosen.ShouldBe(VideoCodec.H264);
    }

    [Fact]
    public void A_codec_the_host_cannot_encode_is_never_chosen()
    {
        VideoCodec chosen = CodecNegotiation.Choose(
            Set(VideoCodec.H264),
            [Viewer(VideoCodec.Av1, VideoCodec.H264, VideoCodec.Av1)],
            VideoCodec.Av1);

        chosen.ShouldBe(VideoCodec.H264);
    }

    [Fact]
    public void The_viewers_request_is_honoured_when_the_host_asked_for_nothing()
    {
        VideoCodec chosen = CodecNegotiation.Choose(
            Set(VideoCodec.H264, VideoCodec.H265),
            [Viewer(VideoCodec.H265, VideoCodec.H264, VideoCodec.H265)],
            preference: null);

        chosen.ShouldBe(VideoCodec.H265);
    }

    /// <summary>Two viewers asking for different codecs is not a tie to break; fall through to the order.</summary>
    [Fact]
    public void Viewers_that_disagree_fall_back_to_the_default_order()
    {
        VideoCodec chosen = CodecNegotiation.Choose(
            Set(VideoCodec.H264, VideoCodec.H265, VideoCodec.Av1),
            [
                Viewer(VideoCodec.H265, VideoCodec.H264, VideoCodec.H265, VideoCodec.Av1),
                Viewer(VideoCodec.Av1, VideoCodec.H264, VideoCodec.H265, VideoCodec.Av1),
            ],
            preference: null);

        chosen.ShouldBe(VideoCodec.H264);
    }

    /// <summary>
    /// A client that sent nothing is an older one that always meant H.264, and a client with no decoder at
    /// all parses the same way. Both are served by H.264: the first reads it, the second ignores video and
    /// refines from lossless tiles either way.
    /// </summary>
    [Fact]
    public void A_viewer_that_said_nothing_is_read_as_h264()
    {
        var silent = new CodecNegotiation.Viewer(SupportedCodecs.None, null);

        silent.CanDecode(VideoCodec.H264).ShouldBeTrue();
        silent.CanDecode(VideoCodec.H265).ShouldBeFalse();
        CodecNegotiation.Choose(Set(VideoCodec.H264, VideoCodec.H265), [silent], VideoCodec.H265)
            .ShouldBe(VideoCodec.H264);
    }

    /// <summary>
    /// With nothing in common there is no right answer, and returning null would mean deciding not to send
    /// video at all. Handing back the preference keeps the existing "this computer has no X encoder" report,
    /// which names the problem, instead of a session that silently shows nothing.
    /// </summary>
    [Fact]
    public void An_impossible_session_still_returns_something_reportable()
    {
        CodecNegotiation.Choose(SupportedCodecs.None, [Viewer(null, VideoCodec.H264)], VideoCodec.H265)
            .ShouldBe(VideoCodec.H265);
        CodecNegotiation.Choose(SupportedCodecs.None, [Viewer(null, VideoCodec.H264)], null)
            .ShouldBe(VideoCodec.H264);
    }

    [Fact]
    public void No_viewers_means_the_preference_stands()
    {
        CodecNegotiation.Choose(Set(VideoCodec.H264, VideoCodec.H265), [], VideoCodec.H265)
            .ShouldBe(VideoCodec.H265);
        CodecNegotiation.Choose(Set(VideoCodec.H265), [], null)
            .ShouldBe(VideoCodec.H265, "the order skips what the host cannot encode");
    }

    [Fact]
    public void The_wire_carries_the_whole_set_both_ways()
    {
        SupportedCodecs mine = Set(VideoCodec.H264, VideoCodec.Av1);

        WireDecoding decoding = CodecWire.ToDecoding(mine, tiles: true);

        decoding.H264.ShouldBeTrue();
        decoding.Av1.ShouldBeTrue();
        decoding.H265.ShouldBeFalse();
        decoding.Tiles.ShouldBeTrue();
        CodecWire.Decodable(decoding).ShouldBe(mine);
        CodecWire.Decodable(null).ShouldBe(SupportedCodecs.None);
        CodecWire.ToEncoding(mine).Av1.ShouldBeTrue();
    }

    [Fact]
    public void Every_codec_survives_the_round_trip_through_the_wire_enum()
    {
        foreach (VideoCodec codec in Enum.GetValues<VideoCodec>())
        {
            WireCodec wire = VideoService.ToWire(codec);
            VideoService.FromWire(wire).ShouldBe(codec, $"{codec} does not round-trip");
        }
    }
}
