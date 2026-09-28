<#
.SYNOPSIS
    Builds the downloadable files for a release -- archives, the Windows installers and the Linux packages -- and the
    release.json the website reads.

.DESCRIPTION
    This is the release path. publish.ps1 is the inner loop -- build-numbered folders, -Loose and -Trimmed
    escape hatches, and a doc comment carrying measurements worth keeping -- and this calls it rather than
    replacing it. Two scripts means a change to how releases are packaged cannot break the daily loop.

    macOS is not built here. build/macos/package.sh owns it, on a Mac, because only that path signs,
    notarises and staples, and the notarised DMG is what carries the app's identity to TCC. If a DMG for
    this version has been copied into artifacts/macos/<arch>/, it is hashed and listed; if not, it is
    reported as missing and left out. An entry for a file the server does not have is the one thing this
    manifest must not be able to express.

    Everything lands in artifacts/release/<channel>/, ready to copy to the release folder the update check reads.

.EXAMPLE
    ./tools/package.ps1 -AppVersion 0.3.0
    ./tools/package.ps1 -AppVersion 0.3.0 -Rids win-x64 -Channel beta
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AppVersion,
    [string[]]$Rids = @("win-x64", "win-x86", "win-arm64", "linux-x64", "linux-arm64", "linux-arm"),
    [string]$Channel = "stable",
    [string]$NotesFile,
    [string]$NotesFileZh,
    # A folder of notes.<code>.txt, one file per language the website is published in (en, zh, zh-hans,
    # ja, ko, de, fr, es, pt-br, ru), one note per line. The download page shows the reader's language and
    # falls back to English, so a missing file is a warning here and English there.
    [string]$NotesDir,
    # The release signing key (PKCS#8, from KeyGen). With it, release.json.sig is written beside the
    # manifest and installed apps can install the update themselves; without it they can only be told.
    [string]$SigningKey,
    # An Artifact Signing metadata file (the account's endpoint, name and certificate profile; the official builds'
    # is kept in the private repository). With it the Windows programs and installers are Authenticode-signed, as
    # whoever ran `az login` on this machine; without it they go out unsigned and SmartScreen names no publisher.
    [string]$CodeSigning,
    [int]$Build = ([int]((Get-Date).ToUniversalTime().ToString("MMddHHmm")))
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

# ---- guards, before anything is built --------------------------------------------------------------
# All three of these are cheap here and expensive after a release has gone out with the wrong number in it.

if ($AppVersion -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "AppVersion '$AppVersion' is not a version. Expected 0.3.0, or 0.3.0-rc.1."
}
$core = ($AppVersion -split '-')[0]

$props = Get-Content (Join-Path $repo "Directory.Build.props") -Raw
if ($props -notmatch '<VersionPrefix>([^<]+)</VersionPrefix>') {
    throw "Directory.Build.props has no VersionPrefix."
}
if ($Matches[1] -ne $core) {
    throw "Directory.Build.props says $($Matches[1]); you asked for $core. Edit VersionPrefix and app.manifest together."
}

$manifest = Get-Content (Join-Path $repo "src\DeskPair.Desktop\app.manifest") -Raw
if ($manifest -notmatch 'assemblyIdentity[^>]*?version="([\d.]+)"') {
    throw "app.manifest has no assemblyIdentity version."
}
if ($Matches[1] -ne "$core.0") {
    throw "app.manifest says $($Matches[1]); expected $core.0. VersionTests enforces this pair, and this is where it gets forgotten."
}

if ((git -C $repo status --porcelain)) {
    Write-Warning "The working tree is dirty. The build will carry a +sha naming a commit that does not contain it."
}

# The Windows installers are built with WiX v7, which refuses to run until this machine has accepted its Open Source
# Maintenance Fee EULA. Accepting it is a person's decision, so it is asked for here, before an hour of building,
# rather than made on their behalf.
if (($Rids | Where-Object { $_ -like "win-*" }) -and -not (Test-Path (Join-Path $env:USERPROFILE ".wix\wix7-osmf-eula.txt"))) {
    throw "The Windows installers need WiX v7, whose Open Source Maintenance Fee EULA this machine has not accepted. Read it (packaging\windows\DeskPair.wixproj says where) and, if you accept it: dotnet build packaging\windows\DeskPair.wixproj -t:AcceptEula -p:EulaId=wix7"
}

