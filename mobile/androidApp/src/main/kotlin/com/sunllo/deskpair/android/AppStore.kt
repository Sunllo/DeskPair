package com.sunllo.deskpair.android

import android.content.Context
import com.sunllo.deskpair.ResolutionMemory
import com.sunllo.deskpair.account.AccountController
import com.sunllo.deskpair.account.AddressBookSync
import com.sunllo.deskpair.account.AccountLink
import com.sunllo.deskpair.account.DeviceIdentity
import com.sunllo.deskpair.crypto.PinnedKeyStore
import com.sunllo.deskpair.store.NetworkDirectory
import com.sunllo.deskpair.store.AppSettings
import com.sunllo.deskpair.store.Favourite
import com.sunllo.deskpair.store.PersistentPinnedKeyStore
import com.sunllo.deskpair.store.RememberedPasswords
import com.sunllo.deskpair.store.SettingsStore
import com.sunllo.deskpair.store.Targets
import com.sunllo.deskpair.store.NetworkDirectoryClient
import com.sunllo.deskpair.transport.OnlineState
import com.sunllo.deskpair.transport.PeerPresence
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/**
 * Everything the app knows before a session starts.
 *
 * A plain object rather than a second ViewModel. The session needs the settings to connect at all, and the
 * settings need to hear that a connection succeeded; two ViewModels pointing at each other is a worse
 * arrangement than one owning the other, and none of this has any lifecycle of its own.
 */
class AppStore(context: Context) {

    private val secrets = AndroidSecretStore(context)

    private val storage = AndroidSettingsStorage(context)

    private val store = SettingsStore(storage)

    val settings: StateFlow<AppSettings> get() = store.settings

    val current: AppSettings get() = store.current

    /** Pins that survive the app closing, which is the only kind worth having. */
    val pinnedKeys: PinnedKeyStore = PersistentPinnedKeyStore(secrets)

    val passwords = RememberedPasswords(secrets)

    /**
     * The account this install is linked to, and the key it proves itself with.
     *
     * Built here so the key is generated once per install and lives in the same keystore as the pins and
     * the remembered passwords. Lazily, because an install that never links never needs a key at all.
     */
    val account: AccountController by lazy {
        AccountController(link)
    }

    private val link by lazy { AccountLink(DeviceIdentity(secrets), secrets, platform = "android") }

    /**
     * Keeps the saved list in step with the account's.
     *
     * Best-effort and silent: a failed sync is the normal state of a phone on a train, and the list on
     * screen works either way — which is the point of syncing something that was already local.
     */
    val bookSync: AddressBookSync by lazy { AddressBookSync(link, store) }


    fun update(transform: (AppSettings) -> AppSettings) = store.update(transform)

    /** Writes anything the debounce is still holding. Called when the app goes to the background. */
    fun flush() = store.flush()

    /**
     * Records a connection that worked.
     *
     * Both lists are touched, the way `App.RememberPeer` touches both `desktop.json` and `devices.json`:
     * the recent list gains an entry, and a favourite for the same machine learns when it was last reached
     * and what it is running.
     */
    fun rememberConnection(target: String, name: String, platform: String) {
        val now = System.currentTimeMillis()
        update { it.withRecent(target, name, platform, now).touchFavourite(target, platform, now) }
    }

    /** Where a session keeps the resolution chosen for [peer]'s displays. */
    fun resolutionsFor(peer: String): ResolutionMemory = store.resolutionsFor(Targets.normalise(peer))

    /**
     * Saves a machine to the device list, or edits the one already there.
     *
     * The name and the password travel together because they are decided together and kept apart: the
     * name is a setting, the password is in the encrypted store. A caller holding both halves would one
     * day write one and not the other.
     */
    fun saveDevice(typed: String, alias: String, group: String, password: String) {
        val target = Targets.normalise(typed)
        if (target.isEmpty()) {
            return
        }

        // What the recent list already knows about this machine: what it calls itself, and what it runs.
        // Without them a saved device is a nine-digit number with no icon, which is what the list looked
        // like the first time somebody saved one.
        val known = current.recent.firstOrNull { it.id.equals(target, ignoreCase = true) }
        val existing = current.favourites.firstOrNull { it.target.equals(target, ignoreCase = true) }
        val hostname = known?.name ?: existing?.note.orEmpty()
        val platform = known?.platform?.ifEmpty { null } ?: existing?.platform.orEmpty()

        update {
            // The group is registered as well as written on the device. That is what lets it outlive the
            // device, what makes an empty one possible, and what puts it on the wire: the sync sends
            // folders separately, and the database has a table for them.
            val withGroup = it.withGroup(group)
            if (withGroup.isFavourite(target)) {
                withGroup.withFavouriteEdited(target, alias, group, hostname)
            } else {
                withGroup.withFavourite(
                    Favourite(target, alias = alias, group = group, note = hostname, platform = platform),
                )
            }
        }

        if (password.isEmpty()) forgetPassword(target) else rememberPassword(target, password)
    }

