import SwiftUI
import UIKit
import DeskPairShared

/// A terminal session and what it shows.
///
/// Observed apart from `SessionModel` so that a screen which can change sixty times a second redraws only
/// itself, not the whole app above it.
@MainActor
final class TerminalModel: ObservableObject {
    let terminal: RemoteTerminal

    @Published private(set) var snapshot: TerminalSnapshot?
    @Published private(set) var status: TerminalStatus?

    /// A scripted run's command, typed once as soon as the shell is open. See `SUNLLO_TEST_TERMINAL`.
    var scripted: String?

    private var observations: [Observation] = []

    init(_ terminal: RemoteTerminal) {
        self.terminal = terminal
        observations.append(SessionObservers.shared.terminalSnapshot(terminal: terminal) { [weak self] (now: TerminalSnapshot) in
            self?.snapshot = now
        })
        observations.append(SessionObservers.shared.terminalStatus(terminal: terminal) { [weak self] (now: TerminalStatus) in
            guard let self else { return }
            self.status = now
            if now.phase == TerminalPhase.open, let command = self.scripted {
                self.scripted = nil
                terminal.type(text: command)
                terminal.key(key: .enter, shift: false, alt: false, control: false)
            }
        })
    }

    func close() {
        terminal.close()
        observations.forEach { $0.stop() }
        observations = []
    }
}

/// A terminal on another computer, full screen.
///
/// Drawn from the shared snapshot: each run placed at its column times the cell width, so the grid holds
/// whatever the font does with a wide character. The on-screen keyboard types through an invisible view that
/// takes text the way a text field would, and a row of keys supplies what a phone keyboard lacks. Ctrl and
/// Alt on that row are sticky: tap Ctrl, then C, is Ctrl+C.
struct TerminalView: View {
    @ObservedObject var terminal: TerminalModel
    let onClose: () -> Void

    @State private var typing = true
    @State private var control = false
    @State private var alt = false
    @State private var pendingPaste: String?
    @State private var dragCarry: CGFloat = 0
    @State private var lastDrag: CGFloat = 0

    private static let font = UIFont.monospacedSystemFont(ofSize: 13, weight: .regular)
    private static let boldFont = UIFont.monospacedSystemFont(ofSize: 13, weight: .bold)

    /// One cell, measured from the font rather than assumed, so runs placed by column line up with the text.
    private static let cell: CGSize = {
        let width = ("M" as NSString).size(withAttributes: [.font: font]).width
        return CGSize(width: width, height: ceil(font.lineHeight))
    }()

    private var remote: RemoteTerminal { terminal.terminal }

    var body: some View {
        VStack(spacing: 0) {
            header
            notice

            GeometryReader { geometry in
                Canvas { context, _ in
                    if let snapshot = terminal.snapshot {
                        draw(snapshot, in: &context)
                    }
                }
                .contentShape(Rectangle())
                .onTapGesture { typing = true }
                .gesture(scrollGesture)
                .onAppear { fit(geometry.size) }
                .onChange(of: geometry.size) { _, size in fit(size) }
                .onChange(of: terminal.snapshot?.columns) { _, _ in fit(geometry.size) }
                .background(
                    TerminalInput(
                        focused: $typing,
                        onText: typed,
                        onBackspace: { send(.backspace) },
                        onKey: hardwareKey
                    )
                    .frame(width: 1, height: 1)
                    .opacity(0.01)
                )
            }
            .padding(4)

            keyBar
        }
        .background(color(terminal.snapshot?.background ?? Int32(bitPattern: 0xFF1E1E1E)).ignoresSafeArea())
        .alert(
            Text("terminal.paste"),
            isPresented: Binding(get: { pendingPaste != nil }, set: { if !$0 { pendingPaste = nil } }),
            presenting: pendingPaste
        ) { text in
            Button(String(localized: "terminal.paste")) {
                remote.paste(text: text)
                pendingPaste = nil
            }
            Button(role: .cancel) { pendingPaste = nil } label: { Text("connect.cancel") }
        } message: { text in
            Text(String(format: String(localized: "terminal.pasteConfirm"), Int64(lineCount(text))))
        }
    }

