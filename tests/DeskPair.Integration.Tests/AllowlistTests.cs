using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Session.Host.Auth;

namespace DeskPair.Integration.Tests;

/// <summary>
/// The allowlist over a real connection. The unit tests say what the matcher decides; these say that the
/// decision is actually enforced, and enforced at the right moment -- a peer refused on its address never
/// gets to exchange a byte, while one refused on its id is turned away at the login.
/// </summary>
public class AllowlistTests
{
    private static Task<(HostRuntime Runtime, HostPasswords Passwords, TestApprover Approver)> HostAsync(Testbed bed) =>
        bed.StartHostAsync(directPort: -1, settings: Testbed.NoServerSettings);

    [Fact]
    public async Task An_address_that_is_not_listed_is_refused_before_the_handshake()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, HostPasswords passwords, _) = await HostAsync(bed);
        host.Allowlist = PeerAllowlist.Create(true, ["203.0.113.5"], out IReadOnlyList<string> _);

        (ControllerSession session, TestCallbacks _, DeskPair.Core.Transport.PeerConnector connector) = bed.CreateController(settings: Testbed.NoServerSettings);

        // The host disposes the transport without answering, so the failure surfaces as the connection
        // dying rather than as a login error -- which is the point: a scanner learns nothing.
        await Should.ThrowAsync<Exception>(async () =>
        {
            await session.ConnectAsync(connector, $"127.0.0.1:{host.BoundDirectAccessPort}", CancellationToken.None);
            await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None);
        });

        host.Sessions.ShouldBeEmpty("no session should ever have been created");
    }

    [Fact]
    public async Task A_listed_address_connects_as_usual()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, HostPasswords passwords, _) = await HostAsync(bed);
        host.Allowlist = PeerAllowlist.Create(true, ["127.0.0.0/8", "::1"], out IReadOnlyList<string> _);

        (ControllerSession session, TestCallbacks _, DeskPair.Core.Transport.PeerConnector connector) = bed.CreateController(settings: Testbed.NoServerSettings);
        await session.ConnectAsync(connector, $"127.0.0.1:{host.BoundDirectAccessPort}", CancellationToken.None);

        (await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();
    }

    /// <summary>
    /// A list of ids cannot refuse on the address, so the connection must be allowed to get as far as saying
    /// who it is -- and then be turned away with a real answer rather than a dropped socket.
    /// </summary>
    [Fact]
    public async Task An_id_that_is_not_listed_is_refused_at_the_login()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, HostPasswords passwords, _) = await HostAsync(bed);
        host.Allowlist = PeerAllowlist.Create(true, ["id:the-one-machine"], out IReadOnlyList<string> _);

        (ControllerSession session, TestCallbacks _, DeskPair.Core.Transport.PeerConnector connector) = bed.CreateController(myId: "somebody-else", settings: Testbed.NoServerSettings);
        await session.ConnectAsync(connector, $"127.0.0.1:{host.BoundDirectAccessPort}", CancellationToken.None);

        LoginResult result = await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None);
        result.Success.ShouldBeFalse();
        result.Error!.Code.ShouldBe(DeskPair.Protocol.Messages.LoginError.Types.Code.RejectedByUser);
    }

    [Fact]
    public async Task A_listed_id_connects_from_any_address()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, HostPasswords passwords, _) = await HostAsync(bed);
        host.Allowlist = PeerAllowlist.Create(true, ["id:the-one-machine"], out IReadOnlyList<string> _);

        (ControllerSession session, TestCallbacks _, DeskPair.Core.Transport.PeerConnector connector) = bed.CreateController(myId: "the-one-machine", settings: Testbed.NoServerSettings);
        await session.ConnectAsync(connector, $"127.0.0.1:{host.BoundDirectAccessPort}", CancellationToken.None);

        (await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();
    }

    /// <summary>Turning it on with nothing in it is a deliberate answer, and it has to hold over a real socket.</summary>
    [Fact]
    public async Task An_empty_allowlist_refuses_the_loopback_too()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, HostPasswords passwords, _) = await HostAsync(bed);
        host.Allowlist = PeerAllowlist.Create(true, [], out IReadOnlyList<string> _);

        (ControllerSession session, TestCallbacks _, DeskPair.Core.Transport.PeerConnector connector) = bed.CreateController(settings: Testbed.NoServerSettings);

        await Should.ThrowAsync<Exception>(async () =>
        {
            await session.ConnectAsync(connector, $"127.0.0.1:{host.BoundDirectAccessPort}", CancellationToken.None);
            await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None);
        });

        host.Sessions.ShouldBeEmpty();
    }
}
