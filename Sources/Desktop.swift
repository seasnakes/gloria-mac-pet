import AppKit
import CoreGraphics

// Window geometry from the window server. Only bounds and owners are read, never window contents or titles.
// Everything is converted to Cocoa global coordinates (origin at the primary screen's bottom-left, y up).

struct Win: Equatable {
    let id: CGWindowID
    let pid: pid_t
    let owner: String
    let rect: CGRect
}

struct Ledge {
    var win: CGWindowID?          // nil = the floor of a screen (top of the Dock)
    var left: CGFloat
    var right: CGFloat
    var y: CGFloat
    var isFloor: Bool { win == nil }
}

enum Desktop {
    static let skipOwners: Set<String> = [
        "Dock", "Window Server", "Control Center", "Notification Center", "Spotlight", "SystemUIServer",
        "WindowManager", "screencaptureui", "loginwindow", "Wallpaper", "TextInputMenuAgent", "Screenshot",
    ]

    static var primaryHeight: CGFloat { NSScreen.screens.first?.frame.height ?? 900 }

    static func cocoa(_ r: CGRect) -> CGRect {
        CGRect(x: r.minX, y: primaryHeight - r.maxY, width: r.width, height: r.height)
    }

    /// Fake windows for recording videos (ctl "stage"): while set, they stand in for every real window.
    static var staged: [Win]?

    /// Normal windows front to back, plus the id order of every on-screen window (ours included).
    static func scan(excluding pid: pid_t) -> (wins: [Win], order: [CGWindowID]) {
        if let s = staged { return (s, s.map(\.id)) }
        guard let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID)
                as? [[String: Any]] else { return ([], []) }
        var wins: [Win] = []
        var order: [CGWindowID] = []
        for w in list {
            let id = (w[kCGWindowNumber as String] as? NSNumber)?.uint32Value ?? 0
            order.append(id)
            guard (w[kCGWindowLayer as String] as? NSNumber)?.intValue == 0 else { continue }
            let owner = w[kCGWindowOwnerName as String] as? String ?? ""
            let wpid = (w[kCGWindowOwnerPID as String] as? NSNumber)?.int32Value ?? 0
            if wpid == pid || skipOwners.contains(owner) { continue }
            if let a = (w[kCGWindowAlpha as String] as? NSNumber)?.doubleValue, a < 0.05 { continue }
            guard let b = w[kCGWindowBounds as String] as? NSDictionary,
                  let cg = CGRect(dictionaryRepresentation: b as CFDictionary) else { continue }
            if cg.width < 160 || cg.height < 90 { continue }
            wins.append(Win(id: id, pid: wpid, owner: owner, rect: cocoa(cg)))
        }
        return (wins, order)
    }

    static func screen(at p: CGPoint) -> NSScreen? {
        NSScreen.screens.first { $0.frame.contains(p) }
    }

    /// A screen's name for the menu: 内建显示器 for the laptop's own, else what macOS calls it.
    static func name(_ s: NSScreen) -> String {
        if let n = s.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber, CGDisplayIsBuiltin(n.uint32Value) != 0 { return "内建显示器" }
        return s.localizedName
    }

    static func nearestScreen(_ p: CGPoint) -> NSScreen? {
        if let s = screen(at: p) { return s }
        return NSScreen.screens.min { a, b in dist(a.frame, p) < dist(b.frame, p) }
    }

    private static func dist(_ r: CGRect, _ p: CGPoint) -> CGFloat {
        let dx = max(r.minX - p.x, 0, p.x - r.maxX), dy = max(r.minY - p.y, 0, p.y - r.maxY)
        return hypot(dx, dy)
    }

    /// Spans of window tops that nothing in front covers and that leave room above them, plus each screen's floor.
    static func ledges(_ wins: [Win], headroom: CGFloat) -> [Ledge] {
        var out: [Ledge] = []
        var above: [CGRect] = []
        for w in wins {
            let y = w.rect.maxY
            var spans: [(CGFloat, CGFloat)] = [(w.rect.minX, w.rect.maxX)]
            for c in above where y > c.minY && y <= c.maxY + 1 {
                spans = spans.flatMap { s -> [(CGFloat, CGFloat)] in
                    if c.maxX <= s.0 || c.minX >= s.1 { return [s] }
                    var r: [(CGFloat, CGFloat)] = []
                    if c.minX > s.0 { r.append((s.0, c.minX)) }
                    if c.maxX < s.1 { r.append((c.maxX, s.1)) }
                    return r
                }
            }
            if let scr = screen(at: CGPoint(x: clamp(w.rect.midX, w.rect.minX, w.rect.maxX), y: y - 1)) {
                let v = scr.visibleFrame
                if y + headroom <= v.maxY + 1 && y > v.minY + 4 {
                    for s in spans {
                        let l = max(s.0, v.minX), r = min(s.1, v.maxX)
                        if r - l >= 48 { out.append(Ledge(win: w.id, left: l, right: r, y: y)) }
                    }
                }
            }
            above.append(w.rect)
        }
        for s in NSScreen.screens {
            let v = s.visibleFrame
            out.append(Ledge(win: nil, left: v.minX, right: v.maxX, y: v.minY))
        }
        return out
    }
}
