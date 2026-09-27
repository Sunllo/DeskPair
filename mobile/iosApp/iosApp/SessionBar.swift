import SwiftUI

/// Which panel of the session bar is open, if any.
///
/// The session used to carry a scrolling row of six identical text buttons, a prominent button with the word
/// "More" in it, and three different kinds of pop-up — a confirmation dialog, an inline sheet and a modal
/// sheet — for three groups of controls that are peers. They are four panels of one bar now.
enum SessionPanel {
    case none
    case actions
    case input
    case screen
    case keyboard

    init(named: String?) {
        switch named {
        case "actions": self = .actions
        case "input": self = .input
        case "screen": self = .screen
        case "keyboard": self = .keyboard
        default: self = .none
        }
    }
}

/// The bar itself: four tabs, and the panel above them.
///
/// Tapping the open tab closes it, which is what leaves the picture alone. That matters more here than
/// anywhere else in the app — everything on this screen is covering something the user is trying to see.
struct SessionBar<Content: View>: View {
    @Binding var panel: SessionPanel
    @ViewBuilder let content: () -> Content

    var body: some View {
        VStack(spacing: 8) {
            if panel != .none && panel != .keyboard {
                PanelCard { content() }
                    .transition(.move(edge: .bottom).combined(with: .opacity))
            }

            HStack(spacing: 0) {
                tab(.actions, "square.3.layers.3d", "session.tab.actions")
                tab(.input, "cursorarrow.motionlines", "session.tab.input")
                tab(.screen, "display", "session.tab.screen")
                tab(.keyboard, "keyboard", "session.tab.keyboard")
            }
            .padding(.vertical, 6)
            .frame(maxWidth: .infinity)
            .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 22))
            .padding(.horizontal, 12)
        }
        .animation(.easeInOut(duration: 0.2), value: panel)
    }

    private func tab(_ which: SessionPanel, _ symbol: String, _ label: LocalizedStringKey) -> some View {
        let selected = panel == which
        return Button {
            panel = selected ? .none : which
        } label: {
            VStack(spacing: 2) {
                Image(systemName: symbol).imageScale(.medium)
                Text(label).font(.caption2)
            }
            .frame(maxWidth: .infinity)
            .foregroundStyle(selected ? AnyShapeStyle(.tint) : AnyShapeStyle(.secondary))
        }
        .buttonStyle(.plain)
    }
}

/// A panel: the same inset card the settings screen uses, over the video rather than over a page.
struct PanelCard<Content: View>: View {
    @ViewBuilder let content: () -> Content

    var body: some View {
        VStack(spacing: 0) { content() }
            .background(Color(.secondarySystemGroupedBackground), in: RoundedRectangle(cornerRadius: 14))
            .padding(.horizontal, 12)
    }
}

/// One row of a panel: a symbol, a name, and whatever sits on the right.
struct PanelRow<Trailing: View>: View {
    private let symbol: String
    private let label: LocalizedStringKey
    private let action: (() -> Void)?
    private let trailing: Trailing

    init(
        _ label: LocalizedStringKey,
        symbol: String,
        action: (() -> Void)? = nil,
        @ViewBuilder trailing: () -> Trailing = { EmptyView() }
    ) {
        self.label = label
        self.symbol = symbol
        self.action = action
        self.trailing = trailing()
    }

    var body: some View {
        let row = HStack(spacing: 14) {
            Image(systemName: symbol).frame(width: 24).foregroundStyle(.secondary)
            Text(label)
            Spacer()
            trailing
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
        .contentShape(Rectangle())

        if let action {
            Button(action: action) { row }.buttonStyle(.plain)
        } else {
            row
        }
    }
}

/// The hairline between two rows of the same card, starting after the symbol column.
struct PanelDivider: View {
    var inset: CGFloat = 54

