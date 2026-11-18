// Compile alongside the Mac sources (see export.sh). Export the actual geometry,
// rig and pose targets, including the keyboard and other SceneKit-created props.
import AppKit
import SceneKit
import SceneKit.ModelIO

let project = URL(fileURLWithPath: CommandLine.arguments[1])
let character = CommandLine.arguments[2]
precondition(["fourteen", "grokbot"].contains(character))
let textured = character == "fourteen"
let source = project.appendingPathComponent("characters").appendingPathComponent(character)
let asset = source.appendingPathComponent(character + ".usdz")
let archive = try Data(contentsOf: asset, options: .mappedIfSafe)
let assets = project.appendingPathComponent("Windows/assets")
let output = textured ? assets.appendingPathComponent(character) : assets
try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
guard let bot = Model3D(url: asset) else { fatalError("Cannot load model: " + character) }
let meta = try JSONSerialization.jsonObject(with: Data(contentsOf: source.appendingPathComponent(character + ".json"))) as! [String: Any]
let clips = meta["anims"] as! [String: [String: Any]]
typealias Record = [String: Any]
func floats(_ v: SIMD3<Float>) -> [Float] { [v.x, v.y, v.z] }

struct Vertex: Hashable {
    let position, normal: SIMD3<Float>
    let uv: SIMD2<Float>
}
var meshData = Data((textured ? "PET2" : "GRK1").utf8)
var meshIDs = [Data: Int]()
func append<T>(_ values: [T], to data: inout Data) {
    values.withUnsafeBytes { data.append(contentsOf: $0) }
}
// USD can index positions, normals and paint coordinates independently on each polygon corner.
func polygonMesh(_ geometry: SCNGeometry) -> Int {
    let element = geometry.elements[0]
    precondition(element.primitiveType == .polygon && element.indicesChannelCount == 3 &&
                 element.hasInterleavedIndicesChannels && element.bytesPerIndex == 4)
    func values(_ semantic: SCNGeometrySource.Semantic, _ components: Int) -> [Float] {
        let src = geometry.sources(for: semantic)[0]
        precondition(src.dataOffset == 0 && src.dataStride == components * 4 && src.usesFloatComponents)
        return src.data.withUnsafeBytes { Array($0.bindMemory(to: Float.self)) }
    }
    let pos = values(.vertex,3), norm = values(.normal,3), tex = values(.texcoord,2)
    let stream = element.data.withUnsafeBytes { Array($0.bindMemory(to: UInt32.self)) }
    var cursor = element.primitiveCount
    var ids = [Vertex: UInt32](), vertices = [Float](), indices = [UInt32]()
    for face in 0..<element.primitiveCount {
        let count = Int(stream[face])
        var first: UInt32 = 0, previous: UInt32 = 0
        for corner in 0..<count {
            let p = Int(stream[cursor])*3, n = Int(stream[cursor+1])*3, t = Int(stream[cursor+2])*2
            cursor += 3
            let v = Vertex(position: SIMD3(pos[p],pos[p+1],pos[p+2]), normal: SIMD3(norm[n],norm[n+1],norm[n+2]), uv: SIMD2(tex[t],tex[t+1]))
            let id: UInt32
            if let existing = ids[v] { id = existing } else {
                id = UInt32(ids.count)
                ids[v] = id
                vertices += floats(v.position) + floats(v.normal) + [v.uv.x,v.uv.y]
            }
            if corner == 0 { first = id }
            if corner >= 2 { indices += [first,previous,id] }
            previous = id
        }
    }
    precondition(cursor == stream.count)
    return storeMesh(vertices, indices)
}
func meshID(_ geometry: SCNGeometry) -> Int {
    if textured { return polygonMesh(geometry) }
    let mesh = MDLMesh(scnGeometry: geometry)
    let positions = mesh.vertexAttributeData(forAttributeNamed: MDLVertexAttributePosition, as: .float3)!
    let normals = mesh.vertexAttributeData(forAttributeNamed: MDLVertexAttributeNormal, as: .float3)!
    func vector(_ attribute: MDLVertexAttributeData, _ index: Int) -> SIMD3<Float> {
        let f = attribute.dataStart.advanced(by: attribute.stride * index).assumingMemoryBound(to: Float.self)
        return SIMD3(f[0], f[1], f[2])
    }
    let parts = mesh.submeshes as! [MDLSubmesh]
    precondition(parts.count == 1, "Unexpected multi-material geometry")
    let triangles = MDLSubmesh(mdlSubmesh: parts[0], indexType: .uInt32, geometryType: .triangles)!
    let map = triangles.indexBuffer.map()
    let original = map.bytes.assumingMemoryBound(to: UInt32.self)
    var ids = [Vertex: UInt32](), vertices = [Float](), indices = [UInt32]()
    for index in 0..<triangles.indexCount {
        let i = Int(original[index])
        let v = Vertex(position: vector(positions,i), normal: vector(normals,i), uv: .zero)
        if let existing = ids[v] { indices.append(existing); continue }
        let next = UInt32(ids.count)
        ids[v] = next
        vertices += floats(v.position) + floats(v.normal)
        indices.append(next)
    }
    return storeMesh(vertices, indices)
}
func storeMesh(_ vertices: [Float], _ indices: [UInt32]) -> Int {
    var block = Data()
    append([UInt32(vertices.count / (textured ? 8 : 6)), UInt32(indices.count)], to: &block)
    append(vertices, to: &block)
    append(indices, to: &block)
    if let id = meshIDs[block] { return id }
    let id = meshIDs.count
    meshData.append(block)
    meshIDs[block] = id
    return id
}

