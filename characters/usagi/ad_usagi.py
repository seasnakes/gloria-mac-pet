"""
Abstract Usagi with Animated Drawings (facebookresearch/AnimatedDrawings, local copy in
/Users/seasnake/Developer/animated-drawings-local) — the same rig approach as DoroPet's standing-prototype/animated-rig.

The plush photo becomes an AD character (texture.png, mask.png, char_cfg.yaml in ./ad). AD builds its ARAP mesh from
the mask; BVH clips are retargeted onto the 16-joint skeleton (plus a `head` joint for tilts), and the mesh bends
with ARAP. Instead of AD's GLFW window, this script takes the deformed vertices straight from AnimatedDrawing and
paints the photo onto them with OpenCV, so it runs offscreen and keeps alpha.

  /Users/seasnake/Developer/animated-drawings-local/.venv/bin/python ad_usagi.py
"""
import json, math, os, sys
from pathlib import Path
import numpy as np
import cv2
import yaml

HERE = Path(__file__).resolve().parent
ADROOT = Path("/Users/seasnake/Developer/animated-drawings-local")
CHAR = HERE / "ad"
sys.path.insert(0, str(ADROOT))
os.environ.setdefault("OPENBLAS_NUM_THREADS", "1")
import logging; logging.disable(logging.INFO)
from animated_drawings.config import CharacterConfig, MotionConfig, RetargetConfig
from animated_drawings.model.animated_drawing import AnimatedDrawing

CW, CH, COLS, ROWS = 192, 208, 8, 15
FEET = 202
TARGET_H = 196                      # standing height (ear tips → feet) in the cell

# ------------------------------------------------------------------ 1. AD character from the photo
src = cv2.cvtColor(cv2.imread(str(HERE / "source.png")), cv2.COLOR_BGR2RGB)
H0, W0 = src.shape[:2]
white = ((src.min(2) > 232) & ((src.max(2).astype(int) - src.min(2)) < 18)).astype(np.uint8) * 255
ff = np.zeros((H0 + 2, W0 + 2), np.uint8)
for seed in [(0, 0), (W0 - 1, 0), (0, H0 - 1), (W0 - 1, H0 - 1)]:
    if white[seed[1], seed[0]] == 255: cv2.floodFill(white, ff, seed, 128)
fg = (white != 128).astype(np.uint8) * 255
fg = cv2.medianBlur(fg, 5)
n, lab, stats, _ = cv2.connectedComponentsWithStats(fg)
fg = (lab == 1 + np.argmax(stats[1:, cv2.CC_STAT_AREA])).astype(np.uint8) * 255
ys, xs = np.nonzero(fg)
M = 40
X0, Y0 = max(0, xs.min() - M), max(0, ys.min() - M)
X1, Y1 = min(W0, xs.max() + M), min(H0, ys.max() + M)
tex = np.dstack([src, fg])[Y0:Y1, X0:X1].copy()
msk = fg[Y0:Y1, X0:X1].copy()
TH, TW = msk.shape
CHAR.mkdir(exist_ok=True)
cv2.imwrite(str(CHAR / "texture.png"), cv2.cvtColor(tex, cv2.COLOR_RGBA2BGRA))
cv2.imwrite(str(CHAR / "mask.png"), msk)

