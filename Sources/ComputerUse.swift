import AppKit
import QuartzCore

// Computer use: when an agent clicks, types, scrolls, drags or takes screenshots on your Mac, its pet acts it out —
// hops onto the window being worked on, points and taps, types on a little laptop, snaps a photo — and the spot it
// clicked ripples. Codex drives apps through its `cua_repl` MCP tool (JavaScript like `app.click([x, y])`);
// Claude Code through computer-use tools (`left_click`, `type`, `screenshot`, …).

struct CUAction {
    enum Kind: String { case tap, double, right, type, key, scroll, drag, snap, look, open }
    let kind: Kind
    /// Point in the target window's own coordinates (top-left origin), when the call names one.
    var point: CGPoint?
    var point2: CGPoint?
    /// Key combination for `key` ("⌘Q"), characters typed for `type` (only the count is kept).
    var keys: String?
    var count: Int?

    var anim: Anim {
        switch kind {
        case .tap, .double, .right, .key: return .tap
        case .type: return .type
        case .scroll: return .scroll
        case .drag: return .drag
        case .snap, .look, .open: return .snap
        }
    }

    var label: String {
        switch kind {
        case .tap: return "点一下"
        case .double: return "双击"
        case .right: return "右键"
        case .type: return count.map { "打字（\($0) 个字）" } ?? "打字"
        case .key: return "按 \(keys ?? "按键")"
        case .scroll: return "滚动页面"
        case .drag: return "拖过去"
        case .snap: return "看一眼屏幕"
        case .look: return "找找窗口"
        case .open: return "打开 App"
        }
    }
}

