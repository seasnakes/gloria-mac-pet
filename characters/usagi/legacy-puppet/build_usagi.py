"""
Abstract Usagi: turn the plush photo (source.png, white background) into a floppy cut-out puppet and render a
desk-pet sprite sheet with the same layout as GLORIA's: 8 columns × 15 rows of 192×208 cells, feet on y=202.

  python3 build_usagi.py            # writes usagi.png, usagi.json, preview.png next to this script

Every foreground pixel belongs to its nearest bone; limbs get extra material around their root joint and are
drawn under the torso, so joints don't open when a limb rotates. Angles are clockwise degrees on screen.
"""
import json, math
from collections import deque
from pathlib import Path
import numpy as np
from PIL import Image, ImageFilter

HERE = Path(__file__).resolve().parent
CW, CH, COLS, ROWS = 192, 208, 8, 15
FEET = 202

# ---------------------------------------------------------------- cut-out
src = Image.open(HERE / "source.png").convert("RGB")
A = np.asarray(src).astype(int)
H, W, _ = A.shape
white = (A.min(axis=2) > 232) & ((A.max(axis=2) - A.min(axis=2)) < 18)
bg = np.zeros((H, W), bool)
q = deque()
for x in range(W):
    for y in (0, H - 1):
        if white[y, x]: bg[y, x] = True; q.append((y, x))
for y in range(H):
    for x in (0, W - 1):
        if white[y, x] and not bg[y, x]: bg[y, x] = True; q.append((y, x))
while q:
    y, x = q.popleft()
    for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
        ny, nx = y + dy, x + dx
        if 0 <= ny < H and 0 <= nx < W and white[ny, nx] and not bg[ny, nx]:
            bg[ny, nx] = True; q.append((ny, nx))
alpha = Image.fromarray(((~bg) * 255).astype("uint8")).filter(ImageFilter.MedianFilter(5)).filter(ImageFilter.GaussianBlur(0.7))
rgba = src.convert("RGBA"); rgba.putalpha(alpha)
FG = np.asarray(alpha) > 8

# ---------------------------------------------------------------- skeleton (source pixels)
P = {"pelvis": (585, 790), "neck": (578, 575), "top": (560, 200), "shL": (535, 612), "handL": (375, 505),
     "shR": (640, 640), "handR": (822, 736), "hipL": (570, 790), "knee": (455, 930), "footL": (515, 1050),
     "hipR": (615, 800), "footR": (805, 960)}
BONES = {  # name: (root, tip, parent)
    "torso": ("pelvis", "neck", None), "head": ("neck", "top", "torso"),
    "armL": ("shL", "handL", "torso"), "armR": ("shR", "handR", "torso"),
    "thighL": ("hipL", "knee", "torso"), "shinL": ("knee", "footL", "thighL"), "legR": ("hipR", "footR", "torso"),
}
ORDER = ["torso", "legR", "thighL", "shinL", "armR", "head", "armL"]   # back to front; joint caps go under each limb
CAPS = {"legR": ("hipR", 30), "thighL": ("hipL", 30), "shinL": ("knee", 26), "armR": ("shR", 27), "armL": ("shL", 27)}

ys, xs = np.nonzero(FG)
pts = np.stack([xs, ys], 1).astype(float)
def seg_dist(p, a, b):
    a, b = np.array(a, float), np.array(b, float)
    ab = b - a
    t = np.clip(((p - a) @ ab) / (ab @ ab), 0, 1)
    return np.linalg.norm(p - (a + t[:, None] * ab), axis=1)
names = list(BONES)
# Distance relative to each bone's thickness, and each bone only claims pixels in its own region:
# the head above the neck, legs below the hips, arms only beyond their shoulder.
RADIUS = {"torso": 58, "head": 110, "armL": 36, "armR": 36, "thighL": 40, "shinL": 40, "legR": 40}
AXIS = dict(P); AXIS["chest"] = (578, 520)
def seg_t(p, a, b):
    a, b = np.array(a, float), np.array(b, float); ab = b - a
    return ((p - a) @ ab) / (ab @ ab)
