using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Time.Testing;
using DeskPair.Protocol.Crypto;
using DeskPair.Protocol.Media;
using DeskPair.Protocol.Rendezvous;
using DeskPair.Relay.Core;
using Google.Protobuf;

namespace DeskPair.Relay.Tests;

/// <summary>
/// Who may use the relay. Before tickets, any two sockets that agreed on a uuid were spliced, which made
/// every relay a free TCP proxy for whoever could reach its port; now the rendezvous server's signature is
/// the admission, on the TCP request and on the UDP bind alike.
/// </summary>
public class RelayAdmissionTests
{
    private static string NewUuid() => Guid.NewGuid().ToString("N");

    /// <summary>True when the relay closed the socket without pairing it: a read returns 0 within a moment.</summary>
    private static async Task<bool> WasClosedAsync(Socket s)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            byte[] one = new byte[1];
            int n = await s.ReceiveAsync(one, SocketFlags.None, cts.Token);
            return n == 0;
        }
        catch (SocketException)
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
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

    [Fact]
    public async Task A_request_without_a_ticket_is_refused()
    {
        await using RelayServerFixture relay = await RelayServerFixture.StartAsync();
        using Socket a = await relay.ConnectAsync(NewUuid(), "A", withTicket: false);

        (await WasClosedAsync(a)).ShouldBeTrue();
        relay.Stats.TicketsRejected.ShouldBe(1);
    }

    [Fact]
    public async Task A_ticket_from_another_key_is_refused()
    {
        await using RelayServerFixture relay = await RelayServerFixture.StartAsync();
        using IdentityKey impostor = IdentityKey.Create();
        string uuid = NewUuid();
        RelayTicket forged = RelayTickets.Issue(impostor, uuid, string.Empty, DateTimeOffset.UtcNow.AddMinutes(2));
        using Socket a = await relay.ConnectAsync(uuid, "A", forged);

        (await WasClosedAsync(a)).ShouldBeTrue();
        relay.Stats.TicketsRejected.ShouldBe(1);
    }

    [Fact]
    public async Task An_expired_ticket_is_refused()
    {
        await using RelayServerFixture relay = await RelayServerFixture.StartAsync();
        string uuid = NewUuid();
        using Socket a = await relay.ConnectAsync(uuid, "A", relay.Ticket(uuid, TimeSpan.FromMinutes(-1)));

        (await WasClosedAsync(a)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_ticket_for_another_pairing_is_refused()
    {
        await using RelayServerFixture relay = await RelayServerFixture.StartAsync();
        using Socket a = await relay.ConnectAsync(NewUuid(), "A", relay.Ticket(NewUuid()));

        (await WasClosedAsync(a)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_good_ticket_pairs_and_a_relay_with_no_key_refuses_everything()
    {
        await using RelayServerFixture relay = await RelayServerFixture.StartAsync();
        string uuid = NewUuid();
        using Socket a = await relay.ConnectAsync(uuid, "A");
        using Socket b = await relay.ConnectAsync(uuid, "B");
        await a.SendAsync("ping"u8.ToArray());
        byte[] got = new byte[4];
        (await b.ReceiveAsync(got)).ShouldBe(4);
        got.ShouldBe("ping"u8.ToArray());

        await using RelayServerFixture keyless = await RelayServerFixture.StartAsync(o => o.RendezvousPublicKey = string.Empty);
        using Socket c = await keyless.ConnectAsync(uuid, "C");
        (await WasClosedAsync(c)).ShouldBeTrue("nothing can verify without a key, so nothing is admitted");
    }

    [Fact]
    public async Task With_tickets_switched_off_the_old_behaviour_returns()
    {
        await using RelayServerFixture relay = await RelayServerFixture.StartAsync(o => o.RequireTicket = false);
        string uuid = NewUuid();
        using Socket a = await relay.ConnectAsync(uuid, "A", withTicket: false);
        using Socket b = await relay.ConnectAsync(uuid, "B", withTicket: false);
        await a.SendAsync("ping"u8.ToArray());
        byte[] got = new byte[4];
        (await b.ReceiveAsync(got)).ShouldBe(4);
    }

    // ---------------------------------------------------------------- UDP

    private static async Task<byte[]?> BindAsync(Socket udp, IPEndPoint relay, byte[] datagram)
    {
        await udp.SendToAsync(datagram, SocketFlags.None, relay);
        byte[] buffer = new byte[64];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            SocketReceiveFromResult r = await udp.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token);
            return buffer[..r.ReceivedBytes];
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static byte[] BindTicketDatagram(RelayTicket ticket)
    {
        byte[] bytes = new byte[RelayDatagram.MaxSize];
        int n = RelayDatagram.WriteBindTicket(bytes, RelayTickets.UdpToken(ticket), ticket.ToByteArray());
        return bytes[..n];
    }

    [Fact]
    public async Task Udp_binds_pair_on_a_ticket_and_a_plain_bind_is_rejected()
    {
        await using RelayServerFixture relay = await RelayServerFixture.StartAsync();
        var target = new IPEndPoint(IPAddress.Loopback, relay.UdpPort);
        RelayTicket ticket = relay.Ticket(NewUuid());
        byte[] token = RelayTickets.UdpToken(ticket);

        using var a = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var b = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        a.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        b.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        // The first bind is pending: no answer until a partner shows up.
        (await BindAsync(a, target, BindTicketDatagram(ticket))).ShouldBeNull();
        byte[]? ack = await BindAsync(b, target, BindTicketDatagram(ticket));
        ack.ShouldNotBeNull();
        RelayDatagram.TryRead(ack, out RelayDatagramType type, out ReadOnlySpan<byte> ackToken).ShouldBeTrue();
        type.ShouldBe(RelayDatagramType.BindAck);
        ackToken.ToArray().ShouldBe(token);

        // Protocol 1's bind, with the right token but no ticket: refused, and told so.
        using var c = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        c.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] plain = new byte[RelayDatagram.Size];
        RelayDatagram.Write(plain, RelayDatagramType.Bind, token);
        byte[]? reject = await BindAsync(c, target, plain);
        reject.ShouldNotBeNull();
        RelayDatagram.TryRead(reject, out type, out _).ShouldBeTrue();
        type.ShouldBe(RelayDatagramType.Reject);
    }

    [Fact]
    public async Task A_udp_bind_whose_token_is_not_the_tickets_is_rejected()
    {
        await using RelayServerFixture relay = await RelayServerFixture.StartAsync();
        var target = new IPEndPoint(IPAddress.Loopback, relay.UdpPort);
        RelayTicket ticket = relay.Ticket(NewUuid());

        // A real ticket presented for somebody else's token: the ticket is fine, the pairing is not its.
        byte[] datagram = new byte[RelayDatagram.MaxSize];
        int n = RelayDatagram.WriteBindTicket(datagram, new byte[RelayDatagram.TokenBytes], ticket.ToByteArray());
        using var a = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        a.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        byte[]? reply = await BindAsync(a, target, datagram[..n]);
        reply.ShouldNotBeNull();
        RelayDatagram.TryRead(reply, out RelayDatagramType type, out _).ShouldBeTrue();
        type.ShouldBe(RelayDatagramType.Reject);
    }

    // ---------------------------------------------------------------- per-address limits

    [Fact]
    public async Task The_pending_limit_per_address_closes_the_extra_socket()
    {
        await using RelayServerFixture relay = await RelayServerFixture.StartAsync(o => o.MaxPendingPerIp = 1);
        using Socket first = await relay.ConnectAsync(NewUuid(), "A");

        // Each socket is handled on its own thread, so "first" is only first once the relay has counted it.
        await WaitUntilAsync(() => relay.Stats.PendingPeers == 1);
        using Socket second = await relay.ConnectAsync(NewUuid(), "B");

        (await WasClosedAsync(second)).ShouldBeTrue("one pending pairing per address");
        relay.Stats.RejectedLimit.ShouldBe(1);
        relay.Stats.PendingPeers.ShouldBe(1);
    }

    [Fact]
    public async Task A_burst_from_one_address_cannot_slip_under_the_pending_limit_together()
    {
        await using RelayServerFixture relay = await RelayServerFixture.StartAsync(o => o.MaxPendingPerIp = 1);
        Socket[] burst = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => relay.ConnectAsync(NewUuid(), $"P{i}")));
        try
        {
            // Every socket ends up either waiting or refused; which one waits is up to the scheduler.
            await WaitUntilAsync(() => relay.Stats.PendingPeers + relay.Stats.RejectedLimit == burst.Length);

            relay.Stats.PendingPeers.ShouldBe(1, "however they arrive, one address holds one pending pairing");
            relay.Stats.RejectedLimit.ShouldBe(burst.Length - 1);
        }
        finally
        {
            foreach (Socket s in burst)
            {
                s.Dispose();
            }
        }
    }

    [Fact]
    public async Task The_session_limit_per_address_refuses_a_second_session()
    {
        await using RelayServerFixture relay = await RelayServerFixture.StartAsync(o => o.MaxSessionsPerIp = 1);
        string first = NewUuid();
        using Socket a = await relay.ConnectAsync(first, "A");
        using Socket b = await relay.ConnectAsync(first, "B");
        await a.SendAsync("x"u8.ToArray());
        (await b.ReceiveAsync(new byte[1])).ShouldBe(1);

        using Socket c = await relay.ConnectAsync(NewUuid(), "C");
        (await WasClosedAsync(c)).ShouldBeTrue("the address already holds a session");
        relay.Stats.RejectedLimit.ShouldBe(1);
    }

    // ---------------------------------------------------------------- bandwidth ceiling

    [Fact]
    public async Task The_throttle_lets_a_burst_through_and_then_meters_the_rest()
    {
        var time = new FakeTimeProvider();
        var throttle = new RelayThrottle(maxBitrateKbps: 800, time); // 100 KB/s, capacity 64 KB

        // Within the initial credit: immediate.
        await throttle.WaitAsync(60_000, CancellationToken.None);

        // Beyond it: waits for the bucket to refill at the metered rate.
        ValueTask wait = throttle.WaitAsync(50_000, CancellationToken.None);
        await Task.Yield();
        wait.IsCompleted.ShouldBeFalse();
        time.Advance(TimeSpan.FromMilliseconds(470)); // 47 KB of credit, plus the 4 KB left over
        await wait.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task An_unmetered_throttle_never_waits()
    {
        var throttle = new RelayThrottle(0, TimeProvider.System);
        await throttle.WaitAsync(100_000_000, CancellationToken.None);
        throttle.IsUnmetered.ShouldBeTrue();
    }

    [Fact]
    public async Task A_metered_relay_delivers_at_no_more_than_its_ceiling()
    {
        // 4 Mb/s = 500 KB/s; a megabyte takes about two seconds instead of milliseconds on loopback.
        await using RelayServerFixture relay = await RelayServerFixture.StartAsync(o => o.MaxBitrateKbps = 4_000);
        string uuid = NewUuid();
        using Socket a = await relay.ConnectAsync(uuid, "A");
        using Socket b = await relay.ConnectAsync(uuid, "B");
        byte[] payload = new byte[1_000_000];
        long started = Environment.TickCount64;
        Task<int> send = a.SendAsync(payload, SocketFlags.None);
        int got = 0;
        byte[] buffer = new byte[64 * 1024];
        while (got < payload.Length)
        {
            got += await b.ReceiveAsync(buffer, SocketFlags.None);
        }

        await send;
        (Environment.TickCount64 - started).ShouldBeGreaterThan(1_500, "one megabyte at 500 KB/s is two seconds");
    }
}
