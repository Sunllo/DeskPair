import SwiftUI
import DeskPairShared

/// The host's pointer, ready to draw.
///
/// Shapes are kept by id because that is how the host sends them: the bitmap travels once, and after that
/// the host just names it. Converting BGRA to a CGImage is not free, and a session crossing window edges
/// would otherwise pay for it several times a second.
struct RemoteCursorState {
    var x: Int
    var y: Int
    var shapeId: Int64
    var shapes: [Int64: CursorShape]

    var shape: CursorShape? { shapes[shapeId] }

    static let empty = RemoteCursorState(x: 0, y: 0, shapeId: 0, shapes: [:])
}

/// One cursor bitmap and the point inside it that actually points.
struct CursorShape {
    let hotX: Int
    let hotY: Int
    let image: CGImage
}

/// Bridges the shared sink to the model, for the same reason the video and audio ones exist.
final class SwiftCursorSink: NSObject, NativeCursorSink {
    private weak var model: SessionModel?

    init(model: SessionModel) {
        self.model = model
    }

    func onCursorShape(id: Int64, hotX: Int32, hotY: Int32, width: Int32, height: Int32, bgra_: Data) {
        guard let image = Self.decode(width: Int(width), height: Int(height), bgra: bgra_) else { return }
        let shape = CursorShape(hotX: Int(hotX), hotY: Int(hotY), image: image)
        Task { @MainActor in self.model?.applyCursorShape(id: id, shape: shape) }
    }

    func onCursorPosition(x: Int32, y: Int32) {
        Task { @MainActor in self.model?.applyCursorPosition(x: Int(x), y: Int(y)) }
    }

    func onCursorShapeChanged(id: Int64) {
        Task { @MainActor in self.model?.applyCursorShapeId(id) }
    }

    /// BGRA premultiplied, as the protocol carries it, into something Core Graphics will draw.
    private static func decode(width: Int, height: Int, bgra: Data) -> CGImage? {
        guard width > 0, height > 0, bgra.count >= width * height * 4 else { return nil }

        guard let provider = CGDataProvider(data: bgra as CFData) else { return nil }
        return CGImage(
            width: width,
            height: height,
            bitsPerComponent: 8,
            bitsPerPixel: 32,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            // Little-endian 32-bit with premultiplied alpha first is how BGRA reads on this architecture.
            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedFirst.rawValue)
                .union(.byteOrder32Little),
            provider: provider,
            decode: nil,
            shouldInterpolate: false,
            intent: .defaultIntent
        )
    }
}

/// Drawn over the picture, offset by its hotspot, never smaller than a fingertip can aim at.
struct RemoteCursorOverlay: View {
    let cursor: RemoteCursorState?
    let canvas: CanvasState

    /// Below this the pointer becomes impossible to see, let alone aim with, on a zoomed-out desktop.
    private let minimumPoints: CGFloat = 18

    var body: some View {
        if let cursor, let shape = cursor.shape {
            let at = canvas.toView(hostX: Int32(cursor.x), hostY: Int32(cursor.y))
            let natural = CGFloat(shape.image.width) * CGFloat(canvas.scale)
            let width = max(natural, minimumPoints)
            let ratio = width / CGFloat(shape.image.width)
            let height = CGFloat(shape.image.height) * ratio

            Image(decorative: shape.image, scale: 1)
                .resizable()
                .frame(width: width, height: height)
                .position(
                    x: CGFloat(at.x) - CGFloat(shape.hotX) * ratio + width / 2,
                    y: CGFloat(at.y) - CGFloat(shape.hotY) * ratio + height / 2
                )
                .allowsHitTesting(false)
        }
    }
}
