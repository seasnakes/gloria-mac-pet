import SceneKit
import simd

// The joints are Blender-local (Z up, front towards -Y). Sleeves and shoes follow their limb pivots.
struct FourteenLimbs {
    var head = SIMD3<Double>.zero
    var leftArm = SIMD3<Double>.zero, rightArm = SIMD3<Double>.zero
    var leftLeg = SIMD3<Double>.zero, rightLeg = SIMD3<Double>.zero

    mutating func approach(_ t: Self, _ k: Double) {
        head += (t.head - head) * k
        leftArm += (t.leftArm - leftArm) * k; rightArm += (t.rightArm - rightArm) * k
        leftLeg += (t.leftLeg - leftLeg) * k; rightLeg += (t.rightLeg - rightLeg) * k
    }
}

final class FourteenRig {
    private let root, head, leftArm, rightArm, leftLeg, rightLeg, eyes, lashes: SCNNode

    init?(scene: SCNScene) {
        guard let root = scene.rootNode.childNode(withName: "Fourteen_Root", recursively: true) else { return nil }
        func joint(_ name: String) -> SCNNode? { root.childNode(withName: name, recursively: true) }
        guard let head = joint("HeadJoint"), let leftArm = joint("ArmLeft"), let rightArm = joint("ArmRight"),
              let leftLeg = joint("LegLeft"), let rightLeg = joint("LegRight"),
              let eyes = joint("EyesOpen"), let lashes = joint("EyesClosed") else { return nil }
        self.root = root; self.head = head; self.leftArm = leftArm; self.rightArm = rightArm
        self.leftLeg = leftLeg; self.rightLeg = rightLeg; self.eyes = eyes; self.lashes = lashes
        lashes.simdScale = SIMD3(repeating: 1)
        // Authored pigment materials follow the scene lights as the body and limbs turn.
        root.enumerateHierarchy { node, _ in
            for material in node.geometry?.materials ?? [] {
                if material.name?.hasPrefix("Live_") == true {
                    material.lightingModel = .lambert
                    material.locksAmbientWithDiffuse = true
                } else {
                    material.lightingModel = .constant
                }
                material.isDoubleSided = true
            }
        }
    }

    func apply(_ p: Pose3D) {
        SCNTransaction.begin(); SCNTransaction.disableActions = true
        root.simdOrientation = Model3D.quat(p.rot)
        root.simdPosition = [Float(p.move.x), 0, Float(0.075 + p.move.y)]
        root.simdScale = [Float(1 + p.squash * 0.3), 1, Float(1 - p.squash)]
        head.simdOrientation = Model3D.quat(p.limbs.head)
        leftArm.simdOrientation = Model3D.quat(p.limbs.leftArm)
        rightArm.simdOrientation = Model3D.quat(p.limbs.rightArm)
        leftLeg.simdOrientation = Model3D.quat(p.limbs.leftLeg)
        rightLeg.simdOrientation = Model3D.quat(p.limbs.rightLeg)
        eyes.isHidden = p.blink > 0.5; lashes.isHidden = !eyes.isHidden
        SCNTransaction.commit()
    }

    static func pose(_ anim: Anim?, _ frame: Int, look: Double) -> Pose3D {
        var p = Pose3D()
        let i = Double(frame)
        func cycle(_ count: Double) -> Double { sin(2 * .pi * i / count) }
        guard let anim else {
            let a = look * .pi / 180
            p.limbs.head = [-8 * cos(a), 0, 27 * sin(a)]
            return p
        }
        switch anim {
        case .idle:
            p.squash = 0.003 * cycle(24)
            p.blink = (15...17).contains(frame) ? 1 : 0
        case .runLeft, .runRight:
            let d: Double = anim == .runRight ? 1 : -1, stride = cycle(8)
            p.rot.z = 47 * d; p.move.y = 0.045 * abs(stride)
            p.limbs.leftLeg.x = 32 * stride; p.limbs.rightLeg.x = -32 * stride
            p.limbs.leftArm.x = -20 * stride; p.limbs.rightArm.x = 20 * stride
        case .wave:
            p.limbs.rightArm = [-54, -114 + 13 * cycle(6), 0]; p.limbs.head.y = -3
        case .jump:
            let up = [0.0, 0, 0.16, 0.10, 0][frame % 5]
            p.move.y = up; p.squash = frame == 1 ? 0.044 : 0
            p.limbs.leftArm.y = up * 245; p.limbs.rightArm.y = -up * 245
        case .failed:
            p.limbs.head = [14, 4 * cycle(8), 0]; p.squash = 0.02
        case .waiting, .review:
            p.limbs.head = [5, 3, 12 * cycle(6)]
        case .work, .type:
            let beat = cycle(anim == .type ? 4 : 12)
            p.limbs.head.x = 6
            p.limbs.leftArm = [-38 + 9 * beat, -6, 0]; p.limbs.rightArm = [-38 - 9 * beat, 6, 0]
        case .perch:
            p.limbs.leftLeg.x = -76 + 4 * cycle(6); p.limbs.rightLeg.x = -72 - 4 * cycle(6)
            p.blink = frame == 3 ? 1 : 0
        case .dance:
            let b = cycle(8)
            p.rot = [0, 4 * b, 10 * b]; p.move.y = 0.045 * abs(b)
            p.limbs.leftArm.y = 30 + 27 * b; p.limbs.rightArm.y = -30 + 27 * b
            p.limbs.leftLeg.x = 13 * b; p.limbs.rightLeg.x = -13 * b
        case .sleep:
            p.blink = 1; p.limbs.head = [11, -5, 0]; p.squash = 0.012 + 0.004 * cycle(6)
        case .dangle:
            let b = cycle(4)
            p.rot.y = 3 * b; p.limbs.leftLeg.x = 14 * b; p.limbs.rightLeg.x = -14 * b
            p.limbs.leftArm.y = 18 + 8 * b; p.limbs.rightArm.y = -18 + 8 * b
        case .tap, .drag, .scroll:
            let reach = (1 - cos(2 * .pi * i / 8)) * 0.5
            if anim == .scroll { p.limbs.rightArm = [-31 - 10 * reach, -21, 0] }
            else { p.limbs.leftArm = [-32 - 10 * reach, 24 + 14 * reach, 0] }
            p.limbs.head.z = (anim == .drag ? 15 : 5) * cycle(8)
        case .snap:
            p.limbs.rightArm = [-59, -20, 0]; p.limbs.head.y = 4
            p.blink = frame == 4 ? 1 : 0
        }
        return p
    }
}
