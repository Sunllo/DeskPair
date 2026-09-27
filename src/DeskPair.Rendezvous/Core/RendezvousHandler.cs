using System.Net;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Rendezvous.Core;

/// <summary>Sends a datagram to a peer's registered UDP endpoint.</summary>
public interface IUdpSender
{
    ValueTask SendAsync(RendezvousMessage message, IPEndPoint target, CancellationToken ct);
}

/// <summary>Counters exposed by the admin API.</summary>
public sealed class RendezvousStats
{
    private long _registrations;
    private long _identityRegistrations;
    private long _punchRequests;
    private long _relaysBrokered;
    private long _rejected;

    public long Registrations => Interlocked.Read(ref _registrations);
    public long IdentityRegistrations => Interlocked.Read(ref _identityRegistrations);
    public long PunchRequests => Interlocked.Read(ref _punchRequests);
    public long RelaysBrokered => Interlocked.Read(ref _relaysBrokered);
    public long Rejected => Interlocked.Read(ref _rejected);

    internal void Registration() => Interlocked.Increment(ref _registrations);
    internal void IdentityRegistration() => Interlocked.Increment(ref _identityRegistrations);
    internal void PunchRequest() => Interlocked.Increment(ref _punchRequests);
    internal void RelayBrokered() => Interlocked.Increment(ref _relaysBrokered);
    internal void Reject() => Interlocked.Increment(ref _rejected);
}

/// <summary>
/// Socket-agnostic rendezvous logic. UDP handlers return the datagram to send back (or null);
/// TCP handlers return the frame to send on the same connection (or null).
/// In this phase every connection request is brokered through the relay; NAT punching comes later.
/// </summary>
public sealed class RendezvousHandler
{
    private readonly PeerTable _peers;
    private readonly ServerKeys _keys;
    private readonly IdAllocator _ids;
    private readonly RelaySelector _relays;
    private readonly RendezvousStats _stats;
    private readonly PunchRegistry _punches;
    private readonly RendezvousOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<RendezvousHandler> _log;
    private readonly RateLimiter _registerLimiter;
    private readonly RateLimiter _punchLimiter;

    public RendezvousHandler(
        PeerTable peers, ServerKeys keys, IdAllocator ids, RelaySelector relays, RendezvousStats stats, PunchRegistry punches,
        IOptions<RendezvousOptions> options, TimeProvider time, ILogger<RendezvousHandler> log)
    {
        _peers = peers;
        _keys = keys;
        _ids = ids;
        _relays = relays;
        _stats = stats;
        _punches = punches;
        _options = options.Value;
        _time = time;
        _log = log;
        _registerLimiter = new RateLimiter(_options.RegisterRateLimitPerMinute, time);
        _punchLimiter = new RateLimiter(_options.PunchRateLimitPerMinute, time);
    }

    public RendezvousMessage? HandleUdp(RendezvousMessage msg, IPEndPoint from)
    {
        switch (msg.UnionCase)
        {
            case RendezvousMessage.UnionOneofCase.RegisterPeer:
                return HandleRegisterPeer(msg.RegisterPeer, from);
            case RendezvousMessage.UnionOneofCase.RegisterIdentity:
                return HandleRegisterIdentity(msg.RegisterIdentity, from);
            case RendezvousMessage.UnionOneofCase.TestNatRequest:
                return new RendezvousMessage { TestNatResponse = new TestNatResponse { ObservedPort = (uint)from.Port, ObservedAddr = ToWire(from) } };
            case RendezvousMessage.UnionOneofCase.QueryOnline:
                return HandleQueryOnline(msg.QueryOnline);
            default:
                _log.LogDebug("Ignoring UDP {Case} from {From}", msg.UnionCase, from);
                return null;
        }
    }

