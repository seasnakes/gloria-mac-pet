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
    // A 3D pet draws into a Metal layer inside the sprite layer, so turning, squashing and swinging apply to it too.
    private(set) var model: Model3D?
    private var metal: CAMetalLayer?
    private(set) var pose = Pose3D()

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
        if let m = metal {
            let k = NSScreen.main?.backingScaleFactor ?? 2
            m.frame = sprite.bounds
            m.contentsScale = k
            m.drawableSize = CGSize(width: (spriteSize.width * k).rounded(), height: (spriteSize.height * k).rounded())
        }
        CATransaction.commit()
    }

    /// Give this body a 3D model to draw instead of sheet cells.
    func attach(_ m: Model3D) {
        model = m
        let l = CAMetalLayer()
        l.device = Model3D.device
        l.pixelFormat = .bgra8Unorm_srgb
        l.isOpaque = false
        l.framebufferOnly = true
        l.actions = ["bounds": NSNull(), "position": NSNull(), "contents": NSNull()]
        sprite.addSublayer(l)
        metal = l
        resize(scale)
    }

    /// 3D: ease toward the pose for this cell and draw it — at most 30 times a second (the easing keeps it smooth).
    private var lastDraw: Double = 0
    private var pending: CGFloat = 0
    func show(_ target: Pose3D, cell c: Cell, dt: CGFloat) {
        guard let model, let metal else { return }
        shown = c
        pending += dt
        let t = now()
        guard t - lastDraw >= 1.0 / 31 else { return }
        pose.approach(target, 1 - exp(-Double(pending) / 0.07))
        pending = 0
        lastDraw = t
        model.draw(pose, into: metal)
    }

    /// For the `scene` self-check: Metal layers don't render into a bitmap, so show a still of the current pose meanwhile.
    func beginCapture() {
        guard let model else { return }
        sprite.contents = model.snapshot(pose, size: CGSize(width: spriteSize.width * 2, height: spriteSize.height * 2))
        metal?.isHidden = true
    }
    func endCapture() {
        guard model != nil else { return }
        sprite.contents = nil
        metal?.isHidden = false
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

    /// Is the screen point on one of her opaque pixels? `as` tests another frame in the same place instead of the one shown.
    func hits(_ screen: CGPoint, _ sprites: Sprites, as other: Cell? = nil) -> Bool {
        let f = panel.frame
        guard f.contains(screen), let root = view.layer else { return false }
        let lp = sprite.convert(CGPoint(x: screen.x - f.minX, y: screen.y - f.minY), from: root)
        if let model { return model.hits(pose, u: lp.x / spriteSize.width, v: lp.y / spriteSize.height) }
        guard let c = other ?? shown else { return false }
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
    enum Mode: String { case perched, walking, held, airborne, hopping, peeking }

    let ch: Character
    var sprites: Sprites { ch.sprites }
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
    /// Following the pointer with her eyes, with a short dwell so she doesn't flicker at the edge.
    private var looking = false
    private var lookSwitched: Double = 0
    /// The exact direction she's looking (the sprite sheet rounds it to 16 frames; a 3D model uses it as is).
    private(set) var lookDeg: Double = 90
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

    // Peeking: tucked in behind the left (side −1) or right (+1) edge of a screen, only her head out, leaning in.
    // p is how far out she is (0 hidden, 1 head and a shoulder out).
    struct Peek {
        var side: CGFloat, edge: CGFloat, headY: CGFloat
        var p: CGFloat = 0.95
        var duckUntil: Double = 0
        var nearSince: Double?
        var found = false
        var fidget: (until: Double, p: CGFloat)?
        var nextFidget = now() + 5
    }
    private(set) var peek: Peek?
    private let peekTilt: CGFloat = 28
    private var lastPeekLine: Double = -60

    // Dock, cursor chasing, pokes
    private var timer: Timer?
    private var observers: [NSObjectProtocol] = []
    private(set) var dockLift: CGFloat = 0
    private var dock: (tile: CGFloat, large: CGFloat, on: Bool) = (64, 128, false)
    private var lastDockRead: Double = -10
    private var bounceStart: Double = 0, bounceUntil: Double = 0
    private(set) var chasing = false
    private lazy var bodyHalf = CGFloat(sprites.halfWidth(ch.cell(.idle, 0)))
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
    var working = false { didSet { if working && !oldValue { workSince = now() } } }
    private var workSince: Double = 0
    var workCaption = ""
    var awaiting: String?
    var awaitingTag = "CLAUDE"
    private var clones: [Clone] = []
    /// Subagent clones on screen (for the recorder).
    var cloneBodies: [Body] { clones.map(\.body) }
    private var lastClonePlace: Double = 0
    // Computer use: the pose she holds while an agent clicks / types / snaps through her.
    private var acting: (anim: Anim, start: Double, until: Double, flip: Bool)?
    private var lastActHop: Double = 0
    var isActing: Bool { acting.map { now() < $0.until } ?? false }

    var scale: CGFloat { body.scale }
    var spriteSize: CGSize { body.spriteSize }
    var gravity: CGFloat { 2600 }

    /// The other pets on screen (for meeting up and passing notes between agents).
    var peers: () -> [Pet] = { [] }
    var others: [Pet] { peers().filter { $0 !== self } }
    /// Which agent she stands in for ("Claude Code", "Codex"), shown in her menu.
    var roles: [String] = []

    init(ch: Character, music: MusicWatch) {
        self.ch = ch
        self.music = music
        body = Body(scale: CGFloat(Prefs.scale) * ch.scaleMul)
        body.view.pet = self
        if let m = ch.model { body.attach(m) }
        display(body, ch.cell(.idle, 0), 1)
        body.panel.ignoresMouseEvents = true
    }

    // MARK: Lifecycle

    func start(at: CGPoint? = nil, slot: Int = 0) {
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
                self?.say(self?.ch.lines.greeting ?? "", tag: self?.ch.tag, duration: 3)
            }
        } else {
            summon(entrance: true, slot: slot)
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
            guard let s = self else { return }
            s.rescan(force: true)
            if let pk = s.peek {
                if s.edgeFree(side: pk.side, edge: pk.edge, headY: pk.headY) == nil { s.summon(entrance: false) }
            } else if Desktop.screen(at: CGPoint(x: s.feet.x, y: s.feet.y + 4)) == nil { s.summon(entrance: false) }
        }
    }

    func setScale(_ s: CGFloat) {
        Prefs.scale = Double(s)
        let k = s * ch.scaleMul
        body.resize(k)
        for c in clones { c.body.resize(k * 0.62) }
        rescan(force: true)
        body.place(feet: feet)
    }

    /// Drop her onto the front window (or the floor) from above — on the screen under the pointer, or the one given.
    func summon(entrance: Bool, slot: Int = 0, on target: NSScreen? = nil) {
        peek = nil
        rescan(force: true)
        let cursor = NSEvent.mouseLocation
        let scr = target ?? Desktop.nearestScreen(cursor) ?? NSScreen.main
        // Only aim at the pointer when it's on that screen.
        var mouse = cursor
        if let f = scr?.frame, !f.contains(cursor) { mouse = CGPoint(x: f.midX, y: f.midY) }
        let onScreen = ledges.filter { l in
            guard let v = scr?.visibleFrame else { return true }
            return l.y >= v.minY && l.y <= v.maxY && l.right > v.minX && l.left < v.maxX
        }
        // The frontmost window top that's wide enough for her whole body, else the floor.
        let roomy = onScreen.filter { !$0.isFloor && $0.right - $0.left >= spriteSize.width * 0.85 }
        let target = roomy.first ?? onScreen.first { $0.isFloor } ?? ledges.last
        guard let l = target else { return }
        let inset = min(spriteSize.width * 0.32, (l.right - l.left) / 2)
        let spread = [0, 1, -1, 2, -2][min(slot, 4)] * spriteSize.width * 0.95
        let base = mouse.x > l.left + inset && mouse.x < l.right - inset && !l.isFloor
            ? mouse.x : clamp(l.right - max(90, (l.right - l.left) * 0.22), l.left + inset, l.right - inset)
        let x = clamp(base - spread, l.left + inset, l.right - inset)
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
                self?.say(self?.ch.lines.greeting ?? "", tag: self?.ch.tag)
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
        peek = nil
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

    var headTop: CGPoint {
        if let pk = peek { let g = headGeo; return CGPoint(x: pk.edge - pk.side * g.r * 0.7, y: pk.headY + g.r) }
        return CGPoint(x: feet.x, y: feet.y + dockLift + (headRise > 0 ? headRise : spriteSize.height * 0.94))
    }
    var isAsleep: Bool { asleep }

    /// Hold her up in the air for a few seconds as if dragged (for checks and the demo menu).
    func simulateHold(_ seconds: Double) {
        synthHold = (now(), now() + seconds, CGPoint(x: feet.x, y: feet.y + spriteSize.height * 1.2))
        grab = CGVector(dx: 0, dy: -spriteSize.height * 1.2)
        startHold()
    }

    private func startHold() {
        peek = nil
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
        guard ch.hasExtras else { return CGPoint(x: m.x + grab.dx, y: m.y + grab.dy) }
        let hang = CGVector(dx: 0, dy: -ch.hang * scale)
        let u = CGFloat(clamp((t - heldSince) / 0.16, 0, 1))
        return CGPoint(x: m.x + lerp(grab.dx, hang.dx, u), y: m.y + lerp(grab.dy, hang.dy, u))
    }

    // MARK: Clones (subagents)

    func spawnClone(_ id: String) {
        guard clones.count < 4, !clones.contains(where: { $0.id == id }) else { return }
        let side: CGFloat = clones.count % 2 == 0 ? 1 : -1
        let dist = spriteSize.width * (0.62 + 0.42 * CGFloat(clones.count / 2))
        let c = Clone(id: id, scale: scale * 0.62, target: side * dist)
        if let m = ch.model { c.body.attach(m) }
        display(c.body, ch.cell(.idle, 0), 1)
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
    /// Windows she occupies in the stack: herself, her clones and her bubble.
    var stackSize: Int { 1 + clones.count + (bubble.visible ? 1 : 0) }

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
            release(at: NSEvent.mouseLocation, velocity: v)
            return
        }
        if e.clickCount >= 2 && peek != nil { leavePeek(); return }
        if e.clickCount >= 2 {
            play(.jump)
            sparkle("✦", color: Palette.periwinkle, count: 2)
        } else {
            pat()
        }
    }

    /// Let go of her at the pointer: she flies off with the throw, or, let go against a screen's side edge, tucks in
    /// behind it and peeks out.
    func release(at m: CGPoint, velocity: CGVector) {
        var v = velocity
        let sp = hypot(v.dx, v.dy)
        if sp > 3200 { v.dx *= 3200 / sp; v.dy *= 3200 / sp }
        if Prefs.edgePeek, sp < 1800, let scr = Desktop.screen(at: m) {
            let f = scr.frame, zone = max(34, bodyHalf * scale * 0.7)
            let side: CGFloat? = m.x - f.minX < zone ? -1 : (f.maxX - m.x < zone ? 1 : nil)
            if let side, let s = edgeFree(side: side, edge: side > 0 ? f.maxX : f.minX, headY: feet.y + headGeo.hc) {
                startPeek(side: side, screen: s, headY: feet.y + headGeo.hc)
                return
            }
        }
        mode = .airborne
        vel = v
        if sp > 1400 { say(["哇啊啊——", "飞起来了！", "轻点轻点！"].randomElement()!, duration: 1.6) }
    }

    /// For checks: as if she'd been carried to `m` and let go there.
    func drop(at m: CGPoint, velocity v: CGVector) {
        startHold()
        feet = heldFeet(m, heldSince + 1)
        release(at: m, velocity: v)
        if mode == .held { mode = .airborne }
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
            say(ch.lines.angry.randomElement()!, duration: 2.6)
            return
        }
        if awaiting != nil {
            say("要你回到 \(awaitingTag == "CODEX" ? "Codex" : "Claude Code") 里批准哦～我只负责提醒。", tag: awaitingTag, duration: 4)
            return
        }
        play(.wave, loops: 2)
        sparkle()
        if Int.random(in: 0..<3) == 0 || !bubble.visible {
            say(ch.lines.pats.randomElement()!, duration: 2.4)
        }
    }

    // MARK: Tick

    private func tick() {
        let t = now()
        let dt = CGFloat(clamp(t - lastTick, 0, 0.05))
        lastTick = t
        if peek != nil && mode != .peeking { peek = nil }
        if t - lastScan > (mode == .perched || mode == .walking ? 1.0 / 15 : 1.0 / 30) { rescan(force: false); lastScan = t }

        if mode == .walking && (awaiting != nil || music.dancing) { mode = .perched }
        if mode == .walking && isActing && abs(walkTarget - feet.x) < 2 { mode = .perched }
        if let h = synthHold {
            let m = CGPoint(x: h.center.x + CGFloat(sin((t - h.start) * 2.6)) * 160, y: h.center.y)
            samples.append((t, m)); samples.removeAll { t - $0.0 > 0.1 }
            feet = heldFeet(m, t)
            if t > h.until { synthHold = nil; mode = .airborne; vel = CGVector(dx: 600, dy: 500) }
        }
        if t - lastIdleCheck > 1 { lastIdleCheck = t; checkSleep(t); keepApart(t) }
        if let m = meet, mode == .perched || t > m.deadline { meet = nil; m.fire() }
        switch mode {
        case .held: break
        case .airborne: fly(dt)
        case .hopping: hopStep(t)
        case .walking: walkStep(dt)
        case .perched: break
        case .peeking: peekStep(t, dt)
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

        if mode == .perched && shot == nil && !music.dancing && awaiting == nil && !isActing { think(t) }
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
        // Walls and ceiling are the edges of the screen she's in — except where another screen carries on past them,
        // so she can be thrown from one display onto the next.
        let here = Desktop.screen(at: feet) ?? Desktop.nearestScreen(feet)
        if let f = here?.frame {
            let mid = next.y + spriteSize.height * 0.5
            if next.x < f.minX + half, Desktop.screen(at: CGPoint(x: f.minX - 2, y: mid)) == nil { next.x = f.minX + half; vel.dx = abs(vel.dx) * 0.45 }
            if next.x > f.maxX - half, Desktop.screen(at: CGPoint(x: f.maxX + 2, y: mid)) == nil { next.x = f.maxX - half; vel.dx = -abs(vel.dx) * 0.45 }
            if next.y + spriteSize.height > f.maxY, Desktop.screen(at: CGPoint(x: next.x, y: f.maxY + 2)) == nil {
                next.y = f.maxY - spriteSize.height; vel.dy = -abs(vel.dy) * 0.3
            }
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
            // Safety net: never fall out of the bottom of the screen she's in. (Not the highest floor at this x: with a
            // display stacked above this one, that's the other display's bottom edge, and she'd be snapped up there.)
            if let scr = here, next.y < scr.visibleFrame.minY - 2 {
                let floor = Ledge(win: nil, left: scr.visibleFrame.minX, right: scr.visibleFrame.maxX, y: scr.visibleFrame.minY)
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
            say(ch.lines.ouch.randomElement()!, duration: 2)
        } else if impact > 300 {
            shot = (.jump, now(), 1, [3, 4])
        }
        nextIdea = now() + Double.random(in: 4...9)
        landedAt = now()
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
        if Desktop.staged != nil { body.panel.level = .floating; return }
        let me = CGWindowID(body.panel.windowNumber)
        guard let hi = order.firstIndex(of: host) else { return }
        let mi = order.firstIndex(of: me)
        if body.panel.level != .normal { body.panel.level = .normal }
        // Other pets on the same window share the slots just above it.
        let room = peers().filter { $0.seat?.win == host }.reduce(0) { $0 + $1.stackSize }
        if mi == nil || mi! > hi || hi - mi! > max(room, stackSize) {
            body.panel.order(.above, relativeTo: Int(host))
            for c in clones { c.body.panel.level = .normal; c.body.panel.order(.above, relativeTo: body.panel.windowNumber) }
        }
    }

    private func updateLevel() {
        if seat?.win == nil || Desktop.staged != nil {
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
        guard Prefs.chase, let s = seat, mode == .perched || mode == .walking, shot == nil, !working, awaiting == nil, !isActing,
              !music.dancing, !asleep, t > grumpyUntil, t > bounceUntil else {
            if chasing { chasing = false; if mode == .walking { mode = .perched } }
            return
        }
        let inBand = m.y >= feet.y + dockLift - 8 && m.y <= feet.y + dockLift + spriteSize.height * 1.1 && m.x >= s.left && m.x <= s.right
        let dx = m.x - feet.x
        // She stops beside the cursor, never under it: her opaque pixels would swallow the clicks.
        let standOff = bodyHalf * scale + 14 * scale / 0.62
        if !chasing {
            if inBand && abs(dx) > standOff + 40 && abs(dx) < 460 && mouseSpeed > 60 && t - lastPounce > 2.5 && !body.hits(m, sprites)
                && !others.contains(where: { $0.chasing }) {
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
        let side: CGFloat = dx >= 0 ? 1 : -1          // cursor to her right → she stops on its left
        let target = clamp(m.x - side * standOff, s.left + margin, s.right - margin)
        walkTarget = target
        if mode != .walking && abs(target - feet.x) > 2 { mode = .walking }
        if abs(target - feet.x) < 8 * scale / 0.62 || (mode == .perched && abs(dx) <= standOff + 4) {
            chasing = false
            mode = .perched
            lastPounce = t
            lookDir = dx >= 0 ? 90 : 270
            lookUntil = t + 1.4
            play(.jump)
            sparkle("✦", color: Palette.periwinkle, count: 2)
            if Int.random(in: 0..<3) == 0 { say(["抓到了！", "嘿！", "跑不掉的～"].randomElement()!, duration: 1.4) }
        }
    }

    func debugDockBounce() { bounceUntil = 0; dockBounce() }

    // MARK: Computer use

    /// Act out one computer-use step: get over to the window being worked on, strike the pose, show where it lands.
    func act(_ a: CUAction, window w: Win?, at p: CGPoint?, tag: String?) {
        let t = now()
        wake(quiet: true)
        if chasing { chasing = false; if mode == .walking { mode = .perched } }
        let target = p ?? w.map { CGPoint(x: $0.rect.midX, y: $0.rect.maxY - 80) }
        // Hop onto the window she's working in (not more than every few seconds), a little to the side of the spot.
        if let w, mode == .perched || mode == .walking, seat?.win != w.id, t - lastActHop > 2.5, awaiting == nil {
            rescan(force: true)
            if let l = ledges.first(where: { $0.win == w.id && $0.right - $0.left >= spriteSize.width * 0.85 }) {
                lastActHop = t
                let margin = spriteSize.width * 0.3
                var x = target?.x ?? (l.left + l.right) / 2
                x += (x - l.left > (l.right - l.left) / 2 ? -1 : 1) * spriteSize.width * 0.7
                hopTo(l, x: clamp(x, l.left + margin, l.right - margin))
            } else if let s = seat, s.isFloor, let tx = target?.x, abs(tx - feet.x) > spriteSize.width * 1.3 {
                // No room on top of that window (it's maximized): walk along the floor to just beside the spot.
                lastActHop = t
                let margin = spriteSize.width * 0.3
                walkTarget = clamp(tx + (tx > feet.x ? -1 : 1) * spriteSize.width * 0.75, s.left + margin, s.right - margin)
                mode = .walking
            }
        }
        let anim = ch.pose(for: a.anim)
        let spec = ch.spec(anim)
        let side = (hop?.to.x ?? feet.x)
        let flip = target.map { tp in (spec.facing == "left" && tp.x > side + 24) || (spec.facing == "right" && tp.x < side - 24) } ?? false
        let hold = max(min(ch.length(anim), 3.2), a.kind == .type ? 2.4 : 1.3)
        if let cur = acting, cur.anim == anim, t < cur.until { acting = (anim, cur.start, t + hold, flip) } else { acting = (anim, t, t + hold, flip) }
        if let tp = target, spec.facing == nil { face(tp.x, for: hold) }
        let k = scale / 0.62
        switch a.kind {
        case .tap, .right: if let p { Effects.ripple(at: p) }
        case .double: if let p { Effects.ripple(at: p, double: true) }
        case .key: body.particle(a.keys ?? "⌨︎", color: Palette.periwinkle, size: 15 * k)
        case .drag:
            if let p, let w, let p2 = a.point2.map({ ComputerUse.screenPoint($0, in: w) }) { Effects.trail(from: p, to: p2) }
            else if let p { Effects.ripple(at: p, color: Palette.periwinkle) }
        case .snap, .look:
            if let w { Effects.flash(w.rect) }
            body.particle("✦", color: Palette.butter, size: 16 * k)
        case .type:
            for i in 0..<3 {
                DispatchQueue.main.asyncAfter(deadline: .now() + Double(i) * 0.22) { [weak self] in
                    self?.body.particle(["⌨︎", "a", "s", "d", "·", "k"].randomElement()!, color: Palette.periwinkle, size: 13 * k)
                }
            }
        case .scroll: body.particle("⇣", color: Palette.cyan, size: 16 * k)
        case .open: body.particle("✧", color: Palette.lavender, size: 15 * k)
        }
        let whereText = w.map { "\($0.owner)：" } ?? ""
        say(whereText + a.label, tag: nil, kind: .caption, duration: max(1.6, hold))
    }

    // MARK: Together (several pets; agents passing notes to each other)

    private var meet: (to: Pet, deadline: Double, fire: () -> Void)?
    private(set) var landedAt: Double = 0

    /// Same window top, or the floor of the same screen.
    func sameLedge(_ o: Pet) -> Bool {
        guard let a = seat, let b = o.seat else { return false }
        if let wa = a.win, let wb = b.win { return wa == wb }
        return a.isFloor && b.isFloor && abs(a.y - b.y) < 2 && Desktop.nearestScreen(feet) == Desktop.nearestScreen(o.feet)
    }

    func face(_ x: CGFloat, for seconds: Double = 2.2) {
        lookDir = x > feet.x ? 90 : 270
        lookUntil = now() + seconds
    }

    /// Where a note leaves her hands / lands on her chest.
    var hands: CGPoint {
        if let pk = peek { let g = headGeo; return CGPoint(x: pk.edge - pk.side * g.r * 0.6, y: pk.headY - g.r * 0.4) }
        return CGPoint(x: feet.x, y: feet.y + dockLift + spriteSize.height * 0.5)
    }

    /// Walk over to another pet when they share a ledge, then throw her a note.
    func passNote(to o: Pet, say text: String?, tag: String?, note: String?, noteTag: String?) {
        let fire = { [weak self, weak o] in
            guard let self, let o else { return }
            self.face(o.feet.x)
            self.play(.wave)
            if let text { self.say(text, tag: tag, duration: 3.4) }
            Courier.fly(from: self, to: o) { [weak o, weak self] in
                guard let o else { return }
                o.gotNote(from: self, note: note, tag: noteTag)
            }
        }
        let w = spriteSize.width
        if mode == .perched || mode == .walking, sameLedge(o), abs(o.feet.x - feet.x) > w * 1.9, awaiting == nil, let s = seat {
            let side: CGFloat = o.feet.x > feet.x ? -1 : 1
            let margin = w * 0.3
            walkTarget = clamp(o.feet.x + side * w * 1.2, s.left + margin, s.right - margin)
            chasing = false
            mode = .walking
            meet = (o, now() + 2.6, fire)
        } else {
            fire()
        }
    }

    func gotNote(from o: Pet?, note: String?, tag: String?) {
        wake(quiet: true)
        if let o { face(o.feet.x) }
        play(.jump)
        sparkle("✉︎", color: Palette.periwinkle, count: 1)
        if let note { say(note, tag: tag, duration: 3.8) }
    }

    /// Two pets standing on top of each other: the one who arrived last steps aside.
    private func keepApart(_ t: Double) {
        guard mode == .perched, shot == nil, meet == nil, !chasing, let s = seat else { return }
        let w = spriteSize.width
        guard let o = others.first(where: { ($0.mode == .perched || $0.mode == .walking) && sameLedge($0)
                                            && abs($0.feet.x - feet.x) < w * 0.55 && $0.landedAt <= landedAt }) else { return }
        let margin = w * 0.3
        let prefer: CGFloat = feet.x >= o.feet.x ? 1 : -1
        for side in [prefer, -prefer] {
            let x = o.feet.x + side * w * 0.95
            if x >= s.left + margin && x <= s.right - margin { walkTarget = x; mode = .walking; return }
        }
    }

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
        } else if r < 0.8, let o = others.first(where: { sameLedge($0) && abs($0.feet.x - feet.x) < 600 && $0.mode == .perched }) {
            // Say hi to the pet next to her.
            face(o.feet.x)
            play(.wave)
            if Bool.random() { say(ch.lines.hello.randomElement()!, duration: 2) }
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak o, weak self] in
                guard let o, let self, o.mode == .perched, !o.working, o.awaiting == nil else { return }
                o.face(self.feet.x)
                o.play(.wave)
            }
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
        var cell = ch.cell(.idle, 0)
        var targetRot: CGFloat = 0
        var sx: CGFloat = 1, sy: CGFloat = 1, dx: CGFloat = 0, lift: CGFloat = 0
        var alpha: Float = 1
        var pivot: CGFloat = 0                      // rotate around a point this far above her feet (0 = the feet)
        let k = scale / 0.62
        let extras = ch.hasExtras
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
                cell = ch.cell(.dangle, ch.index(.dangle, at: t - heldSince))
                targetRot = clamp(-heldVX * 0.02, -30, 30) + CGFloat(sin(t * 4.2)) * 4
                pivot = ch.hang * scale
            } else {
                cell = abs(heldVX) > 160 ? ch.cell(heldVX > 0 ? .runRight : .runLeft, Int(t / 0.06)) : ch.cell(.waiting, Int(t / 0.2) % 3)
                targetRot = clamp(-heldVX * 0.018, -24, 24) + CGFloat(sin(t * 7)) * 3
            }
        case .airborne:
            cell = extras ? ch.cell(.dangle, Int(t / 0.09)) : ch.cell(.jump, 2)
            targetRot = clamp(-vel.dx * 0.014, -32, 32) + CGFloat(sin(t * 16)) * min(8, abs(vel.dx) / 120)
        case .hopping:
            if let h = hop {
                let u = (t - h.start - 0.12) / h.dur
                cell = ch.cell(.jump, u < 0 ? 1 : (u < 0.8 ? 2 : 3))
                if u < 0 { sy = 0.92; sx = 1.05 }
                targetRot = h.to.x > h.from.x ? -6 : 6
            }
        case .walking:
            cell = ch.cell(walkTarget > feet.x ? .runRight : .runLeft, Int(t / ch.frameTime(.runRight)))
            lift = CGFloat(abs(sin(t * 14))) * 2 * k
        case .peeking:
            // Leaning in from behind the edge; a slow sway so she looks alive.
            targetRot = (peek?.side ?? 1) * peekTilt + CGFloat(sin(t * 1.6)) * 2.5
            if let (c, l) = shotFrame(t, k) { cell = c; lift = l * 0.5 }
            else if awaiting != nil { cell = ch.cell(.waiting, Int(t / ch.frameTime(.waiting))) }
            else if let a = acting, t < a.until { cell = ch.cell(a.anim, ch.index(a.anim, at: t - a.start)) }
            else if working { cell = ch.cell(.work, ch.index(.work, at: t - workSince)) }
            else if let look = peekLook() { cell = look }
            else { cell = idleCell(t) }
        case .perched:
            if let (c, l) = shotFrame(t, k) { cell = c; lift = l }
            if shot == nil {
                if let _ = awaiting {
                    cell = ch.cell(.waiting, Int(t / ch.frameTime(.waiting)))
                    // A little attention bounce every two seconds.
                    let u = t.truncatingRemainder(dividingBy: 2.2)
                    if u < 0.36 { lift = CGFloat(sin(u / 0.36 * .pi)) * 9 * k }
                } else if let a = acting, t < a.until {
                    // Acting out computer use: clicking, typing, snapping, dragging, scrolling.
                    cell = ch.cell(a.anim, ch.index(a.anim, at: t - a.start))
                    if a.flip { sx = -1 }
                } else if t < bounceUntil {
                    // Hopping along with a bouncing Dock icon.
                    let u = (t - bounceStart) / 0.55
                    let ph = u - floor(u)
                    cell = ch.cell(.jump, ph < 0.12 ? 1 : (ph < 0.8 ? 2 : 3))
                    lift = CGFloat(max(0, sin(ph * .pi))) * 22 * k
                } else if asleep && extras {
                    cell = ch.cell(.sleep, ch.index(.sleep, at: t, speed: 1.3))
                    if t - lastZ > 1.5 { lastZ = t; body.particle("z", color: Palette.periwinkle, size: CGFloat.random(in: 12...17) * k) }
                } else if dancing {
                    (cell, targetRot, sx, sy, dx, lift) = dance(t)
                } else if t < jostleUntil {
                    cell = canSit ? ch.cell(.perch, 0) : ch.cell(.waiting, 0)
                    targetRot = CGFloat(sin(t * 30)) * 5
                } else if working {
                    cell = ch.cell(.work, ch.index(.work, at: t - workSince))
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
        let sitting = ch.anim(of: cell) == .perch
        if sitting { lift -= ch.seatRise * scale }
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
        headRise = (ch.feetY - CGFloat(sprites.top(cell))) * scale + lift

        CATransaction.begin()
        CATransaction.setDisableActions(true)
        display(body, cell, dt)
        body.pose(rot: rot, sx: sx, sy: sy, dx: dx, lift: lift, alpha: alpha)
        CATransaction.commit()
    }

    /// The frame of the reaction playing now (nil once it's over), and how high its little hop goes.
    private func shotFrame(_ t: Double, _ k: CGFloat) -> (Cell, CGFloat)? {
        guard let s = shot else { return nil }
        let ft = ch.frameTime(s.anim)
        let seq = s.cells ?? Array(0..<ch.count(s.anim))
        let i = Int((t - s.start) / ft)
        if i >= seq.count * s.loops { shot = nil; idleStart = t; return nil }
        var lift: CGFloat = 0
        if s.anim == .jump && s.cells == nil, i < seq.count {
            // A little hop in place for the jump reaction.
            let u = (t - s.start) / (ft * Double(seq.count))
            lift = CGFloat(max(0, sin(u * .pi))) * 16 * k
        }
        return (ch.cell(s.anim, seq[i % seq.count]), lift)
    }

    /// Put a frame on a body: a sheet cell, or for a 3D pet the pose that cell stands for.
    private func display(_ b: Body, _ cell: Cell, _ dt: CGFloat) {
        if ch.model != nil { b.show(ch.pose3D(for: cell, look: lookDeg), cell: cell, dt: dt) } else { b.show(cell, sprites) }
    }

    /// Sitting needs a window edge to hang her legs over. On the Dock or the bottom of the screen she stands.
    private var canSit: Bool {
        guard mode == .perched, let s = seat else { return false }
        return !s.isFloor
    }

    private func idleCell(_ t: Double) -> Cell {
        if ch.spec(.idle).loop { return ch.cell(.idle, ch.index(.idle, at: t)) }
        let u = t - idleStart
        if u < idleHold { return ch.cell(.idle, 0) }
        let i = Int((u - idleHold) / ch.frameTime(.idle)) + 1
        if i >= ch.count(.idle) { idleStart = t; idleHold = Double.random(in: 1.8...4.2); return ch.cell(.idle, 0) }
        return ch.cell(.idle, i)
    }

    /// The seated loop: hold the first frame, then breathe and blink through the rest with the artist's timings.
    private func perchCell(_ t: Double) -> Cell {
        let u = t - idleStart
        if u < idleHold { return ch.cell(.perch, 0) }
        let d = ch.durations(.perch)
        var v = u - idleHold
        for i in 1..<d.count { v -= d[i]; if v < 0 { return ch.cell(.perch, i) } }
        idleStart = t
        idleHold = Double.random(in: 2.0...4.5)
        return ch.cell(.perch, 0)
    }

    private func cursorLook(_ t: Double) -> Cell? {
        if t < lookUntil { lookDeg = lookDir; return sprites.look(lookDir) }
        let m = pointer()
        let head = CGPoint(x: feet.x, y: feet.y + spriteSize.height * 0.72)
        let d = hypot(m.x - head.x, m.y - head.y)
        // Test against her resting silhouette, not the frame on screen: a look frame can swing a hand onto the pointer,
        // which would send her back to resting, which uncovers the pointer again — she'd twitch between the two.
        let want = d < 300 * scale / 0.62 + 120 && d > spriteSize.width * 0.35 && !body.hits(m, sprites, as: ch.model == nil ? ch.cell(.idle, 0) : nil)
        if want != looking && t - lookSwitched > 0.35 { looking = want; lookSwitched = t }
        guard looking else { return nil }
        lookDeg = atan2(Double(m.x - head.x), Double(m.y - head.y)) * 180 / .pi
        return sprites.look(lookDeg)
    }

    // MARK: Naps

    /// She dozes off after three quiet minutes (no keyboard or mouse) and wakes on any input.
    private func checkSleep(_ t: Double) {
        guard ch.hasExtras else { return }
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
        say(ch.lines.wake.randomElement()!, duration: 2.4)
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
        if ch.hasExtras && bar < 12 {
            let spec = ch.spec(.dance)
            if spec.sub > 1 {
                // One key pose per beat, held for the first half of the beat, then in-betweens into the next pose.
                let keys = max(1, spec.count / spec.sub)
                let key = ((i / 16) * 12 + bar) % keys
                let s = ph < 0.55 ? 0 : min(spec.sub - 1, 1 + Int((ph - 0.55) / 0.45 * Double(spec.sub - 1)))
                return (ch.cell(.dance, key * spec.sub + s), swing * 3 * amp, sx, sy, 0, bounce * 3 * k * amp)
            }
            // Sampled dance clips: 8 frames across two beats.
            let f = Int(beatPos * 4) % 8
            return (ch.cell(.dance, f), swing * 3 * amp, sx, sy, 0, bounce * 3 * k * amp)
        }
        if bar < 14 {
            if ph < 0.16 { return (ch.cell(.jump, 1), 0, 1.05, 0.92, 0, 0) }
            if ph < 0.72 {
                let u = (ph - 0.16) / 0.56
                sx = 0.98; sy = 1.03
                return (ch.cell(.jump, 2), CGFloat(i % 2 == 0 ? 4 : -4), sx, sy, 0, CGFloat(sin(u * .pi)) * 15 * k * amp)
            }
            return (ch.cell(.jump, 3), 0, sx, sy, 0, 0)
        }
        if bar == 15 { return (ch.cell(.review, 3), 0, sx, sy, 0, bounce * 4 * k) }
        let cell = i % 2 == 0 ? sprites.look(90) : sprites.look(270)
        return (cell, (i % 2 == 0 ? -6 : 6) * amp, sx, sy, 0, bounce * 4 * k * amp)
    }

    // MARK: Clones

    private func updateClones(_ t: Double, _ dt: CGFloat) {
        guard !clones.isEmpty else { return }
        let speed = 120 * scale / 0.62
        var gone: [String] = []
        for c in clones {
            var cell = ch.cell(.work, ch.index(.work, at: t - c.born))
            if let l = c.leaving {
                let u = t - l
                if u < 0.85 { cell = ch.cell(.review, Int(u / 0.14)) }
                else if u < 1.4 { cell = ch.cell(.wave, Int(u / 0.12)) }
                else { gone.append(c.id) }
                c.body.panel.alphaValue = CGFloat(clamp(0.88 * (1.6 - u) / 0.6, 0, 0.88))
            } else if abs(c.target - c.offset) > 1 {
                let dir: CGFloat = c.target > c.offset ? 1 : -1
                c.offset += dir * min(abs(c.target - c.offset), speed * dt)
                cell = ch.cell(dir > 0 ? .runRight : .runLeft, Int(t / 0.075))
            }
            var x = feet.x + c.offset
            if let s = seat { x = clamp(x, s.left + 20, s.right - 20) }
            CATransaction.begin(); CATransaction.setDisableActions(true)
            display(c.body, cell, dt)
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

    // MARK: Peeking from a screen edge

    /// Her face relative to her feet, upright (dx sideways, hc up), and its radius, in points: from "face" in her JSON,
    /// else guessed from the resting frame (the head is the part between the grab point and the top of the drawing).
    private var headGeo: (dx: CGFloat, hc: CGFloat, r: CGFloat) {
        if let f = ch.face { return ((f.x - Sprites.footX) * scale, (ch.feetY - f.y) * scale, f.r * scale) }
        var rise = (ch.feetY - CGFloat(sprites.top(ch.cell(.idle, 0)))) * scale
        if rise < spriteSize.height * 0.3 { rise = spriteSize.height * 0.8 }
        let neck = clamp(ch.hang * scale, rise * 0.35, rise * 0.8)
        let r = max((rise - neck) / 2, spriteSize.width * 0.12)
        return (0, rise - r, r)
    }

    /// Her face's offset from her feet with the sprite turned `deg` degrees (counter-clockwise positive).
    private func faceOffset(_ deg: CGFloat) -> CGVector {
        let g = headGeo, r = deg * .pi / 180
        return CGVector(dx: g.dx * cos(r) - g.hc * sin(r), dy: g.dx * sin(r) + g.hc * cos(r))
    }

    /// Where her face is right now (follows the tilt as it eases in).
    private var peekHead: CGPoint {
        let o = faceOffset(rot)
        return CGPoint(x: feet.x + o.dx, y: feet.y + o.dy)
    }

    /// The screen she's on (for the menu): the one whose edge she peeks from, else the one under her feet.
    var screen: NSScreen? {
        if let pk = peek { return Desktop.nearestScreen(CGPoint(x: pk.edge - pk.side * 4, y: pk.headY)) }
        return Desktop.nearestScreen(CGPoint(x: feet.x, y: feet.y + 4))
    }

    /// The screen whose left/right edge this is, if nothing carries on past it where her body would hide.
    func edgeFree(side: CGFloat, edge: CGFloat, headY: CGFloat) -> NSScreen? {
        guard let scr = Desktop.screen(at: CGPoint(x: edge - side * 4, y: headY)) ?? Desktop.nearestScreen(CGPoint(x: edge - side * 4, y: headY)),
              abs((side > 0 ? scr.frame.maxX : scr.frame.minX) - edge) < 1 else { return nil }
        let g = headGeo
        let y = clamp(headY, scr.visibleFrame.minY + g.r + 10, scr.visibleFrame.maxY - g.r - 10)
        let w = spriteSize.width * 1.2
        let hide = CGRect(x: side > 0 ? edge : edge - w, y: y - g.hc - 10, width: w, height: g.hc + g.r + 20)
        return NSScreen.screens.contains { $0 != scr && $0.frame.intersects(hide) } ? nil : scr
    }

    /// Tuck in behind a side edge of `screen` with her head at about `headY`, and peek out.
    func startPeek(side: CGFloat, screen scr: NSScreen, headY: CGFloat, quiet: Bool = false) {
        let g = headGeo, v = scr.visibleFrame
        let edge = side > 0 ? scr.frame.maxX : scr.frame.minX
        peek = Peek(side: side, edge: edge, headY: clamp(headY, v.minY + g.r + 10, v.maxY - g.r - 10))
        seat = nil; hostRect = nil; hop = nil; chasing = false
        vel = .zero
        mode = .peeking
        wake(quiet: true)
        body.panel.level = .floating
        body.panel.orderFrontRegardless()
        if !quiet && now() - lastPeekLine > 20 && Int.random(in: 0..<2) == 0 {
            lastPeekLine = now()
            say(["躲好了～", "嘘……", "从这里偷偷看你"].randomElement()!, duration: 2)
        }
    }

    /// Peek from the nearer side edge of the screen she's on (the other one if that side runs on into another screen).
    /// `slot` spreads several pets: odd slots take the far edge, and each pair sits a little lower.
    func peekAtEdge(slot: Int = 0) {
        guard let scr = screen else { return }
        let g = headGeo
        let near: CGFloat = feet.x > scr.frame.midX ? 1 : -1
        let order = slot % 2 == 0 ? [near, -near] : [-near, near]
        let y = (peek?.headY ?? feet.y + g.hc) - CGFloat(slot / 2) * g.r * 3.2
        for side in order {
            if let s = edgeFree(side: side, edge: side > 0 ? scr.frame.maxX : scr.frame.minX, headY: y) {
                startPeek(side: side, screen: s, headY: max(y, s.visibleFrame.minY + spriteSize.height * 0.6))
                return
            }
        }
        say("这块屏幕两边都连着别的屏幕，没地方躲～", duration: 2.4)
    }

    /// Hop out from behind the edge back onto the desktop.
    func leavePeek() {
        guard let pk = peek else { return }
        let g = headGeo
        feet = CGPoint(x: pk.edge - pk.side * spriteSize.width * 0.45, y: pk.headY - g.hc)
        throwHer(vx: -pk.side * 380, vy: 560)
    }

    private func peekStep(_ t: Double, _ dt: CGFloat) {
        guard var pk = peek else { mode = .airborne; return }
        let g = headGeo
        let m = pointer()
        let head = CGPoint(x: pk.edge - pk.side * g.r * 0.4, y: pk.headY)
        let d = hypot(m.x - head.x, m.y - head.y)
        let reach = g.r * 4 + 90
        let over = body.hits(m, sprites)
        var want: CGFloat = 0.68
        if shot != nil || awaiting != nil || working || (bubble.visible && !bubble.text.isEmpty) || isActing {
            want = 1                                   // something to show or say: lean right out
        } else {
            // The pointer rushing at her: duck back behind the edge, leaving a tuft of hair out.
            if d < reach && mouseSpeed > 280 && !over { pk.duckUntil = t + 1.2; pk.found = false }
            // The pointer resting near her (or on her): found out, so she leans out a little sheepishly.
            if (d < reach * 0.8 && mouseSpeed < 70) || over { if pk.nearSince == nil { pk.nearSince = t } } else { pk.nearSince = nil }
            let found = over || (pk.nearSince.map { t - $0 > 0.8 } ?? false)
            if found && !pk.found && t > pk.duckUntil && t - lastPeekLine > 25 && Int.random(in: 0..<3) == 0 {
                lastPeekLine = t
                say(["被发现了……", "嘿嘿", "我只是路过～"].randomElement()!, duration: 1.8)
            }
            pk.found = found
            if t < pk.duckUntil { want = 0.2 }
            else if found { want = 0.95 }
            else {
                // Now and then: duck and come back, or lean out for a better look.
                if pk.fidget == nil && t > pk.nextFidget {
                    pk.fidget = Bool.random() ? (t + 0.9, 0.3) : (t + 1.6, 0.88)
                    pk.nextFidget = t + Double.random(in: 5...11)
                }
                if let f = pk.fidget { if t < f.until { want = f.p } else { pk.fidget = nil } }
            }
        }
        // Ducks fast, comes back out slowly.
        let rate: CGFloat = want < pk.p ? 12 : 2.4
        pk.p += (want - pk.p) * min(1, rate * dt)
        peek = pk
        // The face's spot across the edge, then her feet from the current tilt (so it holds while the tilt eases in).
        let inside = lerp(-g.r - 6, g.r * 1.05, pk.p)
        let o = faceOffset(rot)
        feet = CGPoint(x: pk.edge - pk.side * inside - o.dx, y: pk.headY - o.dy)
    }

    /// Eyes on the pointer while she peeks — the look frames are drawn upright, so take off her tilt.
    private func peekLook() -> Cell? {
        guard let pk = peek else { return nil }
        let m = pointer(), h = peekHead
        var world = atan2(Double(m.x - h.x), Double(m.y - h.y)) * 180 / .pi
        if hypot(m.x - h.x, m.y - h.y) > 900 { world = pk.side > 0 ? 270 : 90 }   // far away: look into the screen
        lookDeg = world + Double(rot)
        return sprites.look(lookDeg)
    }

    // MARK: Status (for the menu and the control socket)

    func status() -> [String: Any] {
        let b = music.beat()
        let s = music.tracker.snapshot()
        return [
            "mode": mode.rawValue,
            "peek": peek.map { ["side": Double($0.side), "edge": Double($0.edge), "headY": Double($0.headY), "out": Double($0.p)] } ?? [:],
            "screen": NSScreen.screens.firstIndex { $0 == screen } ?? -1,
            "feet": [Double(feet.x), Double(feet.y)],
            "seatWindow": seat?.win.map { Int($0) } ?? 0,
            "seatOwner": seat?.win.flatMap { id in wins.first { $0.id == id }?.owner } ?? (seat == nil ? "" : "floor"),
            "cell": body.cell.map { [$0.row, $0.col] } ?? [],
            "rotation": Double(rot),
            "asleep": asleep,
            "character": ch.id,
            "dockLift": Double(dockLift),
            "chasing": chasing,
            "sitting": body.cell.map { ch.anim(of: $0) == .perch } ?? false,
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
            "acting": isActing ? (acting?.anim.rawValue ?? "") : "",
            "flipped": isActing && (acting?.flip ?? false),
            "bubble": bubble.visible ? bubble.text : "",
        ]
    }
}

