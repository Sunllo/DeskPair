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
