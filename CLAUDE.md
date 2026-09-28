# DeskPair

Cross-platform remote desktop system in C#/.NET 10, modeled on RustDesk (`reference/rustdesk-master`, read-only,
not in git). Not wire-compatible with RustDesk. Full design: `docs/architecture.md`.

## Layout

- `protos/sunllo/` — `rendezvous.proto` (signaling), `message.proto` (peer session), `ipc.proto` (local service<->UI).
- `src/DeskPair.Protocol` — generated protobuf, `FramedStream` (4-byte LE header, AES-GCM), `Handshake`, `PasswordProof`, `IdentityKey`.
- `src/DeskPair.Platform.Abstractions` — the only contract platform projects implement (capture, codec, input, cursor, audio, clipboard, hosting, secrets).
- `src/DeskPair.Core` — session state machines, publisher services, QoS, file transfer, rendezvous/relay clients, NAT, IPC.
- `src/DeskPair.Platform.{Windows,MacOS,Linux}` — native implementations; reference `Platform.Abstractions` only.
- `src/DeskPair.Desktop` — the whole product as one executable (`DeskPair`): Avalonia UI, tray,
  connection-manager card and the host engine (`Engine/ServerRole.cs`), all in one process. Roles: none = the
  app, `--server` = engine only (no window), `--allow-firewall`/`--remove-firewall`, `--version`.
  Unattended access (reachable while locked or signed out) is an installed role on all three systems:
  Windows a service (`Engine/Service`), macOS a launchd agent (`Engine/MacService`), Linux a root daemon
  (`Engine/LinuxService`, `--service`) that reads the scanout through DRM/KMS and injects through uinput, with
  the engine as the fixed `deskpair` account. See `docs/unattended-windows.md` and `docs/unattended-linux.md`.