cols = []
for n in names:
    root, tip, _ = BONES[n]
    a, b = (P["pelvis"], AXIS["chest"]) if n == "torso" else (P[root], P[tip])
    d = seg_dist(pts, a, b) / RADIUS[n]
    if n == "head": d[(pts[:, 1] > 590) | ((pts[:, 0] < 430) & (pts[:, 1] > 470))] = np.inf
    if n == "torso": d[pts[:, 1] < 585] = np.inf
    if n in ("thighL", "shinL", "legR"): d[pts[:, 1] < 760] = np.inf
    if n in ("armL", "armR"): d[seg_t(pts, P[root], P[tip]) < 0.12] = np.inf
    cols.append(d)
D = np.stack(cols, 1)
owner = np.argmin(D, 1)
# Each part keeps only its largest connected piece; stray islands go to their next-closest bone.
from scipy import ndimage
for _ in range(3):
    moved = 0
    for i, n in enumerate(names):
        m = np.zeros((H, W), bool); sel = owner == i; m[ys[sel], xs[sel]] = True
        lab, k = ndimage.label(m)
        if k <= 1: continue
        sizes = ndimage.sum(m, lab, range(1, k + 1)); keep = 1 + int(np.argmax(sizes))
        stray = sel & (lab[ys, xs] != keep)
        if stray.any():
            D[stray, i] = np.inf; owner[stray] = np.argmin(D[stray], 1); moved += int(stray.sum())
    if not moved: break

src_arr = np.asarray(rgba).copy()
parts = {}
for i, n in enumerate(names):
    m = np.zeros((H, W), bool)
    m[ys[owner == i], xs[owner == i]] = True
    root = np.array(P[BONES[n][0]], float)
    # Extra material around the root joint, hidden under whatever is drawn on top of it.
    near = np.linalg.norm(pts - root, axis=1) < 0
    m[ys[near], xs[near]] = True
    if n == "torso":   # the torso also covers the neck and hip roots from below
        for j in ("neck",):
            near = np.linalg.norm(pts - np.array(P[j], float), axis=1) < 30
            m[ys[near], xs[near]] = True
    a = src_arr.copy()
    a[..., 3] = np.where(m, a[..., 3], 0)
    parts[n] = Image.fromarray(a).convert("RGBa")    # premultiplied for clean resampling

# Joint caps: a round patch in the plush's own colour, drawn under each limb so its root never shows a gap.
CAP_RGB = {}
for n, (j, r) in CAPS.items():
    c = np.array(P[j], float)
    sel = np.linalg.norm(pts - c, axis=1) < 26
    CAP_RGB[n] = tuple(int(v) for v in np.median(src_arr[ys[sel], xs[sel], :3], axis=0))

# ---------------------------------------------------------------- posing
def T(x, y): return np.array([[1, 0, x], [0, 1, y], [0, 0, 1]], float)
def R(deg):
    r = math.radians(deg); c, s = math.cos(r), math.sin(r)
    return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]], float)
def S(sx, sy): return np.array([[sx, 0, 0], [0, sy, 0], [0, 0, 1]], float)

def bone_angle(n):
    (ax, ay), (bx, by) = P[BONES[n][0]], P[BONES[n][1]]
    return math.degrees(math.atan2(by - ay, bx - ax))
def aim(n, deg_from_down):
    """Rotation that points a bone (in its rest frame) at deg_from_down (0 = straight down, + = toward screen-left)."""
    target = 90 + deg_from_down
    return target - bone_angle(n)

