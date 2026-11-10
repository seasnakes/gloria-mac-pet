import AppKit
import ServiceManagement

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    var pets: [Pet] = []
    var music = MusicWatch()
    let brain = HuskBrain()
    let server = HuskServer()
    var status: NSStatusItem!
    /// While recording a video take, real hook events are ignored (scripted `event` commands still play).
    var muteHooks = false
    /// The pet whose menu is open (right-click on her); nil for the menu bar icon, which speaks for everyone.
    private weak var menuPet: Pet?

    func applicationDidFinishLaunching(_ n: Notification) {
        // One GLORIA at a time (e.g. the copy in Applications and the one in the project folder).
        let me = ProcessInfo.processInfo.processIdentifier
        if let id = Bundle.main.bundleIdentifier,
           NSRunningApplication.runningApplications(withBundleIdentifier: id).contains(where: { $0.processIdentifier != me }) {
            NSApp.terminate(nil)
            return
        }
        brain.petFor = { [weak self] src in self?.petFor(src) }
        brain.pets = { [weak self] in self?.pets ?? [] }
        for (i, id) in Prefs.shown.enumerated() { addPet(id, slot: i) }
        if pets.isEmpty { addPet("gloria", slot: 0) }
        guard !pets.isEmpty else {
            let a = NSAlert()
            a.messageText = "找不到 GLORIA 的精灵图"
            a.runModal()
            NSApp.terminate(nil)
            return
        }

        server.onEvent = { [weak self] e in if self?.muteHooks != true { self?.brain.handle(e) } }
        server.onControl = { [weak self] m in self?.control(m) ?? [:] }
        server.start()

        status = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        status.button?.imagePosition = .imageOnly
        status.button?.toolTip = "GLORIA 桌宠"
        let menu = NSMenu()
        menu.delegate = self
        status.menu = menu
        afterRoster()

        let poll = Timer(timeInterval: 0.5, repeats: true) { [weak self] _ in self?.music.poll() }
        RunLoop.main.add(poll, forMode: .common)

        // Hooks point at this copy of the app; if it moved, point them here again.
        for src in Agents.on where !HookInstaller.installed(src) { try? HookInstaller.set(src, on: true) }
        if !Prefs.onboarded { DispatchQueue.main.asyncAfter(deadline: .now() + 2.5) { [weak self] in self?.onboard() } }
    }

    /// First launch on a Mac: offer to hook up the agents that are installed here (nothing is changed without a yes).
    func onboard() {
        Prefs.onboarded = true
        let found = Agents.all.filter { Agents.present($0.id) && !Prefs.husk($0.id) }
        guard !found.isEmpty else { return }
        let a = NSAlert()
        a.messageText = "让她当 AI 编程助手的化身？"
        a.informativeText = "在这台 Mac 上找到了下面这些。勾上的会装上 GLORIA 的钩子（改之前会备份，菜单里随时能关）："
            + "它们干活、跑命令、等你批准、做完时，她都会演出来、冒泡告诉你。"
        let box = NSStackView()
        box.orientation = .vertical
        box.alignment = .leading
        var checks: [(String, NSButton)] = []
        for info in found {
            let note = ["codex": "（之后 Codex 会问一次要不要信任钩子）", "dsh": "（还要给它装 hooks 插件，见 README）"][info.id] ?? ""
            let c = NSButton(checkboxWithTitle: info.name + note, target: nil, action: nil)
            c.state = .on
            box.addArrangedSubview(c)
            checks.append((info.id, c))
        }
        box.frame = NSRect(x: 0, y: 0, width: 360, height: CGFloat(checks.count) * 24)
        a.accessoryView = box
        a.addButton(withTitle: "接入")
        a.addButton(withTitle: "以后再说")
        NSApp.activate(ignoringOtherApps: true)
        guard a.runModal() == .alertFirstButtonReturn else {
            focus.say("想接的时候，右键我 › 化身 › 接入。", duration: 4)
            return
        }
        let picked = checks.filter { $0.1.state == .on }.map(\.0)
        var failed: [String] = []
        for src in picked {
            do { try HookInstaller.set(src, on: true); Prefs.setHusk(src, true) } catch { failed.append("\(Agents.name(src))：\(error)") }
        }
        afterRoster()
        if !failed.isEmpty { focus.say("有的没接上——" + failed.joined(separator: "；"), duration: 7) }
        else if !picked.isEmpty {
            focus.play(.wave)
            focus.say("接好了：\(picked.map(Agents.name).joined(separator: "、"))。重开一次它们就生效啦。", duration: 6)
        }
    }

    // MARK: Pets on screen

    @discardableResult
    func addPet(_ id: String, slot: Int, at: CGPoint? = nil) -> Pet? {
        guard !pets.contains(where: { $0.ch.id == id }), let ch = Character.load(id) else { return nil }
        let p = Pet(ch: ch, music: music)
        p.peers = { [weak self] in self?.pets ?? [] }
        p.onMenu = { [weak self, weak p] e, v in
            guard let self else { return }
            self.menuPet = p
            NSMenu.popUpContextMenu(self.buildMenu(for: p), with: e, for: v)
        }
        pets.append(p)
        p.start(at: at, slot: slot)
        return p
    }

    /// The pet standing in for an agent: the one picked in the menu, else the first on screen.
    func petFor(_ src: String) -> Pet? {
        let id = Prefs.role(src)
        return pets.first { $0.ch.id == id } ?? pets.first
    }

    func setShown(_ id: String, _ on: Bool) {
        if on {
            guard !pets.contains(where: { $0.ch.id == id }) else { return }
            guard addPet(id, slot: pets.count) != nil else { pets.first?.say("找不到 \(Character.title(id)) 的精灵图。", duration: 3); return }
        } else {
            guard pets.count > 1, let p = pets.first(where: { $0.ch.id == id }) else { return }
            p.teardown()
            pets.removeAll { $0 === p }
        }
        Prefs.shown = pets.map { $0.ch.id }
        afterRoster()
    }

    /// Swap one pet for another character in the same spot.
    func replace(_ old: Pet, with id: String) {
        guard old.ch.id != id else { return }
        if pets.contains(where: { $0.ch.id == id }) { setShown(old.ch.id, false); return }
        guard let ch = Character.load(id) else { old.say("找不到 \(Character.title(id)) 的精灵图。", duration: 3); return }
        let at = old.feet
        let i = pets.firstIndex { $0 === old } ?? 0
        for src in Agents.ids where Prefs.role(src) == old.ch.id { Prefs.setRole(src, id) }
        old.teardown()
        let p = Pet(ch: ch, music: music)
        p.peers = { [weak self] in self?.pets ?? [] }
        p.onMenu = { [weak self, weak p] e, v in
            guard let self else { return }
            self.menuPet = p
            NSMenu.popUpContextMenu(self.buildMenu(for: p), with: e, for: v)
        }
        pets[i] = p
        p.start(at: at)
        Prefs.shown = pets.map { $0.ch.id }
        afterRoster()
    }

    func setRole(_ src: String, _ id: String) {
        if !pets.contains(where: { $0.ch.id == id }) { setShown(id, true) }
        Prefs.setRole(src, id)
        brain.clear()
        afterRoster()
        if let p = petFor(src), Prefs.husk(src) {
            p.play(.wave)
            p.say("我来当 \(Agents.name(src)) 的化身！", tag: Agents.tag(src), duration: 3)
        }
    }

    private func afterRoster() {
        brain.resync()
        for p in pets { p.roles = [] }
        for src in Agents.on { petFor(src)?.roles.append(Agents.name(src)) }
        status?.button?.image = rosterIcon()
    }

    /// Everyone's face side by side for the menu bar.
    private func rosterIcon() -> NSImage? {
        let faces = pets.compactMap { $0.ch.icon(size: 18) }
        guard !faces.isEmpty else { return nil }
        if faces.count == 1 { return faces[0] }
        let overlap: CGFloat = 4
        let w = faces.reduce(0) { $0 + $1.size.width } - overlap * CGFloat(faces.count - 1)
        return NSImage(size: NSSize(width: w, height: 18), flipped: false) { _ in
            var x: CGFloat = 0
            for f in faces { f.draw(in: NSRect(x: x, y: 0, width: f.size.width, height: 18)); x += f.size.width - overlap }
            return true
        }
    }

    func applicationWillTerminate(_ n: Notification) {
        UserDefaults.standard.synchronize()
        server.stop()
        music.stopTap()
    }

    // MARK: Menu

    func menuNeedsUpdate(_ menu: NSMenu) {
        menuPet = nil
        menu.removeAllItems()
        for i in buildMenu(for: nil).items { i.menu?.removeItem(i); menu.addItem(i) }
    }

    private func item(_ title: String, _ sel: Selector?, on: Bool? = nil, key: String = "", tag: Int = 0) -> NSMenuItem {
        let m = NSMenuItem(title: title, action: sel, keyEquivalent: key)
        m.target = self
        m.tag = tag
        if let on { m.state = on ? .on : .off }
        if sel == nil { m.isEnabled = false }
        return m
    }

    private func where_(_ p: Pet) -> String {
        let s = p.status()
        switch s["mode"] as? String {
        case "perched", "walking": return ((s["seatOwner"] as? String) == "floor" ? "站在屏幕底部" : "站在「\(s["seatOwner"] as? String ?? "")」的窗口上") + screenNote(p)
        case "held": return "被你拎着"
        case "peeking": return "躲在屏幕\(((s["peek"] as? [String: Double])?["side"] ?? 1) > 0 ? "右" : "左")边偷看" + screenNote(p)
        default: return "在空中"
        }
    }

    /// "（外接显示器 P275MV）" when there's more than one screen.
    private func screenNote(_ p: Pet) -> String {
        guard NSScreen.screens.count > 1, let s = p.screen else { return "" }
        return "（\(Desktop.name(s))）"
    }

    func buildMenu(for pet: Pet?) -> NSMenu {
        let m = NSMenu()
        m.autoenablesItems = false
        for p in pet.map({ [$0] }) ?? pets {
            let title = Character.title(p.ch.id) + (p.roles.isEmpty ? "" : " · \(p.roles.joined(separator: " / ")) 化身")
            let head = item(title, nil)
            head.attributedTitle = NSAttributedString(string: title, attributes: [.font: NSFont.systemFont(ofSize: 13, weight: .bold)])
            m.addItem(head)
            m.addItem(item("  \(where_(p))", nil))
        }
        let musicLine: String
        if music.dancing { musicLine = "  ♪ 跳舞中" + (music.playerName.isEmpty ? "" : " · \(music.playerName)") + (music.tapLive ? " · \(music.tapNote)" : "") }
        else if music.active { musicLine = "  ♪ \(music.playerName) 有声音（不是音乐 App，等节拍稳定）" }
        else { musicLine = "  ♪ 没在放歌" }
        m.addItem(item(musicLine, nil))
        if !Agents.on.isEmpty { m.addItem(item("  ⌘ 化身：\(brain.summary)", nil)) }
        m.addItem(.separator())

        // Who's on screen.
        let who = NSMenuItem(title: "角色（可以同时放几个）", action: nil, keyEquivalent: "")
        let wm = NSMenu()
        for id in Character.ids {
            let shown = pets.contains { $0.ch.id == id }
            let it = item(Character.title(id), #selector(toggleShown(_:)), on: shown)
            it.representedObject = id
            if shown && pets.count == 1 { it.isEnabled = false }
            wm.addItem(it)
        }
        who.submenu = wm
        m.addItem(who)

        // Agents and the pets standing in for them.
        for (si, src) in Agents.ids.enumerated() {
            let name = Agents.name(src)
            let on = Prefs.husk(src)
            let it = NSMenuItem(title: "\(name) 化身" + (on ? "：\(petFor(src).map { Character.title($0.ch.id) } ?? "")" : "（未接入）"), action: nil, keyEquivalent: "")
            let sm = NSMenu()
            let hook = item("接入 \(name)（装钩子）", #selector(toggleHusk(_:)), on: on, tag: si)
            sm.addItem(hook)
            sm.addItem(item("  " + HookInstaller.file(src).path.replacingOccurrences(of: NSHomeDirectory(), with: "~"), nil))
            sm.addItem(.separator())
            sm.addItem(item("由谁来当：", nil))
            for id in Character.ids {
                let r = item("  " + Character.title(id), #selector(pickRole(_:)), on: petFor(src)?.ch.id == id, tag: si)
                r.representedObject = id
                sm.addItem(r)
            }
            it.submenu = sm
            m.addItem(it)
        }
        m.addItem(item("需要批准时提示音", #selector(toggleChime), on: Prefs.chime))
        m.addItem(.separator())

        m.addItem(item(pet == nil && pets.count > 1 ? "叫她们过来" : "叫她过来", #selector(summon)))
        if let pet, pets.count > 1 { m.addItem(item("让 \(pet.ch.name) 先休息（隐藏）", #selector(hideThis))) }
        if NSScreen.screens.count > 1 {
            let mv = NSMenuItem(title: pet == nil && pets.count > 1 ? "移到显示器（全部）" : "移到显示器", action: nil, keyEquivalent: "")
            let ms = NSMenu()
            let who = pet.map { [$0] } ?? pets
            for (i, s) in NSScreen.screens.enumerated() {
                let here = who.allSatisfy { $0.screen == s }
                ms.addItem(item(Desktop.name(s) + (i == 0 ? "（主显示器）" : ""), #selector(moveToScreen(_:)), on: here, tag: i))
            }
            mv.submenu = ms
            m.addItem(mv)
        }
        m.addItem(item(pet == nil && pets.count > 1 ? "都躲到屏幕边上偷看" : "躲到屏幕边上偷看", #selector(peekEdge)))
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
        m.addItem(item("拖到屏幕左右边缘会躲起来探头", #selector(toggleEdgePeek), on: Prefs.edgePeek))
        m.addItem(item("登录时启动", #selector(toggleLogin), on: SMAppService.mainApp.status == .enabled))
        m.addItem(.separator())

        m.addItem(item("随音乐跳舞", #selector(toggleDance), on: Prefs.dance))
        m.addItem(item("跟着节拍（需要“系统录音”权限）", #selector(toggleTap), on: Prefs.beatTap))
        m.addItem(item("浏览器里的音乐也跳", #selector(toggleBrowser), on: Prefs.browserMusic))
        m.addItem(.separator())

        let demo = NSMenuItem(title: "演示", action: nil, keyEquivalent: "")
        let dm = NSMenu()
        dm.addItem(item("跳舞 16 秒", #selector(demoDance)))
        dm.addItem(item("Claude Code 一轮工作", #selector(demoHusk)))
        dm.addItem(item("Claude 请 Codex 帮忙（两个化身传话）", #selector(demoTogether)))
        dm.addItem(item("Codex 操作电脑（computer use）", #selector(demoComputerUse)))
        dm.addItem(item("被扔出去", #selector(demoThrow)))
        dm.addItem(item("被拎起来晃一晃", #selector(demoHold)))
        dm.addItem(item("打个盹 10 秒", #selector(demoNap)))
        demo.submenu = dm
        m.addItem(demo)
        m.addItem(.separator())
        m.addItem(item("退出", #selector(quit), key: "q"))
        return m
    }

    /// The pet a menu action is about: the one right-clicked, else the first.
    private var focus: Pet { menuPet ?? pets[0] }

    @objc func summon() {
        if let p = menuPet { p.summon(entrance: false); return }
        for (i, p) in pets.enumerated() { p.summon(entrance: false, slot: i) }
    }
    @objc func moveToScreen(_ sender: NSMenuItem) {
        guard sender.tag < NSScreen.screens.count else { return }
        let s = NSScreen.screens[sender.tag]
        if let p = menuPet { p.summon(entrance: false, on: s); return }
        for (i, p) in pets.enumerated() { p.summon(entrance: false, slot: i, on: s) }
    }
    @objc func peekEdge() {
        if let p = menuPet { p.peekAtEdge(); return }
        for (i, p) in pets.enumerated() { p.peekAtEdge(slot: i) }
    }
    @objc func toggleEdgePeek() { Prefs.edgePeek.toggle() }
    @objc func hideThis() { if let p = menuPet { setShown(p.ch.id, false) } }
    @objc func toggleShown(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? String else { return }
        setShown(id, !pets.contains { $0.ch.id == id })
    }
    @objc func pickRole(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? String else { return }
        setRole(Agents.ids[sender.tag], id)
    }
    @objc func setSize(_ sender: NSMenuItem) { for p in pets { p.setScale(CGFloat([0.5, 0.62, 0.8, 1.0][sender.tag])) } }
    @objc func toggleWander() { Prefs.wander.toggle() }
    @objc func toggleNap() { Prefs.nap.toggle() }
    @objc func toggleChase() { Prefs.chase.toggle() }
    @objc func toggleLogin() { setLogin(SMAppService.mainApp.status != .enabled) }

    /// Start at login through macOS's own Login Items (System Settings › General › Login Items).
    @discardableResult
    func setLogin(_ on: Bool) -> String {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            focus.say(on ? "好～以后登录 Mac 我就自己出来。" : "以后登录时我就不自己出来啦。", duration: 3)
            return "ok"
        } catch {
            focus.say("没设置成功：\(error.localizedDescription)", duration: 5)
            return error.localizedDescription
        }
    }
    @objc func toggleDance() { Prefs.dance.toggle() }
    @objc func toggleTap() { Prefs.beatTap.toggle(); if !Prefs.beatTap { music.stopTap() } }
    @objc func toggleBrowser() { Prefs.browserMusic.toggle() }
    @objc func toggleChime() { Prefs.chime.toggle() }
    @objc func toggleHusk(_ sender: NSMenuItem) { let src = Agents.ids[sender.tag]; setHusk(src, !Prefs.husk(src)) }
    @objc func demoDance() { pets[0].forceDance(16) }
    @objc func demoHusk() { brain.demo() }
    @objc func demoTogether() {
        if Set(["claude", "codex"].compactMap { petFor($0).map { ObjectIdentifier($0) } }).count < 2 {
            focus.say("先在「Claude Code 化身」和「Codex 化身」里各选一个不同的角色，她们才能互相传话。", duration: 5)
            return
        }
        brain.demoTogether()
    }
    @objc func demoComputerUse() { brain.demoComputerUse() }
    @objc func demoThrow() { focus.throwHer(vx: CGFloat.random(in: -1400...1400), vy: 1500) }
    @objc func demoHold() { focus.simulateHold(3) }
    @objc func demoNap() { focus.forceSleepUntil = now() + 10 }
    @objc func quit() { NSApp.terminate(nil) }

    private func setHusk(_ src: String, _ on: Bool) {
        let p = petFor(src) ?? focus
        do {
            try HookInstaller.set(src, on: on)
            Prefs.setHusk(src, on)
            afterRoster()
            let app = Agents.name(src)
            let after = ["claude": "重启 Claude Code 后，我就是它的分身啦。",
                         "codex": "重启 Codex 后，我就是它的分身啦。Codex 会问要不要信任这些钩子，选信任。",
                         "cursor": "新开一个 Cursor 对话（编辑器或 cursor-agent）就生效。",
                         "dsh": "钩子写进 ~/.dsh/hooks.json 了。DeepSeek Harness 还要装一个 hooks 插件来读它，命令在 README 里。"][src] ?? ""
            p.say(on ? "好了！" + after : "已经从 \(app) 里拿掉我的钩子。", tag: Agents.tag(src), duration: src == "dsh" ? 8 : 6)
        } catch {
            p.say("没设置成功：\(error)", duration: 6)
        }
    }

    // MARK: Control socket (for scripts and checks)

    func control(_ m: [String: Any]) -> [String: Any] {
        let cmd = m["ctl"] as? String ?? "status"
        func num(_ k: String, _ d: Double) -> Double { (m[k] as? NSNumber)?.doubleValue ?? d }
        // Commands about one pet take "pet": "<character id>"; the default is the first on screen.
        let pet = (m["pet"] as? String).flatMap { id in pets.first { $0.ch.id == id } } ?? pets[0]
        switch cmd {
        case "dance": pet.forceDance(num("seconds", 12))
        case "stopdance": music.forcedUntil = 0
        case "say": pet.say(m["text"] as? String ?? "", tag: m["tag"] as? String, duration: num("seconds", 4))
        case "summon": if m["pet"] == nil { for (i, p) in pets.enumerated() { p.summon(entrance: false, slot: i) } } else { pet.summon(entrance: false) }
        case "throw": pet.throwHer(vx: CGFloat(num("vx", 900)), vy: CGFloat(num("vy", 1200)))
        case "place": pet.place(x: CGFloat(num("x", 500)), y: CGFloat(num("y", 600)))
        case "scale": for p in pets { p.setScale(CGFloat(num("value", 0.62))) }
        case "demo": brain.demo()
        case "together": brain.demoTogether()
        case "cua": brain.demoComputerUse()
        case "hold": pet.simulateHold(num("seconds", 3))
        case "drop": pet.drop(at: CGPoint(x: num("x", 500), y: num("y", 600)), velocity: CGVector(dx: num("vx", 0), dy: num("vy", 0)))
        case "screen":
            let i = Int(num("index", 0))
            if i >= 0 && i < NSScreen.screens.count {
                if m["pet"] == nil { for (k, p) in pets.enumerated() { p.summon(entrance: false, slot: k, on: NSScreen.screens[i]) } }
                else { pet.summon(entrance: false, on: NSScreen.screens[i]) }
            }
        case "peek":
            if let side = m["side"] as? String, side != "auto", let scr = pet.screen {
                let sd: CGFloat = side == "left" ? -1 : 1
                if let s = pet.edgeFree(side: sd, edge: sd > 0 ? scr.frame.maxX : scr.frame.minX, headY: CGFloat(num("y", Double(scr.visibleFrame.midY)))) {
                    pet.startPeek(side: sd, screen: s, headY: CGFloat(num("y", Double(scr.visibleFrame.midY))), quiet: true)
                }
            } else { pet.peekAtEdge() }
        case "unpeek": pet.leavePeek()
        case "character": replace(pet, with: m["id"] as? String ?? "gloria")
        case "show": setShown(m["id"] as? String ?? "", (m["on"] as? Bool) ?? true)
        case "role": setRole(m["source"] as? String ?? "claude", m["id"] as? String ?? pet.ch.id)
        case "launch": for p in pets { p.debugDockBounce() }
        case "huskreset": brain.clear()
        case "quit":
            UserDefaults.standard.synchronize()
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) { NSApp.terminate(nil) }
        case "login":
            let r = setLogin((m["on"] as? Bool) ?? true)
            return ["result": r, "status": SMAppService.mainApp.status.rawValue, "path": Bundle.main.bundlePath]
        case "pointer": for p in pets { p.fakePointer = (CGPoint(x: num("x", 0), y: num("y", 0)), now() + num("seconds", 3)) }
        case "hit": return ["hit": pet.body.hits(CGPoint(x: num("x", 0), y: num("y", 0)), pet.sprites)]
        case "play":
            if let a = Anim(rawValue: m["anim"] as? String ?? "") { pet.play(a, loops: Int(num("loops", 2))) }
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
        case "menu":
            func titles(_ m: NSMenu, _ d: Int) -> [String] {
                m.items.flatMap { i -> [String] in
                    let mark = i.state == .on ? "✓ " : ""
                    let line = i.isSeparatorItem ? "—" : String(repeating: "  ", count: d) + mark + i.title
                    return [line] + (i.submenu.map { titles($0, d + 1) } ?? [])
                }
            }
            return ["items": titles(buildMenu(for: m["pet"] == nil ? nil : pet), 0)]
        case "mute":
            muteHooks = (m["on"] as? Bool) ?? true
            return ["muted": muteHooks]
        case "pref":
            // {"ctl":"pref","key":"dance|chase|wander|nap|chime","on":false} — the same switches as the menu.
            if let k = m["key"] as? String, ["dance", "chase", "wander", "nap", "chime", "beatTap", "browserMusic"].contains(k) {
                UserDefaults.standard.set((m["on"] as? Bool) ?? true, forKey: k)
            }
            return ["dance": Prefs.dance, "chase": Prefs.chase, "wander": Prefs.wander, "nap": Prefs.nap]
        case "stage":
            if (m["off"] as? Bool) == true { Desktop.staged = nil }
            else if let ws = m["wins"] as? [[String: Any]] {
                let running = NSWorkspace.shared.runningApplications
                Desktop.staged = ws.enumerated().compactMap { k, w in
                    guard let r = w["rect"] as? [NSNumber], r.count == 4 else { return nil }
                    let owner = w["owner"] as? String ?? "App"
                    let app = w["app"] as? String
                    let pid = running.first { $0.localizedName == owner || (app != nil && $0.bundleIdentifier == app) }?.processIdentifier ?? getpid()
                    return Win(id: CGWindowID((w["id"] as? NSNumber)?.intValue ?? 900 + k), pid: pid, owner: owner,
                               rect: CGRect(x: r[0].doubleValue, y: r[1].doubleValue, width: r[2].doubleValue, height: r[3].doubleValue))
                }
            }
            return ["staged": (Desktop.staged ?? []).map { ["id": Int($0.id), "owner": $0.owner, "pid": Int($0.pid)] }]
        case "record":
            if (m["stop"] as? Bool) == true { Recorder.active?.stop(); return ["stopped": true] }
            if let r = Recorder.active { return r.status }
            guard let path = m["path"] as? String,
                  let r = Recorder(dir: URL(fileURLWithPath: path), seconds: num("seconds", 10), fps: num("fps", 30),
                                   scale: CGFloat(num("scale", 2)), pets: { [weak self] in self?.pets ?? [] }) else { return ["error": "bad path"] }
            Recorder.active = r
            return r.status
        case "scene":
            if let path = m["path"] as? String { scene(path: path, scale: CGFloat(num("scale", 0.5))) }
        default: break
        }
        var s = pet.status()
        s["husk"] = brain.summary
        s["huskEvents"] = brain.events
        s["pets"] = pets.map { p -> [String: Any] in
            var d = p.status()
            d["roles"] = p.roles
            return d
        }
        s["roles"] = Dictionary(uniqueKeysWithValues: Agents.ids.map { ($0, petFor($0)?.ch.id ?? "") })
        s["hooked"] = Agents.on
        return s
    }

    /// A schematic of the main screen for checks: window outlines (geometry only), every pet, bubble and envelope.
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
        for p in pets {
            p.body.beginCapture()
            put(p.body.panel, p.body.view.layer)
            p.body.endCapture()
            if p.bubble.visible, let rep = p.bubble.view.bitmapImageRepForCachingDisplay(in: p.bubble.view.bounds) {
                p.bubble.view.cacheDisplay(in: p.bubble.view.bounds, to: rep)
                if let img = rep.cgImage {
                    let b = p.bubble.panel.frame
                    ctx.draw(img, in: CGRect(x: (b.minX - f.minX) * k, y: (b.minY - f.minY) * k, width: b.width * k, height: b.height * k))
                }
            }
        }
        for c in Courier.inFlight { put(c.panel, c.layer) }
        // Effects only show through their animations: draw the on-screen (presentation) state.
        for e in Effects.panels { put(e.panel, e.layer?.presentation() ?? e.layer) }
        guard let img = ctx.makeImage() else { return }
        let rep = NSBitmapImageRep(cgImage: img)
        try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path))
    }
}
