"""
Doro from DoroPet-macos (Live2D head + StandingDoro's drawn body) → desk-pet sprite sheet in GLORIA's layout:
8 columns × 15 rows of 192×208 cells, feet on y=202, body centre on x=96.

Run with DoroPet's own environment (live2d-py, PyQt5):
  /Users/seasnake/Developer/DoroPet-macos-env/bin/python render_doropet.py

Each frame is drawn offscreen by DoroPet's real pieces — the Doro.model3.json head and StandingDoro.draw_body —
with a fixed time, head angle and expression, so the sheet is reproducible. Expressions are applied as the
parameter values from their exp3.json files (no fade-in timing).
"""
import json, math, os, sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
DORO = Path("/Users/seasnake/Developer/DoroPet-macos/opendoro")
MODEL = str(DORO / "models/Doro/Doro.model3.json")
sys.path.insert(0, str(DORO)); os.chdir(DORO)

from PyQt5.QtCore import Qt
from PyQt5.QtGui import QSurfaceFormat, QPainter
from PyQt5.QtWidgets import QApplication, QOpenGLWidget
import live2d.v3 as live2d
from src.core.standing_doro import StandingDoro

fmt = QSurfaceFormat(); fmt.setAlphaBufferSize(8); QSurfaceFormat.setDefaultFormat(fmt)
app = QApplication(sys.argv)

CW, CH, COLS, ROWS = 192, 208, 8, 15
FEET_CANVAS = (295, 485)            # StandingDoro's body centre / feet line in its 600-unit canvas
TARGET_H = 172                      # standing height in the cell, px

NEUTRAL = {**{f"ParamExp{i}": 0 for i in range(1, 8)}, "ParamExpEyeStar": 0, "ParamExpEyeHighlights": 0, "ParamTongueOut": 0,
           "ParamEyeLOpen": 1, "ParamEyeROpen": 1, "ParamEyeSmile": 0, "ParamMouthOpenY": 0, "ParamAngleX": 0,
           "ParamAngleY": 0, "ParamAngleZ": 0, "ParamBodyAngleY": 0, "ParamBodyAngleZ": 0}
EXPR = {"黑脸": {"ParamExp1": 1}, "感叹号": {"ParamExp3": 1}, "问号": {"ParamExp4": 1}, "墨镜": {"ParamExp5": 1},
        "思考": {"ParamExp7": 1}, "星星眼": {"ParamExpEyeStar": 1}, "吐舌": {"ParamTongueOut": 1}}


class Stage(QOpenGLWidget):
    def __init__(self):
        super().__init__()
        self.spec = {}
        self.setAttribute(Qt.WA_TranslucentBackground)
        self.setAttribute(Qt.WA_DontShowOnScreen)
        self.resize(600, 600)

    def initializeGL(self):
        live2d.init(); live2d.glInit()
        self.model = live2d.LAppModel(); self.model.LoadModelJson(MODEL)
        self.model.Resize(self.width(), self.height())
        self.model.SetAutoBlinkEnable(False); self.model.SetAutoBreathEnable(False)
        self.sd = StandingDoro()

    def resizeGL(self, w, h):
        self.model.Resize(w, h)

    def paintGL(self):
        s, m, sd = self.spec, self.model, self.sd
        m.Update()
        # Live2D keeps parameter values between frames: start every frame from the neutral face.
        for k, v in NEUTRAL.items(): m.SetParameterValue(k, v, 1)
        for k, v in s.get("params", {}).items(): m.SetParameterValue(k, v, 1)
        # Body pose at a fixed time (StandingDoro.pose takes `now`).
        sd.action = s.get("action", "idle"); sd.started = 0.0; sd.action_started = 0.0
        sd.walking = s.get("walking", False)
        t = s.get("t", 0.0)
        pose = sd.pose(now=t)
        joints = None
        if s.get("clip"):
            joints = sd.motion_bank.sample(s["clip"], s.get("clip_t", 0.0))
        for k, v in s.get("pose", {}).items(): pose[k] = v
        live2d.clearBuffer(0, 0, 0, 0)
        if joints is not None:
            pose["y"] = math.sin(t * 2.5) * 2 + s.get("pose", {}).get("y", 0)
            pose["joints"] = joints
        sd.draw_body(self, pose)
        p = QPainter(self); p.beginNativePainting()
        m.SetScale(.80)
        neck_dx = joints["neck"][0] - 295 if joints else pose.get("music_sway", 0) + pose.get("music_lean", 0)
        neck_dy = joints["neck"][1] - 300 if joints else pose.get("music_neck_y", 0)
        m.SetOffset(.10 + neck_dx / 300, .18 - (pose["y"] + neck_dy) / 300)
        m.Rotate(8 + s.get("head_tilt", 0))
        for i, part in enumerate(m.GetPartIds()):
            if part == "Body_group": m.SetPartOpacity(i, 0)
        m.Draw()
        p.endNativePainting(); p.end()

    def shot(self, **spec):
        self.spec = spec
        for _ in range(3):              # settle physics with the same parameters
            img = self.grabFramebuffer()
        return img


