using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using DeskPair.Core.Config;
using DeskPair.Protocol;
using DeskPair.Protocol.Framing;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Core.Transport.Nat;

/// <summary>
/// TCP hole punching and LAN offers. Every socket involved shares one local port: the connection
/// that tells the rendezvous server our public address, the listener that accepts the peer, and the
/// outbound attempts that open our NAT toward the peer. Whichever completes first becomes the transport.
/// </summary>
public sealed class TcpPuncher
{
    private static readonly TimeSpan WarmUpTimeout = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan AttemptBackoff = TimeSpan.FromMilliseconds(150);

    private readonly PeerSettings _settings;
    private readonly ILogger _log;

    public TcpPuncher(PeerSettings settings, ILogger log)
    {
        _settings = settings;
        _log = log;
    }

    public TimeSpan PunchTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public TimeSpan LocalOfferTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Test hook: announce the address but neither listen nor connect, so the peer's direct attempt fails.</summary>
    public bool DebugUnreachable { get; set; }

    /// <summary>
    /// Host side of a punch: report our public address to the server (over a connection bound to the punch
    /// port), then race a listener against outbound attempts toward the controller's public address.
    /// </summary>
    public async Task<IPeerTransport> AnswerPunchAsync(IPEndPoint controllerAddr, string myId, NatType myNat, string relayServer, string version, CancellationToken ct)
    {
        IPEndPoint server = await ResolveServerAsync(ct).ConfigureAwait(false);
        Socket announce = SocketFactory.BindReusable(0);
        int port = SocketFactory.LocalPort(announce);
        Socket? listener = DebugUnreachable ? null : Listen(port);
        try
        {
            await ConnectAsync(announce, server, ct).ConfigureAwait(false);

            // Open our NAT toward the controller before the server hands it our address.
            if (!DebugUnreachable)
            {
                await WarmUpAsync(port, controllerAddr, ct).ConfigureAwait(false);
            }

            await SendFrameAsync(announce, new RendezvousMessage
            {
                PunchHoleSent = new PunchHoleSent
                {
                    ControllerAddr = SocketAddresses.FromEndPoint(controllerAddr),
                    Id = myId,
                    RelayServer = relayServer,
                    NatType = myNat,
                    Version = version,
                },
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            announce.Dispose();
        }

        if (listener is null)
        {
            throw new IOException("Punch disabled (debug).");
        }

        using (listener)
        {
            Socket socket = await RaceAsync(listener, port, controllerAddr, PunchTimeout, ct).ConfigureAwait(false);
            _log.LogInformation("Punched TCP connection with {Peer} from port {Port}", controllerAddr, port);
            return new TcpPeerTransport(socket, TransportKind.PunchedTcp);
        }
    }

    /// <summary>
    /// Host side of a same-network offer: tell the server the LAN address we reach it from and accept
    /// the controller on that port.
    /// </summary>
    public async Task<IPeerTransport> AnswerLocalAsync(IPEndPoint controllerAddr, string myId, string relayServer, string version, CancellationToken ct)
    {
        IPEndPoint server = await ResolveServerAsync(ct).ConfigureAwait(false);
        Socket announce = SocketFactory.BindReusable(0);
        int port = SocketFactory.LocalPort(announce);
        Socket? listener = DebugUnreachable ? null : Listen(port);
        try
        {
            await ConnectAsync(announce, server, ct).ConfigureAwait(false);
            var local = (IPEndPoint)announce.LocalEndPoint!;
            var localAddr = new IPEndPoint(SocketAddresses.PlainAddress(local.Address), port);
            await SendFrameAsync(announce, new RendezvousMessage
            {
                LocalAddr = new LocalAddr
                {
                    ControllerAddr = SocketAddresses.FromEndPoint(controllerAddr),
                    LocalAddr_ = SocketAddresses.FromEndPoint(localAddr),
                    RelayServer = relayServer,
                    Id = myId,
                    Version = version,
                },
            }, ct).ConfigureAwait(false);
            _log.LogInformation("Offered LAN address {Local} to {Controller}", localAddr, controllerAddr);
        }
        finally
        {
            announce.Dispose();
        }

        if (listener is null)
        {
            throw new IOException("Punch disabled (debug).");
        }

        using (listener)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(LocalOfferTimeout);
            try
            {
                Socket socket = await listener.AcceptAsync(cts.Token).ConfigureAwait(false);
                socket.NoDelay = true;
                return new TcpPeerTransport(socket, TransportKind.Lan);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("The controller did not connect to the offered LAN address.");
            }
        }
    }

    /// <summary>Host side when a relay is the answer: tells the server (over a short TCP connection) which uuid the controller should use.</summary>
    public async Task AnnounceRelayAsync(IPEndPoint controllerAddr, string uuid, string relayServer, string myId, string version, CancellationToken ct)
    {
        IPEndPoint server = await ResolveServerAsync(ct).ConfigureAwait(false);
        using Socket socket = SocketFactory.BindReusable(0);
        await ConnectAsync(socket, server, ct).ConfigureAwait(false);
        await SendFrameAsync(socket, new RendezvousMessage
        {
            RelayResponse = new RelayResponse
            {
                ControllerAddr = SocketAddresses.FromEndPoint(controllerAddr),
                Uuid = uuid,
                RelayServer = relayServer,
                Id = myId,
                Version = version,
            },
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Controller side: repeated attempts from the request port toward the host's address until the deadline.</summary>
    public async Task<Socket> ConnectDirectAsync(int localPort, IPEndPoint hostAddr, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        TimeSpan attemptTimeout = TimeSpan.FromMilliseconds(400);
        TimeSpan backoff = AttemptBackoff;
        Exception? last = null;
        while (!deadline.IsCancellationRequested)
        {
            try
            {
                return await SocketFactory.ConnectFromPortAsync(localPort, hostAddr, attemptTimeout, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is SocketException or TimeoutException or OperationCanceledException && !deadline.IsCancellationRequested)
            {
                last = e;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await Task.Delay(backoff, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 1.5, 1000));
            attemptTimeout = TimeSpan.FromMilliseconds(Math.Min(attemptTimeout.TotalMilliseconds * 1.5, 2000));
        }

        ct.ThrowIfCancellationRequested();
        throw new TimeoutException($"No direct connection to {hostAddr} within {timeout.TotalSeconds:F0}s.", last);
    }

    private async Task<Socket> RaceAsync(Socket listener, int port, IPEndPoint peer, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        Task<Socket> accept = AcceptAsync(listener, cts.Token);
        Task<Socket> connect = ConnectDirectAsync(port, peer, timeout, cts.Token);
        Task<Socket> winner;
        try
        {
            winner = await Task.WhenAny(accept, connect).ConfigureAwait(false);
            if (winner.IsFaulted || winner.IsCanceled)
            {
                Task<Socket> other = winner == accept ? connect : accept;
                winner = other;
            }

            Socket socket = await winner.ConfigureAwait(false);
            await cts.CancelAsync().ConfigureAwait(false);
            _ = DisposeLoserAsync(winner == accept ? connect : accept);
            socket.NoDelay = true;
            return socket;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Hole punch toward {peer} did not complete within {timeout.TotalSeconds:F0}s.");
        }
    }

    private static async Task<Socket> AcceptAsync(Socket listener, CancellationToken ct)
    {
        Socket socket = await listener.AcceptAsync(ct).ConfigureAwait(false);
        return socket;
    }

    private static async Task DisposeLoserAsync(Task<Socket> loser)
    {
        try
        {
            Socket s = await loser.ConfigureAwait(false);
            s.Dispose();
        }
        catch (Exception)
        {
        }
    }

    private static Socket Listen(int port)
    {
        Socket listener = SocketFactory.BindReusable(port);
        listener.Listen(8);
        return listener;
    }

    private async Task WarmUpAsync(int port, IPEndPoint peer, CancellationToken ct)
    {
        try
        {
            using Socket warm = await SocketFactory.ConnectFromPortAsync(port, peer, WarmUpTimeout, ct).ConfigureAwait(false);
            _log.LogDebug("Warm-up connect to {Peer} unexpectedly succeeded; dropping it", peer);
        }
        catch (Exception e) when (e is SocketException or TimeoutException)
        {
            // Expected: the SYN only needed to create our NAT mapping.
        }
    }

    private async Task<IPEndPoint> ResolveServerAsync(CancellationToken ct)
    {
        (string host, int port) = TcpConnector.ParseHostPort(_settings.RendezvousServer, ProtocolConstants.RendezvousPort);
        if (IPAddress.TryParse(host, out IPAddress? literal))
        {
            return new IPEndPoint(literal, port);
        }

        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        IPAddress address = addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6 ? 1 : 0).FirstOrDefault() ?? throw new SocketException((int)SocketError.HostNotFound);
        return new IPEndPoint(address, port);
    }

    private async Task ConnectAsync(Socket socket, IPEndPoint server, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_settings.ConnectTimeout);
        await socket.ConnectAsync(SocketFactory.Normalize(server), cts.Token).ConfigureAwait(false);
    }

    private static async Task SendFrameAsync(Socket socket, RendezvousMessage message, CancellationToken ct)
    {
        await using var stream = new FramedStream(new NetworkStream(socket, ownsSocket: false), FramedStreamOptions.Control);
        await stream.SendAsync(message, ct).ConfigureAwait(false);
    }
}