    public async ValueTask<RendezvousMessage?> HandleTcpAsync(RendezvousMessage msg, IPEndPoint from, IUdpSender udp, CancellationToken ct)
    {
        switch (msg.UnionCase)
        {
            case RendezvousMessage.UnionOneofCase.PunchHoleRequest:
                return await HandlePunchHoleRequestAsync(msg.PunchHoleRequest, from, udp, ct).ConfigureAwait(false);
            case RendezvousMessage.UnionOneofCase.TestNatRequest:
                return new RendezvousMessage { TestNatResponse = new TestNatResponse { ObservedPort = (uint)from.Port, ObservedAddr = ToWire(from) } };
            case RendezvousMessage.UnionOneofCase.QueryOnline:
                return HandleQueryOnline(msg.QueryOnline);
            case RendezvousMessage.UnionOneofCase.KeepAlive:
                return null;
            case RendezvousMessage.UnionOneofCase.PunchHoleSent:
                CompletePunch(msg.PunchHoleSent.ControllerAddr, msg, from);
                return null;
            case RendezvousMessage.UnionOneofCase.LocalAddr:
                CompletePunch(msg.LocalAddr.ControllerAddr, msg, from);
                return null;
            case RendezvousMessage.UnionOneofCase.RelayResponse:
                CompletePunch(msg.RelayResponse.ControllerAddr, msg, from);
                return null;
            default:
                _log.LogDebug("Ignoring TCP {Case} from {From}", msg.UnionCase, from);
                return null;
        }
    }

    /// <summary>A host's answer arrived on its own TCP connection; hand it to the waiting controller request.</summary>
    private void CompletePunch(Protocol.Rendezvous.SocketAddress? controllerAddr, RendezvousMessage answer, IPEndPoint hostFrom)
    {
        IPEndPoint? controller = ToEndPoint(controllerAddr);
        if (controller is null)
        {
            return;
        }

        // The host's public address is where this connection came from; carry it to the controller in the answer.
        if (answer.UnionCase == RendezvousMessage.UnionOneofCase.PunchHoleSent)
        {
            answer = new RendezvousMessage { PunchHoleSent = answer.PunchHoleSent.Clone() };
            answer.PunchHoleSent.ControllerAddr = ToSocketAddress(hostFrom);
        }

        if (!_punches.TryComplete(Key(controller), answer))
        {
            _log.LogDebug("No pending punch for {Controller} ({Case} from {Host})", controller, answer.UnionCase, hostFrom);
        }
    }

    private RendezvousMessage HandleRegisterPeer(RegisterPeer reg, IPEndPoint from)
    {
        _stats.Registration();
        PeerEntry? entry = _peers.Get(reg.Id);
        if (entry is null || entry.IdentityPk.Length == 0)
        {
            return new RendezvousMessage { RegisterPeerResponse = new RegisterPeerResponse { RequestIdentity = true, KeepAliveSec = _options.KeepAliveSeconds } };
        }

        DateTimeOffset now = _time.GetUtcNow();
        // Peers loaded from the store after a restart carry their public key but no signed identity yet; sign it
        // here from the heartbeat, otherwise punch requests would report the host as missing until it restarted.
        bool resign = entry.SignedIdentity is null || now - entry.SignedAtUtc > TimeSpan.FromHours(24);
        _peers.Update(reg.Id, e =>
        {
            PeerEntry baseline = e ?? entry;
            return baseline with
            {
                LastSeenUtc = now,
                UdpEndPoint = from,
                Version = reg.Version,
                SignedIdentity = resign ? _keys.Sign(baseline.Id, baseline.IdentityPk, now) : baseline.SignedIdentity,
                SignedAtUtc = resign ? now : baseline.SignedAtUtc,
            };
        }, persist: false);
        return new RendezvousMessage { RegisterPeerResponse = new RegisterPeerResponse { RequestIdentity = false, KeepAliveSec = _options.KeepAliveSeconds } };
    }

