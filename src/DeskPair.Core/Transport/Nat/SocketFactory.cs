using System.Net;
using System.Net.Sockets;

namespace DeskPair.Core.Transport.Nat;

/// <summary>
/// Creates TCP sockets that can share one local port: hole punching needs a listener and several
/// outbound connections bound to the same port so the NAT mapping the rendezvous server observed is reused.
/// </summary>
public static class SocketFactory
{
    private const int LinuxSolSocket = 1;
    private const int LinuxSoReusePort = 15;
    private const int BsdSolSocket = 0xFFFF;
    private const int BsdSoReusePort = 0x0200;

    /// <summary>Dual-mode TCP socket bound to <paramref name="port"/> (0 = ephemeral) with address/port reuse enabled.</summary>
    public static Socket BindReusable(int port)
    {
        var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true, NoDelay = true };
        try
        {
            EnableReuse(socket);
            socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static void EnableReuse(Socket socket)
    {
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        (int level, int name) = OperatingSystem.IsLinux() ? (LinuxSolSocket, LinuxSoReusePort) : (BsdSolSocket, BsdSoReusePort);
        try
        {
            socket.SetRawSocketOption(level, name, BitConverter.GetBytes(1));
        }
        catch (SocketException)
        {
            // Kernel without SO_REUSEPORT: SO_REUSEADDR alone still covers TIME_WAIT reuse.
        }
    }

    public static int LocalPort(Socket socket) => ((IPEndPoint)socket.LocalEndPoint!).Port;

    /// <summary>Connects with a deadline; a failed socket is never reused (Windows cannot connect it again).</summary>
    public static async Task<Socket> ConnectFromPortAsync(int localPort, IPEndPoint target, TimeSpan timeout, CancellationToken ct)
    {
        Socket socket = BindReusable(localPort);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await socket.ConnectAsync(Normalize(target), cts.Token).ConfigureAwait(false);
            return socket;
        }
        catch (Exception e) when (e is OperationCanceledException && !ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TimeoutException($"Connecting to {target} timed out.", e);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Dual-mode sockets want IPv6 endpoints; map IPv4 targets.</summary>
    public static IPEndPoint Normalize(IPEndPoint ep) => ep.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(ep.Address.MapToIPv6(), ep.Port) : ep;

    /// <summary>
    /// Connects a reusable-local-port socket to <paramref name="target"/>, preferring the dual-stack socket
    /// (so hole-punch mapping reuse is unchanged on healthy stacks) but retrying with a plain per-family
    /// socket when the dual-stack connect is refused. Some stacks — observed on a macOS host with many utun
    /// interfaces — never emit a SYN for a <see cref="Socket.DualMode"/> connect to an IPv4-mapped
    /// <c>::ffff:</c> address yet accept a plain <see cref="AddressFamily.InterNetwork"/> socket to the same
    /// endpoint. The caller reads <see cref="LocalPort"/> from the returned socket (it reflects whichever
    /// attempt connected); passing a fixed <paramref name="localPort"/> keeps the port stable across the retry.
    /// </summary>
    public static async Task<Socket> ConnectReusableAsync(int localPort, IPEndPoint target, CancellationToken ct)
    {
        Socket dual = BindReusable(localPort);
        try
        {
            await dual.ConnectAsync(Normalize(target), ct).ConfigureAwait(false);
            return dual;
        }
        catch (SocketException) when (!ct.IsCancellationRequested)
        {
            dual.Dispose(); // fall through to a plain per-family retry
        }

        Socket plain = BindReusablePlain(localPort, target.AddressFamily);
        try
        {
            await plain.ConnectAsync(target, ct).ConfigureAwait(false); // native endpoint, no ::ffff: mapping
            return plain;
        }
        catch
        {
            plain.Dispose();
            throw;
        }
    }

    /// <summary>A plain per-family (not dual-mode) TCP socket with the same reuse options as <see cref="BindReusable"/>.</summary>
    private static Socket BindReusablePlain(int port, AddressFamily family)
    {
        var socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            EnableReuse(socket);
            IPAddress any = family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
            socket.Bind(new IPEndPoint(any, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
