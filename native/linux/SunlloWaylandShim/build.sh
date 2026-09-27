#!/bin/bash
# Builds the Sunllo DeskPair Wayland shim into a shared library. Run on Linux with a C compiler, pkg-config and
# PipeWire's headers (Debian/Ubuntu: build-essential pkg-config libpipewire-0.3-dev). tools/build-wayland-shim.ps1
# runs this in an Ubuntu 22.04 container, so the result needs nothing newer than 22.04's glibc and PipeWire.
#
# libpipewire is linked, not dlopen'd: a machine without it has no portal screen sharing either, and the loader
# reports the shim unavailable when its dependency is missing, which is the same answer.
set -euo pipefail

DIR="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:-$DIR/libSunlloWaylandShim.so}"

# PipeWire's headers through -isystem: their static inline functions are not ours to hold to -Werror.
CFLAGS_PW="$(pkg-config --cflags libpipewire-0.3 | sed 's/-I/-isystem /g')"
LIBS_PW="$(pkg-config --libs libpipewire-0.3)"

# shellcheck disable=SC2086
cc -std=gnu11 -O2 -g -fPIC -shared -fvisibility=hidden \
    -Wall -Wextra -Werror -Wno-missing-field-initializers \
    $CFLAGS_PW \
    "$DIR/pipewire.c" \
    $LIBS_PW -lpthread \
    -Wl,-soname,libSunlloWaylandShim.so -Wl,--no-undefined -Wl,-z,relro,-z,now \
    -o "$OUT"

echo "Built $OUT (libpipewire $(pkg-config --modversion libpipewire-0.3))"
