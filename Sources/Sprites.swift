import AppKit

// The approved GLORIA 柔彩 Q sheet: 8 columns × 11 rows of 192×208 cells.
// Rows 0–8 are animation states, rows 9–10 are 16 look directions (0° = up, clockwise).

struct Cell: Hashable { let row: Int; let col: Int }

enum Anim: Hashable {
    case idle, runRight, runLeft, wave, jump, failed, waiting, work, review
    // Rows 11–14: sitting on an edge, dance, nap, held up.
    case perch, dance, sleep, dangle

    // Rows, counts and timings come from the character on screen (see Character.swift).
    private var spec: AnimSpec { Character.current.spec(self) }
    var row: Int { spec.row }
    var count: Int { spec.count }
    var frameTime: Double { spec.frameTime }
    /// Per-frame durations; a uniform loop when the artist didn't set them.
    var durations: [Double] { spec.durations ?? Array(repeating: spec.frameTime, count: spec.count) }
    /// How far (cell px) the sprite drops when sitting so the seat line lands on the edge.
    var seatRise: CGFloat { self == .perch ? Character.current.seatRise : 0 }

    func cell(_ i: Int) -> Cell { Cell(row: row, col: ((i % count) + count) % count) }

    /// Frame index at time `t` into a loop that uses the per-frame durations.
    func index(at t: Double, speed: Double = 1) -> Int {
        let d = durations
        let total = d.reduce(0, +) * speed
        var u = t.truncatingRemainder(dividingBy: max(total, 0.001))
        for (i, x) in d.enumerated() { u -= x * speed; if u < 0 { return i } }
        return count - 1
    }
}

final class Sprites {
    static let cellW = 192, cellH = 208
    static let footX: CGFloat = 96          // feet centre, px from the cell's left edge
    static let footUp: CGFloat = 6          // feet contact line y=202, i.e. 6 px above the cell's bottom edge
    static let neutral = Cell(row: 0, col: 6)

    private(set) var images: [Cell: CGImage] = [:]
    private var masks: [Cell: [UInt8]] = [:]
    private var tops: [Cell: Int] = [:]
    private(set) var rows = 0

    init?(url: URL) {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil),
              let sheet = CGImageSourceCreateImageAtIndex(src, 0, nil) else { return nil }
        let w = Sprites.cellW, h = Sprites.cellH
        rows = sheet.height / h
        for r in 0..<rows {
            for c in 0..<8 {
                guard let img = sheet.cropping(to: CGRect(x: c * w, y: r * h, width: w, height: h)) else { continue }
                let mask = Sprites.alphaMask(img)
                if let first = mask.firstIndex(where: { $0 > 40 }) {
                    images[Cell(row: r, col: c)] = img
                    masks[Cell(row: r, col: c)] = mask
                    tops[Cell(row: r, col: c)] = first / w
                }
            }
        }
        if images.isEmpty { return nil }
    }

    /// Whether the sheet has the extra rows (perch, dance, sleep, dangle).
    var hasExtras: Bool { images[Anim.dangle.cell(0)] != nil }

    /// Top of the drawing in a cell (px from the cell's top), for placing the speech bubble.
    func top(_ c: Cell) -> Int { tops[c] ?? 6 }

    func image(_ c: Cell) -> CGImage? { images[c] ?? images[Cell(row: 0, col: 0)] }

    /// Alpha at a pixel, y measured from the top of the cell.
    func alpha(_ c: Cell, x: Int, yTop: Int) -> UInt8 {
        guard x >= 0, yTop >= 0, x < Sprites.cellW, yTop < Sprites.cellH, let m = masks[c] else { return 0 }
        return m[yTop * Sprites.cellW + x]
    }

    /// Look frame for a direction in degrees (0 = up, 90 = right, 180 = down, 270 = left).
    func look(_ degrees: Double) -> Cell {
        var d = degrees.truncatingRemainder(dividingBy: 360)
        if d < 0 { d += 360 }
        let i = Int((d / 22.5).rounded()) % 16
        return Cell(row: 9 + i / 8, col: i % 8)
    }

    /// The head crop of the neutral frame, for the menu bar.
    func icon(size: CGFloat) -> NSImage? {
        guard let img = images[Sprites.neutral]?.cropping(to: CGRect(x: 40, y: 2, width: 112, height: 104)) else { return nil }
        let ns = NSImage(cgImage: img, size: NSSize(width: size * 112 / 104, height: size))
        return ns
    }

    private static func alphaMask(_ img: CGImage) -> [UInt8] {
        let w = img.width, h = img.height
        var rgba = [UInt8](repeating: 0, count: w * h * 4)
        rgba.withUnsafeMutableBytes { buf in
            guard let ctx = CGContext(data: buf.baseAddress, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                                      space: CGColorSpaceCreateDeviceRGB(),
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return }
            ctx.draw(img, in: CGRect(x: 0, y: 0, width: w, height: h))
        }
        var out = [UInt8](repeating: 0, count: w * h)
        for i in 0..<(w * h) { out[i] = rgba[i * 4 + 3] }
        return out   // row 0 = top of the image
    }
}
