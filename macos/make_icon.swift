// Draws the app icon: `swift make_icon.swift out.iconset [style]`, then iconutil. With an output
// ending in .png it writes one 1024-px picture instead (for comparing styles).
// Every style is a sibling of Granola's icon (a lime spiral on a dark tile), on Apple's macOS grid:
// an 824-pt rounded square on a 1024 canvas. The app uses `folder`; the others are kept to compare.
//   arrow   the spiral unwinds into an arrow heading up and out: lectures leaving for your library
//   unroll  the spiral unrolls into lines of notes: a transcript becoming study notes
//   folder  a lime folder with the spiral pressed into it: your library of lectures
//   card    a white note card with a lime spiral, tipped as if being filed
//   bold    the arrow, dark on a lime tile

import AppKit

let args = CommandLine.arguments
let out = URL(fileURLWithPath: args[1])
let style = args.count > 2 ? args[2] : "folder"  // the one Alex picked

func color(_ hex: UInt32, _ a: CGFloat = 1) -> CGColor {
    CGColor(red: CGFloat((hex >> 16) & 0xff) / 255, green: CGFloat((hex >> 8) & 0xff) / 255,
            blue: CGFloat(hex & 0xff) / 255, alpha: a)
}

func gradient(_ colors: [CGColor], _ stops: [CGFloat]) -> CGGradient {
    CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(), colors: colors as CFArray, locations: stops)!
}

let lime = [color(0xe4f07a), color(0xc2de52), color(0x8fbf36)]
let dark = [color(0x333a32), color(0x1c201c), color(0x121512)]
let tile = CGRect(x: 100, y: 100, width: 824, height: 824)
let tilePath = CGPath(roundedRect: tile, cornerWidth: 185, cornerHeight: 185, transform: nil)

/// An Archimedean spiral from the middle outwards that ends at `endAngle` after `rounds` full turns,
/// then runs straight on for `tail`. Returns the path, where it ends, and which way it points there.
func spiral(_ c: CGPoint, rounds: CGFloat, endAngle: CGFloat, inner: CGFloat, outer: CGFloat,
            tail: CGFloat = 0) -> (CGMutablePath, CGPoint, CGFloat) {
    let path = CGMutablePath()
    let steps = 540
    let start: CGFloat = .pi * 0.35  // begin a little way round, so the middle isn't a dot
    let end = endAngle + rounds * 2 * .pi
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
    if tail > 0 { path.addLine(to: tip) }
    return (path, tip, a)
}

func chevron(at head: CGPoint, pointing a: CGFloat, length: CGFloat) -> CGPath {
    let p = CGMutablePath(), spread: CGFloat = .pi * 0.26
    p.move(to: CGPoint(x: head.x - length * cos(a - spread), y: head.y - length * sin(a - spread)))
    p.addLine(to: head)
    p.addLine(to: CGPoint(x: head.x - length * cos(a + spread), y: head.y - length * sin(a + spread)))
    return p
}

/// Fills the rounded tile with a top-to-bottom gradient, under the shadow macOS icons sit on.
func drawTile(_ ctx: CGContext, _ colors: [CGColor], glow: CGPoint? = nil) {
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -10), blur: 24, color: color(0x000000, 0.35))
    ctx.addPath(tilePath)
    ctx.setFillColor(colors[1])
    ctx.fillPath()
    ctx.restoreGState()
    ctx.saveGState()
    ctx.addPath(tilePath)
    ctx.clip()
    ctx.drawLinearGradient(gradient(colors, [0, 0.55, 1]), start: CGPoint(x: 0, y: tile.maxY),
                           end: CGPoint(x: 0, y: tile.minY), options: [])
    if let g = glow {
        ctx.drawRadialGradient(gradient([color(0xb7d84b, 0.2), color(0xb7d84b, 0)], [0, 1]), startCenter: g,
                               startRadius: 0, endCenter: g, endRadius: 400, options: [])
    }
    ctx.restoreGState()
}

func tileEdge(_ ctx: CGContext) {  // the fine light edge along the tile, as on Apple's icons
    ctx.saveGState()
    ctx.addPath(CGPath(roundedRect: tile.insetBy(dx: 1.5, dy: 1.5), cornerWidth: 184, cornerHeight: 184, transform: nil))
    ctx.setLineWidth(3)
    ctx.setStrokeColor(color(0xffffff, 0.08))
    ctx.strokePath()
    ctx.restoreGState()
}

