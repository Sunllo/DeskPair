using System.Net.Sockets;
using System.Security.Cryptography;

namespace DeskPair.Relay.Tests;

public class RelayPairingTests
{
    private static string NewUuid() => Guid.NewGuid().ToString("N");

    private static async Task<byte[]> ReadExactlyAsync(Socket s, int length, CancellationToken ct)
    {
        byte[] buf = new byte[length];
        int got = 0;
        while (got < length)
        {
            int n = await s.ReceiveAsync(buf.AsMemory(got), SocketFlags.None, ct);
            if (n == 0)
            {
                throw new IOException("closed early");
            }

            got += n;
        }

        return buf;
    }

    [Fact]
    public async Task Two_peers_with_same_uuid_are_spliced_in_both_directions()
    {
        await using RelayServerFixture relay = await RelayServerFixture.StartAsync();
        string uuid = NewUuid();
        using Socket a = await relay.ConnectAsync(uuid, "A");
        using Socket b = await relay.ConnectAsync(uuid, "B");

        byte[] aToB = RandomNumberGenerator.GetBytes(10 * 1024 * 1024);
        byte[] bToA = RandomNumberGenerator.GetBytes(10 * 1024 * 1024);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        Task sendA = a.SendAsync(aToB, SocketFlags.None, cts.Token).AsTask();
        Task sendB = b.SendAsync(bToA, SocketFlags.None, cts.Token).AsTask();
        Task<byte[]> recvB = ReadExactlyAsync(b, aToB.Length, cts.Token);
        Task<byte[]> recvA = ReadExactlyAsync(a, bToA.Length, cts.Token);
        await Task.WhenAll(sendA, sendB, recvB, recvA);

        SHA256.HashData(await recvB).ShouldBe(SHA256.HashData(aToB));
        SHA256.HashData(await recvA).ShouldBe(SHA256.HashData(bToA));
        relay.Stats.ActiveSessions.ShouldBe(1);
    }

    [Fact]
    public async Task Closing_one_side_closes_the_other()
    {
        await using RelayServerFixture relay = await RelayServerFixture.StartAsync();
        string uuid = NewUuid();
        using Socket a = await relay.ConnectAsync(uuid, "A");
        using Socket b = await relay.ConnectAsync(uuid, "B");
        await a.SendAsync("ping"u8.ToArray());
        (await ReadExactlyAsync(b, 4, CancellationToken.None)).ShouldBe("ping"u8.ToArray());

        a.Shutdown(SocketShutdown.Both);
        a.Close();

        byte[] buf = new byte[16];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int n = await b.ReceiveAsync(buf, SocketFlags.None, cts.Token);
        n.ShouldBe(0);
        await WaitUntilAsync(() => relay.Stats.ActiveSessions == 0);
        relay.Stats.TotalBytes.ShouldBe(4);
    }

    [Fact]
    public async Task Unpaired_peer_is_dropped_after_pairing_timeout()
    {
        await using RelayServerFixture relay = await RelayServerFixture.StartAsync(o => o.PairingTimeout = TimeSpan.FromMilliseconds(300));
        using Socket a = await relay.ConnectAsync(NewUuid(), "A");

        byte[] buf = new byte[1];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int n = await a.ReceiveAsync(buf, SocketFlags.None, cts.Token);
        n.ShouldBe(0);
        await WaitUntilAsync(() => relay.Stats.PairingTimeouts == 1);
        relay.Stats.PendingPeers.ShouldBe(0);
    }

    [Fact]
    public async Task Third_peer_with_same_uuid_waits_as_a_new_pending_peer()
    {
        await using RelayServerFixture relay = await RelayServerFixture.StartAsync(o => o.PairingTimeout = TimeSpan.FromSeconds(1));
        string uuid = NewUuid();
        using Socket a = await relay.ConnectAsync(uuid, "A");
        using Socket b = await relay.ConnectAsync(uuid, "B");
        await WaitUntilAsync(() => relay.Stats.ActiveSessions == 1);
        await a.SendAsync("x"u8.ToArray());
        (await ReadExactlyAsync(b, 1, CancellationToken.None)).ShouldBe("x"u8.ToArray());

        // A third connection on the same uuid does not disturb the established pair; it becomes a new pending peer.
        using Socket c = await relay.ConnectAsync(uuid, "C");
        await WaitUntilAsync(() => relay.Stats.PendingPeers == 1);
        relay.Stats.ActiveSessions.ShouldBe(1);

        // It is dropped once its pairing window elapses, leaving the original pair intact.
        byte[] buf = new byte[1];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        (await c.ReceiveAsync(buf, SocketFlags.None, cts.Token)).ShouldBe(0);
        relay.Stats.ActiveSessions.ShouldBe(1);
    }

    [Fact]
    public async Task Garbage_first_frame_is_rejected()
    {
        await using RelayServerFixture relay = await RelayServerFixture.StartAsync();
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await s.ConnectAsync(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, relay.Port));
        await s.SendAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F });
        byte[] buf = new byte[1];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        (await s.ReceiveAsync(buf, SocketFlags.None, cts.Token)).ShouldBe(0);
        await WaitUntilAsync(() => relay.Stats.ProtocolErrors == 1);
    }

    [Fact]
    public async Task Idle_session_is_closed()
    {
        await using RelayServerFixture relay = await RelayServerFixture.StartAsync(o => o.IdleTimeout = TimeSpan.FromMilliseconds(200));
        string uuid = NewUuid();
        using Socket a = await relay.ConnectAsync(uuid, "A");
        using Socket b = await relay.ConnectAsync(uuid, "B");

        byte[] buf = new byte[1];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        (await a.ReceiveAsync(buf, SocketFlags.None, cts.Token)).ShouldBe(0);
        await WaitUntilAsync(() => relay.Stats.ActiveSessions == 0);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met.");
            }

            await Task.Delay(20);
        }
    }
}
