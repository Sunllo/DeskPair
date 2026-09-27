using DeskPair.Core.Portal;
using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// The two halves of a sync that have nothing to do with the network: working out what changed here, and
/// folding in what changed elsewhere.
///
/// Both are <c>internal</c> rather than public — the app has no reason to call them — and this project
/// already has <c>InternalsVisibleTo</c>. What they are worth testing for is that a deletion is noticed,
/// that folded-away groups never travel, and that an edit made while a request was in flight is not quietly
/// overwritten. None of those can be checked from outside the class.
/// </summary>
public class AddressBookSyncTests
{
    private static (List<EntryChange> Entries, List<FolderChange> Folders) Diff(DeviceBook book) =>
        AddressBookSync.Diff(book);

    private static DeviceBook Apply(DeviceBook book, SyncResponse response, params string[] pushed) =>
        AddressBookSync.Apply(book, response, pushed);

    private static SavedDevice Device(string target, string alias = "", string group = "") =>
        new() { Target = target, Alias = alias, Group = group };

    private static EntryChange Change(string target, string alias = "", string folder = "", bool deleted = false, long rev = 1) =>
        new(target, alias, folder, string.Empty, 0, string.Empty, deleted, rev);

    [Fact]
    public void A_book_that_matches_what_the_portal_confirmed_has_nothing_to_send()
    {
        SavedDevice device = Device("123456789", "Office PC");
        var book = new DeviceBook { Devices = [device], Synced = [device], GroupNames = ["Site A"], SyncedGroupNames = ["Site A"] };

        (List<EntryChange> entries, List<FolderChange> folders) = Diff(book);

        // Otherwise every sync would bump the revision and make every other device re-read the whole book.
        entries.ShouldBeEmpty();
        folders.ShouldBeEmpty();
    }

    [Fact]
    public void A_new_device_and_an_edited_one_are_both_sent()
    {
        var book = new DeviceBook
        {
            Devices = [Device("111111111", "Renamed"), Device("222222222", "Brand new")],
            Synced = [Device("111111111", "Original")],
        };

        (List<EntryChange> entries, _) = Diff(book);

        entries.Count.ShouldBe(2);
        entries.Single(e => e.Target == "111111111").Alias.ShouldBe("Renamed");
        entries.Single(e => e.Target == "222222222").Deleted.ShouldBeFalse();
    }

    [Fact]
    public void A_device_that_is_gone_from_the_list_is_sent_as_a_deletion()
    {
        var book = new DeviceBook
        {
            Devices = [Device("111111111")],
            Synced = [Device("111111111"), Device("222222222", "Removed")],
        };

        (List<EntryChange> entries, _) = Diff(book);

        // Nothing marked this as deleted. It falls out of the comparison, which is the whole reason the
        // confirmed copy is kept instead of a flag somebody has to remember to set.
        EntryChange tombstone = entries.ShouldHaveSingleItem();
        tombstone.Target.ShouldBe("222222222");
        tombstone.Deleted.ShouldBeTrue();
    }

    [Fact]
    public void A_group_made_and_a_group_removed_are_both_sent()
    {
        var book = new DeviceBook
        {
            GroupNames = ["Site A", "Site C"],
            SyncedGroupNames = ["Site A", "Site B"],
        };

        (_, List<FolderChange> folders) = Diff(book);

        folders.Single(f => f.Name == "Site C").Deleted.ShouldBeFalse();
        folders.Single(f => f.Name == "Site B").Deleted.ShouldBeTrue();
    }

    [Fact]
    public void Folding_a_group_away_is_not_a_change_worth_sending()
    {
        SavedDevice device = Device("123456789", "PC", "Site A");
        var book = new DeviceBook
        {
            Devices = [device],
            Synced = [device],
            GroupNames = ["Site A"],
            SyncedGroupNames = ["Site A"],
            CollapsedGroups = ["Site A"],
        };

        (List<EntryChange> entries, List<FolderChange> folders) = Diff(book);

        // How this screen looks to this person is not what the list contains. Two machines are allowed to
        // disagree about it, and syncing it would make one of them wrong.
        entries.ShouldBeEmpty();
        folders.ShouldBeEmpty();
    }

    [Fact]
    public void What_the_portal_sends_lands_in_the_list_and_is_recorded_as_confirmed()
    {
        var book = new DeviceBook();

        DeviceBook after = Apply(book, new SyncResponse(7, [Change("123456789", "From the phone", "Site A")], [new FolderChange("Site A", false)]));

        after.Devices.ShouldHaveSingleItem().Alias.ShouldBe("From the phone");
        after.Devices[0].Group.ShouldBe("Site A");
        after.GroupNames.ShouldHaveSingleItem().ShouldBe("Site A");
        after.SyncRev.ShouldBe(7);

        // Recorded as confirmed, so the next diff does not send it straight back.
        Diff(after).Entries.ShouldBeEmpty();
    }

