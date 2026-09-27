#!/bin/bash
# Renders assets/logo/deskpair.svg into build/macos/DeskPair.icns. Run on a Mac; AppKit is the renderer,
# so nothing has to be installed. The result is committed, because packaging should not depend on it.
#
# The art is drawn at 82.4% of each canvas rather than edge to edge. That is the proportion Apple's own
# icons use for a rounded square, and an icon that ignores it sits visibly larger than everything beside
# it in the Dock.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SVG="$ROOT/assets/logo/deskpair.svg"
SET="$(mktemp -d)/DeskPair.iconset"
mkdir -p "$SET"

cat > "$SET/../render.swift" <<'SWIFT'
import AppKit

let args = CommandLine.arguments
guard args.count == 4, let image = NSImage(contentsOf: URL(fileURLWithPath: args[1])) else {
    FileHandle.standardError.write("usage: render <svg> <size> <out.png>\n".data(using: .utf8)!)
    exit(2)
}
let size = Int(args[2])!
let inset = (CGFloat(size) * (1 - 0.824) / 2).rounded()
let side = CGFloat(size) - inset * 2

guard let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size,
                                    bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                    colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { exit(1) }
bitmap.size = NSSize(width: size, height: size)
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
NSGraphicsContext.current?.imageInterpolation = .high
image.draw(in: NSRect(x: inset, y: inset, width: side, height: side),
           from: .zero, operation: .sourceOver, fraction: 1)
NSGraphicsContext.restoreGraphicsState()

guard let png = bitmap.representation(using: .png, properties: [:]) else { exit(1) }
try png.write(to: URL(fileURLWithPath: args[3]))
SWIFT

swiftc -O -o "$SET/../render" "$SET/../render.swift" 2>/dev/null ||
    { echo "swiftc failed; is Xcode installed?" >&2; exit 1; }

for pair in "16 16x16" "32 16x16@2x" "32 32x32" "64 32x32@2x" "128 128x128" "256 128x128@2x" \
            "256 256x256" "512 256x256@2x" "512 512x512" "1024 512x512@2x"; do
    set -- $pair
    "$SET/../render" "$SVG" "$1" "$SET/icon_$2.png"
done

iconutil --convert icns --output "$ROOT/build/macos/DeskPair.icns" "$SET"
rm -rf "$(dirname "$SET")"
echo "wrote build/macos/DeskPair.icns"