    private RendezvousMessage HandleRegisterIdentity(RegisterIdentity reg, IPEndPoint from)
    {
        _stats.IdentityRegistration();
        if (!_registerLimiter.TryAcquire(from.Address))
        {
            _stats.Reject();
            return IdentityResult(RegisterIdentityResponse.Types.Result.TooFrequent);
        }

        if (reg.Uuid.Length is < 16 or > 64 || reg.IdentityPk.Length is < 60 or > 200)
        {
            _stats.Reject();
            return IdentityResult(RegisterIdentityResponse.Types.Result.Invalid);
        }

        if (reg.Id.Length > 0 && !_ids.IsValid(reg.Id))
        {
            _stats.Reject();
            return IdentityResult(RegisterIdentityResponse.Types.Result.Invalid);
        }

        DateTimeOffset now = _time.GetUtcNow();
        byte[] uuid = reg.Uuid.ToByteArray();
        byte[] pk = reg.IdentityPk.ToByteArray();

        PeerEntry? byUuid = _peers.GetByUuid(uuid);
        PeerEntry? byId = reg.Id.Length > 0 ? _peers.Get(reg.Id) : null;

        string id;
        if (byId is not null)
        {
            if (!byId.Uuid.AsSpan().SequenceEqual(uuid))
            {
                // Another machine owns this id; the client must drop it and re-register.
                _stats.Reject();
                _log.LogWarning("Uuid mismatch for id {Id} from {From}", reg.Id, from);
                return IdentityResult(RegisterIdentityResponse.Types.Result.UuidMismatch);
            }

            id = byId.Id;
        }
        else if (byUuid is not null)
        {
            // Known machine presenting no id (or a stale one): hand back the id it already owns.
            id = byUuid.Id;
        }
        else
        {
            if (_peers.Count >= _options.MaxPeers)
            {
                _stats.Reject();
                return IdentityResult(RegisterIdentityResponse.Types.Result.ServerError);
            }

            id = AllocateId(uuid, pk, now, from, reg.Version);
        }

        PeerEntry updated = _peers.Update(id, e =>
        {
            PeerEntry baseline = e ?? new PeerEntry(id, uuid, pk, now, now, from, reg.Version, null, DateTimeOffset.MinValue);
            bool pkChanged = !baseline.IdentityPk.AsSpan().SequenceEqual(pk);
            bool resign = pkChanged || baseline.SignedIdentity is null || now - baseline.SignedAtUtc > TimeSpan.FromHours(24);
            return baseline with
            {
                Uuid = uuid,
                IdentityPk = pk,
                LastSeenUtc = now,
                UdpEndPoint = from,
                Version = reg.Version,
                SignedIdentity = resign ? _keys.Sign(id, pk, now) : baseline.SignedIdentity,
                SignedAtUtc = resign ? now : baseline.SignedAtUtc,
            };
        }, persist: true);

        _log.LogInformation("Registered identity for {Id} from {From}", id, from);
        return new RendezvousMessage
        {
            RegisterIdentityResponse = new RegisterIdentityResponse
            {
                Result = RegisterIdentityResponse.Types.Result.Ok,
                Id = id,
                KeepAliveSec = _options.KeepAliveSeconds,
                Identity = updated.SignedIdentity,
            },
        };
    }

