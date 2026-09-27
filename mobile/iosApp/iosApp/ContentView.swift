import PhotosUI
import SwiftUI
import DeskPairShared

struct ContentView: View {
    // Both are built in init so the session model can be handed the very store the views observe;
    // a default initialiser here would construct one and then throw it away.
    @StateObject private var store: AppStore
    @StateObject private var model: SessionModel

    /// Which tab is showing.
    ///
    /// Its initial value can be named in the environment, because the simulator has no way to tap one from
    /// a script and a screen nobody can reach is a screen nobody can check.
    @State private var tab = Tab(named: ProcessInfo.processInfo.environment["SUNLLO_TEST_TAB"])

    /// The one prompt the tabs share.
    ///
    /// Starting a connection is the app's job rather than a screen's: both the connect tab and the
    /// favourites tab open sessions, and a password prompt owned by one of them would disappear the moment
    /// the user changed tab underneath it.
    @State private var asking: String?

    /// Whether the password being asked for opens a terminal rather than the desktop.
    @State private var askingTerminal = false

    @Environment(\.scenePhase) private var scenePhase

    init() {
        let store = AppStore()
        _store = StateObject(wrappedValue: store)
        _model = StateObject(wrappedValue: SessionModel(store: store))
    }

    var body: some View {
        content
            .onAppear(perform: connectIfAutomated)
            .onChange(of: scenePhase) { _, phase in
                // A settings change made a moment before the app is backgrounded would otherwise be lost
                // if the process is killed while the debounce is still running.
                if phase != .active { store.flush() }
            }
    }

    /// The simulator has no way to type into a text field from a script, so a driven run is told where to go
    /// through the environment. The server and key are settings now, not connection arguments, so a scripted
    /// run writes them the way the settings screen does — which keeps the automation on a real user's path.
    ///
    ///   SIMCTL_CHILD_SUNLLO_TEST_HOST=127.0.0.1:21118 xcrun simctl launch booted com.sunllo.deskpair
    private func connectIfAutomated() {
        let environment = ProcessInfo.processInfo.environment

        let server = environment["SUNLLO_TEST_RENDEZVOUS"] ?? ""
        let key = environment["SUNLLO_TEST_SERVER_KEY"] ?? ""
        if !server.isEmpty || !key.isEmpty {
            store.update {
                $0.withRendezvousServer(value: server.isEmpty ? $0.rendezvousServer : server)
                    .withServerPublicKey(value: key.isEmpty ? $0.serverPublicKeyBase64 : key)
            }
        }

        let target = environment["SUNLLO_TEST_ID"].flatMap { $0.isEmpty ? nil : $0 }
            ?? environment["SUNLLO_TEST_HOST"].flatMap { $0.isEmpty ? nil : $0 }
        guard let target else { return }

        // A terminal instead of the desktop. Anything other than "1" is typed into the shell once it
        // opens, followed by Return: the simulator cannot be typed into from a script either.
        if let terminal = environment["SUNLLO_TEST_TERMINAL"], !terminal.isEmpty {
            model.openTerminal(
                target: target,
                password: environment["SUNLLO_TEST_PASSWORD"] ?? "",
                typing: terminal == "1" ? nil : terminal)
            return
        }

        model.connect(target: target, password: environment["SUNLLO_TEST_PASSWORD"] ?? "")
    }

    /// Connects, or asks for a password first.
    ///
    /// A password that came out of a QR code is used first and used once: the whole reason the code carries
    /// one is so that scanning is the entire ceremony, and the host has already thrown it away by the time
    /// anybody could ask for it again. After that a remembered password is used without a prompt — that is
    /// the point of remembering it — and a target nobody has reached yet asks, because only the host knows
    /// whether it needs anything.
    private func start(_ target: String, terminal: Bool = false) {
        let normalised = Targets.shared.normalise(typed: target)
        guard !normalised.isEmpty else { return }

        if let known = store.takeScannedPassword(normalised) ?? store.rememberedPassword(normalised) {
            if terminal {
                model.openTerminal(target: normalised, password: known)
            } else {
                model.connect(target: normalised, password: known, remember: false)
            }
        } else {
            askingTerminal = terminal
            asking = normalised
        }
    }

