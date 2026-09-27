# Sunllo DeskPair

[English](../../README.md) · **繁體中文** · [简体中文](README.zh-Hans.md) ·
[日本語](README.ja.md) · [한국어](README.ko.md) · [Deutsch](README.de.md) ·
[Français](README.fr.md) · [Español](README.es.md) ·
[Português (Brasil)](README.pt-BR.md) · [Русский](README.ru.md)

> 本文譯自 [README.md](../../README.md)，兩者不一致時以英文版為準。

以 C# / .NET 10 撰寫的跨平台（Windows／macOS／Linux）遠端桌面系統。

- **桌面程式**（Avalonia）——控制端與被控端在同一個程式裡。
- **手機 App**（Kotlin Multiplatform，Android 與 iOS）——手機上的控制端。
- **Rendezvous**——ID 註冊、上線狀態與 NAT 打洞的信令。
- **Relay**——無法直接連線的兩端透過它轉送 TCP。

帳號可有可無：它把電腦或手機連結到一個人，並讓各裝置上儲存的電腦清單保持一致。帳號是在 `deskpair.app`
營運的服務，App 經由 HTTPS（`/api/v1`）連線；這項服務的程式碼不公開。

架構參考了 [RustDesk](https://github.com/rustdesk/rustdesk)，但使用自己的協定（protobuf 訊息、端對端
AES-256-GCM、ECDSA P-256 身分）。詳見 `docs/architecture.md`；`docs/` 底下的文件都以繁體中文撰寫。

## 截圖

Windows 上的桌面程式。這些圖由 `tools/DeskPair.Tools.Screenshots` 從程式自己的視窗繪製而成，圖中的電腦、人名與位址都是虛構的。

<table>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/remote-session.png" width="100%" alt="遠端工作階段"><br>
      控制另一台電腦；每台連線中的電腦各有自己的分頁。
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/file-transfer.png" width="100%" alt="檔案傳輸"><br>
      檔案傳輸：左邊是這台電腦，右邊是對方。
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/home.png" width="100%" alt="主視窗"><br>
      這台電腦的 ID 與臨時密碼，以及最近連線過的電腦。
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/devices.png" width="100%" alt="設備清單"><br>
      設備清單依群組排列，並顯示哪些在線。
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/terminal.png" width="100%" alt="遠端終端機"><br>
      另一台電腦上的 shell，以頂端標示的帳號執行。
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/incoming-request.png" width="100%" alt="連線要求"><br>
      有人要求連線時，這台電腦前的人決定接受或拒絕，並勾選允許的權限。
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <img src="../images/screenshots/history.png" width="100%" alt="連線紀錄"><br>
      誰連線過這台電腦，以及當時被允許做什麼。
    </td>
    <td width="50%" valign="top">
      <img src="../images/screenshots/settings-security.png" width="100%" alt="安全設定"><br>
      安全設定：誰可以連線，以及如何證明身分。
    </td>
  </tr>
</table>

## 下載

每個版本都在 [Releases 頁面](https://github.com/Sunllo/DeskPair/releases/latest)與
[deskpair.app/download](https://deskpair.app/download)，每個檔案都附 SHA-256（`SHA256SUMS`）。

| 系統 | 檔案 |
|---|---|
| Windows 10 1809 以上 | `DeskPair-<version>-win-x64.zip`、`-win-arm64.zip`、`-win-x86.zip`（32 位元）。尚未做程式碼簽章，第一次執行時 SmartScreen 會先詢問。 |
| macOS 13 以上 | `DeskPair-<version>-arm64.dmg`（Apple 晶片）、`-x86_64.dmg`（Intel）。已簽章並經 Apple 公證。 |
| Linux，glibc 2.31 以上 | x64、ARM64 與 32 位元 ARM（ARMv7）：各有 `.deb`、`.rpm`、Arch 套件與 `.tar.gz`。 |

在 Linux 上，用發行版自己的工具安裝對應的套件（以下是 x64 的檔名）：

```
sudo apt install ./deskpair_<version>_amd64.deb                              # Debian, Ubuntu, Mint, Raspberry Pi OS
sudo dnf install ./deskpair-<version>-1.x86_64.rpm                            # Fedora, RHEL
sudo zypper install --allow-unsigned-rpm ./deskpair-<version>-1.x86_64.rpm    # openSUSE
sudo pacman -U deskpair-<version>-1-x86_64.pkg.tar.zst                        # Arch, Manjaro
```

套件會把 DeskPair 裝在 `/usr/lib/deskpair`，命令列可以直接執行 `deskpair`，應用程式選單裡也會出現。有新版本時
App 會告訴你，再用同樣的方式安裝新套件。`.tar.gz` 在任何發行版上，解壓到哪裡都能執行，而且會自己更新，
和 Windows、macOS 版一樣。

手機 App（用來控制電腦）還沒有上架。

## 建置

```
dotnet build DeskPair.slnx
dotnet test DeskPair.slnx
```

需要 .NET 10 SDK（`global.json`）。影像使用作業系統的編碼器（Windows 上是 Media Foundation）。本 repository
不附軟體 H.264 編解碼器——H.264 的專利與 OpenH264 的 BSD-2 原始碼是兩回事——但可以自行提供，見
`native/openh264/README.md`。

## 被控端與桌面程式

DeskPair 只有一個執行檔。打開它會顯示你的 ID 與臨時密碼，可以連到其他電腦，同一個行程裡也執行被控端引擎——
所以打開 App 就是讓這台電腦可以被控制，關掉 App 就停止。

```
# App 本身
dotnet run --project src/DeskPair.Desktop

# 只有引擎、沒有視窗：給沒有桌面工作階段的機器
dotnet run --project src/DeskPair.Desktop -- --server
```

**無人值守存取**——電腦鎖定或沒有人登入時也連得到——是另外安裝的角色，從「設定 › 安全 › 鎖定時仍可被連線」
安裝：Windows 上是服務、macOS 上是 launchd agent、Linux 上是 root daemon。見 `docs/unattended-windows.md` 與
`docs/unattended-linux.md`。沒有安裝時，開啟「設定 › 基本 › **登入時自動啟動**」，重新開機並登入後就連得到。

在 macOS 上請從 `.app` 套件執行 App：螢幕錄製與輔助使用的同意是授予這個套件的，從終端機啟動的執行檔
用的則是終端機的同意。因此在 macOS 上，`--server` 只是測試用的角色。

要使用自己的伺服器，在「設定」（或 `config.json`）填入 rendezvous 伺服器與它的公鑰；直接連到 `host:21118`
則不需要任何伺服器。

## 快速上手（無視窗的命令列）

```
# 1. 伺服器（預設：rendezvous udp+tcp/21116、nat-test 21115、http 21114；relay tcp/21117）
dotnet run --project src/DeskPair.Rendezvous
dotnet run --project src/DeskPair.Relay -- --Relay:HttpPort=21124

# 2. 讀出用戶端必須信任的伺服器金鑰
curl http://127.0.0.1:21114/key

# 3. 被控端（印出 9 位數 ID 與臨時密碼）
dotnet run --project tools/DeskPair.Tools.PeerCli -- host --server 127.0.0.1 --key <KEY> --password secret

# 4. 控制端，在另一個終端機執行；輸入的每一行都會當成聊天訊息送出
dotnet run --project tools/DeskPair.Tools.PeerCli -- connect <ID> --server 127.0.0.1 --key <KEY> --password secret
```

用 ID 連線時，依序嘗試區網位址（同一個公開 IP 時）、TCP 打洞，最後才是 relay；`--force-relay`（PeerCli）／
`ForceRelay` 會跳過直接連線。見 `docs/nat-test-matrix.md`。

Docker：`docker compose -f deploy/docker-compose.yml up --build`（使用 host 網路，說明寫在檔案裡）。
正式架設伺服器：見 `deploy/README.md`。

## 授權

DeskPair 是自由軟體，採用 [GNU Affero 通用公共授權條款第 3 版](../../LICENSE)（`AGPL-3.0-only`），本 repository
裡的 App 與伺服器都適用。如果你為別人架設伺服器，要注意的是第 13 條：你必須讓那些使用者取得你實際在運行的
程式的原始碼，包括你做的修改。

第三方元件維持各自的授權；App 在「設定 › 關於 DeskPair」列出它們。