$commit = (git -C $repo rev-parse --short=7 HEAD 2>$null)
$release = Join-Path $repo "artifacts\release\$Channel"
$stageRoot = Join-Path $release "stage"
Remove-Item $release -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

# ---- GNU tar, or no Linux artifact -----------------------------------------------------------------
# The tar in System32 is bsdtar and cannot set the executable bit, so a tarball built with it extracts a
# DeskPair nobody can run. A missing artifact is recoverable; a broken one is a support ticket per user.
function Resolve-GnuTar {
    foreach ($candidate in @(
        "C:\Program Files\Git\usr\bin\tar.exe",
        "C:\Program Files (x86)\Git\usr\bin\tar.exe",
        "/usr/bin/tar")) {
        if (Test-Path $candidate) {
            $version = & $candidate --version 2>$null | Select-Object -First 1
            if ($version -match "GNU tar") { return $candidate }
        }
    }
    return $null
}

$artifacts = @()

function Add-Artifact([string]$Path, [string]$Platform, [string]$Arch, [string]$Requires, [bool]$Signed, [string]$Format) {
    $item = Get-Item $Path
    $hash = (Get-FileHash -Algorithm SHA256 $Path).Hash.ToLowerInvariant()

    # sha256sum -c format: two spaces, so a reader can verify with the tool they already have. And a bare LF: with
    # Set-Content's CRLF, sha256sum took the carriage return for part of the file name and failed every line.
    [System.IO.File]::WriteAllText("$Path.sha256", "$hash  $($item.Name)`n", [System.Text.Encoding]::ASCII)
    $script:artifacts += [ordered]@{
        platform = $Platform
        arch     = $Arch
        name     = $item.Name
        size     = $item.Length
        sha256   = $hash
        requires = $Requires
        signed   = $Signed
        format   = $Format
    }
    "  {0,-38} {1,8:N1} MB" -f $item.Name, ($item.Length / 1MB) | Write-Host
}

