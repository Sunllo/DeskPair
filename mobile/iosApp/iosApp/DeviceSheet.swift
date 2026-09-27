import SwiftUI
import DeskPairShared

/// Naming a machine and, if you want, giving it a password to keep.
///
/// One sheet for both jobs. Adding from a recent connection and editing one already on the list ask for
/// exactly the same two things, and two sheets that ask the same questions drift apart: the desktop's
/// device list has been through that, which is why its add and edit are one dialog too.
///
/// The password is optional and is the point of the whole thing on a phone. A desk shows a fresh one-time
/// password every time; somebody who reaches the same machine every day sets a fixed one on it once, and
/// then never types anything again. It goes to the Keychain rather than into settings, beside the pins.
struct DeviceSheet: View {
    let target: String

    /// The groups this list already has, offered rather than remembered: filing a machine under "Office"
    /// should not depend on spelling it the same way twice.
    let knownGroups: [String]
    let onSave: (_ alias: String, _ group: String, _ password: String?) -> Void
    let onCancel: () -> Void

    @State private var alias: String
    @State private var group: String
    @State private var password: String
    @FocusState private var field: Field?

    private enum Field { case alias, group, password }

    /// [password] nil means "there is one stored and it is not being changed"; the field shows it.
    init(
        target: String,
        alias: String,
        group: String,
        knownGroups: [String],
        password: String,
        onSave: @escaping (String, String, String?) -> Void,
        onCancel: @escaping () -> Void
    ) {
        self.target = target
        self.knownGroups = knownGroups
        self.onSave = onSave
        self.onCancel = onCancel
        _alias = State(initialValue: alias)
        _group = State(initialValue: group)
        _password = State(initialValue: password)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("devices.edit.title")
                .font(.headline)
                .frame(maxWidth: .infinity, alignment: .center)

            Text(Targets.shared.formatId(id: target))
                .font(.body.monospaced())
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, alignment: .center)

            row(symbol: "tag") {
                TextField(String(localized: "devices.edit.name"), text: $alias)
                    .focused($field, equals: .alias)
                    .submitLabel(.next)
                    .onSubmit { field = .group }
            }

            row(symbol: "folder") {
                HStack {
                    TextField(String(localized: "devices.edit.group"), text: $group)
                        .focused($field, equals: .group)
                        .submitLabel(.next)
                        .onSubmit { field = .password }

                    // Typing makes a group; this only saves the typing. A group is a name on a device
                    // here, the way it is on the desktop, so there is nothing else to create.
                    if !knownGroups.isEmpty {
                        Menu {
                            Button(String(localized: "devices.edit.noGroup")) { group = "" }
                            ForEach(knownGroups, id: \.self) { name in
                                Button(name) { group = name }
                            }
                        } label: {
                            Image(systemName: "chevron.down.circle")
                                .foregroundStyle(.secondary)
                        }
                    }
                }
            }

            row(symbol: "key") {
                // Not masked, for the same reason the connection prompt is not: this is a password read
                // off somebody's screen or agreed out loud, and hiding it only hides a slipped thumb.
                TextField(String(localized: "devices.edit.password"), text: $password)
                    .autocorrectionDisabled()
                    .textInputAutocapitalization(.never)
                    .focused($field, equals: .password)
                    .submitLabel(.done)
                    .onSubmit(save)
            }

            Text("devices.edit.passwordHint")
                .font(.footnote)
                .foregroundStyle(.secondary)

            Button(action: save) {
                Text("devices.edit.save").frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
        }
        .padding(.horizontal, 20)
        .padding(.top, 20)
        .padding(.bottom, 24)
        .frame(maxWidth: .infinity, alignment: .leading)
        .presentationBackground(Color(.systemGroupedBackground))
        .presentationDetents([.medium])
        .presentationDragIndicator(.visible)
    }

    private func save() {
        onSave(
            alias.trimmingCharacters(in: .whitespaces),
            group.trimmingCharacters(in: .whitespaces),
            password
        )
    }

    /// The same row the sign-in panel uses: a symbol in a fixed column, then the field.
    private func row(symbol: String, @ViewBuilder field: () -> some View) -> some View {
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
}