J = {  # source-photo pixels; AD's "right" is the character's right (screen left)
    "root": (585, 790), "hip": (585, 790), "torso": (582, 690), "neck": (578, 578), "head": (566, 330),
    "right_shoulder": (536, 612), "right_elbow": (455, 556), "right_hand": (380, 506),
    "left_shoulder": (640, 640), "left_elbow": (730, 688), "left_hand": (820, 736),
    "right_hip": (566, 800), "right_knee": (455, 930), "right_foot": (513, 1048),
    "left_hip": (616, 802), "left_knee": (710, 880), "left_foot": (803, 958),
}
# Snap joints onto the plush: mid-limb joints to the local centreline (distance-transform ridge), tips just inside.
DT = cv2.distanceTransform((fg > 0).astype(np.uint8), cv2.DIST_L2, 5)
def snap(name, r):
    x, y = J[name]
    win = DT[y - r:y + r + 1, x - r:x + r + 1].astype(np.float64)
    yy, xx = np.mgrid[-r:r + 1, -r:r + 1]
    if name.endswith(("hand", "foot")):
        score = np.where(win >= 12, -np.hypot(xx, yy), -1e9)      # nearest point at least 12 px inside
    else:
        score = win - 0.18 * np.hypot(xx, yy)                      # ridge, staying near the guess
    dy, dx = np.unravel_index(np.argmax(score), score.shape)
    J[name] = (int(x + dx - r), int(y + dy - r))
for n in ["right_elbow", "left_elbow", "right_knee", "left_knee", "torso", "neck"]: snap(n, 40)
for n in ["right_hand", "left_hand", "right_foot", "left_foot", "right_shoulder", "left_shoulder", "right_hip", "left_hip"]: snap(n, 30)

PARENT = {"root": None, "hip": "root", "torso": "hip", "neck": "torso", "head": "neck",
          "right_shoulder": "torso", "right_elbow": "right_shoulder", "right_hand": "right_elbow",
          "left_shoulder": "torso", "left_elbow": "left_shoulder", "left_hand": "left_elbow",
          "right_hip": "root", "right_knee": "right_hip", "right_foot": "right_knee",
          "left_hip": "root", "left_knee": "left_hip", "left_foot": "left_knee"}
cfg = {"width": TW, "height": TH,
       "skeleton": [{"name": n, "parent": PARENT[n], "loc": [int(J[n][0] - X0), int(J[n][1] - Y0)]} for n in PARENT]}
(CHAR / "char_cfg.yaml").write_text(yaml.safe_dump(cfg, sort_keys=False))

# ------------------------------------------------------------------ 2. drawing an AnimatedDrawing's current mesh
def load(motion, retarget):
    os.chdir(ADROOT)
    return AnimatedDrawing(CharacterConfig(str(CHAR / "char_cfg.yaml")),
                           RetargetConfig(str(ADROOT / "examples/config/retarget" / retarget)),
                           MotionConfig(str(ADROOT / "examples/config/motion" / motion)))

TEX = tex.astype(np.float32)
TEX[..., :3] *= TEX[..., 3:4] / 255.0          # premultiplied, so edges resample cleanly
ORDER = ["hip", "torso", "neck", "head", "right_hip", "left_hip", "right_knee", "right_foot", "left_knee", "left_foot",
         "right_shoulder", "left_shoulder", "right_elbow", "right_hand", "left_elbow", "left_hand"]

