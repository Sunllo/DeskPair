<#
.SYNOPSIS
Builds DeskPair's virtual display driver (native/idd) with the command-line tools only.

.DESCRIPTION
No WDK installation and no driver MSBuild targets: the headers, the two stub libraries and Inf2Cat come from the
WDK's own NuGet packages, pinned below by version and SHA-256 and kept in artifacts/cache/wdk; the compiler is
Visual Studio's (found with vswhere). For each architecture it compiles and links DeskPairDisplay.dll, fills in the
INF's architecture and version, and makes the catalog. With -CodeSigning the DLL is signed, then the catalog is made
and signed: the order Windows' own driver tooling uses, so the files in the catalog are the ones that ship.

Output: native/idd/out/<rid>/{DeskPairDisplay.dll, DeskPairDisplay.inf, deskpairdisplay.cat}, which
tools/publish.ps1 copies into the build's idd folder.

.PARAMETER Architectures
x64, arm64 or both (the default). IddCx has no 32-bit build on current Windows; win-x86 ships without the driver.

.PARAMETER Version
The driver's version (a.b.c.d). Defaults to the product version from Directory.Build.props with .0 appended.

.PARAMETER CodeSigning
An Artifact Signing metadata file, as for tools/package.ps1. Without it the catalog is unsigned: fine for a build to
look at, not for Windows to install.
#>
param(
    [ValidateSet("x64", "arm64")] [string[]]$Architectures = @("x64", "arm64"),
    [string]$Version,
    [string]$CodeSigning
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$source = Join-Path $repo "native\idd"

$WdkVersion = "10.0.26100.6584"
$WdkPackages = @{
    x64 = "c393d03dfb640b5c92f546b32f6770ef68cd3aaf691956e7d66d8e2c28a1b55e"
    arm64 = "e705b2a63eab891def8f98087666f93e8f21da8e3b5def81a624b83fef5bdae9"
}
$UmdfVersion = "2.25"
$IddCxVersion = "1.4"

if (-not $Version) {
    [xml]$props = Get-Content (Join-Path $repo "Directory.Build.props")
    $prefix = @($props.Project.PropertyGroup | ForEach-Object { $_.VersionPrefix } | Where-Object { $_ })[0]
    $Version = "$prefix.0"
}
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "The driver version must be a.b.c.d, not '$Version'." }

# ---- the WDK, from its NuGet packages ------------------------------------------------------------------
$cache = Join-Path $repo "artifacts\cache\wdk"
New-Item -ItemType Directory -Force $cache | Out-Null
function Get-Wdk([string]$arch) {
    $name = "microsoft.windows.wdk.$arch.$WdkVersion.nupkg"
    $package = Join-Path $cache $name
    if (-not (Test-Path $package)) {
        Write-Host "downloading the WDK for $arch ($WdkVersion)"
        $url = "https://api.nuget.org/v3-flatcontainer/microsoft.windows.wdk.$arch/$WdkVersion/$name"
        Invoke-WebRequest -Uri $url -OutFile "$package.part" -UseBasicParsing
        Move-Item "$package.part" $package -Force
    }
    $hash = (Get-FileHash $package -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $WdkPackages[$arch]) {
        Remove-Item $package -Force
        throw "The WDK package for $arch is not the one pinned here (SHA-256 $hash); removed it."
    }
    $unpacked = Join-Path $cache "$arch-$WdkVersion"
    if (-not (Test-Path (Join-Path $unpacked ".done"))) {
        if (Test-Path $unpacked) { Remove-Item $unpacked -Recurse -Force }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($package, $unpacked)
        New-Item -ItemType File (Join-Path $unpacked ".done") | Out-Null
    }
    return (Join-Path $unpacked "c")
}
$wdk = Get-Wdk "x64" # headers and Inf2Cat, whatever the target

# ---- Visual Studio's compiler --------------------------------------------------------------------------
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "No Visual Studio found (vswhere is missing): install it with the C++ desktop workload." }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "Visual Studio has no C++ build tools: add the 'Desktop development with C++' workload." }
$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvarsall.bat"

# ---- signing (optional) ----------------------------------------------------------------------------------
function Set-Signature([string[]]$Files) {
    $signTool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $signTool) { throw "No SignTool: install the Windows SDK, 10.0.22621 or later." }
    $dlib = Join-Path $repo "artifacts\tools\artifact-signing\Azure.CodeSigning.Dlib.dll"
    if (-not (Test-Path $dlib)) { & (Join-Path $PSScriptRoot "fetch-artifact-signing.ps1") | Out-Null }
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $out = & $signTool sign /fd SHA256 /tr "http://timestamp.acs.microsoft.com" /td SHA256 /dlib $dlib /dmdf (Resolve-Path $CodeSigning).Path @Files 2>&1
    $ErrorActionPreference = $previous
    if ($LASTEXITCODE -ne 0) { $out | Write-Host; throw "Signing the driver failed (SignTool exited $LASTEXITCODE)." }
}

