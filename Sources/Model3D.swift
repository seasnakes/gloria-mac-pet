import AppKit
import Metal
import QuartzCore
import SceneKit
import simd

// A pet that is a real 3D model: a USDZ scene drawn live by SceneKit into a Metal layer inside her window, every frame,
// posed from the same state machine as the sprite pets. The sprite pets pick a cell; a 3D pet turns that cell into a pose
// (which part moves where), eases toward it, and renders. Looking at the pointer uses the exact angle, not 16 frames.
//
// GrokBot comes from the author's Blender project (characters/grokbot/export_grokbot.py → grokbot.usdz). Inside the
// exported `root` node the parts keep Blender's axes (Z up, the face toward −Y), so poses are written in those terms.

/// One movable part: offset from its rest position, extra rotation (degrees, Blender XYZ), scale.
struct Part3D {
    var at = SIMD3<Double>(0, 0, 0)
    var rot = SIMD3<Double>(0, 0, 0)
    var scale = 1.0
    mutating func approach(_ t: Part3D, _ k: Double) {
        at += (t.at - at) * k; rot += (t.rot - rot) * k; scale += (t.scale - scale) * k
    }
}

/// Where every part of the robot is in one moment.
struct Pose3D {
    var rot = SIMD3<Double>(0, 0, 0)          // whole body, degrees, about the core
    var move = SIMD2<Double>(0, 0)            // x, z
    var squash = 0.0                          // + flatter, − taller
    var earL = Part3D(), earR = Part3D(), finL = Part3D(), finR = Part3D()
    var glance = SIMD2<Double>(0, 0)          // eyes across the visor
    var blink = 0.0
    var face = "neutral"
    var ring = true
    var ringScale = 1.0, ringGlow = 1.0
    var cursor: SIMD4<Double>?                // x, y, z, size
    var keys: Set<Int>?                       // the holo keyboard, with these keys lit
    var ripple: SIMD4<Double>?
    var shutter: SIMD4<Double>?

    /// Ease toward another pose (k = 0…1 of the way); the face and props switch at once.
    mutating func approach(_ t: Pose3D, _ k: Double) {
        rot += (t.rot - rot) * k; move += (t.move - move) * k; squash += (t.squash - squash) * k
        earL.approach(t.earL, k); earR.approach(t.earR, k); finL.approach(t.finL, k); finR.approach(t.finR, k)
        glance += (t.glance - glance) * k; blink += (t.blink - blink) * min(1, k * 2.5)
        ringScale += (t.ringScale - ringScale) * k; ringGlow += (t.ringGlow - ringGlow) * k
        if let c = t.cursor, let o = cursor { cursor = o + (c - o) * min(1, k * 1.6) } else { cursor = t.cursor }
        face = t.face; ring = t.ring; keys = t.keys; ripple = t.ripple; shutter = t.shutter
    }
}

final class Model3D {
    static let device = MTLCreateSystemDefaultDevice()
    private static let queue = device?.makeCommandQueue()

    let scene: SCNScene
    private let renderer: SCNRenderer
    private let camera = SCNNode()
    private let bot, earL, earR, finL, finR, eye, ring, cursor: SCNNode
    private var faces: [String: SCNNode] = [:]
    private var base: [ObjectIdentifier: (pos: SIMD3<Float>, rot: SIMD3<Double>, scale: SIMD3<Float>)] = [:]
    private let glow: SCNMaterial?
    private let keyboard = SCNNode()
    private var keys: [SCNNode] = []
    private let keyOn = Model3D.glowing(NSColor(srgbRed: 0.9, green: 1, blue: 1, alpha: 1))
    private let keyOff = Model3D.glowing(NSColor(srgbRed: 0.35, green: 0.85, blue: 1, alpha: 1))
    private let ripple = SCNNode()
    private let flash = SCNNode()
    private var targets: [String: (color: MTLTexture, depth: MTLTexture)] = [:]
    private static let core = SIMD3<Double>(0, 0, 1.17)
    /// Centre of the black visor sphere (Core) in Bot_root's space, and the eyes' resting turn. The LED faces are curved
    /// panels hugging that sphere, so a glance turns them about its centre; sliding them sideways sank one into the glass.
    private let visor: SIMD3<Float>
    private let eyeRest: simd_quatf

