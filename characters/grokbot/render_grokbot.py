"""GrokBot desk-pet frames, rendered from the user's Blender project (grokbot_transform_v6.blend, finished pose at frame 650).

  /Applications/Blender.app/Contents/MacOS/Blender -b <grokbot_transform_v6.blend> --python render_grokbot.py -- [--only anim,anim] [--key] [--samples N]

Every frame is a pose built from the model's own parts: the root (lean / hop / turn about the core), the two ear pivots,
the two fin "hands", the eye pivot (blink, glance), the nine LED faces, the hover ring and the 3D arrow cursor from the
transform film. A small holographic keyboard and a click ripple are added for the computer-use rows.
Output: frames/<anim>_<i>.png at 2× (384×416, transparent), plus frames/anchor.json (where the ground point lands).
pack_grokbot.py turns them into grokbot.png / grokbot.json.
"""
import bpy, json, math, os, sys
from mathutils import Euler, Matrix, Vector
from bpy_extras.object_utils import world_to_camera_view

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "frames")
os.makedirs(OUT, exist_ok=True)
argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
ONLY = None
KEY = "--key" in argv                 # one frame per animation, for a quick look
SAMPLES = 24
for i, a in enumerate(argv):
    if a == "--only": ONLY = set(argv[i + 1].split(","))
    if a == "--samples": SAMPLES = int(argv[i + 1])

W, H = 384, 416
sc = bpy.context.scene
sc.frame_set(650)
for o in bpy.data.objects:
    if o.animation_data: o.animation_data.action = None
for m in bpy.data.materials:
    if m.node_tree and m.node_tree.animation_data: m.node_tree.animation_data.action = None
for n in ["Clean studio background", "LaserGrid", "ScanRing"]:
    if n in bpy.data.objects: bpy.data.objects[n].hide_render = True

r = sc.render
r.film_transparent = True
r.image_settings.file_format = "PNG"; r.image_settings.color_mode = "RGBA"
r.resolution_x, r.resolution_y, r.resolution_percentage = W, H, 100
r.use_persistent_data = True
prefs = bpy.context.preferences.addons["cycles"].preferences
prefs.compute_device_type = "METAL"; prefs.get_devices()
for d in prefs.devices: d.use = d.type == "METAL"
sc.cycles.device = "GPU"; sc.cycles.samples = SAMPLES; sc.cycles.use_denoising = True

# Orthographic camera, slightly from above, ground point near the bottom of the frame.
cam = bpy.data.objects["Cam"]
for c in list(cam.constraints): cam.constraints.remove(c)
cam.data.type = "ORTHO"
cam.data.ortho_scale = 3.62
pitch = math.radians(8)
aim = Vector((0, 0, 1.72))
cam.rotation_mode = "XYZ"
cam.rotation_euler = Euler((math.radians(90) - pitch, 0, 0))
cam.location = aim + Vector((0, -12 * math.cos(pitch), 12 * math.sin(pitch)))
bpy.context.view_layer.update()
g = world_to_camera_view(sc, cam, Vector((0, 0, 0.06)))
with open(os.path.join(OUT, "anchor.json"), "w") as f:
    json.dump({"ground": [g.x * W, (1 - g.y) * H], "size": [W, H], "units_per_px": cam.data.ortho_scale / max(W, H)}, f)

O = bpy.data.objects
root, earL, earR = O["Bot_root"], O["EarPivot-1"], O["EarPivot1"]
finL, finR, eye, ring = O["FinPivot-1"], O["FinPivot1"], O["EyePivot"], O["HoverRing"]
cursor = O["Cursor"]
faces = {o.name[7:]: o for o in O if o.name.startswith("E_Eyes_")}
base = {o.name: (o.location.copy(), o.rotation_euler.copy(), o.scale.copy()) for o in [root, earL, earR, finL, finR, eye, ring, cursor]}
CORE = Vector((0, 0, 1.17))           # world centre of the round body; turns and leans pivot here
led = bpy.data.materials["LED_Off"].node_tree.nodes["Principled BSDF"]
led_base = (tuple(led.inputs["Emission Color"].default_value), led.inputs["Emission Strength"].default_value)
glow = bpy.data.materials["Glow_Soft"].node_tree.nodes["Principled BSDF"]
glow_base = glow.inputs["Emission Strength"].default_value


