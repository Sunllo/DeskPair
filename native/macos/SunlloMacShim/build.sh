#!/bin/bash
# Builds the Sunllo DeskPair macOS shim into a dylib. Run on a Mac with Xcode's clang. The output is
# ad-hoc signed so it loads under a hardened runtime during development; a release build re-signs with a
# real identity (Phase 5). Frameworks are linked here rather than dlopen'd so missing symbols fail the
# build, not the run.
set -euo pipefail

DIR="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:-$DIR/libSunlloMacShim.dylib}"
ARCH="${ARCH:-arm64}"

SRCS=(display.m input.m capture.m encode.m audio.m clipboard.m promise.m cursor.m security.m decode.mm service.m authorize.m pty.m)
# Only compile sources that exist yet, so the shim can be built while capabilities are still landing.
EXIST=()
for s in "${SRCS[@]}"; do
    if [ -f "$DIR/$s" ]; then
        EXIST+=("$DIR/$s")
    fi
done

FRAMEWORKS=(
    -framework Foundation
    -framework AppKit
    -framework CoreGraphics
    -framework CoreVideo
    -framework CoreMedia
    -framework VideoToolbox
    -framework ScreenCaptureKit
    -framework CoreAudio
    -framework AudioToolbox
    -framework IOKit
    -framework Security
    -framework ApplicationServices
    -framework SystemConfiguration
)

clang -arch "$ARCH" -dynamiclib -fobjc-arc -O2 \
    -mmacosx-version-min=13.0 \
    -install_name @rpath/libSunlloMacShim.dylib \
    "${EXIST[@]}" \
    "${FRAMEWORKS[@]}" \
    -lc++ \
    -o "$OUT"

codesign --force --sign - --timestamp=none "$OUT"
echo "Built $OUT ($ARCH)"
