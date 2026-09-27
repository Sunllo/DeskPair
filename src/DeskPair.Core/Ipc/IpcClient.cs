using System.Collections.Concurrent;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using DeskPair.Protocol;
using DeskPair.Protocol.Framing;
using DeskPair.Protocol.Ipc;

namespace DeskPair.Core.Ipc;

/// <summary>UI-side connection to the host service: request/reply plus server pushes.</summary>
public sealed class IpcClient : IAsyncDisposable
{
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<IpcMessage>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private FramedStream? _stream;
    private Task? _readLoop;
    private uint _nextRequestId;

    public IpcClient(ILogger log)
    {
        _log = log;
    }

    /// <summary>Server-initiated messages (approval requests, connection events, chat...).</summary>
    public event Action<IpcMessage>? Pushed;

    public event Action<string>? Disconnected;

    public bool IsConnected => _stream is not null && !_cts.IsCancellationRequested;

    public static async Task<IpcClient> ConnectAsync(IpcEndpoint endpoint, byte[] token, string role, ILogger log, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        Stream raw = await endpoint.ConnectAsync(timeout ?? TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        return await ConnectAsync(raw, token, role, log, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Speaks to whatever is at the other end of <paramref name="raw"/>, which this then owns. For a tool that has to
    /// reach one engine in particular: <see cref="IpcEndpoint.ConnectPaths"/> always tries the machine-wide socket
    /// first, and on a machine with the daemon installed that is the daemon's engine.
    /// </summary>
    public static async Task<IpcClient> ConnectAsync(Stream raw, byte[] token, string role, ILogger log, CancellationToken ct = default)
    {
        var client = new IpcClient(log);
        client._stream = new FramedStream(raw, FramedStreamOptions.Peer);
        client._readLoop = Task.Run(() => client.ReadLoopAsync(client._cts.Token));
        IpcMessage ack = await client.RequestAsync(new IpcMessage { Hello = new IpcHello { Token = ByteString.CopyFrom(token), Role = role, Version = ProtocolConstants.ProtocolVersion.ToString() } }, ct).ConfigureAwait(false);
        if (ack.UnionCase != IpcMessage.UnionOneofCase.HelloAck || !ack.HelloAck.Accepted)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new UnauthorizedAccessException("The host service rejected this process.");
        }

        return client;
    }

    public async Task<IpcMessage> RequestAsync(IpcMessage request, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        uint id = Interlocked.Increment(ref _nextRequestId);
        request.RequestId = id;
        var tcs = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(request, ct).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            linked.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
            using CancellationTokenRegistration reg = linked.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Fire-and-forget message to the service.</summary>
    public async Task SendAsync(IpcMessage message, CancellationToken ct = default)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("Not connected.");
        }

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.SendAsync(message, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        string reason = "closed";
        try
        {
            while (true)
            {
                using Frame? frame = await _stream!.ReceiveAsync(ct).ConfigureAwait(false);
                if (frame is null)
                {
                    reason = "service closed the connection";
                    return;
                }

                var message = IpcMessage.Parser.ParseFrom(frame.Payload.Span);
                if (message.RequestId != 0 && _pending.TryRemove(message.RequestId, out TaskCompletionSource<IpcMessage>? tcs))
                {
                    tcs.TrySetResult(message);
                }
                else
                {
                    try
                    {
                        Pushed?.Invoke(message);
                    }
                    catch (Exception e)
                    {
                        _log.LogError(e, "IPC push handler failed for {Case}", message.UnionCase);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            reason = "disposed";
        }
        catch (Exception e) when (e is IOException or ProtocolException or InvalidProtocolBufferException or ObjectDisposedException)
        {
            reason = e.Message;
        }
        finally
        {
            foreach (TaskCompletionSource<IpcMessage> p in _pending.Values)
            {
                p.TrySetCanceled();
            }

            Disconnected?.Invoke(reason);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _cts.Dispose();
        _sendLock.Dispose();
    }
}
