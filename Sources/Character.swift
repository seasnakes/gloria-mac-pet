import AppKit

// The desk pets. Each has a sprite sheet in GLORIA's layout (8 columns of 192×208 cells, feet on y=202) and a JSON file
// with its rows, timings and anchors. Several can be on screen at once; every pet carries its own Character.
// Built-in pets live in the app bundle; imported ones (tools/import_dsh.py --install) in Application Support/GLORIA/characters/<id>/.

enum Anim: String, CaseIterable {
    case idle, runRight, runLeft, wave, jump, failed, waiting, work, review
    // Sitting on an edge, dance, nap, held up.
    case perch, dance, sleep, dangle
    // Computer use: clicking, typing, taking a screenshot, dragging, scrolling.
    case tap, type, snap, drag, scroll

    static let computerUse: [Anim] = [.tap, .type, .snap, .drag, .scroll]
}

/// One animation: `count` frames starting at `row`, eight per row (longer loops continue on the next rows).
struct AnimSpec {
    let row: Int
    let count: Int
    let frameTime: Double
    let durations: [Double]?
    /// Plays as one continuous loop (no random holds on the first frame).
    let loop: Bool
    /// Frames per key pose (dance: key, then in-betweens toward the next key).
    let sub: Int
    /// Which way the pose reaches ("left"/"right"), so the app can mirror it toward its target.
    let facing: String?
}

final class Character {
    let id: String
    let name: String
    let tag: String
    let sprites: Sprites
    let specs: [Anim: AnimSpec]
    let feetY: CGFloat
    let seatY: CGFloat
    let grab: CGPoint
    let scaleMul: CGFloat
    let lines: Lines
    /// A real 3D model, drawn live (nil for sprite-sheet pets).
    let model: Model3D?
    private var owner: [Cell: Anim] = [:]

    struct Lines { let greeting: String; let pats, wake, ouch, angry, hello: [String] }

    init(id: String, name: String, tag: String, sprites: Sprites, specs: [Anim: AnimSpec], feetY: CGFloat, seatY: CGFloat,
         grab: CGPoint, scaleMul: CGFloat, lines: Lines, model: Model3D? = nil) {
        self.id = id; self.name = name; self.tag = tag; self.sprites = sprites; self.specs = specs; self.model = model
        self.feetY = feetY; self.seatY = seatY; self.grab = grab; self.scaleMul = scaleMul; self.lines = lines
        for (a, s) in specs { for i in 0..<s.count { owner[cell(a, i)] = owner[cell(a, i)] ?? a } }
    }

    /// How far the seat line sits above the feet line, in cell px.
    var seatRise: CGFloat { feetY - seatY }
    /// From the feet line up to the grab point, in cell px.
    var hang: CGFloat { feetY - grab.y }
    /// Whether the sheet has the extra rows (perch, dance, sleep, dangle).
    var hasExtras: Bool { specs[.dangle] != nil && (model != nil || sprites.has(cell(.dangle, 0))) }

    /// The 3D pose for a cell the state machine picked. Look cells use the exact angle when it's the one being shown.
    func pose3D(for c: Cell, look: Double) -> Pose3D {
        if c.row == 9 || c.row == 10 {
            let deg = Double((c.row - 9) * 8 + c.col) * 22.5
            var d = (look - deg).truncatingRemainder(dividingBy: 360)
            if d > 180 { d -= 360 } else if d < -180 { d += 360 }
            return GrokPoses.pose(nil, 0, look: abs(d) <= 11.25 ? look : deg)
        }
        guard let a = owner[c], let s = specs[a] else { return GrokPoses.pose(.idle, 0, look: look) }
        return GrokPoses.pose(a, (c.row - s.row) * 8 + c.col, look: look)
    }

    func spec(_ a: Anim) -> AnimSpec { specs[a] ?? specs[.idle]! }
    func has(_ a: Anim) -> Bool { specs[a] != nil }
    /// One pass through an animation, in seconds.
    func length(_ a: Anim) -> Double { durations(a).reduce(0, +) }

    /// The pose to use for a computer-use action, falling back on what the sheet has.
    func pose(for a: Anim) -> Anim {
        if has(a) { return a }
        switch a {
        case .scroll: return has(.drag) ? .drag : .work
        case .tap: return .wave
        case .snap: return .review
        default: return .work
        }
    }
    func count(_ a: Anim) -> Int { spec(a).count }
    func frameTime(_ a: Anim) -> Double { spec(a).frameTime }
    func durations(_ a: Anim) -> [Double] { let s = spec(a); return s.durations ?? Array(repeating: s.frameTime, count: s.count) }

    func cell(_ a: Anim, _ i: Int) -> Cell {
        let s = spec(a)
        let j = ((i % s.count) + s.count) % s.count
        return Cell(row: s.row + j / 8, col: j % 8)
    }