enum ComputerUse {
    private static func re(_ s: String) -> NSRegularExpression { try! NSRegularExpression(pattern: s) }
    private static let call = re(#"\.(click|doubleClick|rightClick|tripleClick|typeText|type|fill|setValue|pressKey|press|keyPress|scroll|scrollBy|drag|dragTo|getScreenshot|screenshot|getAXStateAndScreenshot|getAXState|domSnapshot|getState|listApps|getApp|getTab|createBrowserTab|goto|navigate|hover|moveMouse)\s*\("#)
    private static let pair = re(#"\[\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\]"#)
    private static let str = re(#"^\s*(['"`])((?:\\.|(?!\1).)*)\1"#)
    private static let appArg = re(#"getApp\s*\(\s*(['"`])((?:\\.|(?!\1).)*)\1"#)

    /// Whether a tool call is computer use, and what it does, in order.
    static func parse(source: String, tool: String, input: [String: Any]) -> (app: String?, acts: [CUAction])? {
        let t = tool.lowercased()
        // Codex: the cua_repl MCP server's `js` tool, whatever the hook calls it.
        if let code = input["code"] as? String, t.contains("cua") || code.contains("cua.") || t == "js" {
            let acts = codex(code)
            return acts.isEmpty ? nil : (app(code), acts)
        }
        // Claude Code: desktop computer use (mcp__computer-use__left_click …), and the browser `computer` tools.
        if t.hasPrefix("mcp__computer-use__") {
            return (nil, claude(String(t.dropFirst("mcp__computer-use__".count)), input).map { [$0] } ?? [])
        }
        if t == "computer" || t.hasSuffix("__computer"), let a = input["action"] as? String {
            return claude(a, input).map { (nil, [$0]) }
        }
        if t.hasSuffix("__browser_batch"), let list = input["actions"] as? [[String: Any]] {
            let acts = list.compactMap { a -> CUAction? in
                guard let n = a["name"] as? String else { return nil }
                if n.hasSuffix("computer"), let act = a["action"] as? String { return claude(act, [:]) }
                return n.hasSuffix("navigate") ? CUAction(kind: .open) : nil
            }
            return acts.isEmpty ? nil : (nil, acts)
        }
        return nil
    }

    /// Clicks that move the real pointer (desktop computer use): the ripple can go where the pointer ends up.
    static func movesPointer(_ tool: String) -> Bool {
        let t = tool.lowercased()
        return t.hasPrefix("mcp__computer-use__") && (t.hasSuffix("click") || t.hasSuffix("drag"))
    }

    private static func claude(_ action: String, _ input: [String: Any]) -> CUAction? {
        switch action {
        case "left_click", "middle_click", "left_mouse_down", "left_mouse_up": return CUAction(kind: .tap)
        case "double_click", "triple_click": return CUAction(kind: .double)
        case "right_click": return CUAction(kind: .right)
        case "type": return CUAction(kind: .type, count: input["text_len"] as? Int)
        case "key", "hold_key": return CUAction(kind: .key, keys: pretty(input["text"] as? String ?? (input["key"] as? String ?? "")))
        case "scroll", "scroll_to": return CUAction(kind: .scroll)
        case "left_click_drag": return CUAction(kind: .drag)
        case "screenshot", "zoom", "read_page", "get_page_text": return CUAction(kind: .snap)
        case "open_application", "switch_display": return CUAction(kind: .open)
        case "mouse_move", "hover", "cursor_position", "wait", "request_access", "list_granted_applications": return nil
        default: return nil
        }
    }

    private static func codex(_ code: String) -> [CUAction] {
        let ns = code as NSString
        var out: [CUAction] = []
        for m in call.matches(in: code, range: NSRange(location: 0, length: ns.length)) {
            let name = ns.substring(with: m.range(at: 1))
            // The argument text up to the closing parenthesis (good enough for literal arguments).
            let rest = ns.substring(from: m.range.location + m.range.length)
            let args = String(rest.prefix(240))
            let pts = pair.matches(in: args, range: NSRange(location: 0, length: (args as NSString).length)).map { p -> CGPoint in
                CGPoint(x: Double((args as NSString).substring(with: p.range(at: 1))) ?? 0, y: Double((args as NSString).substring(with: p.range(at: 2))) ?? 0)
            }
            let s1 = str.firstMatch(in: args, range: NSRange(location: 0, length: (args as NSString).length))
                .map { (args as NSString).substring(with: $0.range(at: 2)) }
            switch name {
            case "click":
                let right = args.prefix(120).contains("right")
                out.append(CUAction(kind: right ? .right : .tap, point: pts.first))
            case "doubleClick", "tripleClick": out.append(CUAction(kind: .double, point: pts.first))
            case "rightClick": out.append(CUAction(kind: .right, point: pts.first))
            case "typeText", "type", "fill", "setValue": out.append(CUAction(kind: .type, point: pts.first, count: s1?.count))
            case "pressKey", "press", "keyPress": out.append(CUAction(kind: .key, keys: pretty(s1 ?? "")))
            case "scroll", "scrollBy": out.append(CUAction(kind: .scroll, point: pts.first))
            case "drag", "dragTo": out.append(CUAction(kind: .drag, point: pts.first, point2: pts.dropFirst().first))
            case "getScreenshot", "screenshot", "getAXStateAndScreenshot", "getAXState", "domSnapshot":
                if out.last?.kind != .snap { out.append(CUAction(kind: .snap)) }
            case "getState", "listApps": if out.isEmpty { out.append(CUAction(kind: .look)) }
            case "goto", "navigate", "createBrowserTab", "getTab": out.append(CUAction(kind: .open))
            default: break
            }
        }
        return Array(out.prefix(8))
    }

    private static func app(_ code: String) -> String? {
        let ns = code as NSString
        return appArg.firstMatch(in: code, range: NSRange(location: 0, length: ns.length)).map { ns.substring(with: $0.range(at: 2)) }
    }

    /// "super+shift+q" → "⌘⇧Q".
    static func pretty(_ k: String) -> String {
        let map = ["super": "⌘", "cmd": "⌘", "command": "⌘", "meta": "⌘", "ctrl": "⌃", "control": "⌃", "alt": "⌥", "option": "⌥",
                   "shift": "⇧", "return": "↩", "enter": "↩", "tab": "⇥", "escape": "⎋", "esc": "⎋", "backspace": "⌫", "delete": "⌫",
                   "space": "␣", "up": "↑", "down": "↓", "left": "←", "right": "→"]
        let parts = k.split(whereSeparator: { $0 == "+" || $0 == " " }).map { p -> String in
            map[p.lowercased()] ?? (p.count == 1 ? p.uppercased() : String(p))
        }
        return cap(parts.joined(), 12)
    }

    /// The window an app name / bundle id / path points at: that app's frontmost window on screen.
    static func window(for app: String?) -> Win? {
        let wins = Desktop.scan(excluding: getpid()).wins
        guard let app, !app.isEmpty else {
            // No app named: the frontmost app's front window.
            let pid = NSWorkspace.shared.frontmostApplication?.processIdentifier
            return wins.first { $0.pid == pid } ?? wins.first
        }
        let running = NSWorkspace.shared.runningApplications
        let match = running.first { r in
            r.bundleIdentifier == app || r.bundleURL?.path == app || r.localizedName == app
                || (app.hasSuffix(".app") && r.bundleURL?.lastPathComponent == (app as NSString).lastPathComponent)
        }
        if let pid = match?.processIdentifier, let w = wins.first(where: { $0.pid == pid }) { return w }
        // Windows-on-Mac and other wrappers: fall back to a window whose owner name appears in the id.
        return wins.first { app.localizedCaseInsensitiveContains($0.owner) && $0.owner.count > 2 }
    }

    /// A point given in a window's own coordinates (top-left origin, points or 2× pixels) → screen (Cocoa) point.
    static func screenPoint(_ p: CGPoint, in w: Win) -> CGPoint {
        var q = p
        if q.x > w.rect.width + 4 || q.y > w.rect.height + 4 { q = CGPoint(x: q.x / 2, y: q.y / 2) }
        q.x = clamp(q.x, 0, w.rect.width); q.y = clamp(q.y, 0, w.rect.height)
        return CGPoint(x: w.rect.minX + q.x, y: w.rect.maxY - q.y)
    }
}

/// Little on-screen effects for computer use: a click ripple, a screenshot flash, a drag trail. They never take clicks.
enum Effects {
    private static var live: [PetPanel] = []
    private static var lastFlash: Double = 0

    private static func panel(_ frame: CGRect) -> (PetPanel, CALayer) {
        let p = PetPanel(size: frame.size)
        p.ignoresMouseEvents = true
        p.level = .floating
        p.setFrameOrigin(frame.origin)
        let v = NSView(frame: NSRect(origin: .zero, size: frame.size))
        v.wantsLayer = true
        v.layer = CALayer()
        p.contentView = v
        p.orderFrontRegardless()
        live.append(p)
        return (p, v.layer!)
    }

    private static func retire(_ p: PetPanel, after s: Double) {
        DispatchQueue.main.asyncAfter(deadline: .now() + s) {
            p.orderOut(nil)
            live.removeAll { $0 === p }
        }
    }

    /// Panels on screen right now (for the `scene` self-check).
    static var panels: [(panel: NSWindow, layer: CALayer?)] { live.map { ($0, $0.contentView?.layer) } }

    /// Two soft rings spreading out from a click.
    static func ripple(at p: CGPoint, color: NSColor = Palette.pink, double: Bool = false) {
        let size: CGFloat = 96
        let (win, root) = panel(CGRect(x: p.x - size / 2, y: p.y - size / 2, width: size, height: size))
        let delays: [Double] = double ? [0, 0.16] : [0, 0.1]
        for k in 0..<2 {
            let delay = delays[k]
            let ring = CAShapeLayer()
            ring.path = CGPath(ellipseIn: CGRect(x: -10, y: -10, width: 20, height: 20), transform: nil)
            ring.position = CGPoint(x: size / 2, y: size / 2)
            ring.fillColor = k == 0 ? color.withAlphaComponent(0.25).cgColor : NSColor.clear.cgColor
            ring.strokeColor = (k == 0 ? color : Palette.periwinkle).cgColor
            ring.lineWidth = k == 0 ? 3 : 2
            ring.opacity = 0
            root.addSublayer(ring)
            let grow = CABasicAnimation(keyPath: "transform.scale")
            grow.fromValue = 0.4; grow.toValue = 3.6
            let fade = CAKeyframeAnimation(keyPath: "opacity")
            fade.values = [0, 1, 0]; fade.keyTimes = [0, 0.15, 1]
            let g = CAAnimationGroup()
            g.animations = [grow, fade]
            g.duration = 0.6
            g.beginTime = CACurrentMediaTime() + delay
            g.timingFunction = CAMediaTimingFunction(name: .easeOut)
            g.fillMode = .both
            ring.add(g, forKey: "ripple")
        }
        retire(win, after: 0.9)
    }

    /// A soft white flash over the window that was just photographed.
    static func flash(_ r: CGRect) {
        // Agents take a screenshot nearly every step; flash at most every couple of seconds.
        guard r.width > 20, r.height > 20, now() - lastFlash > 2 else { return }
        lastFlash = now()
        let (win, root) = panel(r)
        let l = CALayer()
        l.frame = CGRect(origin: .zero, size: r.size)
        l.backgroundColor = NSColor.white.cgColor
        l.cornerRadius = 10
        l.opacity = 0
        root.addSublayer(l)
        let a = CAKeyframeAnimation(keyPath: "opacity")
        a.values = [0, 0.32, 0]; a.keyTimes = [0, 0.18, 1]; a.duration = 0.42
        l.add(a, forKey: "flash")
        // Corner brackets, like a camera's frame.
        let br = CAShapeLayer()
        let path = CGMutablePath(), m: CGFloat = 10, k: CGFloat = min(36, r.width / 6)
        for (x, y, sx, sy) in [(m, m, 1.0, 1.0), (r.width - m, m, -1.0, 1.0), (m, r.height - m, 1.0, -1.0), (r.width - m, r.height - m, -1.0, -1.0)] {
            path.move(to: CGPoint(x: x + k * sx, y: y)); path.addLine(to: CGPoint(x: x, y: y)); path.addLine(to: CGPoint(x: x, y: y + k * sy))
        }
        br.path = path
        br.strokeColor = Palette.pink.cgColor
        br.fillColor = NSColor.clear.cgColor
        br.lineWidth = 4
        br.lineCap = .round
        br.opacity = 0
        root.addSublayer(br)
        let b = CAKeyframeAnimation(keyPath: "opacity")
        b.values = [0, 1, 1, 0]; b.keyTimes = [0, 0.1, 0.7, 1]; b.duration = 0.7
        br.add(b, forKey: "frame")
        retire(win, after: 0.8)
    }

    /// A dotted trail from where a drag starts to where it ends.
    static func trail(from a: CGPoint, to b: CGPoint) {
        let r = CGRect(x: min(a.x, b.x) - 20, y: min(a.y, b.y) - 20, width: abs(b.x - a.x) + 40, height: abs(b.y - a.y) + 40)
        let (win, root) = panel(r)
        let line = CAShapeLayer()
        let p = CGMutablePath()
        p.move(to: CGPoint(x: a.x - r.minX, y: a.y - r.minY)); p.addLine(to: CGPoint(x: b.x - r.minX, y: b.y - r.minY))
        line.path = p
        line.strokeColor = Palette.periwinkle.cgColor
        line.lineWidth = 3
        line.lineDashPattern = [2, 7]
        line.lineCap = .round
        line.strokeEnd = 0
        root.addSublayer(line)
        let draw = CABasicAnimation(keyPath: "strokeEnd")
        draw.fromValue = 0; draw.toValue = 1; draw.duration = 0.55
        draw.fillMode = .forwards; draw.isRemovedOnCompletion = false
        line.add(draw, forKey: "draw")
        let fade = CABasicAnimation(keyPath: "opacity")
        fade.fromValue = 1; fade.toValue = 0; fade.beginTime = CACurrentMediaTime() + 0.8; fade.duration = 0.4
        fade.fillMode = .forwards; fade.isRemovedOnCompletion = false
        line.add(fade, forKey: "fade")
        retire(win, after: 1.3)
        ripple(at: a, color: Palette.periwinkle)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.55) { ripple(at: b) }
    }
}
