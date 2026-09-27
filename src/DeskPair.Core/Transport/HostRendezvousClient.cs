using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using DeskPair.Core.Config;
using DeskPair.Protocol;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Core.Transport;

public sealed record RendezvousStatus(bool Registered, string Id, DateTimeOffset LastResponseUtc, TimeSpan Latency, int ConsecutiveFailures);

/// <summary>
/// Keeps the host registered with the rendezvous server over UDP and surfaces server pushes
/// (relay requests, later punch requests). One instance per configured server.
/// </summary>
public sealed class HostRendezvousClient : IAsyncDisposable
{
    private readonly PeerSettings _settings;
    private readonly PeerIdentityStore _identity;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Socket? _socket;
    private IPEndPoint? _server;
    private int _serial;
    private TimeSpan _interval = ProtocolConstants.RegisterInterval;
    private DateTimeOffset _lastResponse;
    private DateTimeOffset _lastRegisterSent;
    private int _failures;
    private bool _identityRequested = true;

    public HostRendezvousClient(PeerSettings settings, PeerIdentityStore identity, ILogger log, TimeProvider? time = null)
    {
        _settings = settings;
        _identity = identity;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised when the server asks this host to open a relay session toward a controller.</summary>
    public event Func<RequestRelay, CancellationToken, Task>? RelayRequested;

    /// <summary>Raised when a controller wants a direct connection; the handler punches or answers with a relay.</summary>
    public event Func<PunchHole, CancellationToken, Task>? PunchRequested;

    /// <summary>Raised when a controller shares our public IP; the handler offers a LAN address.</summary>
    public event Func<FetchLocalAddr, CancellationToken, Task>? LocalAddrRequested;

    /// <summary>Raised when the server assigns or confirms this host's id.</summary>
    public event Action<string>? IdAssigned;

    /// <summary>Raised whenever <see cref="State"/> changes: the app shows it beside the id.</summary>
    public event Action<RendezvousLinkState>? StateChanged;

    /// <summary>
    /// Whether the server is answering our registrations. Two missed intervals (about twenty-five
    /// seconds) is unreachable -- the host itself is fine, but nothing outside its network can find it
    /// -- and the first answer after that is registered again.
    /// </summary>
    public RendezvousLinkState State { get; private set; } = RendezvousLinkState.Connecting;

    /// <summary>Set by the OS telling us the network moved; the loop rebinds within a second instead of after four missed intervals.</summary>
    private volatile bool _networkChanged;

    public SignedPeerIdentity? SignedIdentity { get; private set; }

    public RendezvousStatus Status => new(
        _failures == 0 && _lastResponse != default && _identity.Id.Length > 0,
        _identity.Id, _lastResponse, Latency, _failures);

    public TimeSpan Latency { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        // A cable pulled, Wi-Fi joined, a VPN up or down: the address change is the earliest signal there
        // is, and waiting for four unanswered registrations instead meant close to a minute during which
        // the machine had a network again and nobody could reach it. The handler only raises a flag; the
        // loop below does the work on its own thread and its own socket.
        NetworkAddressChangedEventHandler onAddress = (_, _) => _networkChanged = true;
        NetworkAvailabilityChangedEventHandler onAvailability = (_, _) => _networkChanged = true;
        NetworkChange.NetworkAddressChanged += onAddress;
        NetworkChange.NetworkAvailabilityChanged += onAvailability;
        try
        {
            await RunLoopAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= onAddress;
            NetworkChange.NetworkAvailabilityChanged -= onAvailability;
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Rendezvous loop failed; retrying in 5s");
                _failures++;
                SetState(RendezvousLinkState.Unreachable);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _time, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        (string host, int port) = TcpConnector.ParseHostPort(_settings.RendezvousServer, ProtocolConstants.RendezvousPort);
        IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        IPAddress address = addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6 ? 1 : 0).First();
        _server = new IPEndPoint(address.MapToIPv6(), port);

        using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        DisableConnectionReset(socket);
        socket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        _socket = socket;
        _identityRequested = _identity.IsLocalId;
        _networkChanged = false;
        _log.LogInformation("Rendezvous client bound to {Local}, server {Server}", socket.LocalEndPoint, _server);

        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task receive = ReceiveLoopAsync(socket, loopCts.Token);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
            await SendRegistrationAsync(ct).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (receive.IsCompleted)
                {
                    await receive.ConfigureAwait(false); // propagate the receive failure
                    throw new IOException("Receive loop ended.");
                }

                if (_networkChanged)
                {
                    _log.LogInformation("The network changed; re-resolving the rendezvous server and registering again");
                    SetState(RendezvousLinkState.Connecting);
                    return; // outer loop re-resolves and rebinds, at once rather than after four misses
                }

                DateTimeOffset now = _time.GetUtcNow();
                if (now - _lastRegisterSent >= _interval)
                {
                    if (_lastResponse < _lastRegisterSent)
                    {
                        _failures++;
                        if (_failures >= 2)
                        {
                            SetState(RendezvousLinkState.Unreachable);
                        }

                        if (_failures >= 4)
                        {
                            _log.LogWarning("No rendezvous response for {Failures} intervals; rebinding", _failures);
                            return; // outer loop re-resolves and rebinds
                        }
                    }

                    await SendRegistrationAsync(ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await loopCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await receive.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            _socket = null;
        }
    }

    private async Task SendRegistrationAsync(CancellationToken ct)
    {
        _lastRegisterSent = _time.GetUtcNow();
        RendezvousMessage msg;
        // A locally derived id (no server has ever assigned one) must not be offered to the server as our id:
        // registering with an empty id is what asks the server to assign the real one.
        if (_identityRequested || _identity.IsLocalId)
        {
            msg = new RendezvousMessage
            {
                RegisterIdentity = new RegisterIdentity
                {
                    Id = _identity.IsLocalId ? string.Empty : _identity.Id,
                    Uuid = ByteString.CopyFrom(_identity.MachineId),
                    IdentityPk = ByteString.CopyFrom(_identity.Key.PublicKeySpki),
                    Version = _settings.Version,
                },
            };
        }
        else
        {
            msg = new RendezvousMessage { RegisterPeer = new RegisterPeer { Id = _identity.Id, Serial = ++_serial, Version = _settings.Version } };
        }

        await SendAsync(msg, ct).ConfigureAwait(false);
    }

    private async Task SendAsync(RendezvousMessage msg, CancellationToken ct)
    {
        Socket? socket = _socket;
        IPEndPoint? server = _server;
        if (socket is null || server is null)
        {
            return;
        }

        byte[] bytes = msg.ToByteArray();
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendToAsync(bytes, SocketFlags.None, server, ct).ConfigureAwait(false);
        }
        catch (SocketException e)
        {
            _log.LogDebug(e, "UDP send failed");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken ct)
    {
        byte[] buffer = new byte[2048];
        var from = new System.Net.SocketAddress(AddressFamily.InterNetworkV6);
        while (!ct.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await socket.ReceiveFromAsync(buffer, SocketFlags.None, from, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException e)
            {
                _log.LogDebug(e, "UDP receive error");
                continue;
            }

            RendezvousMessage msg;
            try
            {
                msg = RendezvousMessage.Parser.ParseFrom(buffer.AsSpan(0, n));
            }
            catch (InvalidProtocolBufferException)
            {
                continue;
            }

            try
            {
                await HandleAsync(msg, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _log.LogError(e, "Handling rendezvous {Case} failed", msg.UnionCase);
            }
        }
    }

    private async Task HandleAsync(RendezvousMessage msg, CancellationToken ct)
    {
        switch (msg.UnionCase)
        {
            case RendezvousMessage.UnionOneofCase.RegisterPeerResponse:
                MarkResponse();
                ApplyKeepAlive(msg.RegisterPeerResponse.KeepAliveSec);
                if (msg.RegisterPeerResponse.RequestIdentity)
                {
                    _identityRequested = true;
                    await SendRegistrationAsync(ct).ConfigureAwait(false);
                }

                break;

            case RendezvousMessage.UnionOneofCase.RegisterIdentityResponse:
                MarkResponse();
                await HandleIdentityResponseAsync(msg.RegisterIdentityResponse, ct).ConfigureAwait(false);
                break;

            case RendezvousMessage.UnionOneofCase.RequestRelay:
                _log.LogInformation("Relay requested by server: {Uuid} via {Relay}", msg.RequestRelay.Uuid[..Math.Min(8, msg.RequestRelay.Uuid.Length)], msg.RequestRelay.RelayServer);
                if (RelayRequested is { } handler)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await handler(msg.RequestRelay, ct).ConfigureAwait(false);
                        }
                        catch (Exception e) when (e is not OperationCanceledException)
                        {
                            _log.LogError(e, "Relay session setup failed");
                        }
                    }, ct);
                }

                break;

            case RendezvousMessage.UnionOneofCase.PunchHole:
                _log.LogInformation("Punch requested by server toward {Controller}", SocketAddresses.ToEndPoint(msg.PunchHole.ControllerAddr));
                Dispatch(PunchRequested, msg.PunchHole, "Punch", ct);
                break;

            case RendezvousMessage.UnionOneofCase.FetchLocalAddr:
                _log.LogInformation("LAN address requested by server for {Controller}", SocketAddresses.ToEndPoint(msg.FetchLocalAddr.ControllerAddr));
                Dispatch(LocalAddrRequested, msg.FetchLocalAddr, "LAN offer", ct);
                break;

            default:
                _log.LogDebug("Ignoring rendezvous {Case}", msg.UnionCase);
                break;
        }
    }

    private void Dispatch<T>(Func<T, CancellationToken, Task>? handler, T message, string what, CancellationToken ct)
    {
        if (handler is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await handler(message, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogWarning(e, "{What} failed", what);
            }
        }, ct);
    }

    private async Task HandleIdentityResponseAsync(RegisterIdentityResponse resp, CancellationToken ct)
    {
        switch (resp.Result)
        {
            case RegisterIdentityResponse.Types.Result.Ok:
                ApplyKeepAlive(resp.KeepAliveSec);
                _identityRequested = false;
                SignedIdentity = resp.Identity;
                if (!string.Equals(resp.Id, _identity.Id, StringComparison.Ordinal))
                {
                    _log.LogInformation("Rendezvous assigned id {Id}", resp.Id);
                    await _identity.SetIdAsync(resp.Id, ct).ConfigureAwait(false);
                    IdAssigned?.Invoke(resp.Id);
                }

                break;

            case RegisterIdentityResponse.Types.Result.UuidMismatch:
                _log.LogWarning("Server reports our id {Id} belongs to another machine; requesting a new id", _identity.Id);
                await _identity.SetIdAsync(string.Empty, ct).ConfigureAwait(false);
                _identityRequested = true;
                await SendRegistrationAsync(ct).ConfigureAwait(false);
                break;

            case RegisterIdentityResponse.Types.Result.TooFrequent:
                _log.LogWarning("Identity registration rate-limited; will retry");
                break;

            default:
                _log.LogError("Identity registration failed: {Result}", resp.Result);
                break;
        }
    }

    private void MarkResponse()
    {
        DateTimeOffset now = _time.GetUtcNow();
        Latency = now - _lastRegisterSent;
        _lastResponse = now;
        _failures = 0;
        SetState(RendezvousLinkState.Registered);
    }

    private void SetState(RendezvousLinkState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        if (state == RendezvousLinkState.Unreachable)
        {
            _log.LogWarning("The rendezvous server is not answering; this machine cannot be reached by id until it does");
        }
        else if (state == RendezvousLinkState.Registered)
        {
            _log.LogInformation("Registered with the rendezvous server");
        }

        StateChanged?.Invoke(state);
    }

    private void ApplyKeepAlive(int seconds)
    {
        if (seconds > 0)
        {
            _interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 3, 300));
        }
    }

    private static void DisableConnectionReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
        }
        catch (SocketException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Whether the signalling server is answering this host's registrations.</summary>
public enum RendezvousLinkState
{
    /// <summary>Bound and registering; no answer yet, or the network just changed.</summary>
    Connecting = 0,

    /// <summary>The server answered the last registration; the host can be found by id.</summary>
    Registered = 1,

    /// <summary>Two registrations in a row went unanswered: no network, or the server is down.</summary>
    Unreachable = 2,
}
