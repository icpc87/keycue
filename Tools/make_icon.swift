// KeyCue のアプリアイコン（1024px PNG）を描く。使い方: swift Tools/make_icon.swift <出力.png>
import AppKit

let size: CGFloat = 1024
let out = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "icon_1024.png"
let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size), pixelsHigh: Int(size), bitsPerSample: 8,
                           samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                           bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
let ctx = NSGraphicsContext.current!.cgContext

// macOS のアイコングリッド：本体は 824px、角丸は約 185px
let body = CGRect(x: 100, y: 100, width: 824, height: 824)
let bodyPath = CGPath(roundedRect: body, cornerWidth: 185, cornerHeight: 185, transform: nil)

// 影
ctx.saveGState()
ctx.setShadow(offset: CGSize(width: 0, height: -14), blur: 30, color: NSColor.black.withAlphaComponent(0.35).cgColor)
ctx.addPath(bodyPath)
ctx.setFillColor(NSColor.black.cgColor)
ctx.fillPath()
ctx.restoreGState()

// 背景：夜のステージのような紺 → 青緑
ctx.saveGState()
ctx.addPath(bodyPath)
ctx.clip()
let bg = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(), colors: [
    NSColor(red: 0.06, green: 0.09, blue: 0.20, alpha: 1).cgColor,
    NSColor(red: 0.05, green: 0.30, blue: 0.40, alpha: 1).cgColor,
] as CFArray, locations: [0, 1])!
ctx.drawLinearGradient(bg, start: CGPoint(x: 0, y: 924), end: CGPoint(x: 1024, y: 100), options: [])
// 上からのスポットライト
let spot = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(), colors: [
    NSColor(white: 1, alpha: 0.18).cgColor, NSColor(white: 1, alpha: 0).cgColor,
] as CFArray, locations: [0, 1])!
ctx.drawRadialGradient(spot, startCenter: CGPoint(x: 512, y: 980), startRadius: 0,
                       endCenter: CGPoint(x: 512, y: 980), endRadius: 620, options: [])
ctx.restoreGState()

// 音の波（キーの右上へ広がる弧）
ctx.saveGState()
ctx.setLineCap(.round)
for (i, r) in [CGFloat(250), 330, 410].enumerated() {
    ctx.setStrokeColor(NSColor(red: 1.0, green: 0.62, blue: 0.25, alpha: 0.85 - CGFloat(i) * 0.22).cgColor)
    ctx.setLineWidth(34 - CGFloat(i) * 6)
    ctx.addArc(center: CGPoint(x: 470, y: 470), radius: r, startAngle: .pi * 0.06, endAngle: .pi * 0.44, clockwise: false)
    ctx.strokePath()
}
ctx.restoreGState()

// キーキャップ（側面 → 上面）
let cap = CGRect(x: 232, y: 232, width: 480, height: 480)
ctx.saveGState()
ctx.setShadow(offset: CGSize(width: 0, height: -22), blur: 34, color: NSColor.black.withAlphaComponent(0.55).cgColor)
let side = CGPath(roundedRect: cap, cornerWidth: 92, cornerHeight: 92, transform: nil)
ctx.addPath(side)
ctx.setFillColor(NSColor(red: 0.72, green: 0.75, blue: 0.80, alpha: 1).cgColor)
ctx.fillPath()
ctx.restoreGState()

let top = cap.insetBy(dx: 38, dy: 38).offsetBy(dx: 0, dy: 26)
let topPath = CGPath(roundedRect: top, cornerWidth: 70, cornerHeight: 70, transform: nil)
ctx.saveGState()
ctx.addPath(topPath)
ctx.clip()
let capGrad = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(), colors: [
    NSColor(white: 0.99, alpha: 1).cgColor, NSColor(red: 0.86, green: 0.88, blue: 0.92, alpha: 1).cgColor,
] as CFArray, locations: [0, 1])!
ctx.drawLinearGradient(capGrad, start: CGPoint(x: top.midX, y: top.maxY), end: CGPoint(x: top.midX, y: top.minY), options: [])
ctx.restoreGState()

// 再生マーク（キュー）
let tri = CGMutablePath()
let cx = top.midX + 14, cy = top.midY
tri.move(to: CGPoint(x: cx - 78, y: cy + 112))
tri.addLine(to: CGPoint(x: cx + 118, y: cy))
tri.addLine(to: CGPoint(x: cx - 78, y: cy - 112))
tri.closeSubpath()
ctx.saveGState()
ctx.addPath(tri)
ctx.setLineJoin(.round)
ctx.setLineWidth(36)
ctx.setStrokeColor(NSColor(red: 1.0, green: 0.45, blue: 0.18, alpha: 1).cgColor)
ctx.setFillColor(NSColor(red: 1.0, green: 0.45, blue: 0.18, alpha: 1).cgColor)
ctx.drawPath(using: .fillStroke)
ctx.restoreGState()

NSGraphicsContext.current = nil
let png = rep.representation(using: .png, properties: [:])!
try! png.write(to: URL(fileURLWithPath: out))
print("wrote \(out)")
