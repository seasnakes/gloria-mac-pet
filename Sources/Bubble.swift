import AppKit

// Speech bubble above her head. Three looks: speech, a quiet caption, and an alert that stays until answered.

final class BubbleView: NSView {
    enum Kind { case speech, caption, alert }
    var kind: Kind = .speech
    var label: String?
    var text = ""
    var tailX: CGFloat = 0
    static let tail: CGFloat = 9
    static let maxText: CGFloat = 250

    override var isFlipped: Bool { false }

    var textFont: NSFont { kind == .caption ? .systemFont(ofSize: 11.5, weight: .medium) : .systemFont(ofSize: 13.5, weight: .semibold) }
    var tagFont: NSFont { .systemFont(ofSize: 9.5, weight: .heavy) }
    var pad: NSSize { kind == .caption ? NSSize(width: 10, height: 6) : NSSize(width: 13, height: 9) }

    private var textAttrs: [NSAttributedString.Key: Any] {
        let p = NSMutableParagraphStyle()
        p.lineSpacing = 2
        p.lineBreakMode = .byWordWrapping
        return [.font: textFont, .foregroundColor: Palette.ink, .paragraphStyle: p]
    }
    private var tagAttrs: [NSAttributedString.Key: Any] {
        [.font: tagFont, .foregroundColor: NSColor.white, .kern: 0.8]
    }

    func measure() -> NSSize {
        let t = NSAttributedString(string: text, attributes: textAttrs)
        let r = t.boundingRect(with: NSSize(width: BubbleView.maxText, height: 400), options: [.usesLineFragmentOrigin, .usesFontLeading])
        var w = ceil(r.width), h = ceil(r.height) + 3
        if let tag = label {
            let tw = ceil(NSAttributedString(string: tag, attributes: tagAttrs).size().width) + 14
            w = max(w, tw)
            h += 19
        }
        return NSSize(width: w + pad.width * 2, height: h + pad.height * 2 + BubbleView.tail)
    }

    override func draw(_ dirty: NSRect) {
        let body = NSRect(x: 1.5, y: BubbleView.tail + 1, width: bounds.width - 3, height: bounds.height - BubbleView.tail - 2.5)
        let radius: CGFloat = kind == .caption ? 9 : 13
        let path = NSBezierPath(roundedRect: body, xRadius: radius, yRadius: radius)
        let tx = clamp(tailX, body.minX + radius + 6, body.maxX - radius - 6)
        let tail = NSBezierPath()
        tail.move(to: NSPoint(x: tx - 7, y: body.minY + 0.5))
        tail.line(to: NSPoint(x: tx, y: 1.5))
        tail.line(to: NSPoint(x: tx + 7, y: body.minY + 0.5))
        tail.close()
        path.append(tail)

        let fill: NSColor, stroke: NSColor
        switch kind {
        case .speech: fill = NSColor.white.withAlphaComponent(0.97); stroke = Palette.pink
        case .caption: fill = Palette.blush.withAlphaComponent(0.93); stroke = Palette.lavender
        case .alert: fill = NSColor.white.withAlphaComponent(0.98); stroke = Palette.periwinkle
        }
        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = Palette.ink.withAlphaComponent(0.18)
        shadow.shadowBlurRadius = 5
        shadow.shadowOffset = NSSize(width: 0, height: -1.5)
        shadow.set()
        fill.setFill()
        path.fill()
        NSGraphicsContext.restoreGraphicsState()
        stroke.setStroke()
        path.lineWidth = kind == .alert ? 2.4 : 1.6
        path.stroke()

        var top = body.maxY - pad.height
        if let tag = label {
            let ts = NSAttributedString(string: tag, attributes: tagAttrs)
            let size = ts.size()
            let chip = NSRect(x: body.minX + pad.width - 2, y: top - 15, width: size.width + 12, height: 15)
            (kind == .alert ? Palette.periwinkle : Palette.pink).setFill()
            NSBezierPath(roundedRect: chip, xRadius: 7.5, yRadius: 7.5).fill()
            ts.draw(at: NSPoint(x: chip.minX + 6, y: chip.minY + (15 - size.height) / 2))
            top -= 19
        }
        let t = NSAttributedString(string: text, attributes: textAttrs)
        let r = NSRect(x: body.minX + pad.width, y: body.minY + pad.height, width: BubbleView.maxText, height: top - body.minY - pad.height)
        t.draw(with: r, options: [.usesLineFragmentOrigin, .usesFontLeading])
    }
}

