using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using DeskPair.Core.Config;
using DeskPair.Core.Transport.Nat;
using DeskPair.Protocol;
using DeskPair.Protocol.Crypto;
using DeskPair.Protocol.Framing;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Core.Transport;

public sealed class PeerConnectException : Exception
{
    public PeerConnectException(string message, PunchHoleResponse.Types.Failure failure = PunchHoleResponse.Types.Failure.None, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
    }

    public PunchHoleResponse.Types.Failure Failure { get; }
}

/// <summary>The transport plus how the host's identity should be verified during the handshake.</summary>
/// <summary><paramref name="RelayServer"/> and <paramref name="RendezvousServer"/> (host[:port]) let the session offer relayed/reflexive UDP media candidates.</summary>
/// <summary><paramref name="RelayTicket"/> is the rendezvous server's permission to use its relay, for the UDP media fallback; null on a direct target.</summary>
public sealed record PeerConnection(IPeerTransport Transport, IHostIdentityVerifier Verifier, string HostVersion, string RelayServer = "", string RendezvousServer = "", RelayTicket? RelayTicket = null);

/// <summary>
/// Controller-side connection establishment. Direct targets (ip[:port] / host:port) connect straight to
/// the host's listener with trust-on-first-use. Peer ids go through the rendezvous server: LAN address
/// when both sides share a public IP, TCP hole punch when both NATs allow it, otherwise (or after a failed
/// direct attempt) a relay.
/// </summary>
public sealed class PeerConnector
{
    private static readonly TimeSpan QuickDirectTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan PunchDirectTimeout = TimeSpan.FromSeconds(6);

    private readonly PeerSettings _settings;
    private readonly ILogger _log;
    private readonly NatTypeDetector _nat;
    private readonly TcpPuncher _puncher;

    public PeerConnector(PeerSettings settings, ILogger log, IKnownHostsStore? knownHosts = null)
    {
        _settings = settings;
        _log = log;
        _nat = new NatTypeDetector(settings, log);
        _puncher = new TcpPuncher(settings, log);
        KnownHosts = knownHosts ?? new InMemoryKnownHostsStore();
    }

    /// <summary>Pinned host keys for direct (address) targets.</summary>
    public IKnownHostsStore KnownHosts { get; }

    public NatTypeDetector Nat => _nat;

    /// <summary>How the last connection was made, for diagnostics.</summary>
    public TransportKind? LastTransport { get; private set; }

    /// <summary>
    /// Peer ids are all digits; anything with a port, a bracketed IPv6, or a dotted IPv4 literal is a direct target.
    /// (IPAddress.TryParse accepts a bare integer as IPv4, so digits must be checked first.)
    /// </summary>
    public static bool IsDirectTarget(string target)
    {
        target = target.Trim();
        if (target.Length == 0 || target.All(char.IsAsciiDigit))
        {
            return false;
        }

        if (target.StartsWith('[') || target.Count(c => c == ':') > 1)
        {
            return true; // IPv6 literal, bracketed or bare
        }

        if (target.Contains(':'))
        {
            return true; // host:port or ipv4:port
        }

        return target.Contains('.') && IPAddress.TryParse(target, out IPAddress? ip) && ip.AddressFamily == AddressFamily.InterNetwork;
    }

