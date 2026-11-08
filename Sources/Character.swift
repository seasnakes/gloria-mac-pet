import AppKit

// The three desk pets. Each has a sprite sheet in GLORIA's layout (8 × 15 cells of 192×208, feet on y=202) and its
// own rows, timings and anchors; one is on screen at a time, so animation lookups go through `Character.current`.

struct AnimSpec { let row: Int; let count: Int; let frameTime: Double; let durations: [Double]? }

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
    let greeting: String
    let pats: [String]
    let wake: [String]
    let ouch: [String]
    let angry: [String]

    static var current: Character!

    init(id: String, name: String, tag: String, sprites: Sprites, specs: [Anim: AnimSpec], feetY: CGFloat = 202, seatY: CGFloat = 149,
         grab: CGPoint = CGPoint(x: 96, y: 72), scaleMul: CGFloat = 1, greeting: String, pats: [String], wake: [String], ouch: [String], angry: [String]) {
        self.id = id; self.name = name; self.tag = tag; self.sprites = sprites; self.specs = specs
        self.feetY = feetY; self.seatY = seatY; self.grab = grab; self.scaleMul = scaleMul
        self.greeting = greeting; self.pats = pats; self.wake = wake; self.ouch = ouch; self.angry = angry
    }

    /// How far the seat line sits above the feet line, in cell px.
    var seatRise: CGFloat { feetY - seatY }
    /// From the feet line up to the grab point, in cell px.
    var hang: CGFloat { feetY - grab.y }

    func spec(_ a: Anim) -> AnimSpec { specs[a] ?? specs[.idle]! }

    static let ids = ["gloria", "doro", "usagi"]
    static func title(_ id: String) -> String { ["gloria": "GLORIA · 柔彩", "doro": "Doro", "usagi": "抽象乌萨奇"][id] ?? id }

    static func load(_ id: String) -> Character? {
        switch id {
        case "doro", "usagi": return fromJSON(id)
        default: return gloria()
        }
    }

    private static func gloria() -> Character? {
        guard let url = Bundle.main.url(forResource: "spritesheet", withExtension: "png"), let sp = Sprites(url: url) else { return nil }
        let rows: [(Anim, Int, Int, Double, [Double]?)] = [
            (.idle, 0, 6, 0.12, nil), (.runRight, 1, 8, 0.075, nil), (.runLeft, 2, 8, 0.075, nil), (.wave, 3, 4, 0.14, nil),
            (.jump, 4, 5, 0.1, nil), (.failed, 5, 8, 0.13, nil), (.waiting, 6, 6, 0.19, nil), (.work, 7, 6, 0.17, nil),
            (.review, 8, 6, 0.17, nil),
            (.perch, 11, 6, 0.16, [0.22, 0.18, 0.09, 0.11, 0.09, 0.26]), (.dance, 12, 8, 0.14, nil),
            (.sleep, 13, 6, 0.29, [0.26, 0.22, 0.26, 0.52, 0.26, 0.22]), (.dangle, 14, 4, 0.12, nil),
        ]
        var specs: [Anim: AnimSpec] = [:]
        for (a, r, n, ft, d) in rows { specs[a] = AnimSpec(row: r, count: n, frameTime: ft, durations: d) }
        return Character(id: "gloria", name: "GLORIA", tag: "GLORIA", sprites: sp, specs: specs, seatY: 149, grab: CGPoint(x: 96, y: 72),
                         greeting: "嗨～我是 GLORIA！把我拖到任何窗口上，我会坐在上面陪你。",
                         pats: ["嘿嘿～", "摸摸头！", "干嘛呀～", "我在呢。", "今天也一起加油！", "要不要放首歌？我想跳舞。"],
                         wake: ["唔……我没睡！", "啊，你回来啦～", "眯了一会儿……"],
                         ouch: ["哎呦……", "头好晕～", "下次轻一点嘛！"],
                         angry: ["别戳啦！", "再戳我就生气了！", "哼！"])
    }

    private static func fromJSON(_ id: String) -> Character? {
        guard let j = Bundle.main.url(forResource: id, withExtension: "json"),
              let data = try? Data(contentsOf: j),
              let m = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let png = Bundle.main.url(forResource: id, withExtension: "png"), let sp = Sprites(url: png) else { return nil }
        let names: [String: Anim] = ["idle": .idle, "runRight": .runRight, "runLeft": .runLeft, "wave": .wave, "jump": .jump,
                                     "failed": .failed, "waiting": .waiting, "work": .work, "review": .review, "perch": .perch,
                                     "dance": .dance, "sleep": .sleep, "dangle": .dangle]
        var specs: [Anim: AnimSpec] = [:]
        for (k, v) in m["anims"] as? [String: [String: Any]] ?? [:] {
            guard let a = names[k], let row = v["row"] as? Int, let n = v["count"] as? Int else { continue }
            let ms = v["frameMs"]
            let d = (ms as? [NSNumber])?.map { $0.doubleValue / 1000 }
            let ft = (ms as? NSNumber)?.doubleValue ?? 120
            specs[a] = AnimSpec(row: row, count: n, frameTime: d.map { $0.reduce(0, +) / Double($0.count) } ?? ft / 1000, durations: d)
        }
        guard specs[.idle] != nil else { return nil }
        func num(_ k: String, _ d: Double) -> CGFloat { CGFloat((m[k] as? NSNumber)?.doubleValue ?? d) }
        let g = (m["grab"] as? [NSNumber]).map { CGPoint(x: $0[0].doubleValue, y: $0[1].doubleValue) } ?? CGPoint(x: 96, y: 40)
        if id == "doro" {
            return Character(id: id, name: "Doro", tag: "DORO", sprites: sp, specs: specs, feetY: num("feetY", 202), seatY: num("seatY", 188),
                             grab: g, scaleMul: num("scale", 1.15),
                             greeting: "Doro～！欧润吉在哪里？",
                             pats: ["Doro～", "欧润吉！", "嘿嘿嘿", "Doro Doro～", "要欧润吉！"],
                             wake: ["Doro？", "欧润吉……梦到欧润吉了……"],
                             ouch: ["呜哇——", "Doro 晕了……", "欧润吉掉了！"],
                             angry: ["Doro 生气了！", "不给欧润吉就别戳！", "哼——"])
        }
        return Character(id: id, name: "乌萨奇", tag: "USAGI", sprites: sp, specs: specs, feetY: num("feetY", 202), seatY: num("seatY", 149),
                         grab: g, scaleMul: num("scale", 1.0),
                         greeting: "呀哈——！",
                         pats: ["呀哈！", "乌拉！", "哈？", "噗噜噜噜噜——", "呼嗯。"],
                         wake: ["呀哈？！", "哈？"],
                         ouch: ["乌拉——！", "噗噜噜……", "哈？？"],
                         angry: ["哈？？？", "乌拉！！", "噗噜噜噜噜噜！"])
    }

    /// Menu bar icon: the top of the idle frame (the face).
    func icon(size: CGFloat) -> NSImage? {
        guard let img = sprites.image(Anim.idle.cell(0)) else { return nil }
        let top = CGFloat(sprites.top(Anim.idle.cell(0)))
        let h = id == "usagi" ? 100.0 : (feetY - top) * 0.62
        let y = id == "usagi" ? top + 22 : top
        let w = h * 1.08
        guard let c = img.cropping(to: CGRect(x: 96 - w / 2, y: y, width: w, height: h)) else { return nil }
        return NSImage(cgImage: c, size: NSSize(width: size * w / h, height: size))
    }
}
