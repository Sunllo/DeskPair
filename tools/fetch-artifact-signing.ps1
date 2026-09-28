<#
.SYNOPSIS
  Fetches the Artifact Signing plug-in for SignTool into artifacts/tools/artifact-signing/.

.DESCRIPTION
  Microsoft's Artifact Signing (formerly Trusted Signing) signs from the cloud: SignTool loads this library (/dlib),
  which has the service sign each file's digest with a certificate the service holds and renews every three days. The
  package (Microsoft.ArtifactSigning.Client, from nuget.org) is pinned by version and SHA-256, so what runs is exactly
  what was reviewed. It carries its own Visual C++ runtime and needs the .NET 8 runtime. Nothing is committed.

  package.ps1 runs this itself when it is asked to sign and the plug-in is missing.
#>
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

$version = "1.0.128"
$url = "https://api.nuget.org/v3-flatcontainer/microsoft.artifactsigning.client/$version/microsoft.artifactsigning.client.$version.nupkg"
$sha256 = "74bd7d27e6ce1051409c38d9b46bc8df0400ecd643d51ffbf2ac00869061e40b"

$out = Join-Path $repo (Join-Path "artifacts" (Join-Path "tools" "artifact-signing"))
$work = Join-Path ([IO.Path]::GetTempPath()) ("deskpair-artifact-signing-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $work, $out | Out-Null
try {
    # A .nupkg is a zip; Expand-Archive on Windows PowerShell only opens files that are called one.
    $zip = Join-Path $work "client.zip"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    $actual = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
    if ($actual -ne $sha256) {
        throw "$url does not match its pinned hash (got $actual, expected $sha256). Nothing was installed."
    }
    Expand-Archive $zip -DestinationPath (Join-Path $work "x") -Force
    Copy-Item (Join-Path $work (Join-Path "x" (Join-Path "bin" (Join-Path "x64" "*")))) $out -Recurse -Force
    Copy-Item (Join-Path $work (Join-Path "x" "LICENSE.md")) $out -Force
    "Artifact Signing client {0} in {1}" -f $version, $out
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
