using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host.Auth;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Transport;

namespace DeskPair.Integration.Tests;

/// <summary>
/// The site with no internet: a host that no rendezvous server has ever seen, reached by its address on the
/// local network. Plus the presence lookup the device list uses, which asks the server about ids and probes
/// addresses directly.
/// </summary>
public class LocalNetworkTests
{
    [Fact]
    public async Task A_host_that_never_reached_a_server_still_has_an_id_and_accepts_a_direct_connection()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, HostPasswords passwords, _) = await bed.StartHostAsync(
            directPort: -1, settings: Testbed.NoServerSettings);

        // Without a server no id was ever assigned, and the handshake refuses an empty one.
        host.Identity.IsLocalId.ShouldBeTrue();
        host.Identity.Id.ShouldStartWith("LAN-");

        (ControllerSession session, _, PeerConnector connector) = bed.CreateController(settings: Testbed.NoServerSettings);
        await session.ConnectAsync(connector, $"127.0.0.1:{host.BoundDirectAccessPort}", CancellationToken.None);
        LoginResult result = await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None);

        result.Success.ShouldBeTrue();
        session.TransportKind.ShouldBe(TransportKind.DirectTcp);
    }

    [Fact]
    public async Task The_server_answers_which_ids_are_online()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, _, _) = await bed.StartHostAsync();
        var presence = new PeerPresence(bed.Settings);

        Dictionary<string, PeerOnlineState> states = await presence.QueryAsync([host.Identity.Id, "999999999"]);

        states[host.Identity.Id].ShouldBe(PeerOnlineState.Online);
        states["999999999"].ShouldBe(PeerOnlineState.Offline);
    }

    [Fact]
    public async Task An_address_is_judged_by_whether_the_direct_port_answers()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, _, _) = await bed.StartHostAsync(directPort: -1, settings: Testbed.NoServerSettings);
        int listening = host.BoundDirectAccessPort;
        int silent = Testbed.SparePort();
        var presence = new PeerPresence(Testbed.NoServerSettings);

        Dictionary<string, PeerOnlineState> states = await presence.QueryAsync([$"127.0.0.1:{listening}", $"127.0.0.1:{silent}"]);

        states[$"127.0.0.1:{listening}"].ShouldBe(PeerOnlineState.Online);
        states[$"127.0.0.1:{silent}"].ShouldBe(PeerOnlineState.Offline);
    }

    [Fact]
    public async Task With_no_server_an_id_cannot_be_judged_rather_than_being_called_offline()
    {
        var presence = new PeerPresence(Testbed.NoServerSettings);

        Dictionary<string, PeerOnlineState> states = await presence.QueryAsync(["123456789"]);

        states["123456789"].ShouldBe(PeerOnlineState.Unknown);
    }

    [Fact]
    public async Task More_ids_than_one_batch_holds_are_all_answered()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, _, _) = await bed.StartHostAsync();
        List<string> targets = Enumerable.Range(0, PeerPresence.MaxBatch + 20).Select(i => (100_000_000 + i).ToString()).ToList();
        targets.Add(host.Identity.Id);
        var presence = new PeerPresence(bed.Settings);

        Dictionary<string, PeerOnlineState> states = await presence.QueryAsync(targets);

        states.Count.ShouldBe(targets.Count);
        states[host.Identity.Id].ShouldBe(PeerOnlineState.Online);
        states.Values.Count(s => s == PeerOnlineState.Offline).ShouldBe(PeerPresence.MaxBatch + 20);
    }
}
