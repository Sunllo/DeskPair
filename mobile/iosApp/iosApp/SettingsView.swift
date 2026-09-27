import Foundation
import SwiftUI
import UIKit
import DeskPairShared

/// Settings, in five sections rather than the desktop's seven.
///
/// Five of the desktop's tabs are `HostConfig` — approve mode, temporary passwords, direct-access ports,
/// capture permissions — and a controller-only client has no host config at all. What is left is what this
/// phone decides for itself, plus the two lists that are the only reason a security section exists here:
/// what it trusts, and what it holds a password for.
///
/// Saved as typed, debounced in the shared `SettingsStore` the way `SettingsViewModel` does it. No Save
/// button and no restart banner: nothing here is baked in at start-up the way the desktop's engine is.
///
/// Every row leads with a symbol and names itself in two or three words; the sentence that explains it goes
/// underneath in grey. Labels that were themselves sentences — "Show the remote pointer as well as yours" —
/// wrapped to two lines on a phone and to three at a larger font scale, and pushed the control they belonged
/// to out of line with every other control on the screen.
struct SettingsView: View {
    @ObservedObject var store: AppStore

    @State private var notice: String?
    @State private var noticeIsGood = false
    @State private var keyText = ""
    @State private var fetching = false

    // The account sheet. The password is held only while the sheet is open, and cleared the moment it has
    // worked; what survives a sign-in is the device token, the same one the console's code flow produces.
    @State private var account = AccountUiState(
        link: LinkState.companion.UNLINKED, busy: false, message: .none, detail: "", selfRegistration: false)
    @State private var email = ""
    @State private var password = ""
    @State private var registering = false
    @State private var accountOpen = false

    /// Which field the keyboard belongs to, so Return moves on rather than dismissing it.
    @FocusState private var accountField: AccountField?

    /// How tall the account panel actually is, so the sheet is that tall and no taller.
    @State private var accountSheetHeight: CGFloat = 260

    private var settings: AppSettings { store.settings }

