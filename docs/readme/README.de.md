# Sunllo DeskPair

[English](../../README.md) · [繁體中文](README.zh-Hant.md) · [简体中文](README.zh-Hans.md) ·
[日本語](README.ja.md) · [한국어](README.ko.md) · **Deutsch** ·
[Français](README.fr.md) · [Español](README.es.md) ·
[Português (Brasil)](README.pt-BR.md) · [Русский](README.ru.md)

> Dies ist eine Übersetzung von [README.md](../../README.md). Wo beide sich widersprechen, gilt die englische Fassung.

Ein plattformübergreifendes Fernwartungssystem (Windows / macOS / Linux), geschrieben in C# / .NET 10.

- **Desktop** (Avalonia) — steuernde und gesteuerte Seite in einer Anwendung.
- **Smartphone-Apps** (Kotlin Multiplatform, Android und iOS) — die steuernde Seite auf dem Telefon.
- **Rendezvous** — ID-Verzeichnis, Online-Status und Signalisierung für NAT-Hole-Punching.
- **Relay** — leitet TCP für Geräte weiter, die sich nicht direkt verbinden können.

Konten sind freiwillig: Sie verknüpfen einen Computer oder ein Telefon mit einer Person und halten die Liste der
gespeicherten Computer auf allen Geräten gleich. Sie sind ein Dienst unter `deskpair.app`, den die Apps über HTTPS
(`/api/v1`) erreichen; der Code dieses Dienstes ist nicht veröffentlicht.

