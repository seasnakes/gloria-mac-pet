"""Images for the README, made from the shipped sprite sheets (and, for together.gif, frames captured from the
running app with `GLORIA --ctl '{"ctl":"scene","path":...,"scale":1}'`).

  python3 tools/readme_media.py [captured-frames-dir]

  docs/hero.gif          the three sprite pets dancing on a window, in sync at 116 BPM
  docs/cast.png          each pet's main poses
  docs/computer-use.png  each pet's computer-use poses (click, type, screenshot, drag, scroll)
  docs/icon.png          the app icon (from GLORIA.app)
  docs/together.gif      Claude Code's pet hands a job to Codex's pet and gets the answer back (needs captured frames)
  docs/computer-use.gif  Codex drives an app and Doro acts it out          (--cua <frames dir> x0,y0,x1,y1)
  docs/grokbot-3d.gif    the live 3D GrokBot following the pointer, acting (--grok3d <frames dir> x0,y0,x1,y1)
GrokBot is a real 3D model drawn live by the app, so its pictures are captured from the running app, not from a sheet.
"""
import json, math, sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
DOCS = ROOT / "docs"; DOCS.mkdir(exist_ok=True)
W, H = 192, 208
FONT = "/System/Library/Fonts/Hiragino Sans GB.ttc"
SYMBOLS = "/Library/Fonts/Arial Unicode.ttf"

BG = (248, 243, 252)
INK = (92, 70, 110)
PINK = (246, 158, 204)


