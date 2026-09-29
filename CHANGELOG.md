# Changelog

Versions are `MAJOR.MINOR.PATCH`, set once in `Directory.Build.props` (`<VersionPrefix>`) and repeated in
`src/DeskPair.Desktop/app.manifest` because a Win32 manifest cannot read an MSBuild property. A test
(`VersionTests`) fails when the two drift. Every build also carries the commit it came from, so what the app
reports looks like `0.2.0+8ec4feb`.

Tag each release `vMAJOR.MINOR.PATCH` on the commit that ships.

## Servers, unreleased

The servers only; no app changes.

### Added

- The rendezvous reports its counts once a minute when `Rendezvous:Report:Url` is set. Counters go as running totals
  with the moment the process started, so a lost report loses nothing and a restart is never read as a negative
  minute; the relays' own totals ride along on the health poll the rendezvous already makes.

## 0.4.8 — 2026-09-29

Desktop only; the phones stay at 0.4.0.

### Fixed

- A remote session's toolbar, in a window too narrow for all of its buttons, scrolled with a scrollbar laid over the
  lower half of the buttons, and a click there went to the scrollbar. The scrollbar now has a row of its own below
  them, shown only when the toolbar does not fit; the tabs of the session window likewise. The English toolbar, the
  widest, met it first.
- On Windows, DeskPair now and then took its own write to the clipboard for somebody else's copy, and a host could
  send a viewer's clipboard straight back to them.
- Linux, unattended access: when the desktop's question whether to share the screen was called off -- the remembered
  permission no longer held, or the last viewer left while the person at the computer was being asked -- the question
  stayed on their screen. It is taken down now, as it was meant to be.
- On macOS and Linux, starting DeskPair just after another start had been handed to the open window -- a link opened
  twice in quick succession, say -- could open a second DeskPair instead of handing over to the first.
- A device added to the list on one computer did not show on another computer signed in to the same account. The other
  computer asked the account only every fifteen minutes -- not when somebody signed in, not when the list was opened --
  and what it fetched went into its file but not onto a list already on screen. Signing in and opening the device list
  now fetch the account's list, an open list asks again every minute and shows what arrives, and the sync starts even
  if this computer had no identity yet when DeskPair opened. A window closed to the tray no longer keeps asking, every
  fifteen seconds, who on its device list is online.

## 0.4.7 — 2026-09-29

Desktop only; the phones stay at 0.4.0.

### Added

- **The Windows installer speaks the language Windows is shown in**: the ten the app does, English for any other. One
  `.msi` still: the other nine languages are transforms of the English package, carried inside it, and Windows Installer
  applies the one for the display language by itself. WiX brings the wizard's own words; ours ("Open DeskPair", the
  refusal to replace a newer version) are in `packaging/windows/Localization`.

## 0.4.6 — 2026-09-29

Desktop only; the phones stay at 0.4.0.

### Fixed

- With unattended access installed on Windows, the home page said "Host service not running" for a moment every
  twelve seconds while the service ran fine, when DeskPair was open in a session that is not the console's -- the
  owner of a computer working on it over remote desktop, for one. The service's engine gives the passwords and the
  settings only to the machine's owner, and it counted an administrator only while elevated, which under UAC an
  administrator's programs are not; and it refused by not answering, which the app took for a lost engine and
  reconnected. An administrator is an owner elevated or not, since a consent prompt is all that separates the two,
  and a refusal is now said: the app stays connected, shows this computer's id, and says that its password,
  settings and connection history are only for someone signed in at its own screen or an administrator.
- The Windows `.zip` and installer, and the Linux `.tar.gz`, no longer put seven `.pdb` files beside the program.
  The symbols are inside it instead, so a crash log still names the file and line. An upgrade by the installer takes
  the old ones away; a `.zip` copy updated in place keeps them, unused.
- Removing DeskPair on Windows also takes away the firewall rules Windows made itself when somebody allowed DeskPair
  through its prompt -- inbound, on private and public networks -- and the entries that started it at sign-in, for
  everybody signed in. Both were left behind.
- The Windows permission prompt calls the installer "DeskPair". It showed the file's name, which when DeskPair is
  removed is the random one Windows keeps its copy of the installer under (`1a2b3c4d.msi`).