class Painter:
    """Maps AD world coordinates back to texture pixels (fitted at rest) and paints triangles in body order."""
    def __init__(self, ad):
        D = ad.char_cfg.img_dim
        v = ad.mesh["vertices"]
        self.src = np.stack([v[:, 0] * D, (TH - 1) - v[:, 1] * D], 1).astype(np.float32)   # texture px (x, y)
        rest = ad.vertices[:, :2].astype(np.float64)
        A = np.hstack([self.src, np.ones((len(v), 1))])
        sol, *_ = np.linalg.lstsq(A, rest, rcond=None)                 # texture px → world
        T = np.eye(3); T[:2, :] = sol.T
        self.inv = np.linalg.inv(T)                                     # world → texture px
        tris = []
        for name in ORDER + [n for n in ad.joint_to_tri_v_idx if n not in ORDER]:
            idx = ad.joint_to_tri_v_idx.get(name)
            if idx is not None and len(idx): tris.append(np.asarray(idx).reshape(-1, 3))
        self.tris = np.vstack(tris)

    def to_px(self, world):
        w = np.hstack([world, np.ones((len(world), 1))])
        return (w @ self.inv.T)[:, :2]

    def paint(self, world, pad=600, k=1.0):
        """Returns an RGBA float image of the texture frame grown by `pad` px on every side, scaled by k."""
        dst = (self.to_px(world) + pad) * k
        Hc, Wc = int((TH + 2 * pad) * k), int((TW + 2 * pad) * k)
        out = np.zeros((Hc, Wc, 4), np.float32)
        for t in self.tris:
            s = self.src[t]; d = dst[t].astype(np.float32)
            x, y, w, h = cv2.boundingRect(d)
            if w <= 0 or h <= 0 or x < 0 or y < 0 or x + w > Wc or y + h > Hc: continue
            Mx = cv2.getAffineTransform(s, d - np.float32([x, y]))
            patch = cv2.warpAffine(TEX, Mx, (w, h), flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT, borderValue=(0, 0, 0, 0))
            m = np.zeros((h, w), np.float32)
            c = d - np.float32([x, y]); ctr = c.mean(0)
            # Hard-edged, slightly grown triangles so neighbours overlap (no hairline cracks); the 4× downscale
            # afterwards smooths the outline.
            grow = c + (c - ctr) / (np.linalg.norm(c - ctr, axis=1, keepdims=True) + 1e-6) * 1.0
            cv2.fillConvexPoly(m, np.round(grow * 4).astype(np.int32), 1.0, lineType=cv2.LINE_8, shift=2)
            a = patch[..., 3:4] / 255.0 * m[..., None]
            region = out[y:y + h, x:x + w]
            region[..., :3] = patch[..., :3] * m[..., None] + region[..., :3] * (1 - a)
            region[..., 3:4] = a * 255 + region[..., 3:4] * (1 - a)
        return out

# ------------------------------------------------------------------ 3. rest pose, custom poses, BVH frames
from animated_drawings.model.animated_drawing import AnimatedDrawingRig, AnimatedDrawingsJoint
os.chdir(ADROOT)
CFG = CharacterConfig(str(CHAR / "char_cfg.yaml"))
def joints_of(rig):
    out, stack = {}, [rig.root_joint]
    while stack:
        j = stack.pop(); out[j.name] = j
        stack += [c for c in j.get_children() if isinstance(c, AnimatedDrawingsJoint)]
    return out
REST_RIG = AnimatedDrawingRig(CFG)
REST_TH = {n: j.starting_theta for n, j in joints_of(REST_RIG).items() if j.get_parent() is not None}
NAMES = [j["name"] for j in CFG.skeleton]          # order of get_joints_2D_positions()
DRIVEN = ["torso", "neck", "head", "right_elbow", "right_hand", "left_elbow", "left_hand",
          "right_knee", "right_foot", "left_knee", "left_foot"]

BASE = load("wave_hello.yaml", "fair1_ppf.yaml")    # any clip: we only need its mesh and ARAP solver here
PAINT = None

def pose_world(deltas=None, aims=None):
    """Joint angles (degrees, counter-clockwise on screen) → (mesh world xy, joint world xy)."""
    rig = AnimatedDrawingRig(CFG)
    th = {n: REST_TH[n] for n in DRIVEN}
    for n, d in (deltas or {}).items(): th[n] += d
    for n, a in (aims or {}).items(): th[n] = a
    rig.set_global_orientations(th)
    cp = rig.get_joints_2D_positions()
    return BASE.arap.solve(cp), cp

def aim(deg_from_down):
    """Global bone angle pointing `deg_from_down` from straight down toward screen-left (negative = screen-right)."""
    return (180 - deg_from_down) % 360

class RestPainter(Painter):
    def __init__(self, ad):
        D = ad.char_cfg.img_dim
        v = ad.mesh["vertices"]
        self.src = np.stack([v[:, 0] * D, (TH - 1) - v[:, 1] * D], 1).astype(np.float32)
        rest, _ = pose_world()
        A = np.hstack([self.src, np.ones((len(v), 1))])
        sol, *_ = np.linalg.lstsq(A, rest, rcond=None)
        T = np.eye(3); T[:2, :] = sol.T
        self.inv = np.linalg.inv(T)
        tris = []
        for name in ORDER + [n for n in ad.joint_to_tri_v_idx if n not in ORDER]:
            idx = ad.joint_to_tri_v_idx.get(name)
            if idx is not None and len(idx): tris.append(np.asarray(idx).reshape(-1, 3))
        self.tris = np.vstack(tris)