final class Bubble {
    let panel: PetPanel
    let view = BubbleView()
    private(set) var visible = false
    private(set) var sticky = false
    private var hideAt: Double = 0
    private var shownAt: Double = 0
    /// The latest caption that arrived while a speech bubble was still being read; shown once it has had its moment.
    private var pending: (text: String, tag: String?, duration: Double?)?
    var kind: BubbleView.Kind { view.kind }
    var text: String { view.text }

    init() {
        panel = PetPanel(size: NSSize(width: 100, height: 40))
        panel.ignoresMouseEvents = true
        panel.level = NSWindow.Level(rawValue: NSWindow.Level.floating.rawValue + 1)
        panel.contentView = view
        panel.alphaValue = 0
    }

    func show(_ text: String, tag: String? = nil, kind: BubbleView.Kind = .speech, duration: Double? = nil, sticky: Bool = false) {
        // A quiet caption never replaces an alert, and lets a speech bubble stay 1.4 s before taking over; a caption
        // that comes sooner waits for that (only the newest one), unless something louder arrives first.
        if kind == .caption && visible && view.kind != .caption && (self.sticky || now() < hideAt - 0.3) {
            guard !self.sticky && view.kind == .speech else { return }
            let ready = shownAt + 1.4
            if now() < ready {
                pending = (text, tag, duration)
                DispatchQueue.main.asyncAfter(deadline: .now() + (ready - now()) + 0.02) { [weak self] in
                    guard let self, let p = self.pending, p.text == text else { return }
                    self.pending = nil
                    self.show(p.text, tag: p.tag, kind: .caption, duration: p.duration)
                }
                return
            }
        }
        if kind != .caption { pending = nil }
        shownAt = now()
        view.kind = kind
        view.label = tag
        view.text = text
        self.sticky = sticky
        let d = duration ?? clamp(2.4 + Double(text.count) * 0.09, 2.6, 9)
        hideAt = now() + d
        let size = view.measure()
        panel.setContentSize(size)
        view.frame = NSRect(origin: .zero, size: size)
        view.needsDisplay = true
        if !visible {
            visible = true
            panel.orderFrontRegardless()
            NSAnimationContext.runAnimationGroup { ctx in
                ctx.duration = 0.16
                panel.animator().alphaValue = 1
            }
        }
    }

    func hide() {
        guard visible else { return }
        visible = false
        sticky = false
        NSAnimationContext.runAnimationGroup({ ctx in
            ctx.duration = 0.22
            panel.animator().alphaValue = 0
        }, completionHandler: { [weak self] in
            if self?.visible == false { self?.panel.orderOut(nil) }
        })
    }

    func unstick() { if sticky { sticky = false; hideAt = now() + 0.8 } }

    /// Keep the tail pointing at `anchor` (the top of her head).
    func follow(_ anchor: CGPoint, tick t: Double) {
        guard visible else { return }
        if !sticky && t > hideAt { hide(); return }
        let s = panel.frame.size
        var x = anchor.x - s.width / 2
        var y = anchor.y + 2
        if let v = Desktop.nearestScreen(anchor)?.visibleFrame {
            x = clamp(x, v.minX + 4, v.maxX - s.width - 4)
            y = min(y, v.maxY - s.height - 2)
        }
        let tail = anchor.x - x
        if abs(view.tailX - tail) > 0.5 { view.tailX = tail; view.needsDisplay = true }
        let o = NSPoint(x: x.rounded(), y: y.rounded())
        if panel.frame.origin != o { panel.setFrameOrigin(o) }
    }
}
