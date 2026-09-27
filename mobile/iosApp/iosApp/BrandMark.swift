import SwiftUI

/// The DeskPair mark, drawn rather than shipped.
///
/// The same 256-unit construction as `assets/logo/deskpair.svg` and
/// `tools/DeskPair.Tools.IconGen/Program.cs`, and as `BrandMark.kt` on the Android side: a 176 square
/// rotated 45 degrees with corner radius 30, crossed by two 24-wide chevrons offset 16 units above and below
/// the centre line. The app icon comes out of the asset catalogue; this is the mark inside the app, and
/// drawing it means the two cannot drift apart at different sizes.
///
/// The stagger is the mark. The two chevrons are not a mirrored pair on one baseline: the left-pointing one
/// sits low, the right-pointing one high, so they read as two arrows passing each other in opposite
/// directions. Anything that "tidies" them into alignment has thrown the meaning away.
struct BrandMark: View {
    var side: CGFloat = 64

    var body: some View {
        Canvas { context, size in
            // Everything below is in the logo's own 256 units; this is the only place the real size enters.
            let u = min(size.width, size.height) / 256
            func at(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: x * u, y: y * u) }

            let diamond = Path(
                roundedRect: CGRect(x: 40 * u, y: 40 * u, width: 176 * u, height: 176 * u),
                cornerSize: CGSize(width: 30 * u, height: 30 * u)
            )

            var body = context
            body.translateBy(x: 128 * u, y: 128 * u)
            body.rotate(by: .degrees(45))
            body.translateBy(x: -128 * u, y: -128 * u)

            // The gradient axis is the unrotated square's own diagonal, which the rotation stands upright:
            // on screen the diamond runs pale cyan at its top point down to near-black navy at its bottom.
            body.fill(
                diamond,
                with: .linearGradient(
                    Gradient(stops: [
                        .init(color: Logo.sky, location: 0),
                        .init(color: Logo.cyan, location: 0.35),
                        .init(color: Logo.azure, location: 0.72),
                        .init(color: Logo.navy, location: 1),
                    ]),
                    startPoint: at(46, 46),
                    endPoint: at(210, 210)
                )
            )

            // A gloss bloom near the top vertex. Without it the diamond is a flat lozenge.
            body.fill(
                diamond,
                with: .radialGradient(
                    Gradient(stops: [
                        .init(color: .white.opacity(97.0 / 255.0), location: 0),
                        .init(color: .white.opacity(15.0 / 255.0), location: 0.55),
                        .init(color: .white.opacity(0), location: 1),
                    ]),
                    center: at(92, 80),
                    startRadius: 0,
                    endRadius: 150 * u
                )
            )

            // Outside the rotation: the chevrons are upright, and that is what sets them against the diamond.
            let stroke = StrokeStyle(lineWidth: 24 * u, lineCap: .round, lineJoin: .round)
            context.stroke(bend(at(117, 100), at(74, 144), at(117, 188)), with: .color(.white), style: stroke)
            context.stroke(bend(at(139, 68), at(182, 112), at(139, 156)), with: .color(.white), style: stroke)
        }
        .frame(width: side, height: side)
        .accessibilityHidden(true)
    }

    private func bend(_ from: CGPoint, _ apex: CGPoint, _ to: CGPoint) -> Path {
        var path = Path()
        path.move(to: from)
        path.addLine(to: apex)
        path.addLine(to: to)
        return path
    }
}

/// The mark's own colours, for the brand mark and nothing else.
///
/// Everything in the interface takes its colour from the system's semantic set and its accent from the
/// `AccentColor` asset. These four are the logo gradient, quoted from IconGen, and they belong to the
/// drawing above rather than to the palette.
enum Logo {
    static let sky = Color(red: 0x74 / 255, green: 0xD7 / 255, blue: 0xEA / 255)
    static let cyan = Color(red: 0x0A / 255, green: 0xA9 / 255, blue: 0xD1 / 255)
    static let azure = Color(red: 0x41 / 255, green: 0x8B / 255, blue: 0xD6 / 255)
    static let navy = Color(red: 0x00 / 255, green: 0x3C / 255, blue: 0x7E / 255)
}
