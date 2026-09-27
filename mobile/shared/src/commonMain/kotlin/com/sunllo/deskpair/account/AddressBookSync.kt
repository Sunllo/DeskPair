package com.sunllo.deskpair.account

import com.sunllo.deskpair.store.AppSettings
import com.sunllo.deskpair.store.Favourite
import com.sunllo.deskpair.store.SettingsStore

/** What one sync did. Both counts include folders as well as entries. */
public data class SyncReport(
    val linked: Boolean,
    val sent: Int = 0,
    val received: Int = 0,
    val problem: String? = null,
) {
    public val changed: Boolean get() = received > 0

    public companion object {
        public val NOT_LINKED: SyncReport = SyncReport(linked = false)
    }
}

/**
 * Keeps this phone's saved list in step with the account's.
 *
 * The same design as `AddressBookSync.cs` on the desktop, and the same reason for it: what changed here is a
 * comparison against [AppSettings.syncedFavourites] rather than a flag somebody has to remember to set. A
 * dirty flag is only ever as reliable as the least careful caller of the method that should have set it, and
 * the favourite list is touched from the connect screen, the favourites tab and the end of every session.
 *
 * Only the personal book. Team books exist on the portal and answer the same endpoint; showing more than one
 * list is a change to the favourites tab rather than to this file.
 */
public class AddressBookSync(
    private val link: AccountLink,
    private val store: SettingsStore,
    private val clientFor: (String) -> PortalClient = { PortalClient(it) },
) {
    private var running = false

    /**
     * Runs one exchange. Safe to call unlinked, offline, or while one is already in flight: all three do
     * nothing and say so, rather than throwing at a caller that is usually a lifecycle event.
     */
    public suspend fun sync(): SyncReport {
        if (running) {
            // A second overlapping exchange would push a diff computed against a snapshot the first one is
            // about to replace.
            return SyncReport(linked = true)
        }

        running = true
        return try {
            run()
        } catch (e: PortalException) {
            SyncReport(linked = true, problem = e.message)
        } finally {
            running = false
        }
    }

    private suspend fun run(): SyncReport {
        val credential = link.credential() ?: return SyncReport.NOT_LINKED
        val state = link.refresh()
        if (!state.isLinked) {
            return SyncReport.NOT_LINKED
        }

        val personal = state.scopes.firstOrNull { it.kind == "personal" } ?: return SyncReport(linked = true)

        val before = store.current
        val (entries, folders) = diff(before)

        val response = clientFor(credential.first).use {
            it.syncBook(credential.second, SyncRequest(personal.kind, personal.id, before.syncRev, entries, folders))
        }

        // Re-read through update: the user may have saved or removed a favourite while the request was in
        // flight, and what is in the store now is what has to be brought up to date.
        store.update { current -> apply(current, response, entries.map { it.target }) }

        return SyncReport(linked = true, sent = entries.size + folders.size, received = response.entries.size + response.folders.size)
    }

    public companion object {
        /** What this phone has that the portal has not confirmed, and what it no longer has. */
        internal fun diff(settings: AppSettings): Pair<List<EntryChange>, List<FolderChange>> {
            val confirmed = settings.syncedFavourites.associateBy { it.target.lowercase() }
            val changes = mutableListOf<EntryChange>()

            for (favourite in settings.favourites) {
                val was = confirmed[favourite.target.lowercase()]
                if (was == null || !sameContent(was, favourite)) {
                    changes += toChange(favourite, deleted = false)
                }
            }

            val live = settings.favourites.map { it.target.lowercase() }.toSet()
            for (gone in settings.syncedFavourites.filter { it.target.lowercase() !in live }) {
                changes += toChange(gone, deleted = true)
            }

            // Always empty in practice: nothing in this app makes or removes a folder. Computed anyway, so
            // that the day it does, this needs no change.
            val confirmedGroups = settings.syncedGroupNames.map { it.lowercase() }.toSet()
            val liveGroups = settings.groupNames.map { it.lowercase() }.toSet()
            val folders = settings.groupNames.filter { it.lowercase() !in confirmedGroups }.map { FolderChange(it, false) } +
                settings.syncedGroupNames.filter { it.lowercase() !in liveGroups }.map { FolderChange(it, true) }

            return changes to folders
        }

        /**
         * Folds what the portal sent into the list, and records it as confirmed.
         *
         * A favourite changed here while the request was in flight is left alone: it was not in what we sent,
         * so the portal's copy is older than what is on screen. It stays different from the snapshot, so the
         * next sync sends it and the two converge.
         */
        internal fun apply(settings: AppSettings, response: SyncResponse, pushed: List<String>): AppSettings {
            val sent = pushed.map { it.lowercase() }.toSet()
            val confirmed = settings.syncedFavourites.associateBy { it.target.lowercase() }.toMutableMap()
            val favourites = settings.favourites.associateBy { it.target.lowercase() }.toMutableMap()

            for (entry in response.entries) {
                val key = entry.target.lowercase()
                val local = favourites[key]
                val editedMeanwhile = key !in sent && local != null && confirmed[key].let { it == null || !sameContent(it, local) }
                if (editedMeanwhile) {
                    continue
                }

                if (entry.deleted) {
                    favourites.remove(key)
                    confirmed.remove(key)
                } else {
                    // lastConnectedMs is the one field the phone knows better than the portal does for a
                    // session it just had; the portal has it too, because touching a favourite is a change
                    // like any other and was sent as one.
                    val applied = fromChange(entry)
                    favourites[key] = applied
                    confirmed[key] = applied
                }
            }

            val groups = settings.groupNames.toMutableList()
            val confirmedGroups = settings.syncedGroupNames.toMutableList()
            for (folder in response.folders) {
                groups.removeAll { it.equals(folder.name, ignoreCase = true) }
                confirmedGroups.removeAll { it.equals(folder.name, ignoreCase = true) }
                if (!folder.deleted) {
                    groups += folder.name
                    confirmedGroups += folder.name
                }
            }

            return settings.copy(
                favourites = favourites.values.toList(),
                syncedFavourites = confirmed.values.toList(),
                groupNames = groups,
                syncedGroupNames = confirmedGroups,
                syncRev = response.rev,
            )
        }

        /** Everything that travels. Nothing about how this screen looks is in here. */
        private fun sameContent(a: Favourite, b: Favourite): Boolean =
            a.alias == b.alias && a.group == b.group && a.note == b.note &&
                a.platform == b.platform && a.lastConnectedMs == b.lastConnectedMs

        private fun toChange(favourite: Favourite, deleted: Boolean) = EntryChange(
            target = favourite.target,
            alias = favourite.alias,
            folder = favourite.group,
            note = favourite.note,
            lastConnected = favourite.lastConnectedMs,
            platform = favourite.platform,
            deleted = deleted,
        )

        private fun fromChange(entry: EntryChange) = Favourite(
            target = entry.target,
            alias = entry.alias,
            group = entry.folder,
            note = entry.note,
            lastConnectedMs = entry.lastConnected,
            platform = entry.platform,
        )
    }
}
