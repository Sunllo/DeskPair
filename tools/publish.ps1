<#
.SYNOPSIS
  Publishes what ships: one program.

.DESCRIPTION
  DeskPair is a single executable -- the window, the connection manager, the tray and the host engine all run
  in one process -- so the folder holds that program, the few native libraries Skia and ANGLE load by name,
  and the symbols, rather than three hundred assemblies.

  Bundling is close to free; compressing the bundle is not, and is deliberately off. Measured, to the first
  window and settled:

    loose, 279 files    110 MB on disk   283 ms   142 MB working set
    bundled             180 MB on disk   287 ms   143 MB working set
    bundled, compressed  97 MB on disk   341 ms   179 MB working set

  Compression trades 83 MB of disk for 37 MB of memory and 54 ms of every start. Memory is the cost that is
  paid the whole time the app is open, and this one sits on other people's machines, so disk wins.

  PlatformHarness is a development tool and is deliberately not included.

  Symbols (.pdb) are kept: the crash log reports file and line, which is worth far more than the few MB.
  XML documentation is dropped, being of no use at runtime.

.PARAMETER Build
  The build number N, published to artifacts/publish/<rid>-N. A running app locks its own folder, so each
  build goes to a new one.

  This is a folder name and nothing else. It was called -Version, which was actively misleading: it was never
  passed to MSBuild, so a build published as "21" still reported itself as 0.1.0 at runtime, like every other
  build ever made. -Version still works as an alias, because it is in people's fingers.

.PARAMETER AppVersion
  The version the build reports as, overriding <VersionPrefix> in Directory.Build.props. Leave it off for a
  development build: the default already carries the commit (0.2.0+abc1234), which is enough to tell two
  builds apart. Set it for a release, and tag the commit to match.

.PARAMETER Loose
  Publishes as separate assemblies instead of one file, which is what to reach for if a single-file build
  ever misbehaves. Nothing in this app reads Assembly.Location, which is the usual thing single-file breaks;
  the analyser fails the build if that changes.

.PARAMETER Trimmed
  Trims unused IL. Off by default, and untested.

  It used to be impossible: two programs shared this folder, so whichever published second overwrote the
  framework assemblies with copies trimmed for its own graph and the other died at startup on a type the
  trimmer had no reason to keep. One program ends that, so trimming is now merely unproven -- against
  Avalonia XAML, compiled bindings, MVVM source generators, protobuf reflection and Vortice COM, which is a
  large surface. Exercise every window before shipping a trimmed build.

  Anyone revisiting this will need TrimmerRootAssembly entries for System.ObjectModel, System.ComponentModel,
  System.ComponentModel.Primitives and System.Console, which the trimmer drops despite plain use because they
  are forwarded between framework assemblies. Put them in the app project: in Directory.Build.props an
  ItemGroup breaks NuGet restore for every project that takes its target framework from there.

  The COM paths do survive trimming, for what it is worth: display enumeration, H.264 encode and decode, the
  MP4 sink writer with AAC, and audio device enumeration were all checked against a trimmed build.

  If the size has to come down, framework-dependent publishing is the better lever: 98 files and 34 MB in one
  folder, with no trimming risk, in exchange for .NET 10 Desktop Runtime being installed on the target.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][Alias("Version")][int]$Build,
    [string]$AppVersion,
    [string]$Rid = "win-x64",
    [switch]$Loose,
    [switch]$Trimmed
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$out = Join-Path $repo "artifacts\publish\$Rid-$Build"
$framework = if ($Rid -like "win-*") { "net10.0-windows" } else { "net10.0" }

$running = Get-Process | Where-Object { $_.Path -like "$out\*" }
if ($running) {
    throw "Something is running from $out. Close it, or publish to a new version number."
}

$project = "src\DeskPair.Desktop"

$common = @(
    "-c", "Release",
    "-r", $Rid,
    "--self-contained",
    "-f", $framework,
    "-o", $out,
    "--nologo",
    # Drop XML docs from the output only. Turning generation off instead breaks the build: IDE0005 is an error
    # here, and the analyzer needs the documentation file to run.
    "-p:PublishDocumentationFile=false",
    "-p:PublishReferencesDocumentationFiles=false",
    # The PDB path each library records becomes /_/... rather than wherever the repository sits on the machine that
    # built it. DeskPair.dll itself keeps the real one: Avalonia's XAML compiler rewrites that assembly after the C#
    # compiler has mapped it. So release builds are made from a clone whose path names nobody (C:\GitHub\DeskPair).
    "-p:ContinuousIntegrationBuild=true"
)

if ($AppVersion) {
    $common += @("-p:Version=$AppVersion")
}

