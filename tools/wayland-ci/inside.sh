#!/bin/bash
# Runs inside the container (run.sh starts it under dbus-run-session): PipeWire, a pretend monitor (test-screen.c)
# published as a PipeWire video source in memfd buffers, and LinuxHarness reading that node through the product's shim
# and capturer -- once handing PipeWire's buffers on uncopied, as a compositor's stream is, once through the copying
# path. Each run must see changing frames of the test colour, and the first must not have copied them.
set -euo pipefail

COLOUR=3366aa
WIDTH=1280
HEIGHT=800
export XDG_RUNTIME_DIR=/tmp/xdg-runtime
mkdir -p -m 700 "$XDG_RUNTIME_DIR"

# The shim from source, the way build.sh builds it for the product.
bash /repo/native/linux/SunlloWaylandShim/build.sh /tmp/libSunlloWaylandShim.so
export SUNLLO_WAYLANDSHIM_PATH=/tmp/libSunlloWaylandShim.so

pipewire > /tmp/pipewire.log 2>&1 &
sleep 1
wireplumber > /tmp/wireplumber.log 2>&1 &
sleep 1

# One colour and a moving bar at 30 frames a second in BGRx, as a monitor's stream comes from a compositor.
# shellcheck disable=SC2046
cc -O2 -Wall -o /tmp/test-screen /repo/tools/wayland-ci/test-screen.c $(pkg-config --cflags --libs libpipewire-0.3)
/tmp/test-screen "$COLOUR" "$WIDTH" "$HEIGHT" > /tmp/test-screen.log 2>&1 &

node=""
for _ in $(seq 1 50); do
  node=$(pw-dump 2> /dev/null \
    | jq -r '.[] | select(.type == "PipeWire:Interface:Node" and .info.props["node.name"] == "deskpair-test-screen") | .id' \
    | head -1)
  [ -n "$node" ] && break
  sleep 0.2
done
[ -n "$node" ] || { echo "the test source never appeared:"; cat /tmp/test-screen.log; exit 2; }

harness=/harness/DeskPair.Tools.LinuxHarness
status=0
for path in uncopied --copy; do
  echo "== pipewire-grab, $path"
  flag=()
  [ "$path" = --copy ] && flag=(--copy)
  "$harness" pipewire-grab --node "$node" --width "$WIDTH" --height "$HEIGHT" --out /tmp/grab --seconds 3 \
    --expect-color "$COLOUR" "${flag[@]}" > /tmp/grab.log 2>&1 || status=$?
  grep -vE '^frame #' /tmp/grab.log
  grep -E '^frame #' /tmp/grab.log | tail -n 2
  [ "$status" -eq 0 ] || break

  # The bar moves every frame, so frames that do not differ were not fresh.
  if ! grep -qE 'diffFromPrev=0[.]0*[1-9]' /tmp/grab.log; then
    echo "every frame was the same picture"
    status=9
    break
  fi
  if [ "$path" = uncopied ] && ! grep -q 'handed on uncopied' /tmp/grab.log; then
    echo "the memfd buffers were copied instead of handed on"
    status=10
    break
  fi
done

if [ "$status" -ne 0 ]; then
  echo "== failed with $status; PipeWire and the test screen said:"
  tail -n 20 /tmp/pipewire.log /tmp/wireplumber.log /tmp/test-screen.log || true
fi
exit "$status"
