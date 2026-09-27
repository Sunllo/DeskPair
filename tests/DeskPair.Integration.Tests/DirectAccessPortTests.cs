using System.Net;
using System.Net.Sockets;
using DeskPair.Core.Config;
using DeskPair.Core.Ipc;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Session.Host.Auth;

namespace DeskPair.Integration.Tests;

/// <summary>
/// Direct access is one way in, not the only one. Found on Linux, where an older build of DeskPair was still
/// holding 21118: the bind failed, the exception came back out of StartAsync, and the headless engine ended as
/// a core dump before it could say why.
/// </summary>
public class DirectAccessPortTests
{
    [Fact]
    public async Task A_direct_access_port_someone_else_holds_does_not_stop_the_host()
    {
        await using Testbed bed = await Testbed.StartAsync();

        // Hold a port the way another program would, then ask the host for that exact one.
        using var squatter = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
        squatter.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        squatter.Listen(1);
        int taken = ((IPEndPoint)squatter.LocalEndPoint!).Port;

        string dir = bed.NewTempDir();
        var store = new FileSecretStore(dir);
        PeerIdentityStore identity = await PeerIdentityStore.LoadOrCreateAsync(store, await StoredMachineIdProvider.LoadOrCreateAsync(store));
        HostPasswords passwords = await HostPasswords.LoadAsync(store);
        var bridge = new HostIpcBridge(new HostConfigStore(Path.Combine(dir, "config.json")), bed.Logs.CreateLogger("bridge"));
        var runtime = new HostRuntime(bed.Settings, identity, passwords, new HostPolicy(), bridge, bed.Logs)
        {
            DirectAccessPort = taken,
        };
        bed.Track(runtime);

        await Should.NotThrowAsync(() => runtime.StartAsync());

        // The feature is off and says so; everything that does not depend on it is still up.
        runtime.BoundDirectAccessPort.ShouldBe(0);
        await Testbed.WaitUntilAsync(() => !runtime.Identity.IsLocalId, "host id");
    }
}