    // MARK: - The frame around the grid

    /// Whose shell this is: the one thing about a terminal on somebody else's computer that must never be
    /// ambiguous.
    private var header: some View {
        HStack {
            Text([terminal.status?.identity ?? "", remote.host.hostname].filter { !$0.isEmpty }.joined(separator: "@"))
                .font(.system(size: 14, weight: .semibold, design: .monospaced))
                .foregroundStyle(Color(red: 0.91, green: 0.75, blue: 0.42))
                .lineLimit(1)
            Spacer()
            Button(String(localized: "terminal.close"), action: onClose)
                .foregroundStyle(Color(white: 0.85))
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(Color(red: 0.106, green: 0.122, blue: 0.149))
    }

    @ViewBuilder
    private var notice: some View {
        if let status = terminal.status, let text = noticeText(status) {
            HStack {
                Text(text)
                    .font(.callout)
                    .foregroundStyle(Color(white: 0.85))
                Spacer()
                if status.phase == TerminalPhase.ended || status.phase == TerminalPhase.failed {
                    Button(String(localized: "terminal.newShell")) { remote.openNewShell() }
                }
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            .background(Color(red: 0.133, green: 0.149, blue: 0.176))
        }
    }

    private func noticeText(_ status: TerminalStatus) -> String? {
        switch status.phase {
        case TerminalPhase.opening:
            return String(localized: "terminal.opening")
        case TerminalPhase.ended:
            return status.message.isEmpty || status.message == "the shell exited"
                ? String(format: String(localized: "terminal.ended"), Int64(status.exitCode))
                : String(format: String(localized: "terminal.endedReason"), status.message)
        case TerminalPhase.failed:
            return String(format: String(localized: "terminal.failed"), status.message)
        case TerminalPhase.closed:
            return String(localized: "terminal.connectionLost")
        default:
            return nil
        }
    }

    /// What a phone keyboard lacks. Ctrl and Alt stay down for the next key, then let go.
    private var keyBar: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 4) {
                KeyCap(label: "Esc") { send(.escape) }
                KeyCap(label: "Tab") { send(.tab) }
                KeyCap(label: "Ctrl", latched: control) { control.toggle() }
                KeyCap(label: "Alt", latched: alt) { alt.toggle() }
                KeyCap(label: "←") { send(.left) }
                KeyCap(label: "↑") { send(.up) }
                KeyCap(label: "↓") { send(.down) }
                KeyCap(label: "→") { send(.right) }
                KeyCap(label: "Home") { send(.home) }
                KeyCap(label: "End") { send(.end) }
                KeyCap(label: "PgUp") { send(.pageUp) }
                KeyCap(label: "PgDn") { send(.pageDown) }
                ForEach(["|", "~", "/", "-", "_", "\\"], id: \.self) { symbol in
                    KeyCap(label: symbol) { typed(symbol) }
                }
                KeyCap(label: String(localized: "terminal.paste")) { paste() }
                KeyCap(systemImage: typing ? "keyboard.chevron.compact.down" : "keyboard") { typing.toggle() }
            }
            .padding(6)
        }
        .background(Color(red: 0.106, green: 0.122, blue: 0.149))
    }

    // MARK: - Drawing

    private func draw(_ snapshot: TerminalSnapshot, in context: inout GraphicsContext) {
        let cell = Self.cell
        for (row, line) in snapshot.lines.enumerated() {
            let y = CGFloat(row) * cell.height
            for run in line.runs {
                let x = CGFloat(run.column) * cell.width
                if run.background != snapshot.background {
                    let box = CGRect(x: x, y: y, width: CGFloat(run.cells) * cell.width, height: cell.height)
                    context.fill(Path(box), with: .color(color(run.background)))
                }

                if !run.text.trimmingCharacters(in: .whitespaces).isEmpty || run.underline || run.strike {
                    var text = Text(run.text)
                        .font(Font(run.bold ? Self.boldFont : Self.font))
                        .foregroundColor(color(run.foreground))
                    if run.italic { text = text.italic() }
                    if run.underline { text = text.underline() }
                    if run.strike { text = text.strikethrough() }
                    context.draw(text, at: CGPoint(x: x, y: y), anchor: .topLeading)
                }
            }
        }

        if snapshot.cursorVisible {
            let box = CGRect(
                x: CGFloat(snapshot.cursorColumn) * cell.width,
                y: CGFloat(snapshot.cursorRow) * cell.height,
                width: cell.width,
                height: cell.height)
            context.fill(Path(box), with: .color(color(snapshot.cursorColor).opacity(0.6)))
        }
    }

    /// The grid that fits, sent to the host whenever it changes. The shared session waits for the size to
    /// settle before telling the shell, so a rotation is one resize rather than a burst of them.
    private func fit(_ size: CGSize) {
        let columns = max(2, Int(size.width / Self.cell.width))
        let rows = max(1, Int(size.height / Self.cell.height))
        if let snapshot = terminal.snapshot, snapshot.columns == columns, snapshot.rows == rows {
            return
        }
        remote.resize(columns: Int32(columns), rows: Int32(rows))
    }

    /// Dragging down reads back into the history, a line per cell height.
    private var scrollGesture: some Gesture {
        DragGesture(minimumDistance: 8)
            .onChanged { value in
                dragCarry += value.translation.height - lastDrag
                lastDrag = value.translation.height
                let lines = Int(dragCarry / Self.cell.height)
                if lines != 0 {
                    dragCarry -= CGFloat(lines) * Self.cell.height
                    remote.scroll(lines: Int32(lines))
                }
            }
            .onEnded { _ in
                dragCarry = 0
                lastDrag = 0
            }
    }

    // MARK: - Typing

    private func send(_ key: TerminalKey) {
        remote.key(key: key, shift: false, alt: alt, control: control)
        control = false
        alt = false
    }

    private func typed(_ text: String) {
        for character in text {
            if character == "\n" || character == "\r" {
                send(.enter)
            } else if control || alt {
                remote.chord(character: String(character), alt: alt, control: control)
                control = false
                alt = false
            } else {
                remote.type(text: String(character))
            }
        }
    }

    /// A hardware keyboard's keys that do not arrive as text: arrows, Escape, the function keys, and
    /// anything held with Control or Option. Returns false for the rest, which then arrive as text.
    private func hardwareKey(_ key: UIKey) -> Bool {
        let flags = key.modifierFlags
        let shift = flags.contains(.shift)
        let option = flags.contains(.alternate)
        let ctrl = flags.contains(.control)
        if let named = Self.named[key.keyCode] {
            remote.key(key: named, shift: shift, alt: option, control: ctrl)
            return true
        }
        if ctrl || option, let base = key.charactersIgnoringModifiers.first {
            remote.chord(character: String(base), alt: option, control: ctrl)
            return true
        }
        return false
    }

    private static let named: [UIKeyboardHIDUsage: TerminalKey] = [
        .keyboardEscape: .escape,
        .keyboardUpArrow: .up,
        .keyboardDownArrow: .down,
        .keyboardLeftArrow: .left,
        .keyboardRightArrow: .right,
        .keyboardHome: .home,
        .keyboardEnd: .end,
        .keyboardPageUp: .pageUp,
        .keyboardPageDown: .pageDown,
        .keyboardDeleteForward: .delete_,
        .keyboardInsert: .insert,
        .keyboardF1: .f1, .keyboardF2: .f2, .keyboardF3: .f3, .keyboardF4: .f4,
        .keyboardF5: .f5, .keyboardF6: .f6, .keyboardF7: .f7, .keyboardF8: .f8,
        .keyboardF9: .f9, .keyboardF10: .f10, .keyboardF11: .f11, .keyboardF12: .f12,
    ]

    /// Asked first when it is more than one line: each line is a command the moment it lands.
    private func paste() {
        guard let text = UIPasteboard.general.string, !text.isEmpty else { return }
        if TerminalKeys.shared.isMultiLine(text: text) {
            pendingPaste = text
        } else {
            remote.paste(text: text)
        }
    }

    private func lineCount(_ text: String) -> Int {
        var trimmed = Substring(text)
        while let last = trimmed.last, last == "\n" || last == "\r" { trimmed = trimmed.dropLast() }
        return trimmed.split(separator: "\n", omittingEmptySubsequences: false).count
    }

    private func color(_ argb: Int32) -> Color {
        let value = UInt32(bitPattern: argb)
        return Color(
            .sRGB,
            red: Double((value >> 16) & 0xFF) / 255,
            green: Double((value >> 8) & 0xFF) / 255,
            blue: Double(value & 0xFF) / 255,
            opacity: Double(value >> 24) / 255)
    }
}