def emissive(name, rgb, strength, alpha=1.0):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = (*rgb, 1)
    b.inputs["Emission Color"].default_value = (*rgb, 1)
    b.inputs["Emission Strength"].default_value = strength
    b.inputs["Alpha"].default_value = alpha
    if hasattr(m, "blend_method"): m.blend_method = "HASHED"
    return m


# The cursor mesh lies in its local XY plane: stand it up to face the camera.
for c in list(cursor.constraints): cursor.constraints.remove(c)
cursor.rotation_mode = "XYZ"; cursor.rotation_euler = Euler((0, 0, 0))
cursor.hide_viewport = False
for ch in cursor.children:
    ch.rotation_mode = "XYZ"; ch.rotation_euler = Euler((math.radians(90), 0, 0))
cursor_parts = list(cursor.children)

# Holographic keyboard: 3 rows of cyan keys on a faint pane, parented to the root so it bobs with her.
kb_mat = emissive("Holo_Key", (0.25, 0.8, 1.0), 2.4)
kb_hot = emissive("Holo_KeyHot", (0.9, 1.0, 1.0), 14.0)
pane_mat = emissive("Holo_Pane", (0.1, 0.45, 0.8), 1.6, alpha=0.45)
keys = []
bpy.ops.mesh.primitive_plane_add(size=1)
pane = bpy.context.active_object; pane.name = "HoloPane"
pane.scale = (1.08, 0.36, 1); pane.data.materials.append(pane_mat)
pane.parent = root
pane.location = Vector((0, -0.95, 0.3))
pane.rotation_euler = Euler((math.radians(62), 0, 0))
for row in range(3):
    for col in range(9 - (row == 2) * 2):
        bpy.ops.mesh.primitive_cube_add(size=1)
        k = bpy.context.active_object; k.name = f"HoloKey{row}{col}"
        k.scale = (0.085, 0.07, 0.012)
        k.data.materials.append(kb_mat)
        k.parent = pane
        n = 9 - (row == 2) * 2
        k.location = Vector(((col - (n - 1) / 2) * 0.108 / 1.08, (1 - row) * 0.11 / 0.36, 0.02))
        k.scale = (0.085 / 1.08, 0.07 / 0.36, 0.012)
        keys.append(k)
kb_parts = [pane] + keys

ripple_mat = emissive("Holo_Ripple", (0.5, 0.95, 1.0), 6.0, alpha=0.9)
bpy.ops.mesh.primitive_torus_add(major_radius=0.2, minor_radius=0.018)
ripple = bpy.context.active_object; ripple.name = "ClickRipple"
ripple.data.materials.append(ripple_mat)
ripple.rotation_euler = Euler((math.radians(90), 0, 0))

flash_mat = emissive("Holo_Flash", (1.0, 1.0, 0.92), 16.0)
flash_parts = []
for k, (sx, sz) in enumerate(((0.06, 0.34), (0.34, 0.06), (0.035, 0.2), (0.2, 0.035))):
    bpy.ops.mesh.primitive_plane_add(size=1)
    f = bpy.context.active_object; f.name = f"ShutterFlash{k}"
    f.data.materials.append(flash_mat)
    f.rotation_euler = Euler((math.radians(90), math.radians(45 if k > 1 else 0), 0))
    f.scale = (sx, sz, 1)
    flash_parts.append(f)
# Diamond-ish rays: squash the plane's middle verts out is overkill; thin crossed bars read as a sparkle at this size.
EXTRAS = cursor_parts + kb_parts + [ripple] + flash_parts


def rad(v): return Euler(tuple(math.radians(x) for x in v))


