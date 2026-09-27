using DeskPair.Core.Portal;
using DeskPair.Desktop.Services;
using DeskPair.Protocol.Ipc;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// Which rows go up. The rule has to send a row again after it has ended -- a connection uploaded while it
/// was still open would otherwise sit in the account for ever with no ending -- without resending the whole
/// file every quarter of an hour.
/// </summary>
public class ConnectionHistoryUploadTests
{
    private static ConnectionHistoryEntry Entry(string id, long startedMs, long endedMs = 0, string kind = "remote")
    {
        var entry = new ConnectionHistoryEntry
        {
            Id = id,
            StartedUtcMs = startedMs,
            EndedUtcMs = endedMs,
            PeerId = "123456789",
            PeerName = "Office PC",
            Address = "192.168.1.7",
            Transport = "DirectTcp",
            Kind = kind,
            Authenticated = "permanent",
        };
        entry.Granted.Add("PermKeyboard");
        return entry;
    }

    /// <summary>Newest first, as the engine hands them over.</summary>
    private static ConnectionHistoryEntry[] Newest(int count, long from = 1_000_000) =>
        [.. Enumerable.Range(0, count).Select(i => Entry($"id{count - 1 - i}", from + ((count - 1 - i) * 1000L), from + ((count - 1 - i) * 1000L) + 500))];

    [Fact]
    public void A_terminal_connection_goes_up_with_its_shells_and_whose_they_were()
    {
        ConnectionHistoryEntry entry = Entry("t", 1_000_000, 1_060_000, kind: "terminal");
        entry.TerminalOpens = 2;
        entry.TerminalIdentity = "NT AUTHORITY\\SYSTEM";

        ConnectionUpload sent = ConnectionHistoryUpload.ToSend([entry], watermark: 0).ShouldHaveSingleItem();

        sent.Kind.ShouldBe("terminal");
        sent.TerminalOpens.ShouldBe(2);
        sent.TerminalIdentity.ShouldBe("NT AUTHORITY\\SYSTEM");
    }

    [Fact]
    public void With_no_watermark_everything_goes()
    {
        IReadOnlyList<ConnectionUpload> send = ConnectionHistoryUpload.ToSend(Newest(3), watermark: 0);

        send.Count.ShouldBe(3);
        send[0].Id.ShouldBe("id2");
        send[0].PeerName.ShouldBe("Office PC");
        send[0].Granted.ShouldBe(["PermKeyboard"]);
    }

    [Fact]
    public void Rows_newer_than_the_watermark_go_and_a_few_older_ones_go_again()
    {
        ConnectionHistoryEntry[] all = Newest(200);
        long watermark = all[100].StartedUtcMs;

        IReadOnlyList<ConnectionUpload> send = ConnectionHistoryUpload.ToSend(all, watermark);

        // The 100 above the mark, plus the overlap that lets a connection which has since ended be closed.
        send.Count.ShouldBe(100 + ConnectionHistoryUpload.Overlap);
        send.Select(e => e.Id).ShouldContain("id199");
        send.Select(e => e.Id).ShouldContain("id99", customMessage: "the overlap reaches below the watermark");
        send.Select(e => e.Id).ShouldNotContain("id0", "and it stops, rather than resending the whole file");
    }

    /// <summary>The whole reason for the overlap.</summary>
    [Fact]
    public void A_connection_uploaded_while_it_was_open_is_sent_again_once_it_has_ended()
    {
        ConnectionHistoryEntry open = Entry("still-going", 5_000);
        ConnectionHistoryUpload.ToSend([open], watermark: 0).ShouldHaveSingleItem().EndedUtc.ShouldBe(0);

        // Next time round the watermark is at or past its start, and it has an ending.
        ConnectionHistoryEntry ended = Entry("still-going", 5_000, 9_000);
        ConnectionUpload again = ConnectionHistoryUpload.ToSend([ended], watermark: 5_000).ShouldHaveSingleItem();
        again.Id.ShouldBe("still-going");
        again.EndedUtc.ShouldBe(9_000);
    }

    /// <summary>Clearing is a fact about this machine's own file, not about anybody connecting to it.</summary>
    [Fact]
    public void The_clearing_marker_is_not_uploaded()
    {
        ConnectionHistoryEntry[] entries = [Entry("cleared-1", 9_000, 9_000, kind: "cleared"), Entry("real", 8_000, 8_500)];

        ConnectionHistoryUpload.ToSend(entries, watermark: 0).ShouldHaveSingleItem().Id.ShouldBe("real");
    }

    /// <summary>
    /// With nothing new, what goes is the overlap and no more -- so a quiet machine re-offers a fixed
    /// handful rather than its whole file, and the portal's merge makes those a no-op.
    /// </summary>
    [Fact]
    public void With_nothing_new_only_the_overlap_goes()
    {
        ConnectionHistoryEntry[] all = Newest(200);
        long watermark = all[0].StartedUtcMs; // the newest row is already up

        IReadOnlyList<ConnectionUpload> send = ConnectionHistoryUpload.ToSend(all, watermark);

        send.Count.ShouldBe(ConnectionHistoryUpload.Overlap);
        send[0].Id.ShouldBe("id199", "starting from the newest");
    }

    /// <summary>A machine with less history than the overlap simply offers all of it; the merge absorbs it.</summary>
    [Fact]
    public void A_short_history_is_offered_whole()
    {
        ConnectionHistoryUpload.ToSend([Entry("a", 1_000, 1_500)], watermark: 100_000)
            .ShouldHaveSingleItem().Id.ShouldBe("a");
    }
}