    /// Frame index at time `t` into a loop that uses the per-frame durations.
    func index(_ a: Anim, at t: Double, speed: Double = 1) -> Int {
        let d = durations(a)
        let total = d.reduce(0, +) * speed
        var u = t.truncatingRemainder(dividingBy: max(total, 0.001))
        if u < 0 { u += total }
        for (i, x) in d.enumerated() { u -= x * speed; if u < 0 { return i } }
        return d.count - 1
    }

    /// Which animation a cell belongs to (for "is she sitting?").
    func anim(of c: Cell) -> Anim? { owner[c] }

    static let builtIn = ["gloria", "doro", "usagi", "grokbot"]
    /// Imported pets: Application Support/GLORIA/characters/<id>/<id>.json + <id>.png.
    static var userDir: URL { Paths.support.appendingPathComponent("characters", isDirectory: true) }

    private static var metaCache: [String: [String: Any]] = [:]
    private static var scanned: [String]?

    /// Every pet that can be shown: the built-in ones first, then imported ones.
    static var ids: [String] {
        if let s = scanned { return s }
        var out = builtIn.filter { files($0) != nil }
        let dirs = (try? FileManager.default.contentsOfDirectory(at: userDir, includingPropertiesForKeys: nil)) ?? []
        for d in dirs.sorted(by: { $0.lastPathComponent < $1.lastPathComponent }) {
            let id = d.lastPathComponent
            if !out.contains(id), files(id) != nil { out.append(id) }
        }
        scanned = out
        return out
    }
    static func rescan() { scanned = nil; metaCache.removeAll() }

    /// A pet's JSON and its art: a sprite sheet (<id>.png) or a 3D model (<id>.usdz).
    private static func files(_ id: String) -> (json: URL, png: URL)? {
        let fm = FileManager.default
        let user = userDir.appendingPathComponent(id, isDirectory: true)
        let uj = user.appendingPathComponent("\(id).json")
        for ext in ["png", "usdz"] {
            let up = user.appendingPathComponent("\(id).\(ext)")
            if fm.fileExists(atPath: uj.path), fm.fileExists(atPath: up.path) { return (uj, up) }
        }
        guard let j = Bundle.main.url(forResource: id, withExtension: "json") else { return nil }
        if let p = Bundle.main.url(forResource: id, withExtension: "png") ?? Bundle.main.url(forResource: id, withExtension: "usdz") { return (j, p) }
        return nil
    }

    private static func meta(_ id: String) -> [String: Any]? {
        if let m = metaCache[id] { return m }
        guard let f = files(id), let data = try? Data(contentsOf: f.json),
              let m = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
        metaCache[id] = m
        return m
    }

    static func title(_ id: String) -> String {
        if let t = (meta(id)?["persona"] as? [String: Any])?["title"] as? String { return t }
        return ["gloria": "GLORIA · 柔彩", "doro": "Doro", "usagi": "抽象乌萨奇", "grokbot": "GrokBot"][id] ?? (meta(id)?["name"] as? String ?? id)
    }

    static func load(_ id: String) -> Character? {
        guard let f = files(id), let m = meta(id) else { return nil }
        var model: Model3D?
        let sp: Sprites
        if f.png.pathExtension == "usdz" {
            guard let md = Model3D(url: f.png) else { return nil }
            model = md
            sp = Sprites(blankRows: 24, top: (m["top"] as? Int) ?? 36)
        } else {
            guard let s = Sprites(url: f.png) else { return nil }
            sp = s
        }
        var specs: [Anim: AnimSpec] = [:]
        for (k, v) in m["anims"] as? [String: [String: Any]] ?? [:] {
            guard let a = Anim(rawValue: k), let row = v["row"] as? Int, let n = v["count"] as? Int, n > 0 else { continue }
            let ms = v["frameMs"]
            let d = (ms as? [NSNumber])?.map { $0.doubleValue / 1000 }
            let ft = ((ms as? NSNumber)?.doubleValue ?? 120) / 1000
            specs[a] = AnimSpec(row: row, count: n, frameTime: d.map { $0.reduce(0, +) / Double($0.count) } ?? ft,
                                durations: d?.count == n ? d : nil, loop: v["loop"] as? Bool ?? false, sub: max(1, v["sub"] as? Int ?? 1),
                                facing: v["facing"] as? String)
        }
        guard specs[.idle] != nil else { return nil }
        func num(_ k: String, _ d: Double) -> CGFloat { CGFloat((m[k] as? NSNumber)?.doubleValue ?? d) }
        let g = (m["grab"] as? [NSNumber]).map { CGPoint(x: $0[0].doubleValue, y: $0[1].doubleValue) } ?? CGPoint(x: 96, y: 40)
        var (name, tag, mul, lines) = persona(id)
        if let p = m["persona"] as? [String: Any] {
            // Imported pets bring their own name and lines.
            name = m["name"] as? String ?? name
            tag = p["tag"] as? String ?? tag
            mul = CGFloat((p["scale"] as? NSNumber)?.doubleValue ?? Double(mul))
            func list(_ k: String, _ d: [String]) -> [String] { let v = (p[k] as? [String]) ?? []; return v.isEmpty ? d : v }
            lines = Lines(greeting: p["greeting"] as? String ?? lines.greeting, pats: list("pats", lines.pats), wake: list("wake", lines.wake),
                          ouch: list("ouch", lines.ouch), angry: list("angry", lines.angry), hello: list("hello", lines.hello))
        }
        return Character(id: id, name: name, tag: tag, sprites: sp, specs: specs, feetY: num("feetY", 202), seatY: num("seatY", 149),
                         grab: g, scaleMul: mul * num("scale", 1), lines: lines, model: model)
    }

