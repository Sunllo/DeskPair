using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Transport;
using DeskPair.Core.Transport.Nat;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Integration.Tests;

public class NatTraversalTests
{
    private static async Task<(ControllerSession Session, PeerConnector Connector)> ConnectAsync(Testbed bed, HostRuntime host, string password, bool forceRelay = false)
    {
        (ControllerSession session, _, PeerConnector connector) = bed.CreateController(forceRelay: forceRelay);
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(password, CancellationToken.None)).Success.ShouldBeTrue();
        return (session, connector);
    }

    [Fact]
    public async Task Nat_type_is_asymmetric_on_loopback()
    {
        await using Testbed bed = await Testbed.StartAsync();
        var detector = new NatTypeDetector(bed.Settings, bed.Logs.CreateLogger("nat"));
        (await detector.DetectAsync(CancellationToken.None)).ShouldBe(NatType.NatAsymmetric);
        detector.Cached.ShouldBe(NatType.NatAsymmetric);
    }

    [Fact]
    public async Task Same_public_ip_connects_over_the_lan_address()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(); // no direct listener: the LAN offer opens its own port
        (ControllerSession session, PeerConnector connector) = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        session.TransportKind.ShouldBe(TransportKind.Lan);
        connector.LastTransport.ShouldBe(TransportKind.Lan);
        host.Sessions.Single().TransportKind.ShouldBe(TransportKind.Lan);
        await session.CloseAsync("done");
    }

    [Fact]
    public async Task Tcp_hole_punch_connects_when_the_server_skips_the_lan_shortcut()
    {
        await using Testbed bed = await Testbed.StartAsync(o => o.AlwaysPunch = true);
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync();
        (ControllerSession session, PeerConnector connector) = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        session.TransportKind.ShouldBe(TransportKind.PunchedTcp);
        connector.LastTransport.ShouldBe(TransportKind.PunchedTcp);
        host.Sessions.Single().TransportKind.ShouldBe(TransportKind.PunchedTcp);
        await session.CloseAsync("done");
    }

    [Fact]
    public async Task Host_behind_symmetric_nat_answers_with_a_relay()
    {
        await using Testbed bed = await Testbed.StartAsync(o => o.AlwaysPunch = true);
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync();
        host.PreferRelay = true;
        (ControllerSession session, PeerConnector connector) = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        session.TransportKind.ShouldBe(TransportKind.Relay);
        connector.LastTransport.ShouldBe(TransportKind.Relay);
        await session.CloseAsync("done");
    }

    [Fact]
    public async Task Failed_direct_attempt_falls_back_to_a_relay()
    {
        await using Testbed bed = await Testbed.StartAsync(o => o.AlwaysPunch = true);
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync();
        host.Puncher.DebugUnreachable = true; // host announces itself but never listens or connects
        (ControllerSession session, PeerConnector connector) = await ConnectAsync(bed, host, passwords.TemporaryPassword);
        session.TransportKind.ShouldBe(TransportKind.Relay);
        connector.LastTransport.ShouldBe(TransportKind.Relay);
        await session.CloseAsync("done");
    }

    [Fact]
    public async Task Forced_relay_skips_direct_paths()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync();
        (ControllerSession session, _) = await ConnectAsync(bed, host, passwords.TemporaryPassword, forceRelay: true);
        session.TransportKind.ShouldBe(TransportKind.Relay);
        await session.CloseAsync("done");
    }

    [Fact]
    public async Task Unanswered_punch_reports_the_host_offline()
    {
        await using Testbed bed = await Testbed.StartAsync(o =>
        {
            o.AlwaysPunch = true;
            o.PunchPendingTimeout = TimeSpan.FromSeconds(2);
        });
        (HostRuntime host, _, _) = await bed.StartHostAsync();
        string id = host.Identity.Id;
        await host.DisposeAsync(); // registered but no longer listening for server pushes
        (ControllerSession session, _, PeerConnector connector) = bed.CreateController();
        PeerConnectException e = await Should.ThrowAsync<PeerConnectException>(() => session.ConnectAsync(connector, id, CancellationToken.None));
        e.Failure.ShouldBe(PunchHoleResponse.Types.Failure.Offline);
    }
}
