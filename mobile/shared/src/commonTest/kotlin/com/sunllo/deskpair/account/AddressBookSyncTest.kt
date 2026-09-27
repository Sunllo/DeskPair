package com.sunllo.deskpair.account

import com.sunllo.deskpair.store.AppSettings
import com.sunllo.deskpair.store.Favourite
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * Working out what changed here, and folding in what changed elsewhere.
 *
 * The same two halves the desktop tests cover, asserted separately rather than trusted to be the same: the
 * two clients are different code and the portal cannot tell them apart, so a phone that merged differently
 * would show up as "sync sometimes loses things" and nothing more specific.
 *
 * The one that matters most here is the folder test. This app has no way to make a folder, so without the
 * list it stores and echoes, its diff would see every folder the desktop made sitting in the confirmed copy
 * and absent from the live one, and delete them for everybody.
 */
class AddressBookSyncTest {

    private fun favourite(target: String, alias: String = "", group: String = "") =
        Favourite(target = target, alias = alias, group = group)

    private fun change(target: String, alias: String = "", folder: String = "", deleted: Boolean = false, rev: Long = 1) =
        EntryChange(target = target, alias = alias, folder = folder, deleted = deleted, rev = rev)

    @Test
    fun `a list that matches what the portal confirmed has nothing to send`() {
        val one = favourite("123456789", "Office PC")
        val settings = AppSettings(
            favourites = listOf(one),
            syncedFavourites = listOf(one),
            groupNames = listOf("Site A"),
            syncedGroupNames = listOf("Site A"),
        )

        val (entries, folders) = AddressBookSync.diff(settings)

        // Otherwise every sync would bump the revision and make every other device re-read the whole book.
        assertTrue(entries.isEmpty(), "sent $entries")
        assertTrue(folders.isEmpty(), "sent $folders")
    }

    @Test
    fun `a new favourite and an edited one are both sent`() {
        val settings = AppSettings(
            favourites = listOf(favourite("111111111", "Renamed"), favourite("222222222", "Brand new")),
            syncedFavourites = listOf(favourite("111111111", "Original")),
        )

        val (entries, _) = AddressBookSync.diff(settings)

        assertEquals(2, entries.size)
        assertEquals("Renamed", entries.first { it.target == "111111111" }.alias)
    }

    @Test
    fun `a favourite that is gone is sent as a deletion`() {
        val settings = AppSettings(
            favourites = listOf(favourite("111111111")),
            syncedFavourites = listOf(favourite("111111111"), favourite("222222222", "Removed")),
        )

        val (entries, _) = AddressBookSync.diff(settings)

        // Nothing marked this as deleted. It falls out of the comparison, which is why the confirmed copy is
        // kept instead of a flag every caller has to remember to set.
        assertEquals(1, entries.size)
        assertEquals("222222222", entries[0].target)
        assertTrue(entries[0].deleted)
    }

    @Test
    fun `this app never deletes a folder it cannot make`() {
        // Exactly what a phone looks like after pulling a desktop's book: folders in both lists, and no way
        // in the whole app to change either. The diff must be silent about them.
        val settings = AppSettings(
            groupNames = listOf("Site A", "Site B"),
            syncedGroupNames = listOf("Site A", "Site B"),
        )

        val (_, folders) = AddressBookSync.diff(settings)

        assertTrue(folders.isEmpty(), "would have deleted $folders for everybody")
    }

    @Test
    fun `the group arrives and is kept`() {
        val after = AddressBookSync.apply(
            AppSettings(),
            SyncResponse(rev = 3, entries = listOf(change("123456789", "Office PC", folder = "Site A"))),
            emptyList(),
        )

        // The field did not exist here until lists started syncing. Without it a book from a desktop would
        // arrive flattened and be pushed back flat, silently losing how somebody had organised it.
        assertEquals("Site A", after.favourites.single().group)
        assertEquals(3, after.syncRev)

        // Recorded as confirmed, so the next diff does not send it straight back.
        assertTrue(AddressBookSync.diff(after).first.isEmpty())
    }

    @Test
    fun `a deletion from the portal removes the favourite here`() {
        val one = favourite("123456789", "Office PC")
        val settings = AppSettings(favourites = listOf(one), syncedFavourites = listOf(one), syncRev = 3)

        val after = AddressBookSync.apply(settings, SyncResponse(rev = 4, entries = listOf(change("123456789", deleted = true, rev = 4))), emptyList())

        assertTrue(after.favourites.isEmpty())

        // Forgotten from the confirmed copy too, or the next diff would send it back as a new favourite.
        assertTrue(AddressBookSync.diff(after).first.isEmpty())
    }

