using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using DeskPair.Core.Session;
using DeskPair.Protocol;
using DeskPair.Protocol.Framing;
using DeskPair.Protocol.Messages;
using DeskPair.Protocol.Tests;

namespace DeskPair.Core.Tests;

/// <summary>
/// A pulled cable looks exactly like a quiet peer until the read timeout runs out, which is half a minute.
/// Long before that the picture on screen has stopped being true, and the window has to say so. These pin
/// when it says so and when it stops.
/// </summary>
public class SessionStallTests
{
    private static Message Chat(string text) => new() { Misc = new Misc { Chat = new ChatMessage { Text = text } } };

    private sealed record Harness(SessionMessagePump Pump, FramedStream Far, FakeTimeProvider Time, List<bool> Changes) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Pump.DisposeAsync();
            await Far.DisposeAsync();
        }
    }

    private static Harness Start(TimeSpan? stalledAfter = null)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        (Stream a, Stream b) = TestStreams.DuplexPair();
        var near = new FramedStream(a, FramedStreamOptions.Peer, time);
        var far = new FramedStream(b, FramedStreamOptions.Peer, time);
        var pump = new SessionMessagePump(near, NullLogger.Instance, time)
        {
            StalledAfter = stalledAfter ?? ProtocolConstants.SessionStalledAfter,
        };

        var changes = new List<bool>();
        pump.Stalled += stalled =>
        {
            lock (changes)
            {
                changes.Add(stalled);
            }
        };

        pump.Start();
        return new Harness(pump, far, time, changes);
    }

    /// <summary>The keepalive loop ticks once a second, so the clock is advanced a second at a time.</summary>
    private static async Task AdvanceAsync(Harness h, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20);
        }
    }

    private static bool[] Changes(Harness h)
    {
        lock (h.Changes)
        {
            return [.. h.Changes];
        }
    }

    [Fact]
    public async Task Silence_is_reported_once_it_has_gone_on_long_enough()
    {
        await using Harness h = Start(TimeSpan.FromSeconds(6));

        await AdvanceAsync(h, 5);
        Changes(h).ShouldBeEmpty("five seconds of quiet is an ordinary still screen");

        await AdvanceAsync(h, 2);
        Changes(h).ShouldBe([true]);
    }

    /// <summary>
    /// The peer coming back must clear it, and clear it once: a window that keeps announcing a connection it
    /// already has is worse than one that never mentioned it.
    /// </summary>
    [Fact]
    public async Task The_peer_answering_again_clears_it()
    {
        await using Harness h = Start(TimeSpan.FromSeconds(6));
        await AdvanceAsync(h, 7);
        Changes(h).ShouldBe([true]);

        await h.Far.SendAsync(Chat("still here"));

        // Wait for the pump to have actually read it: the clock is fake and would otherwise tick past the
        // arrival, which is a race in the test rather than anything the product does.
        await h.Pump.Incoming.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await AdvanceAsync(h, 1);

        Changes(h).ShouldBe([true, false]);
        await AdvanceAsync(h, 2);
        Changes(h).ShouldBe([true, false], "nothing has changed since, so nothing is said");
    }

    /// <summary>
    /// A heartbeat is not a message and never reaches the session, but it is proof the peer is reachable,
    /// which is the only question being asked here.
    /// </summary>
    [Fact]
    public async Task A_heartbeat_counts_as_the_peer_being_there()
    {
        await using Harness h = Start(TimeSpan.FromSeconds(6));

        for (int i = 0; i < 4; i++)
        {
            await AdvanceAsync(h, 4);
            await h.Far.SendHeartbeatAsync();
            await Task.Delay(30);
        }

        Changes(h).ShouldBeEmpty("a session with nothing to say is not a session in trouble");
    }

    [Fact]
    public async Task A_quiet_session_that_never_recovers_says_so_only_once()
    {
        await using Harness h = Start(TimeSpan.FromSeconds(6));

        await AdvanceAsync(h, 20);

        Changes(h).ShouldBe([true]);
    }

    /// <summary>
    /// The warning has to arrive with most of the read timeout still to run, or it is not a warning, it is
    /// an epitaph.
    /// </summary>
    [Fact]
    public void The_warning_comes_well_before_the_session_is_given_up_on()
    {
        ProtocolConstants.SessionStalledAfter.ShouldBeLessThan(ProtocolConstants.SessionReadTimeout / 4);
        ProtocolConstants.SessionStalledAfter.ShouldBeGreaterThan(ProtocolConstants.SessionHeartbeatInterval);
    }
}
