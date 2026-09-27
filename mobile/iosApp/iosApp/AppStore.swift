import Foundation
import DeskPairShared

/// Everything the app knows before a session starts.
///
/// The Android app has the same object for the same reason: connecting needs the settings, and the settings
/// need to hear that a connection succeeded. Two owners pointing at each other is worse than one owning the
/// other, and none of this has a lifecycle of its own.
///
/// `@Published` rather than the Kotlin `StateFlow` behind it, because a generic `StateFlow<AppSettings>`
/// loses its element type crossing into Swift and a view that has to cast every read is a view that will
/// one day cast wrongly.
@MainActor
final class AppStore: ObservableObject {

    private let secrets = KeychainSecretStore()
    private let storage = FileSettingsStorage()
    private let store: SettingsStore

    @Published private(set) var settings: AppSettings

    /// Pins that survive the app closing, which is the only kind worth having.
    let pinnedKeys: PinnedKeyStore

    let passwords: RememberedPasswords

    /// The account this install is linked to, and the key it proves itself with.
    ///
    /// The key is generated on first use and kept in the same Keychain as the pins and the remembered
    /// passwords. A controller had no private key of its own until accounts existed; see `DeviceIdentity`.
    let account: AccountController

    /// Keeps the saved list in step with the account's.
    ///
    /// Best-effort and silent: a failed sync is the normal state of a phone on a train, and the list on
    /// screen works either way, which is the point of syncing something that was already local.
    let bookSync: AddressBookSync

    init() {
        store = SettingsStore(storage: storage, saveDelayMillis: 500)
        settings = store.current
        pinnedKeys = PersistentPinnedKeyStore(secrets: secrets)
        passwords = RememberedPasswords(secrets: secrets)

        let link = AccountLink(
            identity: DeviceIdentity(secrets: secrets),
            secrets: secrets,
            platform: "ios",
            clientFor: { PortalClient(baseUrl: $0) }
        )
        account = AccountController(link: link)
        bookSync = AddressBookSync(link: link, store: store, clientFor: { PortalClient(baseUrl: $0) })
    }

    /// Runs one exchange and republishes whatever came back, so the list on screen follows it.
    func syncBook() async {
        _ = try? await bookSync.sync()
        settings = store.current
    }

    func update(_ transform: @escaping (AppSettings) -> AppSettings) {
        store.update(transform: transform)
        settings = store.current
    }

    /// Writes anything the debounce is still holding, for when the app goes to the background.
    func flush() {
        store.flush()
    }

    /// Records a connection that worked, touching both lists the way `App.RememberPeer` does.
    func rememberConnection(target: String, name: String, platform: String) {
        let now = Int64(Date().timeIntervalSince1970 * 1000)
        update {
            $0.withRecent(id: target, name: name, platform: platform, nowMs: now)
                .touchFavourite(target: target, platform: platform, nowMs: now)
        }
    }

    /// Where a session keeps the resolution chosen for a peer's displays.
    func resolutionsFor(peer: String) -> ResolutionMemory {
        store.resolutionsFor(peer: Targets.shared.normalise(typed: peer))
    }

    func toggleFavourite(_ target: String, alias: String = "", platform: String = "") {
        update {
            $0.isFavourite(target: target)
                ? $0.withoutFavourite(target: target)
                // Every parameter spelled out: Kotlin's defaults do not cross into Swift. A machine saved
                // here starts unfiled, and lands in a folder when somebody puts it in one on a desktop.
                : $0.withFavourite(favourite: Favourite(
                    target: target, alias: alias, group: "", note: "", lastConnectedMs: 0, platform: platform))
        }
    }

    /// Saves a machine to the device list, or edits the one already there.
    ///
    /// The name and the password travel together because they are decided together, and because they live
    /// in different places: the name is a setting, the password is in the Keychain. A caller that had to
    /// remember both would one day write one and not the other.
    func saveDevice(_ target: String, alias: String, group: String, password: String?) {
        let normalised = Targets.shared.normalise(typed: target)
        guard !normalised.isEmpty else { return }

        // What the recent list already knows about this machine: what it calls itself, and what it runs.
        // Without them a saved device is a nine-digit number with no icon, which is what the list looked
        // like the first time somebody saved one.
        let known = settings.recent.first { $0.id == normalised }
        let existing = settings.favourites.first { $0.target == normalised }
        let hostname = known?.name ?? existing?.note ?? ""
        let platform = known?.platform.isEmpty == false
            ? known!.platform
            : existing?.platform ?? ""

        update { settings in
            // The group is registered as well as written on the device. That is what lets it outlive the
            // device, what makes an empty one possible, and what puts it on the wire: the sync sends
            // folders separately, and the database has a table for them.
            let filed = settings.withGroup(name: group)
            return filed.isFavourite(target: normalised)
                ? filed.withFavouriteEdited(target: normalised, alias: alias, group: group, note: hostname)
                : filed.withFavourite(favourite: Favourite(
                    target: normalised, alias: alias, group: group, note: hostname,
                    lastConnectedMs: 0, platform: platform))
        }

        guard let password else { return }
        if password.isEmpty {
            forgetPassword(normalised)
        } else {
            rememberPassword(normalised, password)
        }
    }

    /// Removes a saved machine, and the password saved with it.
    ///
    /// Both, because somebody removing a device means to be rid of it: leaving its password in the
    /// Keychain would be keeping a secret for a machine the app no longer admits to knowing.
    func removeDevice(_ target: String) {
        let normalised = Targets.shared.normalise(typed: target)
        update { $0.withoutFavourite(target: normalised) }
        forgetPassword(normalised)
    }