    var body: some View {
        Divider().padding(.leading, inset)
    }
}

/// How well the session is running, as bars rather than a sentence.
///
/// This used to be a line of white text across the top-left corner — hostname, platform and transport —
/// permanently over the picture. The number matters far less often than "is it healthy", so the chip carries
/// the shape and keeps the sentence for a tap.
struct SignalChip: View {
    let hostname: String
    let platform: String
    let transport: String
    let roundTripMillis: Int?
    let stalled: Bool
    @Binding var expanded: Bool

    var body: some View {
        Button {
            expanded.toggle()
        } label: {
            if expanded {
                open
            } else {
                Image(systemName: symbol)
                    .foregroundStyle(tint)
                    .imageScale(.small)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 6)
                    .background(.black.opacity(0.8), in: Capsule())
            }
        }
        .buttonStyle(.plain)
        .padding(8)
    }

    /// One fact per line.
    ///
    /// These four used to be a single line of text joined with separators, inside a capsule that grew to
    /// the right until it ran under the disconnect button and was cut off mid-word -- so the one thing the
    /// chip exists to tell you, tapped for deliberately, was the part you could not read. Down the page
    /// there is nothing to collide with, and each line can be labelled rather than inferred from position.
    private var open: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 6) {
                Image(systemName: symbol).foregroundStyle(tint).imageScale(.small)
                Text(hostname)
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(.white)
            }

            line("session.signal.platform", platform)
            line("session.signal.route", route)
            if let rtt = roundTripMillis {
                line("session.rtt", "\(rtt) ms")
            }
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
        .frame(maxWidth: 240, alignment: .leading)
        .fixedSize(horizontal: false, vertical: true)
        .background(.black.opacity(0.8), in: RoundedRectangle(cornerRadius: 12))
    }

    private func line(_ label: LocalizedStringKey, _ value: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            Text(label)
                .font(.caption2)
                .foregroundStyle(.white.opacity(0.6))
            Text(value)
                .font(.caption2)
                .foregroundStyle(.white)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    /// The transport in words rather than in the identifier the protocol uses.
    ///
    /// `UDP_REFLEXIVE` is a true and useful thing to print in a log. It is not an answer to "how am I
    /// connected", which is what somebody tapping this chip is asking.
    private var route: String {
        switch transport {
        case "LAN", "UDP_LOCAL": return String(localized: "session.route.lan")
        case "DIRECT_TCP": return String(localized: "session.route.direct")
        case "PUNCHED_TCP", "UDP_REFLEXIVE": return String(localized: "session.route.punched")
        case "RELAY", "UDP_RELAY": return String(localized: "session.route.relay")
        default: return transport
        }
    }

    /// The shape says whether anything is arriving; the colour says how well. Two signals rather than one,
    /// because a stalled session and a slow one need different reactions.
    private var symbol: String {
        stalled ? "antenna.radiowaves.left.and.right.slash" : "antenna.radiowaves.left.and.right"
    }

    /// The desktop's own health colours, so a phone and a desk side by side agree about what "fine" is.
    ///
    /// Three steps rather than two now that there is a round-trip figure to read: red when nothing is
    /// arriving, amber when it is arriving slowly, green when it is fine. The same thresholds as Android.
    private var tint: Color {
        if stalled { return Color(red: 0xE0 / 255, green: 0x52 / 255, blue: 0x4E / 255) }
        guard let rtt = roundTripMillis, rtt < 150 else {
            return Color(red: 0xC9 / 255, green: 0xA2 / 255, blue: 0x27 / 255)
        }
        return Color(red: 0x4C / 255, green: 0xAF / 255, blue: 0x50 / 255)
    }

}

/// The way out, on its own and in red.
///
/// It was one of six identical text buttons, which gave ending the session exactly the same weight as
/// changing the picture quality. The desktop has had the rule for longer: the way out of the application is
/// red, and nothing else is.
struct DisconnectButton: View {
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Label(String(localized: "session.disconnect"), systemImage: "xmark")
                .font(.callout.weight(.semibold))
                .foregroundStyle(.white)
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
                .background(Color(red: 0xF0 / 255, green: 0x5A / 255, blue: 0x5A / 255), in: Capsule())
        }
        .buttonStyle(.plain)
        .padding(8)
    }
}