Die Architektur orientiert sich an [RustDesk](https://github.com/rustdesk/rustdesk), verwendet aber ein eigenes
Protokoll (Protobuf-Nachrichten, Ende-zu-Ende-AES-256-GCM, Identitäten mit ECDSA P-256). Siehe
`docs/architecture.md`; die Dokumente unter `docs/` sind auf Traditionellem Chinesisch verfasst.

## Download

Jede Version steht auf der [Release-Seite](https://github.com/Sunllo/DeskPair/releases/latest) und unter
[deskpair.app/download](https://deskpair.app/download), mit einer SHA-256-Prüfsumme für jede Datei (`SHA256SUMS`).

| System | Dateien |
|---|---|
| Windows 10 1809 oder neuer | `DeskPair-<version>-win-x64.zip`, `-win-arm64.zip`, `-win-x86.zip` (32 Bit). Noch nicht codesigniert, daher fragt SmartScreen vor dem ersten Start nach. |
| macOS 13 oder neuer | `DeskPair-<version>-arm64.dmg` (Apple Silicon), `-x86_64.dmg` (Intel). Signiert und von Apple notarisiert. |
| Linux, glibc 2.31 oder neuer | x64, ARM64 und 32-Bit-ARM (ARMv7): jeweils ein `.deb`, ein `.rpm`, ein Arch-Paket und ein `.tar.gz`. |

Unter Linux installieren Sie das Paket für Ihre Distribution mit deren eigenen Werkzeugen (gezeigt sind die x64-Namen):

```
sudo apt install ./deskpair_<version>_amd64.deb                              # Debian, Ubuntu, Mint, Raspberry Pi OS
sudo dnf install ./deskpair-<version>-1.x86_64.rpm                            # Fedora, RHEL
sudo zypper install --allow-unsigned-rpm ./deskpair-<version>-1.x86_64.rpm    # openSUSE
sudo pacman -U deskpair-<version>-1-x86_64.pkg.tar.zst                        # Arch, Manjaro
```

Ein Paket legt DeskPair unter `/usr/lib/deskpair` ab, macht `deskpair` im Pfad verfügbar und trägt es ins
Anwendungsmenü ein. Erscheint eine neue Version, sagt die App Bescheid, und Sie installieren das neue Paket auf
demselben Weg. Das `.tar.gz` läuft auf jeder Distribution von dort, wo es entpackt wurde, und aktualisiert sich
selbst, wie die Versionen für Windows und macOS.

Die Smartphone-Apps, die einen Computer steuern, sind noch nicht in den Stores.

## Bauen

```
dotnet build DeskPair.slnx
dotnet test DeskPair.slnx
```

Benötigt das .NET 10 SDK (`global.json`). Video nutzt den Encoder des Betriebssystems (unter Windows Media
Foundation). Dieses Repository enthält keinen Software-H.264-Codec — H.264 ist unabhängig vom BSD-2-Quellcode von
OpenH264 patentiert —, aber man kann einen selbst bereitstellen; siehe `native/openh264/README.md`.

## Gesteuerter Rechner und Desktop-App

DeskPair ist eine einzige ausführbare Datei. Beim Öffnen zeigt sie Ihre ID und Ihr Einmalpasswort, verbindet Sie
mit anderen Rechnern und führt im selben Prozess die Host-Engine aus — die App zu öffnen macht diesen Computer also
steuerbar, sie zu schließen beendet das.

```
# die App
dotnet run --project src/DeskPair.Desktop

# nur die Engine, ohne Fenster: für einen Rechner ohne Desktop-Sitzung
dotnet run --project src/DeskPair.Desktop -- --server
```

**Unbeaufsichtigter Zugriff** — erreichbar, während der Computer gesperrt ist oder niemand angemeldet ist — ist eine
Rolle, die Sie unter Einstellungen › Sicherheit › Erreichbar, während gesperrt installieren: ein Dienst unter
Windows, ein launchd-Agent unter macOS und ein Root-Daemon unter Linux. Siehe `docs/unattended-windows.md` und
`docs/unattended-linux.md`. Ohne ihn schalten Sie Einstellungen › Allgemein › **Bei meiner Anmeldung starten** ein,
um nach einem Neustart und Ihrer Anmeldung erreichbar zu sein.

Starten Sie die App unter macOS aus ihrem `.app`-Bundle: Die Zustimmung zu Bildschirmaufnahme und Bedienungshilfen
gilt dem Bundle, und ein aus dem Terminal gestartetes Programm bekommt stattdessen die des Terminals. Deshalb ist
`--server` dort eine Rolle zum Testen.

Um eigene Server zu verwenden, tragen Sie den Rendezvous-Server und seinen öffentlichen Schlüssel in den
Einstellungen (oder in `config.json`) ein; direkte Verbindungen zu `host:21118` brauchen keinen Server.

## Schnellstart (Kommandozeile ohne Fenster)

```
# 1. Server (Standard: rendezvous udp+tcp/21116, nat-test 21115, http 21114; relay tcp/21117)
dotnet run --project src/DeskPair.Rendezvous
dotnet run --project src/DeskPair.Relay -- --Relay:HttpPort=21124

# 2. den Serverschlüssel auslesen, dem die Clients vertrauen müssen
curl http://127.0.0.1:21114/key

# 3. gesteuerter Rechner (gibt seine 9-stellige ID und sein temporäres Passwort aus)
dotnet run --project tools/DeskPair.Tools.PeerCli -- host --server 127.0.0.1 --key <KEY> --password secret

# 4. steuernde Seite, in einem anderen Terminal; eingegebene Zeilen gehen als Chat hinaus
dotnet run --project tools/DeskPair.Tools.PeerCli -- connect <ID> --server 127.0.0.1 --key <KEY> --password secret
```

Verbindungen über die ID versuchen zuerst die LAN-Adresse (bei gleicher öffentlicher IP), dann TCP-Hole-Punching,
dann das Relay; `--force-relay` (PeerCli) / `ForceRelay` überspringt die direkten Wege. Siehe
`docs/nat-test-matrix.md`.

Docker: `docker compose -f deploy/docker-compose.yml up --build` (Host-Netzwerk; Hinweise stehen in der Datei).
Die Server im Ernstbetrieb: `deploy/README.md`.

## Lizenz

DeskPair ist freie Software unter der [GNU Affero General Public License, Version 3](../../LICENSE)
(`AGPL-3.0-only`). Das gilt für die Anwendungen und die Server in diesem Repository gleichermaßen. Wichtig, wenn Sie
die Server für andere betreiben: Abschnitt 13 verlangt, diesen Menschen den Quellcode dessen anzubieten, was Sie
tatsächlich betreiben, samt Ihren Änderungen.

Komponenten Dritter behalten ihre eigenen Lizenzen; die Anwendung listet sie unter Einstellungen › Über DeskPair.
