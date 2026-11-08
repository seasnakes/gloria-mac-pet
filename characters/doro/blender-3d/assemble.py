"""Downsize Doro's 4× renders (render_doro.py) into doro.png, write doro.json and preview.png.
  python3 assemble.py <render_out_dir>"""
import json, sys
from pathlib import Path
from PIL import Image
HERE = Path(__file__).resolve().parent
OUT = Path(sys.argv[1])
CW, CH, COLS, ROWS = 192, 208, 8, 15
sheet = Image.new("RGBA", (CW * COLS, CH * ROWS), (0, 0, 0, 0))
n = 0
for f in sorted((OUT / "frames").glob("r*_c*.png")):
    r, c = int(f.stem[1:3]), int(f.stem.split("_c")[1])
    im = Image.open(f).convert("RGBa").resize((CW, CH), Image.LANCZOS).convert("RGBA")
    sheet.alpha_composite(im, (c * CW, r * CH)); n += 1
sheet.save(HERE / "doro.png")
seat = json.loads((OUT / "render-meta.json").read_text())["seatY"]
# Grab point: top of her head in the dangle row, a little below the bun.
import numpy as np
a = np.asarray(sheet.crop((0, 14 * CH, CW, 15 * CH)))[..., 3]
top = int(np.nonzero(a.max(1) > 40)[0].min())
meta = {
    "id": "doro", "name": "Doro", "sheet": "doro.png", "cell": [CW, CH], "feetY": 202, "seatY": seat,
    "grab": [96, top + 14], "look": True, "scale": 1.15,
    "anims": {
        "idle": {"row": 0, "count": 6, "frameMs": 200}, "runRight": {"row": 1, "count": 8, "frameMs": 95},
        "runLeft": {"row": 2, "count": 8, "frameMs": 95}, "wave": {"row": 3, "count": 4, "frameMs": 140},
        "jump": {"row": 4, "count": 5, "frameMs": 100}, "failed": {"row": 5, "count": 8, "frameMs": 140},
        "waiting": {"row": 6, "count": 6, "frameMs": 170}, "work": {"row": 7, "count": 6, "frameMs": 200},
        "review": {"row": 8, "count": 6, "frameMs": 160}, "perch": {"row": 11, "count": 6, "frameMs": 190},
        "dance": {"row": 12, "count": 8, "frameMs": 130}, "sleep": {"row": 13, "count": 6, "frameMs": 340},
        "dangle": {"row": 14, "count": 4, "frameMs": 110},
    },
}
(HERE / "doro.json").write_text(json.dumps(meta, ensure_ascii=False, indent=1))
pv = Image.new("RGBA", sheet.size, (70, 70, 82, 255)); pv.alpha_composite(sheet)
pv.convert("RGB").save(HERE / "preview.png")
print("frames", n, "grab", meta["grab"], "seat", seat)