class Pet:
    def __init__(self, cid, mul):
        d = ROOT / "characters" / cid
        self.sheet = Image.open(d / f"{cid}.png").convert("RGBA")
        self.meta = json.loads((d / f"{cid}.json").read_text())
        self.mul = mul

    def cell(self, anim, i):
        a = self.meta["anims"][anim]
        j = i % a["count"]
        r, c = a["row"] + j // 8, j % 8
        return self.sheet.crop((c * W, r * H, (c + 1) * W, (r + 1) * H))

    def look(self, deg):
        i = round(deg / 22.5) % 16
        return self.sheet.crop(((i % 8) * W, (9 + i // 8) * H, (i % 8 + 1) * W, (10 + i // 8) * H))

    def dance(self, beat):
        """Frame at a beat position, the way the app picks it."""
        a = self.meta["anims"]["dance"]
        sub = a.get("sub", 1)
        i, ph = int(beat), beat % 1
        if sub > 1:
            keys = a["count"] // sub
            s = 0 if ph < .55 else min(sub - 1, 1 + int((ph - .55) / .45 * (sub - 1)))
            return self.cell("dance", (i % keys) * sub + s)
        return self.cell("dance", int((i % 2 + ph) * 4) % 8)


PETS = {"gloria": Pet("gloria", 1.0), "doro": Pet("doro", 1.15), "usagi": Pet("usagi", 1.0)}
NAMES = [("gloria", "GLORIA · 柔彩"), ("doro", "Doro"), ("usagi", "抽象乌萨奇")]


def paste(canvas, cell, feet, k, lift=0, sy=1.0):
    w, h = int(W * k), int(H * k * sy)
    im = cell.resize((w, h), Image.LANCZOS)
    canvas.alpha_composite(im, (int(feet[0] - 96 * k), int(feet[1] - 202 * k * sy - lift)))


def window(d, box, title=""):
    x0, y0, x1, y1 = box
    d.rounded_rectangle(box, 14, fill=(255, 255, 255), outline=(226, 214, 238), width=2)
    d.rounded_rectangle((x0, y0, x1, y0 + 30), 14, fill=(242, 234, 250))
    d.rectangle((x0, y0 + 16, x1, y0 + 30), fill=(242, 234, 250))
    for i, c in enumerate([(255, 120, 120), (255, 196, 90), (110, 206, 120)]):
        d.ellipse((x0 + 16 + i * 20, y0 + 9, x0 + 28 + i * 20, y0 + 21), fill=c)
    if title:
        d.text(((x0 + x1) / 2, y0 + 15), title, fill=(150, 130, 165), font=ImageFont.truetype(FONT, 13), anchor="mm")


def gif(frames, path, ms):
    pal = frames[len(frames) // 3].convert("RGB").quantize(colors=255, method=Image.Quantize.MEDIANCUT)
    q = [f.convert("RGB").quantize(palette=pal, dither=Image.Dither.NONE) for f in frames]
    q[0].save(path, save_all=True, append_images=q[1:], duration=ms, loop=0, optimize=True, disposal=1)
    print(path.name, len(frames), "frames", path.stat().st_size // 1024, "KB")


def hero():
    Wd, Hd = 900, 290
    k = 0.78
    beat = 60 / 116
    steps = 64                                   # 8 beats at eighth-beat steps
    frames = []
    font = ImageFont.truetype(SYMBOLS, 24)
    for n in range(steps):
        b = n / 8
        img = Image.new("RGBA", (Wd, Hd), BG + (255,))
        d = ImageDraw.Draw(img)
        ledge = 236
        window(d, (70, ledge, Wd - 70, Hd + 20), "Music")
        # Notes drifting up on the beat.
        for j in range(5):
            u = ((b + j * 1.6) % 8) / 8
            x = 120 + j * 160 + 20 * math.sin(u * 6)
            y = ledge - 40 - u * 170
            a = int(255 * min(1, u * 5, (1 - u) * 3))
            d.text((x, y), "♪♫♬♩♪"[j], fill=(PINK if j % 2 else (190, 175, 240)) + (a,), font=font)
        ph = b % 1
        bounce = abs(math.sin(math.pi * ph)) * 4
        hit = max(0, 1 - ph * 5)
        for (cid, x) in (("doro", 250), ("gloria", 450), ("usagi", 650)):
            p = PETS[cid]
            paste(img, p.dance(b), (x, ledge + 2), k * p.mul, lift=bounce, sy=1 - .05 * hit)
        frames.append(img)
    gif(frames, DOCS / "hero.gif", round(beat / 8 * 1000))


def cast():
    cols = [("站着", lambda p: p.cell("idle", 0)), ("坐窗沿", lambda p: p.cell("perch", 0)), ("思考中", lambda p: p.cell("work", 3)),
            ("等你批准", lambda p: p.cell("waiting", 1)), ("跳舞", lambda p: p.dance(2)), ("打盹", lambda p: p.cell("sleep", 0)),
            ("被拎起来", lambda p: p.cell("dangle", 1)), ("看光标", lambda p: p.look(45))]
    k = 0.62
    cw, rh = 112, 150
    names = NAMES
    img = Image.new("RGBA", (130 + cw * len(cols), 44 + rh * len(names)), BG + (255,))
    d = ImageDraw.Draw(img)
    f, fb = ImageFont.truetype(FONT, 14), ImageFont.truetype(FONT, 15)
    for i, (t, _) in enumerate(cols):
        d.text((130 + cw * i + cw / 2, 22), t, fill=INK, font=f, anchor="mm")
    for r, (cid, name) in enumerate(names):
        y = 44 + rh * r
        if r: d.line((16, y, img.width - 16, y), fill=(232, 222, 242), width=1)
        d.text((65, y + rh / 2), name, fill=INK, font=fb, anchor="mm")
        p = PETS[cid]
        for i, (_, fn) in enumerate(cols):
            c = fn(p)
            kk = k * p.mul * (0.9 if cid == "doro" else 1)
            feet_y = y + rh - 10
            if cols[i][0] == "坐窗沿" and p.meta.get("seatY", 149) < 200:
                # The window edge she sits on runs through her seat line; her legs hang in front of the window.
                sy = feet_y - (202 - p.meta.get("seatY", 149)) * kk
                d.rectangle((130 + cw * i + 12, sy, 130 + cw * (i + 1) - 12, feet_y + 6), fill=(255, 255, 255), outline=(214, 200, 230))
            paste(img, c, (130 + cw * i + cw / 2, feet_y), kk)
    img.convert("RGB").save(DOCS / "cast.png", optimize=True)
    print("cast.png", img.size)


def computer_use():
    """Each pet's computer-use poses: one telling frame per action."""
    cols = [("点击", "tap", {"gloria": 2, "doro": 4, "usagi": 4}),
            ("打字", "type", {"gloria": 0, "doro": 0, "usagi": 0}),
            ("截图", "snap", {"gloria": 1, "doro": 4, "usagi": 4}),
            ("拖拽", "drag", {"gloria": 3, "doro": 4, "usagi": 4}),
            ("滚动", "scroll", {"doro": 2, "usagi": 2})]
    k, cw, rh = 0.62, 120, 150
    img = Image.new("RGBA", (130 + cw * len(cols), 44 + rh * len(NAMES)), BG + (255,))
    d = ImageDraw.Draw(img)
    f, fb = ImageFont.truetype(FONT, 14), ImageFont.truetype(FONT, 15)
    for i, (t, _, _) in enumerate(cols):
        d.text((130 + cw * i + cw / 2, 22), t, fill=INK, font=f, anchor="mm")
    for r, (cid, name) in enumerate(NAMES):
        y = 44 + rh * r
        if r: d.line((16, y, img.width - 16, y), fill=(232, 222, 242), width=1)
        d.text((65, y + rh / 2), name, fill=INK, font=fb, anchor="mm")
        p = PETS[cid]
        for i, (_, anim, pick) in enumerate(cols):
            a = anim if anim in p.meta["anims"] else "drag"
            c = p.cell(a, pick.get(cid, 2))
            paste(img, c, (130 + cw * i + cw / 2, y + rh - 10), k * p.mul * (0.9 if cid == "doro" else 1))
    img.convert("RGB").save(DOCS / "computer-use.png", optimize=True)
    print("computer-use.png", img.size)


def icon():
    import subprocess, tempfile
    icns = Path("/Applications/GLORIA.app/Contents/Resources/GLORIA.icns")
    if not icns.exists(): icns = ROOT / "GLORIA.app/Contents/Resources/GLORIA.icns"
    if not icns.exists(): return
    tmp = Path(tempfile.mkdtemp()) / "icon.png"
    subprocess.run(["sips", "-s", "format", "png", str(icns), "--out", str(tmp)], capture_output=True)
    Image.open(tmp).convert("RGBA").resize((256, 256), Image.LANCZOS).save(DOCS / "icon.png", optimize=True)
    print("icon.png")


def clip(cap, box, name, w):
    """A GIF from frames captured with `GLORIA --ctl '{"ctl":"scene",...}'`, cropped to `box` (screen px)."""
    files = sorted(Path(cap).glob("f*.png"))
    if not files: return
    frames = [Image.open(f).convert("RGBA").crop(box) for f in files]
    frames = [im.resize((w, im.height * w // im.width), Image.LANCZOS) for im in frames]
    gif(frames, DOCS / name, 100)


def together(cap):
    files = sorted(Path(cap).glob("f*.png"))
    if not files: return
    box = (600, 690, 1380, 1092)                 # around the two pets on the Dock, in full-res screen px
    frames = []
    for f in files:
        im = Image.open(f).convert("RGBA").crop(box)
        frames.append(im.resize((im.width * 3 // 4, im.height * 3 // 4), Image.LANCZOS))
    gif(frames, DOCS / "together.gif", 83)


if __name__ == "__main__":
    hero()
    cast()
    computer_use()
    icon()
    if len(sys.argv) > 1 and sys.argv[1] != "--cua": together(sys.argv[1])
    for flag, name, w in (("--cua", "computer-use.gif", 760), ("--grok3d", "grokbot-3d.gif", 340)):
        if flag in sys.argv:
            i = sys.argv.index(flag)
            clip(sys.argv[i + 1], tuple(int(v) for v in sys.argv[i + 2].split(",")), name, w)
