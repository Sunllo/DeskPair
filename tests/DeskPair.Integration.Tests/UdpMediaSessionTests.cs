using DeskPair.Core.Services;
using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using Microsoft.Extensions.Logging;
using DeskPair.Core.Transport;
using DeskPair.Core.Transport.Udp;
using DeskPair.Protocol.Messages;

namespace DeskPair.Integration.Tests;

/// <summary>Video over the UDP media channel: direct, lossy, relayed, and the fallbacks to TCP.</summary>
public class UdpMediaSessionTests
{
    private static async Task<(ControllerSession Session, TestCallbacks Cb, HostRuntime Host)> ConnectAsync(Testbed bed, bool forceRelay = false, bool udpMedia = true, double loss = 0, int displays = 1)
    {
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, displays: displays);
        (ControllerSession session, TestCallbacks cb, PeerConnector connector) = bed.CreateController(forceRelay: forceRelay, udpMedia: udpMedia, mediaLoss: loss);
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();
        return (session, cb, host);
    }

    [Fact]
    public async Task Video_moves_to_a_direct_udp_path_and_keeps_flowing()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (ControllerSession session, TestCallbacks cb, HostRuntime host) = await ConnectAsync(bed);

        await Testbed.WaitUntilAsync(() => session.MediaPath is not null, "controller verified a UDP path", 15_000);
        await Testbed.WaitUntilAsync(() => host.Sessions.Single().Context.MediaPath is not null, "host verified a UDP path", 15_000);
        session.MediaPath!.Kind.ShouldBe(MediaCandidate.Types.Kind.Local);

        await Testbed.WaitUntilAsync(() => session.VideoFramesReceivedUdp >= 30, "30 frames over UDP", 15_000);
        HostSession hostSession = host.Sessions.Single();
        hostSession.MediaFramesSent.ShouldBeGreaterThanOrEqualTo(30);
        cb.LastFrameSize.ShouldBe((640, 360));
        await Testbed.WaitUntilAsync(() => !bed.Media!.Qos.IsCongested(hostSession.Context.ConnectionId, 0), "feedback acks frames");
        bed.Media!.Qos.LastRoundTrip(hostSession.Context.ConnectionId).ShouldNotBeNull();

        // Bind probes to the other candidates share the sequence space; they must not read as loss on this path.
        await Task.Delay(1000);
        hostSession.MediaChannel!.Gcc.Reports.ShouldBeGreaterThan(0);
        hostSession.MediaChannel!.Gcc.LossFraction.ShouldBeLessThan(0.02);

        await session.CloseAsync("done");
    }

    /// <summary>
    /// The viewer's datagram reports have to say which stream they are about. They said stream 0 whatever was
    /// being watched, and datagram frames are acknowledged only by those reports, so on a second display the
    /// host never saw a frame of it acknowledged, counted every one as still in flight, and a handful of
    /// frames after the switch decided the link was congested and stopped sending.
    /// </summary>
    [Fact]
    public async Task A_second_display_over_udp_is_acknowledged_as_itself()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (ControllerSession session, TestCallbacks cb, HostRuntime host) = await ConnectAsync(bed, displays: 2);
        await Testbed.WaitUntilAsync(() => session.VideoFramesReceivedUdp >= 10, "UDP video on display 0", 15_000);

        await session.SwitchDisplayAsync(1);
        await Testbed.WaitUntilAsync(() => cb.FramesByDisplay.GetValueOrDefault(1) >= 3, "frames on display 1", 15_000);
        int onSecond = cb.FramesByDisplay.GetValueOrDefault(1);
        long udpBefore = session.VideoFramesReceivedUdp;

        // Both counts are waited for, rather than the second checked once the first is reached: a frame is counted as
        // received before it is decoded, so a frame or two received before the start can be among the sixty decoded
        // after it, and the check then found fifty-nine received since (once in fourteen runs). A TCP fallback still
        // fails: the UDP count stops.
        await Testbed.WaitUntilAsync(
            () => cb.FramesByDisplay.GetValueOrDefault(1) >= onSecond + 60 && session.VideoFramesReceivedUdp >= udpBefore + 60,
            "display 1 keeps coming, over UDP rather than a TCP fallback",
            15_000);
        bed.Media!.Qos.IsCongested(host.Sessions.Single().Context.ConnectionId, 1).ShouldBeFalse();

        await session.CloseAsync("done");
    }

    [Fact]
    public async Task Eight_percent_packet_loss_is_repaired_by_fec_without_keyframe_storms()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (ControllerSession session, TestCallbacks cb, HostRuntime host) = await ConnectAsync(bed, loss: 0.08);

        await Testbed.WaitUntilAsync(() => session.VideoFramesReceivedUdp >= 10, "UDP video started", 15_000);
        long sentBefore = host.Sessions.Single().MediaFramesSent;
        long receivedBefore = session.VideoFramesReceivedUdp;
        int keyframesBefore = cb.KeyFrames;
        await Task.Delay(5000);
        long sent = host.Sessions.Single().MediaFramesSent - sentBefore;
        long received = session.VideoFramesReceivedUdp - receivedBefore;
        UdpMediaChannel hostChannel = host.Sessions.Single().MediaChannel!;
        UdpMediaChannel viewerChannel = session.MediaChannel!;
        bed.Logs.CreateLogger("test").LogInformation("lossy: sent {Sent} received {Received}; host loss estimate {Loss:P1}; viewer loss {Permille}‰, given up {GivenUp}, recovered {Recovered}, rejected {Rejected}",
            sent, received, hostChannel.Planner.Loss, viewerChannel.Link.LossPermille, viewerChannel.Streams.FramesGivenUp, viewerChannel.Streams.ShardsRecovered, viewerChannel.DatagramsRejected);
        received.ShouldBeGreaterThan((long)(sent * 0.95));
        (cb.KeyFrames - keyframesBefore).ShouldBeLessThanOrEqualTo(3);
        session.MediaChannel!.Streams.ShardsRecovered.ShouldBeGreaterThan(0u);
        hostChannel.Planner.Loss.ShouldBeGreaterThan(0.02);

        await session.CloseAsync("done");
    }

    [Fact]
    public async Task Relayed_sessions_use_the_udp_relay()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (ControllerSession session, _, HostRuntime host) = await ConnectAsync(bed, forceRelay: true);
        session.TransportKind.ShouldBe(TransportKind.Relay);

        await Testbed.WaitUntilAsync(() => session.MediaPath is not null && host.Sessions.Single().Context.MediaPath is not null, "UDP path through the test bed", 15_000);
        // Loopback candidates win over the relay on one machine; the relay pairing itself is covered by the relay tests.
        await Testbed.WaitUntilAsync(() => session.VideoFramesReceivedUdp >= 10, "video over UDP", 15_000);
        await session.CloseAsync("done");
    }

    [Fact]
    public async Task A_viewer_without_udp_stays_on_tcp()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (ControllerSession session, TestCallbacks cb, HostRuntime host) = await ConnectAsync(bed, udpMedia: false);
        await Testbed.WaitUntilAsync(() => cb.VideoFrames >= 20, "TCP video", 15_000);
        session.MediaPath.ShouldBeNull();
        session.VideoFramesReceivedUdp.ShouldBe(0);
        host.Sessions.Single().MediaFramesSent.ShouldBe(0);
        await session.CloseAsync("done");
    }

    [Fact]
    public async Task Losing_the_udp_path_falls_back_to_tcp_with_a_keyframe()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (ControllerSession session, TestCallbacks cb, HostRuntime host) = await ConnectAsync(bed);
        await Testbed.WaitUntilAsync(() => session.VideoFramesReceivedUdp >= 10, "UDP video", 15_000);

        // The controller drops its channel: the host notices the close, goes back to TCP and refreshes.
        await session.MediaChannel!.DisposeAsync();
        long udpBefore = session.VideoFramesReceivedUdp;
        await Testbed.WaitUntilAsync(() => host.Sessions.Single().Context.MediaPath is null, "host fell back to TCP", 15_000);
        int tcpBefore = (int)(session.VideoFramesReceived - session.VideoFramesReceivedUdp);
        await Testbed.WaitUntilAsync(() => session.VideoFramesReceived - session.VideoFramesReceivedUdp > tcpBefore + 10, "video resumes on TCP", 15_000);
        session.VideoFramesReceivedUdp.ShouldBe(udpBefore);
        await session.CloseAsync("done");
    }
}
