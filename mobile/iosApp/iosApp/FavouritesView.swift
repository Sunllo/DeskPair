import SwiftUI
import DeskPairShared

/// The machines someone chose to keep, as against the ones they happen to have visited.
///
/// A tab of its own rather than a section under the connect form. The desktop gives favourites a whole page
/// for the same reason: a list people curate is the one they come back to, and it should not have to share
/// the screen with a text box.
/// A target the editing sheet is open for; `sheet(item:)` needs something identifiable.
private struct Editing: Identifiable {
    let target: String

    var id: String { target }
}

struct FavouritesView: View {
    @ObservedObject var store: AppStore
    let onStart: (_ target: String) -> Void

    private var settings: AppSettings { store.settings }

    /// The machine being edited, if the sheet is open.
    @State private var editing: String?

    /// Named apart from SwiftUI's own `Section`, which `body` uses a few lines below; one name for both
    /// resolves to whichever is nearer and the error it produces says nothing about why.
    private struct FolderSection: Identifiable {
        let name: String
        let favourites: [Favourite]
        var id: String { name }
    }

    /// Folders come from the desktop, where the same thing is called a group, and arrive with a synced list.
    /// Sorted by name with the unfiled ones last, which is where somebody looking for them expects to look.
    ///
    /// Built from the named groups as well as the devices, so a group made a moment ago is on screen
    /// before anything is filed in it. A group somebody cannot see is a group they make twice.
    private var sections: [FolderSection] {
        let filed = Dictionary(grouping: settings.favourites) { $0.group.trimmingCharacters(in: .whitespaces) }
        let named = Set(settings.groupNames.map { $0.trimmingCharacters(in: .whitespaces) })
            .filter { !$0.isEmpty }

        return Set(filed.keys).union(named)
            .map { FolderSection(name: $0, favourites: filed[$0] ?? []) }
            // An empty unfiled section is not a section: it is the absence of one.
            .filter { !$0.name.isEmpty || !$0.favourites.isEmpty }
            .sorted {
                if $0.name.isEmpty != $1.name.isEmpty { return $1.name.isEmpty }
                return $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending
            }
    }

    var body: some View {
        content
            .toolbar {
                if store.account.snapshot.link.isLinked {
                    ToolbarItem(placement: .primaryAction) {
                        Button {
                            renamingGroup = ""
                            groupName = ""
                        } label: {
                            Label(String(localized: "devices.group.add"), systemImage: "folder.badge.plus")
                        }
                    }
                }
            }
            .alert(
                Text(renamingGroup?.isEmpty == false ? "devices.group.rename" : "devices.group.add"),
                isPresented: Binding(get: { renamingGroup != nil }, set: { if !$0 { renamingGroup = nil } })
            ) {
                TextField(String(localized: "devices.group.name"), text: $groupName)
                Button(String(localized: "devices.edit.save")) {
                    let name = groupName.trimmingCharacters(in: .whitespaces)
                    if let was = renamingGroup, !was.isEmpty {
                        store.renameGroup(was, to: name)
                    } else {
                        store.addGroup(name)
                    }
                    renamingGroup = nil
                }
                Button(String(localized: "devices.group.cancel"), role: .cancel) { renamingGroup = nil }
            }
            .sheet(item: Binding(get: { editing.map(Editing.init) }, set: { editing = $0?.target })) { item in
                DeviceSheet(
                    target: item.target,
                    alias: settings.favourites.first { $0.target == item.target }?.alias ?? "",
                    group: store.settings.favourites.first { $0.target == item.target }?.group ?? "",
                    knownGroups: store.knownGroups,
                    password: store.rememberedPassword(item.target) ?? "",
                    onSave: { alias, group, password in
                        store.saveDevice(item.target, alias: alias, group: group, password: password)
                        editing = nil
                    },
                    onCancel: { editing = nil }
                )
            }
    }

    /// The group being renamed, or the empty string while one is being made; nil when neither.
    @State private var renamingGroup: String?
    @State private var groupName = ""