def matrices(pose):
    g = pose.get("_", {})
    px, py = P["pelvis"]
    M = {"torso": T(px + g.get("dx", 0), py + g.get("dy", 0)) @ R(g.get("rot", 0) + pose.get("torso", 0)) @ S(g.get("sx", 1), g.get("sy", 1)) @ T(-px, -py)}
    for n in ["head", "armL", "armR", "thighL", "legR", "shinL"]:
        root, _, parent = BONES[n]
        rx, ry = P[root]
        M[n] = M[parent] @ T(rx, ry) @ R(pose.get(n, 0)) @ T(-rx, -ry)
    return M

SC = 196 / (1065 - 140)                 # cell px per source px (idle height incl. ears ≈ 196 px)
BIG = 4                                 # render at 4× the cell, then downsample
PADX = 120                              # extra room either side so wide poses aren't clipped

def render(pose, flip=False):
    M = matrices(pose)
    # source → big canvas: centre the pelvis at x = 96, put it at a provisional height; grounding happens later
    place = T((96 + PADX) * BIG, 150 * BIG) @ S(SC * BIG, SC * BIG) @ T(-P["pelvis"][0], -P["pelvis"][1])
    canvas = Image.new("RGBa", ((CW + 2 * PADX) * BIG, (CH + 120) * BIG), (0, 0, 0, 0))
    from PIL import ImageDraw
    for n in ORDER:
        if n in CAPS:
            j, r = CAPS[n]
            parent = BONES[n][2]
            c = place @ M[parent] @ np.array([P[j][0], P[j][1], 1.0])
            rr = r * SC * BIG
            cap = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
            ImageDraw.Draw(cap).ellipse([c[0] - rr, c[1] - rr, c[0] + rr, c[1] + rr], fill=CAP_RGB[n] + (255,))
            canvas = Image.alpha_composite(canvas.convert("RGBA"), cap).convert("RGBa")
        F = place @ M[n]
        inv = np.linalg.inv(F)
        layer = parts[n].transform(canvas.size, Image.AFFINE, data=tuple(inv[0]) + tuple(inv[1]), resample=Image.BICUBIC)
        canvas = Image.alpha_composite(canvas.convert("RGBA"), layer.convert("RGBA")).convert("RGBa")
    small = canvas.resize((CW + 2 * PADX, CH + 120), Image.LANCZOS).convert("RGBA")
    if flip: small = small.transpose(Image.FLIP_LEFT_RIGHT)
    pelvis_y = 150                      # pelvis row in `small`
    return small, pelvis_y

def bottom(img):
    a = np.asarray(img)[..., 3]
    rows = np.nonzero(a.max(1) > 40)[0]
    return int(rows.max()) + 1 if len(rows) else 0

def top(img):
    a = np.asarray(img)[..., 3]
    rows = np.nonzero(a.max(1) > 40)[0]
    return int(rows.min()) if len(rows) else 0

sheet = Image.new("RGBA", (CW * COLS, CH * ROWS), (0, 0, 0, 0))
def put(row, col, img, dy, dx=0):
    """img is CH+120 tall; shift it by dy and paste the CW×CH window."""
    cell = Image.new("RGBA", (CW, CH), (0, 0, 0, 0))
    cell.alpha_composite(img, (-PADX + dx, dy))
    sheet.alpha_composite(cell, (col * CW, row * CH))
    return cell

def grounded(row, col, pose, flip=False, lift=0):
    img, _ = render(pose, flip)
    put(row, col, img, FEET - bottom(img) - lift)

s = lambda x: math.sin(x)
TAU = math.tau
idle_dy = None

# Row 0 idle: the photo's own pose, gently swaying.
for i in range(6):
    u = i / 6 * TAU
    grounded(0, i, {"torso": 2 * s(u), "head": -3 * s(u), "armL": 6 * s(u), "armR": -4 * s(u)})
# Rows 1/2 walk: floppy alternating legs, arms swinging against them, leaning into the step.
WALK = []
for i in range(8):
    u = i / 8 * TAU
    WALK.append({"_": {"rot": 7}, "torso": 0, "head": -4 + 3 * s(2 * u),
                 "thighL": aim("thighL", -28 * s(u)), "shinL": 12 + 14 * max(0, s(u)),
                 "legR": aim("legR", 28 * s(u)),
                 "armL": aim("armL", 62 + 28 * s(u)), "armR": aim("armR", -62 + 28 * s(u))})
