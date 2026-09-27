<#
.SYNOPSIS
  Fetches the Virtual Display Driver release DeskPair adds displays with, into native/vdd/<rid>/.

.DESCRIPTION
  VDD (github.com/VirtualDrivers/Virtual-Display-Driver, MIT) as its project released it: the driver package
  signed by the SignPath Foundation. Nothing is built or re-signed here, and nothing is committed -- the
  download is pinned by URL and SHA-256 instead, so what ships is exactly what was reviewed.

  Release 25.7.23's "Driver.Only" packages carry the driver signed on 24 December 2024 (DriverVer
  12/24/2024,11.30.4.434); its source is the repository's 24.12.24 tag, and the licence is taken from there.
  The x64 package is named "x86" upstream.

  publish.ps1 copies native/vdd/<rid>/ into the build's vdd folder when it is there; without it a build simply
  cannot add displays, and says so.

.PARAMETER Rid
  win-x64 (default) or win-arm64.
#>
param(
    [ValidateSet("win-x64", "win-arm64")][string]$Rid = "win-x64"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

$packages = @{
    "win-x64"   = @{
        Url    = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip"
        Sha256 = "e24210692b442b39af763536330ce78b423f19342b7a7792c26de3944e418b3a"
    }
    "win-arm64" = @{
        Url    = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-ARM64.Driver.Only.zip"
        Sha256 = "dcd487161469c726bab60f174a7e7f70ee02afd437cad0d5c0e6755953de5a25"
    }
}
$license = @{
    Url    = "https://raw.githubusercontent.com/VirtualDrivers/Virtual-Display-Driver/24.12.24/LICENSE"
    Sha256 = "c2285c867ddc9b88f43b4f8fa2c77a9eec66207166c1217b37353a2ccd62ac03"
}

function Get-Pinned([string]$url, [string]$sha256, [string]$path) {
    Invoke-WebRequest -Uri $url -OutFile $path -UseBasicParsing
    $actual = (Get-FileHash -Algorithm SHA256 $path).Hash.ToLowerInvariant()
    if ($actual -ne $sha256) {
        Remove-Item $path -Force
        throw "$url does not match its pinned hash (got $actual, expected $sha256). Nothing was installed."
    }
}

$out = Join-Path $repo (Join-Path "native" (Join-Path "vdd" $Rid))
$work = Join-Path ([IO.Path]::GetTempPath()) ("deskpair-vdd-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    $zip = Join-Path $work "vdd.zip"
    Get-Pinned $packages[$Rid].Url $packages[$Rid].Sha256 $zip
    Expand-Archive $zip -DestinationPath $work -Force
    $package = Join-Path $work "VirtualDisplayDriver"

    New-Item -ItemType Directory -Force $out | Out-Null
    # The package only. Its sample vdd_settings.xml stays behind: DeskPair writes its own, elsewhere.
    foreach ($file in "MttVDD.inf", "MttVDD.dll", "mttvdd.cat") {
        Copy-Item (Join-Path $package $file) (Join-Path $out $file) -Force
    }

    Get-Pinned $license.Url $license.Sha256 (Join-Path $out "LICENSE")

    $signer = (Get-AuthenticodeSignature (Join-Path $out "mttvdd.cat")).SignerCertificate
    "{0}: MttVDD package in {1}, catalog signed by {2}" -f $Rid, $out, $signer.Subject
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
