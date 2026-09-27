using DeskPair.Desktop.Localization;
using DeskPair.Desktop.ViewModels;
using DeskPair.Protocol.Ipc;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// What one line of the record says. The point of testing the formatting is that this page is read when
/// somebody is worried about who has been on their machine, and a row that is merely decorative is worse
/// than no page at all.
/// </summary>
[Collection("ProcessState")] // reads the string table, which the language tests move
public class ConnectionHistoryRowTests : IDisposable
{
    private readonly string _was = Strings.Language;

    public ConnectionHistoryRowTests() => Strings.Language = AppLanguages.En;

    public void Dispose() => Strings.Language = _was;

    /// <summary>A fixed zone, so the test does not depend on where it runs.</summary>
    private static DateTimeOffset Utc(DateTimeOffset t) => t.ToUniversalTime();

    private static ConnectionHistoryEntry Entry(long startedMs = 1_790_000_000_000, long endedMs = 0)
    {
        var entry = new ConnectionHistoryEntry
        {
            Id = "abc",
            StartedUtcMs = startedMs,
            EndedUtcMs = endedMs,
            PeerId = "123456789",
            PeerName = "Office PC",
            Address = "192.168.1.7",
            Transport = "DirectTcp",
            Kind = "remote",
            Authenticated = "permanent",
        };
        entry.Granted.AddRange(["PermKeyboard", "PermFile"]);
        return entry;
    }

    [Fact]
    public void A_finished_connection_says_who_how_long_from_where_and_how_it_got_in()
    {
        ConnectionHistoryEntry entry = Entry(endedMs: 1_790_000_000_000 + (long)TimeSpan.FromMinutes(12).TotalMilliseconds);

        ConnectionHistoryRow row = ConnectionHistoryRow.From(entry, Utc);

        row.Who.ShouldBe("Office PC");
        row.Detail.ShouldContain("12 min");
        row.Detail.ShouldContain("192.168.1.7");
        row.Detail.ShouldContain("permanent password");
        row.Allowed.ShouldBe("Allowed: Keyboard and mouse, File transfer");
        row.Status.ShouldBeEmpty();
        row.IsOpen.ShouldBeFalse();
    }

    /// <summary>A connection with no end is either still running or was cut off; the row must not pretend to know which.</summary>
    [Fact]
    public void A_connection_with_no_end_says_so_rather_than_showing_a_duration()
    {
        ConnectionHistoryRow row = ConnectionHistoryRow.From(Entry(), Utc);

        row.IsOpen.ShouldBeTrue();
        row.Status.ShouldBe("no record of it ending");
        row.Detail.ShouldNotContain("min");
    }

    /// <summary>
    /// A relayed connection's address is the signalling server's word, not something this computer saw. The
    /// row says which, because "it came from 203.0.113.5" means two different things.
    /// </summary>
    [Fact]
    public void A_reported_address_is_marked_as_reported()
    {
        ConnectionHistoryEntry entry = Entry();
        entry.AddressReported = true;
        entry.Address = "203.0.113.5";

        ConnectionHistoryRow.From(entry, Utc).Detail.ShouldContain("reported by the signalling server");
    }

    [Fact]
    public void A_peer_with_no_name_falls_back_to_its_id_and_then_to_a_word()
    {
        ConnectionHistoryEntry entry = Entry();
        entry.PeerName = string.Empty;
        ConnectionHistoryRow.From(entry, Utc).Who.ShouldBe("123456789");

        entry.PeerId = string.Empty;
        ConnectionHistoryRow.From(entry, Utc).Who.ShouldBe("An unnamed computer");
    }

    [Fact]
    public void Clearing_the_record_reads_as_an_entry_of_its_own()
    {
        var entry = new ConnectionHistoryEntry { Id = "x", Kind = "cleared", StartedUtcMs = 1_790_000_000_000, EndedUtcMs = 1_790_000_000_000 };

        ConnectionHistoryRow row = ConnectionHistoryRow.From(entry, Utc);
        row.Who.ShouldBe("The record was cleared");
        row.Status.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(5, "5 sec")]
    [InlineData(59, "59 sec")]
    [InlineData(60, "1 min")]
    [InlineData(3599, "59 min")]
    [InlineData(3600, "1 h 0 min")]
    [InlineData(7830, "2 h 10 min")]
    public void Durations_are_rounded_to_something_a_person_reads(int seconds, string expected)
    {
        long started = 1_790_000_000_000;
        ConnectionHistoryEntry entry = Entry(started, started + seconds * 1000L);

        ConnectionHistoryRow.From(entry, Utc).Detail.ShouldStartWith(expected);
    }
}