# ---- Linux system packages ---------------------------------------------------------------------------
# One description (packaging/linux/nfpm.yaml), three formats: .deb, .rpm and Arch's .pkg.tar.zst, each named the
# way its own distributions name packages. nfpm reads only some fields from the environment, so the template's
# @NAME@ placeholders are filled in here and the result handed to it.
function Add-LinuxPackages([string]$Rid, [string]$Stage) {
    $nfpm = Join-Path $repo "artifacts\tools\nfpm\nfpm.exe"
    if (-not (Test-Path $nfpm)) { & (Join-Path $PSScriptRoot "fetch-nfpm.ps1") | Out-Null }

    $names = @{
        "linux-x64"   = @{ Nfpm = "amd64"; Deb = "amd64"; Rpm = "x86_64";  Arch = "x86_64";  Rpm64 = "()(64bit)" }
        "linux-arm64" = @{ Nfpm = "arm64"; Deb = "arm64"; Rpm = "aarch64"; Arch = "aarch64"; Rpm64 = "()(64bit)" }
        "linux-arm"   = @{ Nfpm = "arm7";  Deb = "armhf"; Rpm = "armv7hl"; Arch = "armv7h";  Rpm64 = "" }
    }[$Rid]
    $files = [ordered]@{
        "deb"       = "deskpair_$($AppVersion)_$($names.Deb).deb"
        "rpm"       = "deskpair-$AppVersion-1.$($names.Rpm).rpm"
        "archlinux" = "deskpair-$AppVersion-1-$($names.Arch).pkg.tar.zst"
    }

    $template = [IO.File]::ReadAllText((Join-Path $repo "packaging\linux\nfpm.yaml"))
    $utf8 = New-Object System.Text.UTF8Encoding $false
    foreach ($format in $files.Keys) {
        $marker = Join-Path $stageRoot "packaged-$format"
        [IO.File]::WriteAllText($marker, $format, $utf8)
        $config = $template.Replace("@NFPM_ARCH@", $names.Nfpm).Replace("@VERSION@", $AppVersion).
            Replace("@STAGE@", $Stage.Replace('\', '/')).Replace("@REPO@", "$repo".Replace('\', '/')).
            Replace("@MARKER@", $marker.Replace('\', '/')).Replace("@RPM64@", $names.Rpm64)
        $configPath = Join-Path $stageRoot "nfpm-$Rid-$format.yaml"
        [IO.File]::WriteAllText($configPath, $config, $utf8)

        $target = Join-Path $release $files[$format]
        & $nfpm pkg --packager $format --config $configPath --target $target | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $target) -or (Get-Item $target).Length -eq 0) {
            throw "nfpm could not build the $format package for $Rid."
        }
        Add-Artifact -Path $target -Platform "linux" -Arch ($Rid -replace '^linux-', '') `
            -Requires "glibc 2.31" -Signed $false -Format $format
    }
}

# ---- code signing ---------------------------------------------------------------------------------------
# SignTool with the Artifact Signing plug-in (tools/fetch-artifact-signing.ps1): the service signs each file's digest
# with a certificate it holds, signed in through the Azure CLI. Found before anything is built, so a machine that
# cannot sign says so at the start rather than after an hour.
$signTool = $null
$dlib = $null
if ($CodeSigning) {
    if (-not (Test-Path $CodeSigning)) { throw "No Artifact Signing metadata at $CodeSigning." }
    $CodeSigning = (Resolve-Path $CodeSigning).Path
    $signTool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $signTool) { throw "No SignTool: install the Windows SDK, 10.0.22621 or later." }
    $dlib = Join-Path $repo "artifacts\tools\artifact-signing\Azure.CodeSigning.Dlib.dll"
    if (-not (Test-Path $dlib)) { & (Join-Path $PSScriptRoot "fetch-artifact-signing.ps1") | Out-Null }

    # The plug-in signs in through the Azure CLI, which it runs by name, so the CLI has to be on the path. The zip
    # install, which needs no administrator, lives in %LOCALAPPDATA%\Programs\azure-cli and does not put itself there;
    # left off, the only symptom is "Azure CLI authentication failed due to an unknown error".
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        $cli = Join-Path $env:LOCALAPPDATA "Programs\azure-cli\bin"
        if (-not (Test-Path (Join-Path $cli "az.cmd"))) {
            throw "Signing needs the Azure CLI, signed in (az login) as an account with the Artifact Signing Certificate Profile Signer role."
        }
        $env:PATH = "$cli;$env:PATH"
    }
}

function Set-CodeSignature([string[]]$Files, [string]$Description) {
    # Timestamped, because an Artifact Signing certificate lives three days and the signature has to outlive it.
    # A description (/d) is the name the UAC prompt gives a signed installer. Without one it shows the file's name,
    # and when a program is removed Windows runs its own copy of the installer, kept under a random one (1a2b3c4d.msi).
    $described = if ($Description) { @("/d", $Description, "/du", "https://deskpair.app") } else { @() }
    $out = & $signTool sign /fd SHA256 /tr "http://timestamp.acs.microsoft.com" /td SHA256 @described /dlib $dlib /dmdf $CodeSigning @Files 2>&1
    if ($LASTEXITCODE -ne 0) {
        $out | Write-Host
        throw "Signing failed (SignTool exited $LASTEXITCODE). Signed in? The Azure CLI's az login, as an account with the Artifact Signing Certificate Profile Signer role."
    }
    foreach ($file in $Files) {
        $status = (Get-AuthenticodeSignature $file).Status
        if ($status -ne "Valid") { throw "$file does not carry a valid signature after signing ($status)." }
    }
}

# ---- Windows installer --------------------------------------------------------------------------------
# packaging/windows/DeskPair.wixproj around the staged folder: one MSI per architecture, holding exactly what the zip
# holds, plus the marker that tells the app Windows Installer put it there and the licence.
function Add-WindowsInstaller([string]$Rid, [string]$Stage, [string]$Name) {
    $platform = @{ "win-x64" = "x64"; "win-x86" = "x86"; "win-arm64" = "ARM64" }[$Rid]
    $out = Join-Path $stageRoot "msi-$Rid"
    # Windows Installer versions are three numbers, so a pre-release goes out as its core version. --no-incremental:
    # WiX judges a build up to date by its sources and payload, not by the version or the name asked for, and then
    # looks for an MSI under the new name that it never wrote.
    $log = & dotnet build (Join-Path $repo "packaging\windows\DeskPair.wixproj") -c Release -nologo --no-incremental `
        "-p:Platform=$platform" "-p:PayloadDir=$Stage" "-p:ProductVersion=$core" "-p:OutputName=$Name" -o $out 2>&1
    if ($LASTEXITCODE -ne 0) {
        $log | Write-Host
        throw "The installer for $Rid did not build."
    }

    $msi = Join-Path $release "$Name.msi"
    Move-Item (Join-Path $out "$Name.msi") $msi -Force
    # Signed after it is built: the package holds the program already signed, and its own signature is over the lot.
    if ($CodeSigning) { Set-CodeSignature @($msi) "DeskPair" }
    Add-Artifact -Path $msi -Platform "windows" -Arch ($Rid -replace '^win-', '') `
        -Requires "Windows 10 1809" -Signed ([bool]$CodeSigning) -Format "msi"
}

foreach ($rid in $Rids) {
    Write-Host "==> $rid"
    $published = Join-Path $repo "artifacts\publish\$rid-$Build"
    & (Join-Path $PSScriptRoot "publish.ps1") -Build $Build -AppVersion $AppVersion -Rid $rid | Out-Null
    # publish.ps1 throws on failure and ErrorActionPreference is Stop, so reaching here means it worked.
    # $LASTEXITCODE is not checked: it holds whatever the last native command inside it happened to set.
    if (-not (Test-Path $published)) { throw "publish.ps1 produced nothing for $rid." }

    # Staged under a named folder so the archive does not expand as loose files into somebody's Downloads,
    # and so the folder is called what the release is rather than win-x64-41738.
    $name = "DeskPair-$AppVersion-$rid"
    $stage = Join-Path $stageRoot $name
    Copy-Item $published $stage -Recurse -Force

    if ($rid -like "win-*") {
        $exe = Join-Path $stage "DeskPair.exe"
        if (-not (Test-Path $exe)) { throw "No DeskPair.exe in $stage." }

        # The check that makes it impossible to publish a manifest claiming one version beside a binary
        # that answers another -- the exact failure that made an update channel untestable for all of 0.1.0.
        #
        # Read from the binary rather than by running it. DeskPair.exe is a WinExe, so PowerShell starts it
        # detached and whether any output comes back depends on whether a redirection operator happened to
        # be in the line -- which is not a thing to hang a release guard on. ProductVersion is the same
        # AssemblyInformationalVersion that App.Version reads at run time, sha and all.
        $reported = (Get-Item $exe).VersionInfo.ProductVersion
        if (-not $reported -or -not $reported.StartsWith($AppVersion)) {
            throw "The build says '$reported' but this release is $AppVersion."
        }
        Write-Host "  binary says $reported"

        if ($CodeSigning) {
            # Everything in the program's folder that nobody has signed: ours (DeskPair.exe, vpx.dll) and the ANGLE
            # library Avalonia ships unsigned. Skia's two keep Microsoft's signature, and the virtual display driver is
            # signed through its catalog, which a signature of ours on its .dll would break.
            $vdd = Join-Path $stage "vdd"
            [string[]]$unsigned = @(Get-ChildItem $stage -Recurse -Include *.exe, *.dll |
                Where-Object { -not $_.FullName.StartsWith($vdd, [StringComparison]::OrdinalIgnoreCase) } |
                Where-Object { (Get-AuthenticodeSignature $_.FullName).Status -eq "NotSigned" } |
                ForEach-Object { $_.FullName })
            Set-CodeSignature $unsigned
            Write-Host ("  signed " + (($unsigned | ForEach-Object { Split-Path $_ -Leaf }) -join ", "))
        }

        $zip = Join-Path $release "$name.zip"
        # Not Compress-Archive: markedly slower on a bundle this size, and historically odd about separators.
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, "Optimal", $true)
        Add-Artifact -Path $zip -Platform "windows" -Arch ($rid -replace '^win-', '') `
            -Requires "Windows 10 1809" -Signed ([bool]$CodeSigning) -Format "zip"

        # The zip first: an app from before installers existed installs the first file listed for its machine, and can
        # only unpack a zip.
        Add-WindowsInstaller -Rid $rid -Stage $stage -Name $name
    }
    elseif ($rid -like "linux-*") {
        # No equivalent version check here: an ELF has no Win32 version resource, and a single-file publish
        # leaves no managed assembly beside it to read. The guard above covers the case that actually goes
        # wrong, which is a Windows release built from an unchanged VersionPrefix.

        $tar = Resolve-GnuTar
        if (-not $tar) {
            Write-Warning "No GNU tar found; skipping $rid. The tar in System32 cannot set the executable bit, and a tarball whose DeskPair is not executable fails on first run. Install Git for Windows, or build this one on Linux."
            continue
        }

        $archive = Join-Path $release "$name.tar.gz"

        # GNU tar shells out to gzip, which lives beside it in Git for Windows and is not otherwise on
        # PATH. Without this the only symptom is "gzip: command not found" from inside tar.
        $previousPath = $env:PATH
        try {
            $env:PATH = "$(Split-Path $tar);$env:PATH"
            # --force-local, because GNU tar reads "C:\..." as host:path and tries to resolve a machine
            # called C. Without it the error is "Cannot connect to C: resolve failed", which says nothing
            # about paths.
            # --mode is not optional when the source is NTFS, which has no executable bit for tar to
            # preserve: without it every member comes out 0644 and the extracted DeskPair cannot be run.
            # 0755 for everything is coarse -- the managed .dll files do not need it -- but the alternative
            # is building this archive on Linux, and a slightly over-permissive tarball is a smaller price
            # than a release that cannot be produced on the machine the developer is sitting at.
            & $tar --force-local --sort=name --owner=0 --group=0 --numeric-owner --mode='0755' `
                -czf $archive -C $stageRoot $name
            if ($LASTEXITCODE -ne 0) { throw "tar failed for $rid." }
        }
        finally {
            $env:PATH = $previousPath
        }

        # Verify rather than trust: the whole reason for finding GNU tar is this bit.
        $previousPath = $env:PATH
        try {
            $env:PATH = "$(Split-Path $tar);$env:PATH"
            $listing = & $tar --force-local -tvzf $archive
        }
        finally {
            $env:PATH = $previousPath
        }
        $entry = $listing | Where-Object { $_ -match "/DeskPair$" } | Select-Object -First 1
        if (-not $entry -or -not $entry.StartsWith("-rwx")) {
            throw "DeskPair is not executable inside $archive (listing said '$entry')."
        }

        # The archive first: apps from before system packages existed take the first linux file for their
        # architecture and can only install an archive.
        Add-Artifact -Path $archive -Platform "linux" -Arch ($rid -replace '^linux-', '') `
            -Requires "glibc 2.31" -Signed $false -Format "tar.gz"
        Add-LinuxPackages -Rid $rid -Stage $stage
    }
    else {
        Write-Warning "Nothing known about how to package $rid; skipped."
    }
}

# ---- macOS, built elsewhere ------------------------------------------------------------------------
foreach ($arch in @("arm64", "x86_64")) {
    $dmg = Join-Path $repo "artifacts\macos\$arch\DeskPair-$AppVersion-$arch.dmg"
    if (Test-Path $dmg) {
        Write-Host "==> macOS $arch"
        Copy-Item $dmg $release -Force
        # The file keeps Apple's name for the architecture; the manifest uses the one the app reports (x64).
        $manifestArch = if ($arch -eq "x86_64") { "x64" } else { $arch }
        Add-Artifact -Path (Join-Path $release (Split-Path $dmg -Leaf)) -Platform "macos" -Arch $manifestArch `
            -Requires "macOS 13" -Signed $true -Format "dmg"
    }
}
if (-not ($artifacts | Where-Object { $_.platform -eq "macos" })) {
    Write-Warning "No macOS DMG for $AppVersion. Run build/macos/package.sh on the Mac and copy the result into artifacts/macos/<arch>/, then run this again. The release will go out without it."
}

# ---- release.json ----------------------------------------------------------------------------------
# Release notes are written, not derived.
#
# This used to scrape the bullet lines out of the top of CHANGELOG.md, and the result was what that
# deserves: half sentences, because the bullets wrap; markdown markers, because nothing strips them; and
# mojibake, because Get-Content on PowerShell 5.1 reads UTF-8 as ANSI unless told otherwise. CHANGELOG.md
# is for developers and says things like "RelaySelector returned the first configured entry
# unconditionally". That does not belong on a download page.
#
# -NotesFile takes a plain text file, one note per line. Without it the release goes out with none and the
# script says so, which beats publishing nonsense.
# [string[]] is not decoration. Get-Content hands back strings wearing PSPath, PSDrive and the whole
# filesystem provider as note properties, and ConvertTo-Json serialises all of it -- so each release note
# came out as an object describing where the file had been read from.
[string[]]$notes = @()
if ($NotesFile) {
    [string[]]$notes = @(Get-Content $NotesFile -Encoding utf8 | Where-Object { $_.Trim() })
}
# The Chinese half. It used to be left empty with a message telling you to edit release.json by hand, which
# is a step that gets skipped on the release where it matters. The page falls back to the other language
# when one is missing, so an untranslated note is still news -- but not translating it is now a choice.
[string[]]$notesZh = @()
if ($NotesFileZh) {
    [string[]]$notesZh = @(Get-Content $NotesFileZh -Encoding utf8 | Where-Object { $_.Trim() })
}

# Every language, keyed the way the website keys its pages (Download.cshtml.cs asks for the reader's
# culture and falls back to English). -NotesFile/-NotesFileZh fill en and zh when there is no folder.
$noteLanguages = @("en", "zh", "zh-hans", "ja", "ko", "de", "fr", "es", "pt-br", "ru")
$notesByLanguage = [ordered]@{}
if ($NotesDir) {
    if (-not (Test-Path $NotesDir)) { throw "No notes folder at $NotesDir." }
    foreach ($code in $noteLanguages) {
        $file = Join-Path $NotesDir "notes.$code.txt"
        if (Test-Path $file) {
            [string[]]$lines = @(Get-Content $file -Encoding utf8 | Where-Object { $_.Trim() })
            if ($lines.Count -gt 0) { $notesByLanguage[$code] = $lines }
        }
    }
    if ($notesByLanguage.Contains("en")) { [string[]]$notes = $notesByLanguage["en"] }
    if ($notesByLanguage.Contains("zh")) { [string[]]$notesZh = $notesByLanguage["zh"] }
}
if ($notes.Count -gt 0 -and -not $notesByLanguage.Contains("en")) { $notesByLanguage["en"] = $notes }
if ($notesZh.Count -gt 0 -and -not $notesByLanguage.Contains("zh")) { $notesByLanguage["zh"] = $notesZh }

$document = [ordered]@{
    channel   = $Channel
    version   = $AppVersion
    published = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd")
    commit    = $commit
    files     = $artifacts
    stores    = @()
    history   = @(
        [ordered]@{
            version = $AppVersion
            date    = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd")
            notes   = $notesByLanguage
        }
    )
}

$json = $document | ConvertTo-Json -Depth 6
# PowerShell 5.1's Out-File -Encoding utf8 writes a BOM, and a BOM in front of a JSON body upsets strict
# parsers. The update check reads this file.
[System.IO.File]::WriteAllText((Join-Path $release "release.json"), $json, [System.Text.UTF8Encoding]::new($false))

# The signature is over these exact bytes, so nothing may touch release.json after this point.
if ($SigningKey) {
    if (-not (Test-Path $SigningKey)) { throw "No signing key at $SigningKey." }
    # No --nologo: the SDK on the Mac hands it to KeyGen as its first argument, and KeyGen then takes it for the
    # name of a key file, makes a new key under that name and signs nothing.
    & dotnet run --project (Join-Path $repo "tools\DeskPair.Tools.KeyGen") -c Release -- sign $SigningKey (Join-Path $release "release.json")
    if ($LASTEXITCODE -ne 0) { throw "Signing release.json failed." }
} else {
    Write-Warning "No -SigningKey: release.json is unsigned. Installed apps will announce this release but cannot install it."
}

# One combined file beside the side-cars, so `sha256sum -c SHA256SUMS` checks the lot.
$sums = @(Get-ChildItem $release -Filter *.sha256 | ForEach-Object { Get-Content $_.FullName })
if ($sums) {
    [System.IO.File]::WriteAllText((Join-Path $release "SHA256SUMS"), (($sums -join "`n") + "`n"), [System.Text.Encoding]::ASCII)
}

Remove-Item $stageRoot -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "$release"
Write-Host "  $($artifacts.Count) artifact(s), release.json written."
Write-Host "  Copy the contents to the release folder the update check reads."
if ($notes.Count -eq 0) {
    Write-Host "  No release notes. Write them into a -NotesDir (notes.<code>.txt per language), or add them to release.json by hand." -ForegroundColor Yellow
}
elseif ($notesByLanguage.Count -lt $noteLanguages.Count) {
    $absent = @($noteLanguages | Where-Object { -not $notesByLanguage.Contains($_) })
    Write-Host "  Notes in $($notesByLanguage.Count) of $($noteLanguages.Count) languages; missing $($absent -join ', ') (those readers see English)." -ForegroundColor Yellow
}
else {
    Write-Host "  $($notes.Count) note(s) in English, $($notesZh.Count) in Chinese."
}
