// Renders an SVG to a PNG at an exact size, with or without an alpha channel.
//
// AppKit is the renderer so that producing store artwork needs nothing installed beyond Xcode, which the
// build machine has anyway.
//
//   render <svg> <width> <height> <out.png> <opaque:0|1>
import AppKit

let args = CommandLine.arguments
guard args.count == 6,
      let image = NSImage(contentsOf: URL(fileURLWithPath: args[1])),
      let width = Int(args[2]), let height = Int(args[3]) else {
    FileHandle.standardError.write("usage: render <svg> <width> <height> <out.png> <opaque:0|1>\n".data(using: .utf8)!)
    exit(2)
}
let opaque = args[5] == "1"

// Always drawn into an RGBA bitmap, because that is the one shape NSBitmapImageRep accepts without
// argument. An opaque result is made by painting white first and then dropping the channel on the way
// out; asking for a three-sample bitmap up front traps inside AppKit with no message at all.
guard let canvas = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: width, pixelsHigh: height,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { exit(1) }

canvas.size = NSSize(width: width, height: height)
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: canvas)
NSGraphicsContext.current?.imageInterpolation = .high
let rect = NSRect(x: 0, y: 0, width: width, height: height)
if opaque {
    NSColor.white.setFill()
    rect.fill()
}
image.draw(in: rect, from: .zero, operation: .sourceOver, fraction: 1)
NSGraphicsContext.restoreGraphicsState()

var output = canvas
if opaque {
    // Google Play refuses an alpha channel on the feature graphic, and a fully opaque RGBA file still has
    // one. Re-drawing through a CGContext with .noneSkipLast is what actually removes it.
    guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8,
                                  bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
                                  bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue),
          let drawn = canvas.cgImage else { exit(1) }
    context.draw(drawn, in: CGRect(x: 0, y: 0, width: width, height: height))
    guard let flattened = context.makeImage() else { exit(1) }
    output = NSBitmapImageRep(cgImage: flattened)
}

guard let png = output.representation(using: .png, properties: [:]) else { exit(1) }
try png.write(to: URL(fileURLWithPath: args[4]))