for i, p in enumerate(WALK):
    grounded(1, i, p)
    grounded(2, i, p, flip=True)
# Row 3 wave: the raised arm flaps.
for i in range(4):
    grounded(3, i, {"armL": [-22, 6, 24, 6][i], "head": [-4, 0, 4, 0][i], "torso": [1, 0, -1, 0][i]})
# Row 4 jump: crouch, stretch, air, land, settle.
JUMP = [({"_": {"sy": 0.9, "sx": 1.06}, "thighL": aim("thighL", -40), "shinL": 40, "legR": aim("legR", 50), "armL": 20, "armR": -10}, 0),
        ({"_": {"sy": 1.05}, "thighL": aim("thighL", -8), "legR": aim("legR", 8), "armL": aim("armL", 175), "armR": aim("armR", -170)}, 10),
        ({"thighL": aim("thighL", -35), "shinL": 25, "legR": aim("legR", 40), "armL": aim("armL", 160), "armR": aim("armR", -155), "head": 4}, 26),
        ({"_": {"sy": 0.93, "sx": 1.04}, "thighL": aim("thighL", -30), "shinL": 20, "legR": aim("legR", 35)}, 0),
        ({}, 0)]
for i, (p, lift) in enumerate(JUMP):
    grounded(4, i, p, lift=lift)
# Row 5 failed: head drops, arms hang, slow wobble.
for i in range(8):
    u = i / 8 * TAU
    grounded(5, i, {"head": 28 + 6 * s(u), "torso": 6 + 3 * s(u), "armL": aim("armL", 48), "armR": aim("armR", -44),
                    "thighL": aim("thighL", -6), "legR": aim("legR", 10)})
# Row 6 waiting: both arms up, bouncing on the spot.
for i in range(6):
    u = i / 6 * TAU
    grounded(6, i, {"armL": aim("armL", 165) + 8 * s(u), "armR": aim("armR", -165) - 8 * s(u), "head": 5 * s(u),
                    "thighL": aim("thighL", -10), "legR": aim("legR", 10)}, lift=int(4 * max(0, s(u))))
# Row 7 work: hand on head, thinking, head tilting.
for i in range(6):
    u = i / 6 * TAU
    grounded(7, i, {"armL": aim("armL", 150) - 25, "head": -10 + 7 * s(u), "torso": -2 * s(u), "armR": aim("armR", -55),
                    "thighL": aim("thighL", -12), "legR": aim("legR", 14)})
# Row 8 review: pleased, arms alternating up.
for i in range(6):
    u = i / 6 * TAU
    grounded(8, i, {"armL": aim("armL", 150 + 20 * s(u)), "armR": aim("armR", -150 + 20 * s(u)), "head": 6 * s(u)})