def pose(p):
    """p: dict of offsets from the finished pose (degrees / model units)."""
    # Root: turn (rz), lean (ry), nod (rx) about the core, plus a shift and a squash.
    rx, ry, rz = p.get("rot", (0, 0, 0))
    R = rad((rx, ry, rz)).to_matrix()
    L0, _, S0 = base["Bot_root"]
    dx, dz = p.get("move", (0, 0))
    root.rotation_euler = rad((rx, ry, rz))
    root.location = CORE + R @ (L0 - CORE) + Vector((dx, 0, dz))
    sq = p.get("squash", 0)                       # + = flatter
    root.scale = Vector((1 + sq * 0.6, 1 + sq * 0.6, 1 - sq))

    for o, key in ((earL, "earL"), (earR, "earR"), (finL, "finL"), (finR, "finR")):
        l, e, s = base[o.name]
        v = p.get(key, {})
        o.location = l + Vector(v.get("at", (0, 0, 0)))
        o.rotation_euler = Euler(tuple(e[i] + math.radians(v.get("rot", (0, 0, 0))[i]) for i in range(3)))
        o.scale = s * v.get("scale", 1.0)

    l, e, s = base["EyePivot"]
    gx, gz = p.get("glance", (0, 0))
    eye.location = l + Vector((gx, 0, gz))
    eye.scale = Vector((s.x, s.y, s.z * max(0.06, 1 - p.get("blink", 0))))
    face = p.get("face", "neutral")
    for name, o in faces.items(): o.hide_render = name != face

    rl, re, rs = base["HoverRing"]
    ring.hide_render = not p.get("ring", True)
    ring.location = rl + Vector((dx, 0, 0))
    ring.scale = rs * p.get("ringScale", 1.0)
    glow.inputs["Emission Strength"].default_value = glow_base * p.get("ringGlow", 1.0)

    led.inputs["Emission Color"].default_value = led_base[0] if not p.get("flash") else (1, 1, 1, 1)
    led.inputs["Emission Strength"].default_value = led_base[1] + 3.0 * p.get("flash", 0)

    for o in EXTRAS: o.hide_render = True
    if "cursor" in p:
        cx, cy, cz, cs = p["cursor"]
        cursor.location = Vector((cx, cy, cz)); cursor.scale = Vector((cs, cs, cs))
        for o in cursor_parts: o.hide_render = False; o.hide_viewport = False
    if "keys" in p:
        for o in kb_parts: o.hide_render = False
        for i, k in enumerate(keys): k.data.materials[0] = kb_hot if i in p["keys"] else kb_mat
    if "ripple" in p:
        x, y, z, rr = p["ripple"]
        ripple.hide_render = False
        ripple.location = Vector((x, y, z)); ripple.scale = Vector((rr, rr, rr))
    if p.get("shutter"):
        fx, fy, fz, fs = p["shutter"]
        for f in flash_parts:
            f.hide_render = False
            f.location = Vector((fx, fy, fz))
            f.scale = Vector((f.scale.x / max(1e-6, f.get("k", 1)) * fs, f.scale.y / max(1e-6, f.get("k", 1)) * fs, 1)); f["k"] = fs


# ---------- the rows ----------
def wave(i, n): return math.sin(2 * math.pi * i / n)


def idle(i, n=24):
    u = i / n
    b = math.sin(2 * math.pi * u)
    p = {"move": (0, 0.035 * b), "ringScale": 1 - 0.05 * b, "ringGlow": 1 - 0.15 * b,
         "earL": {"rot": (0, -4 * math.sin(2 * math.pi * u + 0.8), 0)}, "earR": {"rot": (0, 4 * math.sin(2 * math.pi * u + 1.3), 0)},
         "finL": {"at": (0, 0, 0.04 * math.sin(2 * math.pi * u + 0.5))}, "finR": {"at": (0, 0, 0.04 * math.sin(2 * math.pi * u + 1.1))}}
    p["blink"] = {15: 0.55, 16: 0.95, 17: 0.5}.get(i, 0)
    if 6 <= i <= 9: p["glance"] = (0.04, 0.01)
    return p


def run(i, d):
    # Gliding: lean into the motion, ears and fins stream behind, a quick bob each step.
    b = abs(math.sin(math.pi * i / 4))
    return {"rot": (0, -14 * d, 10 * d), "move": (0, 0.05 * b), "face": "happy" if i % 8 < 6 else "neutral",
            "earL": {"rot": (0, -16 * d - 6 * b, 0)}, "earR": {"rot": (0, -16 * d + 6 * b, 0)},
            "finL": {"at": (0.06 * d, 0.05, 0.08 * wave(i, 8)), "rot": (0, -12 * d, 0)},
            "finR": {"at": (0.06 * d, 0.05, -0.08 * wave(i, 8)), "rot": (0, -12 * d, 0)},
            "ringScale": 0.92, "glance": (0.05 * d, 0)}


