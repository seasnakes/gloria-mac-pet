import AppKit

// Offline renders, for tools/export_dsh.py and for checking the 3D pet frame by frame:
//
//   GLORIA --render-sheet <id> <out.png> [scale]
//
// writes the pet's whole sheet in GLORIA's layout (8 columns of 192×208 cells, look directions on rows 9–10), `scale`
// times larger. A sprite pet's sheet is resampled; a 3D pet is rendered pose by pose, so it stays sharp at any scale.

enum Export {
    static func sheet(_ args: [String]) -> Int32 {
        guard args.count >= 2 else {
            FileHandle.standardError.write("用法：GLORIA --render-sheet <角色 id> <输出.png> [倍数]\n".data(using: .utf8)!)
            return 2
        }
        let id = args[0], out = URL(fileURLWithPath: args[1])
        let k = max(0.25, min(8, args.count > 2 ? Double(args[2]) ?? 1 : 1))
        guard let ch = Character.load(id) else {
            FileHandle.standardError.write("没有这个角色：\(id)（可选：\(Character.ids.joined(separator: " "))）\n".data(using: .utf8)!)
            return 1
        }
        let cw = Int((Double(Sprites.cellW) * k).rounded()), chh = Int((Double(Sprites.cellH) * k).rounded())
        var cells: [Cell: CGImage] = [:]
        if let model = ch.model {
            let size = CGSize(width: cw, height: chh)
            for a in Anim.allCases where ch.has(a) {
                for i in 0..<ch.count(a) {
                    let c = ch.cell(a, i)
                    if cells[c] == nil, let img = model.snapshot(GrokPoses.pose(a, i, look: 0), size: size) { cells[c] = img }
                }
            }
            for d in 0..<16 {
                let c = Cell(row: 9 + d / 8, col: d % 8)
                if let img = model.snapshot(GrokPoses.pose(nil, 0, look: Double(d) * 22.5), size: size) { cells[c] = img }
            }
        } else {
            for r in 0..<ch.sprites.rows { for col in 0..<8 { let c = Cell(row: r, col: col); if ch.sprites.has(c), let img = ch.sprites.image(c) { cells[c] = img } } }
        }
        let rows = (cells.keys.map(\.row).max() ?? 0) + 1
        guard let ctx = CGContext(data: nil, width: cw * 8, height: chh * rows, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return 1 }
        ctx.interpolationQuality = .high
        for (c, img) in cells {
            ctx.draw(img, in: CGRect(x: c.col * cw, y: (rows - 1 - c.row) * chh, width: cw, height: chh))
        }
        guard let sheet = ctx.makeImage(), let png = NSBitmapImageRep(cgImage: sheet).representation(using: .png, properties: [:]) else { return 1 }
        do { try png.write(to: out) } catch { FileHandle.standardError.write("写不进 \(out.path)：\(error)\n".data(using: .utf8)!); return 1 }
        print("\(out.path)  \(cw * 8)×\(chh * rows)  \(cells.count) 格")
        return 0
    }
}
