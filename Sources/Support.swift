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
    static var huskClaude: Bool { get { bool("huskClaude", false) } set { d.set(newValue, forKey: "huskClaude") } }
    static var huskCodex: Bool { get { bool("huskCodex", false) } set { d.set(newValue, forKey: "huskCodex") } }
    static var chime: Bool { get { bool("chime", true) } set { d.set(newValue, forKey: "chime") } }
    static var wander: Bool { get { bool("wander", true) } set { d.set(newValue, forKey: "wander") } }
    static var nap: Bool { get { bool("nap", true) } set { d.set(newValue, forKey: "nap") } }
    static var chase: Bool { get { bool("chase", true) } set { d.set(newValue, forKey: "chase") } }
    /// Characters on screen, in order. (Older versions kept a single "character".)
    static var shown: [String] {
        get {
            let v = (d.stringArray(forKey: "shown") ?? [d.string(forKey: "character") ?? "gloria"]).filter { Character.ids.contains($0) }
            return v.isEmpty ? ["gloria"] : v
        }
        set { d.set(newValue, forKey: "shown"); d.synchronize() }
    }
    /// Which character stands in for an agent ("claude"/"codex"); defaults to the first one on screen.
    static func role(_ source: String) -> String { d.string(forKey: "role.\(source)") ?? shown[0] }
    static func setRole(_ source: String, _ id: String) { d.set(id, forKey: "role.\(source)"); d.synchronize() }
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
