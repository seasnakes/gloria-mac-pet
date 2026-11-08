import AppKit
import QuartzCore

// GLORIA on the desktop: she stands on window tops, follows them, falls when they go, can be picked up and
// thrown, dances when music plays, and acts out what Claude Code / Codex are doing.

final class PetView: NSView {
    weak var pet: Pet?
    override var isFlipped: Bool { false }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseDown(with e: NSEvent) { pet?.mouseDown(e) }
    override func mouseDragged(with e: NSEvent) { pet?.mouseDragged(e) }
    override func mouseUp(with e: NSEvent) { pet?.mouseUp(e) }
    override func rightMouseDown(with e: NSEvent) { pet?.rightClick(e, in: self) }
}

/// One sprite in a transparent window, anchored at her feet. Used for GLORIA and her clones.
final class Body {
    let panel: PetPanel
    let view = PetView()
    let sprite = CALayer()
    let fx = CALayer()
    private(set) var scale: CGFloat
    private var shown: Cell?

    var spriteSize: CGSize { CGSize(width: CGFloat(Sprites.cellW) * scale, height: CGFloat(Sprites.cellH) * scale) }
    /// Room under her feet: sitting on a window edge, her legs hang over the front of the window.
    var footInset: CGFloat { ceil(58 * scale) + 3 }
    var winSize: CGSize { CGSize(width: ceil(spriteSize.width * 1.9), height: ceil(spriteSize.height * 1.62) + footInset) }

    init(scale: CGFloat) {
        self.scale = scale
        panel = PetPanel(size: .zero)
        view.layer = CALayer()
        view.wantsLayer = true
        sprite.contentsGravity = .resize
        sprite.magnificationFilter = .linear
        sprite.minificationFilter = .trilinear
        sprite.anchorPoint = CGPoint(x: Sprites.footX / CGFloat(Sprites.cellW), y: Sprites.footUp / CGFloat(Sprites.cellH))
        view.layer?.addSublayer(sprite)
        view.layer?.addSublayer(fx)
        panel.contentView = view
        resize(scale)
    }

    func resize(_ s: CGFloat) {
        scale = s
        CATransaction.begin(); CATransaction.setDisableActions(true)
        panel.setContentSize(winSize)
        view.frame = NSRect(origin: .zero, size: winSize)
        sprite.bounds = CGRect(origin: .zero, size: spriteSize)
        sprite.position = CGPoint(x: winSize.width / 2, y: footInset)
        fx.frame = CGRect(origin: .zero, size: winSize)
        CATransaction.commit()
    }

    func show(_ c: Cell, _ sprites: Sprites) {
        guard c != shown else { return }
        shown = c
        sprite.contents = sprites.image(c)
    }
    var cell: Cell? { shown }

    func place(feet: CGPoint) {
        let o = NSPoint(x: (feet.x - winSize.width / 2).rounded(), y: (feet.y - footInset).rounded())
        if panel.frame.origin != o { panel.setFrameOrigin(o) }
    }

    func pose(rot: CGFloat, sx: CGFloat, sy: CGFloat, dx: CGFloat, lift: CGFloat, alpha: Float) {
        var t = CATransform3DMakeTranslation(dx, lift, 0)
        t = CATransform3DRotate(t, rot * .pi / 180, 0, 0, 1)
        t = CATransform3DScale(t, sx, sy, 1)
        sprite.transform = t
        sprite.opacity = alpha
    }

    /// Is the screen point on one of her opaque pixels?
    func hits(_ screen: CGPoint, _ sprites: Sprites) -> Bool {
        let f = panel.frame
        guard f.contains(screen), let c = shown, let root = view.layer else { return false }
        let lp = sprite.convert(CGPoint(x: screen.x - f.minX, y: screen.y - f.minY), from: root)
        let x = Int(lp.x / scale), yTop = Sprites.cellH - 1 - Int(lp.y / scale)
        return sprites.alpha(c, x: x, yTop: yTop) > 40
    }

    func particle(_ text: String, color: NSColor, size: CGFloat) {
        let l = CATextLayer()
        l.string = text
        l.fontSize = size
        l.font = NSFont.systemFont(ofSize: size, weight: .bold)
        l.foregroundColor = color.cgColor
        l.alignmentMode = .center
        l.contentsScale = 2
        l.shadowColor = NSColor.white.cgColor
        l.shadowOpacity = 0.9
        l.shadowRadius = 1.5
        l.shadowOffset = .zero
        let w = winSize.width
        let start = CGPoint(x: w / 2 + CGFloat.random(in: -0.42...0.42) * spriteSize.width, y: footInset + spriteSize.height * CGFloat.random(in: 0.5...0.8))
        l.bounds = CGRect(x: 0, y: 0, width: size * 1.4, height: size * 1.4)
        l.position = start
        fx.addSublayer(l)
        let rise = CABasicAnimation(keyPath: "position")
        rise.fromValue = NSValue(point: start)
        rise.toValue = NSValue(point: CGPoint(x: start.x + CGFloat.random(in: -18...18), y: min(winSize.height - size, start.y + spriteSize.height * 0.5)))
        let fade = CAKeyframeAnimation(keyPath: "opacity")
        fade.values = [0, 1, 1, 0]
        fade.keyTimes = [0, 0.15, 0.6, 1]
        let grow = CABasicAnimation(keyPath: "transform.scale")
        grow.fromValue = 0.6
        grow.toValue = 1.15
        let g = CAAnimationGroup()
        g.animations = [rise, fade, grow]
        g.duration = 1.3
        g.timingFunction = CAMediaTimingFunction(name: .easeOut)
        l.opacity = 0
        l.add(g, forKey: "float")
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.35) { l.removeFromSuperlayer() }
    }
}

final class Clone {
    let id: String
    let body: Body
    var offset: CGFloat
    var target: CGFloat
    let born = now()
    var leaving: Double?
    init(id: String, scale: CGFloat, target: CGFloat) {
        self.id = id
        body = Body(scale: scale)
        body.panel.ignoresMouseEvents = true
        offset = 0
        self.target = target
    }
}

final class Pet {
    enum Mode: String { case perched, walking, held, airborne, hopping }

    let sprites: Sprites
    let body: Body
    let bubble = Bubble()
    let music: MusicWatch
    var onMenu: ((NSEvent, NSView) -> Void)?

