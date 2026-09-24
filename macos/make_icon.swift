// Draws the app icon into an .iconset folder: `swift make_icon.swift out.iconset`, then iconutil.
// A sibling of Granola's icon: the same dark tile and lime green, but the spiral unwinds into an
// arrow heading up and out, for lectures leaving Granola for your library. On Apple's macOS grid (an 824-pt rounded
// square on a 1024 canvas).

import AppKit

let out = URL(fileURLWithPath: CommandLine.arguments[1])
try? FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)

func color(_ hex: UInt32, _ a: CGFloat = 1) -> CGColor {
    CGColor(red: CGFloat((hex >> 16) & 0xff) / 255, green: CGFloat((hex >> 8) & 0xff) / 255,
            blue: CGFloat(hex & 0xff) / 255, alpha: a)
}

func gradient(_ colors: [CGColor], _ stops: [CGFloat]) -> CGGradient {
    CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(), colors: colors as CFArray, locations: stops)!
}

/// An Archimedean spiral from the middle outwards that ends at `endAngle` (after `rounds` full turns),
/// then runs straight on for `tail`. Returns the path, where it ends, and which way it points there.
func spiral(center c: CGPoint, rounds: Int, endAngle: CGFloat, inner: CGFloat, outer: CGFloat,
            tail: CGFloat) -> (CGPath, CGPoint, CGFloat) {
    let path = CGMutablePath()
    let steps = 540
    let start: CGFloat = .pi * 0.35  // begin a little way round, so the middle isn't a dot
    let end = endAngle + CGFloat(rounds) * 2 * .pi
    var last = CGPoint.zero, prev = CGPoint.zero
    for i in 0...steps {
        let t = start + (end - start) * CGFloat(i) / CGFloat(steps)
        let r = inner + (outer - inner) * (t - start) / (end - start)
        let p = CGPoint(x: c.x + r * cos(t), y: c.y + r * sin(t))
        if i == 0 { path.move(to: p) } else { path.addLine(to: p) }
        prev = last
        last = p
    }
    let a = atan2(last.y - prev.y, last.x - prev.x)
    let tip = CGPoint(x: last.x + tail * cos(a), y: last.y + tail * sin(a))
    path.addLine(to: tip)
    return (path, tip, a)
}

func icon(_ px: Int) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let ctx = NSGraphicsContext.current!.cgContext
    ctx.scaleBy(x: CGFloat(px) / 1024, y: CGFloat(px) / 1024)  // design in 1024 units
    let small = px <= 64  // at Finder-list sizes: fewer turns, a thicker line, so it still reads

    let tile = CGRect(x: 100, y: 100, width: 824, height: 824)
    let tilePath = CGPath(roundedRect: tile, cornerWidth: 185, cornerHeight: 185, transform: nil)
    ctx.saveGState()  // the drop shadow macOS icons sit on
    ctx.setShadow(offset: CGSize(width: 0, height: -10), blur: 24, color: color(0x000000, 0.35))
    ctx.addPath(tilePath)
    ctx.setFillColor(color(0x1b1f1b))
    ctx.fillPath()
    ctx.restoreGState()

    ctx.saveGState()
    ctx.addPath(tilePath)
    ctx.clip()
    ctx.drawLinearGradient(gradient([color(0x333a32), color(0x1c201c), color(0x121512)], [0, 0.55, 1]),
                           start: CGPoint(x: 0, y: tile.maxY), end: CGPoint(x: 0, y: tile.minY), options: [])
    // a soft lime glow behind the spiral
    ctx.drawRadialGradient(gradient([color(0xb7d84b, 0.20), color(0xb7d84b, 0)], [0, 1]),
                           startCenter: CGPoint(x: 470, y: 530), startRadius: 0,
                           endCenter: CGPoint(x: 470, y: 530), endRadius: 400, options: [])

    let width: CGFloat = small ? 78 : 58
    // It unwinds at the lower right and heads off up and to the right: out of Granola, to the library.
    let (path, tip, angle) = spiral(center: CGPoint(x: 432, y: 530), rounds: small ? 1 : 2, endAngle: -.pi * 0.12,
                                    inner: small ? 42 : 30, outer: small ? 226 : 236, tail: small ? 110 : 150)
    // the arrowhead where the spiral leaves: a chevron along the line's last direction
    let arrow = CGMutablePath()
    let len: CGFloat = small ? 118 : 104, spread: CGFloat = .pi * 0.26
    let head = tip
    arrow.move(to: CGPoint(x: head.x - len * cos(angle - spread), y: head.y - len * sin(angle - spread)))
    arrow.addLine(to: head)
    arrow.addLine(to: CGPoint(x: head.x - len * cos(angle + spread), y: head.y - len * sin(angle + spread)))

    for p in [path, arrow as CGPath] {
        ctx.saveGState()
        ctx.setShadow(offset: CGSize(width: 0, height: -6), blur: 16, color: color(0x000000, 0.45))
        ctx.addPath(p)
        ctx.setLineWidth(width)
        ctx.setLineCap(.round)
        ctx.setLineJoin(.round)
        ctx.replacePathWithStrokedPath()
        ctx.clip()
        ctx.drawLinearGradient(gradient([color(0xe4f07a), color(0xc2de52), color(0x8fbf36)], [0, 0.5, 1]),
                               start: CGPoint(x: 0, y: 800), end: CGPoint(x: 0, y: 200), options: [])
        ctx.restoreGState()
    }
    ctx.restoreGState()

    // the fine light edge along the top of the tile, as on Apple's icons
    ctx.saveGState()
    ctx.addPath(CGPath(roundedRect: tile.insetBy(dx: 1.5, dy: 1.5), cornerWidth: 184, cornerHeight: 184, transform: nil))
    ctx.setLineWidth(3)
    ctx.setStrokeColor(color(0xffffff, 0.08))
    ctx.strokePath()
    ctx.restoreGState()

    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

for (name, px) in [("16x16", 16), ("16x16@2x", 32), ("32x32", 32), ("32x32@2x", 64), ("128x128", 128),
                   ("128x128@2x", 256), ("256x256", 256), ("256x256@2x", 512), ("512x512", 512), ("512x512@2x", 1024)] {
    try! icon(px).write(to: out.appendingPathComponent("icon_\(name).png"))
}