    @ViewBuilder
    private var content: some View {
        switch model.phase {
        case .disconnected(let error):
            // The tab bar exists only here. A session takes the window: there is no navigating away from a
            // remote desk to look at settings, and the controls that belong to it are its own.
            TabView(selection: $tab) {
                NavigationStack {
                    ConnectView(
                        store: store,
                        error: error,
                        canRetry: model.canReconnect,
                        onStart: { start($0) },
                        onTerminal: { start($0, terminal: true) },
                        onRetry: { model.reconnect() }
                    )
                }
                .tabItem { Label(String(localized: "nav.connect"), systemImage: "display") }
                .tag(Tab.connect)

                NavigationStack {
                    FavouritesView(store: store, onStart: { start($0) })
                }
                .tabItem { Label(String(localized: "nav.favourites"), systemImage: "heart") }
                .tag(Tab.favourites)

                NavigationStack {
                    SettingsView(store: store)
                }
                .tabItem { Label(String(localized: "nav.settings"), systemImage: "gearshape") }
                .tag(Tab.settings)
            }
            .task {
                // The saved list follows the account, when there is one. Once now, then every fifteen
                // minutes: the timer is the only thing that can notice what somebody else changed.
                while !Task.isCancelled {
                    await store.syncBook()
                    try? await Task.sleep(for: .seconds(15 * 60))
                }
            }
            .onChange(of: store.settings.favourites) {
                // Three seconds after an edit here, so a burst is one exchange. A pull changes the list and
                // so fires this once more, which then finds nothing and stops.
                Task {
                    try? await Task.sleep(for: .seconds(3))
                    await store.syncBook()
                }
            }
            .sheet(item: $asking) { target in
                PasswordSheet(
                    target: target,
                    offerToRemember: store.settings.rememberPasswords,
                    onCancel: { asking = nil },
                    onConnect: { password, remember in
                        asking = nil
                        if askingTerminal {
                            model.openTerminal(target: target, password: password, remember: remember)
                        } else {
                            model.connect(target: target, password: password, remember: remember)
                        }
                    }
                )
            }
            // Asked where it happened, rather than answered later in a list of fingerprints somebody
            // would have to know to go and find. Two answers, and the destructive-looking one is the
            // safe one: not trusting it leaves everything as it was.
            .alert(
                Text("keyChanged.title"),
                isPresented: Binding(get: { model.keyChanged != nil }, set: { if !$0 { model.keyChanged = nil } }),
                presenting: model.keyChanged
            ) { change in
                Button(String(localized: "keyChanged.trust"), role: .destructive) {
                    model.trustChangedKey(change)
                }
                Button(String(localized: "keyChanged.cancel"), role: .cancel) {
                    model.keyChanged = nil
                }
            } message: { change in
                Text(String(
                    format: String(localized: "keyChanged.body"),
                    Targets.shared.formatId(id: change.target),
                    String(change.was.prefix(16)),
                    String(change.now.prefix(16))
                ))
            }
        case .connecting(let progress):
            Connecting(message: String(localized: progressKey(progress)))
        case .connected(let hostname, let platform, let transport, let width, let height, let displays):
            RemoteScreen(
                model: model,
                hostname: hostname,
                platform: platform,
                transport: transport,
                width: width,
                height: height,
                displays: displays
            )
        case .terminal:
            if let terminal = model.terminal {
                TerminalView(terminal: terminal, onClose: { model.closeTerminal() })
            }
        }
    }
}

/// The three pre-session tabs. The session is not one of them: it follows from the model's phase.
private enum Tab: Hashable {
    case connect
    case favourites
    case settings

    init(named: String?) {
        switch named {
        case "favourites": self = .favourites
        case "settings": self = .settings
        default: self = .connect
        }
    }
}

/// What the user looks at for as long as the connection takes, which on a bad network is a while.
///
/// It used to be a bare `ProgressView` with a word next to it, which reads as a stall rather than as
/// progress. The mark says whose app this is; the sentence underneath is the only part that changes as the
/// stages go by.
private struct Connecting: View {
    let message: String

    var body: some View {
        VStack(spacing: 28) {
            BrandMark(side: 72)
            Text(message)
                .font(.body)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
            ProgressView()
        }
        .padding(32)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(.systemGroupedBackground))
    }
}

