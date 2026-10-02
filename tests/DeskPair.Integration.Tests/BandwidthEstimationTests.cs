using DeskPair.Core.Services;
using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using Microsoft.Extensions.Logging;
using DeskPair.Core.Transport;
using DeskPair.Core.Transport.Udp;
using DeskPair.Protocol.Messages;

namespace DeskPair.Integration.Tests;

/// <summary>Timing-sensitive tests (simulated links with real clocks) run alone, after the parallel ones.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingSensitiveCollection
{
    public const string Name = "Timing-sensitive";
}

/// <summary>GCC bandwidth estimation end to end: host, UDP channel and a simulated bottleneck in front of the viewer.</summary>
[Collection(TimingSensitiveCollection.Name)]
public class BandwidthEstimationTests
{
    [Fact]
    public async Task Bitrate_follows_a_bottleneck_link_down_and_settles_below_it()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, encoders: new DeskPair.Core.Testing.FakeVideoEncoderFactory { PadToBitrate = true });
        DeskPair.Core.Testing.BottleneckDatagramSocket? link = null;
        (ControllerSession session, _, PeerConnector connector) = bed.CreateController(mediaSocketWrapper: inner =>
            link = new DeskPair.Core.Testing.BottleneckDatagramSocket(inner, 2_500_000, TimeSpan.FromMilliseconds(100)));
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();
        await Testbed.WaitUntilAsync(() => session.VideoFramesReceivedUdp >= 10, "UDP video started", 15_000);

        UdpMediaChannel channel = host.Sessions.Single().MediaChannel!;
        int connection = host.Sessions.Single().Context.ConnectionId;
        ILogger log = bed.Logs.CreateLogger("test");

        async Task<List<double>> SampleAsync(int seconds)
        {
            var samples = new List<double>();
            for (int i = 0; i < seconds * 4; i++)
            {
                await Task.Delay(250);
                samples.Add(channel.Gcc.TargetBps);
                log.LogInformation("gcc {Target:F2} Mb/s {State}, acked {Acked:F2}, encoder target {Kbps} kbps, link dropped {Dropped}",
                    channel.Gcc.TargetBps / 1e6, channel.Gcc.State, (channel.Gcc.AckedBps ?? 0) / 1e6, bed.Media!.Qos.TargetBitrateKbps(640, 360), link!.Dropped);
            }

            return samples;
        }

        // 640x360 caps the encoder at about 3 Mb/s, above the 2.5 Mb/s link: GCC has to find the link.
        List<double> first = await SampleAsync(12);
        first.Skip(32).Max().ShouldBeLessThan(2_500_000 * 1.15);
        // Half the capacity when the machine is idle; the simulated link's delivery thread loses time under a full
        // test run, so the floor here only has to prove GCC found the link (it starts 2.6x above it).
        first.Skip(32).Average().ShouldBeGreaterThan(2_500_000 * 0.4);
        bed.Media!.Qos.BandwidthEstimateBps.ShouldNotBeNull();

        link!.CapacityBps = 1_500_000;
        List<double> second = await SampleAsync(6);
        second.Skip(12).Max().ShouldBeLessThan(1_500_000 * 1.2);

        await session.CloseAsync("done");
    }

    [Fact]
    public async Task Start_up_probing_finds_link_capacity_within_seconds_instead_of_ramping_slowly()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(media: true, encoders: new DeskPair.Core.Testing.FakeVideoEncoderFactory { PadToBitrate = true });
        // A custom 20 Mb/s bitrate raises the probe ceiling above the 640x360 test display's cap.
        var options = new SessionOptions { ImageQuality = ImageQuality.IqCustom, CustomBitrateKbps = 20_000 };
        DeskPair.Core.Testing.BottleneckDatagramSocket? link = null;
        (ControllerSession session, _, PeerConnector connector) = bed.CreateController(options: options, mediaSocketWrapper: inner =>
            link = new DeskPair.Core.Testing.BottleneckDatagramSocket(inner, 12_000_000, TimeSpan.FromMilliseconds(100)));
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();
        await Testbed.WaitUntilAsync(() => host.Sessions.Single().MediaChannel is { IsReady: true }, "UDP channel ready", 15_000);

        UdpMediaChannel channel = host.Sessions.Single().MediaChannel!;
        // Without probing GCC would need about 8 s to grow from 6.5 to 11 Mb/s at 8 %/s. What is waited for is a probe
        // raising the estimate that far, which ramping never does, rather than the estimate getting there within 4 s:
        // ramping alone reaches 9 Mb/s in about five, so the clock told the two apart by little, and a CI Mac too busy
        // to send a probe at its rate once missed it. This stream is never idle (the fake encoder fills its bitrate), and
        // only an idle stream is probed after start-up: a spoilt start-up probe is what the start-up retries are for.
        await Testbed.WaitUntilAsync(() => channel.Gcc.ProbedToBps >= 9_000_000, "a probe raised the estimate", 20_000);
        bed.Logs.CreateLogger("test").LogInformation("probes {Probes}, last {Last}, gcc {Target:F1} Mb/s", channel.ProbesSent, channel.LastProbe, channel.Gcc.TargetBps / 1e6);
        channel.Gcc.ProbesApplied.ShouldBeGreaterThan(0);

        await Task.Delay(3000);
        channel.Gcc.TargetBps.ShouldBeLessThan(12_000_000 * 1.2);
        await session.CloseAsync("done");
    }
}
