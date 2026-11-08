import AppKit
import QuartzCore

// Several pets at once: when one agent hands work to another (Claude Code runs `codex exec`, Codex runs `claude -p`, …)
// the caller's pet throws a little envelope to the callee's pet, and the answer flies back the same way.

final class Courier {
    private static var flying: [Courier] = []

    private let panel: PetPanel
    private let layer = CALayer()
    private weak var from: Pet?
    private weak var to: Pet?
    private let start = now()
    private let duration: Double
    private var a: CGPoint
    private var b: CGPoint
    private var timer: Timer?
    private let arrive: () -> Void
    private let size: CGSize

    /// Throw an envelope from one pet's hands to another's; `arrive` runs when it lands.
    static func fly(from: Pet, to: Pet, arrive: @escaping () -> Void) {
        let c = Courier(from: from, to: to, arrive: arrive)
        flying.append(c)
        c.launch()
    }

    /// Envelopes in the air (for the `scene` self-check).
    static var inFlight: [(panel: NSWindow, layer: CALayer?)] { flying.map { ($0.panel, $0.panel.contentView?.layer) } }

    private init(from: Pet, to: Pet, arrive: @escaping () -> Void) {
        self.from = from; self.to = to; self.arrive = arrive
        a = from.hands; b = to.hands
        duration = clamp(0.65 + Double(hypot(b.x - a.x, b.y - a.y)) / 1700, 0.65, 1.7)
        let k = max(0.8, from.scale / 0.62)
        size = CGSize(width: 34 * k, height: 25 * k)
        panel = PetPanel(size: CGSize(width: size.width * 1.6, height: size.width * 1.6))
        panel.ignoresMouseEvents = true
        panel.level = .floating
        let v = NSView(frame: NSRect(origin: .zero, size: panel.frame.size))
        v.wantsLayer = true
        v.layer = CALayer()
        layer.bounds = CGRect(origin: .zero, size: size)
        layer.position = CGPoint(x: v.bounds.midX, y: v.bounds.midY)
        layer.contents = Courier.envelope(size)
        layer.contentsGravity = .resizeAspect
        layer.shadowColor = NSColor.black.cgColor
        layer.shadowOpacity = 0.18
        layer.shadowRadius = 2
        layer.shadowOffset = CGSize(width: 0, height: -1)
        v.layer?.addSublayer(layer)
        panel.contentView = v
    }

    private func launch() {
        place(0)
        panel.orderFrontRegardless()
        let t = Timer(timeInterval: 1.0 / 60, repeats: true) { [weak self] _ in self?.step() }
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    private func step() {
        // Follow the pets if they move while the note is in the air.
        if let f = from { a = f.hands }
        if let t = to { b = t.hands }
        let u = (now() - start) / duration
        if u >= 1 {
            timer?.invalidate()
            panel.orderOut(nil)
            Courier.flying.removeAll { $0 === self }
            arrive()
            return
        }
        place(u)
    }

    private func place(_ u: Double) {
        let e = CGFloat(u < 0.5 ? 2 * u * u : 1 - pow(-2 * u + 2, 2) / 2)      // ease in-out
        let lift = max(70, abs(b.x - a.x) * 0.35)
        let c = CGPoint(x: (a.x + b.x) / 2, y: max(a.y, b.y) + lift)
        func q(_ s: CGFloat) -> CGPoint {
            CGPoint(x: (1 - s) * (1 - s) * a.x + 2 * (1 - s) * s * c.x + s * s * b.x,
                    y: (1 - s) * (1 - s) * a.y + 2 * (1 - s) * s * c.y + s * s * b.y)
        }
        let p = q(e), p2 = q(min(1, e + 0.02))
        let tilt = atan2(p2.y - p.y, p2.x - p.x)
        let spin = (b.x >= a.x ? 1 : -1) * CGFloat(sin(u * .pi)) * 0.35
        CATransaction.begin(); CATransaction.setDisableActions(true)
        layer.setAffineTransform(CGAffineTransform(rotationAngle: (abs(tilt) > .pi / 2 ? tilt - .pi : tilt) * 0.5 + spin))
        layer.opacity = Float(min(1, u * 8, (1 - u) * 10))
        CATransaction.commit()
        let f = panel.frame.size
        panel.setFrameOrigin(NSPoint(x: (p.x - f.width / 2).rounded(), y: (p.y - f.height / 2).rounded()))
    }

    /// A small pastel envelope with a heart seal.
    private static func envelope(_ s: CGSize) -> CGImage? {
        let img = NSImage(size: s, flipped: false) { r in
            let body = r.insetBy(dx: 1.5, dy: 1.5)
            let p = NSBezierPath(roundedRect: body, xRadius: 3.5, yRadius: 3.5)
            Palette.blush.setFill(); p.fill()
            Palette.pink.setStroke(); p.lineWidth = 1.6; p.stroke()
            let flap = NSBezierPath()
            flap.move(to: CGPoint(x: body.minX + 1, y: body.maxY - 1))
            flap.line(to: CGPoint(x: body.midX, y: body.midY - body.height * 0.08))
            flap.line(to: CGPoint(x: body.maxX - 1, y: body.maxY - 1))
            flap.lineWidth = 1.4; flap.lineJoinStyle = .round
            Palette.pink.setStroke(); flap.stroke()
            let h = body.height * 0.34
            let c = CGPoint(x: body.midX, y: body.midY - body.height * 0.1)
            let heart = NSBezierPath()
            heart.move(to: CGPoint(x: c.x, y: c.y - h * 0.55))
            heart.curve(to: CGPoint(x: c.x - h * 0.6, y: c.y + h * 0.15), controlPoint1: CGPoint(x: c.x - h * 0.2, y: c.y - h * 0.2), controlPoint2: CGPoint(x: c.x - h * 0.6, y: c.y - h * 0.15))
            heart.curve(to: CGPoint(x: c.x, y: c.y + h * 0.2), controlPoint1: CGPoint(x: c.x - h * 0.6, y: c.y + h * 0.55), controlPoint2: CGPoint(x: c.x - h * 0.1, y: c.y + h * 0.5))
            heart.curve(to: CGPoint(x: c.x + h * 0.6, y: c.y + h * 0.15), controlPoint1: CGPoint(x: c.x + h * 0.1, y: c.y + h * 0.5), controlPoint2: CGPoint(x: c.x + h * 0.6, y: c.y + h * 0.55))
            heart.curve(to: CGPoint(x: c.x, y: c.y - h * 0.55), controlPoint1: CGPoint(x: c.x + h * 0.6, y: c.y - h * 0.15), controlPoint2: CGPoint(x: c.x + h * 0.2, y: c.y - h * 0.2))
            Palette.pink.setFill(); heart.fill()
            return true
        }
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(s.width * 2), pixelsHigh: Int(s.height * 2), bitsPerSample: 8,
                                         samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)
        else { return nil }
        rep.size = s
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        img.draw(in: CGRect(origin: .zero, size: s))
        NSGraphicsContext.restoreGraphicsState()
        return rep.cgImage
    }
}

