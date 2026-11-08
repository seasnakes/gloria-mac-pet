#!/bin/bash
# Build GLORIA.app from source. Needs the Swift toolchain (Xcode or Command Line Tools); nothing else.
#
#   bash build.sh              → GLORIA.app next to this script
#   bash build.sh --install    → also copy it into /Applications and (re)start it there
#   bash build.sh --dmg        → also make dist/GLORIA-<version>.dmg (drag-to-Applications installer)
#
# Signing: ad-hoc by default. With a Developer ID the DMG opens without Gatekeeper warnings:
#   SIGN_ID="Developer ID Application: Name (TEAMID)" bash build.sh --dmg
#   NOTARY_PROFILE=<notarytool keychain profile>      (optional: notarize and staple the DMG)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
APP="$ROOT/GLORIA.app"
VERSION="$(tr -d ' \n' < "$ROOT/VERSION")"
INSTALL=0; DMG=0
for a in "$@"; do
  case "$a" in
    --install) INSTALL=1 ;;
    --dmg) DMG=1 ;;
    *) echo "unknown option: $a" >&2; exit 2 ;;
  esac
done
# A DMG is for other Macs: build for Apple Silicon and Intel. A local build only needs this Mac's architecture.
if [ "$DMG" = 1 ]; then ARCHS="${ARCHS:-arm64 x86_64}"; else ARCHS="${ARCHS:-$(uname -m)}"; fi

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

# Pets: sprite sheets (gloria, doro, usagi) and GrokBot's 3D model. Each has <id>.json; see characters/<id>/.
for id in gloria doro usagi; do
  cp "$ROOT/characters/$id/$id.png" "$ROOT/characters/$id/$id.json" "$APP/Contents/Resources/"
done
cp "$ROOT/characters/grokbot/grokbot.usdz" "$ROOT/characters/grokbot/grokbot.json" "$APP/Contents/Resources/"

# Icon (tools/make_icon.py regenerates it) and Info.plist.
cp "$ROOT/Resources/GLORIA.icns" "$APP/Contents/Resources/"
sed "s/__VERSION__/$VERSION/" "$ROOT/Resources/Info.plist" > "$APP/Contents/Info.plist"

BINS=()
for arch in $ARCHS; do
  swiftc -O -swift-version 5 -target "$arch-apple-macosx14.2" \
    -module-cache-path "${TMPDIR:-/tmp}/gloria-pet-swift-cache-$arch" \
    -framework AppKit -framework CoreAudio -framework AudioToolbox -framework QuartzCore -framework SceneKit -framework Metal \
    "$ROOT"/Sources/*.swift -o "$APP/Contents/MacOS/GLORIA-$arch"
  BINS+=("$APP/Contents/MacOS/GLORIA-$arch")
done
lipo -create "${BINS[@]}" -output "$APP/Contents/MacOS/GLORIA"
rm -f "${BINS[@]}"

if [ -n "${SIGN_ID:-}" ]; then
  codesign --force --deep --options runtime --timestamp --sign "$SIGN_ID" "$APP"
else
  codesign --force --deep --sign - "$APP"
fi
echo "$APP ($VERSION, $ARCHS)"

if [ "$DMG" = 1 ]; then
  mkdir -p "$ROOT/dist"
  OUT="$ROOT/dist/GLORIA-$VERSION.dmg"
  STAGE="$(mktemp -d)/GLORIA"
  mkdir -p "$STAGE"
  ditto "$APP" "$STAGE/GLORIA.app"
  ln -s /Applications "$STAGE/Applications"
  cp "$ROOT/Resources/DMG-README.txt" "$STAGE/先读我.txt"
  rm -f "$OUT"
  hdiutil create -volname "GLORIA $VERSION" -srcfolder "$STAGE" -fs HFS+ -format UDZO -imagekey zlib-level=9 -ov "$OUT" >/dev/null
  if [ -n "${SIGN_ID:-}" ]; then codesign --force --timestamp --sign "$SIGN_ID" "$OUT"; fi
  if [ -n "${NOTARY_PROFILE:-}" ]; then
    xcrun notarytool submit "$OUT" --keychain-profile "$NOTARY_PROFILE" --wait
    xcrun stapler staple "$OUT"
  fi
  echo "dmg: $OUT"
fi

if [ "$INSTALL" = 1 ]; then
  # Ask the running copy to quit cleanly (keeps its settings); fall back to pkill.
  for x in /Applications/GLORIA.app/Contents/MacOS/GLORIA "$APP/Contents/MacOS/GLORIA"; do
    [ -x "$x" ] && "$x" --ctl '{"ctl":"quit"}' >/dev/null 2>&1 && break
  done
  for i in $(seq 20); do pgrep -x GLORIA >/dev/null || break; sleep 0.3; done
  pkill -x GLORIA 2>/dev/null || true
  for i in $(seq 20); do pgrep -x GLORIA >/dev/null || break; sleep 0.3; done
  rm -rf /Applications/GLORIA.app
  ditto "$APP" /Applications/GLORIA.app
  sleep 1
  open /Applications/GLORIA.app
  echo "installed: /Applications/GLORIA.app"
fi