/// The screen between agreeing and seeing.
///
/// A session reaching "connected" means the two ends have settled on a codec, a size and a route. It does
/// not mean a frame has arrived, and on a slow link those are seconds apart -- seconds during which the
/// screen was its own black backdrop, which is exactly what a broken session looks like. Worse, the same
/// black is what a host that cannot read its own desktop shows for ever.
///
/// So this sits over the canvas until the first frame is taken, and after a few seconds it stops merely
/// spinning and says what is most likely wrong. The picture, when it appears, is one that has already been
/// negotiated and decoded -- never a flicker of black on the way to it.
private struct WaitingForPicture: View {
    let hostname: String
    let late: Bool

    var body: some View {
        VStack(spacing: 20) {
            ProgressView()
                .controlSize(.large)
                .tint(.white)

            Text(String(format: String(localized: "session.waitingForPicture"), hostname))
                .font(.callout)
                .foregroundStyle(.white)
                .multilineTextAlignment(.center)

            if late {
                Text("session.pictureLate")
                    .font(.footnote)
                    .foregroundStyle(.white.opacity(0.7))
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 320)
            }
        }
        .padding(32)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color.black)
        .animation(.easeInOut(duration: 0.25), value: late)
    }
}

/// What the host said about its screen, when it has none to show.
///
/// No spinner: the host is not necessarily about to send anything. It may be asking the person at that
/// computer, but it may equally have been refused there, and only the host's words tell which. They are
/// shown as the host sent them, as the desktop viewer shows them.
private struct HostNotice: View {
    let hostname: String
    let notice: String

    var body: some View {
        VStack(spacing: 12) {
            Text(verbatim: hostname)
                .font(.footnote)
                .foregroundStyle(.white.opacity(0.7))
                .multilineTextAlignment(.center)

            Text(verbatim: notice)
                .font(.callout)
                .foregroundStyle(.white)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 360)
        }
        .padding(32)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color.black)
    }
}

/// The progress states, as keys the app translates rather than sentences the library invented.
private func progressKey(_ progress: ConnectProgress) -> String.LocalizationValue {
    switch progress {
    case .lookingUp: return "progress.lookingUp"
    case .measuringNetwork: return "progress.measuring"
    case .punching: return "progress.punching"
    case .waitingForApproval: return "progress.waiting"
    default: return "progress.connecting"
    }
}

/// The session: a desktop, the fingers driving it, and as little else on screen as possible.
///
/// Everything that is not the picture hides. The toolbar collapses to a single button, the key bar appears
/// only when it is wanted, and the options that used to sit permanently over the video have moved into
/// sheets. A phone has six inches and the remote machine deserves all of them.
private struct RemoteScreen: View {
    /// Observed, not merely held.
    ///
    /// This was `let model: SessionModel`, which compiles, reads correctly, and never subscribes to
    /// anything. Every @Published change on the model -- the canvas the first frame sizes, a pinch, the
    /// signal, the cursor -- was invisible here, so the screen redrew only when one of its own @State
    /// values changed, which means only when somebody tapped a control.
    ///
    /// The symptom was baffling in exactly the way that follows from that: the picture did not appear
    /// until you tapped a button, and a pinch did nothing until you tapped a button, at which point it
    /// applied the zoom you had asked for some seconds earlier. Nothing was broken in the touch path or
    /// the decoder; the view simply was not listening.
    @ObservedObject var model: SessionModel
    let hostname: String
    let platform: String
    let transport: String
    let width: Int
    let height: Int
    let displays: Int

    /// Its own, not the one ContentView holds: an environment value is read per view.
    @Environment(\.scenePhase) private var scenePhase

    @State private var pickingImage = false
    @State private var pickedImage: PhotosPickerItem?

    /// Which panel is open. Like the pre-session tab, its initial value can be named in the environment,
    /// because the simulator has no way to tap one from a script and a panel nobody can reach is a panel
    /// nobody can check.
    @State private var panel = SessionPanel(named: ProcessInfo.processInfo.environment["SUNLLO_TEST_PANEL"])

    /// "waiting" or "late" holds the waiting screen up so a script can photograph it.
    private static let forcedWait = ProcessInfo.processInfo.environment["SUNLLO_TEST_WAITING"]
    @State private var signalOpen = false
    @State private var gesturesOpen = false
    @State private var qualityOpen = false
    @State private var resolutionOpen = false
    @State private var quality = Quality.balanced