/// Spotting one agent calling another from its tool calls.
enum AgentCalls {
    private static func re(_ s: String) -> NSRegularExpression { try! NSRegularExpression(pattern: s) }
    // The program in command position (after start, ;, &&, |, (, $( or a backtick, env assignments and wrappers).
    private static let lead = #"(?:^|[;&|(`\n]|\$\()\s*(?:[A-Za-z_][A-Za-z0-9_]*=\S*\s+)*(?:(?:env|nohup|time|command|exec|caffeinate(?:\s+-\w+)*|g?timeout\s+\S+)\s+)*"#
    private static let codex = re(lead + #"(?:\S*/)?(?:codex|\$\{?CODEX\}?)(?=\s|$)(?!\s+(?:--version|-V|--help|-h|help|login|logout|mcp|mcp-server|completion|features|apply|debug)\b)"#)
    private static let claude = re(lead + #"(?:\S*/)?claude(?=\s|$)(?!\s+(?:--version|-v|--help|-h|mcp|config|doctor|update|install|migrate-installer|setup-token|plugin|auth)\b)"#)
    private static let quoted = re(#"["“']([^"”']{4,})["”']"#)

    /// The agent this tool call reaches out to ("codex"/"claude"), if any.
    static func callee(from src: String, tool: String, input: [String: Any]) -> String? {
        let other = src == "codex" ? "claude" : "codex"
        let t = tool.lowercased()
        if t.hasPrefix("mcp__") && t.contains(other) { return other }
        if t == "task" || t == "agent", ((input["subagent_type"] as? String) ?? "").lowercased().contains(other) { return other }
        guard tool == "Bash", let cmd = input["command"] as? String else { return nil }
        let r = other == "codex" ? codex : claude
        return r.firstMatch(in: cmd, range: NSRange(cmd.startIndex..., in: cmd)) != nil ? other : nil
    }

    /// A short line describing the job handed over (the first quoted argument, if there is one).
    static func task(_ input: [String: Any]) -> String {
        let s = (input["command"] as? String) ?? (input["description"] as? String) ?? (input["prompt"] as? String) ?? ""
        if let m = quoted.firstMatch(in: s, range: NSRange(s.startIndex..., in: s)), let r = Range(m.range(at: 1), in: s) {
            return cap(String(s[r]), 22)
        }
        if let d = input["description"] as? String, !d.isEmpty { return cap(d, 22) }
        return ""
    }
}