    init?(url: URL) {
        guard let dev = Model3D.device, let sc = try? SCNScene(url: url, options: [.convertToYUp: true]) else { return nil }
        sc.background.contents = NSColor.clear
        sc.rootNode.childNode(withName: "env_light", recursively: true)?.removeFromParentNode()
        func n(_ name: String) -> SCNNode? { sc.rootNode.childNode(withName: name, recursively: true) }
        guard let root = n("root"), let b = n("Bot_root"), let el = n("EarPivot_1"), let er = n("EarPivot1"),
              let fl = n("FinPivot_1"), let fr = n("FinPivot1"), let e = n("EyePivot"), let r = n("HoverRing"), let c = n("Cursor") else { return nil }
        var fs: [String: SCNNode] = [:]
        for f in ["angry", "bars", "happy", "love", "neutral", "sad", "sleepy", "star", "surprised"] { fs[f] = n("E_Eyes_" + f) }
        var g: SCNMaterial?
        r.enumerateHierarchy { node, _ in if let m = node.geometry?.firstMaterial { g = m } }
        var bs: [ObjectIdentifier: (pos: SIMD3<Float>, rot: SIMD3<Double>, scale: SIMD3<Float>)] = [:]
        for node in [b, el, er, fl, fr, e, r, c] {
            let a = node.eulerAngles
            bs[ObjectIdentifier(node)] = (node.simdPosition, SIMD3(Double(a.x), Double(a.y), Double(a.z)), node.simdScale)
        }
        var vc = SIMD3<Float>(0, 0, 1)
        if let sphere = n("Core")?.childNodes.first(where: { $0.geometry != nil }) ?? n("Core") {
            let (lo, hi) = sphere.boundingBox
            vc = b.simdConvertPosition((SIMD3<Float>(lo) + SIMD3<Float>(hi)) / 2, from: sphere)
        }
        visor = vc; eyeRest = e.simdOrientation
        scene = sc
        bot = b; earL = el; earR = er; finL = fl; finR = fr; eye = e; ring = r; cursor = c
        faces = fs; glow = g; base = bs
        renderer = SCNRenderer(device: dev, options: nil)
        renderer.scene = scene
        renderer.autoenablesDefaultLighting = false
        Model3D.addProps(to: b, root: root, keyboard: keyboard, keys: &keys, keyOff: keyOff, ripple: ripple, flash: flash)

        // Orthographic, a little from above, framed like the sprite cells: the ground under the ring lands on (96, 202).
        let cam = SCNCamera()
        cam.usesOrthographicProjection = true
        cam.orthographicScale = 1.81
        cam.zNear = 0.1; cam.zFar = 60
        camera.camera = cam
        let aim = SCNVector3(0, 1.72, 0), pitch = 8.0 * Double.pi / 180
        camera.position = SCNVector3(0, 1.72 + 12 * sin(pitch), -12 * cos(pitch))
        camera.look(at: aim, up: SCNVector3(0, 1, 0), localFront: SCNVector3(0, 0, -1))
        scene.rootNode.addChildNode(camera)
        renderer.pointOfView = camera
        let W = CGFloat(Sprites.cellW), H = CGFloat(Sprites.cellH)
        let unit = 3.62 / Double(H)
        // Where the ground under the ring lands in a cell; slide the camera so it sits on the feet line (96, 202).
        let pv = Model3D.project(SIMD3(0, 0.06, 0), camera: camera, size: CGSize(width: W, height: H))
        let dx = Double(pv.x - W / 2), dy = Double(pv.y - (H - Sprites.footUp))
        camera.simdPosition += camera.simdWorldRight * Float(dx * unit) - camera.simdWorldUp * Float(dy * unit)

        func light(_ t: SCNLight.LightType, _ i: CGFloat, _ p: SCNVector3) {
            let l = SCNNode(); l.light = SCNLight(); l.light!.type = t; l.light!.intensity = i
            l.position = p; l.look(at: aim); scene.rootNode.addChildNode(l)
        }
        light(.directional, 1000, SCNVector3(4, 5, -5))
        light(.directional, 420, SCNVector3(-5, 2.5, -4))
        light(.directional, 600, SCNVector3(0, 4, 4))
        light(.ambient, 260, SCNVector3(0, 0, 0))
    }

