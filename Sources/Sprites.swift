import AppKit

// A character's sheet: 8 columns of 192×208 cells, any number of rows. Rows 9–10 are always the 16 look directions
// (0° = up, clockwise); everything else is described by the character's JSON (see Character.swift).

struct Cell: Hashable { let row: Int; let col: Int }

final class Sprites {
    static let cellW = 192, cellH = 208
    static let footX: CGFloat = 96          // feet centre, px from the cell's left edge
    static let footUp: CGFloat = 6          // feet contact line y=202, i.e. 6 px above the cell's bottom edge

    private(set) var images: [Cell: CGImage] = [:]
    private var masks: [Cell: [UInt8]] = [:]
    private var tops: [Cell: Int] = [:]
    private(set) var rows = 0
    private var fixedTop: Int?

    /// No pictures: a 3D pet keeps the same row layout for its state machine and draws itself.
    init(blankRows: Int, top: Int) {
        rows = blankRows
        fixedTop = top
    }

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

    func has(_ c: Cell) -> Bool { images[c] != nil }

    /// Half the width of the drawing around the feet centre (cell px), ignoring wispy edge pixels.
    func halfWidth(_ c: Cell) -> Int {
        guard let m = masks[c] else { return 50 }
        var r = 0
        for y in 0..<Sprites.cellH {
            for x in 0..<Sprites.cellW where m[y * Sprites.cellW + x] > 120 { r = max(r, abs(x - Int(Sprites.footX))) }
        }
        return r
    }

    /// Top of the drawing in a cell (px from the cell's top), for placing the speech bubble.
    func top(_ c: Cell) -> Int { tops[c] ?? fixedTop ?? 6 }

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
