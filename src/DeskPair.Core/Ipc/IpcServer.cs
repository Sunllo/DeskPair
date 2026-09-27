using System.Collections.Concurrent;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using DeskPair.Protocol;
using DeskPair.Protocol.Framing;
using DeskPair.Protocol.Ipc;

namespace DeskPair.Core.Ipc;

public sealed record IpcClientInfo(int Id, string Role)
{
    /// <summary>
    /// Who the operating system says opened the connection. Unlike <see cref="Role"/>, which the client
    /// chooses for itself, this is not something a caller can assert.
    /// </summary>
    public IpcCaller Caller { get; init; } = IpcCaller.Local;
}

/// <summary>Answers requests from UI processes; returning null sends no reply.</summary>
public interface IIpcHostBridge
{
    Task<IpcMessage?> HandleAsync(IpcMessage request, IpcClientInfo client, CancellationToken ct);

    void ClientConnected(IpcClientInfo client)
    {
    }

    void ClientDisconnected(IpcClientInfo client)
    {
    }
}

/// <summary>
/// Accepts UI/CM/tray processes on the local endpoint. Every client must open with an IpcHello carrying
/// the one-time token this process handed it at spawn; anything else is dropped before any state is exposed.
/// </summary>
public sealed class IpcServer : IAsyncDisposable
{
    private readonly IpcEndpoint _endpoint;
    private readonly byte[] _token;
    private readonly IIpcHostBridge _bridge;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<int, Client> _clients = new();
    private readonly CancellationTokenSource _cts = new();
    private IIpcListener? _listener;
    private Task? _acceptLoop;
    private int _nextClientId;

    public IpcServer(IpcEndpoint endpoint, byte[] token, IIpcHostBridge bridge, ILogger log)
    {
        _endpoint = endpoint;
        _token = token;
        _bridge = bridge;
        _log = log;
    }

    public static byte[] NewToken() => RandomNumberGenerator.GetBytes(32);

    public int ClientCount => _clients.Count;

    public IEnumerable<IpcClientInfo> Clients => _clients.Values.Select(c => c.Info);

    public event Action<IpcClientInfo>? ClientConnected;

    public event Action<IpcClientInfo>? ClientDisconnected;

    public void Start()
    {
        _listener = _endpoint.Listen();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Sends to every authenticated client (optionally only those with a given role). <paramref name="except"/>
    /// skips one client, so a change a client just asked for is not echoed back at it.
    ///
    /// A client that may not ask for something is also not sent it. Gating the requests alone left the
    /// password reachable by connecting and waiting: it is pushed to every client each time it rotates.
    /// </summary>
    public async Task BroadcastAsync(IpcMessage message, string? role = null, IpcClientInfo? except = null, CancellationToken ct = default)
    {
        bool ownersOnly = IpcAuthorities.For(message.UnionCase) == IpcAuthority.Owner;
        foreach (Client c in _clients.Values)
        {
            if (except is not null && c.Info.Id == except.Id)
            {
                continue;
            }

            if (role is not null && c.Info.Role != role)
            {
                continue;
            }

            if (ownersOnly && !c.Info.Caller.IsOwner)
            {
                continue;
            }

            await c.SendAsync(message, ct).ConfigureAwait(false);
        }
    }

    public bool HasClient(string role) => _clients.Values.Any(c => c.Info.Role == role);

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        // A failure that repeats is said once, with its reason, and then only counted: another engine holding the
        // pipe fails every attempt, five times a second, until it goes -- 409 stack traces in eighty seconds once.
        int failing = 0;
        while (!ct.IsCancellationRequested)
        {
            Stream stream;
            try
            {
                stream = await _listener!.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                if (failing++ == 0)
                {
                    _log.LogWarning(e, "IPC accept failed; retrying quietly until it works");
                }
                else
                {
                    _log.LogDebug("IPC accept failed again ({Count}): {Message}", failing, e.Message);
                }

                await Task.Delay(200, ct).ConfigureAwait(false);
                continue;
            }

            if (failing > 0)
            {
                _log.LogInformation("IPC accepting again after {Count} failed attempts", failing);
                failing = 0;
            }

            _ = Task.Run(() => ServeAsync(stream, ct), ct);
        }
    }

