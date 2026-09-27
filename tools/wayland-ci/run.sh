#!/bin/bash
# The PipeWire half of Wayland screen sharing with nobody at a screen: publishes LinuxHarness for linux-x64, builds
# the container in this folder (PipeWire and a GStreamer test source) and runs inside.sh in it, which builds the shim
# from source and reads the test picture through it both ways. Needs the .NET SDK and Docker; runs in CI and by hand:
#
#   bash tools/wayland-ci/run.sh
#
# The portal conversation in front of it is verified by hand on GNOME and KDE (docs/architecture.md, Linux Wayland);
# the Dockerfile says why no portal runs in here.
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
out="$root/artifacts/wayland-ci"

dotnet publish "$root/tools/DeskPair.Tools.LinuxHarness" -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$out/harness" --nologo -v q

docker build -t deskpair-wayland-ci "$root/tools/wayland-ci"

# Docker Desktop on Windows takes a Windows path for a bind mount; Git Bash would rewrite it on the way.
mount_root="$root"
mount_harness="$out/harness"
if command -v cygpath > /dev/null 2>&1; then
  mount_root="$(cygpath -w "$root")"
  mount_harness="$(cygpath -w "$out/harness")"
fi

MSYS_NO_PATHCONV=1 docker run --rm \
  -v "$mount_root:/repo:ro" -v "$mount_harness:/harness:ro" \
  deskpair-wayland-ci dbus-run-session -- bash /repo/tools/wayland-ci/inside.sh