    private(set) var mode: Mode = .airborne
    private(set) var feet = CGPoint(x: 400, y: 400)
    private var vel = CGVector.zero
    private(set) var seat: Ledge?
    private var hostRect: CGRect?
    private var wins: [Win] = []
    private var order: [CGWindowID] = []
    private(set) var ledges: [Ledge] = []
    private var lastScan: Double = 0
    private var seatLostAt: Double?
    private var coveredSince: Double?
    private var walkTarget: CGFloat = 0
    private var hop: (from: CGPoint, to: CGPoint, ledge: Ledge, start: Double, dur: Double, arc: CGFloat)?

    // Animation
    private var shot: (anim: Anim, start: Double, loops: Int, cells: [Int]?)?
    private var idleStart = now()
    private var idleHold = 2.5
    private var nextIdea = now() + 6
    private var lookUntil: Double = 0
    private var lookDir: Double = 90
    private var jostleUntil: Double = 0
    private var glitchUntil: Double = 0
    private var landSquash: (start: Double, amount: CGFloat)?
    private var rot: CGFloat = 0
    private var lastTick = now()
    private var lastBeatIndex = -1
    private var wasDancing = false
    private var wasSitting = false
    private var headRise: CGFloat = 0          // feet → top of her head, in points, for the bubble
    private var asleep = false
    private var lastZ: Double = 0
    private var lastIdleCheck: Double = 0
    private var userIdle: Double = 0
    var forceSleepUntil: Double = 0
    private var synthHold: (start: Double, until: Double, center: CGPoint)?
    private var heldSince: Double = 0

    // Dock, cursor chasing, pokes
    private var timer: Timer?
    private var observers: [NSObjectProtocol] = []
    private(set) var dockLift: CGFloat = 0
    private var dock: (tile: CGFloat, large: CGFloat, on: Bool) = (64, 128, false)
    private var lastDockRead: Double = -10
    private var bounceStart: Double = 0, bounceUntil: Double = 0
    private var chasing = false
    private var chaseLost: Double?
    private var lastPounce: Double = 0
    private var lastMouse = CGPoint.zero
    private var mouseSpeed: CGFloat = 0
    private var clicks: [Double] = []
    private var grumpyUntil: Double = 0
    var fakePointer: (p: CGPoint, until: Double)?      // checks only: stands in for the cursor without moving it
    private func pointer() -> CGPoint {
        if let f = fakePointer, now() < f.until { return f.p }
        return NSEvent.mouseLocation
    }

    // Drag
    private var pressAt = CGPoint.zero
    private var grab = CGVector.zero
    private var dragging = false
    private var samples: [(Double, CGPoint)] = []
    private var heldVX: CGFloat = 0

    // Code / Husk mode state, set by HuskBrain
    var working = false
    var workCaption = ""
    var awaiting: String?
    var awaitingTag = "CLAUDE"
    private var clones: [Clone] = []
    private var lastClonePlace: Double = 0

    var scale: CGFloat { body.scale }
    var spriteSize: CGSize { body.spriteSize }
    var gravity: CGFloat { 2600 }

    init(sprites: Sprites, music: MusicWatch) {
        self.sprites = sprites
        self.music = music
        body = Body(scale: CGFloat(Prefs.scale) * Character.current.scaleMul)
        body.view.pet = self
        body.show(Anim.idle.cell(0), sprites)
        body.panel.ignoresMouseEvents = true
    }

    // MARK: Lifecycle