def wave_row(i, n=6):
    s = math.sin(2 * math.pi * i / n)
    return {"face": "happy", "rot": (0, 3 * s, 0), "move": (0, 0.02),
            "finR": {"at": (0.12, 0, 0.62), "rot": (0, -60 + 28 * s, 0)},
            "earR": {"rot": (0, 6 * s, 0)}, "earL": {"rot": (0, -3, 0)}}


def jump(i):
    return [{"squash": 0.12, "move": (0, -0.04), "face": "neutral", "earL": {"rot": (0, -12, 0)}, "earR": {"rot": (0, 12, 0)}},
            {"squash": 0.2, "move": (0, -0.07), "face": "happy", "finL": {"at": (0, 0, -0.1)}, "finR": {"at": (0, 0, -0.1)},
             "earL": {"rot": (0, -18, 0)}, "earR": {"rot": (0, 18, 0)}},
            {"squash": -0.1, "move": (0, 0.03), "face": "star", "finL": {"at": (-0.08, 0, 0.35), "rot": (0, 30, 0)},
             "finR": {"at": (0.08, 0, 0.35), "rot": (0, -30, 0)}, "earL": {"rot": (0, 12, 0)}, "earR": {"rot": (0, -12, 0)}, "ringScale": 0.8},
            {"squash": -0.04, "face": "happy", "finL": {"at": (-0.04, 0, 0.18)}, "finR": {"at": (0.04, 0, 0.18)}, "ringScale": 0.9},
            {"squash": 0.08, "move": (0, -0.03), "face": "happy"}][i]


def failed(i):
    if i < 2:
        return {"face": "surprised", "rot": (0, (-1) ** i * 5, 0), "move": (0, 0.03),
                "earL": {"rot": (0, -14, 0)}, "earR": {"rot": (0, 14, 0)}, "finL": {"at": (0, 0, 0.12)}, "finR": {"at": (0, 0, 0.12)}}
    u = min(1, (i - 2) / 3)
    s = math.sin(i * 1.7) * (1 - u) * 3
    return {"face": "sad", "rot": (10 * u, s, 0), "move": (0, -0.08 * u), "ringGlow": 1 - 0.5 * u,
            "earL": {"rot": (0, 38 * u, 0)}, "earR": {"rot": (0, -38 * u, 0)},
            "finL": {"at": (0.1 * u, 0, -0.18 * u), "rot": (0, 25 * u, 0)}, "finR": {"at": (-0.1 * u, 0, -0.18 * u), "rot": (0, -25 * u, 0)},
            "blink": 0.25 * u}


def waiting(i, n=6):
    s = math.sin(2 * math.pi * i / n)
    return {"face": "surprised" if i % 3 else "neutral", "move": (0, 0.03 + 0.03 * abs(s)),
            "earL": {"rot": (0, 10 + 6 * s, 0)}, "earR": {"rot": (0, -10 + 6 * s, 0)},
            "finL": {"at": (-0.05, -0.05, 0.55 + 0.1 * s), "rot": (0, 60 - 20 * s, 0)},
            "finR": {"at": (0.05, -0.05, 0.55 - 0.1 * s), "rot": (0, -60 - 20 * s, 0)}}


def work(i, n=12):
    # Thinking: loading-bar eyes, slow antenna scan, one fin at the "chin".
    u = 2 * math.pi * i / n
    return {"face": "bars", "move": (0, 0.025 * math.sin(u)), "rot": (0, 0, 8 * math.sin(u)),
            "earL": {"rot": (8 * math.sin(u), -8 * math.cos(u), 0)}, "earR": {"rot": (-8 * math.sin(u), 8 * math.cos(u + 0.6), 0)},
            "finR": {"at": (-0.5, -0.45, 0.02), "rot": (0, -70, 0)},
            "finL": {"at": (0, 0, 0.03 * math.sin(u))}, "glance": (0.03 * math.sin(u), 0.03)}


def review(i, n=6):
    s = math.sin(2 * math.pi * i / n)
    return {"face": "neutral", "rot": (6, 0, 22 * s), "glance": (0.05 * s, -0.04),
            "finL": {"at": (-0.15, -0.3, 0.1), "rot": (0, 40, 0)}, "earL": {"rot": (0, -4 * s, 0)}, "earR": {"rot": (0, -4 * s, 0)}}