### Documentation

- The README, in all ten languages, says what removing DeskPair leaves on each system -- this computer's id and
  passwords, settings, the device list and the logs, kept so that installing it again gives back the same computer
  -- and the commands that remove those as well, with the keychain items and privacy permissions on macOS and the
  service account on Linux.

## 0.4.5 — 2026-09-28

Desktop only; the phones stay at 0.4.0. The program is 0.4.4's: this release exists to be signed.

### Added

- **Signed Windows builds.** `DeskPair.exe`, the libraries in its folder that nobody else signed (`vpx.dll` and
  Avalonia's ANGLE library) and the three installers carry an Authenticode signature from Microsoft's Artifact
  Signing, timestamped, so Windows names the publisher -- Syno Compute X LLC -- instead of "unknown". Skia's
  libraries keep Microsoft's signature and the virtual display driver its own. `tools/package.ps1 -CodeSigning`
  signs; without it a build goes out unsigned as before. SmartScreen can still ask about a new release until it
  has been downloaded often enough to be known.

### Fixed

- `SHA256SUMS` is written with LF line endings, so `sha256sum -c SHA256SUMS` checks every file; 0.4.3's failed every
  line. (0.4.4's was corrected by hand before it went out.)

## 0.4.4 — 2026-09-28

Desktop only; the phones stay at 0.4.0.

### Added

- **A Windows installer.** An `.msi` for x64, ARM64 and x86 beside each `.zip`. It puts DeskPair in
  `Program Files\Sunllo\DeskPair`, on the Start menu, on the desktop and in Settings › Apps; it stops and restarts the
  unattended service around an upgrade; and removing it takes away what the app set up outside its folder -- the
  service, the firewall rules and the virtual display driver (`DeskPair --remove-system-changes`, which the
  installer runs). A copy it installed updates from the next installer (`msiexec /passive`, which asks for an
  administrator's consent) rather than by copying files over ones Windows Installer owns; a copy run from the `.zip`
  goes on updating from the `.zip`, which the manifest lists first so that apps from before installers keep taking
  it. `msiexec /i DeskPair-<version>-win-x64.msi /qn` installs without a question, and `DESKTOP_SHORTCUT=0` leaves the
  desktop shortcut off. A DeskPair that is open when an upgrade starts is closed by the installer and comes back in
  the notification area when it is done, so a computer upgraded remotely stays reachable. Built by `tools/package.ps1`
  from `packaging/windows/`, with WiX v7.

### Fixed

- The connection manager's card wraps the line that says what somebody wants ("… wants to control this computer")
  instead of cutting it off after their name.
- When Windows signs out or shuts down, or an installer needs DeskPair's files, DeskPair quits. It used to hide in the
  notification area and stay running from files the installer then had to leave for the next restart; and quitting
  that way disposed the engine twice, which ended on a crash dialog.

### Known limits

- The installer's own dialogs are in English; the app follows the system's language as before.
- Neither the installer nor the program is code-signed yet, so SmartScreen asks before the first run of either
  (signed from 0.4.5).

## 0.4.3 — 2026-09-28

Desktop only; the phones stay at 0.4.0.

### Added

- **More machines.** Windows now comes for 32-bit x86 and for ARM64 as well as x64; Linux for ARM64 and for 32-bit
  ARM (ARMv7 -- a Raspberry Pi on 32-bit Raspberry Pi OS, among others) as well as x64; and macOS for Intel as well
  as Apple silicon.
- **Linux packages**: `.deb` (Debian, Ubuntu, Mint, Raspberry Pi OS), `.rpm` (Fedora, RHEL, openSUSE) and Arch
  Linux's `.pkg.tar.zst` for each Linux architecture, beside the `.tar.gz` that runs on any of them. One description,
  `packaging/linux/nfpm.yaml`, is built three ways by `tools/package.ps1` with nfpm. They install to
  `/usr/lib/deskpair` with `deskpair` on the path, a menu entry and icons, and declare what the program loads --
  ICU included, without which the .NET runtime stops before the program starts. With unattended access on, a
  package upgrade hands the service the new version and removing the package removes the service. The app does not
  update a packaged install itself: it says a new version is out, and the package manager installs it. Checked on
  Debian 12, Ubuntu 22.04 and 24.04, Fedora 44, Rocky 9, openSUSE Tumbleweed and Leap 15.6 and Arch: install, start,
  upgrade, remove.

### Fixed

These are all in code that only the new builds run.

- **32-bit native bindings.** Every one that passed a C `long` as 64 bits -- Xlib, XTest, XFixes, XShm, Xrandr,
  `mmap`'s offset, uinput's event times, `struct passwd` -- now uses the pointer-sized type, which is what a C `long`
  is on Linux, and XEvent members and 32-bit X properties are read and written at C-long offsets (`XEventBytes`).
  libvpx's configuration, packet and image structs derive their offsets from the pointer and `long` sizes, and
  libvpx and OpenH264 are called cdecl, which 32-bit Windows needs.
- The unattended daemon's framebuffer check reads the inode through `statx`, with `fstat` fallbacks per
  architecture; on ARM64 with a glibc older than 2.33 it read the wrong field.
- An update keeps the program's own architecture rather than the operating system's: 32-bit Raspberry Pi OS runs a
  64-bit kernel on a Pi 4 or 5, where the ARM64 build cannot start.
- libvpx 1.17 (soname 12, Arch's current one) is looked for by name.

### Known limits

- Windows x86 cannot add displays: the virtual display driver has no 32-bit build. Its VP9 encoder and decoder and
  its codec discovery were run in a 32-bit process; a whole session on 32-bit Windows has not been.
- The Windows ARM64 build has not yet run on ARM hardware.
- The Linux ARM builds were tested under emulation (Docker with QEMU) against a real X server: capture, keyboard and
  mouse, the clipboard both ways, VP9, a terminal, and the packages. Not yet on a Raspberry Pi, and the unattended
  daemon's DRM and uinput paths not on ARM at all.
- The Intel Mac build was run under Rosetta on an Apple silicon Mac -- it starts, captures and sends a session its
  pictures -- but not yet on an Intel Mac.

## 0.4.2 — 2026-09-27

Desktop only; the phones stay at 0.4.0.

- **The source is public**: the applications and the servers are at github.com/Sunllo/DeskPair, under the
  AGPL-3.0. Nothing else changed since 0.4.1; this build is made from that repository, so the commit it reports
  with `--version` can be looked up there.

## 0.4.1 — 2026-09-27

Desktop only; the phones stay at 0.4.0.

### Added

- **A frame rate on the session toolbar**: automatic, or 15 to 120 frames a second, with any quality, sent to the
  host the moment it changes. It is a ceiling -- on a slow link the host still sends fewer -- and it no longer comes
  with the custom quality's fixed bitrate: before, a frame rate could only be had through that quality, and only
  from the settings page before connecting.

### Fixed

- The toolbar's quality list had no "Custom", so a session started with the custom quality from the settings page
  showed an empty quality box.

## 0.4.0 — 2026-09-27

### Added

- **A terminal on the remote computer**, in a window of its own on the desktop and on both phones. Off until the
  host turns it on, and a connection gets one only when the person accepting it ticks that box as well. The shell
  runs as the highest identity the host allows -- SYSTEM under the Windows service, root under the Linux daemon
  unless `/etc/deskpair/daemon.conf` says otherwise, the signed-in user on macOS -- and every shell opened is in the
  connection history. See `docs/terminal.md`.
- **Several monitors at once**, each remote monitor in a window of its own. The host encodes each one separately
  and shares the connection's bandwidth between them.
- **A monitor added to the remote computer** (Windows, with unattended access installed): a virtual display driver
  (VDD, signed by the SignPath Foundation) is installed on request, and a monitor of the viewer's size is plugged in
  from the toolbar and taken out when the last viewer leaves. On Linux under X any resolution can be taught to a
  real output.
- **The remote computer's resolution**, changed from the toolbar and from the phones' screen panel, and put back
  when the viewer leaves.
- **Allowlists**: only listed addresses, networks or IDs may connect, and relayed connections can be refused.
- **Connection history**: who connected, when, how they were let in, what they could do and for how long, on a page
  of its own, and optionally kept with the account.
- **Wayland**: GNOME and KDE on Wayland are shared through the desktop portal -- every monitor, the pointer, the
  keyboard and the mouse. With unattended access installed, the lock screen and the login screen are shown through
  the display hardware and the unlocked desktop through the portal, in one connection. Sharing is allowed once, in
  Settings › Security.
- **VP9 on the phones**, so a host with no H.264 encoder can serve them (on an iPhone, iOS 26.2 or later). The phones
  also show what the host says about its screen: asking, declined, locked.

### Fixed

- A second monitor stopped after a few frames over UDP: the viewer acknowledged every stream as monitor 0.
- On Windows a hardware encoder's late keyframe could hold a still screen black until something moved.
- Displays were numbered differently from the order they were listed in when the primary was not the first
  (X11, macOS).

## Unreleased

Due out as 0.2.0: the first version that is a version.

### Added

- **A licence: AGPL-3.0-only.** The repository had none, which meant nobody but us could lawfully run a line
  of it. AGPL rather than GPL because half the product is servers, and GPL lets somebody run a changed
  server for other people without ever showing the change.

- **Relays are chosen, not assumed.** The rendezvous polls each configured relay's `/api/stats` and picks by
  region first and load second, offering the rest as fallbacks for the client to try. Before this
  `RelaySelector` returned the first configured entry unconditionally: a second relay was never used, one
  that had been switched off went on being handed out, and a busy one went on taking everything.
- Relays report live throughput, their region and a `MaxBitrateKbps` ceiling. A relay at its ceiling is
  refused rather than accepted into saturation, because saturation collapses the picture quality of every
  session already running and is invisible from a session count.

- **Device linking.** A signed-in account is shown an eight-character code; typing it into DeskPair on
  another machine links that installation. The app proves itself by signing the code with the ECDSA P-256
  identity key it has always generated on first run, so no account password is ever typed into or stored on
  a device — only a token, revocable one device at a time from the web console. `DeviceLink` fixes the
  signed bytes because the portal, the .NET client and the Kotlin one must agree on them exactly; both
  languages pin the same vector, and a Kotlin-made signature was checked against the .NET verifier by hand.
- A Settings → Account tab on the desktop, an Account section on both phone apps, and a Your devices page
  in the console. The phone had no private key of its own until now — it only ever verified a host's — so
  `DeviceIdentity` generates one on first use and keeps it in the Keychain or EncryptedSharedPreferences.
- **The saved-computer list follows the account**, on the desktop and on both phones. Link them and the same
  list is everywhere: add, rename, move between folders, delete. It syncs at start-up, a few seconds after
  any local edit, and every fifteen minutes. Which groups are folded away stays on the machine you folded
  them on — that is how the screen looks to you, not what the list contains.
- The phone apps gain folders. `Favourite` had no group field at all, so a list arriving from a desktop
  would have been flattened and pushed back flat; the favourites tab now shows a section per folder.

### Removed

- **`LicenceKey`, everywhere.** It was never a licence. The relay and the rendezvous compared it with
  `string.Equals` against a static value in their own config, and treated an empty value as "off" — which
  every shipped config, example and compose file was. What it actually was is a shared secret for relay
  admission; what it looked like, across a desktop settings field, two mobile settings screens, four string
  tables, `HostConfig`, `PeerSettings`, `DesktopConfig`, the proto and PeerCli, was a commercial licence.
  Keeping that in place next to real accounts would have made the two look like the same thing.

  The proto field numbers (`PunchHoleRequest.6`, `RequestRelay.6`) and the `LICENCE_MISMATCH = 3` enum value
  are `reserved`, not reused. Restricting who may use a relay comes back later, tied to an account.

### Changed

- **The version means something.** `0.1.0` had been a literal in `Directory.Build.props` since the first
  commit, copied by hand into `app.manifest`, never bumped and never tagged. `App.Version` read
  `AssemblyVersion`, which has four numbers and no room for a commit, so two builds of different commits
  answered identically and nothing could tell "you are up to date" from "the check never worked". The version
  now comes from `<VersionPrefix>` plus the commit read out of `.git`, and `--version` prints both.
- `tools/publish.ps1`: `-Version` was a folder-name suffix that was never passed to MSBuild. It is now
  `-Build` (with `-Version` kept as an alias), and a real `-AppVersion` sets the version the build reports.
  The script prints what the published binary answers to `--version`, rather than what it was asked for.