stage = Stage(); stage.show()
from PIL import Image
import numpy as np

def to_pil(q):
    q = q.convertToFormat(q.Format_RGBA8888)
    ptr = q.bits(); ptr.setsize(q.byteCount())
    return Image.frombuffer("RGBA", (q.width(), q.height()), bytes(ptr), "raw", "RGBA", 0, 1)

def bbox(im):
    a = np.asarray(im)[..., 3]
    ys, xs = np.nonzero(a > 30)
    return xs.min(), ys.min(), xs.max() + 1, ys.max() + 1

# Scale from the plain standing frame: feet line → y=202, height → TARGET_H.
ref = to_pil(stage.shot(action="idle", t=0))
FB = ref.width                                   # framebuffer px (1200 on Retina)
U = FB / 600                                     # framebuffer px per canvas unit
x0, y0, x1, y1 = bbox(ref)
SCALE = TARGET_H / ((FEET_CANVAS[1] * U) - y0)   # cell px per framebuffer px
def place(im):
    """Framebuffer image → one 192×208 cell (canvas feet → (96, 202))."""
    small = im.resize((round(im.width * SCALE), round(im.height * SCALE)), Image.LANCZOS)
    fx, fy = FEET_CANVAS[0] * U * SCALE, FEET_CANVAS[1] * U * SCALE
    cell = Image.new("RGBA", (CW, CH), (0, 0, 0, 0))
    cell.alpha_composite(small, (round(96 - fx), round(202 - fy)))
    return cell

sheet = Image.new("RGBA", (CW * COLS, CH * ROWS), (0, 0, 0, 0))
def put(row, col, **spec):
    sheet.alpha_composite(place(to_pil(stage.shot(**spec))), (col * CW, row * CH))

def P(**kw): return kw
S, TAU = math.sin, math.tau

# 0 idle: breathing, a blink on frame 3, tiny head sway.
for i in range(6):
    t = i / 6 * (TAU / 2.5)
    eye = 0.08 if i == 3 else 1
    put(0, i, action="idle", t=t, head_tilt=2 * S(i / 6 * TAU), params=P(ParamEyeLOpen=eye, ParamEyeROpen=eye))
# 1/2 walking right / left: StandingDoro's gait, head turned the way she walks.
for row, ax in ((1, 18), (2, -18)):
    for i in range(8):
        put(row, i, action="idle", walking=True, t=i / 8 * .72, params=P(ParamAngleX=ax, ParamBodyAngleY=ax / 2))
# 3 wave: StandingDoro's own wave, smiling.
for i in range(4):
    put(3, i, action="wave", t=0.4 + i * (TAU / 12) / 4, params=P(ParamEyeSmile=1, ParamMouthOpenY=0.6))
# 4 jump: crouch → take-off → air → landing → settle.
# Crouches use StandingDoro's grounded squash (feet stay on the line) and lower the head with it.
CROUCH = lambda k: P(body_squash=1 - k, music_neck_y=k * 150)
JUMP = [("idle", 0, CROUCH(.12)), ("jump", .12, {}), ("jump", .45, {}), ("jump", .8, {}), ("idle", 0, CROUCH(.05))]
for i, (act, t, extra) in enumerate(JUMP):
    put(4, i, action=act, t=t, pose=extra, params=P(ParamEyeSmile=1 if i == 2 else 0, ParamMouthOpenY=0.8 if i == 2 else 0))
# 5 failed: 黑脸, head down, wobbling, slumped a little.
for i in range(8):
    u = i / 8 * TAU
    put(5, i, action="idle", t=.6, pose=CROUCH(.05), head_tilt=5 * S(u), params={**EXPR["黑脸"], "ParamAngleY": -14, "ParamAngleZ": 6 * S(u)})
# 6 waiting: 感叹号, looking up, small bounce.
for i in range(6):
    u = i / 6 * TAU
    put(6, i, action="idle", t=i * .2, pose=P(y=-6 * max(0, S(u))), params={**EXPR["感叹号"], "ParamAngleY": 14, "ParamAngleZ": 4 * S(u)})
