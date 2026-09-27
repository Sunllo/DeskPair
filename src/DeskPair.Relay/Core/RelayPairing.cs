using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using DeskPair.Protocol;
using DeskPair.Protocol.Crypto;
using DeskPair.Protocol.Framing;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Relay.Core;

/// <summary>
/// Reads the first frame (RequestRelay) from each incoming socket and pairs the two sockets that present
/// the same uuid. Everything after the first frame is opaque and belongs to the peers.
/// </summary>
public sealed class RelayPairing
{
    private readonly ConcurrentDictionary<string, PendingPeer> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<IPAddress, int> _pendingPerIp = new();
    private readonly ConcurrentDictionary<IPAddress, int> _sessionsPerIp = new();
    private readonly RelayOptions _options;
    private readonly RelayStats _stats;
    private readonly RelayThrottle _throttle;
    private readonly TimeProvider _time;
    private readonly ILogger<RelayPairing> _log;

    public RelayPairing(IOptions<RelayOptions> options, RelayStats stats, RelayThrottle throttle, TimeProvider time, ILogger<RelayPairing> log)
    {
        _options = options.Value;
        _stats = stats;
        _throttle = throttle;
        _time = time;
        _log = log;
    }

    /// <summary>
    /// Whether a request may use this relay. With tickets required, that means one signed by the
    /// rendezvous key this relay is configured with, unexpired, and for this uuid; with no key configured
    /// nothing verifies and everything is refused, which is the safe reading of a missing setting.
    /// </summary>
    internal bool Admit(RelayTicket? ticket, string uuid, out string reason)
    {
        if (!_options.RequireTicket)
        {
            reason = string.Empty;
            return true;
        }

        byte[]? key = _options.RendezvousPublicKeySpki;
        if (key is null)
        {
            reason = "no rendezvous key configured, so no ticket can verify";
            return false;
        }

        return RelayTickets.TryVerify(key, ticket, _time.GetUtcNow(), uuid, out _, out reason);
    }

    private sealed record PendingPeer(Socket Socket, string Id, IPAddress Ip, TaskCompletionSource<(Socket Socket, string Id, IPAddress Ip)> Partner);

    public async Task HandleAsync(Socket socket, CancellationToken ct)
    {
        IPAddress ip = (socket.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv6() ?? IPAddress.IPv6None;
        socket.NoDelay = true;
        socket.SendBufferSize = 1 << 20;
        socket.ReceiveBufferSize = 1 << 20;

        RequestRelay request;
        try
        {
            request = await ReadRequestAsync(socket, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is ProtocolException or IOException or SocketException or OperationCanceledException)
        {
            _stats.ProtocolError();
            _log.LogDebug(e, "Rejected relay connection from {Ip}: bad first frame", ip);
            RelaySession.SafeClose(socket);
            return;
        }

        if (request.Uuid.Length is < 16 or > 64 || !request.Uuid.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            _stats.ProtocolError();
            RelaySession.SafeClose(socket);
            return;
        }

        if (!Admit(request.Ticket, request.Uuid, out string refused))
        {
            _stats.TicketRejected();
            _log.LogWarning("Rejected relay request from {Ip} for {Uuid}: {Reason}", ip, RelaySession.Short(request.Uuid), refused);
            RelaySession.SafeClose(socket);
            return;
        }

        if (_stats.ActiveSessions >= _options.MaxSessions || _sessionsPerIp.GetValueOrDefault(ip) >= _options.MaxSessionsPerIp)
        {
            _stats.LimitRejected();
            _log.LogWarning("Rejected relay request from {Ip}: session limit", ip);
            RelaySession.SafeClose(socket);
            return;
        }

        // Pair with a waiting peer for this uuid, or register ourselves and wait. Both peers can arrive
        // concurrently and each fail to see the other; the loop resolves that race without dropping either.
        var me = new PendingPeer(socket, request.Id, ip, new(TaskCreationOptions.RunContinuationsAsynchronously));
        while (true)
        {
            if (_pending.TryRemove(request.Uuid, out PendingPeer? waiting))
            {
                if (waiting.Partner.TrySetResult((socket, request.Id, ip)))
                {
                    return; // we are the second peer; the first peer runs the splice
                }

                continue; // the waiting peer was abandoned; look again
            }

            // Take the address's slot before registering, not after: each socket is handled on its own thread, and a
            // check followed later by an increment let a burst from one address all see the count before any added to it.
            if (_pendingPerIp.AddOrUpdate(ip, 1, (_, v) => v + 1) > _options.MaxPendingPerIp)
            {
                ReleasePending(ip);
                _stats.LimitRejected();
                _log.LogWarning("Rejected relay request from {Ip}: pending limit", ip);
                RelaySession.SafeClose(socket);
                return;
            }

            if (_pending.TryAdd(request.Uuid, me))
            {
                break; // we are the first peer; wait for a partner below
            }

            // Lost the add race with a concurrent same-uuid arrival; give the slot back and loop to pair with it.
            ReleasePending(ip);
        }

        _stats.PendingInc();
        (Socket Socket, string Id, IPAddress Ip) partner;
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(_options.PairingTimeout);
            try
            {
                partner = await me.Partner.Task.WaitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (_pending.TryRemove(new KeyValuePair<string, PendingPeer>(request.Uuid, me)))
                {
                    _stats.PairingTimedOut();
                    _log.LogDebug("Relay pairing {Uuid} timed out", RelaySession.Short(request.Uuid));
                    RelaySession.SafeClose(socket);
                    return;
                }

                // Partner arrived exactly at the deadline; it has already been handed to us.
                partner = await me.Partner.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _stats.PendingDec();
            ReleasePending(ip);
        }

        _sessionsPerIp.AddOrUpdate(ip, 1, (_, v) => v + 1);
        _sessionsPerIp.AddOrUpdate(partner.Ip, 1, (_, v) => v + 1);
        _log.LogInformation("Relay session {Uuid} paired: {IdA} ({IpA}) <-> {IdB} ({IpB})",
            RelaySession.Short(request.Uuid), request.Id, ip, partner.Id, partner.Ip);
        try
        {
            await RelaySession.RunAsync(request.Uuid, request.Id, socket, partner.Id, partner.Socket, _options, _stats, _throttle, _time, _log, ct).ConfigureAwait(false);
        }
        finally
        {
            _sessionsPerIp.AddOrUpdate(ip, 0, (_, v) => Math.Max(0, v - 1));
            _sessionsPerIp.AddOrUpdate(partner.Ip, 0, (_, v) => Math.Max(0, v - 1));
        }
    }

    private void ReleasePending(IPAddress ip) => _pendingPerIp.AddOrUpdate(ip, 0, (_, v) => Math.Max(0, v - 1));

    private async Task<RequestRelay> ReadRequestAsync(Socket socket, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.FirstFrameTimeout);
        var stream = new NetworkStream(socket, ownsSocket: false);
        await using (stream.ConfigureAwait(false))
        {
            using Frame? frame = await FramedStream.ReadExactFrameAsync(stream, ProtocolConstants.MaxControlFrameBytes, timeout.Token).ConfigureAwait(false)
                ?? throw new ProtocolException("Connection closed before RequestRelay.");
            var msg = RendezvousMessage.Parser.ParseFrom(frame.Payload.Span);
            if (msg.UnionCase != RendezvousMessage.UnionOneofCase.RequestRelay)
            {
                throw new ProtocolException($"Expected RequestRelay, got {msg.UnionCase}.");
            }

            return msg.RequestRelay;
        }
    }
}
