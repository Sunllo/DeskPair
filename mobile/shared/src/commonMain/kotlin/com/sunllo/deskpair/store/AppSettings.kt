package com.sunllo.deskpair.store

import kotlinx.serialization.Serializable

/**
 * Everything the app remembers between launches, except secrets.
 *
 * This is the controller-side half of the desktop's `DesktopConfig` (Desktop/Services/DesktopConfig.cs).
 * Settings that only make sense for a machine that is also a host, or for a desktop window, are not here:
 * there is no device name, no codec preference, no approve mode, no start-with-system, no close-to-tray.
 *
 * **Every field has a default, and none is nullable.** That is what makes a file written by an older build
 * readable by a newer one: kotlinx.serialization fills a missing field from its default rather than from
 * null, which is the same property `DesktopConfig.FromJson` goes to the trouble of getting by merging over
 * a fresh instance. `HostConfig.cs` spells out the hazard it avoids — a file that predates a setting would
 * otherwise read every missing flag as off and silently withdraw it.
 */
@Serializable
public data class AppSettings(
    // ---- network ----
    /**
     * Ask the portal which signalling server to use, instead of naming one here.
     *
     * On by default, because most people do not run their own and an empty settings field is the right
     * thing for them to see. Turning it off is what somebody self-hosting does, and then
     * [rendezvousServer] below is theirs and nothing overwrites it. The desktop's
     * `HostConfig.UseDirectoryServers` is the same switch, spelled the same way round.
     */
    val useDirectoryServers: Boolean = true,
    val rendezvousServer: String = "",
    /** Base64 SPKI of the rendezvous signing key. Empty disables identity verification, and says so. */
    val serverPublicKeyBase64: String = "",
    /** The portal this device syncs with: host, host:port or a full URL. Empty means not configured. */
    val portalServer: String = "",
    /**
     * What this device calls itself in an account's device list.
     *
     * A setting rather than something typed while signing in. It was the latter, which meant it could only
     * be set at the one moment nobody is thinking about it, could never be corrected afterwards, and was
     * not shown back once it had been used.
     *
     * Empty means the app fills in the handset's own model name when it first needs one.
     */
    val deviceName: String = "",
    /** Accept the host's UDP media channel (video with forward error correction). */
    val udpMedia: Boolean = true,
    /** Always connect through a relay instead of trying a direct path. */
    val forceRelay: Boolean = false,

    // ---- general ----
    /** "system", "en" or "zh-TW", as on the desktop. */
    val language: String = "system",
    val recentLimit: Int = 20,

    // ---- defaults for a new session ----
    /** "low", "balanced", "best" or "custom" — the desktop's vocabulary, not a second one. */
    val defaultQuality: String = "balanced",
    val customBitrateKbps: Int = 8000,
    val customFps: Int = 30,
    /** Draw the host's pointer as well. Off by default, for the reason DesktopConfig gives. */
    val showRemoteCursor: Boolean = false,
    val losslessRefinement: Boolean = true,
    val fitToWindow: Boolean = true,
    /** Play the host's sound here. Off by default: it is a surprise rather than a feature. */
    val audioEnabled: Boolean = false,

    /** Watch without touching. Off by default: the usual reason to connect is to do something. */
    val viewOnly: Boolean = false,

    /** Lock the remote machine when the session ends, so an unattended desk is not left signed in. */
    val lockAfterSessionEnd: Boolean = false,
    /** "touch" or "mouse" — which pointer model the session screen starts in. */
    val pointerMode: String = "touch",

    // ---- things the desktop has no equivalent for ----
    /** Offer to remember a password after a successful login. Stored in the keystore, never here. */
    val rememberPasswords: Boolean = true,
    /** Where downloads land: a SAF tree uri on Android, a security-scoped bookmark on iOS. */
    val downloadFolder: String = "",
    val recordingFolder: String = "",
    val recordAudio: Boolean = true,

    val recent: List<RecentPeer> = emptyList(),
    val favourites: List<Favourite> = emptyList(),
    /**
     * The resolution chosen for a remote display, asked for again the next time that machine is reached.
     * Kept here and not in the favourites on purpose: what a viewer likes a screen at is this device's
     * business, so it does not travel with the address book.
     */
    val peerResolutions: List<PeerResolution> = emptyList(),

    // ---- account sync ----
    /** The portal revision this list has been brought up to date with. 0 means it has never synced. */
    val syncRev: Long = 0,
    /**
     * The list as the portal last confirmed it, which is what makes a sync a diff rather than a guess.
     * Empty while this install is not linked to an account, so it costs an unlinked user nothing.
     */
    val syncedFavourites: List<Favourite> = emptyList(),
    /**
     * Folders that exist whether or not anything is in them.
     *
     * Carried even though this app does not let anyone make one. Without it the diff below would see every
     * folder the desktop created sitting in [syncedGroupNames] and absent from here, conclude they had all
     * been deleted, and delete them for everybody. A list the phone only stores and echoes is the whole fix.
     */
    val groupNames: List<String> = emptyList(),
    val syncedGroupNames: List<String> = emptyList(),
) {

    /**
     * Records a successful connection, exactly as `DesktopConfig.WithRecent` does.
     *
     * Three behaviours worth keeping together: deduplicate on the id and move it to the front, clamp the
     * limit to something sane whatever the file said, and — the one that is easy to miss — let a peer that
     * reported no platform keep the one it reported last time, so its logo does not come and go between
     * connections.
     */
    /** The resolution remembered for one display of one peer, or null when there is none. */
    public fun resolutionFor(peer: String, display: String): PeerResolution? =
        peerResolutions.firstOrNull { it.peer == peer && it.display == display }

    /** Remembers [chosen] for one display of one peer, or forgets that display when [chosen] is null. */
    public fun withPeerResolution(peer: String, display: String, chosen: PeerResolution?): AppSettings {
        val rest = peerResolutions.filterNot { it.peer == peer && it.display == display }
        return copy(peerResolutions = if (chosen == null) rest else rest + chosen.copy(peer = peer, display = display))
    }

    public fun withRecent(id: String, name: String = "", platform: String = "", nowMs: Long): AppSettings {
        val keep = recentLimit.coerceIn(1, 200) - 1
        val previous = recent.firstOrNull { it.id == id }
        val known = platform.ifEmpty { previous?.platform ?: "" }
        val rest = recent.filter { it.id != id }.take(keep)
        return copy(recent = listOf(RecentPeer(id, name, nowMs, known)) + rest)
    }

    public fun withoutRecent(id: String): AppSettings = copy(recent = recent.filter { it.id != id })

    public fun withNoRecent(): AppSettings = copy(recent = emptyList())

    // ---- one setting at a time ----
    //
    // Named functions rather than `copy`, because Kotlin's default arguments do not cross into Swift: a
    // SwiftUI binding calling `doCopy` would have to pass all twenty-odd fields to change one of them.
    // They cost a line each and they read the same from both languages.

    public fun withRendezvousServer(value: String): AppSettings = copy(rendezvousServer = value)

    public fun withDirectoryServers(value: Boolean): AppSettings = copy(useDirectoryServers = value)
    public fun withServerPublicKey(value: String): AppSettings = copy(serverPublicKeyBase64 = value)
    public fun withPortalServer(value: String): AppSettings = copy(portalServer = value)
    public fun withDeviceName(value: String): AppSettings = copy(deviceName = value)
    public fun withUdpMedia(value: Boolean): AppSettings = copy(udpMedia = value)
    public fun withForceRelay(value: Boolean): AppSettings = copy(forceRelay = value)
    public fun withLanguage(value: String): AppSettings = copy(language = value)
    public fun withRecentLimit(value: Int): AppSettings = copy(recentLimit = value)
    public fun withDefaultQuality(value: String): AppSettings = copy(defaultQuality = value)
    public fun withCustomBitrateKbps(value: Int): AppSettings = copy(customBitrateKbps = value)
    public fun withCustomFps(value: Int): AppSettings = copy(customFps = value)
    public fun withShowRemoteCursor(value: Boolean): AppSettings = copy(showRemoteCursor = value)
    public fun withLosslessRefinement(value: Boolean): AppSettings = copy(losslessRefinement = value)
    public fun withFitToWindow(value: Boolean): AppSettings = copy(fitToWindow = value)
    public fun withAudioEnabled(value: Boolean): AppSettings = copy(audioEnabled = value)
    public fun withViewOnly(value: Boolean): AppSettings = copy(viewOnly = value)
    public fun withLockAfterSessionEnd(value: Boolean): AppSettings = copy(lockAfterSessionEnd = value)
    public fun withPointerMode(value: String): AppSettings = copy(pointerMode = value)
    public fun withRememberPasswords(value: Boolean): AppSettings = copy(rememberPasswords = value)
    public fun withDownloadFolder(value: String): AppSettings = copy(downloadFolder = value)
    public fun withRecordingFolder(value: String): AppSettings = copy(recordingFolder = value)
    public fun withRecordAudio(value: Boolean): AppSettings = copy(recordAudio = value)

    /** Adds a favourite, or returns this unchanged if the target is already saved. */
    public fun withFavourite(favourite: Favourite): AppSettings =
        if (favourites.any { it.target.equals(favourite.target, ignoreCase = true) }) {
            this
        } else {
            copy(favourites = favourites + favourite)
        }

    /**
     * Renames a saved device, or files it somewhere else.
     *
     * Separate from [withFavourite], which adds and then leaves an existing entry alone: saving a machine
     * and editing the one you saved are different intentions, and one function doing both means an edit
     * that silently does nothing when the entry it means to change is already there.
     */
    public fun withFavouriteEdited(target: String, alias: String, group: String = "", note: String = ""): AppSettings =
        copy(
            favourites = favourites.map {
                if (it.target.equals(target, ignoreCase = true)) {
                    it.copy(alias = alias.trim(), group = group.trim(), note = note.trim())
                } else {
                    it
                }
            },
        )

    /**
     * Adds a group, if it is not already one.
     *
     * Groups are a list of their own rather than whatever names happen to be on the devices, which is what
     * lets an empty one exist -- the desktop has had that since folders started syncing, and the database
     * has a table for it. Naming one while filing a device is how a group is made here; this is what makes
     * the name outlive the device, and what puts it on the wire for [AddressBookSync] to send.
     */
    public fun withGroup(name: String): AppSettings {
        val trimmed = name.trim()
        return if (trimmed.isEmpty() || groupNames.any { it.equals(trimmed, ignoreCase = true) }) {
            this
        } else {
            copy(groupNames = groupNames + trimmed)
        }
    }

    /** Removes a group. Devices filed in it become unfiled rather than disappearing with it. */
    public fun withoutGroup(name: String): AppSettings = copy(
        groupNames = groupNames.filterNot { it.equals(name.trim(), ignoreCase = true) },
        favourites = favourites.map {
            if (it.group.equals(name.trim(), ignoreCase = true)) it.copy(group = "") else it
        },
    )

    /** Renames a group, taking the devices filed in it along. */
    public fun withGroupRenamed(from: String, to: String): AppSettings {
        val target = to.trim()
        if (target.isEmpty()) {
            return this
        }

        return copy(
            groupNames = groupNames.map { if (it.equals(from.trim(), ignoreCase = true)) target else it }.distinct(),
            favourites = favourites.map {
                if (it.group.equals(from.trim(), ignoreCase = true)) it.copy(group = target) else it
            },
        )
    }

    /**
     * Forgets the device list and everything the sync knows about it.
     *
     * Called when this install stops belonging to an account, and it has to be: the list is the account's,
     * and what was left behind was pushed into the next account signed in on this phone. Somebody who
     * deleted an account, made a new one with the same address and found their old devices waiting was
     * seeing their own phone put them back -- the portal had deleted them, correctly, and then been told
     * about them again.
     *
     * The bookkeeping goes with the entries. A revision number kept across accounts asks the next portal
     * for "what changed since 41" about a list it has never seen, and a confirmed copy left behind makes
     * the first sync a diff against somebody else's list.
     */
    public fun withoutAccountData(): AppSettings = copy(
        favourites = emptyList(),
        groupNames = emptyList(),
        syncRev = 0,
        syncedFavourites = emptyList(),
        syncedGroupNames = emptyList(),
    )

    public fun withoutFavourite(target: String): AppSettings =
        copy(favourites = favourites.filterNot { it.target.equals(target, ignoreCase = true) })

    public fun isFavourite(target: String): Boolean =
        favourites.any { it.target.equals(target, ignoreCase = true) }

    /** Records that a favourite was reached, so the list can show how recently. */
    public fun touchFavourite(target: String, platform: String = "", nowMs: Long): AppSettings =
        copy(
            favourites = favourites.map {
                if (it.target.equals(target, ignoreCase = true)) {
                    it.copy(lastConnectedMs = nowMs, platform = platform.ifEmpty { it.platform })
                } else {
                    it
                }
            },
        )
}

