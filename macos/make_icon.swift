// Draws the app icon into an .iconset folder: `swift make_icon.swift out.iconset`, then iconutil.
// On Apple's macOS grid (an 824-pt rounded square on a 1024 canvas): a blue tile with a white page that
// holds a sound wave (the lecture) above lines of notes.

import AppKit

let out = URL(fileURLWithPath: CommandLine.arguments[1])
try? FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)

func color(_ hex: UInt32, _ a: CGFloat = 1) -> CGColor {
    CGColor(red: CGFloat((hex >> 16) & 0xff) / 255, green: CGFloat((hex >> 8) & 0xff) / 255,
            blue: CGFloat(hex & 0xff) / 255, alpha: a)
}

func rounded(_ r: CGRect, _ radius: CGFloat) -> CGPath {
    CGPath(roundedRect: r, cornerWidth: radius, cornerHeight: radius, transform: nil)
}

func icon(_ px: Int) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let ctx = NSGraphicsContext.current!.cgContext
    let s = CGFloat(px) / 1024  // design in 1024 units
    ctx.scaleBy(x: s, y: s)

    let tile = CGRect(x: 100, y: 100, width: 824, height: 824)
    let tilePath = rounded(tile, 185)
    ctx.saveGState()  // the drop shadow macOS icons sit on
    ctx.setShadow(offset: CGSize(width: 0, height: -10), blur: 24, color: color(0x000000, 0.28))
    ctx.addPath(tilePath)
    ctx.setFillColor(color(0x0a84ff))
    ctx.fillPath()
    ctx.restoreGState()

    ctx.saveGState()
    ctx.addPath(tilePath)
    ctx.clip()
    let tileGradient = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(),
                                  colors: [color(0x5ac8fa), color(0x007aff), color(0x0a5ce0)] as CFArray,
                                  locations: [0, 0.62, 1])!
    ctx.drawLinearGradient(tileGradient, start: CGPoint(x: 0, y: tile.maxY), end: CGPoint(x: 0, y: tile.minY), options: [])

    let page = CGRect(x: 302, y: 232, width: 420, height: 540)
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -14), blur: 34, color: color(0x001a4d, 0.35))
    ctx.addPath(rounded(page, 44))
    ctx.setFillColor(color(0xffffff))
    ctx.fillPath()
    ctx.restoreGState()

    // the lecture: a sound wave
    let bars: [CGFloat] = [0.34, 0.62, 1.0, 0.74, 0.46, 0.82, 0.52, 0.3]
    let barW: CGFloat = 24, gap: CGFloat = 17, maxH: CGFloat = 150
    let waveW = CGFloat(bars.count) * barW + CGFloat(bars.count - 1) * gap
    let midY = page.maxY - 150
    for (i, h) in bars.enumerated() {
        let x = page.midX - waveW / 2 + CGFloat(i) * (barW + gap)
        let hh = maxH * h
        ctx.addPath(rounded(CGRect(x: x, y: midY - hh / 2, width: barW, height: hh), barW / 2))
    }
    ctx.setFillColor(color(0x007aff))
    ctx.fillPath()

    // the notes: lines of text
    let lines: [(CGFloat, UInt32)] = [(0.74, 0x1d1d1f), (0.64, 0xc7cbd6), (0.7, 0xc7cbd6), (0.46, 0xc7cbd6)]
    for (i, (w, c)) in lines.enumerated() {
        let y = page.minY + 222 - CGFloat(i) * 52
        ctx.addPath(rounded(CGRect(x: page.minX + 62, y: y, width: (page.width - 124) * w, height: 26), 13))
        ctx.setFillColor(color(c, i == 0 ? 0.85 : 1))
        ctx.fillPath()
    }
    ctx.restoreGState()

    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

for (name, px) in [("16x16", 16), ("16x16@2x", 32), ("32x32", 32), ("32x32@2x", 64), ("128x128", 128),
                   ("128x128@2x", 256), ("256x256", 256), ("256x256@2x", 512), ("512x512", 512), ("512x512@2x", 1024)] {
    try! icon(px).write(to: out.appendingPathComponent("icon_\(name).png"))
}
