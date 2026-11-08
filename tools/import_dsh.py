"""Bring a dsh-pet pet (https://github.com/PC2005-cloud/dsh-pet) into GLORIA's sheet layout.

dsh-pet plays whole 10-second transparent clips (VP9-alpha .webm, 640×360, the pet standing in the same spot in every
clip). GLORIA uses short loops in a sprite sheet, so this picks a segment of the right clip for each of GLORIA's states,
samples it, and places the pet on the shared grid (8 columns of 192×208, feet on y=202).

  python3 tools/import_dsh.py <dsh-pet assets dir> [--id dsh] [--name 蓝毛小女仆] [--out characters/dsh] [--install]

  <assets dir>  a folder with webm/ (dsh-pet/assets, $DSH_HOME/dsh-pet/main-animation, or pet/<kind>-animation)
                plus, optionally, config.jsonc next to it (or pass --config) so animation names come from the pet's
                own pools (idle / turn / drag / clicks / events.workStatus / sleep / moves).
  --install     also copy the result to ~/Library/Application Support/GLORIA/characters/<id>/, where GLORIA picks up
                imported pets at launch (menu 角色).

Needs ffmpeg with libvpx (for the alpha channel) and Pillow.
"""
import argparse, json, re, shutil, subprocess, sys, tempfile
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
CW, CH, FEET = 192, 208, 202
BODY_H = 186                      # pet height in the cell, feet to the top of the head (accessories may go higher)

# GLORIA state → (clip name candidates, start s, end s, sample fps, frameMs, loop).
# Candidates are tried in order; "@pool" entries are looked up in the pet's config.jsonc.
PLAN = {
    "idle":     (["@idle", "待机呼吸休闲"], 0.0, 10.0, 3.2, 312, True),
    "look":     (["@turn", "东张西望"], 0.0, 10.0, 4, 0, False),
    "runRight": (["@move", "螃蟹走路"], 1.2, 3.2, 8, 90, True),
    "wave":     (["@click:挥手", "点击回应-元气挥手"], 2.0, 5.0, 4, 130, False),
    "jump":     (["@click:跃", "点击回应-开心跃动"], 3.2, 5.8, 0, 110, False),
    "failed":   (["@work:5", "工作状态-垂头叹气冒汗"], 0.8, 4.6, 2, 180, False),
    "waiting":  (["@work:3", "工作状态-原地踱步张望"], 3.5, 9.0, 2.2, 220, True),
    "work":     (["@work:0", "工作状态-思考冒泡"], 1.0, 9.0, 2, 300, True),
    "review":   (["@work:4", "工作状态-雀跃庆祝"], 2.4, 5.6, 3, 140, False),
    "dance":    (["可爱宅舞", "轻快摇摆舞", "优雅女仆舞"], 2.0, 4.0, 4, 130, False),
    "sleep":    (["@sleep", "睡觉-循环"], 0.0, 10.0, 1, 520, True),
    "dangle":   (["@drag", "被鼠标拖拽悬空反馈"], 1.0, 5.0, 2, 120, False),
    # computer use
    "tap":      (["@work:1", "工作状态-忙碌点按"], 3.0, 7.0, 3, 140, True),
    "type":     (["写代码", "@work:1"], 2.0, 8.0, 2, 170, True),
    "snap":     (["照镜子", "碎碎念-对屏碎碎念"], 0.5, 3.5, 3, 160, False),
    "drag":     (["原地蹲下玩玩具汽车", "原地专心玩魔方"], 2.5, 7.5, 2.4, 160, True),
    "scroll":   (["原地专心玩魔方", "原地蹲下玩玩具汽车"], 2.0, 6.0, 3, 140, True),
}
JUMP_AT = [3.2, 4.0, 4.6, 5.2, 5.8]   # stand, crouch, up, land, stand — the frames the hop logic expects


