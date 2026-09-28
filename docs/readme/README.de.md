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

## Screenshots

Die Desktop-App unter Windows. Die Bilder zeichnet `tools/DeskPair.Tools.Screenshots` aus den eigenen Fenstern der
App; die Computer, Personen und Adressen darin sind erfunden.

<table>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/remote-session.png" width="100%" alt="Eine Fernsitzung"><br>
      Ein anderer Computer wird gesteuert; jeder verbundene Computer hat einen eigenen Tab.
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/file-transfer.png" width="100%" alt="Dateiübertragung"><br>
      Dateiübertragung: links dieser Computer, rechts der andere.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/home.png" width="100%" alt="Das Hauptfenster"><br>
      Die ID und das Einmalpasswort dieses Computers und die zuletzt verbundenen Computer.
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/devices.png" width="100%" alt="Geräte"><br>
      Gespeicherte Geräte in Gruppen, mit ihrem Online-Status.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/terminal.png" width="100%" alt="Ein entferntes Terminal"><br>
      Eine Shell auf einem anderen Computer, ausgeführt unter dem oben genannten Konto.
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/incoming-request.png" width="100%" alt="Eine Verbindungsanfrage"><br>
      Jemand möchte sich verbinden: Die Person an diesem Computer nimmt an oder lehnt ab und wählt, was erlaubt ist.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/history.png" width="100%" alt="Verbindungsverlauf"><br>
      Wer sich mit diesem Computer verbunden hat und was erlaubt war.
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/settings-security.png" width="100%" alt="Sicherheitseinstellungen"><br>
      Sicherheitseinstellungen: wer sich verbinden darf und wie man sich dabei ausweist.
    </td>
  </tr>
</table>

## Download

Jede Version steht auf der [Release-Seite](https://github.com/Sunllo/DeskPair/releases/latest) und unter
[deskpair.app/download](https://deskpair.app/download), mit einer SHA-256-Prüfsumme für jede Datei (`SHA256SUMS`).

| System | Dateien |
|---|---|
| Windows 10 1809 oder neuer | x64, ARM64 und x86 (32 Bit): jeweils ein Installationsprogramm (`.msi`) und eine portable `.zip`, benannt wie `DeskPair-<version>-win-x64.msi`. Codesigniert; solange eine neue Version noch keine Reputation aufgebaut hat, kann SmartScreen vor dem ersten Start trotzdem nachfragen. |
| macOS 13 oder neuer | `DeskPair-<version>-arm64.dmg` (Apple Silicon), `-x86_64.dmg` (Intel). Signiert und von Apple notarisiert. |
| Linux, glibc 2.31 oder neuer | x64, ARM64 und 32-Bit-ARM (ARMv7): jeweils ein `.deb`, ein `.rpm`, ein Arch-Paket und ein `.tar.gz`. |

Unter Windows legt das Installationsprogramm DeskPair unter „Programme“ ab und trägt es ins Startmenü und unter
„Einstellungen › Apps“ ein, wo es sich auch wieder entfernen lässt; neuere Versionen installiert die App selbst,
sobald ein Administrator zustimmt. Die `.zip` läuft dort, wo sie entpackt wurde, und aktualisiert sich selbst. Ohne
Rückfragen installieren, etwa auf vielen Computern zugleich:

```
msiexec /i DeskPair-<version>-win-x64.msi /qn
```

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

## DeskPair entfernen

Beim Entfernen verschwinden das Programm und seine Änderungen am System; was diesen Computer ausmacht, bleibt:
seine ID und sein Schlüssel, seine Passwörter, Ihre Einstellungen und die Geräteliste, der Verbindungsverlauf und die
Protokolle. Neu installiert, kommt DeskPair als derselbe Computer zurück, erreichbar mit demselben festen Passwort.
Um neu anzufangen, löschen Sie auch diese mit den Befehlen unten; die nächste Installation erhält dann eine neue ID.
Ein Computer, der mit einem DeskPair-Konto verknüpft ist, bleibt in der Geräteliste des Kontos, bis er dort entfernt
wird.

Unter Windows deinstallieren Sie DeskPair unter „Einstellungen › Apps“. Das entfernt das Programm, seine
Verknüpfungen, den Dienst für den unbeaufsichtigten Zugriff, seine Firewallregeln – auch die, die Windows selbst
angelegt hat, als DeskPair in seiner Abfrage zugelassen wurde –, die Einträge für den Start bei der Anmeldung und den
virtuellen Anzeigetreiber. Eine Kopie aus der `.zip` hat kein Deinstallationsprogramm: Führen Sie in einem als
Administrator geöffneten Terminal in ihrem Ordner `DeskPair.exe --remove-system-changes` aus, das dasselbe entfernt,
und löschen Sie dann den Ordner. Die Daten löschen zwei Zeilen PowerShell, die erste von jeder Person ausgeführt, die
DeskPair benutzt hat, die zweite einmal als Administrator:

```
Remove-Item -Recurse -Force -ErrorAction Ignore "$env:APPDATA\Sunllo\DeskPair", "$env:LOCALAPPDATA\Sunllo\DeskPair", "$env:TEMP\DeskPair", "$env:TEMP\deskpair-update*"
Remove-Item -Recurse -Force -ErrorAction Ignore "$env:ProgramData\Sunllo\DeskPair"
```

Unter macOS schalten Sie zuerst in DeskPair Einstellungen › Sicherheit › Erreichbar, während gesperrt und
Einstellungen › Allgemein › Bei meiner Anmeldung starten aus und ziehen DeskPair dann aus „Programme“ in den
Papierkorb. Die Daten, die im Schlüsselbund gespeicherten Passwörter und die Berechtigungen für Bildschirmaufnahme und
Bedienungshilfen entfernen diese Zeilen:

```
rm -rf ~/Library/Application\ Support/Sunllo/DeskPair ~/Library/LaunchAgents/com.sunllo.deskpair.login.plist
sudo rm -rf "/Library/Application Support/Sunllo/DeskPair" /Library/LaunchAgents/com.sunllo.deskpair.agent.plist
while security delete-generic-password -s "Sunllo DeskPair" >/dev/null 2>&1; do :; done
tccutil reset ScreenCapture com.sunllo.deskpair
tccutil reset Accessibility com.sunllo.deskpair
```

Unter Linux entfernen Sie das Paket mit dem Werkzeug, mit dem es installiert wurde; der Dienst für den
unbeaufsichtigten Zugriff geht mit:

```
sudo apt remove deskpair       # Debian, Ubuntu, Mint, Raspberry Pi OS
sudo dnf remove deskpair       # Fedora, RHEL
sudo zypper remove deskpair    # openSUSE
sudo pacman -R deskpair        # Arch, Manjaro
```

Eine Kopie aus der `.tar.gz` hat kein Paket: Schalten Sie zuerst „Erreichbar, während gesperrt“ und „Bei meiner
Anmeldung starten“ aus oder führen Sie `sudo /opt/deskpair/DeskPair --uninstall-service` aus, und löschen Sie dann
die Datei. Die Daten, einschließlich dessen, was der Dienst aufbewahrt hat, und des Kontos `deskpair`, unter dem er
lief:

```
rm -rf ~/.local/share/deskpair ~/.local/share/Sunllo/DeskPair ~/.config/Sunllo/DeskPair ~/.config/autostart/deskpair.desktop ~/.cache/DeskPair ~/.net/DeskPair
sudo rm -rf /var/lib/deskpair /etc/deskpair /root/.net/DeskPair
sudo userdel deskpair
```

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