/// One key on the bar.
private struct KeyCap: View {
    var label: String?
    var systemImage: String?
    var latched = false
    let action: () -> Void

    init(label: String, latched: Bool = false, action: @escaping () -> Void) {
        self.label = label
        self.latched = latched
        self.action = action
    }

    init(systemImage: String, action: @escaping () -> Void) {
        self.systemImage = systemImage
        self.action = action
    }

    var body: some View {
        Button(action: action) {
            Group {
                if let systemImage {
                    Image(systemName: systemImage)
                } else {
                    Text(label ?? "")
                }
            }
            .font(.system(size: 14, design: .monospaced))
            .foregroundStyle(Color(white: 0.85))
            .padding(.horizontal, 12)
            .frame(height: 36)
            .background(
                RoundedRectangle(cornerRadius: 6)
                    .fill(latched ? Color(red: 0.23, green: 0.43, blue: 0.65) : Color(red: 0.165, green: 0.188, blue: 0.22)))
        }
        .buttonStyle(.plain)
    }
}

/// The on-screen keyboard's way in: a view that takes text the way a text field would, without a text
/// field's editing, autocorrection or selection getting between the keys and the shell.
private struct TerminalInput: UIViewRepresentable {
    @Binding var focused: Bool
    let onText: (String) -> Void
    let onBackspace: () -> Void
    let onKey: (UIKey) -> Bool

