import AppKit

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    var pet: Pet!
    var music = MusicWatch()
    var brain: HuskBrain!
    let server = HuskServer()
    var status: NSStatusItem!

    func applicationDidFinishLaunching(_ n: Notification) {
        guard let ch = Character.load(Prefs.character) ?? Character.load("gloria") else {
            let a = NSAlert()
            a.messageText = "找不到 GLORIA 的精灵图"
            a.runModal()
            NSApp.terminate(nil)
            return
        }
        Character.current = ch
        pet = Pet(sprites: ch.sprites, music: music)
        brain = HuskBrain(pet: pet)
        wire(pet)

        server.onEvent = { [weak self] e in self?.brain.handle(e) }
        server.onControl = { [weak self] m in self?.control(m) ?? [:] }
        server.start()

        status = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        status.button?.image = ch.icon(size: 18)
        status.button?.imagePosition = .imageOnly
        status.button?.toolTip = "GLORIA"
        let menu = NSMenu()
        menu.delegate = self
        status.menu = menu

        pet.start()
        let poll = Timer(timeInterval: 0.5, repeats: true) { [weak self] _ in self?.music.poll() }
        RunLoop.main.add(poll, forMode: .common)

        // Hooks point at this copy of the app; if it moved, point them here again.
        for src in ["claude", "codex"] where (src == "claude" ? Prefs.huskClaude : Prefs.huskCodex) && !HookInstaller.installed(src) {
            try? HookInstaller.set(src, on: true)
        }
    }

    private func wire(_ p: Pet) {
        p.onMenu = { [weak self] e, v in
            guard let self else { return }
            NSMenu.popUpContextMenu(self.buildMenu(), with: e, for: v)
        }
    }

    /// One character on screen at a time: the new one drops in where the old one was.
    func switchCharacter(_ id: String) {
        guard id != Character.current.id else { return }
        guard let ch = Character.load(id) else { pet.say("找不到 \(Character.title(id)) 的精灵图。", duration: 3); return }
        let at = pet.feet
        pet.teardown()
        Character.current = ch
        Prefs.character = id
        let p = Pet(sprites: ch.sprites, music: music)
        pet = p
        wire(p)
        brain.pet = p
        brain.resync()
        status.button?.image = ch.icon(size: 18)
        p.start(at: at)
    }

    func applicationWillTerminate(_ n: Notification) {
        server.stop()
        music.stopTap()
    }

    // MARK: Menu

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        for i in buildMenu().items { i.menu?.removeItem(i); menu.addItem(i) }
    }

    private func item(_ title: String, _ sel: Selector?, on: Bool? = nil, key: String = "", tag: Int = 0) -> NSMenuItem {
        let m = NSMenuItem(title: title, action: sel, keyEquivalent: key)
        m.target = self
        m.tag = tag
        if let on { m.state = on ? .on : .off }
        if sel == nil { m.isEnabled = false }
        return m
    }

    func buildMenu() -> NSMenu {
        let m = NSMenu()
        m.autoenablesItems = false
        let head = item(Character.title(Character.current.id), nil)
        head.attributedTitle = NSAttributedString(string: Character.title(Character.current.id), attributes: [.font: NSFont.systemFont(ofSize: 13, weight: .bold)])
        m.addItem(head)
        let s = pet.status()
        let place: String
        switch s["mode"] as? String {
        case "perched", "walking": place = (s["seatOwner"] as? String) == "floor" ? "站在屏幕底部" : "站在「\(s["seatOwner"] as? String ?? "")」的窗口上"
        case "held": place = "被你拎着"
        default: place = "在空中"
        }
        m.addItem(item("  \(place)", nil))
        let musicLine: String
        if music.dancing { musicLine = "  ♪ 跳舞中" + (music.playerName.isEmpty ? "" : " · \(music.playerName)") + (music.tapLive ? " · \(music.tapNote)" : "") }
        else if music.active { musicLine = "  ♪ \(music.playerName) 有声音（不是音乐 App，等节拍稳定）" }
        else { musicLine = "  ♪ 没在放歌" }
        m.addItem(item(musicLine, nil))
        if Prefs.huskClaude || Prefs.huskCodex { m.addItem(item("  ⌘ 化身：\(brain.summary)", nil)) }
        m.addItem(.separator())

        let who = NSMenuItem(title: "角色", action: nil, keyEquivalent: "")
        let wm = NSMenu()
        for (i, id) in Character.ids.enumerated() {
            wm.addItem(item(Character.title(id), #selector(pickCharacter(_:)), on: Character.current.id == id, tag: i))
        }
        who.submenu = wm
        m.addItem(who)
        m.addItem(item("叫她过来", #selector(summon)))
        let size = NSMenuItem(title: "大小", action: nil, keyEquivalent: "")
        let sub = NSMenu()
        for (i, (name, v)) in [("小", 0.5), ("中", 0.62), ("大", 0.8), ("特大", 1.0)].enumerated() {
            sub.addItem(item(name, #selector(setSize(_:)), on: abs(Prefs.scale - v) < 0.01, tag: i))
        }
        size.submenu = sub
        m.addItem(size)
        m.addItem(item("自己溜达", #selector(toggleWander), on: Prefs.wander))
        m.addItem(item("闲着会打盹（3 分钟没操作）", #selector(toggleNap), on: Prefs.nap))
        m.addItem(item("追光标", #selector(toggleChase), on: Prefs.chase))
        m.addItem(.separator())

        m.addItem(item("随音乐跳舞", #selector(toggleDance), on: Prefs.dance))
        m.addItem(item("跟着节拍（需要“系统录音”权限）", #selector(toggleTap), on: Prefs.beatTap))
        m.addItem(item("浏览器里的音乐也跳", #selector(toggleBrowser), on: Prefs.browserMusic))
        m.addItem(.separator())

        m.addItem(item("Claude Code 化身", #selector(toggleClaude), on: Prefs.huskClaude))
        m.addItem(item("Codex 化身", #selector(toggleCodex), on: Prefs.huskCodex))
        m.addItem(item("需要批准时提示音", #selector(toggleChime), on: Prefs.chime))
        m.addItem(.separator())

        let demo = NSMenuItem(title: "演示", action: nil, keyEquivalent: "")
        let dm = NSMenu()
        dm.addItem(item("跳舞 16 秒", #selector(demoDance)))
        dm.addItem(item("Claude Code 一轮工作", #selector(demoHusk)))
        dm.addItem(item("被扔出去", #selector(demoThrow)))
        dm.addItem(item("被拎起来晃一晃", #selector(demoHold)))
        dm.addItem(item("打个盹 10 秒", #selector(demoNap)))
        demo.submenu = dm
        m.addItem(demo)
        m.addItem(.separator())
        m.addItem(item("退出", #selector(quit), key: "q"))
        return m
    }

    @objc func summon() { pet.summon(entrance: false) }
    @objc func setSize(_ sender: NSMenuItem) { pet.setScale(CGFloat([0.5, 0.62, 0.8, 1.0][sender.tag])) }
    @objc func toggleWander() { Prefs.wander.toggle() }
    @objc func toggleNap() { Prefs.nap.toggle() }
    @objc func toggleChase() { Prefs.chase.toggle() }
    @objc func pickCharacter(_ sender: NSMenuItem) { switchCharacter(Character.ids[sender.tag]) }
    @objc func toggleDance() { Prefs.dance.toggle() }
    @objc func toggleTap() { Prefs.beatTap.toggle(); if !Prefs.beatTap { music.stopTap() } }
    @objc func toggleBrowser() { Prefs.browserMusic.toggle() }
    @objc func toggleChime() { Prefs.chime.toggle() }
    @objc func toggleClaude() { setHusk("claude", !Prefs.huskClaude) }
    @objc func toggleCodex() { setHusk("codex", !Prefs.huskCodex) }
    @objc func demoDance() { pet.forceDance(16) }
    @objc func demoHusk() { brain.demo() }
    @objc func demoThrow() { pet.throwHer(vx: CGFloat.random(in: -1400...1400), vy: 1500) }
    @objc func demoHold() { pet.simulateHold(3) }
    @objc func demoNap() { pet.forceSleepUntil = now() + 10 }
    @objc func quit() { NSApp.terminate(nil) }

    private func setHusk(_ src: String, _ on: Bool) {
        do {
            try HookInstaller.set(src, on: on)
            if src == "claude" { Prefs.huskClaude = on } else { Prefs.huskCodex = on }
            let app = src == "claude" ? "Claude Code" : "Codex"
            pet.say(on ? "好了！重启 \(app) 后，我就是它的分身啦。\(src == "codex" ? "Codex 会问要不要信任这些钩子，选信任。" : "")" : "已经从 \(app) 里拿掉我的钩子。",
                    tag: src == "claude" ? "CLAUDE CODE" : "CODEX", duration: 6)
        } catch {
            pet.say("没设置成功：\(error)", duration: 6)
        }
    }

    // MARK: Control socket (for scripts and checks)

    func control(_ m: [String: Any]) -> [String: Any] {
        let cmd = m["ctl"] as? String ?? "status"
        func num(_ k: String, _ d: Double) -> Double { (m[k] as? NSNumber)?.doubleValue ?? d }
        switch cmd {
        case "dance": pet.forceDance(num("seconds", 12))
        case "stopdance": music.forcedUntil = 0
        case "say": pet.say(m["text"] as? String ?? "", tag: m["tag"] as? String, duration: num("seconds", 4))
        case "summon": pet.summon(entrance: false)
        case "throw": pet.throwHer(vx: CGFloat(num("vx", 900)), vy: CGFloat(num("vy", 1200)))
        case "place": pet.place(x: CGFloat(num("x", 500)), y: CGFloat(num("y", 600)))
        case "scale": pet.setScale(CGFloat(num("value", 0.62)))
        case "demo": brain.demo()
        case "hold": pet.simulateHold(num("seconds", 3))
        case "character": switchCharacter(m["id"] as? String ?? "gloria")
        case "launch": pet.debugDockBounce()
        case "huskreset": brain.clear()
        case "pointer": pet.fakePointer = (CGPoint(x: num("x", 0), y: num("y", 0)), now() + num("seconds", 3))
        case "play":
            let names: [String: Anim] = ["idle": .idle, "wave": .wave, "jump": .jump, "failed": .failed, "waiting": .waiting, "work": .work,
                                         "review": .review, "perch": .perch, "dance": .dance, "sleep": .sleep, "dangle": .dangle]
            if let a = names[m["anim"] as? String ?? ""] { pet.play(a, loops: Int(num("loops", 2))) }
        case "sleep": pet.forceSleepUntil = now() + num("seconds", 10)
        case "desk":
            let r = Desktop.scan(excluding: getpid())
            return ["screens": NSScreen.screens.map { ["frame": [$0.frame.minX, $0.frame.minY, $0.frame.width, $0.frame.height], "visible": [$0.visibleFrame.minX, $0.visibleFrame.minY, $0.visibleFrame.width, $0.visibleFrame.height]] },
                    "wins": r.wins.map { ["id": Int($0.id), "owner": $0.owner, "rect": [$0.rect.minX, $0.rect.minY, $0.rect.width, $0.rect.height]] },
                    "ledges": Desktop.ledges(r.wins, headroom: pet.spriteSize.height * 0.9).map { ["win": Int($0.win ?? 0), "l": $0.left, "r": $0.right, "y": $0.y] },
                    "order": r.order.prefix(12).map { Int($0) }, "me": pet.body.panel.windowNumber]
        case "event":
            if var e = m["event"] as? [String: Any] { if e["_source"] == nil { e["_source"] = "claude" }; brain.handle(e) }
        case "husk":
            let src = m["source"] as? String ?? "claude"
            return ["installed": HookInstaller.installed(src), "ours": HookInstaller.anyOurs(src), "file": HookInstaller.file(src).path,
                    "command": HookInstaller.command(src), "events": brain.events, "summary": brain.summary]
        case "scene":
            if let path = m["path"] as? String { scene(path: path, scale: CGFloat(num("scale", 0.5))) }
        default: break
        }
        var s = pet.status()
        s["husk"] = brain.summary
        s["huskEvents"] = brain.events
        return s
    }

    /// A schematic of the main screen for checks: window outlines (geometry only), her real rendering and her bubble.
    private func scene(path: String, scale k: CGFloat) {
        guard let scr = NSScreen.screens.first else { return }
        let f = scr.frame
        let W = Int(f.width * k), H = Int(f.height * k)
        guard let ctx = CGContext(data: nil, width: W, height: H, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return }
        ctx.setFillColor(NSColor(srgbRed: 0.16, green: 0.15, blue: 0.2, alpha: 1).cgColor)
        ctx.fill(CGRect(x: 0, y: 0, width: W, height: H))
        let wins = Desktop.scan(excluding: getpid()).wins
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: false)
        for w in wins.reversed() {
            let r = CGRect(x: (w.rect.minX - f.minX) * k, y: (w.rect.minY - f.minY) * k, width: w.rect.width * k, height: w.rect.height * k)
            ctx.setFillColor(NSColor(srgbRed: 0.93, green: 0.93, blue: 0.96, alpha: 1).cgColor)
            ctx.fill(r)
            ctx.setFillColor(NSColor(srgbRed: 0.82, green: 0.8, blue: 0.9, alpha: 1).cgColor)
            ctx.fill(CGRect(x: r.minX, y: r.maxY - 14, width: r.width, height: 14))
            ctx.setStrokeColor(NSColor(srgbRed: 0.5, green: 0.48, blue: 0.6, alpha: 1).cgColor)
            ctx.stroke(r, width: 1)
            NSAttributedString(string: w.owner, attributes: [.font: NSFont.systemFont(ofSize: 10, weight: .semibold), .foregroundColor: NSColor.darkGray])
                .draw(at: CGPoint(x: r.minX + 6, y: r.maxY - 12))
        }
        let v = scr.visibleFrame
        ctx.setFillColor(NSColor(srgbRed: 0.35, green: 0.33, blue: 0.42, alpha: 1).cgColor)
        ctx.fill(CGRect(x: 0, y: 0, width: CGFloat(W), height: (v.minY - f.minY) * k))
        NSGraphicsContext.restoreGraphicsState()
        func put(_ panel: NSWindow, _ layer: CALayer?) {
            guard panel.isVisible, panel.alphaValue > 0.05, let layer else { return }
            ctx.saveGState()
            ctx.translateBy(x: (panel.frame.minX - f.minX) * k, y: (panel.frame.minY - f.minY) * k)
            ctx.scaleBy(x: k, y: k)
            layer.render(in: ctx)
            ctx.restoreGState()
        }
        put(pet.body.panel, pet.body.view.layer)
        if pet.bubble.visible, let rep = pet.bubble.view.bitmapImageRepForCachingDisplay(in: pet.bubble.view.bounds) {
            pet.bubble.view.cacheDisplay(in: pet.bubble.view.bounds, to: rep)
            if let img = rep.cgImage {
                let b = pet.bubble.panel.frame
                ctx.draw(img, in: CGRect(x: (b.minX - f.minX) * k, y: (b.minY - f.minY) * k, width: b.width * k, height: b.height * k))
            }
        }
        guard let img = ctx.makeImage() else { return }
        let rep = NSBitmapImageRep(cgImage: img)
        try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path))
    }
}