    /**
     * Removes a saved machine, and the password saved with it.
     *
     * Both, because somebody removing a device means to be rid of it: leaving its password behind would be
     * keeping a secret for a machine the app no longer admits to knowing.
     */
    fun removeDevice(typed: String) {
        val target = Targets.normalise(typed)
        update { it.withoutFavourite(target) }
        forgetPassword(target)
    }

    fun toggleFavourite(target: String, alias: String = "", platform: String = "") {
        update {
            if (it.isFavourite(target)) {
                it.withoutFavourite(target)
            } else {
                it.withFavourite(Favourite(target, alias = alias, platform = platform))
            }
        }
    }

    /**
     * The password to try without asking, or null if there is none.
     *
     * Keyed on the normalised target so the grouped "699 372 765" a user pasted finds the same entry the
     * plain form stored.
     */
    fun rememberedPassword(typed: String): String? =
        passwords.get(Targets.normalise(typed)).takeUnless { it.isNullOrEmpty() }

    fun rememberPassword(typed: String, password: String) {
        passwords.remember(Targets.normalise(typed), password)
    }

    fun forgetPassword(typed: String) {
        passwords.forget(Targets.normalise(typed))
    }

    /**
     * Passwords that came out of a QR code, waiting for the connection they were scanned for.
     *
     * Not the encrypted store and not disk. The host spends the password in a code the first time anything
     * connects with it, so keeping one past that connection would only mean offering a dead password later.
     */
    private val scanned = mutableMapOf<String, String>()

    fun rememberScannedPassword(typed: String, password: String) {
        val target = Targets.normalise(typed)
        if (target.isNotEmpty() && password.isNotEmpty()) {
            scanned[target] = password
        }
    }

    /**
     * Makes a group with nothing in it yet.
     *
     * A group is a thing rather than a word written on a device: a row of its own in the portal's
     * database and a list of its own in the sync, which is what lets an empty one exist. Naming one while
     * filing a machine also makes one; this is the way round that does not need a machine.
     */
    /**
     * Forgets the device list, because it was the account's and this install no longer has one.
     *
     * Called on both sides of a sign-in, not only on sign-out: a list left behind is a list the next
     * account gets, which is how somebody who deleted an account and made a new one with the same address
     * found their old devices waiting. The portal had deleted them. This phone put them back.
     */
    fun forgetAccountData() = update { it.withoutAccountData() }

    fun addGroup(name: String) = update { it.withGroup(name) }

    fun renameGroup(from: String, to: String) = update { it.withGroupRenamed(from, to) }

    /** Removes a group. What was filed in it becomes unfiled rather than going with it. */
    fun removeGroup(name: String) = update { it.withoutGroup(name) }

    /**
     * Every group this list knows about.
     *
     * The named ones and the ones a device is filed in, because the two can differ: a group made and not
     * yet used is in the first only, and a list synced from a desktop can put a device in a group this
     * phone has not heard named.
     */
    val knownGroups: List<String>
        get() = (current.groupNames + current.favourites.map { it.group })
            .map { it.trim() }
            .filter { it.isNotEmpty() }
            .distinctBy { it.lowercase() }
            .sorted()

    /** The scanned password for this target, if one is waiting. Taking it uses it up. */
    fun takeScannedPassword(typed: String): String? = scanned.remove(Targets.normalise(typed))

    /**
     * The signalling server and key a QR code named, kept for the desk the code was for and for nobody
     * else.
     *
     * These used to be written into the settings, which made every scanned code a permanent change to
     * how this phone reaches every desk. A desktop whose own settings still held a server's previous key
     * put that key on every phone that scanned it, and the phones then refused every host -- including
     * ones they had reached fine a minute earlier -- until somebody found the field in Settings. A code
     * is one desk's directions; the settings are the person's. Not on disk: a self-hosted server that
     * should outlive a restart belongs in Settings, where it was typed on purpose.
     */
    private val scannedNetworks = mutableMapOf<String, NetworkDirectory>()

    fun rememberScannedNetwork(typed: String, rendezvousServer: String, serverPublicKeyBase64: String) {
        val target = Targets.normalise(typed)
        if (target.isNotEmpty() && (rendezvousServer.isNotEmpty() || serverPublicKeyBase64.isNotEmpty())) {
            scannedNetworks[target] = NetworkDirectory(rendezvousServer, serverPublicKeyBase64)
        }
    }

    /** The network a code named for this desk, or null to use the settings and the directory as usual. */
    fun scannedNetwork(typed: String): NetworkDirectory? = scannedNetworks[Targets.normalise(typed)]

    /**
     * Which saved desks are reachable right now, keyed by target, the way the desktop's device list shows
     * a dot beside each machine. Asked while a list is on screen, never stored: it is only true for a
     * moment.
     */
    private val presenceState = MutableStateFlow<Map<String, OnlineState>>(emptyMap())
    val presence: StateFlow<Map<String, OnlineState>> get() = presenceState
    private val peerPresence = PeerPresence()

    suspend fun refreshPresence() {
        val settings = current
        val targets = settings.favourites.map { it.target } + settings.recent.map { it.id }
        if (targets.isEmpty()) {
            presenceState.value = emptyMap()
            return
        }

        val directory = NetworkDirectoryClient.resolve(settings)
        presenceState.value = peerPresence.query(targets, directory.rendezvous)
    }
}
