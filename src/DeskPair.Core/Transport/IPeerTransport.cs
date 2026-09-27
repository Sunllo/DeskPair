using System.Net;
using System.Net.Sockets;

namespace DeskPair.Core.Transport;

public enum TransportKind
{
    DirectTcp,
    Lan,
    PunchedTcp,
    Relay,
}

/// <summary>A connected duplex byte stream to the remote peer; disposing closes the underlying connection.</summary>
public interface IPeerTransport : IAsyncDisposable
{
    TransportKind Kind { get; }

    EndPoint? RemoteEndPoint { get; }

    Stream Stream { get; }
}

public sealed class TcpPeerTransport : IPeerTransport
{
    private readonly Socket _socket;

    public TcpPeerTransport(Socket socket, TransportKind kind)
    {
        _socket = socket;
        _socket.NoDelay = true;
        // A 64 KB default window caps a 100 ms path at ~5 Mbps; video needs room for a bandwidth-delay product.
        _socket.SendBufferSize = 1 << 20;
        _socket.ReceiveBufferSize = 1 << 20;
        Kind = kind;
        Stream = new NetworkStream(socket, ownsSocket: true);
    }

    public TransportKind Kind { get; }

    public EndPoint? RemoteEndPoint
    {
        get
        {
            try
            {
                return _socket.RemoteEndPoint;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }
    }

    public Stream Stream { get; }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
        }

        await Stream.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Opens outbound TCP connections; v1 is plain TCP, later an SslStream variant for server legs.</summary>
public static class TcpConnector
{
    public static async Task<Socket> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        Exception? last = null;
        foreach (IPAddress address in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6 ? 1 : 0))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), cts.Token).ConfigureAwait(false);
                return socket;
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                last = e;
                if (ct.IsCancellationRequested)
                {
                    throw;
                }
            }
        }

        throw last is OperationCanceledException
            ? new TimeoutException($"Connecting to {host}:{port} timed out.", last)
            : last!;
    }

    /// <summary>Splits "host:port" (or "[v6]:port"); falls back to <paramref name="defaultPort"/>.</summary>
    public static (string Host, int Port) ParseHostPort(string value, int defaultPort)
    {
        value = value.Trim();
        if (value.StartsWith('['))
        {
            int close = value.IndexOf(']');
            if (close > 0)
            {
                string host6 = value[1..close];
                string rest = value[(close + 1)..];
                return (host6, rest.StartsWith(':') && int.TryParse(rest[1..], out int p6) ? p6 : defaultPort);
            }
        }

        int colon = value.LastIndexOf(':');
        if (colon > 0 && value.IndexOf(':') == colon && int.TryParse(value[(colon + 1)..], out int port))
        {
            return (value[..colon], port);
        }

        return (value, defaultPort);
    }
}
