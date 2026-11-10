import AppKit

// Recording her for videos, without screen-recording permission: every visible panel she owns (each pet, its
// subagent clones, speech bubbles, flying envelopes, click ripples) is drawn from its own layer into a PNG, and its
// frame is logged. A compositor lays them over a staged desktop.
//
//   --ctl '{"ctl":"stage","wins":[{"id":901,"owner":"Terminal","rect":[x,y,w,h]}, …]}'   fake windows, front to back
//   --ctl '{"ctl":"stage","off":true}'                                                   back to the real ones
//   --ctl '{"ctl":"record","path":"/dir","seconds":12,"fps":30,"scale":2}'
//
// Output: <dir>/NNNNN_<k>.png and <dir>/index.jsonl, one line per frame:
//   {"i":n,"t":seconds,"items":[{"kind":"pet|clone|bubble|courier|fx","id":…,"file":…,"frame":[x,y,w,h]}],"wins":[…]}
// Frames are in Cocoa screen points (origin bottom-left of the primary screen, y up).

final class Recorder {
    static var active: Recorder?

    private let dir: URL
    private let scale: CGFloat
    private let pets: () -> [Pet]
    private var timer: Timer?
    private let t0 = now()
    private let until: Double
    private var n = 0
    private let io = DispatchQueue(label: "studio.seasnake.gloria.record", qos: .userInitiated)
    private let index: FileHandle

    init?(dir: URL, seconds: Double, fps: Double, scale: CGFloat, pets: @escaping () -> [Pet]) {
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let ix = dir.appendingPathComponent("index.jsonl")
        FileManager.default.createFile(atPath: ix.path, contents: nil)
        guard let fh = try? FileHandle(forWritingTo: ix) else { return nil }
        self.dir = dir; self.scale = scale; self.pets = pets; index = fh
        until = t0 + seconds
        let tm = Timer(timeInterval: 1 / max(1, fps), repeats: true) { [weak self] _ in self?.tick() }
        RunLoop.main.add(tm, forMode: .common)
        timer = tm
    }

    var status: [String: Any] { ["frames": n, "elapsed": now() - t0, "dir": dir.path] }

    func stop() {
        timer?.invalidate(); timer = nil
        io.sync { try? index.close() }
        if Recorder.active === self { Recorder.active = nil }
    }

    private func image(_ layer: CALayer?, _ size: CGSize) -> CGImage? {
        guard let layer, size.width >= 1, size.height >= 1 else { return nil }
        let W = Int(size.width * scale), H = Int(size.height * scale)
        guard let ctx = CGContext(data: nil, width: W, height: H, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.scaleBy(x: scale, y: scale)
        layer.render(in: ctx)
        return ctx.makeImage()
    }

    private func viewImage(_ v: NSView) -> CGImage? {
        guard let rep = v.bitmapImageRepForCachingDisplay(in: v.bounds) else { return nil }
        v.cacheDisplay(in: v.bounds, to: rep)
        return rep.cgImage
    }

    private func tick() {
        let t = now()
        if t > until { stop(); return }
        var items: [[String: Any]] = []
        var shots: [(String, CGImage)] = []
        func add(_ kind: String, _ id: String, _ frame: CGRect, _ img: CGImage?, extra: [String: Any] = [:]) {
            guard let img else { return }
            let file = String(format: "%05d_%d.png", n, shots.count)
            shots.append((file, img))
            var d: [String: Any] = ["kind": kind, "id": id, "file": file,
                                    "frame": [frame.minX, frame.minY, frame.width, frame.height].map { Double($0) }]
            for (k, v) in extra { d[k] = v }
            items.append(d)
        }
        for p in pets() {
            let b = p.body
            if b.panel.isVisible && b.panel.alphaValue > 0.02 {
                b.beginCapture()
                add("pet", p.ch.id, b.panel.frame, image(b.view.layer, b.panel.frame.size), extra: ["alpha": Double(b.panel.alphaValue)])
                b.endCapture()
            }
            for (k, c) in p.cloneBodies.enumerated() where c.panel.isVisible && c.panel.alphaValue > 0.02 {
                c.beginCapture()
                add("clone", "\(p.ch.id)#\(k)", c.panel.frame, image(c.view.layer, c.panel.frame.size), extra: ["alpha": Double(c.panel.alphaValue)])
                c.endCapture()
            }
            if p.bubble.visible && p.bubble.panel.alphaValue > 0.02 {
                add("bubble", p.ch.id, p.bubble.panel.frame, viewImage(p.bubble.view),
                    extra: ["alpha": Double(p.bubble.panel.alphaValue), "text": p.bubble.text])
            }
        }
        for (k, c) in Courier.inFlight.enumerated() where c.panel.isVisible {
            add("courier", "\(k)", c.panel.frame, image(c.layer?.presentation() ?? c.layer, c.panel.frame.size))
        }
        for (k, e) in Effects.panels.enumerated() where e.panel.isVisible {
            add("fx", "\(k)", e.panel.frame, image(e.layer?.presentation() ?? e.layer, e.panel.frame.size))
        }
        let wins = (Desktop.staged ?? []).map { ["id": Int($0.id), "owner": $0.owner, "rect": [$0.rect.minX, $0.rect.minY, $0.rect.width, $0.rect.height].map { Double($0) }] as [String: Any] }
        let line: [String: Any] = ["i": n, "t": t - t0, "items": items, "wins": wins]
        n += 1
        let d = dir, fh = index
        io.async {
            for (file, img) in shots {
                if let png = NSBitmapImageRep(cgImage: img).representation(using: .png, properties: [:]) {
                    try? png.write(to: d.appendingPathComponent(file))
                }
            }
            if var data = try? JSONSerialization.data(withJSONObject: line) { data.append(0x0A); fh.write(data) }
        }
    }
}
