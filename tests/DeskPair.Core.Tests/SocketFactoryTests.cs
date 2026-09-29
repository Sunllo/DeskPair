using System.Net;
using System.Net.Sockets;
using DeskPair.Core.Transport.Nat;

namespace DeskPair.Core.Tests;

/// <summary>
/// Covers <see cref="SocketFactory.ConnectReusableAsync"/>, the reusable-local-port connect that prefers a
/// dual-stack socket but falls back to a plain per-family socket when the dual-stack connect is refused
/// (observed on a macOS stack that never emits a SYN for a DualMode connect to an IPv4-mapped address). The
/// fallback branch needs a stack that refuses <c>::ffff:</c> to trigger, so it is covered by review; these
/// tests pin the happy path and the fixed-local-port reuse that NAT detection depends on.
/// </summary>
public sealed class SocketFactoryTests : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(15));

    public SocketFactoryTests() => _listener.Start();

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    [Fact]
    public async Task ConnectReusableAsync_connects_to_a_loopback_listener()
    {
        Task<TcpClient> accept = _listener.AcceptTcpClientAsync();
        using Socket socket = await SocketFactory.ConnectReusableAsync(0, new IPEndPoint(IPAddress.Loopback, Port), _cts.Token);
        using TcpClient _ = await accept;

        socket.Connected.ShouldBeTrue();
        SocketFactory.LocalPort(socket).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ConnectReusableAsync_reuses_a_fixed_local_port()
    {
        // First connect from an ephemeral port, note it, then a second connect pinned to that same local
        // port must succeed — this is the reuse NAT detection relies on across its two probes.
        //
        // To another port, as NAT detection's second probe goes to another port of the server. Back to the same
        // listener it was the same four-tuple as the connection just closed, which the stack may still hold: a CI Mac
        // said "Address already in use" once, and a Mac refused 919 such connects in 1000 made without a pause.
        // To another port, none of 1000.
        CancellationToken ct = _cts.Token;
        using var other = new TcpListener(IPAddress.Loopback, 0);
        other.Start();
        Task<TcpClient> accept1 = _listener.AcceptTcpClientAsync();
        int localPort;
        using (Socket first = await SocketFactory.ConnectReusableAsync(0, new IPEndPoint(IPAddress.Loopback, Port), ct))
        {
            using TcpClient _ = await accept1;
            localPort = SocketFactory.LocalPort(first);
        }

        Task<TcpClient> accept2 = other.AcceptTcpClientAsync();
        using Socket second = await SocketFactory.ConnectReusableAsync(localPort, (IPEndPoint)other.LocalEndpoint, ct);
        using TcpClient __ = await accept2;

        second.Connected.ShouldBeTrue();
        SocketFactory.LocalPort(second).ShouldBe(localPort);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _cts.Dispose();
    }
}
