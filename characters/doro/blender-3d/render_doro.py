"""
Render Doro (experiments/blender-mochi/mochi_pink.blend) into a desk-pet sprite sheet:
8 columns × 15 rows of 192×208 cells, same row layout as GLORIA's sheet; feet contact on y=202, centre x=96.

  /Applications/Blender.app/Contents/MacOS/Blender -b ../../../experiments/blender-mochi/mochi_pink.blend \
      --python render_doro.py -- <out_dir> [--quick]

Writes <out_dir>/frames/r{row}_c{col}.png at 4× (768×832); assemble.py downsizes them into the sheet.
"""
import bpy, math, os, sys, json
from mathutils import Euler, Vector, Quaternion

argv = sys.argv[sys.argv.index('--') + 1:]
OUT = os.path.abspath(argv[0]); QUICK = '--quick' in argv
FR = os.path.join(OUT, 'frames'); os.makedirs(FR, exist_ok=True)
K = 4                                   # render at 4× the cell size
W, H = 192 * K, 208 * K
GROUND_Y = 202 * K                      # pixel row of z = 0
STAND_PX = 128 * K                      # Doro's standing height in the cell

sc = bpy.context.scene
rig = bpy.data.objects['Mochi_Rig']
for o in bpy.data.objects:
    if o.name == 'Ground': o.hide_render = True
sc.render.film_transparent = True
sc.render.resolution_x, sc.render.resolution_y = W, H
sc.render.resolution_percentage = 100
sc.eevee.taa_render_samples = 16 if QUICK else 64
cam = bpy.data.objects['Camera']; cam.data.type = 'ORTHO'
meshes = [o for o in bpy.data.objects if o.type == 'MESH' and o.name != 'Ground']

def snapshot(action, frame=1):
    rig.animation_data.action = bpy.data.actions[action]
    sc.frame_set(frame)
    keep = {pb.name: (pb.location.copy(), pb.rotation_quaternion.copy(), pb.rotation_euler.copy(), pb.scale.copy(), pb.rotation_mode)
            for pb in rig.pose.bones}
    rig.animation_data.action = None
    for pb in rig.pose.bones:
        l, q, e, s, m = keep[pb.name]
        pb.rotation_mode = m; pb.location = l; pb.rotation_quaternion = q; pb.rotation_euler = e; pb.scale = s
    return keep

def pose(base='Pose_Stand', frame=1, rot=None, loc=None, scale=None):
    """rot: {bone: (x, y, z) degrees added in the bone's local space}."""
    snapshot(base, frame)
    rig.pose.bones['root'].location.x = 0; rig.pose.bones['root'].location.y = 0
    for b, (x, y, z) in (rot or {}).items():
        pb = rig.pose.bones[b]
        q = pb.rotation_quaternion.copy() if pb.rotation_mode == 'QUATERNION' else pb.rotation_euler.to_quaternion()
        q = q @ Euler((math.radians(x), math.radians(y), math.radians(z)), 'XYZ').to_quaternion()
        pb.rotation_mode = 'QUATERNION'; pb.rotation_quaternion = q
    for b, v in (loc or {}).items():
        pb = rig.pose.bones[b]; pb.location = pb.location + Vector(v)
    for b, v in (scale or {}).items():
        rig.pose.bones[b].scale = Vector(v)
    bpy.context.view_layer.update()

def world_bbox():
    dg = bpy.context.evaluated_depsgraph_get()
    lo = Vector((1e9,) * 3); hi = Vector((-1e9,) * 3)
    for o in meshes:
        ev = o.evaluated_get(dg); me = ev.to_mesh()
        for v in me.vertices:
            p = ev.matrix_world @ v.co
            lo = Vector(map(min, lo, p)); hi = Vector(map(max, hi, p))
        ev.to_mesh_clear()
    return lo, hi

# Scale: standing height → STAND_PX.
pose('Pose_Stand')
lo, hi = world_bbox()
UPP = (hi.z - max(lo.z, 0)) / STAND_PX           # world units per pixel
cam.data.ortho_scale = UPP * H                    # portrait: ortho_scale spans the height

def camera(yaw, pitch=0, shift=(0, 0)):
    """Orbit around the vertical axis through the root; z = 0 lands on GROUND_Y."""
    a = math.radians(yaw); r = 8
    zc = (GROUND_Y - H / 2) * UPP                  # camera centre height above the ground
    right = Vector((math.cos(a), math.sin(a), 0))
    cam.location = Vector((r * math.sin(a), -r * math.cos(a), zc)) + right * shift[0] * UPP + Vector((0, 0, shift[1] * UPP))
    cam.rotation_euler = Euler((math.radians(90 + pitch), 0, a), 'XYZ')

def shot(row, col, yaw=12, shift=(0, 0)):
    camera(yaw, shift=shift)
    sc.render.filepath = os.path.join(FR, f'r{row:02d}_c{col}.png')
    bpy.ops.render.render(write_still=True)