func rgb(_ property: SCNMaterialProperty) -> [Double] {
    guard let color = (property.contents as? NSColor)?.usingColorSpace(.sRGB) else { return [0,0,0] }
    return [color.redComponent,color.greenComponent,color.blueComponent].map { Double(min(1,max(0,$0))) }
}
var materials = [Record]()
var materialIDs = [ObjectIdentifier: Int]()
func materialID(_ material: SCNMaterial?) -> Int {
    guard let material else { return -1 }
    let key = ObjectIdentifier(material)
    if let id = materialIDs[key] { return id }
    let id = materials.count
    materialIDs[key] = id
    var props: Record = ["diffuse": rgb(material.diffuse), "emission": rgb(material.emission),
                      "alpha": material.transparency, "unlit": material.lightingModel == .constant,
                      "doubleSided": material.isDoubleSided, "name": material.name ?? ""]
    // SceneKit exposes embedded USDZ textures as byte ranges in the source archive.
    if let url = material.diffuse.contents as? URL {
        let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
        func integer(_ name: String) -> Int? { items.first { $0.name == name }?.value.flatMap(Int.init) }
        guard let start = integer("offset"), let length = integer("size"), length > 0, start >= 0,
              start <= archive.count - length else { fatalError("Invalid embedded texture: " + url.absoluteString) }
        let filename = "paint-" + String(start) + ".png"
        try! archive[start..<start+length].write(to: output.appendingPathComponent(filename), options: .atomic)
        props["texture"] = filename
    }
    materials.append(props)
    return id
}
func state(_ node: SCNNode) -> [Float] {
    let q = node.simdOrientation.vector
    return floats(node.simdPosition) + [q.x,q.y,q.z,q.w] + floats(node.simdScale)
        + [node.isHidden ? 1 : 0, Float(materialID(node.geometry?.firstMaterial))]
}
bot.apply(Pose3D())
var nodes = [SCNNode](), rig = [Record](), rest = [[Float]]()
var camera: SCNNode!
func visit(_ node: SCNNode, _ parent: Int) {
    if node.camera != nil { camera = node; return }
    if node.light != nil { return }
    let id = nodes.count
    let initial = state(node)
    nodes.append(node)
    rest.append(initial)
    rig.append(["name": node.name ?? "", "parent": parent, "mesh": node.geometry.map(meshID) ?? -1, "rest": initial])
    for child in node.childNodes { visit(child, id) }
}
// WPF binds one material per geometry. Keep each material subset under its original joint.
func splitGeometry(_ node: SCNNode) {
    guard let g = node.geometry, g.elements.count > 1 else { return }
    node.geometry = nil
    for (index, element) in g.elements.enumerated() {
        let part = SCNGeometry(sources: g.sources, elements: [element])
        part.materials = [g.materials[index % g.materials.count]]
        node.addChildNode(SCNNode(geometry: part))
    }
}
var subsets = [SCNNode]()
bot.scene.rootNode.enumerateHierarchy { node, _ in
    if let g = node.geometry, g.elements.count > 1 { subsets.append(node) }
}
subsets.forEach(splitGeometry)
visit(bot.scene.rootNode, -1)
var animations = [String: [[[Float]]]]()
for anim in Anim.allCases {
    let count = clips[anim.rawValue]!["count"] as! Int
    animations[anim.rawValue] = (0..<count).map { frame in
        bot.apply(bot.pose(anim,frame,look:0))
        return nodes.enumerated().compactMap { id,node -> [Float]? in
            let target = state(node)
            return target == rest[id] ? nil : [Float(id)] + target
        }
    }
}
func visorCenter() -> [Float] {
    guard !textured else { return [0,0,0] }
    let core = nodes.first(where: { $0.name == "Core" })!
    let sphere = core.childNodes.first(where: { $0.geometry != nil }) ?? core
    let (low,high) = sphere.boundingBox
    let body = nodes.first(where: { $0.name == "Bot_root" })!
    let visor = body.simdConvertPosition((SIMD3<Float>(low) + SIMD3<Float>(high)) / 2, from: sphere)
    return floats(visor)
}
let rigFile: Record = ["format": textured ? 2 : 1, "meshCount": meshIDs.count, "nodes": rig, "materials": materials, "animations": animations,
                       "eyeCenter": visorCenter(), "cameraPosition": floats(camera.simdWorldPosition),
                       "cameraFront": floats(camera.simdWorldFront), "cameraUp": floats(camera.simdWorldUp),
                       "cameraWidth": 2 * camera.camera!.orthographicScale * 192 / 208]
try meshData.write(to: output.appendingPathComponent(character + ".mesh"), options: .atomic)
try JSONSerialization.data(withJSONObject: rigFile,options: [.sortedKeys]).write(to: output.appendingPathComponent(character + ".rig.json"), options: .atomic)
print("\(nodes.count) nodes, \(meshIDs.count) meshes, \(animations.count) clips")
