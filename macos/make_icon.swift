// Draws the app icon: `swift make_icon.swift out.iconset`, then iconutil. With an output ending
// in .png it writes one 1024-px picture instead. With an output ending in .tiles it writes
// icon-<px>.png at the sizes Windows and web pages use, with the tile filling the picture (no
// macOS margin or shadow). The icons in assets/ (study-stash.ico, icon.png and
// apple-touch-icon.png) were made from those tiles.
// A lime folder on a dark tile with lines of notes on it: your stash of lecture notes. On Apple's
// macOS grid: an 824-pt rounded square on a 1024 canvas.

import AppKit

let out = URL(fileURLWithPath: CommandLine.arguments[1])

func color(_ hex: UInt32, _ a: CGFloat = 1) -> CGColor {
    CGColor(red: CGFloat((hex >> 16) & 0xff) / 255, green: CGFloat((hex >> 8) & 0xff) / 255,
            blue: CGFloat(hex & 0xff) / 255, alpha: a)
}

func gradient(_ colors: [CGColor]) -> CGGradient {
    let stops: [CGFloat] = colors.count == 3 ? [0, 0.55, 1] : [0, 1]
    return CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(), colors: colors as CFArray, locations: stops)!
}

func rounded(_ r: CGRect, _ radius: CGFloat) -> CGPath {
    CGPath(roundedRect: r, cornerWidth: radius, cornerHeight: radius, transform: nil)
}

/// Fills `path` with `colors` from `top` to `bottom`, over an optional soft shadow.
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
    ctx.drawLinearGradient(gradient(colors), start: CGPoint(x: 0, y: top), end: CGPoint(x: 0, y: bottom),
                           options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
    ctx.restoreGState()
}

func icon(_ px: Int, bleed: Bool = false) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let ctx = NSGraphicsContext.current!.cgContext
    if bleed {  // the 824-unit tile fills 96% of the picture
        let scale = 0.96 * CGFloat(px) / 824
        ctx.translateBy(x: 0.02 * CGFloat(px) - 100 * scale, y: 0.02 * CGFloat(px) - 100 * scale)
        ctx.scaleBy(x: scale, y: scale)
    } else {
        ctx.scaleBy(x: CGFloat(px) / 1024, y: CGFloat(px) / 1024)  // design in 1024 units
    }
    let small = px <= 64  // at Finder-list sizes: fewer, thicker lines, so it still reads

    // the tile, on the shadow macOS icons sit on
    let tile = CGRect(x: 100, y: 100, width: 824, height: 824)
    fill(ctx, rounded(tile, 185), [color(0x353b34), color(0x1d211d), color(0x131613)], top: tile.maxY,
         bottom: tile.minY, shadow: bleed ? 0 : 0.35)

    // the folder: its back with the tab, then the front, the way Finder draws folders
    let bx: CGFloat = 212, by: CGFloat = 268, bw: CGFloat = 600, bh: CGFloat = 470
    let back = CGMutablePath()
    back.addPath(rounded(CGRect(x: bx, y: by, width: bw, height: bh - 40), 46))
    back.addPath(rounded(CGRect(x: bx, y: by + bh - 110, width: 250, height: 110), 40))
    fill(ctx, back, [color(0x9fc93e), color(0x6f9a22)], top: by + bh, bottom: by, shadow: 0.4)
    let frontTop = by + bh - 108
    fill(ctx, rounded(CGRect(x: bx, y: by, width: bw, height: bh - 108), 46),
         [color(0xe4f07a), color(0xc2de52), color(0x8fbf36)], top: frontTop, bottom: by, shadow: 0.25)

    // lines of notes on the front, the first one a heading
    let lines: [CGFloat] = small ? [0.62, 0.8] : [0.46, 0.72, 0.64, 0.7]
    let height: CGFloat = small ? 44 : 30, gap: CGFloat = small ? 92 : 64
    let firstY = frontTop - (small ? 118 : 100)
    for (i, len) in lines.enumerated() {
        let h = i == 0 ? height * 1.25 : height
        let r = CGRect(x: bx + 78, y: firstY - CGFloat(i) * gap, width: (bw - 156) * len, height: h)
        ctx.addPath(rounded(r, h / 2))
        ctx.setFillColor(color(0x2b3a12, i == 0 ? 0.8 : 0.5))
        ctx.fillPath()
    }

    // the fine light edge along the tile, as on Apple's icons
    ctx.addPath(rounded(tile.insetBy(dx: 1.5, dy: 1.5), 184))
    ctx.setLineWidth(3)
    ctx.setStrokeColor(color(0xffffff, 0.08))
    ctx.strokePath()

    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

if out.pathExtension == "png" {
    try! icon(1024).write(to: out)
} else if out.pathExtension == "tiles" {
    try? FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
    for px in [16, 24, 32, 48, 64, 128, 180, 256] {
        try! icon(px, bleed: true).write(to: out.appendingPathComponent("icon-\(px).png"))
    }
} else {
    try? FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
    for (name, px) in [("16x16", 16), ("16x16@2x", 32), ("32x32", 32), ("32x32@2x", 64), ("128x128", 128),
                       ("128x128@2x", 256), ("256x256", 256), ("256x256@2x", 512), ("512x512", 512),
                       ("512x512@2x", 1024)] {
        try! icon(px).write(to: out.appendingPathComponent("icon_\(name).png"))
    }
}
