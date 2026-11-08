"""Make Resources/GLORIA.icns from GLORIA's neutral frame (needs Pillow and macOS iconutil).

  python3 tools/make_icon.py
"""
import subprocess, sys, tempfile
from pathlib import Path
ROOT = Path(__file__).resolve().parent.parent
iconset = Path(tempfile.mkdtemp()) / "GLORIA.iconset"
iconset.mkdir()
sys.argv = ["", str(ROOT / "characters/gloria/base-v3.png"), str(iconset)]
import sys
from PIL import Image
sheet, out = sys.argv[1], sys.argv[2]
im = Image.open(sheet).convert("RGBA")
cell = im.crop((6 * 192, 0, 7 * 192, 208))
head = cell.crop((28, 0, 164, 136))
bg = Image.new("RGBA", (1024, 1024), (0, 0, 0, 0))
from PIL import ImageDraw
d = ImageDraw.Draw(bg)
d.rounded_rectangle((64, 64, 960, 960), radius=200, fill=(255, 236, 246, 255), outline=(246, 158, 204, 255), width=24)
h = head.resize((820, 820), Image.LANCZOS)
bg.alpha_composite(h, (102, 120))
for s in (16, 32, 128, 256, 512):
    bg.resize((s, s), Image.LANCZOS).save(f"{out}/icon_{s}x{s}.png")
    bg.resize((s * 2, s * 2), Image.LANCZOS).save(f"{out}/icon_{s}x{s}@2x.png")
subprocess.run(["iconutil", "-c", "icns", str(iconset), "-o", str(ROOT / "Resources/GLORIA.icns")], check=True)
print("Resources/GLORIA.icns")
