using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using DeskPair.Core.Config;
using DeskPair.Core.Session.Host.Auth;
using DeskPair.Core.Session.Host.Handlers;
using DeskPair.Core.Transport;
using DeskPair.Protocol;
using DeskPair.Protocol.Messages;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Core.Session.Host;

/// <summary>
/// Process-level host state: identity, passwords, policy, rendezvous registration, the direct-access listener
/// and the set of live sessions. Services (video/audio/...) attach in later phases through
/// <see cref="OnSessionAuthorizedAsync"/>.
/// </summary>
public sealed class HostRuntime : IAsyncDisposable
{
    private readonly ILoggerFactory _logs;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<int, HostSession> _sessions = new();
    private readonly CancellationTokenSource _cts = new();
    private int _nextConnectionId;
    private int _disposed;
    private Task? _rendezvousTask;
    private Task? _listenerTask;

    public HostRuntime(
        PeerSettings settings, PeerIdentityStore identity, HostPasswords passwords, HostPolicy policy,
        IConnectionApprover approver, ILoggerFactory logs, TimeProvider? time = null, IEnumerable<ISessionHandler<HostSessionContext>>? extraHandlers = null)
    {
        Settings = settings;
        Identity = identity;
        Passwords = passwords;
        Policy = policy;
        Approver = approver;
        _logs = logs;
        _log = logs.CreateLogger<HostRuntime>();
        _time = time ?? TimeProvider.System;
        FailureTracker = new LoginFailureTracker(_time);
        Rendezvous = new HostRendezvousClient(settings, identity, logs.CreateLogger<HostRendezvousClient>(), _time);
        Rendezvous.RelayRequested += OnRelayRequestedAsync;
        Rendezvous.PunchRequested += OnPunchRequestedAsync;
        Rendezvous.LocalAddrRequested += OnLocalAddrRequestedAsync;
        Nat = new Transport.Nat.NatTypeDetector(settings, logs.CreateLogger("DeskPair.Core.Transport.Nat"), _time);
        Puncher = new Transport.Nat.TcpPuncher(settings, logs.CreateLogger("DeskPair.Core.Transport.Punch"));
        var handlers = new List<ISessionHandler<HostSessionContext>>
        {
            new ChatHandler(), new CloseReasonHandler(), new TestDelayHandler(_time), new OptionsHandler(), new MediaChannelHandler(),
        };
        if (extraHandlers is not null)
        {
            handlers.AddRange(extraHandlers);
        }

        Dispatcher = new MessageDispatcher<HostSessionContext>(handlers, logs.CreateLogger("DeskPair.Core.Session.Host.Dispatcher"));
    }

    public PeerSettings Settings { get; }
    public PeerIdentityStore Identity { get; }
    public HostPasswords Passwords { get; }
    /// <summary>
    /// Host settings in force. Assigning applies them live: new connections use them from the next handshake,
    /// and every open session recomputes its permissions (a tightened setting takes effect at once).
    /// </summary>
    public HostPolicy Policy
    {
        get;
        set
        {
            field = value;
            foreach (HostSession session in Sessions)
            {
                session.Context.Permissions.UpdatePolicy(value);
            }
        }
    }
    public IConnectionApprover Approver { get; }
    public LoginFailureTracker FailureTracker { get; }
    public HostRendezvousClient Rendezvous { get; }

    public Transport.Nat.NatTypeDetector Nat { get; }

    public Transport.Nat.TcpPuncher Puncher { get; }

    /// <summary>Answer every punch with a relay (symmetric NAT known, or policy).</summary>
    public bool PreferRelay { get; set; }

    /// <summary>
    /// Who may connect at all. Assigning applies to the next connection; sessions already open are left
    /// alone, because a list is about who may come in, not about throwing out somebody already working.
    /// </summary>
    public PeerAllowlist Allowlist { get; set; } = PeerAllowlist.Off;

    /// <summary>Refuse relayed connections outright; see <see cref="Config.HostConfig.RefuseRelayed"/>.</summary>
    public bool RefuseRelayed { get; set; }
    public MessageDispatcher<HostSessionContext> Dispatcher { get; }
    public SessionScope Scope { get; } = new();

    /// <summary>Direct-access port; 0 disables the listener, -1 picks an ephemeral port (tests).</summary>
    public int DirectAccessPort { get; init; } = ProtocolConstants.DirectAccessPort;

