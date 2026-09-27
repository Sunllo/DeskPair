using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DeskPair.Core.Transport.Udp;

/// <summary>Minimal datagram socket so the media channel can run over a real UDP socket or a test double.</summary>
public interface IDatagramSocket : IDisposable
{
    IPEndPoint LocalEndPoint { get; }

    /// <summary>Synchronous send (called from the pacing thread); errors are swallowed and reported as 0.</summary>
    int SendTo(ReadOnlySpan<byte> datagram, IPEndPoint target);

    /// <summary>Receives one datagram; <paramref name="from"/> is filled with the sender's address.</summary>
    ValueTask<int> ReceiveFromAsync(Memory<byte> buffer, SocketAddress from, CancellationToken ct);
}

/// <summary>Dual-stack UDP socket with large buffers and Windows' connection-reset quirk disabled.</summary>
public sealed class UdpDatagramSocket : IDatagramSocket
{
    private readonly Socket _socket;
    private readonly Random? _lossRandom;
    private readonly double _receiveLoss;

    private UdpDatagramSocket(Socket socket, double receiveLoss, int lossSeed)
    {
        _socket = socket;
        _receiveLoss = receiveLoss;
        _lossRandom = receiveLoss > 0 ? new Random(lossSeed) : null;
        LocalEndPoint = (IPEndPoint)socket.LocalEndPoint!;
    }

    public IPEndPoint LocalEndPoint { get; }

    public long Sent { get; private set; }

    public long Received { get; private set; }

    public long Dropped { get; private set; }

    /// <summary>Binds an ephemeral port on every interface. <paramref name="receiveLoss"/> drops that fraction of incoming datagrams (tests).</summary>
    public static UdpDatagramSocket Create(int port = 0, double receiveLoss = 0, int lossSeed = 1)
    {
        var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
        try
        {
            socket.ReceiveBufferSize = 4 << 20;
            socket.SendBufferSize = 1 << 20;
            DisableConnectionReset(socket);
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            return new UdpDatagramSocket(socket, receiveLoss, lossSeed);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public int SendTo(ReadOnlySpan<byte> datagram, IPEndPoint target)
    {
        try
        {
            int n = _socket.SendTo(datagram, SocketFlags.None, Normalize(target));
            Sent++;
            return n;
        }
        catch (SocketException)
        {
            return 0;
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
    }

    public async ValueTask<int> ReceiveFromAsync(Memory<byte> buffer, SocketAddress from, CancellationToken ct)
    {
        while (true)
        {
            int n = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, from, ct).ConfigureAwait(false);
            if (_lossRandom is not null && _lossRandom.NextDouble() < _receiveLoss)
            {
                Dropped++;
                continue; // simulated loss
            }

            Received++;
            return n;
        }
    }

    /// <summary>Dual-mode sockets need IPv4 targets in mapped form.</summary>
    public static IPEndPoint Normalize(IPEndPoint ep) => ep.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(ep.Address.MapToIPv6(), ep.Port) : ep;

    /// <summary>Converts a received address into an endpoint with IPv4-mapped addresses unwrapped.</summary>
    public static IPEndPoint ToEndPoint(SocketAddress address)
    {
        var ep = (IPEndPoint)new IPEndPoint(IPAddress.IPv6Any, 0).Create(address);
        return ep.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(ep.Address.MapToIPv4(), ep.Port) : ep;
    }

    /// <summary>On Windows an ICMP port-unreachable reply would otherwise fault every later receive on the socket.</summary>
    private static void DisableConnectionReset(Socket socket)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            const int SioUdpConnReset = -1744830452; // SIO_UDP_CONNRESET
            socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }
        catch (Exception)
        {
        }
    }

    public void Dispose() => _socket.Dispose();
}