def read_config(path: Path | None) -> dict:
    if not path or not path.exists(): return {}
    text = path.read_text(encoding="utf-8")
    text = re.sub(r"(?m)^\s*//.*$", "", text)                # whole-line comments
    text = re.sub(r'("(?:[^"\\]|\\.)*")|//[^\n]*', lambda m: m.group(1) or "", text)   # trailing comments, not inside strings
    text = re.sub(r",(\s*[}\]])", r"\1", text)               # trailing commas
    try: return json.loads(text)
    except json.JSONDecodeError as e:
        print("config.jsonc not readable, using built-in clip names:", e, file=sys.stderr)
        return {}


def resolve(cands, cfg, webm: Path) -> Path | None:
    a = cfg.get("animations", {})
    for c in cands:
        names = []
        if c.startswith("@"):
            k = c[1:]
            if k in ("idle", "turn", "drag"): names = a.get(k, [])[:1]
            elif k == "move": names = [m["name"] for m in a.get("moves", {}).get("actions", [])[:1]]
            elif k == "sleep": names = (a.get("sleep") or [{}])[0].get("loop", [])[:1]
            elif k.startswith("click:"): names = [x for x in a.get("clicks", []) if k[6:] in x][:1]
            elif k.startswith("work:"):
                ws = a.get("events", {}).get("workStatus", [])
                i = int(k[5:])
                if i < len(ws): names = [ws[i]] if isinstance(ws[i], str) else ws[i][:1]
        else:
            names = [c]
        for n in names:
            p = webm / f"{n}.webm"
            if p.exists(): return p
    return None


def frames(clip: Path, t0: float, t1: float, fps: float, tmp: Path, times=None) -> list[Image.Image]:
    out = tmp / clip.stem
    shutil.rmtree(out, ignore_errors=True); out.mkdir(parents=True)
    if times:
        ims = []
        for k, t in enumerate(times):
            p = out / f"t{k}.png"
            subprocess.run(["ffmpeg", "-v", "error", "-c:v", "libvpx-vp9", "-ss", f"{t:.3f}", "-i", str(clip), "-frames:v", "1", str(p)], check=True)
            ims.append(Image.open(p).convert("RGBA"))
        return ims
    subprocess.run(["ffmpeg", "-v", "error", "-c:v", "libvpx-vp9", "-ss", f"{t0:.3f}", "-t", f"{t1 - t0:.3f}", "-i", str(clip),
                    "-vf", f"fps={fps}", str(out / "f%04d.png")], check=True)
    return [Image.open(p).convert("RGBA") for p in sorted(out.glob("f*.png"))]