    public int BoundDirectAccessPort { get; private set; }

    public IReadOnlyCollection<HostSession> Sessions => (IReadOnlyCollection<HostSession>)_sessions.Values;

    /// <summary>Raised after LoginResponse/PeerInfo went out; modules subscribe services here.</summary>
    public event Func<HostSession, CancellationToken, Task>? SessionAuthorized;

    /// <summary>Raised before the session's pump is torn down.</summary>
    public event Func<HostSession, Task>? SessionClosing;

    /// <summary>
    /// Raised once a session is fully closed, with why it ended -- which <see cref="SessionClosing"/> cannot
    /// carry, because it runs while the session is still deciding. The connection journal's hook.
    /// </summary>
    public event Action<HostSession, string>? SessionEnded;

    internal void RaiseSessionEnded(HostSession session, string reason)
    {
        try
        {
            SessionEnded?.Invoke(session, reason);
        }
        catch (Exception e)
        {
            // Recording that a session ended must never be the thing that breaks ending it.
            _log.LogWarning(e, "A session-ended hook threw");
        }
    }

    public Func<IEnumerable<DisplayInfo>>? DisplayProvider { get; set; }

    /// <summary>
    /// What this machine can encode, for the login response. It used to send an empty
    /// <see cref="SupportedEncoding"/>, so a viewer had no way to know what it was about to be sent.
    /// </summary>
    public Func<SupportedEncoding>? EncodingProvider { get; set; }

    /// <summary>A media module that understands display subscriptions is attached; told to viewers as <c>PeerInfo.multi_display</c>.</summary>
    public bool SupportsMultiDisplay { get; set; }

    /// <summary>Round-trip measurements from TestDelay probes, per connection (feeds QoS).</summary>
    public Action<int, TimeSpan>? RoundTripReported { get; set; }

    /// <summary>
    /// UDP media feedback: (connection, display, highest decodable frame seq, frames given up so far, packet loss
    /// fraction). The loss is the link's and comes once per round, so it is null on the other streams' reports.
    /// </summary>
    public Action<int, int, uint, uint, double?>? MediaFeedbackReported { get; set; }

    /// <summary>GCC bandwidth estimate of a connection's UDP media channel (bits per second).</summary>
    public Action<int, double>? BandwidthEstimated { get; set; }

    /// <summary>Highest bitrate worth probing for on a connection's UDP channel (bits per second; 0 = unknown).</summary>
    public Func<int, double>? MediaBitrateCeilingBps { get; set; }

    /// <summary>The controller changed its session options mid-session (quality, frame rate, refinement).</summary>
    public Action<int, SessionOptions>? OptionsUpdated { get; set; }

    /// <summary>The UDP media channel of a connection closed; frames in flight on it will never be acknowledged.</summary>
    public Action<int>? MediaChannelClosed { get; set; }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (Settings.RendezvousServer.Length > 0)
        {
            _rendezvousTask = Task.Run(() => Rendezvous.RunAsync(_cts.Token), ct);
            if (!PreferRelay)
            {
                _ = Task.Run(() => Nat.DetectAsync(_cts.Token), ct); // warms the cache; punches re-check it
            }
        }
        else
        {
            _log.LogInformation("No rendezvous server configured; reachable by direct address only");
        }
        if (DirectAccessPort != 0)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _listenerTask = Task.Run(() => ListenDirectAsync(ready, _cts.Token), ct);
            return ready.Task;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Accepts an already-connected transport (tests and direct listener), unless the allowlist refuses it.
    /// Returns null when it was refused, and the transport is disposed.
    ///
    /// <paramref name="reportedAddress"/> is the peer's address as the rendezvous server saw it, which for a
    /// relayed connection is the only place the real one appears -- the socket belongs to the relay.
    /// </summary>
    public HostSession? Accept(IPeerTransport transport, string? relayServer = null, RelayTicket? relayTicket = null, IPAddress? reportedAddress = null)
    {
        IPAddress? socketAddress = (transport.RemoteEndPoint as IPEndPoint)?.Address;
        if (Refuse(transport.Kind, socketAddress, reportedAddress) is { } refusal)
        {
            // No handshake, no message, one log line: a scanner learns nothing it did not already know.
            _log.LogInformation("Refused a {Kind} connection from {Address}: {Why}", transport.Kind, Describe(socketAddress, reportedAddress), refusal);
            _ = transport.DisposeAsync().AsTask();
            return null;
        }

        int id = Interlocked.Increment(ref _nextConnectionId);
        var session = new HostSession(id, transport, this, _logs.CreateLogger($"DeskPair.Core.Session.Host.Session[{id}]"), _time) { RelayServer = relayServer ?? string.Empty, RelayTicket = relayTicket };
        // Kept for the second half of the allowlist check, which can only happen once the peer has said who
        // it is. Computed here so both halves judge the same address.
        session.AllowlistAddress = transport.Kind == TransportKind.Relay ? reportedAddress : socketAddress ?? reportedAddress;
        _sessions[id] = session;
        _ = Task.Run(async () =>
        {
            try
            {
                await session.RunAsync(_cts.Token).ConfigureAwait(false);
            }
            finally
            {
                _sessions.TryRemove(id, out _);
            }
        }, CancellationToken.None);
        return session;
    }

