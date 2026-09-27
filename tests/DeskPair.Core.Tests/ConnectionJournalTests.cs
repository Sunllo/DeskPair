using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using DeskPair.Core.Session.Host;

namespace DeskPair.Core.Tests;

/// <summary>
/// The record of who connected. Two lines per connection rather than one, so a host that is killed
/// mid-session still leaves evidence that somebody was there -- which is the case worth having a journal for.
/// </summary>
public sealed class ConnectionJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sunllo-journal-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero));

    private string Path_ => System.IO.Path.Combine(_dir, "connections.jsonl");

    private ConnectionJournal Journal(int keepLines = ConnectionJournal.DefaultKeepLines) =>
        new(Path_, _time, NullLogger.Instance, keepLines);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static ConnectionRecord Start(string peer = "123456789") => new()
    {
        PeerId = peer,
        PeerName = "Office PC",
        PeerPlatform = "windows",
        Address = "192.168.1.7",
        Transport = "DirectTcp",
        Kind = "remote",
        Authenticated = "permanent",
        Granted = ["PermKeyboard", "PermClipboard"],
    };

    [Fact]
    public void A_connection_is_one_row_made_of_two_lines()
    {
        ConnectionJournal journal = Journal();
        string id = journal.Started(Start());
        _time.Advance(TimeSpan.FromMinutes(12));
        journal.Ended(id, "closed by user");

        ConnectionRecord row = journal.Read().ShouldHaveSingleItem();
        row.Id.ShouldBe(id);
        row.PeerId.ShouldBe("123456789");
        row.PeerName.ShouldBe("Office PC");
        row.Address.ShouldBe("192.168.1.7");
        row.Authenticated.ShouldBe("permanent");
        row.Granted.ShouldBe(["PermKeyboard", "PermClipboard"]);
        row.Reason.ShouldBe("closed by user");
        row.Duration.ShouldBe(TimeSpan.FromMinutes(12));
        row.Unfinished.ShouldBeFalse();

        // The file really is two lines; the folding happens on the way out.
        File.ReadAllLines(Path_).Length.ShouldBe(2);
    }

    /// <summary>The whole reason the start is written immediately.</summary>
    [Fact]
    public void A_connection_the_host_never_saw_end_is_still_recorded()
    {
        ConnectionJournal journal = Journal();
        journal.Started(Start());

        ConnectionRecord row = journal.Read().ShouldHaveSingleItem();
        row.Unfinished.ShouldBeTrue();
        row.Duration.ShouldBeNull();
        row.EndedUtc.ShouldBeNull();
        row.PeerId.ShouldBe("123456789", "everything known at the start survives even with no end");
    }

    [Fact]
    public void Rows_come_back_newest_first_and_the_limit_is_honoured()
    {
        ConnectionJournal journal = Journal();
        for (int i = 0; i < 5; i++)
        {
            string id = journal.Started(Start($"peer{i}"));
            _time.Advance(TimeSpan.FromMinutes(1));
            journal.Ended(id, "done");
        }

        journal.Read().Select(r => r.PeerId).ShouldBe(["peer4", "peer3", "peer2", "peer1", "peer0"]);
        journal.Read(limit: 2).Select(r => r.PeerId).ShouldBe(["peer4", "peer3"]);
    }

    /// <summary>A truncated last line after a power cut must not hide everything written before it.</summary>
    [Fact]
    public void A_broken_line_is_skipped_and_the_rest_survives()
    {
        ConnectionJournal journal = Journal();
        string id = journal.Started(Start());
        journal.Ended(id, "done");
        File.AppendAllText(Path_, "{\"kind\":\"start\",\"record\":{\"id\":\"trunc" + Environment.NewLine);

        journal.Read().ShouldHaveSingleItem().PeerId.ShouldBe("123456789");
    }

    [Fact]
    public void The_file_is_compacted_rather_than_growing_for_ever()
    {
        ConnectionJournal journal = Journal(keepLines: 20);
        for (int i = 0; i < 40; i++)
        {
            string id = journal.Started(Start($"peer{i}"));
            journal.Ended(id, "done");
        }

        File.ReadAllLines(Path_).Length.ShouldBeLessThanOrEqualTo(20);
        IReadOnlyList<ConnectionRecord> rows = journal.Read();
        rows.ShouldNotBeEmpty();
        rows[0].PeerId.ShouldBe("peer39", "the newest is what is kept");
    }

    /// <summary>A record that can be emptied without trace is not a record.</summary>
    [Fact]
    public void Clearing_is_itself_recorded()
    {
        ConnectionJournal journal = Journal();
        journal.Ended(journal.Started(Start()), "done");

        journal.Clear();

        ConnectionRecord row = journal.Read().ShouldHaveSingleItem();
        row.Kind.ShouldBe("cleared");
        row.Reason.ShouldContain("cleared");
        row.Unfinished.ShouldBeFalse();
    }

    [Fact]
    public void Reading_a_journal_that_does_not_exist_yet_is_empty_rather_than_an_error() =>
        Journal().Read().ShouldBeEmpty();
}
