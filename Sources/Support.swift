import AppKit

// Shared helpers, paths and saved preferences.

func clamp<T: Comparable>(_ v: T, _ lo: T, _ hi: T) -> T { min(max(v, lo), hi) }
func lerp(_ a: CGFloat, _ b: CGFloat, _ t: CGFloat) -> CGFloat { a + (b - a) * t }
func now() -> Double { CACurrentMediaTime() }

func cap(_ s: String, _ n: Int) -> String {
    let t = s.replacingOccurrences(of: "\n", with: " ").trimmingCharacters(in: .whitespaces)
    return t.count > n ? String(t.prefix(n - 1)) + "…" : t
}

enum Paths {
    static var support: URL {
        let u = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/GLORIA", isDirectory: true)
        try? FileManager.default.createDirectory(at: u, withIntermediateDirectories: true)
        return u
    }
    static var socket: String { support.appendingPathComponent("husk.sock").path }
    static var backups: URL {
        let u = support.appendingPathComponent("hook-backups", isDirectory: true)
        try? FileManager.default.createDirectory(at: u, withIntermediateDirectories: true)
        return u
    }
}

enum Prefs {
    private static let d = UserDefaults.standard
    private static func bool(_ k: String, _ def: Bool) -> Bool { d.object(forKey: k) as? Bool ?? def }

    static var scale: Double {
        get { d.object(forKey: "scale") as? Double ?? 0.62 }
        set { d.set(newValue, forKey: "scale") }
    }
    static var dance: Bool { get { bool("dance", true) } set { d.set(newValue, forKey: "dance") } }
    static var beatTap: Bool { get { bool("beatTap", true) } set { d.set(newValue, forKey: "beatTap") } }
    static var browserMusic: Bool { get { bool("browserMusic", true) } set { d.set(newValue, forKey: "browserMusic") } }
    /// Whether her hooks are in this agent's config ("claude", "codex", "cursor", "dsh"). The first two keep their old keys.
    static func husk(_ src: String) -> Bool { bool(huskKey(src), false) }
    static func setHusk(_ src: String, _ on: Bool) { d.set(on, forKey: huskKey(src)); d.synchronize() }
    private static func huskKey(_ src: String) -> String { ["claude": "huskClaude", "codex": "huskCodex"][src] ?? "husk.\(src)" }
    /// Asked once, on the first launch, whether to hook up the agents found on this Mac.
    static var onboarded: Bool { get { bool("onboarded", false) } set { d.set(newValue, forKey: "onboarded") } }
    static var chime: Bool { get { bool("chime", true) } set { d.set(newValue, forKey: "chime") } }
    static var wander: Bool { get { bool("wander", true) } set { d.set(newValue, forKey: "wander") } }
    static var nap: Bool { get { bool("nap", true) } set { d.set(newValue, forKey: "nap") } }
    static var chase: Bool { get { bool("chase", true) } set { d.set(newValue, forKey: "chase") } }
    /// Let go against a screen's side edge, she tucks in behind it and peeks out.
    static var edgePeek: Bool { get { bool("edgePeek", true) } set { d.set(newValue, forKey: "edgePeek") } }
    /// Characters on screen, in order. (Older versions kept a single "character".)
    static var shown: [String] {
        get {
            let v = (d.stringArray(forKey: "shown") ?? [d.string(forKey: "character") ?? "gloria"]).filter { Character.ids.contains($0) }
            return v.isEmpty ? ["gloria"] : v
        }
        set { d.set(newValue, forKey: "shown"); d.synchronize() }
    }
    /// Which character stands in for an agent (see Agents); defaults to the first one on screen.
    static func role(_ source: String) -> String { d.string(forKey: "role.\(source)") ?? shown[0] }
    static func setRole(_ source: String, _ id: String) { d.set(id, forKey: "role.\(source)"); d.synchronize() }
}

/// The coding agents a pet can stand in for. Each one calls `GLORIA --hook <id>` from its own hooks.
enum Agents {
    struct Info {
        let id, name, tag: String
        /// Where its hooks live, for the first-launch check ("is it on this Mac?").
        let home: String
    }
    static let all: [Info] = [
        Info(id: "claude", name: "Claude Code", tag: "CLAUDE CODE", home: ".claude"),
        Info(id: "codex", name: "Codex", tag: "CODEX", home: ".codex"),
        Info(id: "cursor", name: "Cursor", tag: "CURSOR", home: ".cursor"),
        Info(id: "dsh", name: "DeepSeek Harness", tag: "DSH", home: ".dsh"),
    ]
    static let ids = all.map(\.id)
    static func info(_ id: String) -> Info { all.first { $0.id == id } ?? all[0] }
    static func name(_ id: String) -> String { info(id).name }
    static func tag(_ id: String) -> String { info(id).tag }
    /// Hooked up, in menu order.
    static var on: [String] { ids.filter { Prefs.husk($0) } }
    /// Installed on this Mac (its config folder exists).
    static func present(_ id: String) -> Bool {
        let home = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(info(id).home)
        return FileManager.default.fileExists(atPath: home.path)
    }
}

// GLORIA 柔彩 palette.
enum Palette {
    static let pink = NSColor(srgbRed: 0.965, green: 0.62, blue: 0.80, alpha: 1)
    static let blush = NSColor(srgbRed: 1.0, green: 0.93, blue: 0.96, alpha: 1)
    static let lavender = NSColor(srgbRed: 0.76, green: 0.70, blue: 0.95, alpha: 1)
    static let periwinkle = NSColor(srgbRed: 0.55, green: 0.66, blue: 0.98, alpha: 1)
    static let cyan = NSColor(srgbRed: 0.45, green: 0.82, blue: 0.96, alpha: 1)
    static let ink = NSColor(srgbRed: 0.29, green: 0.21, blue: 0.33, alpha: 1)
    static let butter = NSColor(srgbRed: 1.0, green: 0.93, blue: 0.62, alpha: 1)
}

final class PetPanel: NSPanel {
    init(size: CGSize) {
        super.init(contentRect: NSRect(origin: .zero, size: size),
                   styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        level = .floating
        collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle, .fullScreenAuxiliary]
        isMovable = false
        hidesOnDeactivate = false
        becomesKeyOnlyIfNeeded = true
        isReleasedWhenClosed = false
        animationBehavior = .none
    }
    // She stands right under the menu bar sometimes; don't let AppKit push her window down.
    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect { frameRect }
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}