    /// <summary>Why this connection is not welcome, or null when it is.</summary>
    private string? Refuse(TransportKind kind, IPAddress? socketAddress, IPAddress? reportedAddress)
    {
        bool relayed = kind == TransportKind.Relay;
        if (relayed && RefuseRelayed)
        {
            return "relayed connections are refused";
        }

        PeerAllowlist list = Allowlist;
        if (!list.Enabled)
        {
            return null;
        }

        // A relayed socket shows the relay, so the rendezvous server's word is all there is. Everything else
        // is judged on what the socket itself shows, which cannot be asserted by a third party.
        IPAddress? judged = relayed ? reportedAddress : socketAddress ?? reportedAddress;
        if (relayed && judged is null)
        {
            return "the relay did not say where this peer is";
        }

        return list.MayAccept(judged) ? null : "not in the allowlist";
    }

    private static string Describe(IPAddress? socketAddress, IPAddress? reportedAddress) =>
        reportedAddress is null || reportedAddress.Equals(socketAddress)
            ? socketAddress?.ToString() ?? "an unknown address"
            : $"{reportedAddress} (via {socketAddress?.ToString() ?? "?"})";

    private async Task OnRelayRequestedAsync(RequestRelay request, CancellationToken ct)
    {
        IPeerTransport transport = await RelayClient.ConnectAsync(request.RelayServer, request.Uuid, Identity.Id, request.ConnType, Settings.ConnectTimeout, ct, request.Ticket).ConfigureAwait(false);
        Accept(transport, request.RelayServer, request.Ticket, SocketAddresses.ToEndPoint(request.ControllerAddr)?.Address);
    }

