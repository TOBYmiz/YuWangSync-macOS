import AppKit
import CoreGraphics

// Finder has one icon size per folder. Give the instructions a smaller visible
// document inside the same canvas, aligned near its label below the arrow.
func bitmap(size: NSSize, scale: Int, draw: () -> Void) -> NSBitmapImageRep {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size.width) * scale,
                              pixelsHigh: Int(size.height) * scale, bitsPerSample: 8,
                              samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                              colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    NSGraphicsContext.current!.cgContext.clear(CGRect(x: 0, y: 0, width: rep.pixelsWide, height: rep.pixelsHigh))
    NSGraphicsContext.current!.cgContext.scaleBy(x: CGFloat(scale), y: CGFloat(scale))
    draw()
    NSGraphicsContext.restoreGraphicsState()
    rep.size = size
    return rep
}

func artwork(mount: String) throws {
    let size = NSSize(width: 680, height: 450)
    let image = NSImage(size: size)
    for scale in [1, 2] {
        image.addRepresentation(bitmap(size: size, scale: scale) {
            NSColor.white.setFill()
            NSBezierPath(rect: NSRect(origin: .zero, size: size)).fill()
            NSColor(calibratedWhite: 0.36, alpha: 1).setStroke()
            let arrow = NSBezierPath()
            arrow.lineWidth = 2.4
            arrow.lineCapStyle = .round
            arrow.lineJoinStyle = .round
            arrow.move(to: NSPoint(x: 326, y: 270))
            arrow.line(to: NSPoint(x: 354, y: 270))
            arrow.move(to: NSPoint(x: 344, y: 280))
            arrow.line(to: NSPoint(x: 354, y: 270))
            arrow.line(to: NSPoint(x: 344, y: 260))
            arrow.stroke()
            let paragraph = NSMutableParagraphStyle()
            paragraph.alignment = .center
            for (text, top, fontSize, gray) in [
                ("拖动育网云到 Applications，即可安装", 395.0, 16.0, 0.48),
                ("适用于 macOS 14 及以上版本 · Apple Silicon", 422.0, 13.0, 0.56)
            ] {
                (text as NSString).draw(in: NSRect(x: 20, y: size.height - top - 23, width: 640, height: 23),
                    withAttributes: [.font: NSFont.systemFont(ofSize: fontSize),
                                     .foregroundColor: NSColor(calibratedWhite: gray, alpha: 1),
                                     .paragraphStyle: paragraph])
            }
        })
    }
    try image.tiffRepresentation!.write(to: URL(fileURLWithPath: mount).appendingPathComponent(".background.tiff"))

    let instructions = URL(fileURLWithPath: mount).appendingPathComponent("安装说明.txt")
    let document = NSWorkspace.shared.icon(forFile: instructions.path)
    let iconSize = NSSize(width: 96, height: 96)
    let icon = NSImage(size: iconSize)
    for scale in [1, 2, 4, 8] {
        icon.addRepresentation(bitmap(size: iconSize, scale: scale) {
            document.draw(in: NSRect(x: 28, y: 4, width: 40, height: 40),
                          from: .zero, operation: .sourceOver, fraction: 1)
        })
    }
    guard NSWorkspace.shared.setIcon(icon, forFile: instructions.path, options: []) else {
        throw NSError(domain: "DMGArtwork", code: 1, userInfo: [NSLocalizedDescriptionKey: "Could not set instruction icon"])
    }
}

func capture(mount: String, output: String) throws {
    // Capture the mounted final image's Finder window, rather than a mockup.
    let open = Process()
    open.executableURL = URL(fileURLWithPath: "/usr/bin/open")
    open.arguments = ["-a", "Finder", mount]
    try open.run()
    open.waitUntilExit()
    guard open.terminationStatus == 0 else { throw NSError(domain: "DMGPreview", code: 1) }
    for _ in 0..<30 {
        let windows = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] ?? []
        if let window = windows.first(where: {
            ($0[kCGWindowOwnerName as String] as? String) == "Finder" &&
            ["育网云", URL(fileURLWithPath: mount).lastPathComponent].contains($0[kCGWindowName as String] as? String ?? "")
        }), let number = window[kCGWindowNumber as String] as? Int {
            Thread.sleep(forTimeInterval: 1)
            let inspect = Process()
            inspect.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
            inspect.arguments = ["-e", "tell application \"Finder\" to get properties of icon view options of front Finder window"]
            try inspect.run()
            inspect.waitUntilExit()
            let screenshot = Process()
            screenshot.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
            screenshot.arguments = ["-x", "-l", String(number), output]
            try screenshot.run()
            screenshot.waitUntilExit()
            guard screenshot.terminationStatus == 0 else { throw NSError(domain: "DMGPreview", code: 2) }
            print("Captured mounted DMG Finder window: \(output)")
            return
        }
        Thread.sleep(forTimeInterval: 0.5)
    }
    throw NSError(domain: "DMGPreview", code: 3, userInfo: [NSLocalizedDescriptionKey: "DMG Finder window was not found"])
}

guard CommandLine.arguments.count >= 3 else { fatalError("Usage: dmg-artwork render|capture MOUNT [OUTPUT]") }
switch CommandLine.arguments[1] {
case "render": try artwork(mount: CommandLine.arguments[2])
case "capture":
    guard CommandLine.arguments.count == 4 else { fatalError("Missing screenshot output path") }
    try capture(mount: CommandLine.arguments[2], output: CommandLine.arguments[3])
default: fatalError("Unknown DMG artwork operation")
}
