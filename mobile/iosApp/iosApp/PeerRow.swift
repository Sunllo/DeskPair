import SwiftUI
import DeskPairShared

// The pieces both peer lists need. Favourites and Recent are separate tabs now, so what they share has to
// live somewhere neither of them owns.

/// One machine in a list: a platform glyph, a name, and what is worth knowing under it.
struct PeerRow: View {
    let title: String
    let subtitle: String
    let platform: String
    let monospacedTitle: Bool
    var online: OnlineState = .unknown

    var body: some View {
        HStack(spacing: 12) {
            // The slot is 22 points wide whether or not there is a logo for this peer, so an unrecognised
            // one does not pull its own name out of the column the rest of the names are in.
            Group {
                if let logo {
                    Image(logo)
                        .renderingMode(.template)
                        .resizable()
                        .scaledToFit()
                }
            }
            .frame(width: 22, height: 22)
            .foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 8) {
                    // Reachable, not reachable, or not known: the dot the desktop's device list shows.
                    // Grey for unknown rather than red, because "the server did not answer" is not "off".
                    Circle()
                        .fill(dotColour)
                        .frame(width: 8, height: 8)
                        .accessibilityLabel(dotLabel)
                    Text(title)
                        .font(monospacedTitle ? .body.monospaced() : .body)
                }
                if !subtitle.isEmpty {
                    Text(subtitle).font(.caption).foregroundStyle(.secondary)
                }
            }
        }
    }

    /// The asset for this peer's platform, or nil when nothing fits.
    ///
    /// These were SF Symbols — a tower, a monitor, a terminal prompt — which named the *kind* of machine and
    /// left every desktop in the list looking alike. They are now the platforms' own logos, drawn from the
    /// desktop app's vector paths: `tools/platform-icons.py` writes this catalogue from the same file
    /// `Assets/PlatformIcons.axaml` that the desktop draws, so a peer looks the same in the hand as on the
    /// desk. Which logo a platform string maps to is decided in the shared module, so the two phones cannot
    /// drift apart either.
    private var logo: String? {
        PlatformLogos.shared.assetFor(platform: platform).map { "platform-\($0)" }
    }

    private var dotColour: Color {
        switch online {
        case .online: return Color(red: 0.30, green: 0.69, blue: 0.31)
        case .offline: return Color(white: 0.62)
        default: return Color(.tertiarySystemFill)
        }
    }

    private var dotLabel: String {
        switch online {
        case .online: return String(localized: "presence.online")
        case .offline: return String(localized: "presence.offline")
        default: return String(localized: "presence.unknown")
        }
    }
}

/// The password prompt.
///
/// "Remember" writes to the Keychain, never to the settings file, for the same reason the desktop keeps its
/// password hashes out of `desktop.json`.
struct PasswordSheet: View {
    let target: String
    let offerToRemember: Bool
    let onCancel: () -> Void
    let onConnect: (_ password: String, _ remember: Bool) -> Void

    @State private var password = ""
    @State private var remember: Bool

    init(
        target: String,
        offerToRemember: Bool,
        onCancel: @escaping () -> Void,
        onConnect: @escaping (String, Bool) -> Void
    ) {
        self.target = target
        self.offerToRemember = offerToRemember
        self.onCancel = onCancel
        self.onConnect = onConnect
        _remember = State(initialValue: offerToRemember)
    }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    // Plain text, and the return key connects.
                    //
                    // What is usually typed here is a six-character one-time password that changes every
                    // session, read off another screen and thumbed in; masking it buys very little against
                    // a shoulder and costs a retry every time a thumb slips. A host that has set a
                    // permanent password is the case this does not suit, and is the reason to revisit it.
                    TextField(String(localized: "connect.password"), text: $password)
                        .autocorrectionDisabled()
                        .textInputAutocapitalization(.never)
                        // ASCII only: a host password has to survive two keyboards, and a Chinese input
                        // method open over this field would send characters the host never saw. The
                        // keyboard type asks for a plain one; the filter is what guarantees it.
                        .keyboardType(.asciiCapable)
                        .onChange(of: password) { _, value in
                            let ascii = PasswordText.shared.ascii(value: value)
                            if ascii != value { password = ascii }
                        }
                        .submitLabel(.go)
                        .onSubmit { onConnect(password, remember) }
                }
            }
            .navigationTitle(Targets.shared.formatId(id: target))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(String(localized: "connect.cancel"), action: onCancel)
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(String(localized: "connect.action")) { onConnect(password, remember) }
                }
            }
        }
        .presentationDetents([.medium])
    }
}

/// Lets a plain target string drive `.sheet(item:)`, which needs something identifiable.
extension String: @retroactive Identifiable {
    public var id: String { self }
}

/// Roughly how long ago, in whole units. Deliberately coarse: a timestamp to the second on a list someone
/// glances at is noise.
func ago(_ millis: Int64) -> String? {
    guard millis > 0 else { return nil }

    let elapsed = Date().timeIntervalSince1970 - Double(millis) / 1000
    let formatter = RelativeDateTimeFormatter()
    formatter.unitsStyle = .abbreviated
    return formatter.localizedString(fromTimeInterval: -elapsed)
}
