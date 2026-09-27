using DeskPair.Platform.Abstractions.Codec;
using WireDecoding = DeskPair.Protocol.Messages.SupportedDecoding;
using WireEncoding = DeskPair.Protocol.Messages.SupportedEncoding;

namespace DeskPair.Core.Services;

/// <summary>
/// Picks the video codec a stream will use. Until now there was nothing to pick: the host encoded whatever
/// its configuration named and the viewer was told afterwards, which was harmless only because H.264 was the
/// single codec either side could reach. With H.265 and AV1 both real, sending a codec the viewer cannot
/// decode costs it the whole picture — the viewer falls back to lossless tiles, which is a still image.
///
/// The rules, in order:
///   1. A codec the host cannot encode is never chosen, whatever anyone asked for.
///   2. A codec any current viewer cannot decode is never chosen. One viewer that only speaks H.264 holds
///      the whole stream at H.264, because a stream has one encoder and every subscriber reads it.
///   3. Among what is left, the host's configured preference wins, then the viewer's, then <see cref="Order"/>.
/// </summary>
public static class CodecNegotiation
{
    /// <summary>
    /// The fallback order when nobody expressed a preference. H.264 first deliberately: it is the codec every
    /// machine has, its decoders are the best tested, and the newer codecs buy bitrate that a desk session on
    /// a local network is usually not short of. A host that wants H.265 asks for it.
    /// </summary>
    public static readonly VideoCodec[] Order = [VideoCodec.H264, VideoCodec.H265, VideoCodec.Av1, VideoCodec.Vp9, VideoCodec.Vp8];

    /// <summary>
    /// What one viewer can decode. <see cref="SupportedCodecs.None"/> covers two cases that the wire cannot
    /// tell apart, because an absent message and an all-false one parse the same: an older client that never
    /// said, and a client with no decoder at all. Both are read as H.264, and both end up in the same place —
    /// the older client gets the codec it always assumed, and the one with no decoder ignores the video and
    /// refines from lossless tiles, which is what it would have done whatever the host chose.
    /// </summary>
    public readonly record struct Viewer(SupportedCodecs Decodable, VideoCodec? Prefers)
    {
        public bool CanDecode(VideoCodec codec) => Decodable == SupportedCodecs.None
            ? codec == VideoCodec.H264
            : Decodable.Supports(codec);
    }

    /// <summary>
    /// The codec to encode in. Never returns null: when nothing satisfies everyone it returns
    /// <paramref name="preference"/> (or H.264), so the caller still starts a stream and the existing
    /// "this computer has no X encoder" path reports it, rather than this silently deciding not to send video.
    /// </summary>
    public static VideoCodec Choose(SupportedCodecs hostCanEncode, IReadOnlyCollection<Viewer> viewers, VideoCodec? preference)
    {
        bool Usable(VideoCodec codec) =>
            hostCanEncode.Supports(codec) && viewers.All(v => v.CanDecode(codec));

        if (preference is { } wanted && Usable(wanted))
        {
            return wanted;
        }

        // The viewers' own preference, but only when they agree; two viewers wanting different codecs is not
        // a tie to break, it is a reason to fall through to the order below.
        VideoCodec[] asked = [.. viewers.Select(v => v.Prefers).OfType<VideoCodec>().Distinct()];
        if (asked is [VideoCodec single] && Usable(single))
        {
            return single;
        }

        foreach (VideoCodec codec in Order)
        {
            if (Usable(codec))
            {
                return codec;
            }
        }

        return preference ?? VideoCodec.H264;
    }
}

/// <summary>Moves codec sets between the abstraction's flags and the protocol's booleans.</summary>
public static class CodecWire
{
    /// <summary>
    /// What a viewer's <c>SupportedDecoding</c> means as flags. The wire does not distinguish hardware from
    /// software, and a viewer has no reason to care, so each codec contributes both bits.
    /// </summary>
    public static SupportedCodecs Decodable(WireDecoding? decoding)
    {
        if (decoding is null)
        {
            return SupportedCodecs.None;
        }

        SupportedCodecs set = SupportedCodecs.None;
        if (decoding.H264)
        {
            set |= SupportedCodecsExtensions.Pair(VideoCodec.H264);
        }

        if (decoding.H265)
        {
            set |= SupportedCodecsExtensions.Pair(VideoCodec.H265);
        }

        if (decoding.Vp8)
        {
            set |= SupportedCodecsExtensions.Pair(VideoCodec.Vp8);
        }

        if (decoding.Vp9)
        {
            set |= SupportedCodecsExtensions.Pair(VideoCodec.Vp9);
        }

        if (decoding.Av1)
        {
            set |= SupportedCodecsExtensions.Pair(VideoCodec.Av1);
        }

        return set;
    }

    /// <summary>Fills a viewer's <c>SupportedDecoding</c> from what its decoder factory found.</summary>
    public static WireDecoding ToDecoding(SupportedCodecs set, bool tiles) => new()
    {
        H264 = set.Supports(VideoCodec.H264),
        H265 = set.Supports(VideoCodec.H265),
        Vp8 = set.Supports(VideoCodec.Vp8),
        Vp9 = set.Supports(VideoCodec.Vp9),
        Av1 = set.Supports(VideoCodec.Av1),
        Tiles = tiles,
    };

    /// <summary>Fills the host's <c>SupportedEncoding</c>, which the viewer shows and may reason about.</summary>
    public static WireEncoding ToEncoding(SupportedCodecs set) => new()
    {
        H264 = set.Supports(VideoCodec.H264),
        H265 = set.Supports(VideoCodec.H265),
        Vp8 = set.Supports(VideoCodec.Vp8),
        Vp9 = set.Supports(VideoCodec.Vp9),
        Av1 = set.Supports(VideoCodec.Av1),
    };
}