    /// <summary>
    /// A message to one client, for an answer that comes after its request was let go: requests from a client are
    /// handled one at a time, and one that waits for a person would hold up everything the same window asks after it.
    /// </summary>
    public async Task SendToAsync(int clientId, IpcMessage message)
    {
        if (_clients.TryGetValue(clientId, out Client? client))
        {
            try
            {
                await client.SendAsync(message, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
            {
                _log.LogDebug(e, "IPC client {Id} went before its answer", clientId);
            }
        }
    }

    private async Task ServeAsync(Stream stream, CancellationToken ct)
    {
        var framed = new FramedStream(stream, FramedStreamOptions.Peer);
        int id = Interlocked.Increment(ref _nextClientId);
        Client? client = null;
        try
        {
            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            helloTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            IpcMessage? hello = await ReceiveAsync(framed, helloTimeout.Token).ConfigureAwait(false);
            if (hello is null || hello.UnionCase != IpcMessage.UnionOneofCase.Hello ||
                !CryptographicOperations.FixedTimeEquals(hello.Hello.Token.Span, _token))
            {
                _log.LogWarning("IPC client rejected: bad hello");
                await framed.SendAsync(new IpcMessage { RequestId = hello?.RequestId ?? 0, HelloAck = new IpcHelloAck { Accepted = false } }, ct).ConfigureAwait(false);
                return;
            }

            // Ask the operating system who this is, by whichever of the two it gave us. Anything else --
            // an in-process test with a stream it made itself -- has no kernel answer to read, and there
            // is nobody on the machine it could be hiding from either.
            IpcCaller caller = stream switch
            {
                System.IO.Pipes.NamedPipeServerStream pipe when OperatingSystem.IsWindows() => IpcCaller.Of(pipe),
                System.Net.Sockets.NetworkStream socket when !OperatingSystem.IsWindows() => IpcCaller.Of(socket.Socket),
                _ => IpcCaller.Local,
            };
            client = new Client(new IpcClientInfo(id, hello.Hello.Role) { Caller = caller }, framed);
            _clients[id] = client;
            await framed.SendAsync(new IpcMessage { RequestId = hello.RequestId, HelloAck = new IpcHelloAck { Accepted = true, ServerVersion = ProtocolConstants.ProtocolVersion.ToString() } }, ct).ConfigureAwait(false);
            _bridge.ClientConnected(client.Info);
            ClientConnected?.Invoke(client.Info);
            _log.LogInformation(
                "IPC client {Id} connected as {Role}: {User}, {Because}",
                id,
                client.Info.Role,
                caller.Name.Length == 0 ? "unidentified" : caller.Name,
                caller.IsOwner ? caller.Because : "not the owner of this computer");

            while (true)
            {
                IpcMessage? request = await ReceiveAsync(framed, ct).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                IpcMessage? reply;
                try
                {
                    reply = await _bridge.HandleAsync(request, client.Info, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _log.LogError(e, "IPC request {Case} failed", request.UnionCase);
                    continue;
                }

                if (reply is not null)
                {
                    reply.RequestId = request.RequestId;
                    await client.SendAsync(reply, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception e) when (e is IOException or ProtocolException or InvalidProtocolBufferException or OperationCanceledException)
        {
            _log.LogDebug(e, "IPC client {Id} ended", id);
        }
        finally
        {
            if (client is not null && _clients.TryRemove(id, out _))
            {
                _bridge.ClientDisconnected(client.Info);
                ClientDisconnected?.Invoke(client.Info);
                _log.LogInformation("IPC client {Id} disconnected", id);
            }

            await framed.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<IpcMessage?> ReceiveAsync(FramedStream framed, CancellationToken ct)
    {
        using Frame? frame = await framed.ReceiveAsync(ct).ConfigureAwait(false);
        return frame is null ? null : IpcMessage.Parser.ParseFrom(frame.Payload.Span);
    }

    private sealed class Client(IpcClientInfo info, FramedStream stream)
    {
        private readonly SemaphoreSlim _lock = new(1, 1);

        public IpcClientInfo Info { get; } = info;

        public async Task SendAsync(IpcMessage message, CancellationToken ct)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await stream.SendAsync(message, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // The read loop notices the broken pipe and removes the client.
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_listener is not null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _cts.Dispose();
    }
}
