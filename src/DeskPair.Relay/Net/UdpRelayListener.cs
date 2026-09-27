using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using DeskPair.Protocol.Crypto;
using DeskPair.Protocol.Media;
using DeskPair.Relay.Core;

namespace DeskPair.Relay.Net;

/// <summary>
/// UDP side of the relay: pairs two endpoints that present the same token and forwards every other datagram
/// between them untouched. Media datagrams are end-to-end encrypted, so the relay never sees their content.
/// Binds to the TCP relay port number by default so peers address the relay with one host:port.
/// </summary>
public sealed class UdpRelayListener : BackgroundService
{
    private readonly RelayListener _tcp;
    private readonly RelayStats _stats;
    private readonly RelayOptions _options;
    private readonly ILogger<UdpRelayListener> _log;
    private readonly TaskCompletionSource<int> _boundPort = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public UdpRelayListener(RelayListener tcp, RelayStats stats, IOptions<RelayOptions> options, ILogger<UdpRelayListener> log)
    {
        _tcp = tcp;
        _stats = stats;
        _options = options.Value;
        _log = log;
        Table = new UdpRelayTable(TimeProvider.System, options.Value.UdpPairingTimeout, options.Value.UdpIdleTimeout, options.Value.MaxSessions);
    }

    public UdpRelayTable Table { get; }

    public Task<int> BoundPort => _boundPort.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int port = _options.UdpPort > 0 ? _options.UdpPort : await _tcp.BoundPort.ConfigureAwait(false);
        using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        socket.ReceiveBufferSize = 8 << 20;
        socket.SendBufferSize = 8 << 20;
        DisableConnectionReset(socket);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        }
        catch (SocketException e) when (_options.UdpPort == 0)
        {
            // The TCP port number is not available for UDP (excluded range, other process): fall back to any port.
            // Peers then cannot reach the UDP relay; direct paths still work and the operator should pin UdpPort.
            _log.LogWarning(e, "UDP relay cannot bind udp/{Port}; using an ephemeral port instead", port);
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        }
        catch (SocketException e)
        {
            _boundPort.TrySetException(e);
            throw;
        }

        int bound = ((IPEndPoint)socket.LocalEndPoint!).Port;
        _boundPort.TrySetResult(bound);
        _log.LogInformation("Relay listening on udp/{Port}", bound);

        Task sweeper = SweepLoopAsync(stoppingToken);
        byte[] buffer = new byte[2048];
        var from = new SocketAddress(AddressFamily.InterNetworkV6);
        while (!stoppingToken.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await socket.ReceiveFromAsync(buffer, SocketFlags.None, from, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException e)
            {
                _log.LogDebug(e, "UDP relay receive failed");
                continue;
            }

            var sender = (IPEndPoint)new IPEndPoint(IPAddress.IPv6Any, 0).Create(from);
            ReadOnlyMemory<byte> datagram = buffer.AsMemory(0, n);
            if (RelayDatagram.TryRead(datagram.Span, out RelayDatagramType type, out ReadOnlySpan<byte> token, out ReadOnlySpan<byte> ticketBytes))
            {
                if (type is not (RelayDatagramType.Bind or RelayDatagramType.BindTicket))
                {
                    continue;
                }

                byte[] tokenCopy = token.ToArray();
                if (!Admit(type, tokenCopy, ticketBytes, out string refused))
                {
                    _stats.UdpRejected();
                    _log.LogDebug("Rejected UDP bind from {From}: {Reason}", sender, refused);
                    await ReplyAsync(socket, RelayDatagramType.Reject, tokenCopy, sender, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                UdpBindResult result = Table.Bind(tokenCopy, sender, out IPEndPoint? peer);
                switch (result)
                {
                    case UdpBindResult.Paired:
                        _stats.UdpPaired();
                        await ReplyAsync(socket, RelayDatagramType.BindAck, tokenCopy, sender, stoppingToken).ConfigureAwait(false);
                        await ReplyAsync(socket, RelayDatagramType.BindAck, tokenCopy, peer!, stoppingToken).ConfigureAwait(false);
                        break;
                    case UdpBindResult.Rejected:
                        _stats.UdpRejected();
                        await ReplyAsync(socket, RelayDatagramType.Reject, tokenCopy, sender, stoppingToken).ConfigureAwait(false);
                        break;
                }

                continue;
            }

            IPEndPoint? target = Table.PeerOf(sender);
            if (target is null)
            {
                continue; // not paired: dropped without a reply
            }

            try
            {
                await socket.SendToAsync(datagram, SocketFlags.None, target, stoppingToken).ConfigureAwait(false);
                _stats.UdpForwarded(n);
            }
            catch (SocketException)
            {
            }
        }

        await sweeper.ConfigureAwait(false);
    }

    /// <summary>
    /// The UDP side of ticket admission. A BindTicket must carry a ticket that verifies against the
    /// rendezvous key, is unexpired, and whose derived token is the one being bound -- so a ticket cannot
    /// be borrowed for a pairing it was not issued for. A plain Bind is what protocol 1 sent and is
    /// refused while tickets are required.
    /// </summary>
    internal bool Admit(RelayDatagramType type, byte[] token, ReadOnlySpan<byte> ticketBytes, out string reason)
    {
        if (!_options.RequireTicket)
        {
            reason = string.Empty;
            return true;
        }

        if (type != RelayDatagramType.BindTicket)
        {
            reason = "bind without a ticket";
            return false;
        }

        byte[]? key = _options.RendezvousPublicKeySpki;
        if (key is null)
        {
            reason = "no rendezvous key configured";
            return false;
        }

        DeskPair.Protocol.Rendezvous.RelayTicket ticket;
        try
        {
            ticket = DeskPair.Protocol.Rendezvous.RelayTicket.Parser.ParseFrom(ticketBytes);
        }
        catch (Google.Protobuf.InvalidProtocolBufferException)
        {
            reason = "ticket does not parse";
            return false;
        }

        if (!RelayTickets.TryVerify(key, ticket, DateTimeOffset.UtcNow, null, out _, out reason))
        {
            return false;
        }

        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(RelayTickets.UdpToken(ticket), token))
        {
            reason = "token is not the ticket's";
            return false;
        }

        return true;
    }

    private static async Task ReplyAsync(Socket socket, RelayDatagramType type, byte[] token, IPEndPoint target, CancellationToken ct)
    {
        byte[] reply = new byte[RelayDatagram.Size];
        RelayDatagram.Write(reply, type, token);
        try
        {
            await socket.SendToAsync(reply, SocketFlags.None, target, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
        }
    }

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                Table.Sweep();
                _stats.UdpSetActive(Table.PairCount, Table.PendingCount);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void DisableConnectionReset(Socket socket)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            socket.IOControl(-1744830452, [0, 0, 0, 0], null); // SIO_UDP_CONNRESET
        }
        catch (Exception)
        {
        }
    }
}