def s(x): return math.sin(x)
TAU = math.tau
meta = {}

# Row 0 idle: tiny head tilt and breathing.
for i in range(6):
    u = i / 6 * TAU
    pose(rot={'head': (2 * s(u), 3 * s(u), 0), 'root': (0, 1.5 * s(u + 1), 0)}, scale={'chest': (1, 1, 1 + 0.02 * s(u))})
    shot(0, i)
# Rows 1/2 crawl right / left: 8 of the 24-frame Crawl_Walk loop, seen from the side.
for row, yaw in ((1, -90), (2, 90)):
    for i in range(8):
        pose('Crawl_Walk', frame=1 + i * 3)
        shot(row, i, yaw=yaw)
# Row 3 "wave": her arms hide under the hair, so a happy side-to-side wiggle.
for i in range(4):
    u = i / 4 * TAU
    pose(rot={'root': (0, 9 * s(u), 0), 'head': (0, -6 * s(u), 0)}, loc={'root': (0, 0, 0.03 * abs(s(u)))})
    shot(3, i)
# Row 4 jump: crouch, stretch, air, land, settle.
J = [dict(scale={'root': (1.12, 1.12, 0.84)}), dict(scale={'root': (0.94, 0.94, 1.1)}, loc={'root': (0, 0, 0.12)}),
     dict(rot={'leg.L': (-35, 0, 0), 'leg.R': (-35, 0, 0), 'head': (8, 0, 0)}, loc={'root': (0, 0, 0.32)}),
     dict(scale={'root': (1.08, 1.08, 0.9)}), dict()]
for i, kw in enumerate(J):
    pose(**kw); shot(4, i)
# Row 5 failed: head drops, dizzy wobble.
for i in range(8):
    u = i / 8 * TAU
    pose(rot={'head': (-18, 7 * s(u), 0), 'root': (0, 4 * s(u), 0)}, scale={'root': (1.03, 1.03, 0.96)})
    shot(5, i)
# Row 6 waiting: looks up and sways, hopeful.
for i in range(6):
    u = i / 6 * TAU
    pose(rot={'head': (16, 4 * s(u), 0), 'root': (0, 3 * s(u), 0)})
    shot(6, i)
# Row 7 work: thinking — looks left, then right.
for i in range(6):
    u = i / 6 * TAU
    pose(rot={'head': (-4, 4 * s(u), 16 * s(u)), 'root': (0, 0, 6 * s(u))})
    shot(7, i)
# Row 8 review: pleased bounce.
for i in range(6):
    u = i / 6 * TAU
    pose(rot={'head': (6, 8 * s(u), 0), 'root': (0, 3 * s(u), 0)}, loc={'root': (0, 0, 0.04 * max(0, s(u)))})
    shot(8, i)
# Rows 9–10: 16 look directions (0° = up, clockwise), head only.
for d in range(16):
    th = math.radians(d * 22.5)
    pose(rot={'head': (16 * math.cos(th), 0, -26 * math.sin(th))})
    shot(9 + d // 8, d % 8)
# Row 11 perch: sitting on an edge, legs hanging forward and swinging.
for i in range(6):
    u = i / 6 * TAU
    pose(rot={'leg.L': (-40 + 18 * s(u), 0, 0), 'leg.R': (-40 - 18 * s(u), 0, 0), 'head': (0, 3 * s(u), 0)})
    shot(11, i)
# Row 12 dance: lean and bounce, kicking alternate legs.
for i in range(8):
    u = i / 8 * TAU
    pose(rot={'root': (0, 12 * s(u), 0), 'head': (0, -9 * s(u), 0), 'leg.L': (30 * max(0, s(u)), 0, 0), 'leg.R': (30 * max(0, -s(u)), 0, 0)},
         loc={'root': (0, 0, 0.05 * abs(math.cos(u)))})
    shot(12, i)
# Row 13 sleep: lying belly-down (the crawl pose), breathing, seen three-quarter so her face shows.
for i in range(6):
    u = i / 6 * TAU
    pose('Pose_Crawl', scale={'chest': (1, 1, 1 + 0.03 * s(u))}, rot={'head': (-6, 0, 0)})
    shot(13, i, yaw=40)
# Row 14 dangle: held up, kicking.
for i in range(4):
    u = i / 4 * TAU
    pose(rot={'leg.L': (35 * s(u), 0, 0), 'leg.R': (-35 * s(u), 0, 0), 'head': (-6, 5 * s(u), 0)})
    shot(14, i)

# Seat line for row 11: the hips, projected into the frame.
from bpy_extras.object_utils import world_to_camera_view
pose(rot={'leg.L': (-40, 0, 0), 'leg.R': (-40, 0, 0)}); camera(12)
hp = rig.matrix_world @ rig.pose.bones['hips'].head
v = world_to_camera_view(sc, cam, hp)
meta['seatY'] = round((1 - v.y) * H / K)
json.dump(meta, open(os.path.join(OUT, 'render-meta.json'), 'w'))
print('DONE', meta)
