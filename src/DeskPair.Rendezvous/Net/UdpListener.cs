using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using DeskPair.Protocol;
using DeskPair.Protocol.Rendezvous;
using DeskPair.Rendezvous.Core;

namespace DeskPair.Rendezvous.Net;

/// <summary>One dual-stack UDP socket: a single receive loop dispatching to the handler and a single send loop fed by a channel.</summary>
public sealed class UdpListener : BackgroundService, IUdpSender
{
    private readonly RendezvousHandler _handler;
    private readonly RendezvousOptions _options;
    private readonly ILogger<UdpListener> _log;
    private readonly Channel<(byte[] Data, IPEndPoint Target)> _outbound = Channel.CreateBounded<(byte[], IPEndPoint)>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly TaskCompletionSource<int> _boundPort = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public UdpListener(RendezvousHandler handler, IOptions<RendezvousOptions> options, ILogger<UdpListener> log)
    {
        _handler = handler;
        _options = options.Value;
        _log = log;
    }

    public Task<int> BoundPort => _boundPort.Task;

    public ValueTask SendAsync(RendezvousMessage message, IPEndPoint target, CancellationToken ct)
    {
        byte[] bytes = message.ToByteArray();
        if (bytes.Length > ProtocolConstants.MaxUdpDatagramBytes)
        {
            _log.LogError("Refusing to send {Bytes}-byte datagram ({Case}) to {Target}", bytes.Length, message.UnionCase, target);
            return ValueTask.CompletedTask;
        }

        _outbound.Writer.TryWrite((bytes, target));
        return ValueTask.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        socket.DualMode = true;
        DisableConnectionReset(socket);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, _options.UdpPort));
        }
        catch (SocketException e)
        {
            _boundPort.TrySetException(e);
            throw;
        }

        int port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        _boundPort.TrySetResult(port);
        _log.LogInformation("Rendezvous listening on udp/{Port}", port);

        Task sender = SendLoopAsync(socket, stoppingToken);
        await ReceiveLoopAsync(socket, stoppingToken).ConfigureAwait(false);
        _outbound.Writer.TryComplete();
        await sender.ConfigureAwait(false);
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

            var endpoint = (IPEndPoint)new IPEndPoint(IPAddress.IPv6Any, 0).Create(from);
            RendezvousMessage msg;
            try
            {
                msg = RendezvousMessage.Parser.ParseFrom(buffer.AsSpan(0, n));
            }
            catch (InvalidProtocolBufferException)
            {
                _log.LogDebug("Malformed datagram from {From}", endpoint);
                continue;
            }

            try
            {
                RendezvousMessage? reply = _handler.HandleUdp(msg, endpoint);
                if (reply is not null)
                {
                    await SendAsync(reply, endpoint, ct).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                _log.LogError(e, "Handling UDP {Case} from {From} failed", msg.UnionCase, endpoint);
            }
        }
    }

    private async Task SendLoopAsync(Socket socket, CancellationToken ct)
    {
        try
        {
            await foreach ((byte[] data, IPEndPoint target) in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    IPEndPoint v6 = target.AddressFamily == AddressFamily.InterNetworkV6 ? target : new IPEndPoint(target.Address.MapToIPv6(), target.Port);
                    await socket.SendToAsync(data, SocketFlags.None, v6, ct).ConfigureAwait(false);
                }
                catch (SocketException e)
                {
                    _log.LogDebug(e, "UDP send to {Target} failed", target);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Without this, an ICMP port-unreachable for any past send surfaces as ConnectionReset on the next receive (Windows only).
    private static void DisableConnectionReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
        try
        {
            socket.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        }
        catch (SocketException)
        {
        }
    }
}