def look(i):
    a = math.radians(i * 22.5)
    sx, cz = math.sin(a), math.cos(a)
    return {"face": "neutral", "rot": (-18 * cz, 0, 34 * sx), "glance": (0.06 * sx, 0.05 * cz),
            "earL": {"rot": (0, 6 * sx, 0)}, "earR": {"rot": (0, 6 * sx, 0)}}


def perch(i, n=6):
    # Parked on a window edge: the ring tucks away, the body settles onto the line, a slow blink.
    b = math.sin(2 * math.pi * i / n)
    return {"ring": False, "move": (0, -0.16 + 0.012 * b), "face": "neutral", "blink": [0, 0, 0.5, 0.95, 0.5, 0][i],
            "earL": {"rot": (0, 8, 0)}, "earR": {"rot": (0, -8, 0)},
            "finL": {"at": (0.14, 0, -0.3)}, "finR": {"at": (-0.14, 0, -0.42)}}


def dance(i):
    s = (-1) ** i
    return {"face": ["happy", "star", "happy", "love", "happy", "star", "love", "happy"][i],
            "rot": (0, 12 * s, 6 * s), "move": (0.06 * s, 0.05 * (i % 2)),
            "finL": {"at": (0, 0, 0.5 if s > 0 else -0.05), "rot": (0, 40 if s > 0 else 0, 0)},
            "finR": {"at": (0, 0, -0.05 if s > 0 else 0.5), "rot": (0, 0 if s > 0 else -40, 0)},
            "earL": {"rot": (0, -10 * s, 0)}, "earR": {"rot": (0, -10 * s, 0)}}


def sleep(i, n=6):
    b = math.sin(2 * math.pi * i / n)
    return {"face": "sleepy", "ring": True, "ringGlow": 0.45, "ringScale": 1.05, "move": (0, -0.2 + 0.02 * b), "squash": 0.04 + 0.02 * b,
            "rot": (8, 0, 0), "earL": {"rot": (0, 55, 0)}, "earR": {"rot": (0, -55, 0)},
            "finL": {"at": (0.18, 0, -0.32)}, "finR": {"at": (-0.18, 0, -0.45)}}


def dangle(i):
    s = (-1) ** i
    return {"face": "surprised", "ring": False, "rot": (0, 5 * s, 0),
            "earL": {"rot": (0, -16 + 12 * s, 0)}, "earR": {"rot": (0, 16 + 12 * s, 0)},
            "finL": {"at": (-0.05, 0, 0.3 + 0.12 * s), "rot": (0, 45 * s, 0)},
            "finR": {"at": (0.05, 0, 0.3 - 0.12 * s), "rot": (0, -45 * s, 0)}}


# Computer use. The fins stay outside the face; the cursor floats beside her, the keyboard and drags below the visor.
def tap(i):
    # Reach out to the left (0-2), click (3-4: cursor dips, ripple), come back (5-7).
    reach = [0.2, 0.6, 1.0, 1.1, 1.1, 0.85, 0.55, 0.25][i]
    press = i in (3, 4)
    tip = Vector((-1.38, -0.4, 1.36 - (0.05 if press else 0)))
    p = {"face": "happy" if press else "neutral", "rot": (0, 3 * reach, -10 * reach), "glance": (-0.035 * reach, 0.01),
         "finL": {"at": (-0.28 * reach, -0.2 * reach, 0.42 * reach), "rot": (0, -35 * reach, 0)},
         "earL": {"rot": (0, -8 * reach, 0)}, "earR": {"rot": (0, -8 * reach, 0)},
         "cursor": (tip.x, tip.y, tip.z, 0.64 * (0.85 if press else 1))}
    if press: p["ripple"] = (tip.x, tip.y + 0.05, tip.z, 0.6 if i == 3 else 1.05)
    return p


def type_row(i):
    hits = [{0, 12}, {3}, {8, 20}, {5}, {14, 1}, {22}, {10}, {17, 6}][i]
    s = (-1) ** i
    return {"face": "bars" if i % 4 < 2 else "neutral", "rot": (8, 0, 0), "glance": (0, -0.02), "move": (0, 0.12 + 0.01 * s),
            "finL": {"at": (0.46, -0.8, -0.2 - 0.06 * (s > 0)), "rot": (0, 75, 0)},
            "finR": {"at": (-0.51, -0.8, -0.39 - 0.06 * (s < 0)), "rot": (0, -75, 0)}, "keys": hits,
            "earL": {"rot": (0, 4 * s, 0)}, "earR": {"rot": (0, 4 * s, 0)}}


