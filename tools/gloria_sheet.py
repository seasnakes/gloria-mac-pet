"""GLORIA's sheet = the approved 8×15 base (characters/gloria/base-v3.png) + smoothed loops appended below it.

  rows 15–17  thinking (Claude/Codex at work): the approved row-7 poses re-timed with holds, plus
              optical-flow in-betweens between head tilts (blinks still snap)
  rows 18–21  dance: the 8 approved dance poses, each followed by 3 in-betweens toward the next pose
  rows 22–25  computer use (tap / type / snap / drag): key poses drawn with image_gen in the approved pipeline
              (Codex桌宠-柔彩GLORIA/extend-computer-use-20261108 → characters/gloria/cu/*.png), with in-betweens

In-betweens move the nearer key frame along the flow field instead of cross-fading two frames, so there are no
ghosted double eyes or see-through arms; every pixel still comes from an approved frame.

Run with any Python that has OpenCV + Pillow + numpy:
  python3 tools/gloria_sheet.py
"""
import json, sys
from pathlib import Path
import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "tools"))
from inbetween import flows, warp_only

W, H = 192, 208
base = np.array(Image.open(ROOT / "characters/gloria/base-v3.png").convert("RGBA"))
cell = lambda r, c: base[r * H:(r + 1) * H, c * W:(c + 1) * W].copy()
_fl = {}
def ib(a, b, t):
    """In-between of two (row, col) cells at time t."""
    if (a, b) not in _fl: _fl[(a, b)] = flows(cell(*a), cell(*b))
    return warp_only(cell(*a), cell(*b), t, _fl[(a, b)])

# Thinking: (frame, ms). W0 neutral, W1 head tilt, W2 blink, W3 other tilt looking up, W4 glance, W5 settle.
Wk = lambda i: (7, i)
think = [
    (cell(*Wk(0)), 1100), (ib(Wk(0), Wk(1), .33), 70), (ib(Wk(0), Wk(1), .67), 70),
    (cell(*Wk(1)), 800), (cell(*Wk(2)), 130), (cell(*Wk(1)), 500),
    (ib(Wk(1), Wk(0), .33), 70), (ib(Wk(1), Wk(0), .67), 70),
    (cell(*Wk(0)), 600), (ib(Wk(0), Wk(3), .33), 70), (ib(Wk(0), Wk(3), .67), 70),
    (cell(*Wk(3)), 900), (ib(Wk(3), Wk(4), .33), 80), (ib(Wk(3), Wk(4), .67), 80),
    (cell(*Wk(4)), 900), (ib(Wk(4), Wk(5), .33), 70), (ib(Wk(4), Wk(5), .67), 70),
    (cell(*Wk(5)), 700), (ib(Wk(5), Wk(0), .5), 70),
]
# Dance: key i, then 3 in-betweens toward key i+1 (the loop wraps).
dance = []
for i in range(8):
    a, b = (12, i), (12, (i + 1) % 8)
    dance += [cell(*a), ib(a, b, .25), ib(a, b, .5), ib(a, b, .75)]

# Computer use: two key poses each (cu/<pose>-1/-2.png), in-betweens by the same flow warp.
CU = lambda n: np.array(Image.open(ROOT / f"characters/gloria/cu/{n}.png").convert("RGBA"))
def cib(a, b, t):
    k = ("cu", a, b)
    if k not in _fl: _fl[k] = flows(CU(a), CU(b))
    return warp_only(CU(a), CU(b), t, _fl[k])
cu = {
    # reach → press (finger pushed, happy) → back
    "tap": [(CU("tap-1"), 170), (cib("tap-1", "tap-2", .5), 70), (CU("tap-2"), 230), (cib("tap-2", "tap-1", .5), 70), (CU("tap-1"), 160)],
    # fingers down / lifted, alternating
    "type": [(CU("type-1"), 170), (cib("type-1", "type-2", .5), 60), (CU("type-2"), 170), (cib("type-2", "type-1", .5), 60)],
    # aim, click the shutter, back to aiming
    "snap": [(CU("snap-1"), 520), (CU("snap-2"), 360), (cib("snap-2", "snap-1", .5), 80), (CU("snap-1"), 300)],
    # pull, pull harder, ease off
    "drag": [(CU("drag-1"), 260), (cib("drag-1", "drag-2", .33), 80), (cib("drag-1", "drag-2", .67), 80), (CU("drag-2"), 420),
             (cib("drag-2", "drag-1", .5), 100)],
}
FACING = {"tap": "left", "snap": "right", "drag": "left"}

def rows_for(frames):
    n = (len(frames) + 7) // 8
    out = np.zeros((n * H, 8 * W, 4), np.uint8)
    for i, f in enumerate(frames):
        out[(i // 8) * H:(i // 8 + 1) * H, (i % 8) * W:(i % 8 + 1) * W] = f
    return out

think_rows = rows_for([f for f, _ in think])
dance_rows = rows_for(dance)
cu_rows = {k: rows_for([f for f, _ in v]) for k, v in cu.items()}
sheet = np.concatenate([base, think_rows, dance_rows] + list(cu_rows.values()), 0)
Image.fromarray(sheet).save(ROOT / "characters/gloria/gloria.png", optimize=True)

think_row = 15
dance_row = think_row + think_rows.shape[0] // H
meta = {
    "id": "gloria", "name": "GLORIA", "source": "approved 柔彩 Q sheet (base-v3.png) + smoothed loops from tools/gloria_sheet.py",
    "sheet": "gloria.png", "cell": [W, H], "feetY": 202, "seatY": 149, "grab": [96, 72], "scale": 1.0,
    "anims": {
        "idle": {"row": 0, "count": 6, "frameMs": 120},
        "runRight": {"row": 1, "count": 8, "frameMs": 75}, "runLeft": {"row": 2, "count": 8, "frameMs": 75},
        "wave": {"row": 3, "count": 4, "frameMs": 140}, "jump": {"row": 4, "count": 5, "frameMs": 100},
        "failed": {"row": 5, "count": 8, "frameMs": 130}, "waiting": {"row": 6, "count": 6, "frameMs": 200},
        "work": {"row": think_row, "count": len(think), "frameMs": [ms for _, ms in think], "loop": True},
        "review": {"row": 8, "count": 6, "frameMs": 170},
        "perch": {"row": 11, "count": 6, "frameMs": [220, 180, 90, 110, 90, 260]},
        "dance": {"row": dance_row, "count": len(dance), "frameMs": 130, "sub": 4},
        "sleep": {"row": 13, "count": 6, "frameMs": [260, 220, 260, 520, 260, 220]},
        "dangle": {"row": 14, "count": 4, "frameMs": 120},
    },
}
row = dance_row + dance_rows.shape[0] // H
for k, frames in cu.items():
    spec = {"row": row, "count": len(frames), "frameMs": [ms for _, ms in frames]}
    if k in ("type", "drag"): spec["loop"] = True
    if k in FACING: spec["facing"] = FACING[k]
    meta["anims"][k] = spec
    row += cu_rows[k].shape[0] // H
(ROOT / "characters/gloria/gloria.json").write_text(json.dumps(meta, ensure_ascii=False, indent=1))

# Contact sheet for review.
pv = Image.new("RGBA", (sheet.shape[1], sheet.shape[0] - 15 * H), (236, 236, 242, 255))
pv.alpha_composite(Image.fromarray(sheet[15 * H:]))
pv.convert("RGB").save(ROOT / "characters/gloria/preview-smooth.png")
print("rows", sheet.shape[0] // H, "think", len(think), "frames", sum(ms for _, ms in think), "ms; dance", len(dance))