# Rows 9–10: 16 look directions — the head leans toward where she looks.
for d in range(16):
    th = math.radians(d * 22.5)
    grounded(9 + d // 8, d % 8, {"head": 16 * math.sin(th), "torso": 3 * math.sin(th), "_": {"dy": -6 * math.cos(th)}})
# Row 11 perch: legs hang down over an edge and swing; the pelvis is the seat.
PERCH = []
for i in range(6):
    u = i / 6 * TAU
    p = {"thighL": aim("thighL", 6 + 12 * s(u)), "shinL": -aim("shinL", 0) * 0 + 0, "legR": aim("legR", -6 - 12 * s(u)), "head": 3 * s(u)}
    PERCH.append(p)
SEAT = 149
for i, p in enumerate(PERCH):
    img, pelvis = render(p)
    put(11, i, img, SEAT - pelvis)
# Row 12 dance: the full abstract flail.
for i in range(8):
    u = i / 8 * TAU
    grounded(12, i, {"_": {"rot": 10 * s(u)}, "head": -14 * s(u),
                     "armL": aim("armL", 120 + 55 * s(u)), "armR": aim("armR", -120 + 55 * s(u)),
                     "thighL": aim("thighL", -25 * max(0, s(u)) - 4), "shinL": 25 * max(0, s(u)),
                     "legR": aim("legR", 25 * max(0, -s(u)) + 4)}, lift=int(5 * abs(math.cos(u))))
# Row 13 sleep: lying on her side, knees pulled up, breathing.
for i in range(6):
    u = i / 6 * TAU
    img, _ = render({"_": {"rot": -90, "sy": 1 + 0.02 * s(u)}, "thighL": aim("thighL", -60), "shinL": 60, "legR": aim("legR", 40),
                     "armL": aim("armL", 40), "armR": aim("armR", -20), "head": -6})
    al = np.asarray(img)[..., 3]; cols = np.nonzero(al.max(0) > 40)[0]
    span = cols.max() - cols.min()
    if span > CW - 8:
        k = (CW - 8) / span
        img = img.resize((int(img.width * k), int(img.height * k)), Image.LANCZOS)
        al = np.asarray(img)[..., 3]; cols = np.nonzero(al.max(0) > 40)[0]
    shift = Image.new("RGBA", (CW + 2 * PADX, CH + 120), (0, 0, 0, 0))
    shift.alpha_composite(img, (int(96 + PADX - (cols.min() + cols.max()) / 2), 0))
    put(13, i, shift, FEET - bottom(shift))
# Row 14 dangle: held up by the head between the ears; everything hangs and kicks.
GRAB_SRC = (562, 300)
for i in range(4):
    u = i / 4 * TAU
    p = {"armL": aim("armL", 52 + 15 * s(u)), "armR": aim("armR", -52 + 15 * s(u)),
         "thighL": aim("thighL", -10 + 22 * s(u)), "shinL": -10 * s(u), "legR": aim("legR", 10 + 22 * s(u)), "head": -4}
    img, pelvis = render(p)
    # GRAB_SRC in `small` coordinates (the head is barely rotated, so map it with the rest placement)
    gy = pelvis + (GRAB_SRC[1] - P["pelvis"][1]) * SC
    put(14, i, img, int(round(42 - gy)))

sheet.save(HERE / "usagi.png")
# Contact line of the dangle grab point and the perch seat, in cell px.
meta = {
    "id": "usagi", "name": "抽象乌萨奇", "sheet": "usagi.png", "cell": [CW, CH], "feetY": FEET,
    "seatY": SEAT, "grab": [96, 42], "look": True, "scale": 1.0,
    "anims": {
        "idle": {"row": 0, "count": 6, "frameMs": 180}, "runRight": {"row": 1, "count": 8, "frameMs": 85},
        "runLeft": {"row": 2, "count": 8, "frameMs": 85}, "wave": {"row": 3, "count": 4, "frameMs": 130},
        "jump": {"row": 4, "count": 5, "frameMs": 100}, "failed": {"row": 5, "count": 8, "frameMs": 140},
        "waiting": {"row": 6, "count": 6, "frameMs": 150}, "work": {"row": 7, "count": 6, "frameMs": 180},
        "review": {"row": 8, "count": 6, "frameMs": 150}, "perch": {"row": 11, "count": 6, "frameMs": 200},
        "dance": {"row": 12, "count": 8, "frameMs": 130}, "sleep": {"row": 13, "count": 6, "frameMs": 320},
        "dangle": {"row": 14, "count": 4, "frameMs": 110},
    },
}
(HERE / "usagi.json").write_text(json.dumps(meta, ensure_ascii=False, indent=1))

# Preview: every row on a grey checker.
pv = Image.new("RGBA", sheet.size, (70, 70, 82, 255)); pv.alpha_composite(sheet)
pv.convert("RGB").save(HERE / "preview.png")
print("ok", sheet.size)
