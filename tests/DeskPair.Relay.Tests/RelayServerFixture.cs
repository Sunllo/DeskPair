using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Google.Protobuf;
using DeskPair.Protocol;
using DeskPair.Protocol.Crypto;
using DeskPair.Protocol.Rendezvous;
using DeskPair.Relay.Core;
using DeskPair.Relay.Net;

namespace DeskPair.Relay.Tests;

/// <summary>
/// Runs an in-process relay on ephemeral ports, configured to trust one rendezvous key that the fixture
/// holds -- so a test can mint tickets the relay accepts, and tickets it must not.
/// </summary>
public sealed class RelayServerFixture : IAsyncDisposable
{
    private readonly WebApplication _app;

    private RelayServerFixture(WebApplication app, IdentityKey rendezvousKey)
    {
        _app = app;
        RendezvousKey = rendezvousKey;
    }

    public int Port { get; private set; }

    public int UdpPort { get; private set; }

    /// <summary>The key whose tickets this relay accepts.</summary>
    public IdentityKey RendezvousKey { get; }

    public RelayStats Stats => _app.Services.GetRequiredService<RelayStats>();

    public static async Task<RelayServerFixture> StartAsync(Action<RelayOptions>? configure = null)
    {
        IdentityKey rendezvousKey = IdentityKey.Create();
        WebApplication app = RelayServer.Build(
            ["--Logging:LogLevel:Default=Warning"],
            o =>
            {
                o.Port = 0;
                o.HttpPort = 0;
                o.RendezvousPublicKey = Convert.ToBase64String(rendezvousKey.PublicKeySpki);
                configure?.Invoke(o);
            });
        await app.StartAsync();
        var fixture = new RelayServerFixture(app, rendezvousKey)
        {
            Port = await app.Services.GetRequiredService<RelayListener>().BoundPort,
            UdpPort = await app.Services.GetRequiredService<UdpRelayListener>().BoundPort,
        };
        return fixture;
    }

    /// <summary>A ticket this relay accepts for <paramref name="uuid"/>, good for two minutes.</summary>
    public RelayTicket Ticket(string uuid, TimeSpan? lifetime = null) =>
        RelayTickets.Issue(RendezvousKey, uuid, string.Empty, DateTimeOffset.UtcNow + (lifetime ?? ProtocolConstants.RelayTicketLifetime));

    /// <summary>Connects and presents <paramref name="uuid"/> with a ticket this relay accepts; pass <paramref name="ticket"/> to present something else, or <paramref name="withTicket"/> false for none.</summary>
    public async Task<Socket> ConnectAsync(string uuid, string id, RelayTicket? ticket = null, bool withTicket = true)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await s.ConnectAsync(new IPEndPoint(IPAddress.Loopback, Port));

        // Send the RequestRelay as one plaintext frame (4-byte little-endian length prefix) directly on
        // the socket, leaving the socket open and undisturbed for the relayed byte stream that follows.
        var request = new RequestRelay { Uuid = uuid, Id = id };
        if (withTicket)
        {
            request.Ticket = ticket ?? Ticket(uuid);
        }

        byte[] body = new RendezvousMessage { RequestRelay = request }.ToByteArray();
        byte[] frame = new byte[4 + body.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        int sent = 0;
        while (sent < frame.Length)
        {
            sent += await s.SendAsync(frame.AsMemory(sent), SocketFlags.None);
        }

        return s;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        RendezvousKey.Dispose();
    }
}
