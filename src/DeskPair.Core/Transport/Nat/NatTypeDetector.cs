using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using DeskPair.Core.Config;
using DeskPair.Protocol.Framing;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Core.Transport.Nat;

/// <summary>
/// Classifies the local NAT by connecting to two rendezvous ports from the same local port and
/// comparing the public ports the server observed: equal means the mapping is endpoint-independent
/// (punchable), different means symmetric (relay only). Results are cached for a while.
/// </summary>
public sealed class NatTypeDetector
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);

    private readonly PeerSettings _settings;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _detectedAt;
    private int _serial;

    public NatTypeDetector(PeerSettings settings, ILogger log, TimeProvider? time = null)
    {
        _settings = settings;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public NatType Cached { get; private set; } = NatType.NatUnknown;

    public TimeSpan CacheFor { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Test hook: skip probing and report this type.</summary>
    public NatType? Override { get; set; }

    public async Task<NatType> DetectAsync(CancellationToken ct)
    {
        if (Override is { } forced)
        {
            return Cached = forced;
        }

        if (Cached != NatType.NatUnknown && _time.GetUtcNow() - _detectedAt < CacheFor)
        {
            return Cached;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Cached != NatType.NatUnknown && _time.GetUtcNow() - _detectedAt < CacheFor)
            {
                return Cached;
            }

            (string host, int port) = TcpConnector.ParseHostPort(_settings.RendezvousServer, Protocol.ProtocolConstants.RendezvousPort);
            IPAddress address = await Resolve(host, ct).ConfigureAwait(false);

            // Both probes must originate from the same local port so the two observed public ports are
            // comparable. ConnectReusableAsync keeps that port stable across its dual-stack/plain fallback,
            // so the pair stays on one family+port even when the fallback is taken.
            (uint p1, int localPort) = await ProbeAsync(0, new IPEndPoint(address, _settings.NatTestPort), ct).ConfigureAwait(false);
            (uint p2, _) = await ProbeAsync(localPort, new IPEndPoint(address, port), ct).ConfigureAwait(false);

            Cached = p1 == p2 ? NatType.NatAsymmetric : NatType.NatSymmetric;
            _detectedAt = _time.GetUtcNow();
            _log.LogInformation("NAT type: {Type} (observed ports {P1}/{P2} from local {Local})", Cached, p1, p2, localPort);
            return Cached;
        }
        catch (Exception e) when (e is SocketException or IOException or TimeoutException or Protocol.ProtocolException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogWarning(e, "NAT type detection failed");
            return NatType.NatUnknown;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(uint Observed, int LocalPort)> ProbeAsync(int localPort, IPEndPoint server, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeTimeout);
        using Socket socket = await SocketFactory.ConnectReusableAsync(localPort, server, cts.Token).ConfigureAwait(false);
        int actualLocal = SocketFactory.LocalPort(socket);
        await using var stream = new FramedStream(new NetworkStream(socket, ownsSocket: false), FramedStreamOptions.Control);
        await stream.SendAsync(new RendezvousMessage { TestNatRequest = new TestNatRequest { Serial = ++_serial } }, cts.Token).ConfigureAwait(false);
        using Frame? frame = await stream.ReceiveAsync(cts.Token).ConfigureAwait(false) ?? throw new IOException("NAT test connection closed.");
        var reply = RendezvousMessage.Parser.ParseFrom(frame.Payload.Span);
        uint observed = reply.UnionCase == RendezvousMessage.UnionOneofCase.TestNatResponse ? reply.TestNatResponse.ObservedPort : throw new IOException($"Unexpected NAT test reply {reply.UnionCase}.");
        return (observed, actualLocal);
    }

    private static async Task<IPAddress> Resolve(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out IPAddress? literal))
        {
            return literal;
        }

        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        return addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6 ? 1 : 0).FirstOrDefault() ?? throw new SocketException((int)SocketError.HostNotFound);
    }
}
