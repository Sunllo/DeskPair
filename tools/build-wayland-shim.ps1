<#
.SYNOPSIS
  Builds the Linux Wayland shim (libSunlloWaylandShim.so) in an Ubuntu 22.04 container.

.DESCRIPTION
  The shim is a few hundred lines of C around one PipeWire stream (native/linux/SunlloWaylandShim). PipeWire's
  format negotiation is made of static inline functions in headers, so it cannot be reached from C# directly;
  see shim.h for the surface C# sees.

  The Linux build of DeskPair is published from Windows, and C for Linux has to be compiled on Linux, so this
  runs native/linux/SunlloWaylandShim/build.sh inside Docker. Ubuntu 22.04 is chosen on purpose: the library then
  needs nothing newer than its glibc (2.35) and its PipeWire (0.3.48), and runs on every distribution the app
  supports.

  The result is artifacts/wayland-shim/<rid>/libSunlloWaylandShim.so, which publish.ps1 puts beside the Linux
  executable and the loader also finds through SUNLLO_WAYLANDSHIM_PATH. Nothing here is committed.

.PARAMETER Rid
  linux-x64, linux-arm64 or linux-arm (32-bit ARMv7). The ARM ones run the container under emulation, which Docker
  Desktop provides.
#>
[CmdletBinding()]
param(
    [ValidateSet('linux-x64', 'linux-arm64', 'linux-arm')]
    [string]$Rid = 'linux-x64'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$source = Join-Path $root (Join-Path 'native' (Join-Path 'linux' 'SunlloWaylandShim'))
$out = Join-Path $root (Join-Path 'artifacts' (Join-Path 'wayland-shim' $Rid))
New-Item -ItemType Directory -Force -Path $out | Out-Null

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker is not installed; build.sh can also be run by hand on any Linux machine with libpipewire-0.3-dev.'
}

$platform = @{ 'linux-x64' = 'linux/amd64'; 'linux-arm64' = 'linux/arm64'; 'linux-arm' = 'linux/arm/v7' }[$Rid]
$script = 'export DEBIAN_FRONTEND=noninteractive && apt-get update -qq >/dev/null && ' +
          'apt-get install -y -qq gcc libc6-dev pkg-config libpipewire-0.3-dev >/dev/null && ' +
          'bash /shim/build.sh /out/libSunlloWaylandShim.so'

docker run --rm --platform $platform -v "${source}:/shim:ro" -v "${out}:/out" ubuntu:22.04 bash -c $script
if ($LASTEXITCODE -ne 0) {
    throw "The shim did not build (docker exited $LASTEXITCODE)."
}

Get-Item (Join-Path $out 'libSunlloWaylandShim.so') | Select-Object FullName, Length