    func forgetRecent(_ id: String) {
        update { $0.withoutRecent(id: id) }
    }

    /// Passwords that came out of a QR code, waiting for the connection they were scanned for.
    ///
    /// Never written to the Keychain and never written to disk. The host spends the password in the code the
    /// first time anything connects with it, so keeping one would only mean offering a dead password later;
    /// [takeScannedPassword] hands it over once and forgets it.
    private var scannedPasswords: [String: String] = [:]

    func rememberScannedPassword(_ typed: String, _ password: String) {
        let target = Targets.shared.normalise(typed: typed)
        guard !target.isEmpty, !password.isEmpty else { return }
        scannedPasswords[target] = password
    }

    /// The scanned password for this target, if one is waiting. Taking it uses it up.
    func takeScannedPassword(_ typed: String) -> String? {
        scannedPasswords.removeValue(forKey: Targets.shared.normalise(typed: typed))
    }

    /// The signalling server and key a QR code named, kept for the desk the code was for and for nobody
    /// else.
    ///
    /// These used to be written into the settings, which made every scanned code a permanent change to how
    /// this phone reaches every desk. A desktop whose own settings still held a server's previous key put
    /// that key on every phone that scanned it, and the phones then refused every host until somebody
    /// found the field in Settings. A code is one desk's directions; the settings are the person's. Not on
    /// disk: a self-hosted server that should outlive a restart belongs in Settings, where it was typed on
    /// purpose.
    private var scannedNetworks: [String: NetworkDirectory] = [:]

    func rememberScannedNetwork(_ typed: String, rendezvousServer: String, serverPublicKeyBase64: String) {
        let target = Targets.shared.normalise(typed: typed)
        guard !target.isEmpty, !rendezvousServer.isEmpty || !serverPublicKeyBase64.isEmpty else { return }
        scannedNetworks[target] = NetworkDirectory(rendezvous: rendezvousServer, publicKey: serverPublicKeyBase64)
    }

    /// The network a code named for this desk, or nil to use the settings and the directory as usual.
    func scannedNetwork(_ typed: String) -> NetworkDirectory? {
        scannedNetworks[Targets.shared.normalise(typed: typed)]
    }

    /// Which saved desks are reachable right now, keyed by target, the way the desktop's device list shows
    /// a dot beside each machine. Asked while a list is on screen, never stored: it is only true for a
    /// moment.
    @Published private(set) var presence: [String: OnlineState] = [:]
    private let peerPresence = PeerPresence()

    @MainActor
    func refreshPresence() async {
        let current = settings
        let targets = current.favourites.map { $0.target } + current.recent.map { $0.id }
        if targets.isEmpty {
            presence = [:]
            return
        }

        do {
            let directory = try await NetworkDirectoryClient.shared.resolve(settings: current)
            let answer = try await peerPresence.query(targets: targets, rendezvousServer: directory.rendezvous)
            presence = answer
        } catch {
            // Best-effort: the dots stay as they were.
        }
    }

    func onlineState(_ target: String) -> OnlineState {
        presence[Targets.shared.normalise(typed: target)] ?? .unknown
    }

    /// The password to try without asking, or nil if there is none.
    func rememberedPassword(_ typed: String) -> String? {
        let stored = passwords.get(target: Targets.shared.normalise(typed: typed))
        return (stored?.isEmpty ?? true) ? nil : stored
    }

    func rememberPassword(_ typed: String, _ password: String) {
        passwords.remember(target: Targets.shared.normalise(typed: typed), password: password)
    }

    func forgetPassword(_ typed: String) {
        passwords.forget(target: Targets.shared.normalise(typed: typed))
    }

    var trustedHosts: [PinnedHost] { pinnedKeys.all() }

    var rememberedPasswordTargets: [String] { passwords.targets() }

    /// Forgets the device list, because it was the account's and this install no longer has one.
    ///
    /// Called on both sides of a sign-in, not only on sign-out: a list left behind is a list the next
    /// account gets, which is how somebody who deleted an account and made a new one with the same
    /// address found their old devices waiting. The portal had deleted them. This phone put them back.
    func forgetAccountData() {
        update { $0.withoutAccountData() }
    }

    /// Makes a group with nothing in it yet.
    ///
    /// A group is a thing rather than a word written on a device: it is a row of its own in the portal's
    /// database and a list of its own in the sync, which is what lets an empty one exist at all. Naming a
    /// group while filing a machine also makes one; this is the way round that does not need a machine.
    func addGroup(_ name: String) {
        update { $0.withGroup(name: name) }
    }

    func renameGroup(_ from: String, to: String) {
        update { $0.withGroupRenamed(from: from, to: to) }
    }

    /// Removes a group. What was filed in it becomes unfiled rather than disappearing with it.
    func removeGroup(_ name: String) {
        update { $0.withoutGroup(name: name) }
    }

    /// Every group the saved list already uses, for the sheet to offer.
    var knownGroups: [String] {
        // The named ones and the ones a device is filed in, because the two can differ: a group made and
        // not yet used is in the first only, and a list synced from a desktop can put a device in a group
        // this phone has not heard named.
        Set(settings.groupNames.map { $0.trimmingCharacters(in: .whitespaces) })
            .union(settings.favourites.map { $0.group.trimmingCharacters(in: .whitespaces) })
            .filter { !$0.isEmpty }
            .sorted { $0.localizedCaseInsensitiveCompare($1) == .orderedAscending }
    }
}
