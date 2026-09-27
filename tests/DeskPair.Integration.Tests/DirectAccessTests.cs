using System.Net;
using System.Net.Sockets;
using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;

namespace DeskPair.Integration.Tests;

/// <summary>The direct-access listener, when its port is not free at first.</summary>
public class DirectAccessTests
{
    /// <summary>
    /// Seen on a real machine: the app handed its engine to the service while its own was still shutting down, so
    /// the service's engine found the port taken, turned direct access off, and left it off until it restarted.
    /// It now takes the port the moment it is let go.
    /// </summary>
    [Fact]
    public async Task A_port_another_program_holds_is_taken_once_it_is_free()
    {
        using var squatter = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
        squatter.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        squatter.Listen(1);
        int port = ((IPEndPoint)squatter.LocalEndPoint!).Port;

        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(directPort: port, settings: Testbed.NoServerSettings);
        host.BoundDirectAccessPort.ShouldBe(0, "somebody else has it");

        squatter.Close();
        await Testbed.WaitUntilAsync(() => host.BoundDirectAccessPort == port, "listening once the port was let go", 15_000);

        (ControllerSession session, _, DeskPair.Core.Transport.PeerConnector connector) = bed.CreateController(settings: Testbed.NoServerSettings);
        await session.ConnectAsync(connector, $"127.0.0.1:{port}", CancellationToken.None);
        (await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();
    }
}
