# Sunllo DeskPair

[English](../../README.md) · [繁體中文](README.zh-Hant.md) · [简体中文](README.zh-Hans.md) ·
[日本語](README.ja.md) · [한국어](README.ko.md) · [Deutsch](README.de.md) ·
**Français** · [Español](README.es.md) ·
[Português (Brasil)](README.pt-BR.md) · [Русский](README.ru.md)

> Ceci est une traduction de [README.md](../../README.md). En cas de divergence, la version anglaise fait foi.

Un système de bureau à distance multiplateforme (Windows / macOS / Linux) écrit en C# / .NET 10.

- **Bureau** (Avalonia) — le poste qui contrôle et celui qui est contrôlé, dans une seule application.
- **Téléphones** (Kotlin Multiplatform, Android et iOS) — le poste qui contrôle, sur un téléphone.
- **Rendezvous** — annuaire des ID, présence et signalisation pour la traversée de NAT (hole punching).
- **Relay** — relais TCP pour les appareils qui ne peuvent pas se joindre directement.

Les comptes sont facultatifs : ils relient un ordinateur ou un téléphone à une personne et gardent la liste des
ordinateurs enregistrés identique sur tous ses appareils. C'est un service exploité sur `deskpair.app`, que les
applications joignent en HTTPS (`/api/v1`) ; son code n'est pas publié.