- `src/DeskPair.Rendezvous`, `src/DeskPair.Relay` — servers; reference `Protocol` + `Server.Shared`, never `Core`.
- `tools/` — `PeerCli` (headless peer for E2E), `KeyGen`, `PlatformHarness`, `Screenshots` (the README's pictures).
  `tests/` — xunit + Shouldly + NSubstitute.
- `packaging/linux/` — the .deb/.rpm/Arch package description (`nfpm.yaml`) and its install/remove scripts;
  `tools/package.ps1` builds all three per Linux RID with nfpm (`tools/fetch-nfpm.ps1`).
- `packaging/windows/` — the MSI (WiX v7, `DeskPair.wixproj` + `Package.wxs`), one per Windows RID beside the zip,
  built by `tools/package.ps1`. Windows only, so not in `DeskPair.slnx`. The manifest lists each zip before its MSI:
  apps from before installers install the first file for their machine. An installed copy (`packaged` = `msi`)
  updates by running the next MSI; its uninstall runs `DeskPair --remove-system-changes`, which must never show a window.
  Windows builds are Authenticode-signed through Microsoft Artifact Signing when `package.ps1` gets `-CodeSigning <metadata>`
  (SignTool + `tools/fetch-artifact-signing.ps1`'s plug-in, signed in with `az login`); the account's settings are not public.

## Conventions

- .NET 10, `Directory.Build.props` owns TFM/analyzers (`TreatWarningsAsErrors`), `Directory.Packages.props` owns every package version (central package management; `dotnet add package` does not update it — edit the props file).
- Solution file is `DeskPair.slnx`. Build: `dotnet build DeskPair.slnx`; test: `dotnet test DeskPair.slnx`.
- Code, comments and commit messages in English. UI strings go through resources (zh-TW + en).
- README.md (English) is the source of nine translations in `docs/readme/`; change them together.
  `ReadmeTranslationTests` fails when a translation's commands, sections or language links fall behind.
- Time-dependent logic takes a `TimeProvider`; never call `DateTime.UtcNow` directly in Core/Protocol.
- Crypto is BCL only (ECDSA/ECDH P-256, HKDF, AES-256-GCM). Do not add native crypto packages.
- Protocol changes: add fields, never renumber; bump `ProtocolConstants.ProtocolVersion` for incompatible changes.
- The video codec is negotiated, not configured: `CodecNegotiation.Choose` intersects what the host can encode
  with what every subscriber of that stream says it can decode. `HostConfig.CodecPreference` (`auto`/`h264`/
  `h265`/`av1`) is a preference, not an instruction. A stream has one encoder, so a viewer that joins and
  cannot decode it restarts the stream for everyone.
- Pooled buffers (`Frame`) must be disposed after parsing; never keep a `ByteString` that wraps pooled memory.
- Releases cover win-x64/x86/arm64, linux-x64/arm64/arm (ARMv7) and macOS arm64/x86_64, so native bindings must
  hold on 32-bit: a C `long`/`unsigned long` on Linux is `nint`/`nuint` (it is 4 bytes on Windows, hence
  `CLong` in bindings shared with Windows); XEvent members and format-32 property data are C longs
  (`XEventBytes`); struct offsets that pass a pointer or a long are derived, not written as 64-bit numbers
  (`VpxInterop.Abi`); and imports of cdecl C libraries (libvpx, OpenH264) say `CallConvCdecl`, which win-x86 needs.
- Servers never reference `Core`; platform projects never reference `Core` or `Protocol`.
- `net10.0-windows` projects (Platform.Windows, its tests, PlatformHarness) compile on every OS via `EnableWindowsTargeting`
  but only run on Windows; `Service`/`Desktop` add the `net10.0-windows` target only when built on Windows (`IsWindowsBuildHost`).
- Windows platform code uses hand-written `LibraryImport` P/Invoke (`Native/*.cs`) and Vortice for COM (D3D11/DXGI/MF);
  NAudio 3.x (builder API: `WasapiRecorderBuilder`/`WasapiPlayerBuilder`). Media Foundation encoders are enumerated with
  `MFTEnumEx` per codec (H.264/H.265/AV1) and may be asynchronous (hardware) — see `MfVideoEncoder`.
- Tests that need the interactive desktop (clipboard, input, capture) must skip when `InteractiveDesktop.IsAvailable` is false;
  a disconnected RDP session or CI runner has no interactive window station.
- `src/DeskPair.Codec.Vpx` is the royalty-free software codec: VP9 through libvpx (BSD-3, patents
  granted). Unlike OpenH264 a built binary **may** be shipped; none is committed yet. The loader honours
  `SUNLLO_LIBVPX_PATH` and `runtimes/<rid>/native/`, and both factories report themselves unavailable when
  there is nothing to load. The binding reaches into libvpx structs by offset, so `VpxLayoutTests` derives
  every offset from the C declarations and the encoder checks libvpx's own defaults before writing a field.
  See `native/libvpx/README.md`.
- `src/DeskPair.Codec.OpenH264` is the software H.264 fallback (C++ vtable calls through
  `delegate* unmanaged`; struct layouts asserted in tests). **No built binary ships with this repository or the
  product**: H.264 is patented separately from the BSD-2 source and Cisco's royalty coverage follows only the
  binaries Cisco itself distributes. The loader honours `SUNLLO_OPENH264_PATH` and `runtimes/<rid>/native/`, so
  anyone with their own AVC licence can supply one; without it the factory probes as unavailable and
  `FallbackVideoEncoderFactory`/`FallbackVideoDecoderFactory` fall through to the platform codec. Never add a
  `.gitignore` exception for `native/openh264/**/*.dll`. See `native/openh264/README.md`.
- Desktop (Avalonia 11.3 + CommunityToolkit.Mvvm): `[ObservableProperty]` on partial properties, compiled bindings with
  `x:DataType`, UI strings via `Localization/Strings.cs` + `{loc:Loc key}`.
- Settings save as they are typed (no Save button): each `ISettingsSection` raises `Changed`, `SettingsViewModel`
  debounces 500 ms and writes; number boxes go through `NumericField` so a half-typed value never reaches config.
- A host always has an id: `LAN-<fingerprint>` until a rendezvous server assigns one (`PeerIdentityStore.IsLocalId`).
  Tests that need a server-assigned id must wait on `!IsLocalId`, not on `Id.Length > 0`.
  Main window = navigation rail + `HomeView`/`SettingsView`; each settings tab is a `ViewModels/Settings/*SettingsViewModel`
  implementing `ISettingsSection` (Load/Validate/Apply/AfterSaveAsync) with its view under `Views/Settings/`. Controller
  settings persist in `DesktopConfig`, host settings in `HostConfig` over IPC; both read stored values over their defaults.
- The README's pictures (`docs/images/screenshots/`) come from `tools/DeskPair.Tools.Screenshots`: the app's own windows,
  drawn headless by Skia around made-up data (`Samples.cs`), never a capture of a real desk. Run it again after a visible
  UI change (`dotnet run --project tools/DeskPair.Tools.Screenshots`). The internal seams it needs
  (`DesktopConfig.PathOverride`, `SessionViewModelBase.Dials`, `HostLink.Deliver`, `MainWindowViewModel`'s parts
  constructor) are for it alone.
