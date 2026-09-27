using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using DeskPair.Protocol;
using DeskPair.Protocol.Framing;
using DeskPair.Protocol.Rendezvous;
using DeskPair.Rendezvous.Core;

namespace DeskPair.Rendezvous.Net;

/// <summary>Accept loop for the framed TCP endpoints (rendezvous port and NAT-test port share this class).</summary>
public sealed class RendezvousTcpListener
{
    private readonly RendezvousHandler _handler;
    private readonly UdpListener _udp;
    private readonly RendezvousOptions _options;
    private readonly ILogger _log;
    private readonly string _name;
    private readonly Func<RendezvousOptions, int> _port;
    private readonly TaskCompletionSource<int> _boundPort = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RendezvousTcpListener(string name, Func<RendezvousOptions, int> port, RendezvousHandler handler, UdpListener udp, IOptions<RendezvousOptions> options, ILogger log)
    {
        _name = name;
        _port = port;
        _handler = handler;
        _udp = udp;
        _options = options.Value;
        _log = log;
    }

    public Task<int> BoundPort => _boundPort.Task;

    public async Task RunAsync(CancellationToken ct)
    {
        using var listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        listener.DualMode = true;
        try
        {
            listener.Bind(new IPEndPoint(IPAddress.IPv6Any, _port(_options)));
            listener.Listen(512);
        }
        catch (SocketException e)
        {
            _boundPort.TrySetException(e);
            throw;
        }

        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        _boundPort.TrySetResult(port);
        _log.LogInformation("Rendezvous {Name} listening on tcp/{Port}", _name, port);

        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException e)
            {
                _log.LogWarning(e, "Accept failed on {Name}", _name);
                continue;
            }

            _ = Task.Run(() => ServeAsync(client, ct), ct);
        }
    }

    private async Task ServeAsync(Socket socket, CancellationToken ct)
    {
        var remote = socket.RemoteEndPoint as IPEndPoint ?? new IPEndPoint(IPAddress.IPv6None, 0);
        if (remote.Address.IsIPv4MappedToIPv6)
        {
            remote = new IPEndPoint(remote.Address.MapToIPv4(), remote.Port);
        }

        socket.NoDelay = true;
        try
        {
            await using var stream = new FramedStream(new NetworkStream(socket, ownsSocket: true), FramedStreamOptions.Control);
            while (!ct.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(_options.TcpIdleTimeout);
                Frame? frame;
                try
                {
                    frame = await stream.ReceiveAsync(idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return; // idle
                }

                if (frame is null)
                {
                    return;
                }

                RendezvousMessage msg;
                using (frame)
                {
                    msg = RendezvousMessage.Parser.ParseFrom(frame.Payload.Span);
                }

                RendezvousMessage? reply = await _handler.HandleTcpAsync(msg, remote, _udp, ct).ConfigureAwait(false);
                if (reply is not null)
                {
                    await stream.SendAsync(reply, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception e) when (e is ProtocolException or InvalidProtocolBufferException or IOException or SocketException)
        {
            _log.LogDebug(e, "TCP session from {Remote} ended with error", remote);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _log.LogError(e, "Unhandled error serving {Remote}", remote);
        }
    }
}

/// <summary>Hosts the rendezvous TCP listener and the NAT-test listener as one background service.</summary>
public sealed class TcpListenersService : BackgroundService
{
    public TcpListenersService(RendezvousHandler handler, UdpListener udp, IOptions<RendezvousOptions> options, ILoggerFactory logs)
    {
        Rendezvous = new RendezvousTcpListener("signaling", o => o.TcpPort, handler, udp, options, logs.CreateLogger<RendezvousTcpListener>());
        NatTest = new RendezvousTcpListener("nat-test", o => o.NatTestPort, handler, udp, options, logs.CreateLogger<RendezvousTcpListener>());
    }

    public RendezvousTcpListener Rendezvous { get; }

    public RendezvousTcpListener NatTest { get; }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Rendezvous.RunAsync(stoppingToken), NatTest.RunAsync(stoppingToken));
}
