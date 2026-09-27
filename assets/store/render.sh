#!/bin/bash
# Renders the two graphics Google Play demands, from the SVG sources beside this script. Run on a Mac;
# AppKit is the renderer, so nothing has to be installed. Output goes to artifacts/store/.
#
#   icon 512 x 512, 32-bit PNG with alpha
#   feature graphic 1024 x 500, no alpha -- Play rejects transparency here, so it is composited on white
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/artifacts/store"
mkdir -p "$OUT"
WORK="$(mktemp -d)"

swiftc -O -o "$WORK/render" "$ROOT/assets/store/render.swift" ||
    { echo "swiftc failed; is Xcode installed?" >&2; exit 1; }

"$WORK/render" "$ROOT/assets/logo/deskpair.svg" 512 512 "$OUT/play-icon-512.png" 0
"$WORK/render" "$ROOT/assets/store/play-feature-graphic.svg" 1024 500 "$OUT/play-feature-1024x500.png" 1
rm -rf "$WORK"

for f in "$OUT"/play-*.png; do
    printf '%-30s %s x %s  alpha=%s\n' "$(basename "$f")" \
        "$(sips -g pixelWidth "$f" | tail -1 | tr -dc 0-9)" \
        "$(sips -g pixelHeight "$f" | tail -1 | tr -dc 0-9)" \
        "$(sips -g hasAlpha "$f" | tail -1 | awk '{print $2}')"
done
