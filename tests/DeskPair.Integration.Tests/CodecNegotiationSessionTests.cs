using DeskPair.Core.Services;
using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Testing;
using DeskPair.Core.Transport;
using Codec = DeskPair.Platform.Abstractions.Codec.VideoCodec;

namespace DeskPair.Integration.Tests;

/// <summary>
/// The host used to encode whatever its configuration named. These check the negotiation end to end, with
/// the fakes standing in for the codecs: what matters is which codec the frames that reach the viewer are
/// actually in, not whether a real encoder was involved.
/// </summary>
public class CodecNegotiationSessionTests
{
    private static FakeVideoEncoderFactory Encodes(params Codec[] codecs) => new() { Codecs = codecs };

    private static FakeVideoDecoderFactory Decodes(params Codec[] codecs) => new() { Codecs = codecs };

    private static async Task<TestCallbacks> RunAsync(
        Testbed bed, HostRuntime host, string password, FakeVideoDecoderFactory decoders, Codec? prefer = null)
    {
        (ControllerSession session, TestCallbacks cb, PeerConnector connector) =
            bed.CreateController(decoders: decoders, preferredCodec: prefer);
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(password, CancellationToken.None)).Success.ShouldBeTrue();
        await Testbed.WaitUntilAsync(() => cb.LastCodec is not null, "the first encoded frame", 15_000);
        return cb;
    }

    [Fact]
    public async Task The_host_encodes_what_it_asked_for_when_the_viewer_can_read_it()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(
            media: true, encoders: Encodes(Codec.H264, Codec.H265), codec: Codec.H265);

        TestCallbacks cb = await RunAsync(bed, host, passwords.TemporaryPassword, Decodes(Codec.H264, Codec.H265));

        cb.LastCodec.ShouldBe(Codec.H265);
        bed.Media!.GetVideoService(0)!.Codec.ShouldBe(Codec.H265);
    }

    /// <summary>
    /// The case that was broken: a host configured for H.265 sent H.265 to a viewer that could only decode
    /// H.264, which left the viewer on lossless tiles, i.e. a still picture, with nothing said about why.
    /// </summary>
    [Fact]
    public async Task A_viewer_that_cannot_read_the_preference_gets_one_it_can()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(
            media: true, encoders: Encodes(Codec.H264, Codec.H265), codec: Codec.H265);

        TestCallbacks cb = await RunAsync(bed, host, passwords.TemporaryPassword, Decodes(Codec.H264));

        cb.LastCodec.ShouldBe(Codec.H264);
    }

    [Fact]
    public async Task A_viewer_can_ask_for_a_codec_when_the_host_has_no_preference()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(
            media: true, encoders: Encodes(Codec.H264, Codec.H265, Codec.Av1), codec: null);

        TestCallbacks cb = await RunAsync(
            bed, host, passwords.TemporaryPassword, Decodes(Codec.H264, Codec.H265, Codec.Av1), prefer: Codec.Av1);

        cb.LastCodec.ShouldBe(Codec.Av1);
    }

    /// <summary>
    /// One stream, one encoder. A second viewer that cannot read what is running makes the host restart the
    /// stream on something both can, rather than leaving the newcomer with no moving picture.
    /// </summary>
    [Fact]
    public async Task A_second_viewer_that_cannot_read_the_stream_moves_the_whole_stream()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(
            media: true, encoders: Encodes(Codec.H264, Codec.H265), codec: Codec.H265);

        TestCallbacks first = await RunAsync(bed, host, passwords.TemporaryPassword, Decodes(Codec.H264, Codec.H265));
        first.LastCodec.ShouldBe(Codec.H265);

        TestCallbacks second = await RunAsync(bed, host, passwords.TemporaryPassword, Decodes(Codec.H264));

        second.LastCodec.ShouldBe(Codec.H264);
        await Testbed.WaitUntilAsync(() => first.LastCodec == Codec.H264, "the first viewer follows the stream down", 15_000);
        bed.Media!.GetVideoService(0)!.Codec.ShouldBe(Codec.H264);
    }

    /// <summary>The host tells the viewer what it can encode, which it never used to: the field went out empty.</summary>
    [Fact]
    public async Task The_login_response_says_what_the_host_can_encode()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(
            media: true, encoders: Encodes(Codec.H264, Codec.Av1));

        (ControllerSession session, _, PeerConnector connector) = bed.CreateController();
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        LoginResult outcome = await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None);

        outcome.Success.ShouldBeTrue();
        session.PeerInfo.ShouldNotBeNull();
        session.PeerInfo.Encoding.ShouldNotBeNull();
        session.PeerInfo.Encoding.H264.ShouldBeTrue();
        session.PeerInfo.Encoding.Av1.ShouldBeTrue();
        session.PeerInfo.Encoding.H265.ShouldBeFalse();
    }
}
