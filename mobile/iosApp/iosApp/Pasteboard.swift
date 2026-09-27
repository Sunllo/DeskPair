import UIKit
import UniformTypeIdentifiers
import DeskPairShared

/// This device's pasteboard, in both directions — but not symmetrically, because iOS is not symmetric.
///
/// Writing what the host copied costs nothing and needs no consent. Reading is the other way round: since
/// iOS 16 a programmatic read of the general pasteboard raises a system "Allow Paste?" prompt, and reads
/// from the background are refused outright. So this never polls. Sending the phone's clipboard is an
/// action the user takes, and the prompt then arrives in answer to something they just asked for rather
/// than out of nowhere while they were working.
///
/// Pictures go one way only, phone to desk. Nothing pushes the host's screenshots onto this device.
final class Pasteboard: NSObject, ClipboardBridge {

    /// How far a picture is halved before being given up on. Eight halvings take a hundred megapixels
    /// under one, which is well inside the clipboard's budget.
    private static let maxHalvings = 8

    /// Whether there is anything to offer. iOS answers both of these *without* prompting, so the control
    /// can be disabled rather than prompting only to find nothing there.
    var hasContent: Bool { UIPasteboard.general.hasStrings || UIPasteboard.general.hasImages }

    var hasText: Bool { UIPasteboard.general.hasStrings }

    var hasImage: Bool { UIPasteboard.general.hasImages }

    /// Reads the pasteboard's text. This is what raises the system prompt, so it is only ever called from
    /// a deliberate user action.
    func read() -> String? {
        UIPasteboard.general.string?.nonEmpty
    }

    /// The pasteboard's picture as PNG, small enough to carry.
    ///
    /// The raw PNG is asked for first: when an app already put one there those bytes go out untouched,
    /// which is both faster and lossless. `UIPasteboard.image` is the fallback, and it has already decoded
    /// whatever was there into a UIImage, so re-encoding is the only option left.
    func readImage() -> Data? {
        if let png = UIPasteboard.general.data(forPasteboardType: UTType.png.identifier),
           png.count <= Int(ClipboardSync.companion.MAX_BYTES) {
            return png
        }

        guard let image = UIPasteboard.general.image else { return nil }
        return Self.encode(image)
    }

    /// PNG bytes for an image, halving it until they fit.
    ///
    /// The shared layer refuses anything over its limit rather than truncating it, so the shrinking has to
    /// happen here — this is the side with an imaging stack that knows what the picture is.
    static func encode(_ image: UIImage) -> Data? {
        var current = image
        for _ in 0...maxHalvings {
            guard let png = current.pngData() else { return nil }
            if png.count <= Int(ClipboardSync.companion.MAX_BYTES) {
                if current !== image {
                    sessionLog.notice("picture scaled to \(Int(current.size.width))x\(Int(current.size.height)) to fit the clipboard")
                }

                return png
            }

            let half = CGSize(width: max(1, current.size.width / 2), height: max(1, current.size.height / 2))
            let renderer = UIGraphicsImageRenderer(size: half)
            current = renderer.image { _ in current.draw(in: CGRect(origin: .zero, size: half)) }
        }

        return nil
    }

    func onRemoteText(text: String) {
        // Called from the Kotlin side on its own thread; UIPasteboard wants the main one.
        DispatchQueue.main.async {
            UIPasteboard.general.string = text
            sessionLog.debug("clipboard from host: \(text.count) character(s)")
        }
    }
}

private extension String {
    var nonEmpty: String? { isEmpty ? nil : self }
}