PAINT = RestPainter(BASE)

def clip_world(ad, t):
    ad.set_time(t); ad.update()
    return ad.vertices[:, :2].astype(np.float64), ad.rig.get_joints_2D_positions()

def flat(rgba_f):
    bg = np.full(rgba_f.shape[:2] + (3,), 70, np.float32)
    a = rgba_f[..., 3:4] / 255
    return np.clip(rgba_f[..., :3] + bg * (1 - a), 0, 255).astype(np.uint8)

if "--sample" in sys.argv:
    S = Path("/private/tmp/claude-501/-Users-seasnake--claude-spares-spare-cAi3qC/487189f9-0051-41ac-a51e-e83b05e58ff9/scratchpad")
    rows = []
    w, _ = pose_world(); rows.append(("rest", [flat(PAINT.paint(w, pad=320, k=0.32))]))
    for motion, rt in [("wave_hello.yaml", "fair1_ppf.yaml"), ("jumping.yaml", "fair1_ppf.yaml"), ("dab.yaml", "fair1_ppf.yaml"),
                       ("jesse_dance.yaml", "mixamo_fff.yaml"), ("jumping_jacks.yaml", "cmu1_pfp.yaml"), ("zombie.yaml", "fair1_ppf.yaml")]:
        ad = load(motion, rt)
        dur = (ad.retargeter.bvh.frame_max_num - 1) * ad.retargeter.bvh.frame_time
        imgs = []
        for i in range(12):
            t = dur * i / 12
            w, _ = clip_world(ad, t)
            img = flat(PAINT.paint(w, pad=320, k=0.32))
            cv2.putText(img, f"{t:.1f}s", (6, 18), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255, 230, 0), 1)
            imgs.append(img)
        rows.append((f"{motion} {dur:.1f}s", imgs))
    hs = max(r[1][0].shape[0] for r in rows)
    lines = []
    for name, imgs in rows:
        line = np.hstack(imgs + [np.full_like(imgs[0], 70)] * (12 - len(imgs)))
        cv2.putText(line, name, (6, hs - 8), cv2.FONT_HERSHEY_SIMPLEX, 0.55, (255, 255, 255), 1)
        lines.append(line)
    sheet = np.vstack(lines)
    cv2.imwrite(str(S / "ad_sample.jpg"), cv2.cvtColor(sheet, cv2.COLOR_RGB2BGR), [cv2.IMWRITE_JPEG_QUALITY, 82])
    print("joints", {n: J[n] for n in ["right_elbow", "left_elbow", "right_knee", "left_knee", "right_hand", "left_foot"]})
    print("sample ok", sheet.shape)

# ------------------------------------------------------------------ 4. the sheet
MX, MT = 80, 90                                           # working margin around each cell (sides, top)