    var body: some View {
        Form {
            // The account comes first, and it says what an account says: who you are and how many
            // machines are saved. What used to be here instead was the machinery of linking -- a portal
            // address, a name for this handset, an eight-character code -- none of which is a fact about
            // an account, and all of which is typed once and never looked at again. It lives behind this
            // row now.
            Section {
                Button { accountOpen = true } label: { accountRow }
                    .buttonStyle(.plain)
            }

            Section(String(localized: "settings.section.general")) {
                // First, because it is the one thing on this screen that is about this handset rather than
                // about how it behaves -- and because it used to be collected in the middle of signing in,
                // which is the one moment nobody is thinking about what to call their phone.
                LabeledContent {
                    TextField(Self.defaultDeviceName, text: deviceNameBinding)
                        .multilineTextAlignment(.trailing)
                } label: {
                    Label(String(localized: "settings.deviceName"), systemImage: "iphone")
                }

                Picker(selection: languageBinding) {
                    Text("settings.language.system").tag(AppLanguages.shared.SYSTEM)
                    ForEach(AppLanguages.shared.all, id: \.code) { language in
                        Text(verbatim: language.nativeName).tag(language.code)
                    }
                } label: {
                    Label(String(localized: "settings.language"), systemImage: "globe")
                }
                // A menu of ten would open over the row; a push shows them full width with the current
                // one ticked, which is what every other settings screen on the phone does.
                .pickerStyle(.navigationLink)
                if settings.language != AppLanguages.shared.SYSTEM {
                    Text("settings.language.restart")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                }

                Stepper(value: recentLimitBinding, in: 1...200) {
                    HStack {
                        SettingLabel(
                            "settings.recentLimit",
                            hint: "settings.recentLimitHint",
                            symbol: "clock.arrow.circlepath"
                        )
                        Spacer()
                        Text("\(settings.recentLimit)").foregroundStyle(.secondary)
                    }
                }

                if !settings.recent.isEmpty {
                    Button(role: .destructive) {
                        store.update { $0.withNoRecent() }
                    } label: {
                        Label(String(localized: "settings.clearRecent"), systemImage: "trash")
                    }
                }
            }

            Section(String(localized: "settings.section.session")) {
                Picker(selection: qualityBinding) {
                    Text("session.quality.low").tag("low")
                    Text("session.quality.balanced").tag("balanced")
                    Text("session.quality.best").tag("best")
                    Text("settings.quality.custom").tag("custom")
                } label: {
                    SettingLabel(
                        "settings.defaultQuality",
                        hint: "settings.defaultQualityHint",
                        symbol: "slider.horizontal.3"
                    )
                }
                .pickerStyle(.navigationLink)

                // Offering "Custom" with nothing to customise was a dead end: the Android screen has had
                // these two since the setting existed, and iOS had the choice but not the controls.
                if settings.defaultQuality == "custom" {
                    NumberField(
                        "settings.bitrate",
                        symbol: "speedometer",
                        value: numberBinding(
                            get: { Int($0.customBitrateKbps) },
                            set: { $0.withCustomBitrateKbps(value: Int32($1)) }
                        ),
                        range: 100...200_000,
                        invalid: "settings.bitrateInvalid"
                    )
                    NumberField(
                        "settings.fps",
                        symbol: "video",
                        value: numberBinding(
                            get: { Int($0.customFps) },
                            set: { $0.withCustomFps(value: Int32($1)) }
                        ),
                        range: 5...120,
                        invalid: "settings.fpsInvalid"
                    )
                }

                Picker(selection: pointerBinding) {
                    Text("mode.touch").tag("touch")
                    Text("mode.mouse").tag("mouse")
                } label: {
                    Label(String(localized: "settings.pointerMode"), systemImage: "hand.tap")
                }
                .pickerStyle(.navigationLink)
            }

            Section {
                Toggle(isOn: boolBinding(get: { $0.showRemoteCursor }, set: { $0.withShowRemoteCursor(value: $1) })) {
                    SettingLabel("session.remoteCursor", hint: "session.remoteCursorHint", symbol: "cursorarrow")
                }
                Toggle(isOn: boolBinding(get: { $0.fitToWindow }, set: { $0.withFitToWindow(value: $1) })) {
                    SettingLabel(
                        "settings.fitToWindow",
                        hint: "settings.fitToWindowHint",
                        symbol: "arrow.up.left.and.arrow.down.right"
                    )
                }
                Toggle(isOn: boolBinding(get: { $0.losslessRefinement }, set: { $0.withLosslessRefinement(value: $1) })) {
                    SettingLabel("settings.lossless", hint: "settings.losslessHint", symbol: "wand.and.stars")
                }
                Toggle(isOn: boolBinding(get: { $0.audioEnabled }, set: { $0.withAudioEnabled(value: $1) })) {
                    SettingLabel("settings.audio", hint: "settings.audioHint", symbol: "speaker.wave.2")
                }
                // Both of these the host has always honoured and no client here has ever asked for.
                Toggle(isOn: boolBinding(get: { $0.viewOnly }, set: { $0.withViewOnly(value: $1) })) {
                    SettingLabel("settings.viewOnly", hint: "settings.viewOnlyHint", symbol: "eye")
                }
                Toggle(isOn: boolBinding(
                    get: { $0.lockAfterSessionEnd },
                    set: { $0.withLockAfterSessionEnd(value: $1) }
                )) {
                    SettingLabel("settings.lockAfterEnd", hint: "settings.lockAfterEndHint", symbol: "lock")
                }
            }

            Section {
                // Off, there is nothing here about where the servers are, because there is nothing here
                // for that person to decide. On, the two fields below are theirs.
                Toggle(isOn: Binding(
                    get: { !settings.useDirectoryServers },
                    set: { own in
                        store.update {
                            // Clearing on the way back matters: a stored address would silently win over
                            // whatever the portal answers, from a field no longer on screen.
                            own
                                ? $0.withDirectoryServers(value: false)
                                : $0.withDirectoryServers(value: true)
                                    .withRendezvousServer(value: "")
                                    .withServerPublicKey(value: "")
                        }
                        if !own { keyText = "" }
                    }
                )) {
                    SettingLabel("settings.selfHosted", hint: nil, symbol: "server.rack")
                }

                if settings.useDirectoryServers {
                    Text("settings.directoryHint").font(.caption).foregroundStyle(.secondary)
                }

                if !settings.useDirectoryServers {
                LabeledContent {
                    TextField(String(localized: "settings.server"), text: serverBinding)
                        .autocorrectionDisabled()
                        .textInputAutocapitalization(.never)
                        .keyboardType(.URL)
                        .multilineTextAlignment(.trailing)
                } label: {
                    Label(String(localized: "settings.server"), systemImage: "server.rack")
                }

                VStack(alignment: .leading, spacing: 4) {
                    Label(String(localized: "settings.serverKey"), systemImage: "key")
                    TextField(String(localized: "settings.serverKey"), text: $keyText, axis: .vertical)
                        .autocorrectionDisabled()
                        .textInputAutocapitalization(.never)
                        .font(.caption.monospaced())
                        .lineLimit(1...3)
                        .onChange(of: keyText) { _, value in
                            // Only a key that could actually be used is written; a half-pasted one stays on
                            // screen, as `StorableKey` does on the desktop.
                            if ServerKey.shared.isValid(value: value) {
                                let cleaned = ServerKey.shared.clean(value: value)
                                store.update { $0.withServerPublicKey(value: cleaned) }
                            }
                        }

                    if !keyText.isEmpty && !ServerKey.shared.isValid(value: keyText) {
                        Text("settings.keyInvalid").font(.caption).foregroundStyle(.red)
                    } else if keyText.isEmpty {
                        Text("settings.keyMissing").font(.caption).foregroundStyle(.secondary)
                    }
                }

                // Close to mandatory on a phone: nobody is pasting a ninety-one byte SPKI with their thumbs.
                Button(action: fetchKey) {
                    Label(String(localized: "settings.fetchKey"), systemImage: "arrow.down.circle")
                }
                .disabled(fetching)

                // Here rather than on the account screen. It is a server address, like the one above it,
                // and the only thing a person signing in wants to see is where to type their email. It
                // stays because the front page says every server can be your own, and this is where
                // somebody who took that up points the app at theirs.
                LabeledContent {
                    TextField(String(localized: "settings.account.portal"), text: portalBinding)
                        .autocorrectionDisabled()
                        .textInputAutocapitalization(.never)
                        .keyboardType(.URL)
                        .multilineTextAlignment(.trailing)
                } label: {
                    Label(String(localized: "settings.account.portal"), systemImage: "cloud")
                }
                }
            } header: {
                Text("settings.section.network")
            } footer: {
                // Said where the button is, not at the top of a form the user has scrolled past.
                if let notice {
                    Text(notice).foregroundStyle(noticeIsGood ? Color.accentColor : .red)
                }
            }

            Section {
                Toggle(isOn: boolBinding(get: { $0.udpMedia }, set: { $0.withUdpMedia(value: $1) })) {
                    SettingLabel("settings.udpMedia", hint: "settings.udpMediaHint", symbol: "bolt")
                }
                Toggle(isOn: boolBinding(get: { $0.forceRelay }, set: { $0.withForceRelay(value: $1) })) {
                    SettingLabel("settings.forceRelay", hint: "settings.forceRelayHint", symbol: "arrow.triangle.branch")
                }
            }

            // The trusted computers and the remembered passwords used to be listed here.
            //
            // Passwords belong to the machine they open, and are edited on it now, in the device list.
            // Pinned keys were only ever here as a way out of one situation -- a host reinstalled, so its
            // key no longer matches and every connection is refused -- and a list of fingerprints is a
            // strange place to solve that. It is solved where it happens: the connection that fails says
            // the key changed and offers to trust the new one. See SessionModel.trustChangedKey.

            Section(String(localized: "settings.section.about")) {
                LabeledContent {
                    Text(Self.version).foregroundStyle(.secondary)
                } label: {
                    Label(String(localized: "settings.version"), systemImage: "info.circle")
                }

                // No path on screen. Where this app keeps its settings is not something anybody reading
                // this screen needs, and on a phone it is not even somewhere they can go.
            }
        }
        .navigationTitle(Text("settings.title"))
        .navigationBarTitleDisplayMode(.inline)
        .sheet(isPresented: $accountOpen) { accountSheet }
        .onAppear {
            keyText = settings.serverPublicKeyBase64
            account = store.account.snapshot
        }
        .task {
            // Says whether the link is still good, without holding the screen up for it. A suspend
            // function always exports as `throws`; the only thing it can actually throw is cancellation,
            // because every portal failure is caught and reported as state.
            try? await store.account.refresh()
            account = store.account.snapshot
        }
    }