    /// Screen point (top-left origin, cell units) of a world point for this orthographic camera.
    private static func project(_ w: SIMD3<Float>, camera: SCNNode, size: CGSize) -> CGPoint {
        let v = camera.simdConvertPosition(w, from: nil)          // camera space: x right, y up, −z forward
        let half = Float(camera.camera!.orthographicScale)
        let aspect = Float(size.width / size.height)
        let nx = v.x / (half * aspect), ny = v.y / half
        return CGPoint(x: CGFloat((nx + 1) / 2) * size.width, y: CGFloat((1 - ny) / 2) * size.height)
    }

    private static func glowing(_ c: NSColor, alpha: CGFloat = 1) -> SCNMaterial {
        let m = SCNMaterial()
        m.lightingModel = .constant
        m.diffuse.contents = c
        m.transparency = alpha
        m.isDoubleSided = true
        m.writesToDepthBuffer = alpha >= 1
        return m
    }

    /// Blender XYZ Euler (degrees) → quaternion (X first, then Y, then Z).
    private static func quat(_ r: SIMD3<Double>) -> simd_quatf {
        let qx = simd_quatf(angle: Float(r.x * .pi / 180), axis: [1, 0, 0])
        let qy = simd_quatf(angle: Float(r.y * .pi / 180), axis: [0, 1, 0])
        let qz = simd_quatf(angle: Float(r.z * .pi / 180), axis: [0, 0, 1])
        return qz * qy * qx
    }

    private static func addProps(to bot: SCNNode, root: SCNNode, keyboard: SCNNode, keys: inout [SCNNode], keyOff: SCNMaterial,
                                 ripple: SCNNode, flash: SCNNode) {
        // Holographic keyboard: a faint pane with three rows of glowing keys, tilted toward her.
        let pane = SCNPlane(width: 1.08, height: 0.36)
        pane.firstMaterial = glowing(NSColor(srgbRed: 0.1, green: 0.45, blue: 0.8, alpha: 1), alpha: 0.45)
        keyboard.addChildNode(SCNNode(geometry: pane))
        for row in 0..<3 {
            let n = row == 2 ? 7 : 9
            for col in 0..<n {
                let k = SCNNode(geometry: SCNBox(width: 0.085, height: 0.07, length: 0.012, chamferRadius: 0.006))
                k.geometry?.firstMaterial = keyOff
                k.simdPosition = [Float(Double(col) - Double(n - 1) / 2) * 0.108, Float(1 - row) * 0.11, 0.02]
                keyboard.addChildNode(k)
                keys.append(k)
            }
        }
        keyboard.simdPosition = [0, -0.95, 0.3]
        keyboard.simdOrientation = quat([62, 0, 0])
        keyboard.isHidden = true
        bot.addChildNode(keyboard)
        // Click ripple: a glowing ring facing her (an SCNTorus already stands in Blender's XZ plane here).
        let torus = SCNTorus(ringRadius: 0.2, pipeRadius: 0.018)
        torus.firstMaterial = glowing(NSColor(srgbRed: 0.5, green: 0.95, blue: 1, alpha: 1), alpha: 0.9)
        ripple.geometry = torus
        ripple.isHidden = true
        root.addChildNode(ripple)
        // Shutter sparkle: four thin crossed bars.
        let warm = glowing(NSColor(srgbRed: 1, green: 1, blue: 0.9, alpha: 1))
        for (k, (w, h)) in [(0.06, 0.34), (0.34, 0.06), (0.035, 0.2), (0.2, 0.035)].enumerated() {
            let p = SCNPlane(width: w, height: h)
            p.firstMaterial = warm
            let bar = SCNNode(geometry: p)
            bar.simdOrientation = quat([90, k > 1 ? 45 : 0, 0])
            flash.addChildNode(bar)
        }
        flash.isHidden = true
        root.addChildNode(flash)
    }