    var body: some View {
        GeometryReader { geometry in
            ZStack(alignment: .topLeading) {
                // Black bars, as every video player uses: the picture reads better against them and the
                // overlay stays legible whatever the host's wallpaper happens to be.
                Color.black.ignoresSafeArea()

                CanvasVideo(model: model, canvas: model.canvas)

                if model.showRemoteCursor {
                    RemoteCursorOverlay(cursor: model.remoteCursor, canvas: model.canvas)
                }

                // Above the picture and below the controls: it must see every touch the controls do not.
                RemoteTouchView(model: model)

                // Over both, until there is a picture underneath worth uncovering -- or, when the host has
                // no screen to share and has said why, its reason in place of a picture, a stale one
                // included: sharing stopped at that machine leaves the last frame behind.
                //
                // The environment override is the same reason SUNLLO_TEST_PANEL exists: on a fast path the
                // first frame lands inside a second, so the one state this screen was added for is the one
                // state a screenshot can never catch. "late" as well as "waiting", because the sentence
                // that explains a host stuck at its lock screen is the part most worth reading back.
                if let notice = model.hostNotice, model.displays.isEmpty {
                    HostNotice(hostname: hostname, notice: notice)
                        .transition(.opacity)
                        .allowsHitTesting(false)
                } else if !model.hasPicture || Self.forcedWait != nil {
                    WaitingForPicture(
                        hostname: hostname,
                        late: Self.forcedWait == "late" || model.pictureIsLate)
                        .transition(.opacity)
                        .allowsHitTesting(false)
                }

                VStack(alignment: .leading, spacing: 4) {
                    SignalChip(
                        hostname: hostname,
                        platform: platform,
                        transport: transport,
                        roundTripMillis: model.roundTripMillis,
                        stalled: model.stalled,
                        expanded: $signalOpen
                    )

                    // Only when there is something to say. Sound that quietly does not work is
                    // indistinguishable from a host that is not playing anything.
                    // The host can withdraw the keyboard at any point, and used to do it invisibly.
                    if !model.permissions.keyboard {
                        Text("session.inputRefused")
                            .font(.caption)
                            .foregroundStyle(.white)
                            .padding(.horizontal, 12)
                    }

                    if let problem = model.videoProblem {
                        Text(String(format: String(localized: "video.unavailable"), problem))
                            .font(.caption)
                            .foregroundStyle(.white)
                            .padding(.horizontal, 12)
                    }

                    if let problem = model.audioProblem {
                        Text(String(format: String(localized: "audio.unavailable"), problem))
                            .font(.caption)
                            .foregroundStyle(.white)
                            .padding(.horizontal, 12)
                    }
                }

                if model.stalled {
                    StalledNotice(model: model)
                }

                VStack(spacing: 0) {
                    Spacer()
                    KeyBar(
                        model: model,
                        pinned: keyBarPinned,
                        // Never uninvited, and never gone while a modifier is still held — otherwise the
                        // user is left wondering why everything types as Cmd+letter.
                        visible: panel == .keyboard || !model.heldModifiers.isEmpty
                    )

                    SessionBar(panel: $panel) {
                        switch panel {
                        case .actions:
                            actionsPanel
                        case .input:
                            inputPanel
                        case .screen:
                            screenPanel
                        default:
                            EmptyView()
                        }
                    }
                }

                DisconnectButton { model.disconnect() }
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topTrailing)
            }
            .onAppear {
                model.viewResized(geometry.size)
                // Watching a remote desktop is watching, not using: nothing is touched for minutes at a
                // time and the phone locks itself in the middle of it.
                UIApplication.shared.isIdleTimerDisabled = true
                probePointerIfAutomated(in: geometry.size)
                probeKeyboardIfAutomated()
                switchResolutionIfAutomated()
            }
            // Released with the screen rather than on disconnect, so every way out of the session — including
            // the one where it drops by itself — gives the timer back.
            .onDisappear { UIApplication.shared.isIdleTimerDisabled = false }
            // Coming back from the background with a layer that has been sitting on a stale reference
            // frame: ask for a fresh one rather than waiting for the host's own keyframe interval.
            .onChange(of: scenePhase) { _, phase in
                // Inactive comes first (the app switcher), before the OS starts dropping our packets.
                model.setBackgrounded(phase != .active)
                if phase == .active { model.refreshVideo() }
            }
            .onChange(of: geometry.size) { _, size in model.viewResized(size) }
            // The photo picker rather than the pasteboard, because most apps will not let anyone copy a
            // picture at all — they offer Share instead. Without this the feature works only in theory.
            .photosPicker(isPresented: $pickingImage, selection: $pickedImage, matching: .images)
            .onChange(of: pickedImage) { _, item in
                guard let item else { return }
                Task {
                    defer { pickedImage = nil }
                    guard let data = try? await item.loadTransferable(type: Data.self) else {
                        model.imageOffer = .failed
                        return
                    }

                    // Whatever the library held: HEIC on a modern iPhone, so it has to be re-encoded, and
                    // the size check has to happen after that rather than on the original.
                    guard let image = UIImage(data: data), let png = Pasteboard.encode(image) else {
                        model.imageOffer = .failed
                        return
                    }

                    model.sendImage(png)
                }
            }
            .overlay(alignment: .top) { imageNotice }
        }
    }

    /// The key bar owns a pinned flag; here that is simply "the keys panel is the open one".
    private var keyBarPinned: Binding<Bool> {
        Binding(get: { panel == .keyboard }, set: { panel = $0 ? .keyboard : .none })
    }

    /// What to do to the remote machine — kept apart from how the picture looks.
    private var actionsPanel: some View {
        VStack(spacing: 0) {
            PanelRow("session.ctrlAltDel", symbol: "keyboard") { model.press(.ctrlAltDelete) }
            PanelDivider()
            PanelRow("session.lock", symbol: "lock") { model.press(.lockScreen) }
            PanelDivider()
            PanelRow("session.refresh", symbol: "arrow.clockwise") { model.refreshVideo() }
            PanelDivider()
            PanelRow("clipboard.send", symbol: "doc.on.clipboard") { model.sendClipboard() }
            PanelDivider()
            PanelRow("clipboard.sendImage", symbol: "photo") { pickingImage = true }
        }
    }

    /// What the fingers do, and the switch that changes it. The gesture table answers a question people
    /// ask once, so it folds away rather than sitting open over the picture.
    private var inputPanel: some View {
        VStack(spacing: 0) {
            Picker("", selection: Binding(
                get: { model.pointerMode },
                set: { model.setPointerMode($0) }
            )) {
                Text("mode.touch").tag(PointerMode.touch)
                Text("mode.mouse").tag(PointerMode.mouse)
            }
            .pickerStyle(.segmented)
            .padding(16)

            PanelDivider(inset: 0)
            PanelRow("session.remoteCursor", symbol: "cursorarrow") {
                Toggle("", isOn: Binding(
                    get: { model.showRemoteCursor },
                    set: { model.showRemoteCursor = $0 }
                ))
                .labelsHidden()
            }
            PanelDivider()
            PanelRow("session.gestures", symbol: "hand.tap", action: { gesturesOpen.toggle() }) {
                Image(systemName: gesturesOpen ? "chevron.up" : "chevron.down").foregroundStyle(.secondary)
            }

            if gesturesOpen {
                GestureSheet(model: model)
            }
        }
    }

    /// How the picture looks — kept apart from what the remote machine is told to do.
    private var screenPanel: some View {
        VStack(spacing: 0) {
            PanelRow("session.quality", symbol: "slider.horizontal.3", action: { qualityOpen.toggle() }) {
                Text(qualityLabel).foregroundStyle(.secondary)
                Image(systemName: qualityOpen ? "chevron.up" : "chevron.down").foregroundStyle(.secondary)
            }

            if qualityOpen {
                // Custom is the bitrate and frame rate from Settings, not something edited here: a
                // numeric field over the video would be the wrong place to type one, and the stored pair
                // already reaches every new session.
                ForEach([Quality.low, Quality.balanced, Quality.best, Quality.custom], id: \.self) { option in
                    PanelRow(
                        label(for: option),
                        symbol: option == quality ? "largecircle.fill.circle" : "circle",
                        action: {
                            quality = option
                            model.setQuality(option)
                            qualityOpen = false
                        }
                    )
                }
            }

            PanelDivider()
            PanelRow("session.fit", symbol: "arrow.up.left.and.arrow.down.right") { model.resetCanvas() }
            PanelDivider()
            PanelRow("session.original", symbol: "aspectratio") { model.actualSize() }

            // The host's screen resolution. Only where the host offers modes and lets this viewer use
            // the keyboard: somebody who may only watch does not get to resize the other person's desktop.
            if let shown = shownDisplay, !shown.modes.isEmpty, model.permissions.keyboard {
                let selected = Int(Resolutions.shared.selectedChoice(display: shown))
                let originalLabel = shown.original.map { "\(String(localized: "session.resolution.original")) (\($0.label))" }
                    ?? String(localized: "session.resolution.original")
                PanelDivider()
                PanelRow("session.resolution", symbol: "rectangle.expand.vertical", action: { resolutionOpen.toggle() }) {
                    Text(selected == 0 ? originalLabel : shown.modes[selected - 1].label).foregroundStyle(.secondary)
                    Image(systemName: resolutionOpen ? "chevron.up" : "chevron.down").foregroundStyle(.secondary)
                }

                if resolutionOpen {
                    PanelRow(
                        LocalizedStringKey(originalLabel),
                        symbol: selected == 0 ? "largecircle.fill.circle" : "circle",
                        action: {
                            model.setResolution(model.currentDisplay, nil)
                            resolutionOpen = false
                        }
                    )
                    ForEach(Array(shown.modes.enumerated()), id: \.offset) { index, mode in
                        PanelRow(
                            LocalizedStringKey(mode.label),
                            symbol: selected == index + 1 ? "largecircle.fill.circle" : "circle",
                            action: {
                                model.setResolution(model.currentDisplay, mode)
                                resolutionOpen = false
                            }
                        )
                    }
                }

                if let failure = model.resolutionFailure {
                    Text(String(format: String(localized: "session.resolution.failed"), failure))
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .padding(.horizontal, 12)
                        .padding(.bottom, 4)
                }
            }

            // Only where there is a choice to make. One screen needs no switcher.
            let displayCount = max(displays, model.displays.count)
            if displayCount > 1 {
                ForEach(0..<displayCount, id: \.self) { index in
                    PanelDivider()
                    PanelRow(
                        LocalizedStringKey("\(String(format: String(localized: "display.label"), index + 1))"),
                        symbol: index == model.currentDisplay ? "largecircle.fill.circle" : "circle"
                    ) {
                        model.switchDisplay(index)
                    }
                }
            }
        }
    }

    private var qualityLabel: LocalizedStringKey { label(for: quality) }

    /// The display the host is sending now, as it is now.
    private var shownDisplay: RemoteDisplay? {
        model.currentDisplay < model.displays.count ? model.displays[model.currentDisplay] : nil
    }

    private func label(for value: Quality) -> LocalizedStringKey {
        switch value {
        case .low: return "session.quality.low"
        case .best: return "session.quality.best"
        case .custom: return "settings.quality.custom"
        default: return "session.quality.balanced"
        }
    }

    /// What became of the last picture offered. A picture that arrives with no acknowledgement looks
    /// exactly like one that did not, and the next step is on the other machine — so it says so.
    @ViewBuilder
    private var imageNotice: some View {
        if let offer = model.imageOffer {
            Text(
                offer == .alreadyThere ? "clipboard.imageKnown"
                    : offer == .failed ? "clipboard.imageFailed"
                    : "clipboard.imageSent"
            )
            .font(.callout)
            .foregroundStyle(.white)
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            .background(.black.opacity(0.8), in: RoundedRectangle(cornerRadius: 8))
            .padding(.top, 48)
            .task(id: offer) {
                try? await Task.sleep(for: .seconds(4))
                model.imageOffer = nil
            }
        }
    }

    /// The simulator cannot be tapped from a script, so a driven run asks for one pointer move through the
    /// same canvas a real touch uses. It proves the coordinate maths against the host's actual cursor.
    ///
    ///   SIMCTL_CHILD_SUNLLO_TEST_POINTER=60,400
    ///   SIMCTL_CHILD_SUNLLO_TEST_ZOOM=3
    /// A driven run can also exercise the keyboard, through the same calls the bar makes.
    ///
    ///   SIMCTL_CHILD_SUNLLO_TEST_TYPE="DeskPair keyboard works"
    ///   SIMCTL_CHILD_SUNLLO_TEST_SHORTCUT="meta+a,meta+c"
    ///
    /// Shortcuts are a list because the assertion worth making needs two of them: type something, select it
    /// all and copy it, then read the Mac's pasteboard. Asserting on a saved file instead looks simpler and
    /// is not — a modified document raises a save sheet and the check then blames the keyboard.
    private func probeKeyboardIfAutomated() {
        let environment = ProcessInfo.processInfo.environment

        // Generous, and adjustable, because connecting raises the host's own connection-manager window and
        // whatever the script wants focused has to be brought back in front of it first.
        let delay = Double(environment["SUNLLO_TEST_DELAY"] ?? "") ?? 4

        if let text = environment["SUNLLO_TEST_TYPE"], !text.isEmpty {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) { model.type(text) }
        }

        // Put a known string on this device's pasteboard and offer it, which is what the button does. The
        // read still goes through UIPasteboard, so the path under test is the real one.
        if let clip = environment["SUNLLO_TEST_CLIPBOARD"], !clip.isEmpty {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) {
                UIPasteboard.general.string = clip
                model.sendClipboard()
            }
        }

        // Sends a generated picture of a given size, e.g. SUNLLO_TEST_SEND_IMAGE=800x600.
        //
        // The pasteboard cannot be used for this: simctl pbcopy reads standard input, and pbsync carries
        // only text between a Mac and a simulator — a PNG placed on the host pasteboard simply does not
        // arrive. So the one path a script can drive is this one, which still covers everything after the
        // read: the NSData bridge, the shared encoding and the wire.
        if let size = environment["SUNLLO_TEST_SEND_IMAGE"], !size.isEmpty {
            let parts = size.split(separator: "x").compactMap { Int($0) }
            let wanted = parts.count == 2 ? CGSize(width: parts[0], height: parts[1]) : CGSize(width: 640, height: 480)
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) {
                let image = UIGraphicsImageRenderer(size: wanted).image { context in
                    // Not a flat colour: a PNG of one is a few hundred bytes and would not exercise
                    // anything about carrying a real picture.
                    for y in stride(from: 0, to: Int(wanted.height), by: 8) {
                        for x in stride(from: 0, to: Int(wanted.width), by: 8) {
                            UIColor(red: CGFloat((x * 7) % 255) / 255, green: CGFloat((y * 11) % 255) / 255,
                                    blue: CGFloat((x + y) % 255) / 255, alpha: 1).setFill()
                            context.fill(CGRect(x: x, y: y, width: 8, height: 8))
                        }
                    }
                }

                guard let png = Pasteboard.encode(image) else { return }
                model.sendImage(png)
            }
        }

        // Offers whatever is already on the pasteboard, without putting anything there first. That is the
        // only way to drive the image path from a script: a picture has to be placed by simctl pbcopy
        // beforehand, and SUNLLO_TEST_CLIPBOARD would overwrite it with text.
        if environment["SUNLLO_TEST_SEND_CLIPBOARD"] == "1" {
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) {
                model.sendClipboard()
            }
        }

        guard let shortcuts = environment["SUNLLO_TEST_SHORTCUT"], !shortcuts.isEmpty else { return }

        for (index, shortcut) in shortcuts.split(separator: ",").enumerated() {
            let parts = shortcut.trimmingCharacters(in: .whitespaces).split(separator: "+")
            guard let last = parts.last, let character = last.first?.utf16.first else { continue }

            let modifiers: [RemoteModifier] = parts.dropLast().compactMap {
                switch $0.lowercased() {
                case "ctrl", "control": return .control
                case "alt": return .alt
                case "shift": return .shift
                case "meta", "cmd": return .meta
                default: return nil
                }
            }

            DispatchQueue.main.asyncAfter(deadline: .now() + delay + 4 + Double(index) * 2) {
                for modifier in modifiers { model.toggleModifier(modifier) }
                model.type(String(UnicodeScalar(character) ?? " "))
            }
        }
    }

    private func probePointerIfAutomated(in size: CGSize) {
        let environment = ProcessInfo.processInfo.environment

        // Pinching cannot be scripted on the simulator, and the mapping after a zoom is exactly what is
        // worth checking. This applies the same canvas operation a pinch would.
        if let zoom = Double(environment["SUNLLO_TEST_ZOOM"] ?? "") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 6) { model.zoomForTest(Float(zoom)) }
        }

        guard let raw = environment["SUNLLO_TEST_POINTER"] else { return }
        let parts = raw.split(separator: ",").compactMap { Double($0) }
        guard parts.count == 2 else { return }

        let after = Double(environment["SUNLLO_TEST_DELAY"] ?? "") ?? 0
        DispatchQueue.main.asyncAfter(deadline: .now() + after + 8) {
            model.probeMove(CGPoint(x: parts[0], y: parts[1]))
        }
    }

    /// The screen panel's resolution row cannot be tapped from a script either, on a simulator or on a real phone.
    /// SUNLLO_TEST_RESOLUTION=1280x720 picks that mode of the display on screen, by its size in pixels, through the
    /// call the row makes; "original" asks for the display's own mode back. It waits for the host to say which
    /// modes it has, and five seconds after asking writes what it saw -- the modes, the display's size and any
    /// refusal -- to Documents/resolution.txt, for `devicectl device copy from` to bring back.
    private func switchResolutionIfAutomated() {
        let environment = ProcessInfo.processInfo.environment
        guard let wanted = environment["SUNLLO_TEST_RESOLUTION"], !wanted.isEmpty else { return }

        let delay = Double(environment["SUNLLO_TEST_DELAY"] ?? "") ?? 6
        let size = wanted.split(separator: "x").compactMap { Int32($0) }

        func report(_ lines: [String]) {
            guard let documents = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first else { return }
            try? Data((lines.joined(separator: "\n") + "\n").utf8).write(to: documents.appendingPathComponent("resolution.txt"))
        }

        func attempt(_ tries: Int) {
            guard model.displays.indices.contains(model.currentDisplay), !model.displays[model.currentDisplay].modes.isEmpty else {
                if tries < 40 {
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { attempt(tries + 1) }
                } else {
                    report(["asked \(wanted)", "the host offered no modes for display \(model.currentDisplay)"])
                }
                return
            }

            let display = model.displays[model.currentDisplay]
            let modes = display.modes.map { "\($0.width)x\($0.height) \($0.label)" }
            if wanted == "original" {
                model.setResolution(model.currentDisplay, nil)
            } else if size.count == 2, let mode = display.modes.first(where: { $0.width == size[0] && $0.height == size[1] }) {
                model.setResolution(model.currentDisplay, mode)
            } else {
                report(["asked \(wanted)", "no such mode among:"] + modes)
                return
            }

            DispatchQueue.main.asyncAfter(deadline: .now() + 5) {
                let now = model.displays.indices.contains(model.currentDisplay) ? model.displays[model.currentDisplay] : display
                report(["asked \(wanted)", "before \(display.width)x\(display.height)", "after \(now.width)x\(now.height)",
                        "refused \(model.resolutionFailure ?? "no")", "modes:"] + modes)
            }
        }

        DispatchQueue.main.asyncAfter(deadline: .now() + delay) { attempt(0) }
    }
}