    func makeUIView(context: Context) -> InputView {
        InputView()
    }

    func updateUIView(_ view: InputView, context: Context) {
        view.onText = onText
        view.onBackspace = onBackspace
        view.onKey = onKey
        view.onResign = { if focused { focused = false } }
        DispatchQueue.main.async {
            if focused, !view.isFirstResponder, view.window != nil {
                view.becomeFirstResponder()
            } else if !focused, view.isFirstResponder {
                view.resignFirstResponder()
            }
        }
    }

    final class InputView: UIView, UIKeyInput {
        var onText: (String) -> Void = { _ in }
        var onBackspace: () -> Void = {}
        var onKey: (UIKey) -> Bool = { _ in false }
        var onResign: () -> Void = {}

        override var canBecomeFirstResponder: Bool { true }

        override func didMoveToWindow() {
            super.didMoveToWindow()
            if window != nil {
                becomeFirstResponder()
            }
        }

        @discardableResult
        override func resignFirstResponder() -> Bool {
            let resigned = super.resignFirstResponder()
            if resigned { onResign() }
            return resigned
        }

        // Always true, so a backspace on an empty line still reaches the shell.
        var hasText: Bool { true }

        func insertText(_ text: String) { onText(text) }

        func deleteBackward() { onBackspace() }

        // A shell is not prose: nothing may be corrected, capitalised or "smartened" on its way there.
        var autocorrectionType: UITextAutocorrectionType = .no
        var autocapitalizationType: UITextAutocapitalizationType = .none
        var spellCheckingType: UITextSpellCheckingType = .no
        var smartQuotesType: UITextSmartQuotesType = .no
        var smartDashesType: UITextSmartDashesType = .no
        var smartInsertDeleteType: UITextSmartInsertDeleteType = .no
        var keyboardType: UIKeyboardType = .asciiCapable
        var returnKeyType: UIReturnKeyType = .default

        override func pressesBegan(_ presses: Set<UIPress>, with event: UIPressesEvent?) {
            var rest = Set<UIPress>()
            for press in presses {
                if let key = press.key, onKey(key) { continue }
                rest.insert(press)
            }
            if !rest.isEmpty {
                super.pressesBegan(rest, with: event)
            }
        }
    }
}