L'architecture s'inspire de [RustDesk](https://github.com/rustdesk/rustdesk) mais utilise son propre protocole
(messages protobuf, AES-256-GCM de bout en bout, identités ECDSA P-256). Voir `docs/architecture.md` ; les documents
de `docs/` sont rédigés en chinois traditionnel.

## Captures d'écran

L'application de bureau sous Windows. Les images sont dessinées à partir des propres fenêtres de l'application par
`tools/DeskPair.Tools.Screenshots` ; les ordinateurs, les personnes et les adresses qu'elles montrent sont fictifs.

<table>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/remote-session.png" width="100%" alt="Une session à distance"><br>
      Le contrôle d'un autre ordinateur ; chaque ordinateur connecté a son propre onglet.
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/file-transfer.png" width="100%" alt="Transfert de fichiers"><br>
      Transfert de fichiers : cet ordinateur à gauche, l'autre à droite.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/home.png" width="100%" alt="La fenêtre principale"><br>
      L'ID et le mot de passe à usage unique de cet ordinateur, et les ordinateurs joints récemment.
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/devices.png" width="100%" alt="Appareils"><br>
      Les appareils enregistrés, par groupes, et ceux qui sont en ligne.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/terminal.png" width="100%" alt="Un terminal à distance"><br>
      Un shell sur un autre ordinateur, exécuté sous le compte indiqué en haut.
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/incoming-request.png" width="100%" alt="Une demande de connexion"><br>
      Quelqu'un demande à se connecter : la personne devant cet ordinateur accepte ou refuse, et choisit ce qu'elle autorise.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/history.png" width="100%" alt="Historique des connexions"><br>
      Qui s'est connecté à cet ordinateur, et ce qui lui était permis.
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/settings-security.png" width="100%" alt="Paramètres de sécurité"><br>
      Paramètres de sécurité : qui peut se connecter, et comment le prouver.
    </td>
  </tr>
</table>

## Téléchargement

Chaque version se trouve sur la [page des versions](https://github.com/Sunllo/DeskPair/releases/latest) et sur
[deskpair.app/download](https://deskpair.app/download), avec un SHA-256 pour chaque fichier (`SHA256SUMS`).

| Système | Fichiers |
|---|---|
| Windows 10 1809 ou ultérieur | x64, ARM64 et x86 (32 bits) : pour chacun un programme d'installation (`.msi`) et un `.zip` portable, nommés comme `DeskPair-<version>-win-x64.msi`. Pas encore signés, donc SmartScreen demande confirmation avant le premier lancement. |
| macOS 13 ou ultérieur | `DeskPair-<version>-arm64.dmg` (Apple Silicon), `-x86_64.dmg` (Intel). Signés et notariés par Apple. |
| Linux, glibc 2.31 ou ultérieure | x64, ARM64 et ARM 32 bits (ARMv7) : pour chacun un `.deb`, un `.rpm`, un paquet Arch et un `.tar.gz`. |

Sous Windows, le programme d'installation place DeskPair dans Program Files et l'ajoute au menu Démarrer et à
Paramètres › Applications, d'où l'on peut aussi le désinstaller ; l'application installe elle-même les versions
suivantes dès qu'un administrateur les approuve. Le `.zip` fonctionne là où il a été décompressé et se met à jour
tout seul. Pour installer sans aucune question, par exemple sur de nombreux ordinateurs à la fois :

```
msiexec /i DeskPair-<version>-win-x64.msi /qn
```

Sous Linux, installez le paquet de votre distribution avec ses propres outils (les noms x64 sont donnés ici) :

```
sudo apt install ./deskpair_<version>_amd64.deb                              # Debian, Ubuntu, Mint, Raspberry Pi OS
sudo dnf install ./deskpair-<version>-1.x86_64.rpm                            # Fedora, RHEL
sudo zypper install --allow-unsigned-rpm ./deskpair-<version>-1.x86_64.rpm    # openSUSE
sudo pacman -U deskpair-<version>-1-x86_64.pkg.tar.zst                        # Arch, Manjaro
```

Un paquet installe DeskPair dans `/usr/lib/deskpair`, rend `deskpair` disponible dans le chemin et l'ajoute au menu
des applications. Quand une nouvelle version sort, l'application vous le signale, et vous installez le nouveau
paquet de la même façon. Le `.tar.gz` fonctionne sur toute distribution, là où il a été décompressé, et se met à
jour tout seul, comme les versions Windows et macOS.

Les applications pour téléphone, qui contrôlent un ordinateur, ne sont pas encore dans les stores.

## Compilation

```
dotnet build DeskPair.slnx
dotnet test DeskPair.slnx
```

Nécessite le SDK .NET 10 (`global.json`). La vidéo utilise l'encodeur du système (Media Foundation sous Windows).
Aucun codec H.264 logiciel n'est fourni avec ce dépôt — H.264 est breveté indépendamment du code source BSD-2
d'OpenH264 —, mais vous pouvez en fournir un ; voir `native/openh264/README.md`.

## Poste contrôlé et application de bureau

DeskPair est un exécutable unique. À l'ouverture, il affiche votre ID et votre mot de passe à usage unique, vous
permet de vous connecter à d'autres ordinateurs et fait tourner le moteur du poste contrôlé dans le même processus :
ouvrir l'application rend donc cet ordinateur contrôlable, et la fermer y met fin.

```
# l'application
dotnet run --project src/DeskPair.Desktop

# le moteur seul, sans fenêtre : pour une machine sans session de bureau
dotnet run --project src/DeskPair.Desktop -- --server
```

**L'accès sans surveillance** — être joignable quand l'ordinateur est verrouillé ou que personne n'a ouvert de
session — est un rôle que vous installez depuis Paramètres › Sécurité › Joignable une fois verrouillé : un service
sous Windows, un agent launchd sous macOS et un daemon root sous Linux. Voir `docs/unattended-windows.md` et
`docs/unattended-linux.md`. Sans lui, activez Paramètres › Général › **Démarrer à l'ouverture de session** pour être
joignable après un redémarrage, une fois votre session ouverte.

Sous macOS, lancez l'application depuis son paquet `.app` : l'autorisation d'enregistrement de l'écran et
d'accessibilité est accordée au paquet, et un exécutable lancé depuis un terminal hérite de celle du terminal.
C'est pourquoi `--server` n'y sert qu'aux tests.

Pour utiliser vos propres serveurs, indiquez le serveur rendezvous et sa clé publique dans les Paramètres (ou dans
`config.json`) ; les connexions directes à `host:21118` n'ont besoin d'aucun serveur.

## Démarrage rapide (ligne de commande, sans fenêtre)

```
# 1. serveurs (par défaut : rendezvous udp+tcp/21116, nat-test 21115, http 21114 ; relay tcp/21117)
dotnet run --project src/DeskPair.Rendezvous
dotnet run --project src/DeskPair.Relay -- --Relay:HttpPort=21124

# 2. lire la clé du serveur à laquelle les clients doivent se fier
curl http://127.0.0.1:21114/key

# 3. poste contrôlé (affiche son ID à 9 chiffres et son mot de passe temporaire)
dotnet run --project tools/DeskPair.Tools.PeerCli -- host --server 127.0.0.1 --key <KEY> --password secret

# 4. poste qui contrôle, dans un autre terminal ; les lignes tapées partent comme messages de discussion
dotnet run --project tools/DeskPair.Tools.PeerCli -- connect <ID> --server 127.0.0.1 --key <KEY> --password secret
```

Une connexion par ID essaie l'adresse du réseau local (même IP publique), puis la traversée de NAT en TCP, puis le
relais ; `--force-relay` (PeerCli) / `ForceRelay` saute les chemins directs. Voir `docs/nat-test-matrix.md`.

Docker : `docker compose -f deploy/docker-compose.yml up --build` (réseau de l'hôte ; les remarques sont dans le
fichier). Faire tourner les serveurs pour de bon : `deploy/README.md`.

## Licence

DeskPair est un logiciel libre sous la [GNU Affero General Public License, version 3](../../LICENSE)
(`AGPL-3.0-only`), qui couvre de la même façon les applications et les serveurs de ce dépôt. Ce qui compte si vous
exploitez les serveurs pour d'autres personnes : la section 13 vous demande de leur proposer le code source de ce que
vous exécutez réellement, modifications comprises.

Les composants tiers conservent leurs propres licences ; l'application les liste dans Paramètres › À propos de DeskPair.