def placement(ref: Image.Image):
    """Scale and offset that put the standing pet's feet on y=202 and her centre on x=96."""
    a = ref.getchannel("A").point(lambda v: 255 if v > 24 else 0)
    x0, y0, x1, y1 = a.getbbox()
    s = BODY_H / (y1 - y0)
    # Horizontal centre from the lower body (hair and tail swing; feet don't).
    lower = a.crop((x0, y0 + (y1 - y0) * 2 // 3, x1, y1))
    lx0, _, lx1, _ = lower.getbbox()
    cx = x0 + (lx0 + lx1) / 2
    return s, cx, y1


def clean(im: Image.Image) -> Image.Image:
    """VP9 alpha leaves faint noise (alpha 1–9) over the whole frame; clear it so the background is truly empty."""
    a = im.getchannel("A").point(lambda v: 0 if v < 10 else v)
    out = im.copy(); out.putalpha(a)
    return out


def to_cell(im: Image.Image, s, cx, feet, mirror=False) -> Image.Image:
    im = clean(im)
    w, h = im.size
    big = im.convert("RGBa").resize((round(w * s), round(h * s)), Image.LANCZOS).convert("RGBA")
    if mirror: big = big.transpose(Image.FLIP_LEFT_RIGHT); cx = w - cx
    cell = Image.new("RGBA", (CW, CH))
    cell.alpha_composite(big, (round(96 - cx * s), round(FEET - feet * s)))
    # Premultiplied-black fringe from the decoder: drop colour where nothing is drawn.
    a = cell.getchannel("A")
    return Image.merge("RGBA", (*[Image.composite(ch, Image.new("L", cell.size, 0), a.point(lambda v: 255 if v else 0)) for ch in cell.split()[:3]], a))


def pick_look(ims: list[Image.Image], s, cx, feet):
    """From a look-around clip, the frames where the head is furthest left / right of the body."""
    def head_shift(im):
        a = im.getchannel("A").point(lambda v: 255 if v > 24 else 0)
        x0, y0, x1, y1 = a.getbbox()
        head = a.crop((x0, y0, x1, y0 + (y1 - y0) // 3)).getbbox()
        return (x0 + (head[0] + head[2]) / 2) - cx if head else 0
    shifts = [head_shift(im) for im in ims]
    # The clearer of the two glances, and its mirror image for the other side.
    i = max(range(len(ims)), key=lambda i: abs(shifts[i]))
    g = ims[i]
    m = g.transpose(Image.FLIP_LEFT_RIGHT)
    return ims[0], (g, False) if shifts[i] < 0 else (m, True), (m, True) if shifts[i] < 0 else (g, False)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("assets")
    ap.add_argument("--config")
    ap.add_argument("--id", default="dsh")
    ap.add_argument("--name")
    ap.add_argument("--out")
    ap.add_argument("--install", action="store_true")
    args = ap.parse_args()
    assets = Path(args.assets).expanduser()
    webm = assets / "webm" if (assets / "webm").is_dir() else assets
    cfg_path = Path(args.config) if args.config else next((p for p in [assets / "config.jsonc", assets.parent / "config.jsonc"] if p.exists()), None)
    cfg = read_config(cfg_path)
    pet = (cfg.get("pets") or [{}])[0]
    name = args.name or pet.get("name") or args.id
    out = Path(args.out) if args.out else ROOT / "characters" / args.id
    out.mkdir(parents=True, exist_ok=True)

    tmp = Path(tempfile.mkdtemp(prefix="dsh-"))
    idle_clip = resolve(PLAN["idle"][0], cfg, webm)
    if not idle_clip: sys.exit(f"no idle clip in {webm}")
    s, cx, feet = placement(frames(idle_clip, 0, 0.1, 10, tmp)[0])

    rows: list[tuple[str, list[Image.Image]]] = []
    used = {}
    for state, (cands, t0, t1, fps, ms, loop) in PLAN.items():
        clip = resolve(cands, cfg, webm)
        if not clip:
            print(f"  {state:9s} — no clip, GLORIA falls back to another pose")
            continue
        dur = float(subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", str(clip)],
                                   capture_output=True, text=True).stdout.strip() or 10)
        t1c = min(t1, dur - 0.05)
        if state == "jump":
            ims = frames(clip, 0, 0, 0, tmp, times=[min(t, dur - 0.05) for t in JUMP_AT])
        else:
            ims = frames(clip, min(t0, max(0, t1c - 1)), t1c, fps, tmp)
        used[state] = clip.stem
        if state == "look":
            f, l, r = pick_look(ims, s, cx, feet)
            # 16 directions, 0° = up, clockwise: right half → right glance, left half → left glance, up/down → front.
            look = []
            for i in range(16):
                d = i * 22.5
                if 22.5 < d < 157.5: im, flip = r
                elif 202.5 < d < 337.5: im, flip = l
                else: im, flip = f, False
                look.append(to_cell(im, s, (im.width - cx) if flip else cx, feet))
            rows.append(("look", look))
            continue
        cells = [to_cell(im, s, cx, feet) for im in ims]
        rows.append((state, cells))
        if state == "runRight":
            rows.append(("runLeft", [to_cell(im, s, cx, feet, mirror=True) for im in ims]))
        print(f"  {state:9s} ← {clip.stem}  {len(cells)} frames")

    # Lay out: look must sit on rows 9–10; everything else fills rows in order around it.
    meta_anims = {}
    grid: dict[int, list[Image.Image]] = {}
    row = 0
    def take(n):
        nonlocal row
        need = (n + 7) // 8
        while any(r in (9, 10) for r in range(row, row + need)): row += 1
        r0 = row; row += need
        return r0
    for state, cells in rows:
        if state == "look":
            for i, c in enumerate(cells): grid.setdefault(9 + i // 8, [None] * 8)[i % 8] = c
            continue
        r0 = take(len(cells))
        for i, c in enumerate(cells): grid.setdefault(r0 + i // 8, [None] * 8)[i % 8] = c
        cands, t0, t1, fps, ms, loop = PLAN.get(state, PLAN["runRight"])
        spec = {"row": r0, "count": len(cells), "frameMs": ms if ms else 120}
        if loop: spec["loop"] = True
        meta_anims[state] = spec
    # Standing everywhere: she has no seated pose, so the perch row is the idle loop and the seat line is her feet.
    meta_anims["perch"] = dict(meta_anims["idle"])
    nrows = max(grid) + 1
    sheet = Image.new("RGBA", (CW * 8, CH * nrows))
    for r, cs in grid.items():
        for c, im in enumerate(cs):
            if im is not None: sheet.alpha_composite(im, (c * CW, r * CH))
    sheet.save(out / f"{args.id}.png", optimize=True)

    lines = {
        "greeting": "主人好～我是蓝毛小女仆，从 DSH 搬过来陪你啦！",
        "pats": ["嘿嘿，主人～", "Ciallo～", "要摸摸头吗？", "今天也要好好吃饭哦。", "鲸鲸在这里！"],
        "wake": cfg.get("sleepEndTexts") or ["唔……我醒了。", "睡饱啦，主人！"],
        "ouch": ["呜……摔疼了。", "主人轻一点嘛！", "眼冒金星……"],
        "angry": ["哼，不理你了！", "再戳就咬你！", "主人好坏！"],
        "hello": ["你好呀～", "一起加油！", "Ciallo～"],
    }
    meta = {"id": args.id, "name": name,
            "source": f"dsh-pet ({assets}) via tools/import_dsh.py; clips: " + ", ".join(f"{k}={v}" for k, v in used.items()),
            "sheet": f"{args.id}.png", "cell": [CW, CH], "feetY": FEET, "seatY": FEET, "grab": [96, 30], "look": True, "scale": 1.0,
            "persona": {"title": f"{name} · DSH", "tag": "DSH", "scale": 1.0, **lines},
            "anims": meta_anims}
    (out / f"{args.id}.json").write_text(json.dumps(meta, indent=1, ensure_ascii=False))
    lic = assets.parent.parent / "LICENSE" if (assets.parent.parent / "LICENSE").exists() else assets.parent / "LICENSE"
    notice = "Animation frames converted from dsh-pet by PC2005-cloud (https://github.com/PC2005-cloud/dsh-pet), MIT License.\n"
    if lic.exists(): notice += "\n" + lic.read_text()
    (out / "NOTICE.txt").write_text(notice)
    prev = Image.new("RGBA", sheet.size, (54, 56, 66, 255)); prev.alpha_composite(sheet)
    prev.convert("RGB").resize((sheet.width // 2, sheet.height // 2), Image.LANCZOS).save(out / "preview.png")
    print(f"{out / (args.id + '.png')}: {nrows} rows, scale {s:.3f}")
    if args.install:
        dst = Path.home() / "Library/Application Support/GLORIA/characters" / args.id
        dst.mkdir(parents=True, exist_ok=True)
        for f in (f"{args.id}.png", f"{args.id}.json", "NOTICE.txt"): shutil.copy2(out / f, dst / f)
        print("installed to", dst)
    shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    main()
