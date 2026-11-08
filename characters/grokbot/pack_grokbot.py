"""Pack GrokBot's rendered frames (frames/*.png, 2×) into the shared sheet layout: 8 columns of 192×208, ground on y=202.

  python3 pack_grokbot.py            → grokbot.png, grokbot.json, preview.png
  python3 pack_grokbot.py --keys     → frames/keys.png (one frame per animation, for checking poses)
"""
import json, sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).resolve().parent
FR = HERE / "frames"
CW, CH, FEET = 192, 208, 202

# Rows 9–10 are always the 16 look directions; long loops sit at the end and run on across rows.
LAYOUT = [  # (anim, row, count, frameMs, extra)
    ("tap", 0, 8, [130, 110, 110, 90, 140, 120, 120, 140], {"facing": "left"}),
    ("runRight", 1, 8, 80, {}),
    ("runLeft", 2, 8, 80, {}),
    ("wave", 3, 6, 120, {}),
    ("jump", 4, 5, 100, {}),
    ("failed", 5, 8, 150, {}),
    ("waiting", 6, 6, 150, {}),
    ("review", 7, 6, 170, {}),
    ("type", 8, 8, 95, {"loop": True}),
    ("look", 9, 16, 0, {}),
    ("perch", 11, 6, [1400, 180, 110, 160, 110, 180], {}),
    ("dance", 12, 8, 130, {}),
    ("sleep", 13, 6, 380, {}),
    ("dangle", 14, 4, 110, {}),
    ("idle", 15, 24, [140] * 15 + [70, 70, 70] + [140] * 6, {"loop": True}),
    ("work", 18, 12, 170, {"loop": True}),
    ("snap", 20, 8, [160, 140, 220, 260, 70, 90, 180, 260], {}),
    ("drag", 21, 8, [140, 120, 110, 110, 110, 110, 120, 200], {}),
    ("scroll", 22, 8, 100, {"loop": True, "facing": "right"}),
]


def cell(anim, i, gx, gy):
    im = Image.open(FR / f"{anim}_{i:02d}.png").convert("RGBa")
    small = im.resize((CW, CH), Image.LANCZOS).convert("RGBA")
    # Put the ground point on (96, 202).
    dx, dy = round(96 - gx / 2), round(FEET - gy / 2)
    out = Image.new("RGBA", (CW, CH))
    out.alpha_composite(small, (dx, dy))
    return out


def main():
    anc = json.loads((FR / "anchor.json").read_text())
    gx, gy = anc["ground"]
    if "--keys" in sys.argv:
        shots = sorted(FR.glob("*_*.png"))
        sheet = Image.new("RGBA", (CW * 8, CH * ((len(shots) + 7) // 8)), (54, 56, 66, 255))
        d = ImageDraw.Draw(sheet)
        for n, f in enumerate(shots):
            a, i = f.stem.rsplit("_", 1)
            c = cell(a, int(i), gx, gy)
            x, y = (n % 8) * CW, (n // 8) * CH
            d.line((x, y + FEET, x + CW, y + FEET), fill=(120, 120, 140))
            sheet.alpha_composite(c, (x, y))
            d.text((x + 4, y + 2), f.stem, fill=(230, 230, 240))
        sheet.save(FR / "keys.png")
        print("keys", sheet.size)
        return
    rows = max(r + (n + 7) // 8 for _, r, n, _, _ in LAYOUT)
    sheet = Image.new("RGBA", (CW * 8, CH * rows))
    anims = {}
    for a, row, n, ms, extra in LAYOUT:
        for i in range(n):
            sheet.alpha_composite(cell(a, i, gx, gy), ((i % 8) * CW, (row + i // 8) * CH))
        if a != "look":
            anims[a] = {"row": row, "count": n, "frameMs": ms, **extra}
    sheet.save(HERE / "grokbot.png", optimize=True)
    meta = {"id": "grokbot", "name": "GrokBot", "source": "Blender: grokbot_transform_v6.blend (frame 650 pose) via render_grokbot.py",
            "sheet": "grokbot.png", "cell": [CW, CH], "feetY": FEET, "seatY": FEET, "grab": [96, 96], "look": True, "scale": 1.2,
            "anims": anims}
    (HERE / "grokbot.json").write_text(json.dumps(meta, indent=1, ensure_ascii=False))
    # Preview: first frame of every row on a dark strip.
    prev = Image.new("RGBA", (CW * 8, CH * rows), (54, 56, 66, 255))
    prev.alpha_composite(sheet)
    prev.convert("RGB").resize((CW * 4, CH * rows // 2), Image.LANCZOS).save(HERE / "preview.png")
    print("sheet", sheet.size, "rows", rows)


if __name__ == "__main__":
    main()
