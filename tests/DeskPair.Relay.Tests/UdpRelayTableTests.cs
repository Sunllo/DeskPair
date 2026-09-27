using System.Net;
using Microsoft.Extensions.Time.Testing;
using DeskPair.Relay.Core;

namespace DeskPair.Relay.Tests;

public class UdpRelayTableTests
{
    private static readonly byte[] TokenA = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] TokenB = Enumerable.Range(100, 16).Select(i => (byte)i).ToArray();
    private static readonly IPEndPoint A = new(IPAddress.Parse("10.0.0.1"), 5000);
    private static readonly IPEndPoint B = new(IPAddress.Parse("10.0.0.2"), 6000);
    private static readonly IPEndPoint C = new(IPAddress.Parse("10.0.0.3"), 7000);

    private static (UdpRelayTable Table, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        return (new UdpRelayTable(time, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), maxPairs: 10), time);
    }

    [Fact]
    public void Two_endpoints_with_the_same_token_pair_and_forward_both_ways()
    {
        (UdpRelayTable table, _) = Create();
        table.Bind(TokenA, A, out IPEndPoint? peer).ShouldBe(UdpBindResult.Pending);
        peer.ShouldBeNull();
        table.PendingCount.ShouldBe(1);

        table.Bind(TokenA, B, out peer).ShouldBe(UdpBindResult.Paired);
        peer.ShouldBe(A);
        table.PairCount.ShouldBe(1);
        table.PeerOf(A).ShouldBe(B);
        table.PeerOf(B).ShouldBe(A);
        table.PeerOf(C).ShouldBeNull();

        // A repeated bind from a paired endpoint (lost ack) is answered again, not re-paired.
        table.Bind(TokenA, A, out peer).ShouldBe(UdpBindResult.Paired);
        peer.ShouldBe(B);
    }

    [Fact]
    public void Different_tokens_never_pair_and_a_third_endpoint_is_rejected()
    {
        (UdpRelayTable table, _) = Create();
        table.Bind(TokenA, A, out _).ShouldBe(UdpBindResult.Pending);
        table.Bind(TokenB, B, out _).ShouldBe(UdpBindResult.Pending);
        table.PairCount.ShouldBe(0);

        table.Bind(TokenA, C, out IPEndPoint? peer).ShouldBe(UdpBindResult.Paired);
        peer.ShouldBe(A);
        table.Bind(TokenA, B, out _).ShouldBe(UdpBindResult.Rejected); // TokenA already paired A with C
    }

    [Fact]
    public void Unpaired_binds_expire_and_idle_pairs_are_swept()
    {
        (UdpRelayTable table, FakeTimeProvider time) = Create();
        table.Bind(TokenA, A, out _);
        time.Advance(TimeSpan.FromSeconds(31));
        table.Sweep().ShouldBe(1);
        table.PendingCount.ShouldBe(0);

        table.Bind(TokenB, B, out _);
        table.Bind(TokenB, C, out _).ShouldBe(UdpBindResult.Paired);
        time.Advance(TimeSpan.FromSeconds(30));
        table.PeerOf(B).ShouldBe(C); // traffic keeps the pair alive
        time.Advance(TimeSpan.FromSeconds(61));
        table.Sweep().ShouldBe(2);
        table.PairCount.ShouldBe(0);
    }
}