    private func fetchKey() {
        let server = settings.rendezvousServer
        guard !server.isEmpty else {
            notice = String(localized: "settings.serverRequired")
            noticeIsGood = false
            return
        }

        fetching = true
        notice = nil
        Task {
            do {
                let key = try await ServerKey.shared.fetch(rendezvousServer: server, timeoutMillis: 8_000)
                keyText = key
                store.update { $0.withServerPublicKey(value: key) }
                notice = String(localized: "settings.keyFetched")
                noticeIsGood = true
            } catch {
                notice = error.localizedDescription
                noticeIsGood = false
            }
            fetching = false
        }
    }

    private static var version: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "—"
    }

    // MARK: - Bindings
    //
    // Each one reads from the store and writes a whole new settings value, so the debounce in the shared
    // SettingsStore is the only thing that ever touches the file.

    private func boolBinding(
        get: @escaping (AppSettings) -> Bool,
        set: @escaping (AppSettings, Bool) -> AppSettings
    ) -> Binding<Bool> {
        Binding(get: { get(settings) }, set: { value in store.update { set($0, value) } })
    }

    private func numberBinding(
        get: @escaping (AppSettings) -> Int,
        set: @escaping (AppSettings, Int) -> AppSettings
    ) -> Binding<Int> {
        Binding(get: { get(settings) }, set: { value in store.update { set($0, value) } })
    }

    /// Remembered in the shared settings and applied through the one setting iOS honours for an app's
    /// language, which takes effect at the next launch. This used to be stored and read by nothing.
    private var languageBinding: Binding<String> {
        Binding(
            get: { settings.language },
            set: { v in
                store.update { $0.withLanguage(value: v) }
                if v == AppLanguages.shared.SYSTEM {
                    UserDefaults.standard.removeObject(forKey: "AppleLanguages")
                } else {
                    UserDefaults.standard.set([v], forKey: "AppleLanguages")
                }
            }
        )
    }

    private var qualityBinding: Binding<String> {
        Binding(get: { settings.defaultQuality }, set: { v in store.update { $0.withDefaultQuality(value: v) } })
    }

    private var pointerBinding: Binding<String> {
        Binding(get: { settings.pointerMode }, set: { v in store.update { $0.withPointerMode(value: v) } })
    }

    private var serverBinding: Binding<String> {
        Binding(
            get: { settings.rendezvousServer },
            set: { v in
                let trimmed = v.trimmingCharacters(in: .whitespaces)
                store.update { $0.withRendezvousServer(value: trimmed) }
            }
        )
    }

    private var portalBinding: Binding<String> {
        Binding(
            get: { store.settings.portalServer },
            set: { v in store.update { $0.withPortalServer(value: v.trimmingCharacters(in: .whitespaces)) } }
        )
    }

    /// Turns what the shared controller reports into a sentence in the user's language.
    ///
    /// Who this install belongs to, as the top row of the settings list.
    private var accountRow: some View {
        HStack(spacing: 14) {
            Image(systemName: account.link.isLinked ? "person.crop.circle.fill" : "person.crop.circle")
                .font(.system(size: 38))
                .foregroundStyle(account.link.isLinked ? AnyShapeStyle(.tint) : AnyShapeStyle(.secondary))
            VStack(alignment: .leading, spacing: 3) {
                Text(account.link.isLinked
                     ? account.link.account
                     : String(localized: "settings.account.signedOut"))
                    .font(.headline)
                Text(account.link.isLinked
                     ? String(format: String(localized: "settings.account.devices"), settings.favourites.count)
                     : String(localized: "settings.account.signedOutHint"))
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(.vertical, 6)
    }

    /// Signing in, as a panel from the bottom rather than a page from the side.
    ///
    /// Deliberately short: a sheet tall enough to hold a Form is a sheet the keyboard has to fight, and a
    /// grouped list is mostly padding when what it holds is two fields and a button. This is sized to its
    /// own contents, so it sits above the keyboard rather than under it, and it grows if somebody's text
    /// is larger rather than hiding the button behind the keys.
    private var accountSheet: some View {
        VStack(alignment: .leading, spacing: 14) {
            // Centred, where a sheet's title goes: it names the panel rather than labelling the row under
            // it, and left-aligned it read as the first of the things in the list. It says what the panel
            // is for, which is not the same in both of its states.
            Text(account.link.isLinked ? "settings.section.account" : "settings.account.signInTitle")
                .font(.headline)
                .frame(maxWidth: .infinity, alignment: .center)

            if account.link.isLinked {
                HStack(spacing: 12) {
                    Image(systemName: "person.crop.circle")
                        .foregroundStyle(.secondary)
                        .frame(width: 24)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(account.link.account)
                        if !account.link.alias.isEmpty {
                            Text(account.link.alias).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    Spacer(minLength: 0)
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 11)
                .background(Color(.secondarySystemGroupedBackground))
                .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))

                Button(role: .destructive) {
                    Task {
                        try? await store.account.unlink()
                        // The list goes with the account it belonged to.
                        store.forgetAccountData()
                        account = store.account.snapshot
                    }
                } label: {
                    Text("settings.account.unlink").frame(maxWidth: .infinity)
                }
                .buttonStyle(.bordered)
                .controlSize(.large)
                .disabled(account.busy)
            } else {
                // The grouped-list look, laid out here rather than taken from a Form: the look is worth
                // keeping and the Form's section padding is what made this sheet too tall to share with a
                // keyboard. A bordered box would have been less work and looks like a web page.
                //
                // A card each rather than two rows under one hairline. They are two separate things to
                // answer, and a hairline reads as "these belong together" -- which is right for a list of
                // related settings and wrong for a question and its secret.
                accountFieldRow(symbol: "envelope") {
                    TextField(String(localized: "settings.account.email"), text: $email)
                        .autocorrectionDisabled()
                        .textInputAutocapitalization(.never)
                        .keyboardType(.emailAddress)
                        .textContentType(.username)
                        .focused($accountField, equals: .email)
                        .submitLabel(.next)
                        .onSubmit { accountField = .password }
                }

                accountFieldRow(symbol: "key") {
                    SecureField(String(localized: "settings.account.password"), text: $password)
                        .textContentType(registering ? .newPassword : .password)
                        .focused($accountField, equals: .password)
                        // The keyboard's own key signs in, because with a keyboard up any button on
                        // the screen is further from the thumb than the key it rests on.
                        .submitLabel(.go)
                        .onSubmit(signIn)
                }

                Button(action: signIn) {
                    // The frame goes on the label, not the button: outside it the button hugs its text.
                    Text("settings.account.signIn").frame(maxWidth: .infinity)
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                // Only while a request is in flight: a control that is inert before it is pressed cannot
                // say why, and the controller below already reports which field is missing.
                .disabled(account.busy)

                if account.selfRegistration {
                    // Second, and quieter. Somebody who has an account comes here far more often than
                    // somebody making one, and two buttons of equal weight ask a question twice.
                    Button(action: register) {
                        Text("settings.account.register").frame(maxWidth: .infinity)
                    }
                    .controlSize(.large)
                    .disabled(account.busy)
                }
            }

            if let message = accountMessage {
                Text(message)
                    .font(.footnote)
                    .foregroundStyle(account.isGood ? Color.accentColor : .red)
            }
        }
        .padding(.horizontal, 20)
        .padding(.top, 20)
        .padding(.bottom, 24)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background {
            // Measured rather than guessed at. A fixed height is a height that is wrong at the text sizes
            // somebody actually uses, and being wrong here means the button is behind the keyboard.
            GeometryReader { proxy in
                Color.clear.onChange(of: proxy.size.height, initial: true) { _, height in
                    accountSheetHeight = height
                }
            }
        }
        .presentationBackground(Color(.systemGroupedBackground))
        .presentationDetents([.height(accountSheetHeight)])
        .presentationDragIndicator(.visible)
        .task {
            // What this portal will let the app offer. Quiet on failure; the panel works either way.
            try? await store.account.loadOptions(portalUrl: store.settings.portalServer)
            account = store.account.snapshot
        }
    }

    private enum AccountField { case email, password }

    /// One row of the card: a symbol in a fixed column, then whatever is being typed into.
    ///
    /// The width is fixed rather than fitted so the two fields line up with each other, which is the whole
    /// reason a settings list reads as a column rather than as two rows that happen to be stacked.
    private func accountFieldRow(symbol: String, @ViewBuilder field: () -> some View) -> some View {
        HStack(spacing: 12) {
            Image(systemName: symbol)
                .foregroundStyle(.secondary)
                .frame(width: 24)
            field()
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 13)
        .background(Color(.secondarySystemGroupedBackground))
        .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
    }

    private func signIn() {
        registering = false
        accountField = nil
        Task {
            try? await store.account.signIn(
                portalUrl: store.settings.portalServer,
                email: email,
                password: password,
                alias: deviceName
            )
            finishAccountAction()
        }
    }

    private func register() {
        registering = true
        accountField = nil
        Task {
            try? await store.account.register(
                portalUrl: store.settings.portalServer,
                email: email,
                password: password,
                name: "",
                alias: deviceName,
                // The language this app is actually running in, not the phone's region: somebody reading
                // DeskPair in English should not be mailed in Chinese because of where they live.
                language: Locale.preferredLanguages.first ?? "en"
            )
            finishAccountAction()
        }
    }

    /// Reads the result back and closes the sheet once there is an account to close it on.
    private func finishAccountAction() {
        account = store.account.snapshot
        if account.link.isLinked {
            password = ""
            accountOpen = false
        }
    }

    /// What this handset is called in the account's device list, with a sensible first answer.
    private var deviceName: String {
        settings.deviceName.isEmpty ? Self.defaultDeviceName : settings.deviceName
    }

    /// The model, which is what somebody would have typed anyway.
    static let defaultDeviceName = UIDevice.current.name

    private var deviceNameBinding: Binding<String> {
        Binding(
            get: { settings.deviceName },
            set: { v in store.update { $0.withDeviceName(value: v) } }
        )
    }


    /// The shared layer has no string table, so it reports an `AccountMessage` rather than English. The one
    /// exception is `portalSaid`, where the portal's own words pass through: a case per server error would
    /// leave the app mute about any error it had not been taught.
    private var accountMessage: String? {
        switch account.message {
        case .none: return nil
        // .codeRequired is unreachable from this app now that the code field is gone; the shared layer
        // still reports it because the desktop still has that route.
        case .emailRequired: return String(localized: "settings.account.needEmail")
        case .passwordRequired: return String(localized: "settings.account.needPassword")
        case .verifyEmail: return String(format: String(localized: "settings.account.verify"), account.detail)
        case .badCredentials: return String(localized: "account.error.badCredentials")
        case .notVerified: return String(localized: "account.error.notVerified")
        case .disabled: return String(localized: "account.error.disabled")
        case .registrationClosed: return String(localized: "account.error.closed")
        case .emailTaken: return String(localized: "account.error.emailTaken")
        case .weakPassword: return String(localized: "account.error.weakPassword")
        case .badEmail: return String(localized: "account.error.badEmail")
        case .tooManyAttempts: return String(localized: "account.error.tooMany")
        case .linked: return String(format: String(localized: "settings.account.linked"), account.detail)
        case .unlinked: return String(localized: "settings.account.unlinked")
        case .unlinkedLocally: return String(format: String(localized: "settings.account.unlinkedLocally"), account.detail)
        case .portalSaid: return account.detail.isEmpty ? nil : account.detail
        default: return nil
        }
    }

    private var recentLimitBinding: Binding<Int> {
        Binding(
            get: { Int(settings.recentLimit) },
            set: { v in store.update { $0.withRecentLimit(value: Int32(v)) } }
        )
    }
}

/// A short name, the sentence that explains it, and the symbol that leads the row.
private struct SettingLabel: View {
    private let key: LocalizedStringKey
    private let hint: LocalizedStringKey?
    private let symbol: String

    init(_ key: LocalizedStringKey, hint: LocalizedStringKey? = nil, symbol: String) {
        self.key = key
        self.hint = hint
        self.symbol = symbol
    }

    var body: some View {
        Label {
            VStack(alignment: .leading, spacing: 2) {
                Text(key)
                if let hint {
                    Text(hint).font(.caption).foregroundStyle(.secondary)
                }
            }
        } icon: {
            Image(systemName: symbol)
        }
    }
}

/// A bounded integer.
///
/// Nothing out of range ever reaches the settings, which is what `NumericField` is for on the desktop: a
/// half-typed "1" on the way to "120" must not be stored as a frame rate of one.
private struct NumberField: View {
    private let key: LocalizedStringKey
    private let symbol: String
    private let value: Binding<Int>
    private let range: ClosedRange<Int>
    private let invalid: LocalizedStringKey

    @State private var text: String

    init(
        _ key: LocalizedStringKey,
        symbol: String,
        value: Binding<Int>,
        range: ClosedRange<Int>,
        invalid: LocalizedStringKey
    ) {
        self.key = key
        self.symbol = symbol
        self.value = value
        self.range = range
        self.invalid = invalid
        _text = State(initialValue: String(value.wrappedValue))
    }

    private var parsed: Int? { Int(text) }
    private var bad: Bool { parsed.map { !range.contains($0) } ?? true }

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            LabeledContent {
                TextField("", text: $text)
                    .keyboardType(.numberPad)
                    .multilineTextAlignment(.trailing)
                    .onChange(of: text) { _, new in
                        let digits = String(new.filter(\.isNumber).prefix(6))
                        if digits != new { text = digits }
                        if let n = Int(digits), range.contains(n) { value.wrappedValue = n }
                    }
            } label: {
                Label(key, systemImage: symbol)
            }

            if bad {
                Text(invalid).font(.caption).foregroundStyle(.red)
            }
        }
    }
}