def build():
    root_i, head_i = NAMES.index("root"), NAMES.index("head")
    w0, j0 = pose_world()
    big0 = PAINT.paint(w0, pad=0, k=1.0)
    ys = np.nonzero(big0[..., 3].max(1) > 40)[0]
    SC = TARGET_H / (ys.max() - ys.min())                 # cell px per texture px
    K = 4 * SC                                            # paint at 4× the cell, then downsample
    PAD = 520
    WW, WH = CW + 2 * MX, CH + MT + 40                    # working canvas; the cell sits at (MX, MT)

    def render(world, joints, mode="ground", rotate=0, seat=None, grab=None, lift=0):
        """→ premultiplied float canvas WW×WH with the cell at (MX, MT), placed by `mode`."""
        big = PAINT.paint(world, pad=PAD, k=K)
        jp = (PAINT.to_px(joints) + PAD) * K
        if rotate:
            R = cv2.getRotationMatrix2D(tuple(jp[root_i]), rotate, 1.0)
            big = cv2.warpAffine(big, R, (big.shape[1], big.shape[0]), flags=cv2.INTER_LINEAR)
            jp = np.hstack([jp, np.ones((len(jp), 1))]) @ R.T
        small = cv2.resize(big, (big.shape[1] // 4, big.shape[0] // 4), interpolation=cv2.INTER_AREA)
        jc = jp / 4
        a = small[..., 3]
        rows = np.nonzero(a.max(1) > 40)[0]; cols = np.nonzero(a.max(0) > 40)[0]
        dx = (96 - (cols.min() + cols.max()) / 2) if mode == "lying" else 96 - jc[root_i][0]
        if mode in ("ground", "lying"): dy = FEET - (rows.max() + 1) - lift
        elif mode == "seat": dy = seat - jc[root_i][1]
        elif mode == "grab": dy = grab - jc[head_i][1]
        M2 = np.float32([[1, 0, dx + MX], [0, 1, dy + MT]])
        return cv2.warpAffine(small, M2, (WW, WH), flags=cv2.INTER_LINEAR), jc[root_i][1] + dy

    def fit_row(canvases, anchor):
        """Shrink a whole row by one factor around `anchor` (cell coords) if any frame leaves the cell."""
        ax, ay = anchor[0] + MX, anchor[1] + MT
        f = 1.0
        for c in canvases:
            a = c[..., 3]; rows = np.nonzero(a.max(1) > 40)[0]; cols = np.nonzero(a.max(0) > 40)[0]
            top, bot, left, right = rows.min() - MT, rows.max() + 1 - MT, cols.min() - MX, cols.max() + 1 - MX
            if top < 2: f = min(f, (anchor[1] - 2) / (anchor[1] - top))
            if bot > CH - 1: f = min(f, (CH - 1 - anchor[1]) / (bot - anchor[1]))
            if left < 2: f = min(f, (anchor[0] - 2) / (anchor[0] - left))
            if right > CW - 2: f = min(f, (CW - 2 - anchor[0]) / (right - anchor[0]))
        out = []
        for c in canvases:
            M3 = np.float32([[f, 0, ax - f * ax - MX], [0, f, ay - f * ay - MT]])
            cell = cv2.warpAffine(c, M3, (CW, CH), flags=cv2.INTER_AREA if f < 1 else cv2.INTER_LINEAR)
            rgb = np.where(cell[..., 3:4] > 0, cell[..., :3] * 255 / np.maximum(cell[..., 3:4], 1e-3), 0)
            out.append(np.dstack([np.clip(rgb, 0, 255), np.clip(cell[..., 3], 0, 255)]).astype(np.uint8))
        return out, f

    sheet = np.zeros((CH * ROWS, CW * COLS, 4), np.uint8)
    fits = {}
    def put_row(r, canvases, anchor=(96, FEET), mirror_to=None):
        cells, f = fit_row(canvases, anchor); fits[r] = round(f, 3)
        for c, img in enumerate(cells):
            sheet[r * CH:(r + 1) * CH, c * CW:(c + 1) * CW] = img
            if mirror_to is not None: sheet[mirror_to * CH:(mirror_to + 1) * CH, c * CW:(c + 1) * CW] = img[:, ::-1]
    S, TAU = math.sin, math.tau
    G = lambda *wj, **kw: render(*wj, **kw)[0]

    # 0 idle: the photo's own pose, swaying gently.
    put_row(0, [G(*pose_world({"torso": 2 * S(u), "neck": -1.5 * S(u), "head": 3 * S(u + .6), "right_elbow": 4 * S(u),
                                "right_hand": 7 * S(u + .4), "left_elbow": -3 * S(u), "left_hand": -5 * S(u)}))
                for u in [i / 6 * TAU for i in range(6)]])
    # 1/2 walk: alternate knee lifts, arms swinging, leaning into the step; left is the mirror image.
    walk = []
    for i in range(8):
        u = i / 8 * TAU; s = S(u); L, Rr = max(0, s), max(0, -s)
        walk.append(G(*pose_world({"torso": -5, "head": 4}, {
            "right_knee": aim(12 + 30 * L), "right_foot": aim(10 - 14 * L), "left_knee": aim(-(12 + 30 * Rr)), "left_foot": aim(-(10 - 14 * Rr)),
            "right_elbow": aim(42 + 20 * s), "right_hand": aim(30 + 22 * s), "left_elbow": aim(-(42 - 20 * s)), "left_hand": aim(-(30 - 22 * s))})))
    put_row(1, walk, mirror_to=2)
    # 3 wave: the raised arm from the photo, flapping from the elbow.
    put_row(3, [G(*pose_world({"right_hand": 22 * k, "right_elbow": 6 * k, "head": -3 * k})) for k in [-1, 0, 1, 0]])
    # 4 jump: AD retargeted `jumping`; take-off and peak keep ~60% of the clip's height, the rest stand on the ground.
    ad = load("jumping.yaml", "fair1_ppf.yaml")
    frames = [clip_world(ad, t) for t in [1.06, 1.23, 1.39, 1.72, 2.05]]
    ground = [render(w, j) for w, j in frames]
    roots = [(PAINT.to_px(j)[root_i][1]) for w, j in frames]
    jump = []
    for i, ((w, j), (cnv, ry)) in enumerate(zip(frames, ground)):
        lift = 0
        if i in (1, 2):
            want = max(0.0, (roots[0] - roots[i]) * SC * 0.6)
            a = cnv[..., 3]; top = np.nonzero(a.max(1) > 40)[0].min() - MT
            lift = min(want, max(0, top - 3))
        jump.append(G(w, j, lift=lift))
    put_row(4, jump)
    # 5 failed: head hangs, arms dangle, knees in, slow wobble.
    put_row(5, [G(*pose_world({"head": -16 + 5 * S(u), "neck": -8, "torso": 4 * S(u)}, {
        "right_elbow": aim(24), "right_hand": aim(12), "left_elbow": aim(-24), "left_hand": aim(-12),
        "right_knee": aim(6), "right_foot": aim(-6), "left_knee": aim(-6), "left_foot": aim(6)})) for u in [i / 8 * TAU for i in range(8)]])
    # 6 waiting: AD `jumping_jacks` — arms up and down, "over here!".
    ad = load("jumping_jacks.yaml", "cmu1_pfp.yaml")
    put_row(6, [G(*clip_world(ad, float(t))) for t in np.linspace(0.0, 1.0, 6)])
    # 7 work: hand on her head, thinking, head tilting.
    put_row(7, [G(*pose_world({"head": 8 + 5 * S(u), "torso": -2 * S(u)}, {
        "right_elbow": aim(148), "right_hand": aim(-118 + 8 * S(u)), "left_elbow": aim(-32), "left_hand": aim(-20)}))
        for u in [i / 6 * TAU for i in range(6)]])
    # 8 review: AD `dab` — pleased with herself.
    ad = load("dab.yaml", "fair1_ppf.yaml")
    put_row(8, [G(*clip_world(ad, float(t))) for t in np.linspace(8.27, 9.73, 6)])
    # 9–10 look: the head tilts toward where she looks (0° = up, clockwise).
    looks = [G(*pose_world({"head": -16 * math.sin(th) + 4 * math.cos(th), "neck": -6 * math.sin(th)}))
             for th in [math.radians(d * 22.5) for d in range(16)]]
    put_row(9, looks[:8]); put_row(10, looks[8:])
    # 11 perch: legs hang straight down over the edge and swing; the hip is the seat.
    SEAT = 130
    put_row(11, [G(*pose_world({"head": 3 * S(u)}, {"right_knee": aim(5 + 9 * S(u)), "right_foot": aim(5 + 12 * S(u)),
                                                   "left_knee": aim(-5 + 9 * S(u)), "left_foot": aim(-5 + 12 * S(u))}), mode="seat", seat=SEAT)
                 for u in [i / 6 * TAU for i in range(6)]], anchor=(96, SEAT))
    # 12 dance: AD retargeted `jesse_dance`.
    ad = load("jesse_dance.yaml", "mixamo_fff.yaml")
    put_row(12, [G(*clip_world(ad, float(t))) for t in np.linspace(0.2, 2.53, 8)])
    # 13 sleep: curled on her side (rotated so she lies down), breathing.
    put_row(13, [G(*pose_world({"torso": 1.5 * S(u), "head": 6}, {"right_knee": aim(60), "right_foot": aim(-20), "left_knee": aim(30),
                                                                 "left_foot": aim(-40), "right_elbow": aim(35), "right_hand": aim(20),
                                                                 "left_elbow": aim(-20), "left_hand": aim(-10)}), mode="lying", rotate=90)
                 for u in [i / 6 * TAU for i in range(6)]])
    # 14 dangle: held up by the head, everything hanging and kicking.
    GRAB = 48
    put_row(14, [G(*pose_world({"head": 4 * S(u)}, {
        "right_elbow": aim(16 + 14 * S(u)), "right_hand": aim(8 + 18 * S(u)), "left_elbow": aim(-16 + 14 * S(u)), "left_hand": aim(-8 + 18 * S(u)),
        "right_knee": aim(6 + 16 * S(u)), "right_foot": aim(4 + 22 * S(u)), "left_knee": aim(-6 + 16 * S(u)), "left_foot": aim(-4 + 22 * S(u))}),
        mode="grab", grab=GRAB) for u in [i / 4 * TAU for i in range(4)]], anchor=(96, GRAB))

    cv2.imwrite(str(HERE / "usagi.png"), cv2.cvtColor(sheet, cv2.COLOR_RGBA2BGRA))
    meta = {
        "id": "usagi", "name": "抽象乌萨奇", "source": "Animated Drawings (ARAP mesh + BVH retargeting) on source.png; see ad/",
        "sheet": "usagi.png", "cell": [CW, CH], "feetY": FEET, "seatY": SEAT, "grab": [96, GRAB], "look": True, "scale": 1.0,
        "rowFit": fits,
        "anims": {
            "idle": {"row": 0, "count": 6, "frameMs": 180}, "runRight": {"row": 1, "count": 8, "frameMs": 90},
            "runLeft": {"row": 2, "count": 8, "frameMs": 90}, "wave": {"row": 3, "count": 4, "frameMs": 130},
            "jump": {"row": 4, "count": 5, "frameMs": 110}, "failed": {"row": 5, "count": 8, "frameMs": 150},
            "waiting": {"row": 6, "count": 6, "frameMs": 140}, "work": {"row": 7, "count": 6, "frameMs": 200},
            "review": {"row": 8, "count": 6, "frameMs": 150}, "perch": {"row": 11, "count": 6, "frameMs": 200},
            "dance": {"row": 12, "count": 8, "frameMs": 140}, "sleep": {"row": 13, "count": 6, "frameMs": 340},
            "dangle": {"row": 14, "count": 4, "frameMs": 110},
        },
    }
    (HERE / "usagi.json").write_text(json.dumps(meta, ensure_ascii=False, indent=1))
    bg = np.full(sheet.shape[:2] + (3,), 70, np.float32); a = sheet[..., 3:4] / 255.0
    pv = (sheet[..., :3] * a + bg * (1 - a)).astype(np.uint8)
    cv2.imwrite(str(HERE / "preview.png"), cv2.cvtColor(pv, cv2.COLOR_RGB2BGR))
    print("sheet ok, cell scale", round(SC, 4), "row fits", fits)

if "--build" in sys.argv:
    build()
