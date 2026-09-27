<#
.SYNOPSIS
  Builds a libvpx DLL for Windows, for the royalty-free VP9 encoder.

.DESCRIPTION
  Two steps, because neither alone produces what .NET can load.

  vcpkg builds libvpx, fetching its own nasm, so nothing has to be installed beyond Visual Studio. Its
  Windows port produces a static vpx.lib: libvpx's MSVC build has no shared-library configuration, and
  vcpkg does not invent one.

  A static library cannot be loaded by NativeLibrary, so the second step links one. libvpx ships the list of
  symbols it means to export (vpx/exports_com, exports_enc, exports_dec and the per-codec lists), which is
  exactly a module definition file, so the DLL exports what libvpx itself would have exported and nothing
  more.

  The result is artifacts/vpx/shim/vpx.dll, which the loader finds through SUNLLO_LIBVPX_PATH, or beside the
  executable once publish.ps1 has copied it.

  Nothing here is committed. Unlike OpenH264 there is no licence reason not to ship the binary -- libvpx is
  BSD-3 and VP9's patents are granted -- but a built artefact does not belong in the source tree either.

.PARAMETER Sources
  Where libvpx's own headers are, for the export lists. Defaults to reference/libvpx, which is the read-only
  checkout the rest of the project uses; see native/libvpx/README.md for how to get it.

.PARAMETER BuildTrees
  vcpkg's scratch directory. Keep it short: MSVC fails with "cannot open compiler generated file ''" when the
  intermediate paths approach MAX_PATH, which a deep temp directory does on its own.
#>
[CmdletBinding()]
param(
    [string]$Sources = (Join-Path $PSScriptRoot '..\reference\libvpx'),
    [string]$BuildTrees = 'C:\vpxbt'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$work = Join-Path $root 'artifacts\vpx'
$shim = Join-Path $work 'shim'

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "Visual Studio is not installed (no vswhere at $vswhere)." }
$vs = & $vswhere -latest -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio is installed but without the C++ tools.' }

$vcpkg = Join-Path $vs 'VC\vcpkg\vcpkg.exe'
if (-not (Test-Path $vcpkg)) { throw "This Visual Studio has no bundled vcpkg at $vcpkg." }

$exports = @(
    'vpx\exports_com', 'vpx\exports_enc', 'vpx\exports_dec',
    'vp8\exports_enc', 'vp8\exports_dec', 'vp9\exports_enc', 'vp9\exports_dec'
) | ForEach-Object { Join-Path $Sources $_ } | Where-Object { Test-Path $_ }
if (-not $exports) { throw "No libvpx export lists under $Sources; clone libvpx there first." }

New-Item -ItemType Directory -Force -Path $work, $shim | Out-Null

# vcpkg in manifest mode: the manifest names the port, and the baseline pins which version of it.
$manifest = Join-Path $work 'vcpkg.json'
if (-not (Test-Path $manifest)) {
    '{ "name": "sunllo-vpx-build", "version-string": "0.0.1", "dependencies": [ "libvpx" ] }' |
        Out-File -FilePath $manifest -Encoding utf8
    Push-Location $work
    try { & $vcpkg x-update-baseline --add-initial-baseline } finally { Pop-Location }
}

Write-Host "building libvpx through vcpkg (this takes a few minutes the first time)"
Push-Location $work
try {
    & $vcpkg install --triplet x64-windows --x-buildtrees-root=$BuildTrees
    if ($LASTEXITCODE -ne 0) { throw "vcpkg install failed ($LASTEXITCODE)." }
} finally {
    Pop-Location
}

$static = Join-Path $work 'vcpkg_installed\x64-windows\lib\vpx.lib'
if (-not (Test-Path $static)) { throw "vcpkg did not produce $static." }

# libvpx's export lists are "text <name>" for functions and "data <name>" for the codec interface objects,
# which a .def marks with DATA or the loader hands back the address of a pointer instead of the object.
$def = Join-Path $shim 'vpx.def'
$lines = Get-Content $exports |
    Sort-Object -Unique |
    ForEach-Object {
        $parts = $_ -split '\s+', 2
        if ($parts[0] -eq 'data') { "    $($parts[1]) DATA" }
        elseif ($parts[0] -eq 'text') { "    $($parts[1])" }
    }
@('EXPORTS') + $lines | Out-File -FilePath $def -Encoding ascii

$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
Push-Location $shim
try {
    cmd /c "`"$vcvars`" >nul && link /NOLOGO /DLL /MACHINE:X64 /OUT:vpx.dll /DEF:vpx.def `"$static`"" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "link failed ($LASTEXITCODE)." }
} finally {
    Pop-Location
}

$dll = Join-Path $shim 'vpx.dll'
$size = [math]::Round((Get-Item $dll).Length / 1MB, 1)
Write-Host "$dll ($size MB, $($lines.Count) exports)"
Write-Host 'set SUNLLO_LIBVPX_PATH to it, or let tools/publish.ps1 copy it beside the executables.'