private struct StalledNotice: View {
    /// Not observed on purpose: this reads nothing from the model, it only calls reconnect().
    let model: SessionModel

    var body: some View {
        VStack(alignment: .leading) {
            Text("session.stalled")
            Text("session.stalledHint").font(.caption)
            Button(String(localized: "connect.retry")) { model.reconnect() }
        }
        .padding(16)
        .background(.black.opacity(0.8))
        .foregroundStyle(.white)
    }
}


/// What the fingers do, and the switch that changes it — one surface for both, following RustDesk.
private struct GestureSheet: View {
    /// Reads model.pointerMode through a Binding, so it has to hear when the model changes it.
    @ObservedObject var model: SessionModel

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Picker("", selection: Binding(
                get: { model.pointerMode },
                set: { model.setPointerMode($0) }
            )) {
                Text("mode.touch").tag(PointerMode.touch)
                Text("mode.mouse").tag(PointerMode.mouse)
            }
            .pickerStyle(.segmented)

            let isTouch = model.pointerMode == .touch
            row("gesture.tap", isTouch ? "gesture.tapTouch" : "gesture.tapMouse")
            row("gesture.twoTap", "gesture.twoTapAction")
            row("gesture.drag", isTouch ? "gesture.dragTouch" : "gesture.dragMouse")
            row("gesture.twoDrag", "gesture.twoDragAction")
            row("gesture.pinch", "gesture.pinchAction")
            row("gesture.three", "gesture.threeAction")
        }
        .font(.caption)
        .padding(12)
        .background(.ultraThinMaterial)
    }

    private func row(_ gesture: String.LocalizationValue, _ action: String.LocalizationValue) -> some View {
        HStack {
            Text(String(localized: gesture))
            Spacer()
            Text(String(localized: action))
        }
    }
}

