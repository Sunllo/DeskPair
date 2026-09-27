import SwiftUI
import DeskPairShared

/// Fingers, forwarded to the shared interpreter.
///
/// SwiftUI's gesture recognisers cannot express this either. The previous version stacked a `DragGesture`
/// and a `LongPressGesture` with `.simultaneousGesture`, which did exactly what it says: a long press fired
/// the right click *and* left the drag running, so the host received a left button pressed before it and
/// released after. Multi-touch was simply absent — no pinch, no pan, no scroll.
///
/// So the raw touches are read through a `UIView` instead, and what any of them mean is decided once, in
/// `TouchInterpreter`, for both platforms.
struct RemoteTouchView: UIViewRepresentable {
    let model: SessionModel

    func makeUIView(context: Context) -> TouchForwardingView {
        let view = TouchForwardingView()
        view.model = model
        view.isMultipleTouchEnabled = true
        view.backgroundColor = .clear
        return view
    }

    func updateUIView(_ uiView: TouchForwardingView, context: Context) {
        uiView.model = model
    }
}

/// Reads UIKit touches and hands the whole current set over on every change.
///
/// Passing the full set rather than deltas is what gets the finger-count transitions right for free: the
/// interpreter can see a gesture become two-fingered without this view having to track identities.
final class TouchForwardingView: UIView {
    weak var model: SessionModel?

    private var active: [ObjectIdentifier: UITouch] = [:]
    private var firstTouchPoint: CGPoint = .zero

    /// How quickly a second tap must follow to mean "and hold the button while I drag".
    private let doubleTapWindow: TimeInterval = 0.3
    private var lastTouchEnded: Date?

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        let wasEmpty = active.isEmpty
        for touch in touches {
            active[ObjectIdentifier(touch)] = touch
        }

        if wasEmpty {
            firstTouchPoint = touches.first?.location(in: self) ?? .zero

            // A second tap arriving within the window, and held, is the trackpad's drag gesture.
            if let ended = lastTouchEnded, Date().timeIntervalSince(ended) < doubleTapWindow {
                model?.onDoubleTapDrag(firstTouchPoint)
            }
        }

        forward()
    }

    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
        forward()
    }

    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        for touch in touches {
            active.removeValue(forKey: ObjectIdentifier(touch))
        }

        if active.isEmpty {
            lastTouchEnded = Date()
        }

        // The right click used to be recognised here, when two fingers left together. It is now read from
        // the fingers themselves in TouchInterpreter, which sees the whole gesture: fingers rarely leave
        // the glass in the same event, and this could not tell a tap from the end of a pinch.
        forward()
    }

    /// A call, a system gesture, the app going away. A held button must not be left held.
    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) {
        active.removeAll()
        model?.onTouchCancelled()
    }

    private func forward() {
        let pointers = active.values.map { touch -> Touch in
            let at = touch.location(in: self)
            return Touch(
                // The touch object's address, reinterpreted. Two conversions to get wrong and the previous
                // version got both: it hashed the identifier, which yields an Int that is negative about
                // half the time, widened those bits into a UInt, and then *converted* that to Int64 --
                // which traps for every value above Int64.max. So roughly one touch in two killed the app
                // with "Not enough bits to represent the passed value", on the first tap of a session.
                //
                // bitPattern reinterprets instead of converting, so nothing can overflow, and the address
                // is unique among touches that are live at the same time where a hash is merely unlikely
                // to collide.
                id: Int64(bitPattern: UInt64(UInt(bitPattern: ObjectIdentifier(touch)))),
                x: Float(at.x),
                y: Float(at.y)
            )
        }
        model?.onTouch(pointers)
    }
}
