import SwiftUI
import DeskPairShared

/// Where a session starts.
///
/// Laid out after the desktop's second home card (`Views/HomeView.axaml`) — the first is "this computer",
/// and this app is not one. One text field takes both an id and an address, as the desktop's does, and
/// `Targets.isDirect` decides which it was.
///
/// Three buttons used to sit in a row under the field, all the same size: Connect, Scan a code, and
/// sometimes Try again. None of them looked like the main thing to do. Connecting is the main thing to do,
/// so it is the only prominent button on the screen; scanning is how you fill the field in, so it has moved
/// into the field; and retrying is plain text under it, when there is something to retry.
/// A target the naming sheet is open for. `sheet(item:)` wants something identifiable, and a bare
/// string is not.
private struct Saving: Identifiable {
    let target: String

    var id: String { target }
}

struct ConnectView: View {
    @ObservedObject var store: AppStore
    let error: String?
    let canRetry: Bool
    let onStart: (_ target: String) -> Void
    var onTerminal: (_ target: String) -> Void = { _ in }
    let onRetry: () -> Void

    @State private var typed = ""
    @State private var scanning = false
    @State private var scanNotice: String?

    /// The machine being named, if the sheet is open.
    @State private var saving: String?

    private var settings: AppSettings { store.settings }

    var body: some View {
        List {
            Section {
                masthead
            }
            .listRowBackground(Color.clear)
            .listRowSeparator(.hidden)

            Section {
                HStack {
                    TextField(String(localized: "connect.target"), text: $typed)
                        .font(.body.monospaced())
                        .autocorrectionDisabled()
                        .textInputAutocapitalization(.never)
                        .keyboardType(.asciiCapable)
                        .submitLabel(.go)
                        .onSubmit { onStart(typed) }

                    Button {
                        scanNotice = nil
                        scanning = true
                    } label: {
                        Image(systemName: "qrcode.viewfinder").imageScale(.large)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(Text("home.scan"))
                }
            }

            Section {
                // The frame goes on the label, not on the button: a frame outside it widens the row and
                // leaves the button hugging its text in the middle of the space.
                Button {
                    onStart(typed)
                } label: {
                    Text("connect.action").frame(maxWidth: .infinity)
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .disabled(Targets.shared.normalise(typed: typed).isEmpty)

                // A shell rather than a picture. Plain, not a second prominent button: connecting is still
                // the main thing to do, and a host only allows this if its owner has turned it on.
                if !Targets.shared.normalise(typed: typed).isEmpty {
                    Button {
                        onTerminal(typed)
                    } label: {
                        Text("terminal.open").frame(maxWidth: .infinity)
                    }
                }

                if canRetry {
                    // A phone loses its network in lifts and tunnels. Retyping an address every time is
                    // what turns a tool someone keeps into one they uninstall.
                    Button {
                        onRetry()
                    } label: {
                        Text("connect.retry").frame(maxWidth: .infinity)
                    }
                }
            } footer: {
                notices
            }
            .listRowBackground(Color.clear)

            Section(String(localized: "home.recent")) {
                if settings.recent.isEmpty {
                    Text("home.noRecent").foregroundStyle(.secondary)
                } else {
                    ForEach(settings.recent, id: \.id) { peer in
                        Button { onStart(peer.id) } label: {
                            HStack {
                                PeerRow(
                                    title: Targets.shared.formatId(id: peer.id),
                                    subtitle: [peer.name.isEmpty ? nil : peer.name, ago(peer.lastConnectedMs)]
                                        .compactMap { $0 }.joined(separator: "  ·  "),
                                    platform: peer.platform,
                                    monospacedTitle: true,
                                    online: store.onlineState(peer.id)
                                )
                                Spacer()
                                // Saving is not a toggle any more: it asks what to call the machine and
                                // whether to keep a password for it, the way the desktop's device list
                                // does. A heart that silently filed an unnamed entry left a list of
                                // nine-digit numbers nobody could tell apart.
                                Image(systemName: settings.isFavourite(target: peer.id)
                                      ? "checkmark.circle.fill" : "plus.circle")
                                    .foregroundStyle(settings.isFavourite(target: peer.id)
                                                     ? AnyShapeStyle(.tint) : AnyShapeStyle(.secondary))
                                    .onTapGesture { saving = peer.id }
                            }
                        }
                        .buttonStyle(.plain)
                        // Offline is the one answer that takes the tap away; unknown keeps it.
                        .disabled(store.onlineState(peer.id) == .offline)
                        .opacity(store.onlineState(peer.id) == .offline ? 0.45 : 1)
                        .swipeActions {
                            Button(String(localized: "home.forgetRecent"), role: .destructive) {
                                store.forgetRecent(peer.id)
                            }
                        }
                    }
                }
            }
        }
        .navigationTitle("")
        .navigationBarTitleDisplayMode(.inline)
        // Which desks are reachable, every fifteen seconds while the list is on screen.
        .task(id: settings.recent.map { $0.id }) {
            while !Task.isCancelled {
                await store.refreshPresence()
                try? await Task.sleep(for: .seconds(15))
            }
        }
        .sheet(item: Binding(get: { saving.map(Saving.init) }, set: { saving = $0?.target })) { item in
            DeviceSheet(
                target: item.target,
                alias: store.settings.favourites.first { $0.target == item.target }?.alias
                    ?? store.settings.recent.first { $0.id == item.target }?.name ?? "",
                group: store.settings.favourites.first { $0.target == item.target }?.group ?? "",
                knownGroups: store.knownGroups,
                password: store.rememberedPassword(item.target) ?? "",
                onSave: { alias, group, password in
                    store.saveDevice(item.target, alias: alias, group: group, password: password)
                    saving = nil
                },
                onCancel: { saving = nil }
            )
        }
        .sheet(isPresented: $scanning) {
            QrScannerView { scanned in
                scanning = false
                switch applyScan(scanned, to: store) {
                case .cancelled:
                    break
                case .applied(let target, let message):
                    // Scanned, so dialled. This used to only fill the field, on the grounds that
                    // connecting to whatever a camera happened to see was not a good default -- but a code
                    // now carries the desk's one-time password, so scanning it is the whole ceremony and
                    // stopping to ask for one more tap is asking for nothing. The field is filled in as
                    // well, because a connection that fails leaves something to try again with.
                    typed = Targets.shared.formatId(id: target)
                    scanNotice = message
                    onStart(target)
                case .rejected(let message):
                    scanNotice = message
                }
            }
        }
    }

    /// The mark and the product's name.
    ///
    /// The screen used to open with the sentence "Control a remote desk", which describes what the app is
    /// for but never says what it is. A first-time user should be able to tell which of the apps on their
    /// phone this is.
    private var masthead: some View {
        VStack(spacing: 8) {
            BrandMark(side: 56)
            Text(verbatim: "DeskPair").font(.title2.weight(.semibold))
            Text("home.controlRemote")
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 8)
    }

    /// Everything the screen might need to say, in the one place someone is already looking.
    @ViewBuilder
    private var notices: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let scanNotice {
                Text(scanNotice)
            }

            // Said here rather than only in settings: this is the moment someone types an id and finds
            // out it cannot work.
            //
            // An empty address is the normal state now, not a broken one -- it means the portal is asked.
            // Only somebody who turned that off and then named nothing has nowhere to go.
            if settings.rendezvousServer.isEmpty && !settings.useDirectoryServers {
                Text("home.noServer")
            }

            if let error {
                Text(error).foregroundStyle(.red)
            }
        }
        .padding(.top, 4)
    }
}
