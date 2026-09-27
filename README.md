# Sunllo DeskPair

Cross-platform (Windows / macOS / Linux) remote desktop system written in C# / .NET 10.

- **Desktop** (Avalonia) — controller and host in one application.
- **Phones** (Kotlin Multiplatform, Android and iOS) — the controller on a phone.
- **Rendezvous** — ID registry, presence and NAT hole-punch signaling.
- **Relay** — TCP relay for peers that cannot connect directly.

Accounts are optional: they link a computer or a phone to a person and keep the saved-computer list in step
across them. They are a service run at `deskpair.app`, which the apps reach over HTTPS (`/api/v1`); its code is
not published.

The architecture borrows from [RustDesk](https://github.com/rustdesk/rustdesk) but uses its own protocol
(protobuf messages, end-to-end AES-256-GCM, ECDSA P-256 identities). See `docs/architecture.md`.

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

**Unattended access** -- reachable while the computer is locked or nobody is signed in -- is a role you install:
a service on Windows, a launchd agent on macOS and a root daemon on Linux. See `docs/unattended-windows.md` and
`docs/unattended-linux.md`. Without it, turn on "start at sign-in" in Settings to be reachable after a restart.

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

Third-party components keep their own licences; the application lists them under Settings -> About.
