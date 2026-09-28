# Sunllo DeskPair

**English** · [繁體中文](docs/readme/README.zh-Hant.md) · [简体中文](docs/readme/README.zh-Hans.md) ·
[日本語](docs/readme/README.ja.md) · [한국어](docs/readme/README.ko.md) · [Deutsch](docs/readme/README.de.md) ·
[Français](docs/readme/README.fr.md) · [Español](docs/readme/README.es.md) ·
[Português (Brasil)](docs/readme/README.pt-BR.md) · [Русский](docs/readme/README.ru.md)

Cross-platform (Windows / macOS / Linux) remote desktop system written in C# / .NET 10.

- **Desktop** (Avalonia) — controller and host in one application.
- **Phones** (Kotlin Multiplatform, Android and iOS) — the controller on a phone.
- **Rendezvous** — ID registry, presence and NAT hole-punch signaling.
- **Relay** — TCP relay for peers that cannot connect directly.

Accounts are optional: they link a computer or a phone to a person and keep the saved-computer list in step
across them. They are a service run at `deskpair.app`, which the apps reach over HTTPS (`/api/v1`); its code is
not published.

The architecture borrows from [RustDesk](https://github.com/rustdesk/rustdesk) but uses its own protocol
(protobuf messages, end-to-end AES-256-GCM, ECDSA P-256 identities). See `docs/architecture.md`; the documents
under `docs/` are written in Traditional Chinese.

## Screenshots

The desktop app on Windows. The pictures are drawn from the app's own windows by `tools/DeskPair.Tools.Screenshots`,
and the computers, people and addresses in them are made up.

<table>
  <tr>
    <td width="50%" valign="top">
      <img src="docs/images/screenshots/remote-session.png" width="100%" alt="A remote session"><br>
      Controlling another computer; each one you are connected to has its own tab.
    </td>
    <td width="50%" valign="top">
      <img src="docs/images/screenshots/file-transfer.png" width="100%" alt="File transfer"><br>
      File transfer: this computer on the left, the other one on the right.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="docs/images/screenshots/home.png" width="100%" alt="The main window"><br>
      Your ID and one-time password, and the computers you reached recently.
    </td>
    <td width="50%" valign="top">
      <img src="docs/images/screenshots/devices.png" width="100%" alt="Devices"><br>
      Saved devices in groups, and which of them are online.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="docs/images/screenshots/terminal.png" width="100%" alt="A remote terminal"><br>
      A shell on another computer, running as the account named at the top.
    </td>
    <td width="50%" valign="top">
      <img src="docs/images/screenshots/incoming-request.png" width="100%" alt="A request to connect"><br>
      Someone asks to connect: the person at this computer accepts or rejects, and picks what to allow.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="docs/images/screenshots/history.png" width="100%" alt="Connection history"><br>
      Who has connected to this computer, and what they were allowed to do.
    </td>
    <td width="50%" valign="top">
      <img src="docs/images/screenshots/settings-security.png" width="100%" alt="Security settings"><br>
      Security settings: who may connect, and how they prove it.
    </td>
  </tr>
</table>

## Download

Every release is on the [releases page](https://github.com/Sunllo/DeskPair/releases/latest) and at
[deskpair.app/download](https://deskpair.app/download), with a SHA-256 for each file (`SHA256SUMS`).

| System | Files |
|---|---|
| Windows 10 1809 or later | x64, ARM64 and x86 (32-bit): an installer (`.msi`) and a portable `.zip` for each, named like `DeskPair-<version>-win-x64.msi`. Code-signed; SmartScreen may still ask before the first run while a new release builds up its reputation. |
| macOS 13 or later | `DeskPair-<version>-arm64.dmg` (Apple silicon), `-x86_64.dmg` (Intel). Signed and notarised. |
| Linux, glibc 2.31 or later | x64, ARM64 and 32-bit ARM (ARMv7): a `.deb`, an `.rpm`, an Arch package and a `.tar.gz` for each. |

On Windows, the installer puts DeskPair in Program Files, on the Start menu and in Settings › Apps, where it can be
removed again, and the app installs later versions itself once an administrator approves. The `.zip` runs from
wherever it is unpacked and updates itself. To install without any questions, on many computers at once:

```
msiexec /i DeskPair-<version>-win-x64.msi /qn
```

On Linux, install the package for your distribution with its own tools (the x64 names are shown):

```
sudo apt install ./deskpair_<version>_amd64.deb                              # Debian, Ubuntu, Mint, Raspberry Pi OS
sudo dnf install ./deskpair-<version>-1.x86_64.rpm                            # Fedora, RHEL
sudo zypper install --allow-unsigned-rpm ./deskpair-<version>-1.x86_64.rpm    # openSUSE
sudo pacman -U deskpair-<version>-1-x86_64.pkg.tar.zst                        # Arch, Manjaro
```

A package puts DeskPair in `/usr/lib/deskpair`, with `deskpair` on the path and an entry in the applications menu.
When a new version comes out the app says so, and the new package is installed the same way. The `.tar.gz` runs from
wherever it is unpacked, on any distribution, and updates itself, as the Windows and macOS builds do.

The phone apps, which control a computer, are not in the stores yet.

## Removing DeskPair

Removing DeskPair takes away the program and what it changed in the system, and keeps what makes this computer
itself: its ID and key, its passwords, your settings and device list, the connection history and the logs. Installed
again, DeskPair comes back as the same computer, reachable with the same permanent password. To start afresh
instead, delete those as well with the commands below, and the next installation gets a new ID. A computer linked to
a DeskPair account stays in the account's device list until it is removed there.

On Windows, uninstall DeskPair from Settings › Apps. That removes the program, its shortcuts, the unattended
service, its firewall rules -- including the ones Windows made when DeskPair was allowed through its prompt -- the
entries that start it at sign-in and the virtual display driver. A copy run from the `.zip` has no uninstaller: in a
terminal opened as administrator, run `DeskPair.exe --remove-system-changes` from its folder, which removes the same
things, then delete the folder. The data goes with two lines of PowerShell, the first run by each person who used
DeskPair and the second once as an administrator:

```
Remove-Item -Recurse -Force -ErrorAction Ignore "$env:APPDATA\Sunllo\DeskPair", "$env:LOCALAPPDATA\Sunllo\DeskPair", "$env:TEMP\DeskPair", "$env:TEMP\deskpair-update*"
Remove-Item -Recurse -Force -ErrorAction Ignore "$env:ProgramData\Sunllo\DeskPair"
```

On macOS, first turn off Settings › Security › Reachable while locked and Settings › General › Start when I sign
in, then move DeskPair from Applications to the Trash. The data, the passwords it kept in the keychain and the
permissions it was given for screen recording and accessibility go with:

```
rm -rf ~/Library/Application\ Support/Sunllo/DeskPair ~/Library/LaunchAgents/com.sunllo.deskpair.login.plist
sudo rm -rf "/Library/Application Support/Sunllo/DeskPair" /Library/LaunchAgents/com.sunllo.deskpair.agent.plist
while security delete-generic-password -s "Sunllo DeskPair" >/dev/null 2>&1; do :; done
tccutil reset ScreenCapture com.sunllo.deskpair
tccutil reset Accessibility com.sunllo.deskpair
```

On Linux, remove the package with the tool that installed it; the unattended service goes with it:

```
sudo apt remove deskpair       # Debian, Ubuntu, Mint, Raspberry Pi OS
sudo dnf remove deskpair       # Fedora, RHEL
sudo zypper remove deskpair    # openSUSE
sudo pacman -R deskpair        # Arch, Manjaro
```

A copy run from the `.tar.gz` has no package: turn off Reachable while locked and Start when I sign in first, or
run `sudo /opt/deskpair/DeskPair --uninstall-service`, then delete the file. The data, including what the unattended
service kept and the `deskpair` account it ran as:

```
rm -rf ~/.local/share/deskpair ~/.local/share/Sunllo/DeskPair ~/.config/Sunllo/DeskPair ~/.config/autostart/deskpair.desktop ~/.cache/DeskPair ~/.net/DeskPair
sudo rm -rf /var/lib/deskpair /etc/deskpair /root/.net/DeskPair
sudo userdel deskpair
```

## Build

```
dotnet build DeskPair.slnx
dotnet test DeskPair.slnx
```

Requires the .NET 10 SDK (`global.json`). Video uses the OS encoder (Media Foundation on Windows). No software
H.264 codec ships with this repository — H.264 is patented separately from OpenH264's BSD-2 source — but one can
be supplied; see `native/openh264/README.md`.

## Host and desktop app

DeskPair is one executable. Opening it shows your ID and one-time password, lets you connect to other desks,
and runs the host engine in the same process -- so opening the app is what makes this computer controllable,
and closing it is what stops that.

```
# the app
dotnet run --project src/DeskPair.Desktop

# the engine on its own, no window: for a machine with no desktop session
dotnet run --project src/DeskPair.Desktop -- --server
```

**Unattended access** -- reachable while the computer is locked or nobody is signed in -- is a role you install
from Settings › Security › Reachable while locked: a service on Windows, a launchd agent on macOS and a root daemon
on Linux. See `docs/unattended-windows.md` and `docs/unattended-linux.md`. Without it, turn on Settings › General ›
**Start when I sign in** to be reachable after a restart.

On macOS, run the app from its `.app` bundle: screen-recording and accessibility consent is granted to the
bundle, and a binary started from a terminal takes the terminal's consent instead. That makes `--server` a
testing role there.

To use your own servers, set the rendezvous server and its public key in Settings (or `config.json`); direct
connections to `host:21118` need no server.

## Quick start (headless CLI)

```
# 1. servers (defaults: rendezvous udp+tcp/21116, nat-test 21115, http 21114; relay tcp/21117)
dotnet run --project src/DeskPair.Rendezvous
dotnet run --project src/DeskPair.Relay -- --Relay:HttpPort=21124

# 2. read the server key the clients must trust
curl http://127.0.0.1:21114/key

# 3. host (prints its 9-digit ID and temporary password)
dotnet run --project tools/DeskPair.Tools.PeerCli -- host --server 127.0.0.1 --key <KEY> --password secret

# 4. controller, from another terminal; typed lines are sent as chat
dotnet run --project tools/DeskPair.Tools.PeerCli -- connect <ID> --server 127.0.0.1 --key <KEY> --password secret
```

Connections by id try the LAN address (same public IP), then a TCP hole punch, then the relay;
`--force-relay` (PeerCli) / `ForceRelay` skips the direct paths. See `docs/nat-test-matrix.md`.

Docker: `docker compose -f deploy/docker-compose.yml up --build` (host networking; see the file for notes).
Running the servers for real: `deploy/README.md`.

## Licence

DeskPair is free software under the [GNU Affero General Public License, version 3](LICENSE)
(`AGPL-3.0-only`). That covers the applications and the servers in this repository alike. The part that matters
if you run the servers for other people: section 13 asks you to offer those people the source of what you are
actually running, changes included.

Third-party components keep their own licences; the application lists them under Settings › About DeskPair.