    public async Task<PeerConnection> ConnectAsync(string target, string myId, ConnType connType, CancellationToken ct)
    {
        if (IsDirectTarget(target))
        {
            (string host, int port) = TcpConnector.ParseHostPort(target, ProtocolConstants.DirectAccessPort);
            _log.LogInformation("Connecting directly to {Host}:{Port}", host, port);
            Socket socket = await TcpConnector.ConnectAsync(host, port, _settings.ConnectTimeout, ct).ConfigureAwait(false);
            LastTransport = TransportKind.DirectTcp;
            return new PeerConnection(new TcpPeerTransport(socket, TransportKind.DirectTcp), new TofuIdentityVerifier(KnownHosts, $"{host}:{port}"), string.Empty, string.Empty, _settings.RendezvousServer);
        }

        NatType myNat = _settings.ForceRelay ? NatType.NatUnknown : await _nat.DetectAsync(ct).ConfigureAwait(false);
        (RendezvousMessage reply, int localPort, TimeSpan punchTime) = await RequestAsync(target, connType, myNat, _settings.ForceRelay, ct).ConfigureAwait(false);
        if (reply.UnionCase == RendezvousMessage.UnionOneofCase.RelayResponse)
        {
            return await ConnectRelayAsync(target, myId, connType, reply.RelayResponse, ct).ConfigureAwait(false);
        }

        PunchHoleResponse punch = reply.PunchHoleResponse;
        IHostIdentityVerifier verifier = BuildVerifier(target, punch.Identity);
        IPEndPoint? hostAddr = SocketAddresses.ToEndPoint(punch.HostAddr);
        if (hostAddr is not null)
        {
            bool quick = punch.IsLocal || punch.NatType == NatType.NatSymmetric || myNat == NatType.NatSymmetric;
            TimeSpan timeout = quick ? QuickDirectTimeout : PunchDirectTimeout;
            _log.LogInformation("Direct attempt to {Id} at {Addr} ({Mode}, punch signalling took {Ms} ms)", target, hostAddr, punch.IsLocal ? "LAN" : "punch", (int)punchTime.TotalMilliseconds);
            try
            {
                Socket socket = await _puncher.ConnectDirectAsync(localPort, hostAddr, timeout, ct).ConfigureAwait(false);
                TransportKind kind = punch.IsLocal ? TransportKind.Lan : TransportKind.PunchedTcp;
                LastTransport = kind;
                _log.LogInformation("Connected to {Id} via {Kind}", target, kind);
                return new PeerConnection(new TcpPeerTransport(socket, kind), verifier, punch.HostVersion, punch.RelayServer, _settings.RendezvousServer, punch.RelayTicket);
            }
            catch (Exception e) when (e is SocketException or TimeoutException or IOException)
            {
                _log.LogInformation("Direct attempt to {Id} failed ({Reason}); falling back to relay", target, e.Message);
            }
        }
        else
        {
            _log.LogInformation("No host address for {Id}; using relay", target);
        }

        (RendezvousMessage relayReply, _, _) = await RequestAsync(target, connType, myNat, forceRelay: true, ct).ConfigureAwait(false);
        if (relayReply.UnionCase != RendezvousMessage.UnionOneofCase.RelayResponse)
        {
            throw new PeerConnectException("The server did not broker a relay.");
        }

        return await ConnectRelayAsync(target, myId, connType, relayReply.RelayResponse, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Dials the relays the server offered, in order, until one answers.
    ///
    /// The server chose these a moment ago on health it had polled up to half a minute earlier, so the
    /// first can be gone by the time it is dialled. Before this there was one address and no second chance,
    /// and a relay restarting turned into a failed connection rather than a second of delay.
    /// </summary>
    private async Task<PeerConnection> ConnectRelayAsync(string target, string myId, ConnType connType, RelayResponse relay, CancellationToken ct)
    {
        IHostIdentityVerifier verifier = BuildVerifier(target, relay.Identity);

        // relay_servers repeats the chosen one first; an older server sends only relay_server.
        List<string> candidates = relay.RelayServers.Count > 0
            ? [.. relay.RelayServers]
            : [relay.RelayServer];

        Exception? last = null;
        for (int i = 0; i < candidates.Count; i++)
        {
            string address = candidates[i];
            if (address.Length == 0)
            {
                continue;
            }

            _log.LogInformation("Connecting to {Id} through relay {Relay}", target, address);
            try
            {
                IPeerTransport transport = await RelayClient
                    .ConnectAsync(address, relay.Uuid, myId, connType, _settings.ConnectTimeout, ct, relay.Ticket)
                    .ConfigureAwait(false);
                LastTransport = TransportKind.Relay;
                return new PeerConnection(transport, verifier, relay.Version, address, _settings.RendezvousServer, relay.Ticket);
            }
            catch (Exception e) when (e is SocketException or TimeoutException or IOException && i < candidates.Count - 1)
            {
                // Only when there is somewhere else to go. The last one's exception is the one worth
                // showing, because by then "the relay would not answer" is the whole story.
                _log.LogInformation("Relay {Relay} did not answer ({Reason}); trying the next", address, e.Message);
                last = e;
            }
        }

        throw new PeerConnectException("Could not reach a relay server.", inner: last);
    }

    private IHostIdentityVerifier BuildVerifier(string hostId, SignedPeerIdentity? identity) =>
        VerifierFor(_settings.ServerPublicKey, hostId, identity);

    /// <summary>
    /// How a host reached by id is verified: with the rendezvous server's signature over its identity, and
    /// in no other way.
    ///
    /// Without the server's public key there is nothing to check the signature against, and this used to
    /// fall back to accepting whatever key the far end presented, with a warning in a log nobody reads
    /// mid-connection. The signalling channel is not encrypted, so that fallback was the whole security of
    /// the session resting on nobody being between the two machines. A connection that cannot be verified
    /// is refused instead, with the way to fix it in the message; direct targets keep their own pinning.
    /// </summary>
    /// <exception cref="PeerConnectException">No server key is configured, or the server sent no identity.</exception>
    public static IHostIdentityVerifier VerifierFor(byte[]? serverKey, string hostId, SignedPeerIdentity? identity)
    {
        if (serverKey is null)
        {
            throw new PeerConnectException(
                "The rendezvous server's public key is not configured, so the host's identity cannot be "
                + "verified and the connection is refused. Fetch the key in Settings > Network, or connect by address.");
        }

        if (identity is null)
        {
            throw new PeerConnectException("Rendezvous did not provide a signed host identity.");
        }

        return new ServerSignedIdentityVerifier(serverKey, hostId, identity);
    }

    /// <summary>
    /// Sends a punch request over a TCP connection bound to a reusable local port and waits for the
    /// server's answer. The local port is returned because direct attempts must originate from it: that is
    /// the mapping the server observed and handed to the host.
    /// </summary>
    private async Task<(RendezvousMessage Reply, int LocalPort, TimeSpan Elapsed)> RequestAsync(string peerId, ConnType connType, NatType natType, bool forceRelay, CancellationToken ct)
    {
        (string host, int port) = TcpConnector.ParseHostPort(_settings.RendezvousServer, ProtocolConstants.RendezvousPort);
        Exception? last = null;
        for (int attempt = 1; attempt <= ProtocolConstants.PunchRequestAttempts; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(ProtocolConstants.PunchRequestBaseDeadline * attempt + (forceRelay ? TimeSpan.Zero : _settings.PunchSignallingTimeout));
            Socket? socket = null;
            long started = Environment.TickCount64;
            try
            {
                IPAddress address = IPAddress.TryParse(host, out IPAddress? literal) ? literal : (await Dns.GetHostAddressesAsync(host, attemptCts.Token).ConfigureAwait(false)).OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6 ? 1 : 0).FirstOrDefault() ?? throw new SocketException((int)SocketError.HostNotFound);
                socket = await SocketFactory.ConnectReusableAsync(0, new IPEndPoint(address, port), attemptCts.Token).ConfigureAwait(false);
                int localPort = SocketFactory.LocalPort(socket);
                await using var stream = new FramedStream(new NetworkStream(socket, ownsSocket: false), FramedStreamOptions.Control);
                await stream.SendAsync(new RendezvousMessage
                {
                    PunchHoleRequest = new PunchHoleRequest
                    {
                        Id = peerId,
                        ConnType = connType,
                        ForceRelay = forceRelay,
                        NatType = natType,
                        Version = _settings.Version,
                    },
                }, attemptCts.Token).ConfigureAwait(false);

                using Frame? frame = await stream.ReceiveAsync(attemptCts.Token).ConfigureAwait(false)
                    ?? throw new PeerConnectException("Rendezvous closed the connection without answering.");
                var msg = RendezvousMessage.Parser.ParseFrom(frame.Payload.Span);
                TimeSpan elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
                switch (msg.UnionCase)
                {
                    case RendezvousMessage.UnionOneofCase.RelayResponse:
                        return (msg, localPort, elapsed);
                    case RendezvousMessage.UnionOneofCase.PunchHoleResponse when msg.PunchHoleResponse.Failure != PunchHoleResponse.Types.Failure.None:
                        throw new PeerConnectException(Describe(msg.PunchHoleResponse.Failure), msg.PunchHoleResponse.Failure);
                    case RendezvousMessage.UnionOneofCase.PunchHoleResponse:
                        return (msg, localPort, elapsed);
                    default:
                        throw new PeerConnectException($"Unexpected rendezvous reply {msg.UnionCase}.");
                }
            }
            catch (PeerConnectException e) when (e.Failure is PunchHoleResponse.Types.Failure.ServerBusy && attempt < ProtocolConstants.PunchRequestAttempts)
            {
                last = e;
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or IOException or TimeoutException or ProtocolException && !ct.IsCancellationRequested)
            {
                last = e;
                _log.LogDebug(e, "Rendezvous request attempt {Attempt} failed", attempt);
            }
            finally
            {
                socket?.Dispose();
            }
        }

        throw new PeerConnectException("Could not reach the rendezvous server.", inner: last);
    }

    private static string Describe(PunchHoleResponse.Types.Failure failure) => failure switch
    {
        PunchHoleResponse.Types.Failure.IdNotExist => "The ID does not exist.",
        PunchHoleResponse.Types.Failure.Offline => "The remote device is offline.",
        PunchHoleResponse.Types.Failure.ServerBusy => "The server is busy.",
        _ => "Connection refused by the server.",
    };
}