    // MARK: Posing

    private func place(_ node: SCNNode, _ p: Part3D) {
        guard let b = base[ObjectIdentifier(node)] else { return }
        node.simdPosition = b.pos + SIMD3<Float>(Float(p.at.x), Float(p.at.y), Float(p.at.z))
        node.simdOrientation = Model3D.quat(b.rot * 180 / .pi + p.rot)
        node.simdScale = b.scale * Float(p.scale)
    }

    func apply(_ p: Pose3D) {
        SCNTransaction.begin(); SCNTransaction.disableActions = true
        // Body: turn and lean about the core, then shift and squash.
        if let b = base[ObjectIdentifier(bot)] {
            let q = Model3D.quat(p.rot)
            let l0 = SIMD3<Double>(Double(b.pos.x), Double(b.pos.y), Double(b.pos.z))
            let rel = simd_act(simd_quatd(ix: Double(q.imag.x), iy: Double(q.imag.y), iz: Double(q.imag.z), r: Double(q.real)), l0 - Model3D.core)
            let pos = Model3D.core + rel + SIMD3(p.move.x, 0, p.move.y)
            bot.simdPosition = SIMD3<Float>(Float(pos.x), Float(pos.y), Float(pos.z))
            bot.simdOrientation = q
            bot.simdScale = [Float(1 + p.squash * 0.6), Float(1 + p.squash * 0.6), Float(1 - p.squash)]
        }
        place(earL, p.earL); place(earR, p.earR); place(finL, p.finL); place(finR, p.finR)
        if let b = base[ObjectIdentifier(eye)] {
            // Glance = roll the eyes over the visor: arc length glance.x across (about Blender Z), glance.y up (about X).
            let off = b.pos - visor, r = max(0.2, simd_length(off))
            let q = simd_quatf(angle: Float(p.glance.x) / r, axis: [0, 0, 1]) * simd_quatf(angle: -Float(p.glance.y) / r, axis: [1, 0, 0])
            eye.simdPosition = visor + q.act(off)
            eye.simdOrientation = q * eyeRest
            eye.simdScale = [b.scale.x, b.scale.y, b.scale.z * Float(max(0.06, 1 - p.blink))]
        }
        for (name, f) in faces { f.isHidden = name != p.face }
        if let b = base[ObjectIdentifier(ring)] {
            ring.isHidden = !p.ring
            ring.simdPosition = b.pos + SIMD3<Float>(Float(p.move.x), 0, 0)
            ring.simdScale = b.scale * Float(p.ringScale)
        }
        glow?.emission.intensity = CGFloat(p.ringGlow)
        if let c = p.cursor {
            cursor.isHidden = false
            cursor.simdPosition = [Float(c.x), Float(c.y), Float(c.z)]
            cursor.simdScale = SIMD3(repeating: Float(c.w))
        } else { cursor.isHidden = true }
        keyboard.isHidden = p.keys == nil
        if let lit = p.keys { for (i, k) in keys.enumerated() { k.geometry?.firstMaterial = lit.contains(i) ? keyOn : keyOff } }
        if let r = p.ripple {
            ripple.isHidden = false
            ripple.simdPosition = [Float(r.x), Float(r.y), Float(r.z)]; ripple.simdScale = SIMD3(repeating: Float(r.w))
        } else { ripple.isHidden = true }
        if let s = p.shutter {
            flash.isHidden = false
            flash.simdPosition = [Float(s.x), Float(s.y), Float(s.z)]; flash.simdScale = SIMD3(repeating: Float(s.w))
        } else { flash.isHidden = true }
        SCNTransaction.commit()
    }

    // MARK: Drawing