/** A resolution chosen for a remote display: the peer, the display's name on the host, and the mode's pixels and scale. */
@Serializable
public data class PeerResolution(
    val peer: String,
    val display: String,
    val width: Int,
    val height: Int,
    val scale: Double = 0.0,
)

/** One entry in the recent list. The same four fields the desktop's `RecentPeer` carries, and no more. */
@Serializable
public data class RecentPeer(
    val id: String,
    val name: String = "",
    val lastConnectedMs: Long = 0,
    /** What it was running when it was last reached, for the logo. Empty shows none. */
    val platform: String = "",
)

/**
 * A saved machine.
 *
 * Thinner than the desktop's `SavedDevice` in one way that stays: no online state. The desktop polls
 * the rendezvous server every fifteen seconds to colour its dots (`PeerPresence.QueryAsync`), which is a
 * reasonable thing for a machine on mains power to do and an unreasonable thing to do on a phone.
 */
@Serializable
public data class Favourite(
    /** An id or an address; the same string the user would type. */
    val target: String,
    val alias: String = "",
    /**
     * The folder this sits in; the desktop calls the same thing a `Group`.
     *
     * This was missing entirely, which mattered the moment lists started syncing: a book arriving from a
     * desktop would have been flattened, and pushed back flat, silently losing how somebody had organised it.
     */
    val group: String = "",
    val note: String = "",
    val lastConnectedMs: Long = 0,
    val platform: String = "",
) {
    /** What to show: the alias if there is one, else the target in the grouped nine-digit form. */
    public val title: String get() = alias.ifEmpty { Targets.formatId(target) }
}
