// Renders the macOS app icon (Big Sur grid: 824 pt rounded square on a 1024 canvas, drop shadow) from the Flarial mark,
// then the .icns. Usage: swift tools/make-macos-icon.swift assets/flarial-launcher.png assets/macos
import AppKit

let args = CommandLine.arguments
let mark = NSImage(contentsOfFile: args[1])!
let out = URL(fileURLWithPath: args[2])
let size = 1024, inset: CGFloat = 100, side: CGFloat = 824, radius: CGFloat = 185

let ctx = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8, bytesPerRow: 0,
                    space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
let tile = CGRect(x: inset, y: inset + 12, width: side, height: side)
let path = CGPath(roundedRect: tile, cornerWidth: radius, cornerHeight: radius, transform: nil)

ctx.saveGState()
ctx.setShadow(offset: CGSize(width: 0, height: -12), blur: 28, color: CGColor(gray: 0, alpha: 0.35))
ctx.addPath(path); ctx.setFillColor(CGColor(srgbRed: 0.09, green: 0.07, blue: 0.08, alpha: 1)); ctx.fillPath()
ctx.restoreGState()

ctx.saveGState()
ctx.addPath(path); ctx.clip()
let gradient = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB)!, colors: [
    CGColor(srgbRed: 0.20, green: 0.15, blue: 0.17, alpha: 1), CGColor(srgbRed: 0.08, green: 0.06, blue: 0.07, alpha: 1)] as CFArray, locations: [0, 1])!
ctx.drawLinearGradient(gradient, start: CGPoint(x: 0, y: tile.maxY), end: CGPoint(x: 0, y: tile.minY), options: [])
ctx.restoreGState()

let markSide = side * 0.62
var rect = CGRect(x: tile.midX - markSide / 2, y: tile.midY - markSide / 2, width: markSide, height: markSide)
let cg = mark.cgImage(forProposedRect: &rect, context: nil, hints: [.interpolation: NSImageInterpolation.high.rawValue])!
ctx.interpolationQuality = .high
ctx.draw(cg, in: rect)

let png = NSBitmapImageRep(cgImage: ctx.makeImage()!).representation(using: .png, properties: [:])!
try! FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
try! png.write(to: out.appendingPathComponent("AppIcon.png"))
