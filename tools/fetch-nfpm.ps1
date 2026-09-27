<#
.SYNOPSIS
  Fetches nfpm, which builds the Linux .deb, .rpm and Arch packages, into artifacts/tools/nfpm/.

.DESCRIPTION
  nfpm (github.com/goreleaser/nfpm, MIT) turns one description of a package -- packaging/linux/nfpm.yaml -- into
  all three formats, and runs on Windows, so the Linux packages are made on the same machine as the archives
  and need neither dpkg-deb nor rpmbuild. The download is pinned by URL and SHA-256 (from the release's own
  checksums.txt), so what runs is exactly what was reviewed. Nothing is committed.

  package.ps1 runs this itself when artifacts/tools/nfpm/nfpm.exe is missing.
#>
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

$version = "2.47.0"
$url = "https://github.com/goreleaser/nfpm/releases/download/v$version/nfpm_${version}_Windows_x86_64.zip"
$sha256 = "788f88a3bba0d89baa639aba54ba28b384958878700d440ce199a7ff4a567f11"

$out = Join-Path $repo (Join-Path "artifacts" (Join-Path "tools" "nfpm"))
$work = Join-Path ([IO.Path]::GetTempPath()) ("deskpair-nfpm-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $work, $out | Out-Null
try {
    $zip = Join-Path $work "nfpm.zip"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    $actual = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
    if ($actual -ne $sha256) {
        throw "$url does not match its pinned hash (got $actual, expected $sha256). Nothing was installed."
    }
    Expand-Archive $zip -DestinationPath $work -Force
    Copy-Item (Join-Path $work "nfpm.exe") (Join-Path $out "nfpm.exe") -Force
    Copy-Item (Join-Path $work "LICENSE*") $out -Force -ErrorAction SilentlyContinue
    "nfpm {0} in {1}" -f $version, $out
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