    private string AllocateId(byte[] uuid, byte[] pk, DateTimeOffset now, IPEndPoint from, string version)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            string candidate = _ids.Next();
            var entry = new PeerEntry(candidate, uuid, pk, now, now, from, version, null, DateTimeOffset.MinValue);
            if (_peers.TryAdd(entry))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not allocate a unique peer id.");
    }

    private async ValueTask<RendezvousMessage> HandlePunchHoleRequestAsync(PunchHoleRequest req, IPEndPoint from, IUdpSender udp, CancellationToken ct)
    {
        _stats.PunchRequest();
        if (!_punchLimiter.TryAcquire(from.Address))
        {
            _stats.Reject();
            return PunchFailure(PunchHoleResponse.Types.Failure.ServerBusy);
        }

        PeerEntry? host = _peers.Get(req.Id);
        if (host is { SignedIdentity: null, IdentityPk.Length: > 0 })
        {
            DateTimeOffset signedAt = _time.GetUtcNow();
            host = _peers.Update(req.Id, e => (e ?? host) with { SignedIdentity = _keys.Sign(host.Id, host.IdentityPk, signedAt), SignedAtUtc = signedAt }, persist: false);
        }

        if (host is null || host.SignedIdentity is null)
        {
            return PunchFailure(PunchHoleResponse.Types.Failure.IdNotExist);
        }

        if (!host.IsOnline(_time.GetUtcNow(), _options.PeerOfflineAfter))
        {
            return PunchFailure(PunchHoleResponse.Types.Failure.Offline);
        }

        (RelaySelector.Outcome outcome, IReadOnlyList<string> relays) = _relays.Select(from.Address);

        // A punched connection never touches a relay, so a missing or full relay must not stop one. This
        // used to refuse every connection when no relay was configured -- including the ones that would
        // have gone peer to peer and never needed it.
        if (!req.ForceRelay)
        {
            return await PunchAsync(req, from, host, relays, udp, ct).ConfigureAwait(false);
        }

        if (outcome != RelaySelector.Outcome.Ok)
        {
            _log.LogError("Cannot broker a relay for {Id}: {Reason}", req.Id, outcome);
            return PunchFailure(PunchHoleResponse.Types.Failure.ServerBusy);
        }

        string relay = relays[0];
        string uuid = Guid.NewGuid().ToString("N");
        RelayTicket ticket = IssueTicket(uuid);
        var toHost = new RendezvousMessage
        {
            RequestRelay = new RequestRelay
            {
                Uuid = uuid,
                Id = req.Id,
                ControllerAddr = ToSocketAddress(from),
                RelayServer = relay,
                ConnType = req.ConnType,
                Ticket = ticket,
            },
        };
        await udp.SendAsync(toHost, host.UdpEndPoint!, ct).ConfigureAwait(false);
        _stats.RelayBrokered();
        _log.LogInformation("Brokered relay {Uuid} for {Controller} -> {Host} via {Relay}", uuid[..8], from, req.Id, relay);

        var response = new RelayResponse
        {
            Uuid = uuid,
            RelayServer = relay,
            Id = req.Id,
            Version = host.Version,
            Identity = host.SignedIdentity,
            ControllerAddr = ToSocketAddress(from),
            Ticket = ticket,
        };
        response.RelayServers.AddRange(relays);
        return new RendezvousMessage { RelayResponse = response };
    }

    /// <summary>
    /// Permission to use the relay for one pairing. Issued for every connection, not only the ones this
    /// server sends to a relay: a session that goes peer to peer still falls back to the relay for its
    /// media channel, and a host behind a symmetric NAT opens its relay leg before the server hears of it.
    /// </summary>
    private RelayTicket IssueTicket(string uuid) =>
        _keys.IssueRelayTicket(uuid, _time.GetUtcNow() + _options.RelayTicketLifetime);

    /// <summary>
    /// Direct path: ask the host for a LAN address (same public IP) or a punch, then wait for its answer on
    /// this controller's connection. The host may still answer with a relay when its NAT is symmetric.
    /// </summary>
    private async ValueTask<RendezvousMessage> PunchAsync(
        PunchHoleRequest req, IPEndPoint from, PeerEntry host, IReadOnlyList<string> relays, IUdpSender udp, CancellationToken ct)
    {
        // Empty when nothing is configured or everything is full. The host is told so, and answers with a
        // punch rather than offering a relay it cannot reach either.
        string relay = relays.Count > 0 ? relays[0] : string.Empty;
        string key = Key(from);

        // The uuid a relay leg of this connection will use, decided here so the ticket can be issued
        // before anyone dials: the host gets it with the punch, the controller with the answer.
        string relayUuid = Guid.NewGuid().ToString("N");
        RelayTicket ticket = IssueTicket(relayUuid);
        TaskCompletionSource<RendezvousMessage> pending = _punches.Register(key);
        try
        {
            bool sameIp = !_options.AlwaysPunch && host.UdpEndPoint is not null && SameAddress(host.UdpEndPoint.Address, from.Address);
            RendezvousMessage toHost = sameIp
                ? new RendezvousMessage { FetchLocalAddr = new FetchLocalAddr { ControllerAddr = ToSocketAddress(from), RelayServer = relay, RelayTicket = ticket } }
                : new RendezvousMessage { PunchHole = new PunchHole { ControllerAddr = ToSocketAddress(from), RelayServer = relay, NatType = req.NatType, ConnType = req.ConnType, RelayTicket = ticket } };
            await udp.SendAsync(toHost, host.UdpEndPoint!, ct).ConfigureAwait(false);
            _log.LogInformation("{What} {Controller} -> {Host}", sameIp ? "LAN offer" : "Punch", from, req.Id);

            RendezvousMessage answer;
            try
            {
                answer = await pending.Task.WaitAsync(_options.PunchPendingTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _log.LogInformation("Host {Id} did not answer the punch for {Controller}", req.Id, from);
                return PunchFailure(PunchHoleResponse.Types.Failure.Offline);
            }

            switch (answer.UnionCase)
            {
                case RendezvousMessage.UnionOneofCase.PunchHoleSent:
                {
                    var punched = new PunchHoleResponse
                    {
                        HostAddr = answer.PunchHoleSent.ControllerAddr, // the host's public address (see CompletePunch)
                        Identity = host.SignedIdentity,
                        RelayServer = relay,
                        NatType = answer.PunchHoleSent.NatType,
                        IsLocal = false,
                        HostVersion = answer.PunchHoleSent.Version,
                        RelayTicket = ticket,
                    };
                    punched.RelayServers.AddRange(relays);
                    return new RendezvousMessage { PunchHoleResponse = punched };
                }

                case RendezvousMessage.UnionOneofCase.LocalAddr:
                {
                    var local = new PunchHoleResponse
                    {
                        HostAddr = answer.LocalAddr.LocalAddr_,
                        Identity = host.SignedIdentity,
                        RelayServer = relay,
                        IsLocal = true,
                        HostVersion = answer.LocalAddr.Version,
                        RelayTicket = ticket,
                    };
                    local.RelayServers.AddRange(relays);
                    return new RendezvousMessage { PunchHoleResponse = local };
                }

                case RendezvousMessage.UnionOneofCase.RelayResponse:
                {
                    // The host volunteers a relay when its own NAT is symmetric, and that address used to be
                    // forwarded to the controller unchecked -- a host that had been told to could point every
                    // session it took part in at a machine of its choosing. Only an address this rendezvous
                    // configured is passed on.
                    string offered = answer.RelayResponse.RelayServer;
                    if (offered.Length > 0 && !_relays.IsConfigured(offered))
                    {
                        _log.LogWarning("Host {Id} offered an unconfigured relay {Relay}; using our own", req.Id, offered);
                        offered = string.Empty;
                    }

                    if (offered.Length == 0 && relay.Length == 0)
                    {
                        return PunchFailure(PunchHoleResponse.Types.Failure.ServerBusy);
                    }

                    _stats.RelayBrokered();
                    var relayed = new RelayResponse
                    {
                        Uuid = answer.RelayResponse.Uuid,
                        RelayServer = offered.Length > 0 ? offered : relay,
                        Id = req.Id,
                        Version = answer.RelayResponse.Version,
                        Identity = host.SignedIdentity,
                        ControllerAddr = ToSocketAddress(from),
                        // A host that used the uuid it was given gets the ticket it was given; a host that
                        // minted its own (protocol 1 did) gets one for that, since the relay trusts this
                        // server and not the host either way.
                        Ticket = answer.RelayResponse.Uuid == relayUuid ? ticket : IssueTicket(answer.RelayResponse.Uuid),
                    };
                    relayed.RelayServers.AddRange(relays);
                    return new RendezvousMessage { RelayResponse = relayed };
                }

                default:
                    return PunchFailure(PunchHoleResponse.Types.Failure.ServerBusy);
            }
        }
        finally
        {
            _punches.Remove(key, pending);
        }
    }

    private static string Key(IPEndPoint ep)
    {
        IPAddress ip = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
        return $"{ip}:{ep.Port}";
    }

    private static bool SameAddress(IPAddress a, IPAddress b)
    {
        static IPAddress Plain(IPAddress x) => x.IsIPv4MappedToIPv6 ? x.MapToIPv4() : x;
        return Plain(a).Equals(Plain(b));
    }

    private static Protocol.Rendezvous.SocketAddress ToWire(IPEndPoint ep)
    {
        IPAddress ip = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
        return new Protocol.Rendezvous.SocketAddress { Ip = ByteString.CopyFrom(ip.GetAddressBytes()), Port = (uint)ep.Port };
    }

    private static IPEndPoint? ToEndPoint(Protocol.Rendezvous.SocketAddress? address)
    {
        if (address is null || address.Ip.Length is not (4 or 16) || address.Port is 0 or > 65535)
        {
            return null;
        }

        var ip = new IPAddress(address.Ip.Span);
        return new IPEndPoint(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip, (int)address.Port);
    }

    private RendezvousMessage HandleQueryOnline(QueryOnline q)
    {
        DateTimeOffset now = _time.GetUtcNow();
        var resp = new QueryOnlineResponse();
        foreach (string id in q.Ids.Take(100))
        {
            resp.Online.Add(_peers.Get(id)?.IsOnline(now, _options.PeerOfflineAfter) ?? false);
        }

        return new RendezvousMessage { QueryOnlineResponse = resp };
    }

    private static RendezvousMessage IdentityResult(RegisterIdentityResponse.Types.Result result) =>
        new() { RegisterIdentityResponse = new RegisterIdentityResponse { Result = result } };

    private static RendezvousMessage PunchFailure(PunchHoleResponse.Types.Failure failure) =>
        new() { PunchHoleResponse = new PunchHoleResponse { Failure = failure } };

    public static Protocol.Rendezvous.SocketAddress ToSocketAddress(IPEndPoint ep)
    {
        IPAddress ip = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
        return new Protocol.Rendezvous.SocketAddress { Ip = ByteString.CopyFrom(ip.GetAddressBytes()), Port = (uint)ep.Port };
    }
}
