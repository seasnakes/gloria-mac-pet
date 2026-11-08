#!/bin/bash
# Build GLORIA.app next to this script. Needs the Swift toolchain (Xcode or Command Line Tools).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
APP="$ROOT/GLORIA.app"
VERSION="0.3.0"
ARCH="$(uname -m)"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$ROOT/Resources/spritesheet.png" "$APP/Contents/Resources/"
# Other characters: sheets and anchors built by characters/doro and characters/usagi
cp "$ROOT"/characters/doro/doro.{png,json} "$ROOT"/characters/usagi/usagi.{png,json} "$APP/Contents/Resources/"

# Menu bar / Finder icon from the neutral frame's head.
ICONSET="$(mktemp -d)/GLORIA.iconset"
mkdir -p "$ICONSET"
python3 - "$ROOT/Resources/spritesheet.png" "$ICONSET" <<'PY' || true
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
PY
if ls "$ICONSET"/*.png >/dev/null 2>&1; then iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/GLORIA.icns"; fi

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>studio.seasnake.gloria.pet</string>
<key>CFBundleName</key><string>GLORIA</string>
<key>CFBundleDisplayName</key><string>GLORIA</string>
<key>CFBundleExecutable</key><string>GLORIA</string>
<key>CFBundleIconFile</key><string>GLORIA</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>$VERSION</string>
<key>CFBundleVersion</key><string>1</string>
<key>LSMinimumSystemVersion</key><string>14.2</string>
<key>LSUIElement</key><true/>
<key>NSHighResolutionCapable</key><true/>
<key>NSAudioCaptureUsageDescription</key><string>GLORIA 只用系统声音的节拍来跟着音乐跳舞：在内存里分析，不录音、不保存、不上传。</string>
</dict></plist>
PLIST

swiftc -O -swift-version 5 -target "$ARCH-apple-macosx14.2" \
  -module-cache-path "${TMPDIR:-/tmp}/gloria-pet-swift-cache" \
  -framework AppKit -framework CoreAudio -framework AudioToolbox -framework QuartzCore \
  "$ROOT"/Sources/*.swift -o "$APP/Contents/MacOS/GLORIA"

codesign --force --deep --sign - "$APP"
echo "$APP"