    /// Draw a pose into a Metal layer (sized and scaled by the caller), 4× multisampled.
    func draw(_ p: Pose3D, into layer: CAMetalLayer) {
        guard let q = Model3D.queue, let dev = Model3D.device else { return }
        let size = layer.drawableSize
        guard size.width >= 4, size.height >= 4, let drawable = layer.nextDrawable() else { return }
        apply(p)
        let key = "\(Int(size.width))x\(Int(size.height))"
        if targets[key] == nil {
            func tex(_ f: MTLPixelFormat) -> MTLTexture? {
                let d = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: f, width: Int(size.width), height: Int(size.height), mipmapped: false)
                d.textureType = .type2DMultisample; d.sampleCount = 4; d.usage = .renderTarget; d.storageMode = .private
                return dev.makeTexture(descriptor: d)
            }
            if let c = tex(layer.pixelFormat), let d = tex(.depth32Float) { targets[key] = (c, d) }
        }
        guard let t = targets[key], let cb = q.makeCommandBuffer() else { return }
        let pass = MTLRenderPassDescriptor()
        pass.colorAttachments[0].texture = t.color
        pass.colorAttachments[0].resolveTexture = drawable.texture
        pass.colorAttachments[0].loadAction = .clear
        pass.colorAttachments[0].clearColor = MTLClearColor(red: 0, green: 0, blue: 0, alpha: 0)
        pass.colorAttachments[0].storeAction = .multisampleResolve
        pass.depthAttachment.texture = t.depth
        pass.depthAttachment.loadAction = .clear
        pass.depthAttachment.clearDepth = 1
        pass.depthAttachment.storeAction = .dontCare
        renderer.render(atTime: CACurrentMediaTime(), viewport: CGRect(origin: .zero, size: size), commandBuffer: cb, passDescriptor: pass)
        cb.present(drawable)
        cb.commit()
    }

    /// A still of a pose (for the menu bar icon and the `scene` self-check).
    func snapshot(_ p: Pose3D, size: CGSize) -> CGImage? {
        apply(p)
        let img = renderer.snapshot(atTime: CACurrentMediaTime(), with: size, antialiasingMode: .multisampling4X)
        return img.cgImage(forProposedRect: nil, context: nil, hints: nil)
    }

    /// Is this point (0…1 across, 0…1 up from the bottom of the cell) on the model in this pose?
    func hits(_ p: Pose3D, u: CGFloat, v: CGFloat) -> Bool {
        apply(p)
        // A ray straight into the scene from that point of the orthographic view.
        guard let cam = camera.camera else { return false }
        let half = Float(cam.orthographicScale), aspect = Float(Sprites.cellW) / Float(Sprites.cellH)
        let x = (Float(u) * 2 - 1) * half * aspect, y = (Float(v) * 2 - 1) * half
        let a = camera.simdConvertPosition([x, y, 0], to: nil), b = camera.simdConvertPosition([x, y, -60], to: nil)
        let hit = scene.rootNode.hitTestWithSegment(from: SCNVector3(a), to: SCNVector3(b),
                                                     options: [SCNHitTestOption.searchMode.rawValue: SCNHitTestSearchMode.any.rawValue,
                                                               SCNHitTestOption.ignoreHiddenNodes.rawValue: true])
        return !hit.isEmpty
    }
}

// MARK: GrokBot's poses (ported from the Blender pose rows: same numbers, Blender axes)

enum GrokPoses {
    private static func s(_ x: Double) -> Double { sin(x) }
    private static let tau = 2 * Double.pi