# ---- build each architecture ---------------------------------------------------------------------------
$driverDate = (Get-Date).ToString("MM/dd/yyyy", [Globalization.CultureInfo]::InvariantCulture)
foreach ($arch in $Architectures) {
    $rid = "win-$arch"
    $out = Join-Path $source "out\$rid"
    $obj = Join-Path $source "obj\$rid"
    New-Item -ItemType Directory -Force $out, $obj | Out-Null
    Get-ChildItem $out | Remove-Item -Force

    $libs = if ($arch -eq "x64") { $wdk } else { Get-Wdk $arch }
    $libArch = if ($arch -eq "x64") { "x64" } else { "ARM64" }
    $vcArch = if ($arch -eq "x64") { "amd64" } else { "amd64_arm64" }
    $defines = "/DUMDF_DRIVER /DIDDCX_VERSION_MAJOR=1 /DIDDCX_VERSION_MINOR=4 /DIDDCX_MINIMUM_VERSION_REQUIRED=4 " +
        "/DUMDF_VERSION_MAJOR=2 /DUMDF_VERSION_MINOR=25 /D_UNICODE /DUNICODE"
    # The WDK's and the SDK's headers are someone else's: their warnings are not ours to fail on. Ours are, at /W4.
    $includes = "/external:W0 /external:env:INCLUDE /external:I`"$wdk\Include\wdf\umdf\$UmdfVersion`" /external:I`"$wdk\Include\10.0.26100.0\um\iddcx\$IddCxVersion`""
    $linkLibs = "`"$libs\Lib\wdf\umdf\$libArch\$UmdfVersion\WdfDriverStubUm.lib`" `"$libs\Lib\10.0.26100.0\um\$libArch\iddcx\$IddCxVersion\iddcxstub.lib`" " +
        "OneCoreUAP.lib avrt.lib d3d11.lib dxgi.lib ntdll.lib"
    # /MT: the C++ runtime inside the driver, since the machines it lands on need not have Visual Studio's.
    $build = "set `"PATH=$(Split-Path $vswhere);%PATH%`" && call `"$vcvars`" $vcArch >nul && " +
        "cl /nologo /c /W4 /WX /wd4005 /EHsc /std:c++17 /O2 /MT /GS /guard:cf /Zi $defines $includes " +
        "/Fo`"$obj\DeskPairDisplay.obj`" /Fd`"$obj\DeskPairDisplay.pdb`" `"$source\src\DeskPairDisplay.cpp`" && " +
        "link /nologo /DLL /DEBUG /OPT:REF /OPT:ICF /GUARD:CF /SUBSYSTEM:WINDOWS /OUT:`"$out\DeskPairDisplay.dll`" " +
        "/PDB:`"$obj\DeskPairDisplay.pdb`" /IMPLIB:`"$obj\DeskPairDisplay.lib`" `"$obj\DeskPairDisplay.obj`" $linkLibs"
    # Both streams through cmd: PowerShell 5.1 turns a native program's stderr into errors, which Stop makes fatal.
    & cmd.exe /c "$build 2>&1" | Where-Object { $_ -notmatch "^\s*$" } | Write-Host
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $out "DeskPairDisplay.dll"))) { throw "Building the driver for $arch failed." }

    $infArch = if ($arch -eq "x64") { "amd64" } else { "arm64" }
    (Get-Content (Join-Path $source "DeskPairDisplay.inf") -Raw).
        Replace('$ARCH$', $infArch).
        Replace('$DRIVERVER$', "$driverDate,$Version") | Set-Content (Join-Path $out "DeskPairDisplay.inf") -Encoding ascii

    if ($CodeSigning) { Set-Signature @(Join-Path $out "DeskPairDisplay.dll") }

    $os = if ($arch -eq "x64") { "10_RS5_X64,10_19H1_X64,10_VB_X64,10_CO_X64,10_NI_X64,10_GE_X64" } else { "10_RS5_ARM64,10_19H1_ARM64,10_VB_ARM64,10_CO_ARM64,10_NI_ARM64,10_GE_ARM64" }
    $inf2cat = Join-Path $wdk "bin\10.0.26100.0\x86\Inf2Cat.exe"
    $catalogOut = & cmd.exe /c "`"$inf2cat`" /driver:`"$out`" /os:$os 2>&1"
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $out "deskpairdisplay.cat"))) {
        $catalogOut | Write-Host
        throw "Making the driver's catalog for $arch failed."
    }

    if ($CodeSigning) { Set-Signature @(Join-Path $out "deskpairdisplay.cat") }
    Write-Host "driver $Version for $rid in $out$(if ($CodeSigning) { ', signed' } else { ', unsigned' })"
}