    @Test
    fun `an edit made while the request was in flight is not overwritten`() {
        val settings = AppSettings(
            favourites = listOf(favourite("123456789", "Mine")),
            syncedFavourites = listOf(favourite("123456789", "Original")),
        )

        val after = AddressBookSync.apply(settings, SyncResponse(rev = 5, entries = listOf(change("123456789", "Theirs", rev = 5))), emptyList())

        // Overwriting would lose an edit the user watched themselves make.
        assertEquals("Mine", after.favourites.single().alias)

        // Still different from the confirmed copy, so the next sync sends it and the two converge.
        assertEquals("Mine", AddressBookSync.diff(after).first.single().alias)
    }

    @Test
    fun `what this phone just sent is accepted back even if the portal changed it`() {
        val settings = AppSettings(
            favourites = listOf(favourite("123456789", "Mine")),
            syncedFavourites = listOf(favourite("123456789", "Original")),
        )

        // We pushed "Mine"; somebody else pushed "Theirs" a moment later and won. Last write wins, and this
        // phone has to let it settle rather than fight it forever.
        val after = AddressBookSync.apply(
            settings,
            SyncResponse(rev = 6, entries = listOf(change("123456789", "Theirs", rev = 6))),
            listOf("123456789"),
        )

        assertEquals("Theirs", after.favourites.single().alias)
        assertTrue(AddressBookSync.diff(after).first.isEmpty(), "the argument is settled")
    }

    @Test
    fun `a folder from the portal is stored and echoed and not dropped`() {
        val after = AddressBookSync.apply(
            AppSettings(),
            SyncResponse(rev = 2, folders = listOf(FolderChange("Site A"))),
            emptyList(),
        )

        assertEquals(listOf("Site A"), after.groupNames)
        assertEquals(listOf("Site A"), after.syncedGroupNames)

        // And the echo is silent: it is in both lists, so nothing is sent back.
        assertTrue(AddressBookSync.diff(after).second.isEmpty())
    }

    @Test
    fun `a folder deleted elsewhere goes away here without taking its favourites`() {
        val one = favourite("123456789", "PC", group = "Site A")
        val settings = AppSettings(
            favourites = listOf(one),
            syncedFavourites = listOf(one),
            groupNames = listOf("Site A"),
            syncedGroupNames = listOf("Site A"),
        )

        val after = AddressBookSync.apply(settings, SyncResponse(rev = 9, folders = listOf(FolderChange("Site A", deleted = true))), emptyList())

        assertTrue(after.groupNames.isEmpty())

        // The favourite stays. Removing a folder has never deleted what was in it, on either side.
        assertEquals("123456789", after.favourites.single().target)
    }

    @Test
    fun `a fresh install is told about deletions rather than resurrecting them`() {
        val after = AddressBookSync.apply(
            AppSettings(),
            SyncResponse(
                rev = 12,
                entries = listOf(change("111111111", "Still here", rev = 11), change("222222222", deleted = true, rev = 12)),
            ),
            emptyList(),
        )

        assertEquals("111111111", after.favourites.single().target)

        // The dangerous half: a reinstall that ignored tombstones would push the deleted one back.
        assertTrue(AddressBookSync.diff(after).first.isEmpty())
    }

    @Test
    fun `a target is the same machine whatever its capitalisation`() {
        val settings = AppSettings(
            favourites = listOf(favourite("Office-PC.local", "Mine")),
            syncedFavourites = listOf(favourite("office-pc.local", "Mine")),
        )

        // The rest of this app already treats these as one machine; the comparison here must agree, or every
        // sync would send an entry that nothing had changed.
        assertTrue(AddressBookSync.diff(settings).first.isEmpty())
    }

    @Test
    fun `settings written by an older build sync their whole list once`() {
        // No syncedFavourites and no syncRev: the fields did not exist when this file was written.
        val settings = AppSettings(favourites = listOf(favourite("123456789", "Office PC")))

        assertEquals(0, settings.syncRev)
        assertEquals("123456789", AddressBookSync.diff(settings).first.single().target)
    }
}