    /// The pose for frame `i` of `anim` (nil = a look direction), or the look pose at `look` degrees (0 = up, clockwise).
    static func pose(_ anim: Anim?, _ i: Int, look: Double) -> Pose3D {
        var p = Pose3D()
        guard let anim else {
            let a = look * .pi / 180, sx = sin(a), cz = cos(a)
            p.rot = [-18 * cz, 0, 34 * sx]; p.glance = [0.06 * sx, 0.05 * cz]
            p.earL.rot = [0, 6 * sx, 0]; p.earR.rot = [0, 6 * sx, 0]
            return p
        }
        switch anim {
        case .idle:
            let u = Double(i) / 24, b = s(tau * u)
            p.move = [0, 0.035 * b]; p.ringScale = 1 - 0.05 * b; p.ringGlow = 1 - 0.15 * b
            p.earL.rot = [0, -4 * s(tau * u + 0.8), 0]; p.earR.rot = [0, 4 * s(tau * u + 1.3), 0]
            p.finL.at = [0, 0, 0.04 * s(tau * u + 0.5)]; p.finR.at = [0, 0, 0.04 * s(tau * u + 1.1)]
            p.blink = [15: 0.55, 16: 0.95, 17: 0.5][i] ?? 0
            if (6...9).contains(i) { p.glance = [0.04, 0.01] }
        case .runRight, .runLeft:
            let d: Double = anim == .runRight ? 1 : -1
            let b = abs(s(.pi * Double(i) / 4)), w = s(tau * Double(i) / 8)
            p.rot = [0, -14 * d, 10 * d]; p.move = [0, 0.05 * b]; p.face = i % 8 < 6 ? "happy" : "neutral"
            p.earL.rot = [0, -16 * d - 6 * b, 0]; p.earR.rot = [0, -16 * d + 6 * b, 0]
            p.finL.at = [0.06 * d, 0.05, 0.08 * w]; p.finL.rot = [0, -12 * d, 0]
            p.finR.at = [0.06 * d, 0.05, -0.08 * w]; p.finR.rot = [0, -12 * d, 0]
            p.ringScale = 0.92; p.glance = [0.05 * d, 0]
        case .wave:
            let w = s(tau * Double(i) / 6)
            p.face = "happy"; p.rot = [0, 3 * w, 0]; p.move = [0, 0.02]
            p.finR.at = [0.12, 0, 0.62]; p.finR.rot = [0, -60 + 28 * w, 0]
            p.earR.rot = [0, 6 * w, 0]; p.earL.rot = [0, -3, 0]
        case .jump:
            switch i {
            case 0: p.squash = 0.12; p.move = [0, -0.04]; p.earL.rot = [0, -12, 0]; p.earR.rot = [0, 12, 0]
            case 1: p.squash = 0.2; p.move = [0, -0.07]; p.face = "happy"; p.finL.at = [0, 0, -0.1]; p.finR.at = [0, 0, -0.1]
                    p.earL.rot = [0, -18, 0]; p.earR.rot = [0, 18, 0]
            case 2: p.squash = -0.1; p.move = [0, 0.03]; p.face = "star"; p.finL.at = [-0.08, 0, 0.35]; p.finL.rot = [0, 30, 0]
                    p.finR.at = [0.08, 0, 0.35]; p.finR.rot = [0, -30, 0]; p.earL.rot = [0, 12, 0]; p.earR.rot = [0, -12, 0]; p.ringScale = 0.8
            case 3: p.squash = -0.04; p.face = "happy"; p.finL.at = [-0.04, 0, 0.18]; p.finR.at = [0.04, 0, 0.18]; p.ringScale = 0.9
            default: p.squash = 0.08; p.move = [0, -0.03]; p.face = "happy"
            }
        case .failed:
            if i < 2 {
                p.face = "surprised"; p.rot = [0, i % 2 == 0 ? 5 : -5, 0]; p.move = [0, 0.03]
                p.earL.rot = [0, -14, 0]; p.earR.rot = [0, 14, 0]; p.finL.at = [0, 0, 0.12]; p.finR.at = [0, 0, 0.12]
            } else {
                let u = min(1, Double(i - 2) / 3), w = s(Double(i) * 1.7) * (1 - u) * 3
                p.face = "sad"; p.rot = [10 * u, w, 0]; p.move = [0, -0.08 * u]; p.ringGlow = 1 - 0.5 * u
                p.earL.rot = [0, 38 * u, 0]; p.earR.rot = [0, -38 * u, 0]
                p.finL.at = [0.1 * u, 0, -0.18 * u]; p.finL.rot = [0, 25 * u, 0]
                p.finR.at = [-0.1 * u, 0, -0.18 * u]; p.finR.rot = [0, -25 * u, 0]; p.blink = 0.25 * u
            }
        case .waiting:
            let w = s(tau * Double(i) / 6)
            p.face = i % 3 != 0 ? "surprised" : "neutral"; p.move = [0, 0.03 + 0.03 * abs(w)]
            p.earL.rot = [0, 10 + 6 * w, 0]; p.earR.rot = [0, -10 + 6 * w, 0]
            p.finL.at = [-0.05, -0.05, 0.55 + 0.1 * w]; p.finL.rot = [0, 60 - 20 * w, 0]
            p.finR.at = [0.05, -0.05, 0.55 - 0.1 * w]; p.finR.rot = [0, -60 - 20 * w, 0]
        case .work:
            let u = tau * Double(i) / 12
            p.face = "bars"; p.move = [0, 0.025 * s(u)]; p.rot = [0, 0, 8 * s(u)]
            p.earL.rot = [8 * s(u), -8 * cos(u), 0]; p.earR.rot = [-8 * s(u), 8 * cos(u + 0.6), 0]
            p.finR.at = [-0.5, -0.45, 0.02]; p.finR.rot = [0, -70, 0]; p.finL.at = [0, 0, 0.03 * s(u)]; p.glance = [0.03 * s(u), 0.03]
        case .review:
            let w = s(tau * Double(i) / 6)
            p.rot = [6, 0, 22 * w]; p.glance = [0.05 * w, -0.04]
            p.finL.at = [-0.15, -0.3, 0.1]; p.finL.rot = [0, 40, 0]; p.earL.rot = [0, -4 * w, 0]; p.earR.rot = [0, -4 * w, 0]
        case .perch:
            let b = s(tau * Double(i) / 6)
            p.ring = false; p.move = [0, -0.16 + 0.012 * b]; p.blink = [0, 0, 0.5, 0.95, 0.5, 0][i % 6]
            p.earL.rot = [0, 8, 0]; p.earR.rot = [0, -8, 0]; p.finL.at = [0.14, 0, -0.3]; p.finR.at = [-0.14, 0, -0.42]
        case .dance:
            let sg: Double = i % 2 == 0 ? 1 : -1
            p.face = ["happy", "star", "happy", "love", "happy", "star", "love", "happy"][i % 8]
            p.rot = [0, 12 * sg, 6 * sg]; p.move = [0.06 * sg, 0.05 * Double(i % 2)]
            p.finL.at = [0, 0, sg > 0 ? 0.5 : -0.05]; p.finL.rot = [0, sg > 0 ? 40 : 0, 0]
            p.finR.at = [0, 0, sg > 0 ? -0.05 : 0.5]; p.finR.rot = [0, sg > 0 ? 0 : -40, 0]
            p.earL.rot = [0, -10 * sg, 0]; p.earR.rot = [0, -10 * sg, 0]
        case .sleep:
            let b = s(tau * Double(i) / 6)
            p.face = "sleepy"; p.ringGlow = 0.45; p.ringScale = 1.05; p.move = [0, -0.2 + 0.02 * b]; p.squash = 0.04 + 0.02 * b
            p.rot = [8, 0, 0]; p.earL.rot = [0, 55, 0]; p.earR.rot = [0, -55, 0]; p.finL.at = [0.18, 0, -0.32]; p.finR.at = [-0.18, 0, -0.45]
        case .dangle:
            let sg: Double = i % 2 == 0 ? 1 : -1
            p.face = "surprised"; p.ring = false; p.rot = [0, 5 * sg, 0]
            p.earL.rot = [0, -16 + 12 * sg, 0]; p.earR.rot = [0, 16 + 12 * sg, 0]
            p.finL.at = [-0.05, 0, 0.3 + 0.12 * sg]; p.finL.rot = [0, 45 * sg, 0]
            p.finR.at = [0.05, 0, 0.3 - 0.12 * sg]; p.finR.rot = [0, -45 * sg, 0]
        case .tap:
            let reach = [0.2, 0.6, 1.0, 1.1, 1.1, 0.85, 0.55, 0.25][i % 8], press = i == 3 || i == 4
            let tip = SIMD3<Double>(-1.38, -0.4, 1.36 - (press ? 0.05 : 0))
            p.face = press ? "happy" : "neutral"; p.rot = [0, 3 * reach, -10 * reach]; p.glance = [-0.035 * reach, 0.01]
            p.finL.at = [-0.28 * reach, -0.2 * reach, 0.42 * reach]; p.finL.rot = [0, -35 * reach, 0]
            p.earL.rot = [0, -8 * reach, 0]; p.earR.rot = [0, -8 * reach, 0]
            p.cursor = [tip.x, tip.y, tip.z, 0.64 * (press ? 0.85 : 1)]
            if press { p.ripple = [tip.x, tip.y + 0.05, tip.z, i == 3 ? 0.6 : 1.05] }
        case .type:
            let lit: [Set<Int>] = [[0, 12], [3], [8, 20], [5], [14, 1], [22], [10], [17, 6]]
            let sg: Double = i % 2 == 0 ? 1 : -1
            p.face = i % 4 < 2 ? "bars" : "neutral"; p.rot = [8, 0, 0]; p.glance = [0, -0.02]; p.move = [0, 0.12 + 0.01 * sg]
            p.finL.at = [0.46, -0.8, -0.2 - (sg > 0 ? 0.06 : 0)]; p.finL.rot = [0, 75, 0]
            p.finR.at = [-0.51, -0.8, -0.39 - (sg < 0 ? 0.06 : 0)]; p.finR.rot = [0, -75, 0]
            p.keys = lit[i % 8]; p.earL.rot = [0, 4 * sg, 0]; p.earR.rot = [0, 4 * sg, 0]
        case .snap:
            let up = min(1, Double(i) / 2)
            p.face = i < 4 ? "surprised" : (i < 6 ? "star" : "happy"); p.rot = [-3 * up, 0, 0]
            p.finL.at = [0.22 * up, -0.35 * up, 0.92 * up]; p.finL.rot = [0, 50 * up, 0]
            p.finR.at = [-0.22 * up, -0.35 * up, -0.08 * up]; p.finR.rot = [0, 50 * up, 0]
            p.earL.rot = [0, -8 * up, 0]; p.earR.rot = [0, 8 * up, 0]
            if i == 4 { p.shutter = [-0.78, -0.75, 2.02, 1.0]; p.squash = 0.05 }
            if i == 5 { p.shutter = [-0.78, -0.75, 2.02, 0.55] }
        case .drag:
            let u = [0, 0, 0.15, 0.35, 0.55, 0.75, 0.92, 1.0][i % 8], x = -1.3 + 2.6 * u
            let lean: Double = (2...6).contains(i) ? 9 : 0
            p.face = i < 2 ? "neutral" : (i < 7 ? "bars" : "happy"); p.rot = [4, -lean, 12 * (u - 0.5)]
            p.move = [0.12 * (u - 0.5), 0.04]; p.glance = [0.04 * (u - 0.5), -0.015]
            p.finL.at = [x + 0.84 - 0.12, -0.5, -0.12]; p.finL.rot = [0, 60, 0]
            p.earL.rot = [0, -12 * (u - 0.5), 0]; p.earR.rot = [0, -12 * (u - 0.5), 0]
            p.cursor = [x - 0.12, -0.6, 0.66, i == 0 || i == 7 ? 0.56 : 0.5]
        case .scroll:
            let w = s(tau * Double(i) / 8), z = 1.42 - 0.3 * (Double(i % 4) / 3)
            p.face = i % 4 != 0 ? "neutral" : "happy"; p.rot = [0, 0, 8]; p.glance = [0.03, 0.01 * w]
            p.finR.at = [0.35, -0.3, z - 1.2 + 0.08]; p.finR.rot = [0, 30, 0]
            p.earL.rot = [0, -6 * w, 0]; p.earR.rot = [0, -6 * w, 0]
            p.cursor = [1.3, -0.4, z, 0.6]
        }
        return p
    }
}