    func start(at: CGPoint? = nil) {
        rescan(force: true)
        if let at {
            // Switching characters: drop in where the previous one was.
            feet = CGPoint(x: at.x, y: at.y + 60)
            mode = .airborne
            vel = CGVector(dx: 0, dy: 120)
            body.panel.level = .floating
            body.place(feet: feet)
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.9) { [weak self] in
                self?.play(.wave, loops: 2)
                self?.say(Character.current.greeting, tag: Character.current.tag, duration: 3)
            }
        } else {
            summon(entrance: true)
        }
        body.panel.orderFrontRegardless()
        let timer = Timer(timeInterval: 1.0 / 60, repeats: true) { [weak self] _ in self?.tick() }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
        let ws = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.willLaunchApplicationNotification, NSWorkspace.didLaunchApplicationNotification] {
            observers.append(ws.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in self?.dockBounce() })
        }
        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main) { [weak self] _ in
            self?.rescan(force: true)
            if let s = self, Desktop.screen(at: CGPoint(x: s.feet.x, y: s.feet.y + 4)) == nil { s.summon(entrance: false) }
        }
    }

    func setScale(_ s: CGFloat) {
        Prefs.scale = Double(s)
        let k = s * Character.current.scaleMul
        body.resize(k)
        for c in clones { c.body.resize(k * 0.62) }
        rescan(force: true)
        body.place(feet: feet)
    }

    /// Drop her onto the front window (or the floor) from above.
    func summon(entrance: Bool) {
        rescan(force: true)
        let mouse = NSEvent.mouseLocation
        let scr = Desktop.nearestScreen(mouse) ?? NSScreen.main
        let onScreen = ledges.filter { l in
            guard let v = scr?.visibleFrame else { return true }
            return l.y >= v.minY && l.y <= v.maxY && l.right > v.minX && l.left < v.maxX
        }
        // The frontmost window top that's wide enough for her whole body, else the floor.
        let roomy = onScreen.filter { !$0.isFloor && $0.right - $0.left >= spriteSize.width * 0.85 }
        let target = roomy.first ?? onScreen.first { $0.isFloor } ?? ledges.last
        guard let l = target else { return }
        let inset = min(spriteSize.width * 0.32, (l.right - l.left) / 2)
        let x = mouse.x > l.left + inset && mouse.x < l.right - inset && !l.isFloor
            ? mouse.x : clamp(l.right - max(90, (l.right - l.left) * 0.22), l.left + inset, l.right - inset)
        // Drop from just above the target, below any other ledge over that spot so she can't catch it first.
        var ceiling = (scr?.visibleFrame.maxY ?? l.y + 400) - spriteSize.height - 4
        for o in ledges where o.y > l.y && x >= o.left - 8 && x <= o.right + 8 { ceiling = min(ceiling, o.y - 2) }
        let start = min(ceiling, l.y + (entrance ? 320 : 160))
        feet = CGPoint(x: x, y: max(l.y, start))
        seat = nil
        mode = .airborne
        vel = CGVector(dx: 0, dy: entrance ? -200 : 0)
        body.panel.level = .floating
        body.place(feet: feet)
        if entrance {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) { [weak self] in
                self?.play(.wave, loops: 2)
                self?.say(Character.current.greeting, tag: Character.current.tag)
            }
        }
    }

    // MARK: Public reactions

    func say(_ text: String, tag: String? = nil, kind: BubbleView.Kind = .speech, duration: Double? = nil, sticky: Bool = false) {
        bubble.show(text, tag: tag, kind: kind, duration: duration, sticky: sticky)
        bubble.follow(headTop, tick: now())
    }

    func play(_ a: Anim, loops: Int = 1, cells: [Int]? = nil) {
        guard mode != .held && mode != .airborne && mode != .hopping else { return }
        if mode == .walking { mode = .perched }
        shot = (a, now(), loops, cells)
    }

    func glitch(_ seconds: Double = 1.0) { glitchUntil = now() + seconds }

    func sparkle(_ text: String = "♥", color: NSColor = Palette.pink, count: Int = 3) {
        for i in 0..<count {
            DispatchQueue.main.asyncAfter(deadline: .now() + Double(i) * 0.12) { [weak self] in
                guard let s = self else { return }
                s.body.particle(text, color: color, size: 15 * s.scale / 0.62)
            }
        }
    }

    func forceDance(_ seconds: Double) {
        music.forcedUntil = now() + seconds
        if mode == .walking { mode = .perched }
    }

    func throwHer(vx: CGFloat, vy: CGFloat) {
        seat = nil
        hostRect = nil
        mode = .airborne
        vel = CGVector(dx: vx, dy: vy)
        body.panel.level = .floating
    }

    func place(x: CGFloat, y: CGFloat) {
        feet = CGPoint(x: x, y: y)
        throwHer(vx: 0, vy: 0)
    }

    var headTop: CGPoint { CGPoint(x: feet.x, y: feet.y + dockLift + (headRise > 0 ? headRise : spriteSize.height * 0.94)) }
    var isAsleep: Bool { asleep }

    /// Hold her up in the air for a few seconds as if dragged (for checks and the demo menu).
    func simulateHold(_ seconds: Double) {
        synthHold = (now(), now() + seconds, CGPoint(x: feet.x, y: feet.y + spriteSize.height * 1.2))
        grab = CGVector(dx: 0, dy: -spriteSize.height * 1.2)
        startHold()
    }

    private func startHold() {
        mode = .held
        heldSince = now()
        seat = nil
        hostRect = nil
        shot = nil
        hop = nil
        wake(quiet: true)
        body.panel.level = .floating
        body.panel.orderFrontRegardless()
        if !bubble.sticky { bubble.hide() }
    }

    /// Where her feet go while she hangs from the cursor by the back of her neck.
    private func heldFeet(_ m: CGPoint, _ t: Double) -> CGPoint {
        guard sprites.hasExtras else { return CGPoint(x: m.x + grab.dx, y: m.y + grab.dy) }
        let hang = CGVector(dx: 0, dy: -Character.current.hang * scale)
        let u = CGFloat(clamp((t - heldSince) / 0.16, 0, 1))
        return CGPoint(x: m.x + lerp(grab.dx, hang.dx, u), y: m.y + lerp(grab.dy, hang.dy, u))
    }

    // MARK: Clones (subagents)

    func spawnClone(_ id: String) {
        guard clones.count < 4, !clones.contains(where: { $0.id == id }) else { return }
        let side: CGFloat = clones.count % 2 == 0 ? 1 : -1
        let dist = spriteSize.width * (0.62 + 0.42 * CGFloat(clones.count / 2))
        let c = Clone(id: id, scale: scale * 0.62, target: side * dist)
        c.body.show(Anim.idle.cell(0), sprites)
        c.body.place(feet: feet)
        c.body.panel.level = body.panel.level
        c.body.panel.alphaValue = 0.88
        c.body.panel.orderFrontRegardless()
        clones.append(c)
        glitch(0.35)
    }

    func retireClone(_ id: String?) {
        for c in clones where (id == nil || c.id == id) && c.leaving == nil { c.leaving = now() }
    }

    var cloneCount: Int { clones.filter { $0.leaving == nil }.count }

    // MARK: Mouse

    func mouseDown(_ e: NSEvent) {
        let m = NSEvent.mouseLocation
        pressAt = m
        grab = CGVector(dx: feet.x - m.x, dy: feet.y - m.y)
        dragging = false
        samples = [(now(), m)]
    }

    func mouseDragged(_ e: NSEvent) {
        let m = NSEvent.mouseLocation
        if !dragging && hypot(m.x - pressAt.x, m.y - pressAt.y) > 3 {
            dragging = true
            startHold()
        }
        guard dragging else { return }
        let t = now()
        samples.append((t, m))
        samples.removeAll { t - $0.0 > 0.1 }
        feet = heldFeet(m, t)
    }

    func mouseUp(_ e: NSEvent) {
        if dragging {
            dragging = false
            var v = CGVector.zero
            if let a = samples.first, let b = samples.last, b.0 - a.0 > 0.012 {
                v = CGVector(dx: (b.1.x - a.1.x) / CGFloat(b.0 - a.0), dy: (b.1.y - a.1.y) / CGFloat(b.0 - a.0))
            }
            let sp = hypot(v.dx, v.dy)
            if sp > 3200 { v.dx *= 3200 / sp; v.dy *= 3200 / sp }
            mode = .airborne
            vel = v
            if sp > 1400 { say(["哇啊啊——", "飞起来了！", "轻点轻点！"].randomElement()!, duration: 1.6) }
            return
        }
        if e.clickCount >= 2 {
            play(.jump)
            sparkle("✦", color: Palette.periwinkle, count: 2)
        } else {
            pat()
        }
    }

    func rightClick(_ e: NSEvent, in v: NSView) { onMenu?(e, v) }

    private func pat() {
        if asleep { wake(quiet: false); return }
        let t = now()
        clicks.append(t); clicks.removeAll { t - $0 > 3 }
        if t < grumpyUntil { say("哼！", duration: 1.2); return }
        if clicks.count >= 5 && awaiting == nil {
            clicks.removeAll()
            grumpyUntil = t + 5
            play(.failed)
            sparkle("💢", color: Palette.pink, count: 2)
            say(Character.current.angry.randomElement()!, duration: 2.6)
            return
        }
        if awaiting != nil {
            say("要你回到 \(awaitingTag == "CODEX" ? "Codex" : "Claude Code") 里批准哦～我只负责提醒。", tag: awaitingTag, duration: 4)
            return
        }
        play(.wave, loops: 2)
        sparkle()
        if Int.random(in: 0..<3) == 0 || !bubble.visible {
            say(Character.current.pats.randomElement()!, duration: 2.4)
        }
    }

    // MARK: Tick

    private func tick() {
        let t = now()
        let dt = CGFloat(clamp(t - lastTick, 0, 0.05))
        lastTick = t
        if t - lastScan > (mode == .perched || mode == .walking ? 1.0 / 15 : 1.0 / 30) { rescan(force: false); lastScan = t }

        if mode == .walking && (awaiting != nil || music.dancing) { mode = .perched }
        if let h = synthHold {
            let m = CGPoint(x: h.center.x + CGFloat(sin((t - h.start) * 2.6)) * 160, y: h.center.y)
            samples.append((t, m)); samples.removeAll { t - $0.0 > 0.1 }
            feet = heldFeet(m, t)
            if t > h.until { synthHold = nil; mode = .airborne; vel = CGVector(dx: 600, dy: 500) }
        }
        if t - lastIdleCheck > 1 { lastIdleCheck = t; checkSleep(t) }
        switch mode {
        case .held: break
        case .airborne: fly(dt)
        case .hopping: hopStep(t)
        case .walking: walkStep(dt)
        case .perched: break
        }
        if mode == .perched || mode == .walking { holdSeat(t) }

        updateDockLift(t, dt)
        chase(t, dt)
        animate(t, dt)
        body.place(feet: CGPoint(x: feet.x, y: feet.y + dockLift))
        updateClones(t, dt)
        bubble.follow(headTop, tick: t)

        // Clicks pass through everything except her.
        let hit = dragging || body.hits(NSEvent.mouseLocation, sprites)
        if body.panel.ignoresMouseEvents == hit { body.panel.ignoresMouseEvents = !hit }

        if mode == .perched && shot == nil && !music.dancing && awaiting == nil { think(t) }
    }

    private func rescan(force: Bool) {
        let r = Desktop.scan(excluding: getpid())
        wins = r.wins
        order = r.order
        ledges = Desktop.ledges(wins, headroom: spriteSize.height * 0.9)
    }

    // MARK: Physics

    private func fly(_ dt: CGFloat) {
        vel.dy -= gravity * dt
        vel.dx *= pow(0.7, dt)
        var next = CGPoint(x: feet.x + vel.dx * dt, y: feet.y + vel.dy * dt)
        let half = spriteSize.width * 0.3
        let all = NSScreen.screens.reduce(CGRect.null) { $0.union($1.frame) }
        if next.x < all.minX + half { next.x = all.minX + half; vel.dx = abs(vel.dx) * 0.45 }
        if next.x > all.maxX - half { next.x = all.maxX - half; vel.dx = -abs(vel.dx) * 0.45 }
        if let scr = Desktop.nearestScreen(CGPoint(x: next.x, y: max(next.y, 0))) {
            let f = scr.frame
            if next.y + spriteSize.height > f.maxY { next.y = f.maxY - spriteSize.height; vel.dy = -abs(vel.dy) * 0.3 }
        }
        if vel.dy <= 0 {
            var best: Ledge?
            for l in ledges where feet.y >= l.y - 0.5 && next.y <= l.y && next.x >= l.left - 6 && next.x <= l.right + 6 {
                if best == nil || l.y > best!.y { best = l }
            }
            if let l = best {
                let impact = -vel.dy
                feet = CGPoint(x: next.x, y: l.y)
                land(on: l, impact: impact)
                return
            }
            // Safety net: never fall past the bottom of every screen.
            if let floor = ledges.filter({ $0.isFloor && next.x >= $0.left - 1 && next.x <= $0.right + 1 }).max(by: { $0.y < $1.y }), next.y < floor.y - 2 {
                feet = CGPoint(x: next.x, y: floor.y)
                land(on: floor, impact: -vel.dy)
                return
            }
            if !NSScreen.screens.contains(where: { $0.frame.insetBy(dx: -2, dy: -2).contains(next) }),
               let floor = ledges.filter({ $0.isFloor }).min(by: { abs(($0.left + $0.right) / 2 - next.x) < abs(($1.left + $1.right) / 2 - next.x) }) {
                feet = CGPoint(x: clamp(next.x, floor.left + 40, floor.right - 40), y: floor.y)
                land(on: floor, impact: 0)
                return
            }
        }
        feet = next
    }

    private func land(on l: Ledge, impact: CGFloat) {
        mode = .perched
        vel = .zero
        seat = l
        hostRect = l.win.flatMap { id in wins.first { $0.id == id }?.rect }
        seatLostAt = nil
        coveredSince = nil
        let margin = min(spriteSize.width * 0.3, (l.right - l.left) / 2)
        var x = clamp(feet.x, l.left + margin, l.right - margin)
        if let v = Desktop.nearestScreen(CGPoint(x: x, y: l.y + 2))?.visibleFrame { x = clamp(x, v.minX + margin, v.maxX - margin) }
        feet = CGPoint(x: x, y: l.y)
        landSquash = (now(), clamp(impact / 2400, 0.08, 0.28))
        if impact > 1700 {
            shot = (.failed, now(), 1, nil)
            say(Character.current.ouch.randomElement()!, duration: 2)
        } else if impact > 300 {
            shot = (.jump, now(), 1, [3, 4])
        }
        nextIdea = now() + Double.random(in: 4...9)
        updateLevel()
    }

    private func fall() {
        seat = nil
        hostRect = nil
        mode = .airborne
        vel = CGVector(dx: 0, dy: 60)
        body.panel.level = .floating
    }

    private func hopTo(_ l: Ledge, x: CGFloat) {
        let to = CGPoint(x: clamp(x, l.left + spriteSize.width * 0.3, l.right - spriteSize.width * 0.3), y: l.y)
        let d = hypot(to.x - feet.x, to.y - feet.y)
        hop = (feet, to, l, now(), Double(0.5 + min(0.5, d / 1600)), max(40, (to.y - feet.y)) + 46 * scale / 0.62)
        mode = .hopping
        seat = nil
        hostRect = nil
        body.panel.level = .floating
    }

    private func hopStep(_ t: Double) {
        guard let h = hop else { mode = .airborne; return }
        let crouch = 0.12
        let u = CGFloat(clamp((t - h.start - crouch) / h.dur, 0, 1))
        if t - h.start < crouch { return }
        // Parabola that clears both ends.
        let base = lerp(h.from.y, h.to.y, u)
        let lift = 4 * u * (1 - u) * h.arc
        feet = CGPoint(x: lerp(h.from.x, h.to.x, u), y: base + lift)
        if u >= 1 {
            hop = nil
            // Re-read the ledge: the window may have moved meanwhile.
            rescan(force: true)
            if let id = h.ledge.win, let l = ledges.first(where: { $0.win == id && h.to.x >= $0.left - 4 && h.to.x <= $0.right + 4 }) {
                feet.y = l.y; land(on: l, impact: 400)
            } else if h.ledge.isFloor {
                land(on: h.ledge, impact: 400)
            } else { fall() }
        }
    }

    private func walkStep(_ dt: CGFloat) {
        let speed = 95 * scale / 0.62 * (chasing ? 1.8 : 1)
        let dx = walkTarget - feet.x
        if abs(dx) < speed * dt + 0.5 { feet.x = walkTarget; mode = .perched; nextIdea = now() + Double.random(in: 5...12); return }
        feet.x += (dx > 0 ? 1 : -1) * speed * dt
    }

    /// Ride along with the window she stands on; drop if it goes away or gets covered.
    private func holdSeat(_ t: Double) {
        guard let s = seat else { return }
        guard let id = s.win else {
            // Floor: keep on the current screen's floor (Dock may have moved).
            if let v = Desktop.nearestScreen(CGPoint(x: feet.x, y: feet.y + 2))?.visibleFrame, abs(v.minY - feet.y) > 0.5 {
                if feet.y > v.minY + 1 { fall() } else { feet.y = v.minY; seat?.y = v.minY }
            }
            return
        }
        guard let w = wins.first(where: { $0.id == id }) else {
            if seatLostAt == nil { seatLostAt = t }
            if t - seatLostAt! > 0.3 { fall() }
            return
        }
        seatLostAt = nil
        if let old = hostRect, old != w.rect {
            let dx = w.rect.minX - old.minX
            let moved = abs(dx) + abs(w.rect.maxY - old.maxY)
            if abs(w.rect.width - old.width) < 0.5 { feet.x += dx; walkTarget += dx }
            if moved > 2 { jostleUntil = t + 0.35 }
        }
        hostRect = w.rect
        seat?.left = w.rect.minX
        seat?.right = w.rect.maxX
        seat?.y = w.rect.maxY
        feet.y = w.rect.maxY
        let margin = spriteSize.width * 0.22
        feet.x = clamp(feet.x, w.rect.minX + margin, w.rect.maxX - margin)
        if let scr = Desktop.screen(at: CGPoint(x: feet.x, y: feet.y - 1)), feet.y + spriteSize.height * 0.5 > scr.frame.maxY + 4 {
            fall(); return
        }

        // Something in front of her window now sits over her body.
        let probe = CGPoint(x: feet.x, y: feet.y + spriteSize.height * 0.35)
        let hostIndex = order.firstIndex(of: id) ?? Int.max
        let cover = wins.first { w2 in
            guard w2.id != id, let i = order.firstIndex(of: w2.id), i < hostIndex else { return false }
            return w2.rect.contains(probe)
        }
        if let c = cover {
            if coveredSince == nil { coveredSince = t }
            if t - coveredSince! > 0.7 {
                coveredSince = nil
                if let l = ledges.first(where: { $0.win == c.id }) { hopTo(l, x: feet.x) } else { fall() }
                return
            }
        } else { coveredSince = nil }
        keepAbove(id)
    }

    /// Stand in the window stack just above her window, so windows in front of it can cover her.
    private func keepAbove(_ host: CGWindowID) {
        let me = CGWindowID(body.panel.windowNumber)
        guard let hi = order.firstIndex(of: host) else { return }
        let mi = order.firstIndex(of: me)
        if body.panel.level != .normal { body.panel.level = .normal }
        if mi == nil || mi! > hi || hi - mi! > 1 + clones.count + (bubble.visible ? 1 : 0) {
            body.panel.order(.above, relativeTo: Int(host))
            for c in clones { c.body.panel.level = .normal; c.body.panel.order(.above, relativeTo: body.panel.windowNumber) }
        }
    }

    private func updateLevel() {
        if seat?.win == nil {
            body.panel.level = .floating
            for c in clones { c.body.panel.level = .floating }
        } else if let id = seat?.win {
            body.panel.level = .normal
            body.panel.order(.above, relativeTo: Int(id))
        }
    }

    // MARK: Dock and cursor

    /// Riding the Dock's magnification: the icon under her grows, so she rises with it.
    private func updateDockLift(_ t: Double, _ dt: CGFloat) {
        if t - lastDockRead > 5 { readDock(); lastDockRead = t }
        var target: CGFloat = 0
        if dock.on, mode == .perched || mode == .walking, let s = seat, s.isFloor,
           let scr = Desktop.nearestScreen(CGPoint(x: feet.x, y: feet.y + 2)) {
            let f = scr.frame, v = scr.visibleFrame, m = pointer()
            if v.minY - f.minY > 8, m.y >= f.minY, m.y < v.minY, m.x >= f.minX, m.x <= f.maxX {
                let d = abs(m.x - feet.x)
                let reach = max(dock.large * 1.6, dock.tile * 3.2)
                if d < reach { target = (dock.large - dock.tile) * CGFloat((cos(Double.pi * Double(d / reach)) + 1) / 2) }
            }
        }
        dockLift += (target - dockLift) * min(1, 0.32 * dt * 60)
        if target == 0 && dockLift < 0.3 { dockLift = 0 }
    }

    private func readDock() {
        func val(_ k: String) -> Any? { CFPreferencesCopyAppValue(k as CFString, "com.apple.dock" as CFString) }
        let tile = (val("tilesize") as? NSNumber)?.doubleValue ?? 48
        let large = (val("largesize") as? NSNumber)?.doubleValue ?? 128
        let on = (val("magnification") as? NSNumber)?.boolValue ?? false
        let bottom = ((val("orientation") as? String) ?? "bottom") == "bottom"
        dock = (CGFloat(tile), CGFloat(max(large, tile)), on && bottom)
    }

    /// An app is launching, so its Dock icon is bouncing: if she's on the Dock, she bounces along.
    private func dockBounce() {
        let t = now()
        guard t > bounceUntil + 0.5, mode == .perched, let s = seat, s.isFloor, awaiting == nil,
              let scr = Desktop.nearestScreen(CGPoint(x: feet.x, y: feet.y + 2)), scr.visibleFrame.minY - scr.frame.minY > 8 else { return }
        wake(quiet: true)
        shot = nil
        bounceStart = t
        bounceUntil = t + 0.55 * 3
    }

    /// The cursor skimming along her ledge: she runs after it and pounces.
    private func chase(_ t: Double, _ dt: CGFloat) {
        let m = pointer()
        let v = hypot(m.x - lastMouse.x, m.y - lastMouse.y) / max(dt, 0.001)
        mouseSpeed += (v - mouseSpeed) * 0.2
        lastMouse = m
        guard Prefs.chase, let s = seat, mode == .perched || mode == .walking, shot == nil, !working, awaiting == nil,
              !music.dancing, !asleep, t > grumpyUntil, t > bounceUntil else {
            if chasing { chasing = false; if mode == .walking { mode = .perched } }
            return
        }
        let inBand = m.y >= feet.y + dockLift - 8 && m.y <= feet.y + dockLift + spriteSize.height * 1.1 && m.x >= s.left && m.x <= s.right
        let dx = m.x - feet.x
        if !chasing {
            if inBand && abs(dx) > 70 && abs(dx) < 460 && mouseSpeed > 60 && t - lastPounce > 2.5 && !body.hits(m, sprites) {
                chasing = true; chaseLost = nil; mode = .walking
            }
            return
        }
        if !inBand {
            if chaseLost == nil { chaseLost = t }
            if t - chaseLost! > 0.7 { chasing = false; mode = .perched }
            return
        }
        chaseLost = nil
        let margin = spriteSize.width * 0.3
        walkTarget = clamp(m.x, s.left + margin, s.right - margin)
        if mode != .walking { mode = .walking }
        if abs(dx) < 26 * scale / 0.62 {
            chasing = false
            mode = .perched
            lastPounce = t
            play(.jump)
            sparkle("✦", color: Palette.periwinkle, count: 2)
            if Int.random(in: 0..<3) == 0 { say(["抓到了！", "嘿！", "跑不掉的～"].randomElement()!, duration: 1.4) }
        }
    }

    func debugDockBounce() { bounceUntil = 0; dockBounce() }

    /// Hide everything and stop ticking (switching characters).
    func teardown() {
        timer?.invalidate(); timer = nil
        for o in observers { NSWorkspace.shared.notificationCenter.removeObserver(o) }
        observers.removeAll()
        for c in clones { c.body.panel.orderOut(nil) }
        clones.removeAll()
        bubble.panel.orderOut(nil)
        body.panel.orderOut(nil)
    }

    // MARK: Idle thoughts

    private func think(_ t: Double) {
        guard t > nextIdea, Prefs.wander, let s = seat else { return }
        nextIdea = t + Double.random(in: 7...16)
        if working || asleep { return }
        let r = Double.random(in: 0..<1)
        let margin = spriteSize.width * 0.3
        if r < 0.42 {
            var x = CGFloat.random(in: (s.left + margin)...(max(s.left + margin + 1, s.right - margin)))
            if let v = Desktop.nearestScreen(feet)?.visibleFrame { x = clamp(x, v.minX + margin, v.maxX - margin) }
            if abs(x - feet.x) > 50 { walkTarget = x; mode = .walking }
        } else if r < 0.58 {
            play(.wave, loops: 1)
        } else if r < 0.74 {
            lookDir = Bool.random() ? 90 : 270
            lookUntil = t + 1.6
        } else if r < 0.84 {
            // Hop to another window that's close by.
            let near = ledges.filter { l in
                guard l.win != nil, l.win != s.win, l.right - l.left >= spriteSize.width * 0.85 else { return false }
                let x = clamp(feet.x, l.left + margin, l.right - margin)
                return abs(x - feet.x) < 420 && abs(l.y - feet.y) < 260
            }
            if let l = near.randomElement() { hopTo(l, x: clamp(feet.x, l.left + margin, l.right - margin)) }
        }
    }

    // MARK: Animation

    private func animate(_ t: Double, _ dt: CGFloat) {
        var cell = Anim.idle.cell(0)
        var targetRot: CGFloat = 0
        var sx: CGFloat = 1, sy: CGFloat = 1, dx: CGFloat = 0, lift: CGFloat = 0
        var alpha: Float = 1
        var pivot: CGFloat = 0                      // rotate around a point this far above her feet (0 = the feet)
        let k = scale / 0.62
        let extras = sprites.hasExtras
        let dancing = music.dancing && (mode == .perched)

        if dancing != wasDancing {
            wasDancing = dancing
            if dancing {
                lastBeatIndex = -1
                if !music.forced && !bubble.visible {
                    let who = music.playerName.isEmpty ? "" : "\(music.playerName) 在放歌，"
                    say("\(who)跳起来～ ♪", duration: 2.6)
                }
            }
        }

        switch mode {
        case .held:
            let a = samples.first, b = samples.last
            var vx: CGFloat = 0
            if let a, let b, b.0 - a.0 > 0.01 { vx = (b.1.x - a.1.x) / CGFloat(b.0 - a.0) }
            heldVX += (vx - heldVX) * 0.25
            if extras {
                // Hanging by the back of her neck: flail, and swing like a pendulum behind the cursor.
                cell = Anim.dangle.cell(Anim.dangle.index(at: t - heldSince))
                targetRot = clamp(-heldVX * 0.02, -30, 30) + CGFloat(sin(t * 4.2)) * 4
                pivot = Character.current.hang * scale
            } else {
                cell = abs(heldVX) > 160 ? (heldVX > 0 ? Anim.runRight : Anim.runLeft).cell(Int(t / 0.06)) : Anim.waiting.cell(Int(t / 0.2) % 3)
                targetRot = clamp(-heldVX * 0.018, -24, 24) + CGFloat(sin(t * 7)) * 3
            }
        case .airborne:
            cell = extras ? Anim.dangle.cell(Int(t / 0.09)) : Anim.jump.cell(2)
            targetRot = clamp(-vel.dx * 0.014, -32, 32) + CGFloat(sin(t * 16)) * min(8, abs(vel.dx) / 120)
        case .hopping:
            if let h = hop {
                let u = (t - h.start - 0.12) / h.dur
                cell = Anim.jump.cell(u < 0 ? 1 : (u < 0.8 ? 2 : 3))
                if u < 0 { sy = 0.92; sx = 1.05 }
                targetRot = h.to.x > h.from.x ? -6 : 6
            }
        case .walking:
            cell = (walkTarget > feet.x ? Anim.runRight : Anim.runLeft).cell(Int(t / Anim.runRight.frameTime))
            lift = CGFloat(abs(sin(t * 14))) * 2 * k
        case .perched:
            if let s = shot {
                let ft = s.anim.frameTime
                let seq = s.cells ?? Array(0..<s.anim.count)
                let i = Int((t - s.start) / ft)
                if i >= seq.count * s.loops { shot = nil; idleStart = t } else { cell = s.anim.cell(seq[i % seq.count]) }
                if s.anim == .jump && s.cells == nil, i < seq.count {
                    // A little hop in place for the jump reaction.
                    let u = (t - s.start) / (ft * Double(seq.count))
                    lift = CGFloat(max(0, sin(u * .pi))) * 16 * k
                }
            }
            if shot == nil {
                if let _ = awaiting {
                    cell = Anim.waiting.cell(Int(t / Anim.waiting.frameTime))
                    // A little attention bounce every two seconds.
                    let u = t.truncatingRemainder(dividingBy: 2.2)
                    if u < 0.36 { lift = CGFloat(sin(u / 0.36 * .pi)) * 9 * k }
                } else if t < bounceUntil {
                    // Hopping along with a bouncing Dock icon.
                    let u = (t - bounceStart) / 0.55
                    let ph = u - floor(u)
                    cell = Anim.jump.cell(ph < 0.12 ? 1 : (ph < 0.8 ? 2 : 3))
                    lift = CGFloat(max(0, sin(ph * .pi))) * 22 * k
                } else if asleep && extras {
                    cell = Anim.sleep.cell(Anim.sleep.index(at: t, speed: 1.3))
                    if t - lastZ > 1.5 { lastZ = t; body.particle("z", color: Palette.periwinkle, size: CGFloat.random(in: 12...17) * k) }
                } else if dancing {
                    (cell, targetRot, sx, sy, dx, lift) = dance(t)
                } else if t < jostleUntil {
                    cell = canSit ? Anim.perch.cell(0) : Anim.waiting.cell(0)
                    targetRot = CGFloat(sin(t * 30)) * 5
                } else if working {
                    cell = Anim.work.cell(Int(t / Anim.work.frameTime))
                } else if canSit && extras {
                    cell = perchCell(t)
                } else if let look = cursorLook(t) {
                    cell = look
                } else {
                    cell = idleCell(t)
                }
            }
        }

        // Sitting on a window edge: drop the sprite so the seat line (y=149) lands on the edge.
        let sitting = cell.row == Anim.perch.row
        if sitting { lift -= Anim.perch.seatRise * scale }
        if sitting != wasSitting {
            wasSitting = sitting
            if mode == .perched { landSquash = (t, 0.07) }
        }

        // Landing squash.
        if let ls = landSquash {
            let u = (t - ls.start) / 0.28
            if u >= 1 { landSquash = nil } else {
                let s = ls.amount * CGFloat(sin(u * .pi)) * CGFloat(1 - u * 0.4)
                sy *= 1 - s; sx *= 1 + s * 0.6
            }
        }
        if t < glitchUntil {
            dx += CGFloat.random(in: -4...4) * k
            alpha = Float.random(in: 0.45...1)
            if Int(t * 30) % 3 == 0 { cell = sprites.look(Double(Int.random(in: 0..<16)) * 22.5) }
        }

        let follow: CGFloat = (mode == .held || mode == .airborne) ? 0.25 : 0.35
        rot += (targetRot - rot) * min(1, follow * dt * 60)
        if abs(rot) < 0.05 && targetRot == 0 { rot = 0 }

        // Swing around the grab point instead of the feet: keep that point fixed under the cursor.
        if pivot > 0 {
            let r = rot * .pi / 180
            dx += pivot * sin(r)
            lift += pivot * (1 - cos(r))
        }
        headRise = (Character.current.feetY - CGFloat(sprites.top(cell))) * scale + lift

        CATransaction.begin()
        CATransaction.setDisableActions(true)
        body.show(cell, sprites)
        body.pose(rot: rot, sx: sx, sy: sy, dx: dx, lift: lift, alpha: alpha)
        CATransaction.commit()
    }

    /// Sitting needs something to hang her legs over: a window edge, or the top of a visible Dock.
    private var canSit: Bool {
        guard mode == .perched, let s = seat else { return false }
        if !s.isFloor { return true }
        guard let scr = Desktop.nearestScreen(CGPoint(x: feet.x, y: feet.y + 2)) else { return false }
        return scr.visibleFrame.minY - scr.frame.minY > Anim.perch.seatRise * scale * 0.6
    }

    private func idleCell(_ t: Double) -> Cell {
        let u = t - idleStart
        if u < idleHold { return Anim.idle.cell(0) }
        let i = Int((u - idleHold) / Anim.idle.frameTime) + 1
        if i > 5 { idleStart = t; idleHold = Double.random(in: 1.8...4.2); return Anim.idle.cell(0) }
        return Anim.idle.cell(i)
    }

    /// The seated loop: hold the first frame, then breathe and blink through the rest with the artist's timings.
    private func perchCell(_ t: Double) -> Cell {
        let u = t - idleStart
        if u < idleHold { return Anim.perch.cell(0) }
        let d = Anim.perch.durations
        var v = u - idleHold
        for i in 1..<d.count { v -= d[i]; if v < 0 { return Anim.perch.cell(i) } }
        idleStart = t
        idleHold = Double.random(in: 2.0...4.5)
        return Anim.perch.cell(0)
    }

    private func cursorLook(_ t: Double) -> Cell? {
        if t < lookUntil { return sprites.look(lookDir) }
        let m = pointer()
        let head = CGPoint(x: feet.x, y: feet.y + spriteSize.height * 0.72)
        let d = hypot(m.x - head.x, m.y - head.y)
        guard d < 300 * scale / 0.62 + 120, d > spriteSize.width * 0.35, !body.hits(m, sprites) else { return nil }
        let deg = atan2(Double(m.x - head.x), Double(m.y - head.y)) * 180 / .pi
        return sprites.look(deg)
    }

    // MARK: Naps

    /// She dozes off after three quiet minutes (no keyboard or mouse) and wakes on any input.
    private func checkSleep(_ t: Double) {
        guard sprites.hasExtras else { return }
        userIdle = CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: CGEventType(rawValue: ~0)!)
        let forced = t < forceSleepUntil       // the demo nap wins over work and music
        let calm = mode == .perched && shot == nil && awaiting == nil && (forced || (!working && !music.dancing))
        let drowsy = forced || (Prefs.nap && userIdle > 180)
        if !asleep && calm && drowsy {
            asleep = true
            if bubble.visible && !bubble.sticky { bubble.hide() }
        } else if asleep && (!calm || !drowsy) {
            wake(quiet: !calm)
        }
    }

    private func wake(quiet: Bool) {
        guard asleep else { return }
        asleep = false
        forceSleepUntil = 0
        if quiet || mode != .perched { return }
        play(.jump, cells: [1, 0])
        say(Character.current.wake.randomElement()!, duration: 2.4)
    }

    /// The real dance frames, two frames per beat, with a hop and a pose closing every 16-beat phrase.
    private func dance(_ t: Double) -> (Cell, CGFloat, CGFloat, CGFloat, CGFloat, CGFloat) {
        let b = music.beat()
        let k = scale / 0.62
        let amp = CGFloat(0.55 + 0.45 * b.energy)
        let ph = b.phase
        let i = b.index
        if i != lastBeatIndex {
            lastBeatIndex = i
            if Double.random(in: 0..<1) < 0.45 {
                body.particle(["♪", "♫", "♬", "♩"].randomElement()!, color: [Palette.pink, Palette.periwinkle, Palette.lavender, Palette.cyan].randomElement()!, size: 15 * k)
            }
        }
        let beatPos = Double(i % 2) + ph
        let swing = CGFloat(cos(.pi * beatPos))                         // ±1 on the beat, 0 between
        let bounce = CGFloat(abs(sin(.pi * ph)))                        // up between beats
        let hit = CGFloat(max(0, 1 - ph * 5))                           // squash right on the beat
        var sx: CGFloat = 1 + 0.04 * hit * amp, sy: CGFloat = 1 - 0.06 * hit * amp
        let bar = i % 16
        if sprites.hasExtras && bar < 12 {
            // 8 frames across two beats (≈ the artist's 140 ms frames at 116 BPM).
            let f = Int(beatPos * 4) % 8
            return (Anim.dance.cell(f), swing * 3 * amp, sx, sy, 0, bounce * 3 * k * amp)
        }
        if bar < 14 {
            if ph < 0.16 { return (Anim.jump.cell(1), 0, 1.05, 0.92, 0, 0) }
            if ph < 0.72 {
                let u = (ph - 0.16) / 0.56
                sx = 0.98; sy = 1.03
                return (Anim.jump.cell(2), CGFloat(i % 2 == 0 ? 4 : -4), sx, sy, 0, CGFloat(sin(u * .pi)) * 15 * k * amp)
            }
            return (Anim.jump.cell(3), 0, sx, sy, 0, 0)
        }
        if bar == 15 { return (Anim.review.cell(3), 0, sx, sy, 0, bounce * 4 * k) }
        let cell = i % 2 == 0 ? sprites.look(90) : sprites.look(270)
        return (cell, (i % 2 == 0 ? -6 : 6) * amp, sx, sy, 0, bounce * 4 * k * amp)
    }

    // MARK: Clones

    private func updateClones(_ t: Double, _ dt: CGFloat) {
        guard !clones.isEmpty else { return }
        let speed = 120 * scale / 0.62
        var gone: [String] = []
        for c in clones {
            var cell = Anim.work.cell(Int((t - c.born) / Anim.work.frameTime))
            if let l = c.leaving {
                let u = t - l
                if u < 0.85 { cell = Anim.review.cell(Int(u / 0.14)) }
                else if u < 1.4 { cell = Anim.wave.cell(Int(u / 0.12)) }
                else { gone.append(c.id) }
                c.body.panel.alphaValue = CGFloat(clamp(0.88 * (1.6 - u) / 0.6, 0, 0.88))
            } else if abs(c.target - c.offset) > 1 {
                let dir: CGFloat = c.target > c.offset ? 1 : -1
                c.offset += dir * min(abs(c.target - c.offset), speed * dt)
                cell = (dir > 0 ? Anim.runRight : Anim.runLeft).cell(Int(t / 0.075))
            }
            var x = feet.x + c.offset
            if let s = seat { x = clamp(x, s.left + 20, s.right - 20) }
            CATransaction.begin(); CATransaction.setDisableActions(true)
            c.body.show(cell, sprites)
            c.body.pose(rot: 0, sx: 1, sy: 1, dx: 0, lift: 0, alpha: 1)
            CATransaction.commit()
            c.body.place(feet: CGPoint(x: x, y: feet.y + dockLift))
            if c.body.panel.level != body.panel.level { c.body.panel.level = body.panel.level }
        }
        if !gone.isEmpty {
            for c in clones where gone.contains(c.id) { c.body.panel.orderOut(nil) }
            clones.removeAll { gone.contains($0.id) }
        }
        if t - lastClonePlace > 0.5 {
            lastClonePlace = t
            for c in clones { c.body.panel.order(.above, relativeTo: body.panel.windowNumber) }
        }
    }

    // MARK: Status (for the menu and the control socket)

    func status() -> [String: Any] {
        let b = music.beat()
        let s = music.tracker.snapshot()
        return [
            "mode": mode.rawValue,
            "feet": [Double(feet.x), Double(feet.y)],
            "seatWindow": seat?.win.map { Int($0) } ?? 0,
            "seatOwner": seat?.win.flatMap { id in wins.first { $0.id == id }?.owner } ?? (seat == nil ? "" : "floor"),
            "cell": body.cell.map { [$0.row, $0.col] } ?? [],
            "rotation": Double(rot),
            "asleep": asleep,
            "character": Character.current.id,
            "dockLift": Double(dockLift),
            "chasing": chasing,
            "sitting": body.cell?.row == Anim.perch.row,
            "level": body.panel.level.rawValue,
            "windowNumber": body.panel.windowNumber,
            "frame": [Double(body.panel.frame.minX), Double(body.panel.frame.minY), Double(body.panel.frame.width), Double(body.panel.frame.height)],
            "passThrough": body.panel.ignoresMouseEvents,
            "ledges": ledges.count,
            "dancing": music.dancing,
            "musicActive": music.active,
            "player": music.playerName,
            "beatLive": b.live,
            "bpm": s.bpm,
            "confidence": s.confidence,
            "tap": music.tapNote,
            "working": working,
            "awaiting": awaiting ?? "",
            "clones": cloneCount,
            "bubble": bubble.visible ? bubble.text : "",
        ]
    }
}