    private static func persona(_ id: String) -> (String, String, CGFloat, Lines) {
        switch id {
        case "doro":
            return ("Doro", "DORO", 1.15, Lines(greeting: "Doro～！欧润吉在哪里？",
                pats: ["Doro～", "欧润吉！", "嘿嘿嘿", "Doro Doro～", "要欧润吉！"],
                wake: ["Doro？", "欧润吉……梦到欧润吉了……"],
                ouch: ["呜哇——", "Doro 晕了……", "欧润吉掉了！"],
                angry: ["Doro 生气了！", "不给欧润吉就别戳！", "哼——"],
                hello: ["Doro～", "分你一个欧润吉！", "嘿！"]))
        case "usagi":
            return ("乌萨奇", "USAGI", 1.0, Lines(greeting: "呀哈——！",
                pats: ["呀哈！", "乌拉！", "哈？", "噗噜噜噜噜——", "呼嗯。"],
                wake: ["呀哈？！", "哈？"],
                ouch: ["乌拉——！", "噗噜噜……", "哈？？"],
                angry: ["哈？？？", "乌拉！！", "噗噜噜噜噜噜！"],
                hello: ["呀哈！", "乌拉～", "哈！"]))
        case "grokbot":
            return ("GrokBot", "GROKBOT", 1.0, Lines(greeting: "哔——GrokBot 上线！耳朵天线已展开。",
                pats: ["哔哔～", "收到！", "嘀——好感度 +1", "（耳朵转了转）", "扫描到主人，开心。"],
                wake: ["系统唤醒……哔！", "刚才在省电模式～"],
                ouch: ["警告：姿态异常！", "陀螺仪晕了……", "哔——撞到了！"],
                angry: ["过载了！别戳！", "哔！哔！哔！", "进入防御模式。"],
                hello: ["哔～你好！", "信号已连接。", "嘀嘀！"]))
        default:
            return ("GLORIA", "GLORIA", 1.0, Lines(greeting: "嗨～我是 GLORIA！把我拖到任何窗口上，我会坐在上面陪你。",
                pats: ["嘿嘿～", "摸摸头！", "干嘛呀～", "我在呢。", "今天也一起加油！", "要不要放首歌？我想跳舞。"],
                wake: ["唔……我没睡！", "啊，你回来啦～", "眯了一会儿……"],
                ouch: ["哎呦……", "头好晕～", "下次轻一点嘛！"],
                angry: ["别戳啦！", "再戳我就生气了！", "哼！"],
                hello: ["嗨～", "一起加油！", "你也在呀～"]))
        }
    }

    /// Menu bar icon: the top of the idle frame (the face).
    func icon(size: CGFloat) -> NSImage? {
        if let model {
            // The face of the 3D model: render the resting pose and crop around the round body.
            guard let img = model.snapshot(GrokPoses.pose(.idle, 0, look: 90), size: CGSize(width: Sprites.cellW * 2, height: Sprites.cellH * 2)),
                  let c = img.cropping(to: CGRect(x: 48 * 2, y: 88 * 2, width: 96 * 2, height: 90 * 2)) else { return nil }
            return NSImage(cgImage: c, size: NSSize(width: size * 96 / 90, height: size))
        }
        guard let img = sprites.image(cell(.idle, 0)) else { return nil }
        let top = CGFloat(sprites.top(cell(.idle, 0)))
        let h = id == "usagi" ? 100.0 : (feetY - top) * 0.62
        let y = id == "usagi" ? top + 22 : top
        let w = h * 1.08
        guard let c = img.cropping(to: CGRect(x: 96 - w / 2, y: y, width: w, height: h)) else { return nil }
        return NSImage(cgImage: c, size: NSSize(width: size * w / h, height: size))
    }
}