if (-not $Loose) {
    $common += @("-p:PublishSingleFile=true")
    if ($Rid -notlike "win-*") {
        # On Linux the one file is what gets copied around -- to /opt by the installer, to a desktop by
        # hand -- and libSkiaSharp.so left behind in the publish folder is an app that dies before its
        # first window ("Unable to load shared library 'libSkiaSharp'", seen on the second VM). Embedding
        # the native libraries makes the file self-sufficient; they extract beside the runtime's own.
        $common += @("-p:IncludeNativeLibrariesForSelfExtract=true")
    }
}

if ($Trimmed) {
    Write-Warning "Trimming has never been exercised against the Avalonia UI; open every window before shipping this build."
    $common += @("-p:PublishTrimmed=true", "-p:TrimMode=partial", "-p:TreatWarningsAsErrors=false")
}

Write-Host "publishing $project"
dotnet publish (Join-Path $repo $project) @common
if ($LASTEXITCODE -ne 0) {
    throw "$project failed to publish."
}

# The royalty-free VP9 encoder, when one has been built. It is not in the source tree and not required: the
# loader reports VP9 unavailable without it and negotiation simply never offers it. tools/build-libvpx.ps1
# produces it; unlike OpenH264 there is no licence reason it cannot ship. It is a Windows DLL, so it only
# belongs in a Windows build -- it was being copied into macOS and Linux folders, where nothing could load it.
# One per architecture (build-libvpx.ps1 -Rid); an x64 one built before that existed is still in artifacts/vpx/shim.
$vpx = Join-Path $repo (Join-Path 'artifacts' (Join-Path 'vpx' (Join-Path $Rid 'vpx.dll')))
if (($Rid -eq 'win-x64') -and -not (Test-Path $vpx)) {
    $vpx = Join-Path $repo (Join-Path 'artifacts' (Join-Path 'vpx' (Join-Path 'shim' 'vpx.dll')))
}
if (($Rid -like 'win-*') -and (Test-Path $vpx)) {
    Copy-Item $vpx (Join-Path $out 'vpx.dll') -Force
    Write-Host "included vpx.dll (VP9)"
} elseif ($Rid -like 'win-*') {
    Write-Host "no vpx.dll built; this build offers no VP9 (tools/build-libvpx.ps1 makes one)"
}

# The virtual display driver, as its project signed and released it (tools/fetch-vdd.ps1 fetches it, pinned).
# It goes in a vdd folder, where --install-virtual-display looks; without it this build cannot add displays.
$vdd = Join-Path $repo (Join-Path 'native' (Join-Path 'vdd' $Rid))
if (($Rid -like 'win-*') -and (Test-Path (Join-Path $vdd 'mttvdd.cat'))) {
    $vddOut = Join-Path $out 'vdd'
    New-Item -ItemType Directory -Force $vddOut | Out-Null
    Copy-Item (Join-Path $vdd '*') $vddOut -Force
    Write-Host "included the virtual display driver"
} elseif ($Rid -like 'win-*') {
    Write-Host "no virtual display driver fetched; this build cannot add displays (tools/fetch-vdd.ps1 fetches it)"
}

# The Wayland shim, when one has been built (tools/build-wayland-shim.ps1). WaylandShim.targets has already put it
# into the build -- inside the single file, or beside a loose one -- so this only says whether it is there: a Linux
# build without it runs, and cannot share a Wayland session's screen.
$shim = Join-Path $repo (Join-Path 'artifacts' (Join-Path 'wayland-shim' (Join-Path $Rid 'libSunlloWaylandShim.so')))
if (($Rid -like 'linux-*') -and (Test-Path $shim)) {
    Write-Host "included the Wayland shim"
} elseif ($Rid -like 'linux-*') {
    Write-Host "no Wayland shim built; this build cannot capture a Wayland session (tools/build-wayland-shim.ps1 builds one)"
}

$files = Get-ChildItem $out -File
"{0}: {1} files, {2:N0} MB" -f $out, $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB)

# Ask the build what it calls itself, rather than printing what we asked for. This is the one thing worth
# checking on every publish: for the whole of 0.1.0 the answer was the same for every build ever made, so an
# update check had nothing to compare. A Windows build can answer here; a cross-published one cannot, and nor can
# one for a processor this machine does not run -- an x64 Windows cannot start an ARM64 program.
$exe = Join-Path $out "DeskPair.exe"
if (($Rid -like "win-*") -and (Test-Path $exe)) {
    try {
        "version: {0}" -f (& $exe --version)
    } catch {
        "version: this machine cannot run a $Rid program; run '<app> --version' on one that can"
    }
} else {
    "version: run '<app> --version' on the target to confirm it"
}