    [Fact]
    public void A_deletion_from_the_portal_removes_the_device_here()
    {
        SavedDevice device = Device("123456789", "Office PC");
        var book = new DeviceBook { Devices = [device], Synced = [device], SyncRev = 3 };

        DeviceBook after = Apply(book, new SyncResponse(4, [Change("123456789", deleted: true, rev: 4)], []));

        after.Devices.ShouldBeEmpty();

        // And forgotten from the confirmed copy too, or the next diff would send it back as a new device.
        Diff(after).Entries.ShouldBeEmpty();
    }

    [Fact]
    public void An_edit_made_while_the_request_was_in_flight_is_not_overwritten()
    {
        // Confirmed as "Original"; the user has since typed "Mine", and that edit was not in what we sent.
        var book = new DeviceBook
        {
            Devices = [Device("123456789", "Mine")],
            Synced = [Device("123456789", "Original")],
        };

        // The portal answers with a version that predates the typing.
        DeviceBook after = Apply(book, new SyncResponse(5, [Change("123456789", "Theirs", rev: 5)], []));

        // Overwriting would lose an edit the user watched themselves make.
        after.Devices.ShouldHaveSingleItem().Alias.ShouldBe("Mine");

        // It is still different from the confirmed copy, so the next sync sends it and the two converge.
        Diff(after).Entries.ShouldHaveSingleItem().Alias.ShouldBe("Mine");
    }

    [Fact]
    public void What_this_machine_just_sent_is_accepted_back_even_if_the_portal_changed_it()
    {
        var book = new DeviceBook
        {
            Devices = [Device("123456789", "Mine")],
            Synced = [Device("123456789", "Original")],
        };

        // We pushed "Mine"; somebody else pushed "Theirs" a moment later and won. Last write wins, and this
        // machine has to accept that rather than fight it forever.
        DeviceBook after = Apply(book, new SyncResponse(6, [Change("123456789", "Theirs", rev: 6)], []), "123456789");

        after.Devices.ShouldHaveSingleItem().Alias.ShouldBe("Theirs");
        Diff(after).Entries.ShouldBeEmpty("the argument is settled");
    }

    [Fact]
    public void A_group_deleted_elsewhere_goes_away_here_without_taking_its_devices()
    {
        SavedDevice device = Device("123456789", "PC", "Site A");
        var book = new DeviceBook
        {
            Devices = [device],
            Synced = [device],
            GroupNames = ["Site A"],
            SyncedGroupNames = ["Site A"],
        };

        DeviceBook after = Apply(book, new SyncResponse(9, [], [new FolderChange("Site A", true)]));

        after.GroupNames.ShouldBeEmpty();

        // The device stays. Removing a group has never deleted what was in it, on either side.
        after.Devices.ShouldHaveSingleItem().Target.ShouldBe("123456789");
    }

    [Fact]
    public void A_fresh_install_is_told_about_deletions_rather_than_resurrecting_them()
    {
        // since=0 on a book that has never synced. The portal sends tombstones as well as live entries.
        var book = new DeviceBook();

        DeviceBook after = Apply(book, new SyncResponse(12,
            [Change("111111111", "Still here", rev: 11), Change("222222222", deleted: true, rev: 12)], []));

        after.Devices.ShouldHaveSingleItem().Target.ShouldBe("111111111");

        // The dangerous half: a reinstall that ignored tombstones would push the deleted one back.
        Diff(after).Entries.ShouldBeEmpty();
    }

    [Fact]
    public void A_target_is_the_same_device_whatever_its_capitalisation()
    {
        var book = new DeviceBook { Devices = [Device("Office-PC.local", "Mine")], Synced = [Device("office-pc.local", "Mine")] };

        // The desktop already treats these as one computer; the comparison here must agree, or every sync
        // would send an entry that nothing had changed.
        Diff(book).Entries.ShouldBeEmpty();
    }

    [Fact]
    public void A_book_written_by_an_older_build_syncs_its_whole_contents_once()
    {
        // No Synced and no SyncRev: the fields did not exist when this file was written.
        DeviceBook book = DeviceBook.FromJson("""
            { "Devices": [ { "Target": "123456789", "Alias": "Office PC" } ], "GroupNames": [ "Site A" ] }
            """);

        book.SyncRev.ShouldBe(0);
        (List<EntryChange> entries, List<FolderChange> folders) = Diff(book);

        entries.ShouldHaveSingleItem().Target.ShouldBe("123456789");
        folders.ShouldHaveSingleItem().Name.ShouldBe("Site A");
    }
}
