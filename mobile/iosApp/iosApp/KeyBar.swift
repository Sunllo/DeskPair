import SwiftUI
import DeskPairShared

/// The keys a touchscreen cannot otherwise reach.
///
/// A phone has no way to hold Cmd and press S at the same time, so the modifier chips latch instead: tap
/// Cmd, then type the next key, and the modifier is released afterwards. That is how sticky keys works on a
/// Mac, and it is what people already expect from a remote desktop client on a phone.
///
/// Everything past the first row is grouped and folded away, and the groups live in the shared layer so
/// the two platforms cannot offer different keyboards again — which they did: this bar carried a
/// Ctrl+Alt+Del chip Android's did not, and between them they reached twenty-seven of eighty keys. There
/// was no way to send Backspace at all without a hardware keyboard.
struct KeyBar: View {
    @ObservedObject var model: SessionModel
    @Binding var pinned: Bool
    let visible: Bool

    @State private var typing = ""
    @State private var open: KeyGroup?
    @FocusState private var typingFocused: Bool

    /// A foldable group of key caps, named on the bar. The caps themselves are shared with Android.
    private enum KeyGroup: String, CaseIterable, Identifiable {
        case function, numpad, more, ime

        var id: String { rawValue }

        var label: LocalizedStringKey {
            switch self {
            case .function: return "keyboard.function"
            case .numpad: return "keyboard.numpad"
            case .more: return "keyboard.more"
            case .ime: return "keyboard.ime"
            }
        }

        var caps: [KeyGroups.Cap] {
            switch self {
            case .function: return KeyGroups.shared.function
            case .numpad: return KeyGroups.shared.numpad
            case .more: return KeyGroups.shared.more
            case .ime: return KeyGroups.shared.ime
            }
        }
    }

    var body: some View {
        // Never uninvited, and never gone while a modifier is still held: a bar that vanished mid-latch
        // is how someone ends up wondering why everything types as Cmd+letter.
        if !visible {
            EmptyView()
        } else {
        VStack(spacing: 4) {
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 6) {
                    ModifierChip(label: "Ctrl", modifier: .control, model: model)
                    ModifierChip(label: "Alt", modifier: .alt, model: model)
                    ModifierChip(label: "Shift", modifier: .shift, model: model)
                    ModifierChip(label: "Cmd", modifier: .meta, model: model)

                    ForEach(KeyGroups.shared.basic, id: \.key) { cap in
                        KeyChip(label: cap.label, key: cap.key, model: model)
                    }

                    KeyChip(label: "Ctrl+Alt+Del", key: .ctrlAltDelete, model: model)

                    ForEach(KeyGroup.allCases) { group in
                        Button(group.label) { open = open == group ? nil : group }
                            .buttonStyle(.bordered)
                            .tint(open == group ? .accentColor : .secondary)
                    }

                    // Keeps the bar up across everything else; without it the bar only lives as long as
                    // the reason it appeared.
                    Button(String(localized: "keyboard.pin")) { pinned.toggle() }
                        .buttonStyle(.bordered)
                        .tint(pinned ? .accentColor : .secondary)

                    // Not automatic, and not a missing feature: iOS raises its own "Allow Paste?" prompt
                    // whenever an app reads the pasteboard, so this happens when the user asks for it.
                    Button(String(localized: "clipboard.send")) { model.sendClipboard() }
                        .buttonStyle(.bordered)
                        .tint(.secondary)
                        .disabled(!model.canSendClipboard)
                }
                .padding(.horizontal, 8)
            }

            if let open {
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 6) {
                        ForEach(open.caps, id: \.key) { cap in
                            KeyChip(label: cap.label, key: cap.key, model: model)
                        }
                    }
                    .padding(.horizontal, 8)
                }
            }

            // Sent on submit rather than per keystroke: an autocorrecting keyboard rewrites what is already
            // there, and forwarding each edit would reach the host as a stream of corrections.
            TextField(String(localized: "keyboard.type"), text: $typing)
                .textFieldStyle(.roundedBorder)
                .autocorrectionDisabled()
                .textInputAutocapitalization(.never)
                .focused($typingFocused)
                .onSubmit {
                    model.type(typing)
                    typing = ""
                }
                .onChange(of: typingFocused) { _, focused in
                    // While this has focus, keystrokes belong here rather than to the host.
                    model.typingFieldFocused = focused
                }
                .padding(.horizontal, 8)
        }
        .padding(.vertical, 4)
        .background(.ultraThinMaterial)
        }
    }
}

private struct ModifierChip: View {
    let label: String
    let modifier: RemoteModifier
    @ObservedObject var model: SessionModel

    var body: some View {
        Button(label) { model.toggleModifier(modifier) }
            .buttonStyle(.bordered)
            .tint(model.heldModifiers.contains(modifier) ? .accentColor : .secondary)
    }
}

private struct KeyChip: View {
    let label: String
    let key: RemoteKey
    @ObservedObject var model: SessionModel

    var body: some View {
        Button(label) { model.press(key) }
            .buttonStyle(.bordered)
            .tint(.secondary)
    }
}