def snap(i):
    # Frame the shot with both fins at the visor's corners, then the LED face flashes like a shutter.
    up = min(1, i / 2)
    p = {"face": "surprised" if i < 4 else ("star" if i < 6 else "happy"), "rot": (-3 * up, 0, 0),
         "finL": {"at": (0.22 * up, -0.35 * up, 0.92 * up), "rot": (0, 50 * up, 0)},
         "finR": {"at": (-0.22 * up, -0.35 * up, -0.08 * up), "rot": (0, 50 * up, 0)},
         "earL": {"rot": (0, -8 * up, 0)}, "earR": {"rot": (0, 8 * up, 0)}}
    if i == 4: p["shutter"] = (-0.78, -0.75, 2.02, 1.0); p["squash"] = 0.05
    if i == 5: p["shutter"] = (-0.78, -0.75, 2.02, 0.55)
    return p


def drag(i):
    # Grab the cursor on the left and haul it across under her body.
    u = [0, 0, 0.15, 0.35, 0.55, 0.75, 0.92, 1.0][i]
    x = -1.3 + 2.6 * u
    lean = 9 if 1 < i < 7 else 0
    return {"face": "neutral" if i < 2 else ("bars" if i < 7 else "happy"), "rot": (4, -lean, 12 * (u - 0.5)),
            "move": (0.12 * (u - 0.5), 0.04), "glance": (0.04 * (u - 0.5), -0.015),
            "finL": {"at": (x + 0.84 - 0.12, -0.5, -0.12), "rot": (0, 60, 0)},
            "earL": {"rot": (0, -12 * (u - 0.5), 0)}, "earR": {"rot": (0, -12 * (u - 0.5), 0)},
            "cursor": (x - 0.12, -0.6, 0.66, 0.56 if i in (0, 7) else 0.5)}


def scroll(i):
    # Rolling the page with the cursor on her right: the fin wheels it down, ears bob along.
    s = math.sin(2 * math.pi * i / 8)
    z = 1.42 - 0.3 * ((i % 4) / 3)
    return {"face": "neutral" if i % 4 else "happy", "rot": (0, 0, 8), "glance": (0.03, 0.01 * s),
            "finR": {"at": (0.35, -0.3, z - 1.2 + 0.08), "rot": (0, 30, 0)},
            "earL": {"rot": (0, -6 * s, 0)}, "earR": {"rot": (0, -6 * s, 0)},
            "cursor": (1.3, -0.4, z, 0.6)}


ROWS = {
    "idle": [idle(i) for i in range(24)],
    "runRight": [run(i, 1) for i in range(8)],
    "runLeft": [run(i, -1) for i in range(8)],
    "wave": [wave_row(i) for i in range(6)],
    "jump": [jump(i) for i in range(5)],
    "failed": [failed(i) for i in range(8)],
    "waiting": [waiting(i) for i in range(6)],
    "work": [work(i) for i in range(12)],
    "review": [review(i) for i in range(6)],
    "look": [look(i) for i in range(16)],
    "perch": [perch(i) for i in range(6)],
    "dance": [dance(i) for i in range(8)],
    "sleep": [sleep(i) for i in range(6)],
    "dangle": [dangle(i) for i in range(4)],
    "tap": [tap(i) for i in range(8)],
    "type": [type_row(i) for i in range(8)],
    "snap": [snap(i) for i in range(8)],
    "drag": [drag(i) for i in range(8)],
    "scroll": [scroll(i) for i in range(8)],
}

for anim, frames in ROWS.items():
    if ONLY and anim not in ONLY: continue
    picks = range(len(frames)) if not KEY else [min(len(frames) - 1, {"tap": 3, "snap": 4, "drag": 4, "look": 4, "jump": 2}.get(anim, 0))]
    for i in picks:
        pose(frames[i])
        r.filepath = os.path.join(OUT, f"{anim}_{i:02d}.png")
        bpy.ops.render.render(write_still=True)
print("done", flush=True)