    private var content: some View {
        Group {
            if !store.account.snapshot.link.isLinked {
                // The list belongs to an account, so without one there is nothing to show and nothing
                // that would survive this phone. Said here rather than by showing an empty list, which
                // would look like a list somebody had not filled in yet.
                ContentUnavailableView(
                    String(localized: "devices.signInRequired"),
                    systemImage: "person.crop.circle",
                    description: Text("devices.signInRequiredHint")
                )
            } else if settings.favourites.isEmpty {
                ContentUnavailableView(
                    String(localized: "home.noFavourites"),
                    systemImage: "plus.circle",
                    description: Text("home.favouritesEmptyHint")
                )
            } else {
                List {
                    ForEach(sections, id: \.name) { section in
                        Section {
                            ForEach(section.favourites, id: \.target) { favourite in
                                Button { onStart(favourite.target) } label: {
                                    PeerRow(
                                        title: favourite.title,
                                        // The machine's own name and its id, which is what somebody needs
                                        // to tell two saved machines apart when they named both of them
                                        // something like "office".
                                        subtitle: [
                                            favourite.note.isEmpty ? nil : favourite.note,
                                            favourite.alias.isEmpty
                                                ? nil : Targets.shared.formatId(id: favourite.target),
                                        ].compactMap { $0 }.joined(separator: "  ·  "),
                                        platform: favourite.platform,
                                        monospacedTitle: favourite.alias.isEmpty,
                                        online: store.onlineState(favourite.target)
                                    )
                                }
                                .buttonStyle(.plain)
                                // Offline is the one answer that takes the tap away; unknown keeps it.
                                .disabled(store.onlineState(favourite.target) == .offline)
                                .opacity(store.onlineState(favourite.target) == .offline ? 0.45 : 1)
                                // Remove is declared first because a trailing swipe lays its buttons out
                                // from the edge inward, and the outer one is the one a thumb reaches.
                                .swipeActions(edge: .trailing) {
                                    Button(role: .destructive) {
                                        // Takes the saved password with it: keeping a secret for a machine
                                        // the app no longer admits to knowing is not tidiness.
                                        store.removeDevice(favourite.target)
                                    } label: {
                                        // Padded rather than padded out with spaces: these buttons size to
                                        // their label, and two short words make two buttons too narrow to
                                        // hit without looking.
                                        Text("devices.remove").padding(.horizontal, 12)
                                    }

                                    Button {
                                        editing = favourite.target
                                    } label: {
                                        Text("devices.edit").padding(.horizontal, 12)
                                    }
                                    .tint(.accentColor)
                                }
                            }
                        } header: {
                            // Always, including when "Ungrouped" is the only one. It was hidden while
                            // there was one section, which read as a list with no folders at all -- and
                            // somebody who has just pressed New group and seen nothing change has no way
                            // to tell that the machines they already had are the ones sitting outside it.
                            Group {
                                HStack {
                                    Text(section.name.isEmpty
                                         ? String(localized: "favourites.ungrouped") : section.name)
                                    if !section.name.isEmpty {
                                        Spacer()
                                        // On the header rather than behind a swipe: a section header is
                                        // not a row, and a gesture it does not answer is worse than a
                                        // button that is plainly there.
                                        Menu {
                                            Button(String(localized: "devices.group.rename")) {
                                                renamingGroup = section.name
                                                groupName = section.name
                                            }
                                            Button(String(localized: "devices.group.delete"), role: .destructive) {
                                                store.removeGroup(section.name)
                                            }
                                        } label: {
                                            Image(systemName: "ellipsis.circle")
                                        }
                                    }
                                }
                                // Dropped here, it is filed here. The whole header is the target rather
                                // than a strip of it, because a drop zone somebody has to aim at on a
                                // phone is a drop zone they miss.
                            }
                        }
                    }
                }
            }
        }
        .navigationTitle(Text("nav.favourites"))
        // Which desks are reachable, every fifteen seconds while the list is on screen -- the desktop's
        // cadence -- and again when the list changes.
        .task(id: settings.favourites.map { $0.target }) {
            while !Task.isCancelled {
                await store.refreshPresence()
                try? await Task.sleep(for: .seconds(15))
            }
        }
        .navigationBarTitleDisplayMode(.inline)
    }
}