# 7 work: 思考, glancing left and right.
for i in range(6):
    u = i / 6 * TAU
    put(7, i, action="idle", t=i * .3, params={**EXPR["思考"], "ParamAngleX": 16 * S(u), "ParamAngleY": -4})
# 8 review: 星星眼, pleased bounce.
for i in range(6):
    u = i / 6 * TAU
    put(8, i, action="idle", t=i * .2, pose=P(y=-5 * max(0, S(u))), head_tilt=4 * S(u),
        params={**EXPR["星星眼"], "ParamEyeSmile": 0.5, "ParamMouthOpenY": 0.4})
# 9–10 look: 16 directions with the head (0° = up, clockwise).
for d in range(16):
    th = math.radians(d * 22.5)
    put(9 + d // 8, d % 8, action="idle", t=0, params=P(ParamAngleX=30 * math.sin(th), ParamAngleY=30 * math.cos(th)))
# 11 perch: sitting on an edge — feet swing below the seat (the belly line).
for i in range(6):
    u = i / 6 * TAU
    put(11, i, action="idle", t=i * .25, pose=P(left_lift=6 + 6 * max(0, S(u)), right_lift=6 + 6 * max(0, -S(u)), step=0.35 * S(u)),
        params=P(ParamAngleZ=3 * S(u)))
# 12 dance: the motion bank's dance clip with sunglasses (墨镜).
clip = json.loads((DORO / "data/standing_doro/motions.json").read_text())
n = len(clip["motions"]["dance"]["frames"]); fps = clip["fps"]
for i in range(8):
    put(12, i, action="idle", clip="dance", clip_t=(i / 8) * min(n / fps, 1.0), t=i * .13, params={**EXPR["墨镜"], "ParamMouthOpenY": 0.5})
# 13 sleep: eyes shut, head drooping, crouched, slow breathing.
for i in range(6):
    u = i / 6 * TAU
    put(13, i, action="idle", t=0, pose=CROUCH(.15 + .01 * S(u)), head_tilt=10,
        params=P(ParamEyeLOpen=0, ParamEyeROpen=0, ParamAngleY=-12, ParamAngleZ=10, ParamMouthOpenY=0.1 + 0.1 * S(u)))
# 14 dangle: held up by the head, arms and feet flailing, surprised.
for i in range(4):
    u = i / 4 * TAU
    put(14, i, action="idle", t=i * .2, pose=P(arm_left=-30 + 25 * S(u), arm_right=-30 - 25 * S(u), left_lift=14 * max(0, S(u)),
        right_lift=14 * max(0, -S(u)), step=0.6 * S(u)), params={**EXPR["感叹号"], "ParamMouthOpenY": 1})

sheet.save(HERE / "doro.png")

# Anchors in cell px: the seat is the belly line (feet lifted ~30 units above the feet line); the grab point is the
# top of her head in the dangle row.
seat = round(202 - 31 * U * SCALE)
a = np.asarray(sheet.crop((0, 14 * CH, CW, 15 * CH)))[..., 3]
top = int(np.nonzero(a.max(1) > 40)[0].min())
meta = {
    "id": "doro", "name": "Doro", "source": "DoroPet-macos: Live2D head (models/Doro/Doro.model3.json) + StandingDoro body",
    "sheet": "doro.png", "cell": [CW, CH], "feetY": 202, "seatY": seat, "grab": [96, top + 16], "look": True, "scale": 1.0,
    "anims": {
        "idle": {"row": 0, "count": 6, "frameMs": 150},
        "runRight": {"row": 1, "count": 8, "frameMs": 90}, "runLeft": {"row": 2, "count": 8, "frameMs": 90},
        "wave": {"row": 3, "count": 4, "frameMs": 130}, "jump": {"row": 4, "count": 5, "frameMs": 110},
        "failed": {"row": 5, "count": 8, "frameMs": 140}, "waiting": {"row": 6, "count": 6, "frameMs": 170},
        "work": {"row": 7, "count": 6, "frameMs": 220}, "review": {"row": 8, "count": 6, "frameMs": 160},
        "perch": {"row": 11, "count": 6, "frameMs": 200}, "dance": {"row": 12, "count": 8, "frameMs": 130},
        "sleep": {"row": 13, "count": 6, "frameMs": 360}, "dangle": {"row": 14, "count": 4, "frameMs": 110},
    },
}
(HERE / "doro.json").write_text(json.dumps(meta, ensure_ascii=False, indent=1))
pv = Image.new("RGBA", sheet.size, (70, 70, 82, 255)); pv.alpha_composite(sheet)
pv.convert("RGB").save(HERE / "preview.png")
print("ok framebuffer", FB, "scale", round(SCALE, 4), "seat", seat, "grab", meta["grab"])
app.quit()