/// Strokes `path` with round ends, filled with `colors` top to bottom, over a soft shadow.
func stroke(_ ctx: CGContext, _ path: CGPath, width: CGFloat, _ colors: [CGColor], shadow: CGFloat = 0.45) {
    ctx.saveGState()
    if shadow > 0 { ctx.setShadow(offset: CGSize(width: 0, height: -6), blur: 16, color: color(0x000000, shadow)) }
    ctx.addPath(path)
    ctx.setLineWidth(width)
    ctx.setLineCap(.round)
    ctx.setLineJoin(.round)
    ctx.replacePathWithStrokedPath()
    ctx.clip()
    ctx.drawLinearGradient(gradient(colors, colors.count == 3 ? [0, 0.5, 1] : [0, 1]), start: CGPoint(x: 0, y: 820),
                           end: CGPoint(x: 0, y: 200), options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
    ctx.restoreGState()
}

func fill(_ ctx: CGContext, _ path: CGPath, _ colors: [CGColor], top: CGFloat, bottom: CGFloat, shadow: CGFloat = 0) {
    ctx.saveGState()
    if shadow > 0 { ctx.setShadow(offset: CGSize(width: 0, height: -12), blur: 30, color: color(0x000000, shadow)) }
    ctx.addPath(path)
    ctx.setFillColor(colors.last!)
    ctx.fillPath()
    ctx.restoreGState()
    ctx.saveGState()
    ctx.addPath(path)
    ctx.clip()
    ctx.drawLinearGradient(gradient(colors, colors.count == 3 ? [0, 0.5, 1] : [0, 1]), start: CGPoint(x: 0, y: top),
                           end: CGPoint(x: 0, y: bottom), options: [])
    ctx.restoreGState()
}

func rounded(_ r: CGRect, _ radius: CGFloat) -> CGPath {
    CGPath(roundedRect: r, cornerWidth: radius, cornerHeight: radius, transform: nil)
}

// MARK: - the styles

func arrow(_ ctx: CGContext, small: Bool, ink: [CGColor], shadow: CGFloat) {
    let w: CGFloat = small ? 78 : 58
    let (path, tip, a) = spiral(CGPoint(x: 432, y: 530), rounds: small ? 1 : 2, endAngle: -.pi * 0.12,
                                inner: small ? 42 : 30, outer: small ? 226 : 236, tail: small ? 110 : 150)
    stroke(ctx, path, width: w, ink, shadow: shadow)
    stroke(ctx, chevron(at: tip, pointing: a, length: small ? 118 : 104), width: w, ink, shadow: shadow)
}

func unroll(_ ctx: CGContext, small: Bool) {
    let w: CGFloat = small ? 74 : 54
    // ends at the bottom heading right, then carries straight on as the first line of notes
    let c = CGPoint(x: 400, y: small ? 624 : 650), outer: CGFloat = small ? 170 : 176
    let (path, tip, _) = spiral(c, rounds: small ? 1 : 2, endAngle: -.pi / 2,
                                inner: small ? 40 : 26, outer: outer, tail: small ? 300 : 330)
    stroke(ctx, path, width: w, lime)
    let gap: CGFloat = small ? 128 : 104
    let lengths: [CGFloat] = small ? [0.7] : [0.84, 0.6]
    for (i, len) in lengths.enumerated() {
        let y = tip.y - gap * CGFloat(i + 1)
        let line = CGMutablePath()
        let x0: CGFloat = c.x  // under the first line, like a paragraph
        line.move(to: CGPoint(x: x0, y: y))
        line.addLine(to: CGPoint(x: x0 + (tip.x - x0) * len, y: y))
        stroke(ctx, line, width: w, [color(0xc2de52, 0.55), color(0x8fbf36, 0.55)], shadow: 0.3)
    }
}

func folder(_ ctx: CGContext, small: Bool) {
    // the folder's back with its tab, then the front, the way Finder draws folders
    let back = CGMutablePath()
    let bx: CGFloat = 212, by: CGFloat = 268, bw: CGFloat = 600, bh: CGFloat = 470
    back.addPath(rounded(CGRect(x: bx, y: by, width: bw, height: bh - 40), 46))
    back.addPath(rounded(CGRect(x: bx, y: by + bh - 110, width: 250, height: 110), 40))
    fill(ctx, back, [color(0x9fc93e), color(0x6f9a22)], top: by + bh, bottom: by, shadow: 0.4)
    let front = rounded(CGRect(x: bx, y: by, width: bw, height: bh - 108), 46)
    fill(ctx, front, lime, top: by + bh - 108, bottom: by, shadow: 0.25)
    // the spiral pressed into the front
    let (path, _, _) = spiral(CGPoint(x: bx + bw / 2, y: by + (bh - 108) / 2), rounds: small ? 1 : 1.6,
                              endAngle: .pi * 0.15, inner: small ? 20 : 14, outer: small ? 108 : 118)
    stroke(ctx, path, width: small ? 40 : 30, [color(0x2b3a12, 0.75), color(0x2b3a12, 0.75)], shadow: 0)
}

func card(_ ctx: CGContext, small: Bool) {
    ctx.saveGState()
    ctx.translateBy(x: 512, y: 512)
    ctx.rotate(by: -.pi / 30)
    ctx.translateBy(x: -512, y: -512)
    let r = CGRect(x: 262, y: 222, width: 500, height: 590)
    fill(ctx, rounded(r, 52), [color(0xffffff), color(0xeef0ea)], top: r.maxY, bottom: r.minY, shadow: 0.5)
    let (path, _, _) = spiral(CGPoint(x: r.minX + 150, y: r.maxY - 150), rounds: small ? 1 : 1.5, endAngle: .pi * 0.1,
                              inner: small ? 20 : 12, outer: small ? 82 : 88)
    stroke(ctx, path, width: small ? 40 : 30, lime, shadow: 0)
    // the note's lines
    let lines: [(CGFloat, UInt32)] = small ? [(0.8, 0x2a2f2a), (0.6, 0xc9cec4)]
        : [(0.8, 0x2a2f2a), (0.66, 0xc9cec4), (0.74, 0xc9cec4), (0.5, 0xc9cec4)]
    for (i, (len, c)) in lines.enumerated() {
        let y = r.maxY - 318 - CGFloat(i) * (small ? 84 : 62)
        ctx.addPath(rounded(CGRect(x: r.minX + 72, y: y, width: (r.width - 144) * len, height: small ? 40 : 28), small ? 20 : 14))
        ctx.setFillColor(color(c))
        ctx.fillPath()
    }
    ctx.restoreGState()
}

// MARK: - drawing one size

func icon(_ px: Int) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let ctx = NSGraphicsContext.current!.cgContext
    ctx.scaleBy(x: CGFloat(px) / 1024, y: CGFloat(px) / 1024)  // design in 1024 units
    let small = px <= 64  // at Finder-list sizes: simpler, thicker lines, so it still reads

    if style == "bold" {
        drawTile(ctx, lime)
    } else {
        drawTile(ctx, dark, glow: style == "arrow" || style == "unroll" ? CGPoint(x: 470, y: 530) : nil)
    }
    ctx.saveGState()
    ctx.addPath(tilePath)
    ctx.clip()
    switch style {
    case "unroll": unroll(ctx, small: small)
    case "folder": folder(ctx, small: small)
    case "card": card(ctx, small: small)
    case "bold": arrow(ctx, small: small, ink: [color(0x2a3020), color(0x161a12)], shadow: 0.18)
    default: arrow(ctx, small: small, ink: lime, shadow: 0.45)
    }
    ctx.restoreGState()
    tileEdge(ctx)

    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

if out.pathExtension == "png" {
    try! icon(1024).write(to: out)
} else {
    try? FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
    for (name, px) in [("16x16", 16), ("16x16@2x", 32), ("32x32", 32), ("32x32@2x", 64), ("128x128", 128),
                       ("128x128@2x", 256), ("256x256", 256), ("256x256@2x", 512), ("512x512", 512),
                       ("512x512@2x", 1024)] {
        try! icon(px).write(to: out.appendingPathComponent("icon_\(name).png"))
    }
}