    private async Task OnPunchRequestedAsync(PunchHole punch, CancellationToken ct)
    {
        IPEndPoint? controller = SocketAddresses.ToEndPoint(punch.ControllerAddr);
        if (controller is null)
        {
            _log.LogWarning("Punch request without a controller address");
            return;
        }

        NatType myNat = PreferRelay ? NatType.NatSymmetric : await Nat.DetectAsync(ct).ConfigureAwait(false);
        if (PreferRelay || myNat == NatType.NatSymmetric || punch.NatType == NatType.NatSymmetric)
        {
            // Symmetric NAT on either side: open the relay leg first, then tell the server where to send the
            // controller. The uuid is the one the server issued a ticket for; the relay checks that they match.
            string uuid = Protocol.Crypto.RelayTickets.UuidOf(punch.RelayTicket) ?? Guid.NewGuid().ToString("N");
            IPeerTransport relay = await RelayClient.ConnectAsync(punch.RelayServer, uuid, Identity.Id, punch.ConnType, Settings.ConnectTimeout, ct, punch.RelayTicket).ConfigureAwait(false);
            try
            {
                await Puncher.AnnounceRelayAsync(controller, uuid, punch.RelayServer, Identity.Id, Settings.Version, ct).ConfigureAwait(false);
            }
            catch
            {
                await relay.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _log.LogInformation("Answered punch from {Controller} with relay {Uuid} (nat {Mine}/{Theirs})", controller, uuid[..8], myNat, punch.NatType);
            Accept(relay, punch.RelayServer, punch.RelayTicket, controller.Address);
            return;
        }

        IPeerTransport transport = await Puncher.AnswerPunchAsync(controller, Identity.Id, myNat, punch.RelayServer, Settings.Version, ct).ConfigureAwait(false);
        Accept(transport, punch.RelayServer, punch.RelayTicket, controller.Address);
    }

    private async Task OnLocalAddrRequestedAsync(FetchLocalAddr request, CancellationToken ct)
    {
        IPEndPoint? controller = SocketAddresses.ToEndPoint(request.ControllerAddr);
        if (controller is null)
        {
            return;
        }

        IPeerTransport transport = await Puncher.AnswerLocalAsync(controller, Identity.Id, request.RelayServer, Settings.Version, ct).ConfigureAwait(false);
        Accept(transport, request.RelayServer, request.RelayTicket, controller.Address);
    }

    /// <summary>How long to wait before trying a direct-access port that another program holds again.</summary>
    internal static readonly TimeSpan DirectAccessRetry = TimeSpan.FromSeconds(5);

    private async Task ListenDirectAsync(TaskCompletionSource ready, CancellationToken ct)
    {
        Socket? listener = null;
        bool waiting = false;
        while (listener is null)
        {
            var candidate = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
            try
            {
                candidate.Bind(new IPEndPoint(IPAddress.IPv6Any, DirectAccessPort < 0 ? 0 : DirectAccessPort));
                candidate.Listen(16);
                listener = candidate;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                // Most often the engine this one replaces, still letting go: the app hands the engine to the
                // service while its own is shutting down. Rendezvous and relay do not wait for it; direct access
                // does, and starts the moment the port is free instead of staying off until the next restart.
                candidate.Dispose();
                if (!waiting)
                {
                    _log.LogWarning("Direct-access port {Port} is in use; trying again every {Seconds} s, rendezvous and relay work meanwhile", DirectAccessPort, DirectAccessRetry.TotalSeconds);
                    waiting = true;
                    ready.TrySetResult();
                }

                try
                {
                    await Task.Delay(DirectAccessRetry, _time, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            catch (SocketException e)
            {
                // Direct access is one way in, not the only one. Faulting here took the whole host down over a
                // port another program happened to hold -- on Linux that surfaced as a core dump before anything
                // had a chance to log why. Turn the feature off, say so, and let rendezvous and relay carry on.
                candidate.Dispose();
                _log.LogError(e, "Cannot listen on direct-access port {Port}; direct connections are off for this run, rendezvous and relay still work", DirectAccessPort);
                BoundDirectAccessPort = 0;
                ready.TrySetResult();
                return;
            }
        }

        using Socket _ = listener;
        BoundDirectAccessPort = ((IPEndPoint)listener.LocalEndPoint!).Port;
        ready.TrySetResult();
        _log.LogInformation("Direct access listening on tcp/{Port}", BoundDirectAccessPort);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Socket client = await listener.AcceptAsync(ct).ConfigureAwait(false);
                Accept(new TcpPeerTransport(client, TransportKind.DirectTcp));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException e)
            {
                _log.LogWarning(e, "Direct-access accept failed");
            }
        }
    }

    internal async Task OnSessionAuthorizedAsync(HostSession session, CancellationToken ct)
    {
        if (SessionAuthorized is { } handlers)
        {
            foreach (Func<HostSession, CancellationToken, Task> h in handlers.GetInvocationList().Cast<Func<HostSession, CancellationToken, Task>>())
            {
                await h(session, ct).ConfigureAwait(false);
            }
        }
    }

    internal async Task OnSessionClosingAsync(HostSession session)
    {
        if (SessionClosing is { } handlers)
        {
            foreach (Func<HostSession, Task> h in handlers.GetInvocationList().Cast<Func<HostSession, Task>>())
            {
                try
                {
                    await h(session).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _log.LogWarning(e, "Session closing hook failed");
                }
            }
        }
    }

    internal IEnumerable<DisplayInfo> DescribeDisplays() => DisplayProvider?.Invoke() ?? [];

    internal void RecordRoundTrip(int connectionId, TimeSpan rtt)
    {
        if (_sessions.TryGetValue(connectionId, out HostSession? s))
        {
            s.LastRoundTrip = rtt;
        }

        RoundTripReported?.Invoke(connectionId, rtt);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        foreach (HostSession s in _sessions.Values)
        {
            await s.CloseAsync("host shutting down").ConfigureAwait(false);
        }

        foreach (Task? t in new[] { _rendezvousTask, _listenerTask })
        {
            if (t is not null)
            {
                try
                {
                    await t.ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }
        }

        await Rendezvous.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
