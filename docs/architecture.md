# DeskPair — 系統架構與實作計畫

## 1. Context

在 `C:\GitHub\DeskPair` (目前只有 `reference/rustdesk-master`) 從零建立 Sunllo 品牌的遠端桌面系統，
技術堆疊 C# / .NET，以 RustDesk v1.5.0 fork (Rust) 為架構參考，**不做二進位相容**，自由簡化。

### 已確認決策

| 項目 | 決定 |
|---|---|
| 命名 | 根命名空間與資料夾名都是 `DeskPair` (單一 s)；2026-09-17 從 `C:\Dev\Sunllo.FasstDesk` 搬到 `C:\GitHub\DeskPair`，順便修掉資料夾名的錯字 |
| 元件 | Client 控制端 + Host 被控端 (同一桌面 App) + `Rendezvous` 訊號伺服器 + `Relay` 中繼伺服器 (**兩個獨立程式**) |
| 協定 | 自訂，借鏡 RustDesk (protobuf 訊息、identity 簽章、打洞/中繼流程) |
| UI | Avalonia 11.3.x + CommunityToolkit.Mvvm；**Windows / macOS / Linux 三平台同步開發** |
| 視訊編解碼 | 平台原生 API：Windows Media Foundation / macOS VideoToolbox / Linux VAAPI (經 FFmpeg 封裝)；軟體後備需自備（見 §軟體後備編解碼） |
| .NET | **.NET 10 (LTS)**，`Directory.Build.props` 統一 TFM |
| v1 功能 | 遠端桌面 (畫面+鍵鼠+游標) / 無人值守 (常駐服務+固定密碼) / 檔案傳輸+文字/圖片剪貼簿 / Opus 音訊+文字聊天 |
| 語言 | 程式碼、註解、commit 英文；UI 字串走資源檔，預設 zh-TW + en |
| 版控 | `git init`，`reference/` 進 `.gitignore` |
| 部署 | 兩個伺服器各一個 Docker image，正式環境 `network_mode: host` |

### 參考專案的關鍵事實

- `libs/hbb_common/` 是**空的 submodule**：`rendezvous.proto`、`config.rs`、`bytes_codec.rs` 不在本機，rendezvous 流程從
  `src/rendezvous_mediator.rs`、`src/client.rs`、`src/server/connection.rs` 讀出；唯一 proto 是 `libs/base/protos/message.proto` (1,023 行)。
- 總量 ~168k 行 Rust (扣除 ~45k 翻譯)。最大檔：`connection.rs` 7,603、`client.rs` 5,721、`platform/windows.rs` 4,955、`input_service.rs` 2,535。
- 我們刻意**不做**的功能：terminal、port forward/RDP、camera、cliprdr 檔案剪貼簿、plugin、whiteboard、privacy mode、
  虛擬顯示器、switch sides、portable service、WebRTC、KCP。

---

## 2. 整體架構

```
            ┌──────────────────────┐   UDP/TCP 21116 (註冊、打洞訊號)    ┌──────────────────────┐
            │  DeskPair.    │◄─────────────────────────────────►│  DeskPair.    │
            │  Rendezvous (Docker) │   TCP 21115 (NAT 測試) HTTP 21114  │  Desktop / Service   │
            └──────────┬───────────┘                                    │      (Host 被控端)    │
                       │ /healthz 探測                                  └──────────▲───────────┘
            ┌──────────▼───────────┐   TCP 21117 (RequestRelay{uuid})              │ 直連 21118 / 打洞 / 中繼
            │  DeskPair.    │◄──────────────────────────────────────────────┤
            │  Relay (Docker)      │◄──────────────────────────────────────────────┤
            └──────────────────────┘                                    ┌──────────▼───────────┐
                                                                        │  DeskPair.    │
      端到端加密：ControllerHello/HostHello → ECDH → AES-GCM，          │  Desktop (Controller)│
      中繼只做 byte 對接、無法解讀                                       └──────────────────────┘
```

Host 機器上的程序模型 (三平台一致)：**單一執行檔、單一程序**。

```
DeskPair (登入使用者身分)
  ├─ 主視窗 / 工作列圖示 / 連線管理員卡片
  ├─ Host 引擎 (擷取 / 編碼 / 注入)          ← 同一個程序內的背景 Task
  └─ IPC server (named pipe / UDS)           ← UI 與連線管理員各自以 client 連回本機
```

引擎不再是獨立的子程序。macOS 的「畫面錄製」與「輔助使用」權限是**逐執行檔**授予的，另一個執行檔就是另一份
同意書；使用者授權給 App 的權限蓋不到它，擷取因此靜默失敗、對方只看到黑畫面。單一程序＝單一身分＝單一份權限。

代價寫明白：**沒有無人值守**。沒人登入時連不上，登入畫面、UAC 與 secure desktop 都擷取不到。開機後要能被連，
靠的是設定裡的「登入時啟動」。

---

## 3. 解決方案配置

```
C:\GitHub\DeskPair\
  DeskPair.sln
  global.json                         SDK 10.0.x
  Directory.Build.props               net10.0, Nullable, ImplicitUsings, LangVersion latest, TreatWarningsAsErrors
  Directory.Packages.props            Central Package Management
  .editorconfig  .gitignore (含 reference/)  CLAUDE.md  README.md
  protos/sunllo/
    rendezvous.proto  message.proto  ipc.proto
  src/
    DeskPair.Protocol/         生成 protobuf、FramedStream、SessionCipher、Handshake、PasswordProof、ProtocolConstants
    DeskPair.Platform.Abstractions/   IScreenCapturer/IVideoEncoder/IInputInjector/… 純介面 + 值型別
    DeskPair.Core/             Session 狀態機、Dispatcher、Publisher Services、QoS、FileTransfer、ClipboardSync、
                                      RendezvousClient/RelayClient/PeerConnector、NAT、IPC server/client、Testing fakes
    DeskPair.Platform.Windows/ net10.0-windows；CsWin32 + Vortice (DXGI/D3D11/MF)、NAudio
    DeskPair.Platform.MacOS/   ObjC shim dylib P/Invoke (ScreenCaptureKit/VideoToolbox/CGEvent)
    DeskPair.Platform.Linux/   LibraryImport libX11/Xext/Xtst/Xfixes、FFmpeg.AutoGen (VAAPI)、Tmds.DBus
    DeskPair.Desktop/          唯一的 app exe (DeskPair)：Avalonia UI + 工作列 + 連線管理員 +
                                      Engine/ (host 引擎)。角色：無參數＝App、--server＝只跑引擎不開視窗、
                                      --allow-firewall/--remove-firewall、--version
    DeskPair.Server.Shared/    兩個伺服器共用的 hosting 膠水 (Serilog、/healthz、metrics、設定綁定)
    DeskPair.Rendezvous/       exe (Generic Host + Kestrel 21114)
    DeskPair.Relay/            exe
  native/
    macos/SunlloMacShim/              ObjC 原始碼 + build.sh → SunlloMacShim.dylib
    linux/                            (預留)
  tools/
    DeskPair.Tools.PeerCli/    無頭 host/controller，用 fake 媒體跑 E2E；每個里程碑的驗證工具
    DeskPair.Tools.KeyGen/     產生伺服器 ECDSA key、印出 base64 SPKI
    DeskPair.Tools.PlatformHarness/  capture-to-png / encode-roundtrip / inject-and-verify
  tests/
    *.Protocol.Tests  *.Core.Tests  *.Rendezvous.Tests  *.Relay.Tests
    *.Integration.Tests               in-process Rendezvous + Relay + 兩個 Core peer，不需 Docker
    *.Platform.Windows.Tests
  deploy/
    docker-compose.yml  Dockerfile.rendezvous  Dockerfile.relay  appsettings.*.example.json
  docs/
    architecture.md (本計畫精簡版)  protocol.md  nat-test-matrix.md
```

相依方向 (→ = 參考)：
`Protocol` ← 無；`Platform.Abstractions` ← 無；`Core` → Protocol, Platform.Abstractions；
`Platform.*` → Platform.Abstractions (只此)；`Service` → Core, Platform.*；`Desktop` → Core, Platform.*；
`Rendezvous`/`Relay` → Protocol, Server.Shared (**伺服器絕不參考 Core**)。

主要 NuGet：`Google.Protobuf` + `Grpc.Tools` (GrpcServices=None)、`Microsoft.Extensions.Hosting`、`Microsoft.Data.Sqlite`、
`Serilog.AspNetCore`、`prometheus-net.AspNetCore` (選用)、`Avalonia` 11.3 + `Avalonia.Desktop`、`CommunityToolkit.Mvvm`、
`Microsoft.Windows.CsWin32`、`Vortice.Direct3D11/DXGI/MediaFoundation`、`NAudio`、`FFmpeg.AutoGen`、`Tmds.DBus`、
`Concentus` (純 C# Opus，先用；CPU 不夠再換 libopus)、`Otp.NET` (2FA, 後期)、測試 `xunit` + `NSubstitute` + `Shouldly`。

---

## 4. 協定設計 (`DeskPair.Protocol`)

### 4.1 Framing (`Framing/FramedStream.cs`)

- **固定 4-byte LE 標頭**：bit31 = encrypted、bit30 保留、低 30 bit = 長度。`len == 0` 為 heartbeat，永不加密。
- 加密幀：`[hdr][AES-256-GCM ciphertext][16B tag]`，AAD = 4-byte 標頭；nonce 12B = HKDF 4B 方向前綴 + 8B LE 計數器 (從 1 起，每方向獨立 key 與計數器，溢位前丟例外)。
- 每條串流上限：peer session 32 MiB、rendezvous TCP / relay 首幀 16 KiB、UDP 單一 datagram ≤ 1200 B (無前綴、不加密)。
- 以 `PipeReader` 讀、`ArrayPool` 借出 `Frame` (呼叫端 Dispose)；送出用 `UnsafeByteOperations.UnsafeWrap` 避免複製視訊/檔案 block。
- **Relay 讀取首幀 (`RequestRelay`) 必須精確讀取**，不得用 PipeReader 預讀 (後續 bytes 屬於端到端串流)。

### 4.2 加密 (純 BCL，`Crypto/`)

| 用途 | 演算法 | BCL 型別 |
|---|---|---|
| 裝置/伺服器 identity 簽章 | ECDSA P-256, SHA-256, `r‖s` 64B | `ECDsa` |
| 金鑰協商 | 雙方 ephemeral ECDH P-256 + HKDF-SHA256 | `ECDiffieHellman`, `HKDF` |
| 串流 AEAD | AES-256-GCM | `AesGcm` (啟動時檢查 `IsSupported`) |
| 密碼 | `h1 = SHA256(pw‖salt)` 儲存，`proof = SHA256(h1‖challenge)` | `SHA256`, `FixedTimeEquals` |
| 公鑰格式 | SPKI DER | `ExportSubjectPublicKeyInfo` |

以 `IIdentityKey` / `IKeyAgreement` 隔離，未來可換 Ed25519/X25519 (BouncyCastle)。`ChaCha20Poly1305` 因 Windows 10 不支援而不採用。

### 4.3 Peer 交握 (`Crypto/Handshake.cs`)

```
C→H  ControllerHello { protocol_version, eph_c(SPKI), nonce_c(32B), version }                        明文
H→C  HostHello { protocol_version, id, identity_pk, eph_h, nonce_h,
                 sig = ECDSA_identity( SHA256("SunlloHS1"‖id‖identity_pk‖eph_h‖nonce_h‖eph_c‖nonce_c) ) }  明文
雙方  ss = ECDH(eph_own, eph_peer); prk = HKDF-Extract(nonce_c‖nonce_h, ss)
      k_c2h / k_h2c / iv_c2h / iv_h2c = HKDF-Expand(prk, "sunllo/v1/…")；抹除 ephemeral 私鑰
H→C  AuthChallenge { salt, challenge(32B), approve_mode, requires_totp }                              加密
C→H  LoginRequest { password_proof, my_id, my_name, my_platform, hwid, conn_type, options, version, session_id, totp_code }
H→C  LoginResponse { error(LoginError{code,…}) | peer_info } (waiting_for_approval)
```

Controller 驗證 HostHello 的兩層：(1) `identity_pk` 必須等於 Rendezvous 簽發的 `SignedPeerIdentity{id, pk}` 內的 pk (用設定的伺服器公鑰驗簽)；
直連 IP / LAN 無伺服器時改用 TOFU pin (`known_hosts.json`，變更時 UI 警告)。(2) 用 `identity_pk` 驗 transcript 簽章 (含 eph_c/nonce_c 防重放)。
Ephemeral ECDH 提供前向保密 (優於 RustDesk 靜態金鑰 seal)。

### 4.4 認證 / 核准 / 防暴力

- Host 保存 `PermanentH1` (使用者設定) 與 `TemporaryH1` (服務啟動時隨機、可設定每次連線後輪換)，salt 每次安裝產生一次。兩者都算、都 `FixedTimeEquals`。
- `ApproveMode`：`Password` / `Click` (CM 視窗核准，30 s 逾時 → `APPROVAL_TIMEOUT`；**無 UI 程序時一律拒絕，絕不自動接受**) / `Both`。
- `LoginFailureTracker` (以 `TimeProvider` 可測)：每來源 IP 60 s 內 6 次失敗 → `TOO_MANY_ATTEMPTS` + `retry_after_ms`；1 h 內 30 次 → 封鎖 1 h；
  每次失敗回覆前 `Delay(1 s)`；臨時密碼連錯 10 次 (不分 IP) 即輪換並通知 UI。
- IP 白名單 (選用) 在交握前檢查。TOTP 2FA 為後期項目。

### 4.5 逾時與心跳 (`ProtocolConstants`)

| 連線 | 心跳 | 判定斷線 |
|---|---|---|
| Host → Rendezvous UDP `RegisterPeer` | 12 s (伺服器可回 `keep_alive_sec`) | 30 s 未註冊即離線 |
| Host TCP mediator (UDP 被擋時) | 空幀 10 s | 30 s |
| Controller → Rendezvous `PunchHoleRequest` | 3 次重試，deadline 3 s × 次數 | — |
| Relay 配對前 | — | 30 s 未配對即丟棄；配對後閒置 60 s |
| Peer session | Host 每 1 s `TestDelay` (兼 RTT 探測)；5 s 無送出則空幀 | 30 s 無收到即關閉 |
| 交握 | `ControllerHello` 10 s 內、`LoginRequest` 60 s 內 | 寫入 30 s (視訊) / 60 s (檔案) 未完成即關閉 |

### 4.6 Proto 檔 (全部重新編號，只含 v1 功能)

**`rendezvous.proto`** (`package sunllo.rendezvous`)：`SocketAddress{ip bytes, port}`；`PeerIdentity{id, identity_pk, issued_at}` + `SignedPeerIdentity{payload, server_signature}`；
`RegisterPeer/RegisterPeerResponse{request_identity, keep_alive_sec}`；`RegisterIdentity{id, uuid, identity_pk}/RegisterIdentityResponse{result OK|UUID_MISMATCH|INVALID|TOO_FREQUENT|SERVER_ERROR, id, keep_alive_sec, identity}`；
`TestNatRequest/TestNatResponse{observed_port}`；`PunchHoleRequest{id, nat_type, conn_type, force_relay, version}`；`PunchHole{controller_addr, relay_server, nat_type, conn_type}`；
`FetchLocalAddr`；`PunchHoleSent`；`LocalAddr`；`RequestRelay{uuid, id, controller_addr, relay_server, conn_type}` (也是送給 Relay 的首幀)；
`RelayResponse{…, identity}`；`PunchHoleResponse{host_addr, identity, relay_server, nat_type, is_local, failure NONE|ID_NOT_EXIST|OFFLINE|SERVER_BUSY}`；
`QueryOnline/QueryOnlineResponse`；`KeepAlive`；外層 `RendezvousMessage{oneof}`。

**`message.proto`** (`package sunllo.messages`)：`ControllerHello`、`HostHello`、`AuthChallenge`、`LoginRequest`、`LoginError{code, message, retry_after_ms}`、`LoginResponse`、
`PeerInfo{username, hostname, platform, displays[], current_display, version, encoding, granted[]}`、`DisplayInfo{x, y, width, height, name, online, primary, scale, modes[], original}`、`Resolution{width, height, scale}`、`DisplayResolution{display, resolution}`、`DisplaysChanged{displays[], current_display, changed, failure}`、
`VideoFrame{codec, display, frame{data, key, pts_ms, seq}, width, height}` (**單幀 + seq**)、`VideoAck{display, seq}`、`RefreshVideo`、`SwitchDisplay`、`SupportedEncoding/SupportedDecoding`、
`MouseEvent{mask, x, y, modifiers, display}` (mask 低 3 bit = 類型、高位 = 按鈕，沿用 RustDesk)、`KeyEvent{down, press, oneof control_key|chr|unicode|seq, modifiers, mode MAP|TRANSLATE}`、`ControlKey` (照抄 RustDesk 清單)、
`TerminalAction{open|input|resize|signal|close|ack}`（控制端→主機，`Message` 19）、`TerminalResponse{opened|output|exit|error}`（主機→控制端，20）；`TerminalOpen{id, columns, rows}` 刻意不帶程式、參數、目錄或環境變數，`data` 一律 `bytes`；`Permission.PERM_TERMINAL = 5`、`ConnType.CONN_TERMINAL = 2`、
`CursorData{id, hotx, hoty, w, h, bgra}`、`CursorPosition`、`CursorId`、`AudioFormat`、`AudioFrame{opus, pts_ms}`、
`Clipboard{format TEXT|HTML|RTF|IMAGE_PNG, content, compressed, w, h}`、`MultiClipboards{items[], content_hash}`、`ChatMessage`、
檔案傳輸：`FileAction{oneof read_dir|all_files|send|receive|create|remove_dir|remove_file|rename|cancel|send_confirm}`、
`FileResponse{oneof dir|block{id, file_num, blk_id, data, compressed}|error|done|digest{…, transferred_size, is_resume, is_identical}}`、
`Permission{KEYBOARD|CLIPBOARD|AUDIO|FILE|RESTART}`、`PermissionInfo`、`SessionOptions{image_quality, custom_bitrate_kbps, custom_fps, show_remote_cursor, disable_audio/clipboard/keyboard, lock_after_session_end, supported_decoding}`、
`TestDelay{time_ms, from_controller, last_delay_ms, target_bitrate_kbps}`、`CloseReason`、`MessageBox`、`Misc{oneof chat|switch_display|permission_info|options|audio_format|close_reason|refresh_video|video_ack|restart_remote_device|supported_encoding|message_box|…|display_resolution|displays_changed}`；外層 `Message{oneof}`。

**`ipc.proto`**：`IpcMessage{oneof Hello{token} | GetConfig/SetConfig/ConfigSnapshot | GetId/IdChanged | SetPassword/PasswordAck | GetTempPassword |
ConnectionOpened | ApprovalRequest/ApprovalDecision | ConnectionClosed | PermissionChange | ChatIn/ChatOut | FileJobProgress | Sas | Ping/Pong | Close}`。

### 4.7 埠號

21114 HTTP API / 21115 TCP NAT 測試 / 21116 UDP+TCP rendezvous / 21117 TCP relay + **UDP 媒體中繼**（同一埠號） / 21118 TCP host 直連監聽 / 21119 UDP LAN 探索 (後期)。

21118 也是設備清單判斷「位址類型」設備是否在線的探測目標（`PeerPresence`，1.5 秒 TCP 連線）。21119 仍未使用：目前的區域網路功能只做「開關＋以 IP 連線」，不做自動發現。

---

## 5. 伺服器

### 5.1 `DeskPair.Rendezvous`

```
Program.cs (WebApplication + BackgroundServices)   RendezvousOptions.cs
Net/UdpListener.cs  Net/TcpListener.cs  Net/NatTestListener.cs  Net/TcpClientSession.cs
Core/RendezvousHandler.cs (純邏輯、可單元測試)  Core/PeerTable.cs  Core/PeerEntry.cs  Core/IdAllocator.cs
Core/PunchRegistry.cs  Core/RelaySelector.cs  Core/ServerKeys.cs
Persistence/IPeerStore.cs  SqlitePeerStore.cs  NullPeerStore.cs
Http/AdminEndpoints.cs   appsettings.json
```

- UDP：單一 dual-mode socket，單一接收迴圈 (`ReceiveFromAsync` 無配置多載)，`Channel` 驅動單一送出任務；Windows 上關閉 `SIO_UDP_CONNRESET`。
- TCP 21116 / 21115 各一個 accept 迴圈，每連線一個 `TcpClientSession` (FramedStream 16 KiB 上限、30 s 閒置)。
- `PeerTable`：`ConcurrentDictionary<string, PeerEntry>` 熱路徑，write-behind 每 5 s / 500 筆寫入 SQLite (`peers(id PK, uuid UNIQUE, pk, created, last_seen, version, info)`，手寫 SQL，WAL)。
- **ID 由伺服器配發**：9 位數隨機、UNIQUE 重試；同 uuid 永遠拿回同 ID (重灌保留)；uuid 不符 → `UUID_MISMATCH` → 客戶端清 ID 重註冊；`RegisterIdentity` 每 IP 10 次/分。
- 訊息處理表：`RegisterPeer` → 更新 lastSeen/udpEndpoint (無 pk 時 `request_identity`)；`PunchHoleRequest` → 離線/授權檢查 → `force_relay` 或任一方 symmetric ⇒ 產 uuid、送 host `RequestRelay`、回 controller `RelayResponse`；
  同公網 IP ⇒ 送 host `FetchLocalAddr`；否則登記 `PunchRegistry` (key = controller 公網位址，15 s 逾時) 並送 host `PunchHole`。
  `PunchHoleSent` (host 由新 TCP 送來) → `host_addr` = 該 TCP 的遠端位址，附 identity 回 controller `PunchHoleResponse`；`LocalAddr` → `is_local`；host 主動 `RelayResponse` → 轉發。
- `ServerKeys.Sign(PeerIdentity)` 每 peer 快取，pk 變更或 24 h 重簽。
- `RelayHealthMonitor`：每 `RelayHealthInterval`（預設 30 s）對每台 relay 取 `/api/stats`，記下最後一次成功的時間與負載。輪詢而非註冊 —— relay 無狀態，不該為了回報而長出對外呼叫與憑證。
- `RelaySelector`：先濾掉過期的（超過兩個間隔沒回應），再以來源 IP 的國別對到 `Region`，最後取負載最低者；負載 = `max(sessions/MaxSessions, bytesPerSecond/MaxBitrateKbps)`，上限為 0 表示不設限。全部滿載回 `SERVER_BUSY`（明確拒絕勝過讓所有人一起劣化）。回傳整串候選，client 依序嘗試。
  - **從來沒有任何一台回應過**時退回「全部都用」並印警告 —— 那幾乎必然是 `StatsAddress` 設錯，不該讓監測失誤變成全面拒絕連線。
  - host 在 symmetric NAT 下自願提供的 relay 位址，只有在本伺服器設定清單內才轉發。
- HTTP 21114：`GET /healthz`、`/api/stats`、`/api/peers/{id}`、`/api/online?ids=`，`Admin:ApiKey` bearer；`/metrics` 選用。`GET /key` 回公鑰，給自架伺服器的使用者手動取鑰；位址與公鑰由目錄發給用戶端的部署可以把 21114 對外關閉（見 `deploy/README.md` Firewall）。
- 設定 `Rendezvous:{UdpPort, TcpPort, NatTestPort, HttpPort, KeyPath, DatabasePath, RelayServers[](舊式字串清單), Relays[]{Address, StatsAddress, AdminKey, Region}, RelayHealthInterval, PeerOfflineAfterSeconds, KeepAliveSeconds, PunchPendingSeconds, RegisterRateLimitPerMinute}`；`KeyPath` 不存在時自動產生並印出 base64 SPKI。

### 5.2 `DeskPair.Relay`

```
Program.cs  RelayOptions.cs  Net/RelayListener.cs  Core/RelayPairing.cs  Core/RelaySession.cs  Core/RelayStats.cs  Http/AdminEndpoints.cs
```

- 21117 accept → 精確讀取首幀 `RequestRelay` (10 s 內)；授權 key 檢查；`uuid` 16..64 字元。
- `RelayPairing`：`ConcurrentDictionary<uuid, PendingPeer{Socket, TaskCompletionSource<Socket>}>`；第一個到達者等待 (30 s)，第二個到達 `TrySetResult`，由第一個的任務執行 `RelaySession.RunAsync`。
- `RelaySession`：兩個方向各一個任務，`ArrayPool` 64 KiB，`Socket.ReceiveAsync/SendAsync` 直接搬運 (不經 NetworkStream)，`Interlocked.Add` 計位元組，`n==0` → 對向 `Shutdown(Send)`；`PeriodicTimer` 10 s 檢查閒置 60 s。結束記錄 `{uuid 前 8 碼, idA, idB, duration, bytes 兩向, reason}`。
- 限制：`MaxSessions` 1000、`MaxPendingPerIp` 5、`MaxSessionsPerIp` 20、`PairingTimeout` 30 s、`IdleTimeout` 60 s；`MaxKbpsPerSession` token bucket 後期。每個 socket 在自己的執行緒上處理，所以 pending 名額是**先佔再登記**：原本先檢查、登記後才加一，同一位址同時到的一批連線可以一起溜過上限（`A_burst_from_one_address_cannot_slip_under_the_pending_limit_together`）。
- Docker：`mcr.microsoft.com/dotnet/aspnet:10.0` (Debian)，`InvariantGlobalization=true`；`EXPOSE 21117 21114`。

---

## 6. Session Core (`DeskPair.Core`)

```
Transport/  IPeerTransport (Kind: DirectTcp|Lan|PunchedTcp|Relay, Stream)、TcpPeerTransport、IServerChannelFactory (v1 NetworkStream，預留 SslStream+pin)
            RendezvousClient (UDP 註冊迴圈 + TCP 請求)、RelayClient、PeerConnector (controller 端：rendezvous → punch/relay 競速)、HostListener (21118)
            Nat/NatTypeDetector、Nat/TcpPuncher、Nat/SocketFactory
Session/    SessionMessagePump (單讀者 + 優先權寫入佇列 Control>Input>Video>Bulk；視訊為 bounded(8) DropOldest)
            MessageDispatcher<TContext> (UnionOneofCase → ISessionHandler；AllowedCases[state, connType] 資料驅動的範圍檢查)
            Host/  HostRuntime (程序層：identity、註冊、監聽、ServiceRegistry、IPC)、HostSession (狀態機 ~300 行)、HostSessionContext、
                   Auth/{PasswordVerifier, LoginFailureTracker, TemporaryPasswordManager, IConnectionApprover}、
                   Handlers/{Login, Input, Clipboard, FileTransfer, Chat, Options, Qos, VideoControl}Handler
            Controller/  ControllerSession、IControllerCallbacks (UI 實作)、Handlers/…
Services/   PublisherService (抽象：Subscribe/Unsubscribe、首位訂閱者啟動迴圈、末位停止硬體、SnapshotAsync 給新訂閱者)、
            VideoService(display)、AudioService、CursorService (30 Hz)、ClipboardService、ServiceRegistry
Qos/        VideoQosController (~250 行，移植 video_qos.rs 精髓)、RttEstimator
FileTransfer/  FileTransferEngine、TransferJob/ReadJob/WriteJob、IFileSystem/LocalFileSystem/InMemoryFileSystem、PathGuard、ResumeSidecar
Clipboard/  ClipboardSync (echo 抑制：lastAppliedHash / lastSentHash、300 ms debounce、16 MiB 上限)
Config/     HostConfig、ControllerConfig、ISecretStore、IMachineIdProvider
Ipc/        IpcServer、IpcClient (4-byte LE + protobuf `ipc.proto`；一次性 token + 對端程序驗證)
Testing/    FakeScreenCapturer (漸層動畫)、FakeVideoEncoder (raw/RLE)、PassthroughDecoder、FakeInputInjector、LoopbackTransportPair
```

### 6.1 Host 狀態機

```
Handshake → Authenticating → (AwaitingApproval) → Authorized → Closing → Closed
Authorize(): 組 PeerInfo (IDisplayEnumerator、IVideoEncoderFactory.Supported、有效權限) → LoginResponse
             → Misc.permission_info ×N → Misc.options → Misc.switch_display
             → 依 conn_type/權限訂閱服務 (REMOTE: Video(current)+Cursor+Audio+Clipboard；FILE_TRANSFER: 只用 FileTransferEngine)
             → 啟動 TestDelay 1 s 計時器 → 通知 CM
Closing:     退訂所有服務、取消檔案工作、IInputInjector.ReleaseAll()、盡力送 CloseReason (2 s)、依選項鎖定螢幕
```

### 6.2 權限模型

`PermissionSet` = Host 政策 (`IHostPolicy` 設定) ∧ Session 請求 (`SessionOptions.disable_*`) ∧ CM 即時切換；變更即重算並推 `PermissionInfo`，服務隨之訂閱/退訂。

### 6.3 VideoService 迴圈 (移植 `video_service.rs` 精髓)

`spf = QoS.SecondsPerFrame` → `TryCapture(spf)` 無變化則略過 (但每 1 s 強制送一幀) → 編碼 → `BroadcastVideo(seq++)` → 訂閱者 `TryPublishVideo` 回報丟幀者下一輪 `RequestKeyframe()` →
每 5 秒的統計會把擷取時間拆成「等待桌面更新」與「GPU 回讀」：回讀約 3–4 ms（1440p），等待才是幀率的上限。實測遠端桌面工作階段用的 Microsoft Remote Display Adapter 只有 32 Hz、實際每秒約送 16 張，被控端在 RDP 內執行時拖動只會有 15–20 fps，與擷取程式無關。
HW 編碼器 (`IsLatencyFree=false`) 畫面靜止時重送上一幀最多 10 次 → 睡到 spf。`RefreshVideo`/新訂閱者 = 要求 keyframe (不重建服務)。無訂閱者時擷取與編碼完全停止。
Windows 的硬體編碼器 (MF 非同步 MFT) 每次只等自己的輸出 6 ms，來不及的那張留在編碼器裡。所以迴圈每一輪開頭先 `TryCollect()` 把它取出送走，而不是等下一次送入時才順便帶出來：lossless tiles 模式下靜止的畫面不會再送入任何東西，一張被要求的 keyframe（新開的螢幕、壞掉要重整的畫面）就會一直卡到那個螢幕上有東西動為止。實測 2560×1440 的 keyframe 在 NVENC 上常超過 6 ms，兩個螢幕同時要時更是如此；`LateEncoderTests` 以假編碼器的 `OneFrameLate` 模式重現。
QoS：每連線 `UserDelay` (2 樣本)，fps ∈ [5, 使用者上限 30 預設/120 最高]、bitrate ratio ∈ [0.2, 1.0] × `BaseBitrate(w,h)` 表；每 3 s 平均延遲 >150 ms 降、持續 <150 ms 逐步回復；CBR 下先降 bitrate 再降 fps。
Controller 端：解碼佇列 > 解碼 fps/2 → 透過 `SessionOptions.custom_fps` 要求降 fps。

### 6.4 由控制端改變被控端的螢幕解析度

`DisplayInfo.modes[]` 是被控端顯示器**可切換的模式清單**（空 = 這台不能從遠端改：Wayland、DRM 常駐程式、RDP 主機、沒有 switcher 的平台），`original` 只在有人改過之後出現。
控制端送 `Misc.display_resolution{display, resolution}`（`resolution` 未設 = 還原原始）；被控端 `HostMediaModule.SetResolutionAsync` 檢查鍵盤權限（只能看的人不能改別人的桌面）→
`DisplayModeService.ChangeAsync`：只接受清單內的模式、**第一次改才記下原始值**（之後不論誰改幾次，還原都回到工作階段前的模式）、`IDisplayModeSwitcher.TrySetMode` 之後每 100 ms 讀
`IDisplayEnumerator` 最多 2 s 確認描述子真的變了（逾時就試著還原並回報失敗）→ 重啟所有在跑的 `VideoService` 並對每個訂閱者 `Qos.ResetStream`（Windows 改一台的模式會移動其他螢幕的原點）→
對每個 `ConnRemote` session 廣播 `Misc.displays_changed{displays, current_display, changed}`；被拒時只對請求者送 `displays_changed{failure}`。多人連線：後寫者勝、所有人都被告知、只有一個原始值。
最後一個遠端 session 關閉與引擎停止時 `RestoreAllAsync` 還原。被控端當掉無法由我們還原，靠平台不持久化：Windows `ChangeDisplaySettingsEx` 不帶 `CDS_UPDATEREGISTRY`（登入即自癒）、
macOS `CGCompleteDisplayConfiguration(kCGConfigureForSession)`（登出即還原）、Linux `xrandr --output NAME --mode WxH`（X 重啟即忘）。
平台實作：`WindowsDisplayModes`（`EnumDisplaySettingsEx`／`ChangeDisplaySettingsEx`，先 `CDS_TEST`；`SM_REMOTESESSION` 回空）、`MacDisplayModes`（shim `fd_display_modes_get`／`fd_display_mode_set`，
`kCGDisplayShowDuplicateLowResolutionModes` 讓 Retina 的 HiDPI 變體以 `scale=2` 出現；`acquireShared` 發現尺寸不同時對共用的 SCK 串流 `updateConfiguration`，否則畫面會被壓到舊尺寸）、
`X11DisplayModes`（libXrandr 列舉 output 的 mode 清單、xrandr 工具切換）。
控制端：桌面工具列 Display 旁的解析度下拉（第 0 項「原始」，HiDPI 模式顯示成點數加倍率如 `1920×1080 (2x)`）、手機 SCREEN 面板的「解析度」列；每台電腦每個顯示器記住選過的模式
（桌面 `DesktopConfig.PeerResolutions`、手機 `AppSettings.peerResolutions`，都是本機設定、不進通訊錄），重連登入後若主機仍提供且尚未在該模式就自動重套一次。
沒有虛擬顯示器驅動就不能自由輸入寬高；那是之後的事。

**顯示器編號的不變式**：`DisplayDescriptor.Index` 就是它在 `GetDisplays()` 清單裡的位置。這個數字會走上每一個影像封包的 `display` 位元組、`SwitchDisplay`、`MouseEvent.Display`，
而被控端全部用**位置**去解；列舉器若先配號再把主螢幕排到前面，被控端會把第 2 個螢幕編成「display 0」送出，控制端的組裝器卻被告知只收 stream 1，結果是黑畫面而不是例外。
所以每個列舉器都經過 `DisplayOrdering.PrimaryFirst`（主螢幕在前、其餘照發現順序、`Index` 重新投影成位置），原生識別留在 `Name` 與 `AdapterLuid`；`DisplayOrdering.IsConsistent` 是各平台測試共用的斷言。

**拓樸變動**：`IDisplayEnumerator.DisplaysChanged` 目前只有 Windows 會發（`WindowsDisplayEnumerator` 在第一個訂閱者出現時開一個從不顯示的頂層視窗收 `WM_DISPLAYCHANGE`；
message-only 視窗收不到廣播）。`HostMediaModule` 收到後等 500 ms 沒有新通知（`DisplayChangeSettle`：Windows 每碰一台螢幕就送一次）再重新列舉，
與上次動作過的簽章（名稱、位置、尺寸、倍率、旋轉、主螢幕）比對——相同就是自己改解析度的回音，什麼都不做；不同才把卡在已消失螢幕上的 viewer 切回主螢幕（先送 `SwitchDisplay` 讓組裝器換串流）、
丟掉沒有螢幕可擷取的 `VideoService`、其餘串流以新幾何重啟，最後廣播 `displays_changed{changed = -1}`。X11 的 `RRScreenChangeNotify` 與 macOS 的 `CGDisplayRegisterReconfigurationCallback` 尚未接。
**2026-09-25 起訂閱依名字搬過去**（見下節）：拔掉中間一台時，後面的編號全部往前挪一格，照編號留著的訂閱會默默變成隔壁那台，所以每個 viewer 的集合與 focus 以 `Name`
（Windows 裝置路徑、X11 輸出名、macOS 帶顯示器 ID 的名字，都唯一）對到新編號，消失的丟掉；什麼都不剩或 focus 消失的落到主螢幕。只看一台的舊 viewer 也一樣，所以「看著第 3 台、第 2 台被拔」
現在會跟著那台走到新的編號，而不是看到別台。

### 6.4b 多螢幕同時顯示（F，2026-09-25）

**協定**（只加欄位）：`Misc.display_subscription = 18` 的 `DisplaySubscription{displays, focus, failure}`、`PeerInfo.multi_display = 9`。控制端**每次送整個集合**，不送增減——
遺失或重送一則增減會讓兩端永久不一致，整個集合每送一次都是重新對齊。主機只回給送過它的 viewer，而且**永遠照舊送 `SwitchDisplay{focus}`**，所以不懂這個訊息的 viewer
（舊版桌面、手機）看不到任何變化；舊主機收到未知的 `Misc` 會丟掉，控制端靠 `multi_display` 決定要不要提供。

**主機**：`HostMediaModule` 每個連線一份 `Subscription{Displays, Focus, SpeaksSubscription}`；`SwitchDisplay` 是「只有一個元素的集合」、答法照舊（沒變就不回）。
套用時**先退訂再訂閱**（一台換一台不會短暫付兩份編碼器）、`Qos.ResetStream` 離開的螢幕、`context.CurrentDisplay = focus`。拒絕時整個集合原樣保留並回 `failure`：
不存在的螢幕、超過 `HostConfig.MaxDisplaysPerViewer`（預設 2）、所有 viewer 合起來超過 `MaxConcurrentStreams`（預設 4，一張消費級 GPU 硬體編碼器同時工作階段的安全值）；兩者即時生效、不需重啟。
**還原解析度改以已授權的遠端 session 數為準**（`_remoteSessions`），不看訂閱：訂到零個螢幕但還連著（只傳檔、聊天）的 viewer 仍然在場，照舊的算法會在它的工作階段中途把解析度彈回去。
`SessionScope` 把它歸在 `RemoteOnlyMisc`。

**控制端**：`ControllerSession.SubscribeDisplaysAsync(displays, focus)`；送過之後 `_speaksSubscription` 為真，主機的 `SwitchDisplay` 只代表 focus，**不得再收窄 UDP 的串流集合**——
這是相容層唯一會變成黑畫面的地方，`MultiDisplayTests.Two_displays_come_over_udp_and_the_focus_does_not_narrow_them` 以拿掉防護會失敗的方式釘住。
收到確認才 `Streams.Expect(集合)`；確認之後才開的 UDP 通道開通時補上。tile 水位改成每個螢幕一份（`ConcurrentDictionary`），因為每個螢幕的序號各自從 1 起算。
`IControllerCallbacks.OnDisplaySubscription` 有預設實作。PeerCli：`:subscribe 0,1 [focus]`，每 5 秒的統計行分螢幕列出封包數。

**QoS**：`TargetBitrateKbps(display, w, h)`：一條連線的預算（GCC 估計、自訂碼率）依像素分給它看的每個螢幕，focus 乘 `FocusWeight` 1.5；「畫面需要多少」的
`BaseBitrateKbps`／`MaxBitrateKbps` 不分攤。探測上限 `BitrateCeilingKbpsFor(連線)` 是該連線所有螢幕上限的總和（原本是全域一個值，兩條串流會輪流覆寫）。
計畫原本要把 in-flight 上限除以串流數，**刻意不做**：每條串流在路上的影格數是「幀率 × RTT」，與有幾條串流共用這條線無關，除下去只會讓兩個螢幕時誤判壅塞；
真的塞車時 RTT 與每條串流自己的 backlog 會反映出來。`_fps`／`_ratio`／`_probe`／`_tier` 仍是全域：忙的那個螢幕造成的壅塞會讓安靜的那個也降速，本來跨 viewer 就是如此。

**桌面（F3）**：分頁模型是一台機器一個分頁，而遠端螢幕不是工作階段、是它的一個視圖，所以**分頁顯示一個螢幕，其餘每個螢幕一個 `RemoteScreenWindow`**（`RemoteScreenViewModel` 持有分頁的
`RemoteSessionViewModel`，不持有 `ControllerSession`）。工具列顯示器下拉旁的「⧉」列出還沒開的螢幕（主機 `multi_display` 且超過一個螢幕才出現；選單項目在 code-behind 開啟時建立，
不用 XAML 模板——popup 裡的項目要綁回分頁需要主題查找與祖先綁定）。螢幕視窗只有細長一條：縮放、這個螢幕的重新整理、回到分頁；畫質、聲音、錄影是工作階段狀態，留在分頁。
- **畫面分派**：`OnVideoFrame(display)` 在送過訂閱後依螢幕送到分頁或對應視窗（`_screenViews` 整份替換、影像執行緒不上鎖讀）；沒送過訂閱時照舊全部進分頁。
  游標形狀與位置送給每一個畫面，各自扣掉自己螢幕的原點（`RemoteDisplayView.Origin`）、只畫落在自己範圍內的——**原本第二個螢幕上的遠端游標就畫錯位置**（主機報的是桌面座標），這裡一併修好。
- **輸入**不需要新管線：每個畫面把自己的 `DisplayIndex` 蓋進 `MouseEvent.Display`；鍵盤跟著主機上最後一次點擊走。
- **focus**：分頁所在的視窗或某個螢幕視窗被帶到前面時送新的訂閱（focus 換成它），主機把較大的碼率份額給它；只有分頁時什麼都不送。
- **關閉規則**：關螢幕視窗＝退訂那個螢幕；分頁換到某個已開視窗的螢幕時那個視窗關掉（一個螢幕只在一個地方）；關分頁先關它的螢幕視窗；重連後在 `OnAuthorizedAsync` 重播訂閱。
- **依名字配對**（`DisplayWindowPlan`，純函式、有單元測試）：`DisplaySubscription.names = 4`（新訊息自己的欄位）讓主機的回覆同時帶編號與名字；拔螢幕時這則回覆可能比新的清單先到，
  照編號配對會在那一刻誤關或顯示錯的螢幕。回覆裡沒有的名字對應的視窗關掉並提示；被拒時回覆帶著保留的集合，剛開的視窗就關掉並顯示原因。分頁送過訂閱後以名字保留自己的螢幕，不跟著主機的「目前螢幕」（那是 focus）。
- 還沒做：螢幕視窗自己的解析度選單（在分頁切到那個螢幕即可改）；Linux／macOS 的拓樸變動通知（D 節）。

**macOS（F4）**：`capture.m` 原本一個行程只有一條 `SCStream`，而且用 `content.displays[0]`、把傳進來的 `displayId` 丟掉——**Mac 上看第二個螢幕看到的其實是第一個**；
F2 之後兩個擷取器若共用它，還會搶同一個「已取走」計數、對同一個像素緩衝各自解鎖與釋放。現在以 `CGDirectDisplayID` 為鍵，每個螢幕各自的串流、佇列、最新影格與引用計數；
**音訊只掛在主螢幕那條**（`fd_audio_capture_create` 取的是主螢幕的串流，沒有影像擷取器時也會建立它），否則每個螢幕的串流都會送一份同樣的系統聲音。
尺寸或游標設定改變時對既有串流 `updateConfiguration`，不拆掉別人還握著的串流。鎖改用 `dispatch_once` 建立（舊的 `if (gLock == nil)` 兩個擷取器同時啟動會建出兩把）。
**只驗證到編譯**：SSH 啟動的行程沒有螢幕錄製權限（連 `SCShareableContent` 都拿不到），替換已安裝 App 的 dylib 會改變簽章、讓 TCC 失效；要在下一個簽章過的版本上實測。

### 6.4c 虛擬顯示器（G，2026-09-25 起）

**協定**：`DisplayInfo.virtual_display = 11`（主機應 viewer 要求插上的、可以再拔掉的螢幕）、`Misc.virtual_display_request = 19`
（`VD_ADD` 可帶起始尺寸、`VD_REMOVE` 帶螢幕編號）。回覆沿用 `displays_changed`：新清單給所有人（`changed` 是新插上的那個），被拒時只給請求者並帶原因——與改解析度同一種答法。
權限同改解析度：要 `PERM_KEYBOARD`（只能看的人不能改別人的桌面），另外要擁有者開 `HostConfig.AllowVirtualDisplay`（預設關、即時生效）。`SessionScope` 歸 `RemoteOnlyMisc`。

**抽象**（`Platform.Abstractions/Capture/VirtualDisplays.cs`）：`IVirtualDisplayProvider`（新增／移除一台不存在的螢幕，有生命週期的拓樸變動）與
`IArbitraryModeSink`（教一台螢幕一個它沒宣告過的尺寸）。分成兩個是因為 Linux 只做得到後者（`xrandr --newmode`）、Windows 兩者都靠虛擬顯示器驅動、macOS 都沒有。
`DisplayModeService` 的接縫：要求的尺寸不在清單上、而 `CanTeach` 為真時先 `TeachAsync`，重讀清單後**原封不動**走原本的流程（記原始值、確認、失敗還原）；`RestoreAllAsync` 最後 `ForgetTaughtModesAsync`。

**主機**（`HostMediaModule`）：新增後**等列舉器真的列出它**（最多 5 秒）才算數，移除後等它從清單消失；接著走與平台拓樸通知同一條 `FollowDisplaysAsync`
（`_topologyGate` 一次一個；平台自己的通知晚到時簽章相同、什麼都不做）。一次最多 4 台。**什麼時候插**：只有被要求時；唯一例外是**完全沒有螢幕的主機**在工作階段授權時自動插一台
（否則那個工作階段沒有東西可看，仍受 `AllowVirtualDisplay` 管）。**什麼時候拔**：最後一個已授權的遠端 session 離開時（與還原解析度同一個鉤子）以及引擎停止時全部拔掉。
測試用 `Core.Testing.FakeVirtualDisplays`：像真的驅動一樣把新螢幕接在清單尾端並發出變動、移除後重新編號。PeerCli：`:vdisplay add [WxH]`、`:vdisplay remove N`。

**Linux 教尺寸**（G2）：`X11ModeTeacher`（`IArbitraryModeSink`）以 C# 算的 CVT-RB 時序（`Cvt`，逐字對 `cvt` 測過）`xrandr --newmode deskpair-WxH` ＋ `--addmode`，之後照一般改解析度流程；`RestoreAllAsync` 還原後先 `--delmode` 再 `--rmmode`。`X11DisplayModes` 切換時依該輸出上這個尺寸的模式**名字**下 `--mode`（xrandr 不依尺寸找）。只限 X11；Wayland 與 DRM daemon 不教。沒有 GPU 的機器用 Xorg＋dummy 驅動得到一個真 X 螢幕，見 `unattended-linux.md` I。
**Windows 驅動**（G3，路線 A）：直接散布 VDD（MIT）官方、由 SignPath Foundation 簽的版本，`tools/fetch-vdd.ps1` 以網址＋SHA-256 釘死，`publish.ps1` 放進發佈目錄的 `vdd\`；來龍去脈與安裝細節見 `native/vdd/README.md`。
`WindowsVirtualDisplays`（`IVirtualDisplayProvider`）：驅動只在裝置啟動時讀一次 `vdd_settings.xml`、`RELOAD_DRIVER` 不重讀，所以不用它的 pipe。安裝時照 nefcon／devcon 的做法建一個根列舉裝置（硬體 ID `MttVDD`），裝好驅動就 `CM_Disable_DevNode(PERSIST)`；要改螢幕數就**停用 → 寫檔 → 啟用**。每次啟用所有虛擬螢幕都會重插一次而且改名（`DISPLAY10` → `DISPLAY11`），所以移除拿掉的是最後一台、新增會讓其他虛擬螢幕的串流重啟一次，而已經接上的螢幕**不教新尺寸**（Windows 不實作 `IArbitraryModeSink`）；新增時要求的尺寸先寫進檔案，Windows 若記得別的尺寸，`HostMediaModule` 在新螢幕出現後照一般的改解析度流程設一次。哪些 `\\.\DISPLAYn` 是我們的：`EnumDisplayDevices` 的 adapter `DeviceID` 為 `MttVDD`。啟用／停用要系統管理員權限，所以只有服務的引擎能用，App 模式如實回報不可用；引擎當掉時裝置維持啟用，下一個引擎啟動時先停用。**不用 `SwDeviceCreate`**：它對 LocalSystem 一律回 `0x8007007E`（工作階段 0 或 1、任何裝置都一樣，2026-09-25 以服務的終端機實測）。安裝／移除是 `--install-virtual-display`／`--remove-virtual-display`（提權）：設定目錄 `%ProgramData%\Sunllo\DeskPair\vdd`（SYSTEM／Administrators 可寫、其他人可讀）、`VDDPATH` 指過去（已被別人指走就拒絕）、暫時信任發行者時 `SetupCopyOEMInf` 放進 driver store、裝置 instance ID 與 `oemNN.inf` 記在安裝紀錄；移除時 `DIF_REMOVE` 裝置、`SetupUninstallOEMInf`。

### 6.4 檔案傳輸

128 KiB block；每 session 一個 `FileTransferEngine`；`ReadJob` 每檔先送 `Digest` 等 `SendConfirm{skip|offset_blk}` 再串流 block → `Done`；
`WriteJob` 以 `PathGuard` 拒絕 `..`/絕對路徑/保留名，同 size+mtime → skip，`.sunllo-part` + JSON sidecar → 續傳，否則 `IFileTransferPolicy` (覆寫/略過/詢問)；`blk_id` 連續性檢查；每 500 ms `TransferJobSnapshot` 給 UI。
**Windows 上 SYSTEM 端不直接讀寫使用者檔案**：`FileTransferHandler` 透過 IPC 委託 CM 程序 (使用者身分) 執行 `IFileSystem`。

---

### 6.5 混合分區無損編碼（RDP-GFX 式漸進精修）

整張畫面只走 H.264 4:2:0 時，文字邊緣永遠有損、且清晰度和頻寬綁死。仿照 RDP-GFX「依區塊、依內容選編碼」，`VideoService` 現在每幀做三選一：

| 情況 | 送出 | 控制端 |
|---|---|---|
| 沒有格子變化 | 什麼都不送；若有「待精修」格且畫面已靜止 ≥ `RefinementDelay(tier)` → `TileUpdate(refinement=true)` | 逐格貼上 |
| 變化格 ≤ `TileThresholdFraction(tier)` × 總格數，且位元組在每幀預算內 | `TileUpdate(refinement=false)`（無損） | 逐格貼上 |
| 其他（捲動、拖曳、影片） | `VideoFrame`（H.264，既有路徑），所有格標記「有損，待精修」 | 整幀覆蓋 |

- **格子編碼** `Core/Video/TileCodec.cs`：64×64 BGRA → 丟 alpha、平面化 B/G/R、左差分 → Brotli（quality 3, window 16）。文字格約 16×，純色格 < 20 bytes；2560×1440 全畫面約 65 ms（單核）。
- **變化偵測** `Core/Video/ChangeDetector.cs`：每格 `XxHash3`；`CaptureFrame.DirtyRects`（Windows 由 Desktop Duplication 的 dirty/move rects 填入）有值時只重算相交的格，否則整幀掃描。另記錄每格「最後一次無損送出的雜湊」→ `PendingRefinement` / `RefinementCandidates`（游標附近優先）。
- **序號與流控**：`TileUpdate.seq` 與 `VideoFrame` 共用 `_seq`，控制端同樣回 `VideoAck`，所以 `VideoQosController` 的 in-flight / 壅塞判斷原樣涵蓋精修流量；精修每 tick 只送 `BytesPerSecondBudget × 幀間隔` 對應的格數。
- **控制端合成** `Core/Video/FrameCompositor.cs`：`ApplyVideo` 整幀覆蓋、`ApplyTiles` 逐格貼上；尺寸不符則丟棄並送 `RefreshVideo`。`ControllerSession` 在 `SupportedDecoding.tiles=true` 時宣告支援；主機以 `IServiceSubscriber.SupportsLosslessTiles` 過濾，舊控制端只拿 H.264。
- **編碼協商** `Core/Services/CodecNegotiation.cs`：控制端的 `SupportedDecoding` 由 `IVideoDecoderFactory.Probe()` 填（以前寫死 `h264 = true`），主機端的 `PeerInfo.SupportedEncoding` 由 `IVideoEncoderFactory.Probe()` 填（以前送空的）。主機選的是「自己編得了」∩「**每一個**現有訂閱者都解得了」，优先序為主機偏好（`HostConfig.CodecPreference`，`auto` 代表沒意見）→ 控制端請求（`SupportedDecoding.prefer`）→ `H264 > H265 > AV1 > VP9 > VP8`。一條串流只有一個編碼器，所以**最窄的那個訂閱者決定整條串流**；中途加入的訂閱者若讀不懂現行編碼，`PublisherService.RestartAsync()` 重啟擷取迴圈換成大家都讀得懂的（只收窄、不回升，避免每次有人離線就重啟）。
- **鏈路分級** `VideoQosController.LinkTier`（每秒依 RTT 平均與 ack backlog 重評，升級一次一階、降級立即）：

| Tier | 條件 | H.264 位元率上探上限 | 直接無損門檻 | 精修延遲 |
|---|---|---|---|---|
| Excellent | RTT < 30 ms、backlog ≤ 1、ratio = 1 | 4.5× 表值 | 60% 格 | 0 |
| Good | RTT < 80 ms、backlog ≤ 2 | 2× | 20% | 300 ms |
| Fair | RTT < 200 ms、backlog ≤ MaxInFlight | 1× | 8% | 1 s |
| Poor | 其他 | 1×（並降 ratio/fps） | 0（只精修） | 2 s |

  （TCP 路徑）無壅塞連續達標時 `_probe` ×1.15（上限 1.5 倍，2026-09 起不再探測到 4.5 倍——數秒內衝到 26–29 Mbps 是頓挫主因之一），一出現壅塞先退回 ×0.6 再動 `_ratio`。UDP 路徑改由 GCC 決定碼率（§6.6）。`ImageQuality` 只縮放上限（低 0.5 / 平衡 1 / 最佳 1.5），`IqLow` 或 `SessionOptions.lossless_refinement=BO_NO` 關閉精修。
- **動態區域偵測**：`ChangeDetector` 為每格保留活動量（每次變化 +4、每個 tick −1，≥ 6 視為「動態」）。含有動態格的幀一律走 H.264，動態格在停止前也不精修——播放中的影片視窗即使只佔 4 格也不會被當成文字送無損（無損 Brotli 對影片內容幾乎壓不動，會把頻寬與 CPU 一起吃光）。
- **鏈路分級看「排隊延遲」而非絕對 RTT**：以最近 30 個樣本的最小 RTT 為基準，平均 RTT 超出基準的部分才是壅塞；穩定 120 ms 的跨國線路一樣可以是 Excellent。`IsCongested` 的未 ack 上限 = max(6, fps × RTT × 1.5 + 2)，60 fps 配 150 ms RTT 不會被誤判壅塞。絕對 RTT > 300 ms 或 ≥ 1 s 才直接視為 Poor。
- **幀率與編碼器**：預設 60 fps（QoS 壅塞時往下降），Media Foundation 編碼器改 High profile（8×8 transform + CABAC，同位元率下桌面文字更銳利）。
- **顯示**：`RemoteDisplayView` 用 HighQuality 內插，「縮放」模式放得下就 1:1、放不下才縮小（不放大），確保無損像素不被再取樣；狀態列顯示影片 Mb/s 與無損 KB/s。控制端有 3 幀的抖動緩衝（`SmoothPlayback`）：以到達間隔的移動平均排定呈現時間，把網路的忽快忽慢攤平成穩定的動態；每幀都是完整畫面，落後時直接跳過最舊的一幀。

**2026-09 修訂：單一時間線。** 實測發現影片走 UDP、格子走 TCP 時，只要掉一張影片幀，格子就會貼到舊底圖上（「雪花格子」），而且動態時混送格子讓整體順暢度不如純影片。現行規則：
- 任何變化一律走影片；無損格子只做「精修」，條件是畫面與影片串流都靜止 ≥ 500 ms（`VideoService.RefinementQuietTime`），且每個控制端都已確認收到最後一張影片幀、參考鏈完整（`VideoQosController.ViewerCaughtUp`：UDP 以回饋的最高可解碼 seq 與放棄幀計數判斷，放棄後需等之後的關鍵幀被確認）。
- 精修以焦點列為中心整列展開，每輪帶 `TileUpdate.pass`；控制端整輪完成（`pass_complete`）或累積 150 ms 才呈現一次。
- 控制端在等待關鍵幀或 UDP 參考鏈斷裂時拒收格子（`TilesRejectedBrokenReference`）並要求關鍵幀；解碼不顯示的幀前先 `FrameCompositor.DetachFromDecoder()`，避免解碼器覆寫被借用的緩衝。
- 上面表格中「變化格 ≤ 門檻 → 無損格子」與依 Tier 的門檻/精修延遲已不再使用。

### 6.6 UDP 媒體通道（FEC、部分可靠）

TCP 上一個封包遺失會讓後面所有幀一起等重傳（head-of-line blocking），這是跨外網看影片不順的根本原因之一。授權完成後主機會另外協商一條 **UDP 媒體通道**，只承載 H.264 影片；控制、輸入、剪貼簿、檔案、`TileUpdate`、`RefreshVideo` 全部留在 TCP。5 秒內建不起來或中途死掉就無感回到 TCP。

- **協商**（`Misc.media_offer/answer/ready/close`，`LoginRequest.media` 宣告能力）：主機綁 UDP socket → 蒐集候選位址（`CandidateGatherer`：本機介面 LOCAL、從媒體 socket 向 rendezvous 送 `TestNatRequest` 得到的公網映射 REFLEXIVE、本 session 的中繼伺服器 RELAY）→ 送 offer → 控制端回 answer 帶自己的候選 → 雙方同時對所有候選送加密 `Bind`，收到 `BindAck` 即驗證（等同 ICE 的同時探測，cone NAT 因此打洞）→ 各自送 `media_ready`，雙方都 ready 才開始送影片。1 秒內優先度高的路徑（LOCAL 300 > REFLEXIVE 200 > RELAY 100）可取代低的。
- **金鑰**：`Handshake.Derive` 從同一個 HKDF PRK 多推 4 段（`sunllo/v1/media/*`），與 TCP 金鑰獨立；每個封包 AES-256-GCM，nonce = 4 bytes 前綴 ‖ 明文 `pkt_seq`，明文通用標頭當 AAD，1024 格滑動視窗防重放（`Protocol/Media/MediaPacket.cs`）。
- **封包**：≤ 1200 bytes = 通用標頭 12 + 分片標頭 32（stream/codec/block/frame_seq/pts/shard index/k/m/長度/寬高）+ 酬載 1140 + tag 16。`frame_seq` 與 `VideoFrame.seq`/`TileUpdate.seq` 同一序號空間。
- **FEC**：Reed-Solomon GF(256)（Cauchy 矩陣，AVX2 split-nibble 乘法，`Core/Transport/Udp/Fec/`）；每 block k ≤ 128。`FecPlanner` 依控制端回報的丟包率（含 1.5× 餘裕）用二項分布挑最小的 m，使「掉超過 m 個」的機率低於 1e-3（關鍵幀 1e-4），夾在 10%–50%（Sunshine 預設固定 20%；我們保留 10% 底線並依丟包自適應）：小幀需要的比例比大幀高，固定比例兩邊都會錯。實測 8% 隨機丟包下 198/198 幀全部復原、0 次關鍵幀請求。
- **送端**：`MediaPacketizer` 切片 + 編碼，`PacedSender` 把一幀的封包攤在 `min(0.7 × 幀間隔, 12 ms)` 內送出（不 burst）；大幀（關鍵幀）以 pacing rate = GCC 目標 × 2.5（libwebrtc 預設 pacing factor，且不低於實際產出速率 × 1.25）攤開、最多 100 ms。排隊超過 150 ms 的幀整幀丟棄，但**不再回報失敗、不要求關鍵幀**：控制端看到參考鏈斷裂才以限速的 `RefreshVideo` 要求，避免同一次遺失觸發兩張關鍵幀。
- **頻寬估算（GCC）**：結構照 draft-ietf-rmcat-gcc 與 libwebrtc / Pion `interceptor/gcc`，純邏輯在 `Core/Qos/Gcc/`。控制端記錄每個通過驗證封包的到達時間，每 50 ms 送 `TransportFeedback`（媒體封包型別 8：起始 `pkt_seq`、筆數、接收位元圖、以 250 µs 為單位的 zig-zag varint 到達間隔，類似 transport-cc）；主機以環形緩衝（4096 格）記住每個封包（影片與控制）的送出時間與大小，配對後交給 `GccController`：
  - `InterArrival` 把 5 ms 內送出的封包分組，算組間「到達間隔 − 送出間隔」；`TrendlineEstimator` 對最近 20 組的平滑累積延遲做線性回歸，斜率 × 增益與自適應門檻（k_up 0.0087 / k_down 0.039，6–600）比較，判定 overuse / normal / underuse。
  - `AckedBitrateEstimator`：500 ms 視窗內實際送達的位元率。
  - `AimdRateControl`：normal 每秒 ×1.08（曾在 overuse 時量到的鏈路容量附近改為每秒 +5% 加性），但不超過 1.5 × 送達速率——**也不因送得少而下修**（靜止桌面不代表鏈路變窄，下次畫面動起來仍從原估算開始）；overuse 降到 0.85 × 送達速率（送達遠低於目標時每次最多減半）；underuse 保持。
  - `LossBasedBwe`：每累積 ≥ 100 個封包算一次丟包率，< 2% 可每秒 ×1.08、2–10% 保持、> 10% 降為 `送達速率 × (1 − 0.5 × loss)`（以送達速率為基準，overshoot 後的一波丟包不會與延遲估算的下修疊乘）。
  - **頻寬探測**（`UdpMediaChannel.MaybeProbe`、`ProbeBitrateEstimator`，照 libwebrtc ProbeController）：桌面大部分時間是靜止的，只靠送達速率 GCC 永遠長不上去（實測 1440p 一直停在 7 Mbps，拖動視窗明顯糊）。通道就緒後先送 30 ms 的加密補位封包（媒體封包型別 9）叢集，速率 2 × 目前估算；分別量送出與接收速率，接收明顯低於送出代表鏈路在約 0.95 × 接收速率飽和。成功（實測 ≥ 探測速率的 70%、且封包沒被截掉）就立刻再探 2 倍，直到上限；反之視為鏈路撐不住這個速率，實測值就是鏈路真正能送的量，可以直接**下修**估算（不低於目前已達成的送達速率），省掉慢速線路開頭數秒的超送。探測自身的丟包不計入丟包估算（那是探測的結果，不是影片送太快），反過來整串到齊的探測會把丟包估算直接提高到實測值（建線瞬間關鍵幀造成的一次丟包，原本要用每秒 8% 爬 30 秒才解除）；之後在送得少（送達 < 目標一半）、無 overuse、丟包 < 2% 時每 5 秒探一次（失敗退避 15 秒）。探測只會調高延遲估算，下修仍交給偵測器。上限 = `VideoQosController.BitrateCeilingKbps`（解析度 × 畫質上限，或自訂碼率），由主機在每次回饋時告知通道。
  - 目標 = min(延遲估算, 丟包估算)，起始 6.5 Mbps、下限 1 Mbps。`VideoQosController.ReportBandwidth` 讓 UDP 觀看者以 GCC 取代 RTT 探測；上限 = `像素 × 幀率 × 0.35 bit × 畫質倍率`（`MotionBitsPerPixel`，夾在 1 Mbps–200 Mbps）。0.35 bit/pixel 來自實測：1440p 拖動視窗在每幀約 172 KB 時看起來正確。同一個標準換算到各解析度與幀率：1080p30 約 22 Mbps、1440p30 約 39 Mbps、1440p60 約 77 Mbps、4K60 到上限 200 Mbps。幀率或解析度一改，探測上限就跟著改；同時有 TCP 觀看者時取兩者較小值。GCC 在工作時 RTT/丟包只在碼率已貼底（≤ 1.2 Mbps）時才降幀率。
  - 模擬驗證：8 Mbps / 100 ms drop-tail 鏈路上 10 s 後平均落在容量 60–100%，容量降到 4 Mbps 後 2 s 內跟上；整合測試 `BandwidthEstimationTests` 以 `BottleneckDatagramSocket` 在控制端前面模擬 2.5 → 1.5 Mbps 鏈路。主機每 5 秒記錄 `gcc … Mb/s {State} (delay, loss, acked)`。
- **丟包只算目前路徑**：`TransportFeedback` 的位元圖會把所有缺號當成遺失，但對其他候選位址送的 Bind/Ping 也佔用序號、本來就不會從這條路到達；主機以送出紀錄只計算真正送往目前路徑的缺號，否則建線階段就讀成 > 10% 丟包、丟包估算被砍半（實測開頭 30 秒卡在 6 Mbps）。
- **幀率補償**：MF 編碼器的 CBR 以設定的 60 fps 分配位元，拖動視窗時實際常只有 15–20 fps，每幀只拿到四分之一。`VideoService` 以動態期間擷取間隔的 EWMA 算出 `設定 fps / 實際 fps`（上限 3 倍）乘到編碼器碼率，讓實際送出量落在目標上；補償後仍夾在 200 Mbps 以內。
- **編碼器跟隨**（Sunshine 式）：CBR，`CODECAPI_AVEncCommonBufferSize` = max(鏈路碼率, 8 Mbps) × 0.2 s，依**鏈路**碼率（未含幀率補償）設定，鏈路碼率變動超過 2 倍才重設（原本的兩幀緩衝把 1440p 關鍵幀壓到約 27 KB，拖動第一瞬間也沒有位元可借；中途改緩衝會改 HRD 參數、可能插入關鍵幀）；`VideoService` 只在差距 ≥ 10% 時改碼率，下修間隔 ≥ 250 ms、上調間隔 ≥ 1 s。修復用的關鍵幀請求合併、每秒最多一張；新訂閱者與媒體通道關閉時的關鍵幀不受限。每張關鍵幀記錄大小與原因（`keyframe N KB at K kbps (reason)`，編碼器自行插入時為 `encoder decided`）；5 秒統計另記動態期間的實際幀率（相隔 < 200 ms 的擷取幀視為同一段動態）、平均與最大單幀大小。
- **收端**：`FrameAssembler` 依 `frame_seq` 排序，block 收滿 k 個 shard 即修復；放棄規則：有更新的完整關鍵幀、幀齡 > 200 ms、更新的完整幀已等 `max(2×幀間隔, 30 ms)`、待處理 > 8 幀。放棄後標記參考鏈斷裂：後續 delta 幀只丟棄，經 TCP 送 `RefreshVideo`（250 ms 內一次）直到關鍵幀。完整幀包成 `VideoFrame` 訊息送進既有的解碼佇列；序號比最後一次無損格子更新舊的影片幀只解碼不顯示，避免蓋掉無損像素。
  **序號空間是每個顯示器一個**（2026-09-24 修）：主機每個 `VideoService` 的 `seq` 都從 1 起算，切換顯示器後新串流的序號遠低於舊的，
  單一「已送過」水位會把它們全部丟掉——現象是切到螢幕 2 後畫面停在螢幕 1 的最後一幀、主機記錄 `no acknowledgements` 每 2 秒重送關鍵幀。
  TCP 路徑的 tile 水位（`_lastTileSeq`）同樣在確認時歸零。
  **2026-09-25 拆成三件（多螢幕 F 的前置，行為不變）**：`LinkStats`（整條路徑：FEC 前丟包率、累計 bytes——封包序號是所有串流與控制封包共用的，
  每串流各算會把別的串流的封包讀成缺號）、`FrameAssembler`（**一條串流**：水位、放棄規則、回報用的計數都屬於它，下一條串流從零開始）、
  `StreamAssemblers`（哪個資料包屬於哪條串流）。session 還沒說在看哪些螢幕時一次只有一條：另一個螢幕的關鍵幀取代它、delta 丟棄；
  收到確認（`Expect`，目前由 `SwitchDisplay` 觸發，F 之後由訂閱集合觸發）後恰好那幾條活著，其餘是殘幀。單一螢幕就是「只有一個元素的集合」。
  參考鏈斷裂與 `RefreshVideo` 也改成每條串流各自判斷、各自限速，tile 只在**自己那個螢幕**斷裂時才拒收。Kotlin 的 `LinkStats`／`FrameAssembler`／`StreamAssemblers` 是同一份設計。
- **幀層級回饋**：控制端每 16 ms **每條串流**送一則 `Feedback`（48 B：FEC 前丟包率、最高可解碼 seq、最近收到的 `pkt_seq` 與回聲延遲、累計 bytes、放棄/修復計數），取代每幀的 TCP `VideoAck`：主機由回聲算 RTT → `Qos.ReportDelay`（60 Hz）、最高 seq → `FrameAcked`、丟包 → `Qos.ReportLoss`（> 5% 視為壅塞、> 10% 降到 Poor）與 `FecPlanner`。
  **第一個位元組是串流編號，而它必須是真的**：2026-09-25 以前控制端（桌面與手機）一律寫 0，UDP 上的影格只靠這則回報確認，於是看第二個螢幕時主機從沒收到它的確認、
  in-flight 滿了就判定壅塞停送——切過去幾張之後畫面就凍住（`UdpMediaSessionTests.A_second_display_over_udp_is_acknowledged_as_itself`）。
  第二個位元組（原本固定 0）是旗標：bit 0 `LinkFieldsIgnored` 表示這一則的連線層欄位（丟包、bytes、回聲）是同一輪另一則的副本，主機只算一次；
  故意反向，舊的控制端寫 0 讀成「這些是權威的」，正確。
- **保活與失效**：閒置 2 s 送 Ping；4 s 沒有任何合法封包 → `media_close`、回 TCP、清掉 in-flight 記帳並要關鍵幀（`HostRuntime.MediaChannelClosed`）。直連路徑上來源位址改變且 `pkt_seq` 更新就跟著遷移（QUIC 式）；中繼路徑固定。`VideoService` 另有保險：2 秒沒有任何 ack 就重置記帳並送關鍵幀，避免傳輸切換時卡死。
- **UDP 中繼**（`Relay/Net/UdpRelayListener.cs`、`Core/UdpRelayTable.cs`）：與 TCP 同埠號（`Relay:UdpPort`，0 = 同 TCP）。20 bytes 明文配對封包（magic `R`、Bind/BindAck/Reject、16 bytes token）：同 token 的兩個端點配成一對後其餘封包原樣互轉；第三個端點或已配對的 token 拒絕；未配對 30 s、閒置 60 s 清除。中繼看不到媒體內容。防火牆需開 **21117/udp**。
- 設定：`PeerSettings.UdpMedia` / `HostConfig.UdpMedia`（主機）、`ControllerSessionOptions.UdpMedia`（控制端，`MediaDebugLoss` 供測試注入丟包）。狀態列顯示 `UDP Local/Reflexive/Relay`。

### 6.7 遠端終端機

這一節是實作。行為、各平台的身分對照、紀錄、風險與排錯見 [terminal.md](terminal.md)；安全回歸的驗法見 [hardening.md](hardening.md)。

**權限與範圍**：`HostConfig.TerminalEnabled` 預設 **false**（`HostPolicy.TerminalEnabled`→`PERM_TERMINAL`），`SessionOptions`／`LoginRequest` 裡**沒有**任何終端機欄位——控制端只能被授予，不能要求（測試釘住 descriptor）。
`SessionScope`：`TerminalAction` 只在 `ConnType == CONN_TERMINAL` **且** 持有 `PERM_TERMINAL` 時允許（刻意比檔案傳輸嚴：那裡連線類型隱含權限）；`TerminalResponse` 主機永不接受。違規即關閉工作階段。
舊主機不可能在 `PeerInfo.granted` 回 `PERM_TERMINAL`，所以「沒有這個權限」同時回答了「主機太舊」與「主機不允許」。

**主機端**：`Platform.Abstractions/Terminal/ITerminalHost`（`IsAvailable`／`UnavailableReason`／`DescribeIdentity(runAs)`／`StartAsync(runAs, cols, rows)`）與 `ITerminal`（`Output` 是 `Stream`、`WriteAsync`、`Resize`、`Signal`、`Exited`）；
`TerminalRunAs { Highest, User }` 對應 `HostConfig.TerminalRunsAs = "system"|"user"`（預設 system，使用者的決定）。`Core/Terminal/TerminalSession`（每連線）：id 表、上限 4、單則輸出 64 KiB、credit window 256 KiB（控制端每收一則 `output` 就回 `ack`）、
8 ms 合併；`output`／`exit` 走 **Bulk**（bounded、會等待，慢連線變成 pty 背壓而不是主機記憶體），`exit` 與 `output` 同佇列所以永遠排在最後一個位元組之後；`input` 走 **Input**。
`Services/HostTerminalModule`（照 `HostFileModule`）：`SessionAuthorized` 且 `CONN_TERMINAL` 才建、`PERM_TERMINAL` 被撤回→`CloseAllAsync`（殺掉所有 shell 並送 `exit`）、`SessionClosing`→dispose。
**IPC 沒有「開終端機」的指令**，唯一能到 `StartAsync` 的路是已授權工作階段自己的請求。連線紀錄的 `terminal_opens`／`terminal_identity` 由模組寫進 `HostSessionContext`，journal 結束時帶出（種類 `terminal`）；同步到帳號時也帶這兩欄（`ConnectionUpload`→portal 的 `connection_events.terminal_opens`／`terminal_identity`，舊資料庫在啟動時補欄位）。

**目前狀態（E1，2026-09-25）**：協定、權限、scope、抽象、`TerminalSession`、模組、控制端 API（`OpenTerminalAsync`／`SendTerminalInputAsync`／`ResizeTerminalAsync`／`SignalTerminalAsync`／`CloseTerminalAsync`、`IControllerCallbacks.OnTerminal`）、
`Core.Testing.FakeTerminalHost`、PeerCli（`--terminal`、`term`／`tsend`／`tresize`／`tsig`／`tclose`／`expect`／`dump`）都在，所有安全測試對假 shell 綠。
**平台實作（E3，2026-09-25）**：shell 以**引擎自己的帳號**執行（Windows 服務下即 SYSTEM、app 內為登入使用者；Linux daemon 下為 `deskpair`、app 內為登入使用者；macOS 為登入使用者），
`DescribeIdentity` 回報的就是這個帳號。以 root 或「服務下的登入使用者」執行是 E5。
- **Windows**（`Platform.Windows/Terminal/WindowsTerminalHost`）：ConPTY；預設 Windows PowerShell（`-NoLogo`），沒有就 `cmd.exe`；行程以 `CREATE_SUSPENDED` 建立、放進 `KILL_ON_JOB_CLOSE` 的 Job object 後才恢復執行，
  所以 shell 啟動的任何東西都跟著 Job 一起死（測試用 `Start-Process ping` 驗證）。`STARTF_USESTDHANDLES` 且三個 handle 皆空，否則引擎自己的標準輸出被重導時子行程會寫到那裡去。
  **「忽略 Ctrl+C」是會被子行程繼承的行程旗標**，服務與測試主機通常帶著它，shell 裡的 Ctrl+C 因此完全無效（實測）；建立行程前 `SetConsoleCtrlHandler(NULL, FALSE)` 清掉。
  pseudo console 在 shell 結束後不會自己結束輸出，由等待執行緒在結束 200 ms 後 `ClosePseudoConsole`（讀取執行緒仍在讀，所以不會卡住）。
- **Linux**（`Platform.Linux/Terminal/LinuxTerminal`）：`posix_spawn` + `POSIX_SPAWN_SETSID` + file action 以 `O_RDWR`（無 `O_NOCTTY`）開啟 slave 到 0，dup 到 1、2——glibc 先 setsid 再做 file actions，
  session leader 開啟 tty 即取得控制終端；`SETSIGDEF`（全部）＋空的 `SETSIGMASK`（執行時期忽略 SIGPIPE，不能帶進 shell）；glibc 2.34+ 加 `addclosefrom_np(3)`，2.29+ 加 `addchdir_np(HOME)`。
  daemon 模式下引擎在建平台前把 daemon 交來的四個描述子設為 close-on-exec，否則 shell 會拿到 uinput 與通往 root daemon 的 socket（實測 shell 內 `leaked=0`）。
- **macOS**（`Platform.MacOS/Terminal/MacTerminal`＋shim `pty.m`）：Apple arm64 的可變參數放在堆疊上，`ioctl` 無法從 .NET 呼叫，`posix_spawn` 也沒有設定控制終端的動作，所以在 C 裡 `fork`，
  子行程只呼叫 async-signal-safe 的 `setsid`／`TIOCSCTTY`／`dup2`／`close`／`sigaction`／`chdir`／`execve`。視窗大小要設在 slave 開啟**之後**，否則 macOS 會忘掉（實測 `stty size` 讀到 0 0）。
- **共用**（`src/Shared/UnixTerminal`，以 `Compile Include` 連結進 Linux 與 macOS 兩個專案，型別為 internal）：選 shell（帳號的 shell；nologin／false 退到 bash、再退到 sh）、以登入 shell 啟動（argv[0] 前加 `-`）、
  環境變數從零建立（HOME/USER/LOGNAME/SHELL/PATH/TERM=xterm-256color/COLORTERM/LANG），讀取用 `poll` 以便停止、寫入獨立執行緒（程式不讀輸入時不會卡住工作階段）、`waitpid` 等待；
  結束時 `killpg(SIGHUP)`、寬限 2 秒，再把同一個 session（`getsid`）裡剩下的行程全部 SIGKILL——只殺 shell 的 process group 會漏掉它啟動的 job（實測忽略 SIGHUP 的背景行程被清掉）。自行 `setsid` 的行程會逃出，與 ssh 登出相同。
- `PeerCli host --terminal` 以執行 PeerCli 的帳號提供真的 shell，用來在 Mac 與 Linux 實驗機上跑完整路徑而不必重新部署已安裝的產品。

**桌面端（E4，2026-09-25）**：
- **視窗**：`Views/TerminalWindow`＋`ViewModels/TerminalViewModel`（`ConnType = ConnTerminal`，照檔案傳輸視窗的形狀，`App.OpenTerminal(target)`）。入口在首頁（「終端機」按鈕）、裝置清單每一列、遠端控制工具列。
  登入後先看 `PeerInfo.granted` 有沒有 `PERM_TERMINAL`——沒有就說「這台電腦不允許終端機」而不送 `TerminalAction`（送了就是 scope 違規、整條連線被關）。頂端一條固定顯示 shell 的身分（黃字）與程式；
  shell 結束或連線中斷時上方橫幅說明原因，連線還在就提供「開一個新的 shell」（新的 id，畫面先 `ESC c` 重設）。不做 detach／reattach。視窗大小停止變動 150 ms 後才送 `resize`。
- **控制項** `Controls/TerminalControl`：每一段同字型同色的文字畫成一個 `GlyphRun`，每格的 advance 固定為格寬（寬字元兩格），不經 TextLayout——排版會照字形各自的寬度與字距排，一行內就會偏離欄位。
  字型依序探測（Windows：Cascadia Mono／Consolas；macOS：SF Mono／Menlo；Linux：DejaVu Sans Mono／Noto Sans Mono…），字型管理員找不到時會回預設字型，所以比對 family 名稱；
  缺字（中日韓、符號）用 `FontManager.TryMatchCharacter` 找 fallback，仍放在格子上。顏色見 `TerminalPalette`（16 色、6×6×6 立方、灰階、真彩；粗體讓前 8 色變亮、反白、淡、隱藏）。
  一般文字走 `OnTextInput`（輸入法提交的文字才進得來），特殊鍵與 Ctrl／Alt 組合走 `OnKeyDown` 用 `TerminalKeys` 翻譯，全部 `Handled`（Tab 不會移走焦點）；Ctrl+Alt 視為 AltGr 交給文字輸入。
  複製／貼上：Ctrl+Shift+C／V、Ctrl+Insert／Shift+Insert、macOS 的 Cmd+C／V、右鍵選單（Ctrl+C 永遠是 SIGINT）。多行貼上先在橫幅問「每一行都會當成指令執行」。
  滑鼠拖曳選取（跨 scrollback）、滾輪看 scrollback、Shift+PageUp/Down 翻頁；在副畫面（vim、less）滾輪改送方向鍵。渲染以 headless Skia 實際畫出來檢查過（顏色、線條字元、寬字元與欄位對齊、游標）。
- **設定頁**（安全性）：終端機不是勾選框，是「開啟終端機…」按鈕，展開說明（任何拿到密碼且被允許的人都能以下列帳號執行任何指令，包括沒人在電腦前時；每個 shell 都記在連線紀錄）後再按「開啟」才寫入設定。
  **已安裝無人值守存取而沒有永久密碼時不能開啟**（只剩一次性密碼擋在一個 SYSTEM／root shell 前面）。開啟後可選「這台電腦允許的最高身分」或「目前登入的使用者」（`HostConfig.TerminalRunsAs`；後者在服務下的實作是 E5）。
- **連線管理員**：終端機請求的卡片寫「X 想要在這台電腦上開終端機」，並有一個**預設不勾**的「允許終端機，以 {身分} 執行」，沒勾之前「接受」是灰的（`ConnectionManagerViewModel.Decision` 也把未勾的接受當成拒絕）。
  IPC `ApprovalRequest.terminal_identity = 7` 帶著身分（`HostIpcBridge.TerminalIdentity` 由引擎的終端機模組提供）。已連上的終端機工作階段，這個勾選框就是即時開關：取消即撤權、殺掉所有 shell、連線保留。
  主機端另一道規則：**核准只在 `Granted` 明確列出 `PERM_TERMINAL` 時才給終端機**——空的清單或不認得終端機的舊版連線管理員都不會意外給出去（先前空清單會讓所有權限照政策全給）。

**提權（E5，2026-09-25）**：
- **Windows**：「最高」＝引擎自己的帳號（服務下是 SYSTEM）。「登入使用者」在引擎是 SYSTEM 時以 `WTSQueryUserToken`（先主控台、再任一 active session）取 token，
  `CreateEnvironmentBlock`＋`GetUserProfileDirectory`，`CreateProcessAsUserW` 仍帶 ConPTY 屬性並放進同一個 Job object；沒有人登入就拒絕並說原因，**不會默默改給 SYSTEM**。
  引擎不是 SYSTEM（由 app 執行）時兩種都是目前使用者。選擇邏輯是純函式 `WindowsShellIdentity.Plan`。**這條路還沒在 SYSTEM 下實測**（開發機沒有提權、也沒裝服務）。
- **Linux daemon**：引擎是 `deskpair` 且永久放棄了 root，所以 root（或登入使用者）的 shell 只能由 daemon 開。線路（daemon↔引擎，內部格式，不是對外協定）：
  `DrmWire` 7 `OpenTerminal{runAs, cols, rows, describeOnly}`（只有數字）、8 `Terminal{handle, 帳號\nshell}`＋SCM_RIGHTS 附 pty master、9 `TerminalSignal{handle, 0|1|9|15}`、10 `TerminalState{exited, code}`；
  拒絕用 `KindError` 加 `FlagRefused`。
  - **閘門**：`/etc/deskpair/daemon.conf` 的 `terminal-root = yes`，root 擁有、引擎寫不到，**每次請求重讀**；檔案不存在或不是明確的 yes 就是 no。`--install-service` 在沒有檔案時寫入 yes（擁有者的決定）並印出一句警告：
    「deskpair 帳號在這台機器上等同 root」。升級不覆寫既有檔案。閘門關閉時 daemon 回拒絕，引擎改開**自己帳號**的 shell，身分老實寫 `deskpair`。測試釘住閘門路徑在 /etc、不在引擎資料目錄。
  - **不是 daemon 的子行程**：daemon 跑在 unit 的沙箱裡（`ProtectSystem=full` 讓 /usr、/etc 唯讀；`RestrictSUIDSGID` 是 seccomp），子行程全部繼承——實測 daemon 與引擎的 /usr、/etc 都是 ro、`Seccomp: 2`，
    root shell 會連 `apt install` 都做不了；seccomp 拿不掉，多執行緒的 .NET 行程也不能 `setns` 換 mount namespace。所以 daemon 開好 pty 後用 `systemd-run` 起一個暫時性 unit
    （`TTYPath=` slave、`StandardInput/Output/Error=tty` 讓它成為控制終端、`--uid=` 帳號、`KillMode=control-group`、`TimeoutStopSec=2`、shell `-l`），把 master 交給引擎後自己不留副本。
    參數全部是常數、數字或 passwd 查到的值（不含 `$`，systemd 會展開它）；透過 `ArgumentList` 執行，沒有 shell 解析。這取代了計畫裡的 `--pty-child`。
  - **結束**：引擎不能對 root 行程送訊號或 waitpid，所以 `DaemonPtyProcess` 經 daemon 問（每秒一次 `systemctl show`）與送（HUP/TERM＝`systemctl kill`、KILL＝`systemctl stop`）；
    stop 殺整個 cgroup，連 `setsid -f` 出去的程序也一起（實測）。引擎的通道關閉（引擎結束或當掉）時 daemon 停掉它所有的 unit（實測 kill -9 引擎後 unit 與行程歸零）。每個引擎同時最多 8 個、每分鐘最多開 20 個。
  - 實測（Linux 測試機）：root（`/usr` 可寫、可建 setuid 檔）、登入使用者（8 個群組、家目錄）、閘門關閉退回 deskpair、Ctrl+C、結束代碼、連線紀錄寫下 `TerminalOpens`／`TerminalIdentity`。
- **IPC 不能開終端機**：`TerminalIpcTests` 釘住 IPC 沒有任何終端機指令（只允許日後的 `*_state` 推播），且 `HostIpcBridge` 不持有任何能啟動 shell 的型別，只有 `Func<string> TerminalIdentity`。

**控制端的終端機模型（E2，2026-09-25）**：`Core/Terminal/VtParser`（Paul Williams 狀態機，位元組導向，前面接跨呼叫的 UTF-8 解碼器；DCS／SOS／PM／APC 吞掉不印）、
`TerminalScreen`（格子＋游標＋捲動區＋主／副畫面＋上限 10000 行的 scrollback；DECAWM 的待換行、寬字元放不下最後一欄先換行、覆寫寬字元的一半會清掉另一半；
SGR 含 256 色與真彩、`:` 分隔也讀；DSR／DA 的回覆由 `TakeReplies` 交給工作階段送回；`TakeDirtyRows` 讓控制項只重畫變動的列；縮放不 reflow，變矮時游標上方的列進 scrollback）、
`TerminalKeys`（xterm 的按鍵位元組：DECCKM、`CSI 1;m` 修飾鍵、Ctrl+字母→C0、Alt→ESC 前綴、貼上轉 CR 並在 bracketed 模式下框起來且移除內含的結束標記）。
字寬取 `Wcwidth`（MIT）。行為由 `tests/fixtures/vt/*.json` 的共用向量釘住（格式見該目錄 README），每筆都以整段、逐位元組、隨機切段三種方式餵入且結果必須相同；Kotlin 版在 E6 讀同一批檔案。

**手機（E6，2026-09-25）**：
- **共用層**（`mobile/shared/.../terminal/`）：`VtParser.kt`、`TerminalScreen.kt`、`CharWidth.kt`（`Wcwidth` 的零寬與寬字區間，二分搜尋）、`TerminalKeys.kt` 是 C# 版的逐行移植，UTF-8 自己解。
  Gradle 任務 `generateVtVectors` 把 `tests/fixtures/vt/*.json` 烘進測試原始碼，`VtVectorsTest` 以同樣三種切法跑每一筆，JVM 與 iOS 模擬器（Kotlin/Native）都跑。
- **連線**：`DeskPair.connectTerminal(target, password, columns, rows)`。`ConnType.CONN_TERMINAL` 穿過 `PeerConnector.connectById`→`RendezvousClient`／`RelayClient` 與 `ControllerSession` 的登入請求；
  能力宣告不要 UDP、音訊、剪貼簿。登入後 `granted` 沒有 `PERM_TERMINAL` 就丟 `TerminalNotAllowedException` 並關閉連線，一則 `TerminalAction` 都不送。
- **`RemoteTerminal`**：碰到畫面的一切（讀取迴圈、打字、縮放、捲動）都在同一個 `limitedParallelism(1)` 的 dispatcher 上，所以畫面不用鎖。對外兩個 `StateFlow`：
  `snapshot`（顏色已解好的 run，寬字元自成一個兩格的 run，所以依「欄 × 格寬」放置就不會離開格子；每 16 ms 最多一份）與 `status`（開啟中／開啟／結束／失敗／連線中斷，加身分與結束代碼）。
  每收一則 `output` 回 `ack`、`resize` 去抖 150 ms、5 秒 heartbeat、回應 `test_delay`。iOS 經 `SessionObservers.terminalSnapshot`／`terminalStatus` 訂閱。
- **Android**（`TerminalView.kt`）：Compose `Canvas` 以 `drawText` 畫每個 run，格寬量 "M"。隱形的 `BasicTextField` 永遠只放一個佔位字元：多出來的字是輸入、佔位字被刪是 Backspace、換行是 Enter，
  輸入法提交的文字也從這裡進來。實體鍵盤走 `SessionViewModel.onHardwareKey`→`TerminalKeyMap`。
- **iOS**（`TerminalView.swift`）：SwiftUI `Canvas`；隱形的 `UIKeyInput` view 關掉自動修正與智慧標點，`hasText` 永遠為真，空行也收得到 Backspace；實體鍵盤的方向鍵、Esc、F 鍵與 Ctrl／Option 組合在 `pressesBegan` 攔下，
  其餘當文字。`TerminalModel` 與 `SessionModel` 分開，畫面一秒更新六十次只重畫自己。
- **兩個 App 共同的部分**：頂端黃字固定寫 `身分@主機`；按鍵列有 Esc、Tab、黏著的 Ctrl／Alt（按一下 Ctrl 再按 C＝Ctrl+C）、方向鍵、Home／End／PgUp／PgDn、常用符號與貼上（多行先確認）；
  上下拖曳看 scrollback。入口是連線頁的「開啟終端機」（輸入框有內容時才出現、不是第二個主按鈕），走與遠端桌面相同的密碼規則；主機金鑰變更後選擇信任，重試的也是終端機。
- **測試掛鉤**：iOS `SUNLLO_TEST_TERMINAL`（`1`，或 shell 開好後要打的指令）；Android `-e terminal 1`，再用 `adb shell input text` 打字（走實體鍵盤的路徑）。
- **實測**（Mac 上的 PeerCli `host --terminal`）：iPhone 17 模擬器與 Android 模擬器都開到 zsh，`id -un` 為登入使用者，粗體、背景色與中文寬字都對齊欄位；軟鍵盤升起後格子縮到按鍵列上方並通知主機
  （Android `stty size` 為 24 44）。shell 開好之前打的字會被丟掉。

## 7. 平台抽象層 (`DeskPair.Platform.Abstractions`)

值型別：`PixelFormat{Bgra32, Rgba32, Nv12, I420}`、`FrameRotation`、`VideoCodec{H264, H265, Vp8, Vp9, Av1}`、`GpuApi{D3D11, Metal, OpenGl, Vaapi, None}`、
`GpuSurfaceHandle{Api, Handle, SharedHandle, FourCc}`、`CaptureFrame{IsGpuTexture, W, H, Rotation, Format, Cpu(ReadOnlyMemory), Stride, Gpu, Timestamp}` (借用至下次 Acquire)、
`CaptureStatus{Frame, Timeout, DesktopSwitched, Error}`、`DisplayDescriptor{Index, Name, X, Y, W, H, Scale, Rotation, IsPrimary, AdapterLuid}`、
`EncodedPacket{Data, IsKeyFrame, PtsTicks}`、`DecodedFrame{IsGpuSurface, Gpu, Cpu, W, H, Stride, Format, Pts}`、`AudioFormat{SampleRate, Channels}`、`ClipboardData{Format, Payload}`。

```csharp
public interface IDisplayEnumerator { IReadOnlyList<DisplayDescriptor> GetDisplays(); event EventHandler? DisplaysChanged; }
public interface IScreenCapturerFactory { IScreenCapturer Create(DisplayDescriptor d, bool preferGpu); }
public interface IScreenCapturer : IAsyncDisposable {
    DisplayDescriptor Display { get; } bool SupportsGpuTexture { get; } GpuApi GpuApi { get; }
    ValueTask<CaptureResult> AcquireFrameAsync(TimeSpan timeout, CancellationToken ct);   // Timeout = 畫面無變化
    void ForceFallbackPath();                                                             // Windows: DXGI→GDI
}
public sealed record VideoEncoderConfig(VideoCodec Codec, int W, int H, int Fps, int BitrateKbps, bool PreferHardware, PixelFormat InputFormat, GpuApi InputGpuApi, long AdapterLuid);
public interface IVideoEncoder : IAsyncDisposable {
    VideoCodec Codec { get; } bool IsHardware { get; } bool IsLatencyFree { get; } PixelFormat RequiredInputFormat { get; }
    void SetBitrate(int kbps); void RequestKeyFrame();
    bool TryEncodeCpu(ReadOnlySpan<byte> input, int stride, long pts, out EncodedPacket packet);
    bool TryEncodeTexture(in GpuSurfaceHandle tex, long pts, out EncodedPacket packet);
    bool TryCollect(out EncodedPacket packet);   // 先前送入、呼叫返回時還沒好的那一張；同步編碼器預設 false
}
public interface IVideoEncoderFactory { SupportedCodecs Probe(); IVideoEncoder Create(VideoEncoderConfig cfg); }
public interface IVideoDecoder : IAsyncDisposable { VideoCodec Codec { get; } bool OutputsGpuSurface { get; } bool TryDecode(ReadOnlySpan<byte> packet, bool key, out DecodedFrame frame); }
public interface IVideoDecoderFactory { SupportedCodecs Probe(); IVideoDecoder Create(VideoCodec codec, GpuApi preferredOutput, long adapterLuid); }
public interface IInputInjector : IAsyncDisposable {
    void EnsureInputDesktop();                       // Windows: OpenInputDesktop/SetThreadDesktop，每次注入前呼叫
    void InjectMouse(in MouseEvent e, in VirtualScreenRect virt); void InjectKey(in KeyEvent e);
    void SetLockKeyStates(bool caps, bool num, bool scroll); void ReleaseAllKeys(); void SendCtrlAltDel(); void LockWorkstation();
}
public interface ICursorProvider : IAsyncDisposable { ulong GetCurrentCursorId(); CursorImage GetCursorImage(ulong id); (int X, int Y)? GetCursorPosition(); }
public interface IAudioCapture : IAsyncDisposable { AudioFormat Format { get; } ChannelReader<ReadOnlyMemory<float>> Frames { get; } ValueTask StartAsync(CancellationToken ct); }  // 10 ms f32 interleaved
public interface IAudioPlayback : IAsyncDisposable { ValueTask ConfigureAsync(AudioFormat f); void Enqueue(ReadOnlySpan<float> pcm); }
public interface IClipboard : IAsyncDisposable { ChannelReader<IReadOnlyList<ClipboardData>> Changes { get; } void Set(IReadOnlyList<ClipboardData> data); }
public interface ISessionMonitor { ActiveSession GetActiveSession(); event EventHandler<ActiveSession>? ActiveSessionChanged; int LaunchInSession(uint sessionId, string exe, string args, SessionLaunchAs asWho); }
public interface IServiceHost { bool IsInstalled { get; } void Install(); void Uninstall(); void Start(); void Stop(); void RunServiceMainLoop(CancellationToken ct); }
public interface IPlatformInfo { OsPlatform Platform { get; } bool IsElevated { get; } bool IsWayland { get; } PermissionStatus CheckPermissions(); void RequestPermissions(); }
public interface IMachineIdProvider { byte[] GetStableMachineId(); }
public interface ISecretStore { Task<byte[]?> GetAsync(string key); Task SetAsync(string key, byte[] value); }   // DPAPI / Keychain / libsecret (檔案 0600 後備)
```

Opus 編解碼放在 Core (`Audio/OpusCodec.cs`，Concentus)，不屬平台層。每個平台組件提供 `PlatformModule.AddPlatform(IServiceCollection)`，啟動時依 `RuntimeInformation` 註冊。

---

## 8. 各平台實作

### Windows (`Platform.Windows`, `net10.0-windows`；CsWin32 做 flat Win32，Vortice 做 COM 物件)

| 項目 | API | 備註 |
|---|---|---|
| 擷取 | DXGI Desktop Duplication (`AcquireNextFrame`；`WaitTimeout`/`LastPresentTime==0` → Timeout)；GDI `BitBlt` 後備 (DXGI 失敗或連續 4 次 timeout) | HDR 輸出為 `R16G16B16A16_FLOAT`，需偵測並要求 SDR/tone-map |
| 顯示器列舉 | `EnumDisplayDevices` + DXGI outputs，重排成 GDI 順序保持索引穩定 | |
| 編碼 | Media Foundation H.264/HEVC/AV1 MFT（`MFTEnumEx` 逐格式列舉，硬體優先）；目前輸入的是**系統記憶體的 NV12**，texture 輸入（`MF_SA_D3D11_AWARE`）尚未接上；H.264 SW 備有 `CLSID_CMSH264EncoderMFT` | 無週期性 keyframe，靠 `RequestKeyFrame` (`CODECAPI_AVEncVideoForceKeyFrame`) |
| 解碼 | MF 解碼 MFT + `IMFDXGIDeviceManager` → D3D11 shared NT handle texture | 給 Avalonia GPU interop |
| 輸入 | `SendInput`：`MOUSEEVENTF_ABSOLUTE\|VIRTUALDESK` 以虛擬螢幕座標換算 65535；`dwExtraInfo` 標記自家事件；scancode (Map) / unicode (Translate)；`MapVirtualKeyEx` 用前景視窗鍵盤配置 | 每次注入前 `EnsureInputDesktop()` |
| 游標 | `GetCursorInfo`/`GetIconInfo` → BGRA (含單色 AND/XOR mask 處理) | |
| 音訊 | NAudio WASAPI loopback 擷取 + 播放 | |
| 剪貼簿 | `AddClipboardFormatListener` 隱藏訊息視窗；Text/HTML/RTF/PNG/DIB | |
| 服務/Session | **已移除**。沒有 Windows 服務、不複製 token、不跨 session 啟動程序；DeskPair 以登入使用者身分跑，開機後靠 HKCU `Run` 自動啟動。`SendSAS` 仍在程式碼裡，但只有 SYSTEM 能用，除非管理員開啟 `SoftwareSASGeneration` 原則，否則 Ctrl+Alt+Del 會失敗並記錄 | `--allow-firewall` 提權建規則 (規則指向這個唯一執行檔)；需 EV 簽章避免 SmartScreen/EDR 誤判 |
| 密文儲存 | DPAPI (`ProtectedData`, LocalMachine scope 給 SYSTEM) | |

### Linux (`Platform.Linux`；`LibraryImport` + FFmpeg.AutoGen + Tmds.DBus.Protocol)

| 項目 | API | 備註 |
|---|---|---|
| 擷取 | X11 `XShmGetImage` + XRandR；Wayland：xdg-desktop-portal RemoteDesktop／ScreenCast + PipeWire（見下方 Wayland 小節） | **v1 以 X11 為主**，Wayland 盡力 (需使用者 session、portal 同意，無法無人值守) |
| 編碼/解碼 | FFmpeg `h264_vaapi`/`hevc_vaapi` (+ `hwaccel vaapi` 解碼 → dmabuf/GL) | 直接 libva 太底層；FFmpeg 共享庫隨附 (LGPL，動態連結) |
| 輸入 | XTest；Wayland 用 portal RemoteDesktop 的 `Notify*`（H3）；DRM daemon 用 `/dev/uinput` | |
| 游標 | `XFixesGetCursorImage` | |
| 音訊 | PulseAudio/PipeWire (`libpulse-simple` P/Invoke) | |
| 剪貼簿 | X11 selections + XFixes SelectionNotify | |
| 服務/Session | **已移除**。沒有 systemd unit、不找 seat、不降權啟動子程序。桌面工作階段裡開 App 即可；無桌面的機器用 `--server` | 登入畫面擷取一併放棄 |
| 密文儲存 | libsecret；後備檔案 0600 | |

### Linux：Wayland（H，2026-09-26 起）

Wayland 的合成器不讓任何 client 自己讀螢幕或送輸入；唯一涵蓋 GNOME 與 KDE 的路是 xdg-desktop-portal 的 RemoteDesktop 工作階段（來源用 ScreenCast 選），由坐在那台機器前的人同意一次。**這不是無人值守**：沒有登入的使用者工作階段就沒有 portal，登入畫面、鎖定畫面、冷開機仍然歸 DRM daemon。

**H1 portal 工作階段**（`Wayland/PortalSession`；D-Bus 用 `Tmds.DBus.Protocol`：低階、沒有 `Reflection.Emit`、收得到 Unix fd。版本釘在 Avalonia 用的 0.21.3，原因見 H4b）：

- 對話順序：`RemoteDesktop.CreateSession` → `ScreenCast.SelectSources`（整個螢幕、`multiple`、`cursor_mode` 能給 metadata 就給 metadata）→ `RemoteDesktop.SelectDevices`（鍵盤＋指標、`persist_mode 2`、上次的 `restore_token`）→ `RemoteDesktop.Start` → `ScreenCast.OpenPipeWireRemote`（一個只看得到這些 node 的 PipeWire socket fd）。
- 前四個的答案不在方法回覆裡，而在 request 物件的 `Response` 訊號。路徑事先算得出來（`/org/freedesktop/portal/desktop/request/<sender>/<token>`），所以**先訂閱再呼叫**：不必問人的 `Start` 可能比方法回覆還早到。
- `Start` 硬逾時 45 秒，逾時先 `Request.Close` 把對話框收掉（不留給之後坐下的人按），再關 session，並給 viewer 一句「這台機器的螢幕上正在問要不要分享，沒有人回答」；其他請求 10 秒。對方按拒絕、portal 不在（`ServiceUnknown`）、後端沒裝（讀不到介面屬性，訊息指名 `xdg-desktop-portal-gnome`／`-kde`）各是一種 `PortalFailure`。
- **restore token**（`PortalTokens`）存在 secret store，鍵是 `wayland-portal-<uid>`：root daemon 與使用者引擎共用一個資料目錄，固定的鍵會互相蓋掉。token 只能用一次，每次 `Start` 之後重存，沒拿到新的就刪掉；讀不到（別人的）就不寫。
- GNOME 46 的對話框裡「允許遠端互動」**預設關閉**，沒打開就是 `devices = 0`：看得到畫面、不能操作。「記住這個選擇」因為 `persist_mode 2` 預設勾選。

**實測（Linux 測試機，Ubuntu 24.04、GNOME Shell 46.0、xdg-desktop-portal 1.18.4、xdg-desktop-portal-gnome 46.2；RemoteDesktop 2、ScreenCast 5，GNOME 後端的 RemoteDesktop 也是 2）**：`DeskPair.Tools.LinuxHarness portal` 以登入使用者的 session bus 執行。第一次跳出「Remote Desktop」對話框，經由 DeskPair 本身（DRM daemon 擷取＋uinput）遠端打開「允許遠端互動」並按「分享」：`Start` 25.5 秒、一條串流（node 55、1280×800 於 0,0、帶 mapping id）、devices 3、token 已存。第二、三、四次 **`Start` 5 ms、8 ms、6 ms，沒有對話框**，右上角出現分享中的指示，`OpenPipeWireRemote` 回的是一個 socket。另外兩條路也在同一台量過：沒有 token、對話框不理它，45 秒後 `TimedOut` 而且對話框已被收掉；按「取消」得到 `Refused`，什麼都不存。

**H2 PipeWire 擷取**（`native/linux/SunlloWaylandShim`＋`Wayland/PortalScreenCapturer`、`PortalCapture`）：

- **C shim**：SPA pod 的建構與剖析全是標頭檔裡的 `static inline`，沒有符號可以 P/Invoke，所以串流放在一支小 C 函式庫裡、跑在 PipeWire 自己的執行緒上，C# 只「拉」：`dp_pw_wait`／`lock_frame`／`release_frame`（見該目錄 README）。只協商 BGRx/BGRA/RGBx/RGBA 與 MemFd/MemPtr、**不宣告 modifier**，合成器就給共享記憶體、自己做 GPU 讀回；緩衝區至少 3 個；要 Header、VideoCrop、Cursor 三種 meta；只有游標動的空 chunk 不算新畫面。
- **零複製**：memfd 由 shim 自己 `mmap`，不交給 `pw_stream`：重新協商時 `pw_stream` 會連映射一起拿走緩衝區，而 C# 可能還在編碼上一張。借出的畫面活到下一次 `lock_frame`，正好是 `CaptureFrame` 的借用契約；可讀長度不到 stride×height（引擎會切 `Cpu[..(Stride*Height)]`）、緩衝區少於 3 個或不是 memfd 時，改成逐列複製後立刻還。下一張取走時舊的 `Memory` 立刻失效，之後再讀會丟例外，而不是讀到合成器正在畫、甚至已經解除映射的記憶體。
- **尺寸是像素**：編碼器照 `DisplayDescriptor` 的尺寸建，portal 給的是邏輯座標（有縮放時不同），所以 `PortalCapture` 開始時每條串流開一次、看第一張畫面的像素尺寸。之後擷取器看到不同尺寸就回 `DesktopSwitched` 並回報，清單更新、觸發 `DisplaysChanged`，交給既有的重建流程。原點那台當主螢幕；名字是 `wayland@X,Y`（node id 每個工作階段都換，F 的視窗依名字配對）；`AdapterLuid` 放 node id。每個擷取器各自 `OpenPipeWireRemote`：一條 PipeWire 連線只能是一個 client。
- **建置與打包**：`tools/build-wayland-shim.ps1` 在 Docker 的 Ubuntu 22.04 編（glibc 2.35、PipeWire 0.3.48 起都能用）到 `artifacts/wayland-shim/<rid>/`；`WaylandShim.targets` 讓 Linux 的單檔發佈把它包進去，執行時解到 `~/.net/<app>/<hash>/`，Linux 安裝程式只複製那一個檔案也不會漏。沒有 shim 時照常建置與執行，Wayland 擷取不可用。`dp_pw_abi` 回報結構大小，舊版 shim 會被載入器拒絕；`WaylandShimLayoutTests` 直接讀 `shim.h` 算位移比對 C# 鏡像。
- **Tmds 的讀取執行緒**：Tmds 在讀 bus 的那條執行緒上完成方法呼叫的 Task，已註冊的 continuation 就在那裡同步執行（`ConfigureAwait(false)` 或 `ForceYielding` 都攔不住，後者只在 Task 已完成時才讓出）。於是 `await` 一個回覆之後，呼叫端的程式碼跑在讀取迴圈上；它若再同步等另一個回覆（擷取器工廠必須是同步的），就是讀取迴圈在等自己，直到逾時。dbus-monitor 看到 portal 1 ms 內就回覆，dotnet-stack 看到整串堆疊都在 `ReceiveMessages` 底下。現在 `PortalSession` 對 bus 的每個 await 都經過 `LeaveReaderAsync`（等完成、`Task.Yield()` 回到執行緒池、再取結果）。

**實測（Linux 測試機，PipeWire 1.0.5）**：`LinuxHarness portal-grab` 經由產品的 `PortalCapture`／`PortalScreenCapturer`：1280×800 BGRx、stride 5120、協商到 4 個緩衝區、**不複製直接交出**；遠端打字時畫面逐張變化（終端機游標閃爍的差異 0.0002、出現一行字 0.06），存下的畫面是真的桌面、顏色正確、**畫面裡沒有滑鼠游標**（metadata 模式）。單一執行檔、旁邊沒有 `.so` 時一樣能載入 shim。順帶修好 uinput 打長字串會掉字（evdev 佇列溢位，`acfbf3c`）。

**H3 輸入**（`Wayland/PortalInputInjector`＋`PortalSession` 的 `IPortalInput`）：

- **座標**：引擎的虛擬螢幕是像素（檢視端點在畫面上的像素，再加上該螢幕的 X、Y），portal 要的是「某一條串流、那條串流的邏輯座標」。`PortalCapture` 因此把 portal 的邏輯版面**以最大縮放倍率放大**當作像素位置：每台螢幕的像素矩形都落在自己的邏輯矩形放大後的範圍內，彼此不重疊，一個點只屬於一台。`PortalInputInjector.ToStream`（純函式）找出點所在的螢幕、換回該串流的邏輯座標與 node id；點在所有螢幕之外就貼到最近那台的邊緣。
- **鍵盤**：Map 模式的碼本來就是 evdev 鍵碼，portal 的 `NotifyKeyboardKeycode` 也收 evdev——不能像 X11 那樣加 8。控制鍵查 `Evdev.ForControl`。文字逐字以 keysym 送（換行、Tab、Backspace 換成對應的功能鍵 keysym）。
- **非 ASCII 打不出來（GNOME 46）**：計畫原本以為 `NotifyKeyboardKeysym` 能直接送任何 Unicode、藉此退休 X11 的剪貼簿繞路。實測不成立：mutter 只在目前的鍵盤配置裡找 keysym，找不到就丟棄並記 `No keycode found for keyval e9 in current group`，é、ü、中文都一樣。配置上有的字（例如法文配置的 é）可以；其餘要等第 4 階段跟剪貼簿一起做（貼上）。
- **必須一次一個**：連續送出的 Notify 呼叫會被重排——xdg-desktop-portal 用執行緒池處理方法呼叫，放開可能超車按下，mutter 記 `Received multiple virtual key presses (ignoring)` 並讓鍵卡住。`echo "portal` 打出來是 `e"CPORh otal`。現在所有輸入經由一個佇列，**每個呼叫等到 portal 回覆才送下一個**（portal 轉交後才回覆，所以順序得以保持）；排隊中的連續指標移動只留最後一個位置、相對移動相加（`PortalSession.Merge`），portal 慢時指標是晚到而不是重播軌跡。
- **滾輪**：協定的正值是往上（遠離使用者），portal 的離散軸正值是往下、往右，所以垂直取負、水平照送。按鈕用 evdev 碼（`BTN_LEFT` 等）。
- **能力**：對方在對話框裡沒開「允許遠端互動」時（`devices` 沒有指標或鍵盤），`Capabilities` 就不宣告 Mouse／Keyboard，事件直接丟棄並記一次。鎖定螢幕交給 logind（引擎就是工作階段的擁有者）。鎖定鍵狀態讀不到，不宣告；Ctrl+Alt+Del 只是送給合成器的三個鍵（GNOME 是登出對話框），不是安全注意序列。

**實測（Linux 測試機）**：`LinuxHarness portal-input` 經由 `PortalCapture`＋`PortalInputInjector`：點選終端機、打 `echo 'portal typed: The Quick Brown Fox 1234567890 @#$%^&*()_+{}|:<>?~ done'`（一字不差，含所有 Shift 字元）、用 Map 模式鍵碼打 `ls` 與 Enter、`seq 1 80` 之後滾輪往上 5 格（畫面停在第 43–66 行），GNOME 日誌沒有任何重複按鍵警告。每一步的畫面都由 portal 自己的擷取取得。

**H4a 接進引擎**（`PortalHost`、`IDisplaySession`、`DisplaysChanged.notice`）：

- **選擇**：Wayland 工作階段（`XDG_SESSION_TYPE=wayland` 或有 `WAYLAND_DISPLAY`）的引擎改走 portal（`PlatformServices.LinuxWayland` → `LinuxHostPlatform.CreateWayland`）；X11 在 Wayland 上只看得到 Xwayland 的程式，**不再是後備**，要 `--x11` 或 `SUNLLO_X11=1` 明確選。daemon 的 DRM 路徑不變。shim 不在時引擎照樣啟動，改由每個檢視端聽到原因。
- **有人在看才開**：`PortalHost` 同時是顯示器列舉、擷取器工廠、輸入、游標與新的 `IDisplaySession`。一直開著會讓 GNOME 右上角的「分享中」一直亮、沒有 token 時一登入就跳對話框，所以**第一位檢視端授權時才開、最後一位離開時關**（`HostMediaModule`）。開啟在背景做：授權掛鉤跑完之前，這個工作階段不處理收到的訊息，不能在那裡等最多 45 秒的同意。開好後廣播新清單，並把還沒在看螢幕的檢視端切到主螢幕；多位同時到只問一次；等待中大家都離開就收回對話框。
- **主機的說明**：協定新增 `DisplaysChanged.notice = 5`（只加欄位）。這是主機主動說「為什麼螢幕是現在這樣」，不是回覆某個請求，所以清單照收（`failure` 則不動清單）。內容包括：超過 1.5 秒還在等人按時的「正在詢問」、對方拒絕、逾時、沒有 portal 後端（指名 `xdg-desktop-portal-gnome`／`-kde`）、畫面讀不到，以及對方從頂列停止分享（`Session.Closed` → `IDisplaySession.Closed`）。新加入等待中的檢視端會立刻收到目前的說明。桌面檢視端在「沒有螢幕」的位置改顯示這句話（`HostNotice`）。舊檢視端只會看到一般的清單變更。
- **游標**：`cursor_mode` metadata，游標另外傳、不畫進畫面。shim 讀 `SPA_META_Cursor`；`PortalScreenCapturer.Cursor()` 回傳位置（加上該螢幕在虛擬畫面的位置）與形狀（RGBA 轉 BGRA，mutter 給的已是預乘；以內容雜湊當 id，所以同一個形狀在任何螢幕、任何時候都是同一個 id，檢視端可以快取）。形狀序號變了才複製點陣圖。**游標 meta 的大小範圍必須涵蓋合成器提出的值**：上限原本寫 256×256，mutter 提出更大的固定值，兩邊沒有交集，meta 被靜靜丟掉，串流完全沒有游標；改成跟 OBS 一樣的 1024×1024 後才有。還沒看到任何形狀前給一般的箭頭。
- **剪貼簿與非 ASCII**：剪貼簿沿用 `X11Clipboard`，經由 Xwayland 與 Wayland 剪貼簿雙向橋接。keysym 打不出來的字（第 3 階段發現的限制）改成存剪貼簿 → Ctrl+V → 還原，與 X11 相同；終端機要 Ctrl+Shift+V，一樣是缺口。
- **測試**：`DisplaySessionTests`（整合，`FakeDisplaySession`）：第一位開、最後一位關、詢問中與拒絕的說明、從主機端停止、兩位共用一次詢問、等待中離開會收回問題。Linux 單元：游標轉換與 id、只在形狀改變時複製、非 ASCII 走貼上、失敗原因的文字。PeerCli 多了 `ipc-password`（以指定 token 啟動的測試引擎可設臨時密碼；`IpcClient` 多一個接受現成 Stream 的多載，因為 `ConnectPaths` 永遠先試 daemon 的系統 socket）。

**實測（Linux 測試機，真正的 `DeskPair --server`，以登入使用者身分在 GNOME Wayland 工作階段）**：啟動記錄 `Linux host (Wayland): ... (PipeWire shim loaded) ... clipboard through Xwayland, encoders: Vp9Software`。
第一次連線，資料目錄裡沒有 token：檢視端登入時 0 個螢幕，收到「正在詢問」的說明；在主機畫面上按下同意後，立刻收到 1280×800 的螢幕和畫面。經 portal 點選、打字（`echo wayland engine ok` 有執行），從檢視端送出「中文貼上測試 OK」到主機剪貼簿，再用 Ctrl+Shift+V 貼上，中文正確出現。
引擎重啟後第二次連線：**沒有對話框**，6 ms 開啟。從另一條路（daemon 的 uinput）移動真正的滑鼠，檢視端收到 I 字形與箭頭兩種 24×24 形狀，以及位置更新。檢視端離開後日誌記 `Screen sharing closed: nobody is watching`，顯示器變成 none，分享指示消失。

**H4b 在設定頁先問好**（`Wayland/PortalPermission`；設定 › 安全「允許遠端分享螢幕」）：

- **為什麼**：沒有 token 時，要等第一位檢視端連進來才跳對話框，按的是當時剛好坐在螢幕前的人，或者沒人按、45 秒後逾時。設定頁的按鈕把它變成刻意的安裝步驟：坐在這台電腦前的人按「允許…」，對話框出現在自己面前，按「分享」後答案存起來，之後的檢視端不再問。
- **三種狀態**：token 旁邊多存 `wayland-portal-<uid>.devices`（`Start` 回的 devices，4 bytes），設定頁因此分得出「已允許」（鍵盤＋指標）、「只能看」（沒打開「允許遠端互動」）與「尚未允許」。只有 token、沒有 devices 的舊資料算已允許（H1–H4a 存下的都是按過分享的）。token 刪掉時 devices 一起刪。
- **一定要跳對話框**：這次握手不附 restore token（`offerRestoreToken: false`），否則允許過的機器按了也不會問，「只能看」就改不過來。問完立刻關掉工作階段（分享指示只亮一下），答案存在引擎會讀的地方：App 內的引擎與 `--server` 用同一個資料目錄（`ServerRole.DefaultDataDir()`），鍵也含 uid。
- **結果**：允許、只能看、拒絕、沒人回答、其他失敗，各有一句十種語言的說明；拒絕與逾時不動已存的答案。`LinuxPlatformInfo` 在 Wayland 上不再一律回 `None`、`RequestPermissions` 不再是空方法：兩者都照這份狀態與這次詢問。
- **何時不顯示**：不是 Wayland 工作階段；或是裝了無人值守存取。後者的 App 把引擎交給 daemon（DRM＋uinput，不經 portal），這裡問到的答案用不到。
- **對話框的字**：說明引用 GNOME 46 在各語言的標籤（「遠端桌面」「允許遠端互動」「記住此選擇」「分享」）；日文版 GNOME 這個對話框沒有翻譯，照英文原字。
- **Tmds 版本**：H1 用 Tmds.DBus.Protocol 0.95.1，引擎（不載入 Avalonia）一切正常，**但 Linux 的 App 一啟動就當掉**：Avalonia.FreeDesktop 11.3 是對 0.21 編的，一個應用程式只有一份組件，新版蓋掉舊版，而 0.95 改了型別名稱（`X11DBusImeHelper` 裡 `TypeLoadException: Tmds.DBus.Protocol.Connection`）。改回 0.21.3 並改寫 `PortalSession`（`Connection`、`Address.Session`、`DBusException`、`AddMatchAsync` 的 Action 版）；`TmdsVersionTests` 比對 Avalonia.FreeDesktop 參考的版本與我們的，不同就失敗。**Linux 的相依套件有變時，要真的開一次 App**，只跑引擎看不出來。

**實測（Linux 測試機，繁中介面）**：設定 › 安全出現「允許遠端分享螢幕」一節。按「允許…」即使已有 token 也跳對話框；打開「允許遠端互動」再按「分享」→「已允許」；只按「分享」→「只能看」；按「取消」→ 說明已拒絕，狀態不變。之後 `busctl` 看不到殘留的 portal 工作階段。改回 0.21.3 後重跑 `portal-grab`：5 ms 開啟、畫面不複製直接交出。

**手機顯示主機的說明**：共用層的 `RemoteSession.hostNotice` 由每一則 `DisplaysChanged` 覆寫（拒絕的回覆也帶著目前的說明），Android 與 iOS 在主機**沒有螢幕、而且說了原因**時，以主機的原話取代畫面。不轉圈：主機可能正在詢問，也可能已被拒絕，只有它的原話分得出來。已經有畫面時也蓋上去，因為在那台機器上停止分享會留下最後一張。以前手機只會轉圈，六秒後說「對方電腦可能停在 Windows 的鎖定或登入畫面」，對 Wayland 主機是錯的。沒有新字串：說明是主機的原文，與桌面檢視端相同。

**實測（iPhone 17 模擬器 → Linux 測試機的真引擎，經中繼）**：刪掉 token 後連線，手機顯示「正在詢問」；沒人按，45 秒後自己換成「沒有人回答」。重連後在測試機上按「取消」→「對方拒絕」。再重連、打開「允許遠端互動」並按「分享」→ 螢幕出現。接著按測試機頂列分享指示上的停止鍵，手機的畫面換成「對方停止分享」。

**發現：手機只收 H.264**。像這台 Linux 測試機這樣沒有 VAAPI、也沒有 OpenH264 的 Linux 主機（Wayland 的 VM 多半如此）只能編 VP9；手機那邊的人一按「分享」，就收到「this computer has no H264 encoder」，連線結束。實驗時在測試機上放了 Cisco 自己發佈的 `libopenh264-2.6.0`（從 ciscobinary.openh264.org 直接下載到那台，以 `SUNLLO_OPENH264_PATH` 指定），不進 repo。這件事的解法是讓手機也能解 VP9，見下一段。

**手機解 VP9**：
- **誰決定**：`VideoSink.formats` 由各平台的接收端宣告自己真的解得出哪些格式，`connect` 照著填 `SupportedDecoding`；每一格都帶著主機選的格式（`VideoFormat`，TCP 取 `VideoFrame.codec`、UDP 取封包標頭的 codec 位元組）。主機的選法不變：主機設定的偏好 → 檢視端一致的偏好（手機偏好 H.264）→ H.264 > H.265 > AV1 > VP9。所以能編 H.264 的主機照樣送 H.264（手機硬體解、省電），只有編不出 H.264 的主機才送 VP9。
- **Android**：`MediaCodecList.findDecoderForFormat` 問得到 1920×1080 的 VP9 解碼器才宣告；`SurfaceVideoSink` 依格式選 MIME，格式或尺寸一變就重建解碼器。只會 VP9、螢幕又超過那支手機 VP9 上限的主機，手機會顯示「沒有畫面：…」與解碼器的原因，而不是黑畫面。
- **iOS**：VideoToolbox 的 VP9 是「補充解碼器」：iOS 26.2 起才有 `VTRegisterSupplementalVideoDecoderIfAvailable`，而且只對呼叫過的行程有效。App 登記後實際建一個 8-bit 4:2:0 的解碼工作階段，建得起來才宣告 VP9（`VideoDisplayView.decodesVp9`）。VP9 不交給 `AVSampleBufferDisplayLayer` 自己解，而是用 `VTDecompressionSession` 解成 IOSurface 畫面再交給同一個 layer，確定用得到本行程登記的解碼器。VideoToolbox 解 VP9 需要格式描述裡的 `vpcC`（ISO 媒體綁定的 VP Codec Configuration Record），直播沒有容器，所以由共用層的 `Vp9Configuration` 從每個關鍵幀的 uncompressed header 讀出 profile、位元深度、色度取樣、色域與矩陣、尺寸，level 依尺寸以 60 fps 選。
- **主機標示色彩空間**：主機一律以 BT.601、studio range 把 BGRA 轉成 I420（`PixelConversion`），但 libvpx 預設在位元流裡寫「未知」，會讓信任位元流的解碼器自己猜，手機多半猜 HD 就是 BT.709。現在 VP9 編碼器設 `VP9E_SET_COLOR_SPACE = VPX_CS_BT_601`（控制碼 46，`VpxLayoutTests` 依 `vp8cx.h` 的列舉位置核對）。桌面的 libvpx 解碼端本來就用 `PixelConversion` 轉回去，不受影響。

**實測（Pixel 10 → Linux 測試機只開 VP9 的引擎）**：Pixel 的 `c2.google.vp9.decoder` 解出 1280×800 畫面；同一台引擎帶上 OpenH264 時，改選 H.264（`c2.google.avc.decoder`）。Kotlin 的 `LiveVp9Test` 以 ID 連線、只宣告 VP9，拿到的真實 libvpx 1.14.0 關鍵幀開頭是 `83 49 83 42 20 4f f0 31 f0`，確認標頭解析與 BT.601 標示（`error_resilient_mode` 為 1，解析不讀這一位）；這組位元組也成了單元測試的錨點。Pixel 的解碼器回報的輸出色彩是 BT.709，也就是它沒有採用位元流裡的色彩空間，H.264 路徑一樣，屬平台行為。**iOS 模擬器沒有 VP9 解碼器**（建立工作階段回 -12906），所以 App 只宣告 H.264，只會 VP9 的主機照舊結束連線；iOS 的 VP9 解碼路徑要等一支 iOS 26.2 以上的實體 iPhone 才能驗證。

**Android（Pixel 10、Android 17，接在 Mac 上）**：同樣四種情況都正確（詢問 → 取消後的拒絕 → 分享後的 H.264 畫面 → 從頂列停止）。Mac 上的模擬器（AVD `pane3qr`）完全沒有 DNS，解析不了中繼的主機名稱，所以改用實機。實機順便抓到一個與 H4b 無關的當機：深色模式下 App 一啟動就關閉。十種語言那次把 `MainActivity` 改成 `AppCompatActivity`、主題改成 AppCompat，但 `values-night` 裡的主題副本還是框架的 Material 主題（`fa83ae5` 刪掉該檔，`ThemeTest` 檢查每個資源資料夾）。模擬器都是淺色模式，所以一直沒發現。

**H5 桌面矩陣、鎖定與自動化測試**（進行中）：

| | GNOME 46（Linux 測試機） | KDE Plasma 5.27（Linux 測試機，同機另一個工作階段） | wlroots（sway 等） |
|---|---|---|---|
| portal 介面 | RemoteDesktop 2＋ScreenCast 5 | RemoteDesktop 2＋ScreenCast | 只有 ScreenCast（xdg-desktop-portal-wlr） |
| 第一次要不要問 | 跳對話框，要有人按「分享」 | **不問**，只發通知「Remote control session started」 | 依設定（chooser），可以不問 |
| 記住同意 | restore token，之後 5–16 ms 不跳窗 | 沒有 token（反正不問） | ScreenCast 4 起在 SelectSources 記 |
| 遠端輸入 | 有（打開「允許遠端互動」時） | 有 | **沒有**：只能看 |
| 串流位置 | 有 | **沒有**：依順序左到右排、名稱 `wayland#1` | — |
| 沒變化時的畫面 | 有變化才送 | 以約 53–60 fps 持續送 | — |
| 鎖定畫面 | **分享立刻被結束** | 分享繼續，看得到鎖定畫面，**遠端輸入密碼可以解鎖** | — |
| 兩台螢幕 | 第一次分享正確（0,0 與 800,0）；**記住的同意還原時，兩台相同的螢幕會變成同一台兩次** | 正確：portal 自己把串流座標換算到整個桌面 | — |

- **只能看的後備**：portal 沒有 RemoteDesktop（wlroots 桌面）時改開 ScreenCast 工作階段，`Devices` 為 0，檢視端能看不能操作（`PortalSession.RemoteControl`）。兩種都沒有時，訊息同時指名 gnome／kde／wlr 三個後端套件。xdg-desktop-portal-wlr 0.7.1 在容器裡接受了整段對話（建立、選來源含 `persist_mode 2`、Start），之後因為拿不到 dmabuf 而擷取失敗；有 GPU 的桌面才是它真正的使用情境，尚未實機驗證。
- **GNOME 鎖定**：鎖定時 GNOME 會結束 portal 分享。現在 `PortalHost` 在分享被結束時問 logind 使用者圖形工作階段的 `LockedHint`（經由 User.Display 找，因為從 shell 啟動的引擎屬於 shell 的工作階段）；分享結束得比鎖定記錄早一點，實測第一次問是 false，所以 1 秒後再問一次。鎖定時告訴檢視端「螢幕已鎖定，解鎖後畫面會回來」，之後每 2 秒問一次，解鎖就發 `IDisplaySession.Reopenable`，`HostMediaModule` 為還連著的檢視端重新開啟（用記住的同意，不跳窗）。實測：17:22:44.9 鎖定、一秒後收到說明、17:23:00 解鎖、17:23:02.0 以 14 ms 重開、檢視端不用重連就回到螢幕 0。**遠端無法解鎖 GNOME**：鎖定期間沒有 portal 工作階段，也就沒有輸入，所以 H6（無人值守組合）從選配變成必做。
- **順帶修掉的 bug**：所有螢幕同時消失時（Wayland 分享結束、最後一台螢幕拔掉），檢視端的訂閱原本原封不動，影像服務卻都停了；螢幕回來時舊訂閱被當成「已經在看」，不會重新開始串流，檢視端一直看著黑畫面。現在會清空並標記，螢幕回來時放回主螢幕；自己選擇不看任何螢幕的（檔案傳輸、聊天）不受影響。兩個新整合測試在修正前都會失敗。
- **KDE 的特性**：同意由 KDE 自己決定，Plasma 5.27 對非沙箱程式只通知不詢問。鎖定時分享不中斷，檢視端看得到 kscreenlocker，並能經由 portal 的鍵盤輸入密碼解鎖（實測解鎖成功）。daemon 的 DRM 擷取在 KWin 下只看得到主平面，畫面上方與部分區域是黑的，這會影響 H6 在 KDE 上的做法。
- **CI**（`tools/wayland-ci`）：原本計畫用 sway 無頭模式加 wlroots portal，但它在沒有 GPU 的容器裡無法擷取（見上）。改成 `test-screen.c` 假螢幕：一個 PipeWire 影像來源，BGRx，單色加一條每格移動的白線，緩衝區照 mutter 的做法自己用 memfd 配置（由 PipeWire 配置的緩衝區到另一個行程會變成 MemPtr，只會走複製路徑）。`LinuxHarness pipewire-grab` 不經 portal，直接連 PipeWire daemon 的 socket（portal 的 `OpenPipeWireRemote` 給的也是同一種連線），經由產品的 shim（容器內從原始碼編）和 `PortalScreenCapturer` 讀取，零複製與複製各跑一次，要求畫面一直在變、主色正確，且第一次確實沒有複製。CI 多了 `wayland` job。本機實測：兩條路徑各 92 格，第一次「4 buffers, handed on uncopied」。

**兩台螢幕實測（2026-09-26）**：Linux 測試機是 VMware VM，兩台螢幕用 `vmw-layout`（對 vmwgfx 下 `DRM_VMW_UPDATE_LAYOUT`，等於 VMware Tools 收到主機要求的拓樸）加上 `kscreen-doctor` 設定；這台 VM 的顯示記憶體只有 4 MiB，兩台 800×600 放得下，兩台 1280×768 放不下（EINVAL）。重開機後會回到一台。

- **KDE Plasma 5.27**：viewer 在第 1 號螢幕移動後，KWin 的 `activeOutputName` 是 Virtual-2；在第 0 號移動後是 Virtual-1。5.27 的串流沒有位置，但 portal 自己會把串流內的座標加上該串流在桌面上的位置（`requestPointerMoveAbsolute(pos + geometry().topLeft())`），所以左到右排版加上串流內座標就是對的。
  中途一度以為 5.27 把座標當成整個桌面的座標，還試了用 Xwayland 查位置再自己加上原點，結果被前端拒絕（`Invalid position`）：xdg-desktop-portal 前端會檢查座標是否落在「那條串流自己的大小」之內，所以絕對座標一定是串流內的座標。真正的原因是 PeerCli 的 `:move` 從來沒有帶 `Display`，`:switch 1` 之後的滑鼠仍然落在第 0 號螢幕（`0dadd4a`；桌面與手機一直有帶）。
- **GNOME 46**：第一次分享時 portal 給出 `node 62 800x600@0,0; node 58 800x600@800,0`，名稱 `wayland@0,0`、`wayland@800,0`。把指標移到 viewer 的第 1 號螢幕後開計算機，mutter 把視窗開在第二台（新視窗開在指標所在的螢幕）；在第 1 號螢幕上點「7 + 2 =」，第二台螢幕上的計算機顯示 `7+2 = 9`。
- **GNOME 記住的選擇認不出相同的螢幕**：還原資料以「廠商:型號:序號」記每台螢幕（permission store 裡是兩筆 `'unknown:unknown:unknown'`），還原時兩筆都找到第一台，於是得到兩條同一台螢幕、位置都是 0,0 的串流。VM 的虛擬螢幕沒有 EDID，實體螢幕中同型號又沒有序號的也會這樣。`PortalCapture.Describe` 現在略過與前面某條串流位置、大小都相同的串流，並記一行警告說明原因；第二台螢幕在這種情況下不會被分享，這是 GNOME 端的限制，我們這邊沒有辦法讓它記得對。
- **daemon 在多螢幕上的限制**（影響 H6）：DRM daemon 只擷取第一個掃描輸出（`scanout-43`，800×600），但它的 uinput 絕對座標指標被 libinput／mutter 對應到**整個桌面**（兩台共 1600 寬），所以 daemon 的點擊在 x 方向被放大一倍，落到別處；在登入畫面按使用者頭像、按對話框都要把 x 減半才按得到。鍵盤不受影響（密碼照樣打得進去）。要修得先知道合成器的桌面配置，KMS 裡沒有這個資訊；列為 H6 的題目。**H6f 已修鎖定畫面**（代理回報 Mutter 的配置，見下段）；登入畫面沒有代理，仍會偏。
- **daemon 的引擎當掉**（SIGSEGV，`/var/crash/_opt_deskpair_DeskPair.997.crash`）：`memcpy` 讀 scanout 時碰到已解除映射的位址。擷取器在 `DrmCaptureChannel.Poll()` 的鎖外複製畫面，而列舉器（輸入每來一個滑鼠事件就問一次顯示器）的輪詢可能在這時替換或丟掉映射。現在只有在輪詢的鎖裡、經由 `DrmFrameReader` 才能讀映射，`DrmPollResult` 不再交出映射；引擎端的映射最多保留 `KnownFbSlots` 個（最近用過的，全部都會在下一次輪詢被點名），daemon 端握著的 dma-buf 最多 8 個。之前兩邊都沒有上限：core 裡有 8 段映射，其中好幾段是同一個 buffer，因為超過 4 個之後沒被點名的那些一直被重送、替換，也讓競態更容易發生。新版（`c6e9e01`）裝到測試機的 daemon 後，viewer 看著畫面、指標在 Dock 上來回掃約 100 秒（每次移動都觸發一次列舉器輪詢，hover 效果讓畫面一直變），期間 daemon 替換過映射（`Framebuffer id 116 now names a different buffer`），引擎沒有當掉。
- **OpenH264 的碼率卡在起始值的兩倍**：`SetBitrate` 只調整整體上限，OpenH264 卻是拿目標碼率跟「每一層」的上限比，於是超過起始值兩倍的調整全部被拒絕（`MaxSpatialBitrate (3000000) should be larger than SpatialBitrate (10080000)`），編碼器一直停在起始的碼率。現在兩個上限一起動，調高時先動上限、調低時後動上限；新測試在 Linux 容器裡用 Cisco 的 2.6.0 驗證，舊寫法會在 10000 kbps 失敗。測試機的 daemon 換成新版後，引擎從 1500 kbps 起跳、被探測拉到 10–15 Mbps，OpenH264 沒有再報錯。
- **PeerCli 的 `:save`**：沒有 `--save-frame`／`--frame-stats` 時用的是假解碼器，讀不懂真實主機的影像，畫面只來自無損 tile；連續 12 個影格解不出畫面後，那個螢幕會等一個假解碼器永遠接不住的關鍵幀，此後拒收 tile、每秒要一次重新整理。這是測試工具的現象（真正的客戶端會解碼），說明已寫進 PeerCli；對真實主機存圖要加 `--frame-stats`。

**H6 無人值守組合（2026-09-26）**：裝了無人值守的 Linux，daemon 在 seat0 是某個使用者的 Wayland 桌面時，以那個使用者身分起一個
**工作階段代理**（`--session-agent`），代理只做 portal：開工作階段、把每條串流的 PipeWire 連線以 SCM_RIGHTS 交給引擎、把輸入轉成
Notify*。引擎（`deskpair` 帳號）的畫面是 `ScanoutPortalHost`：解鎖時經 portal（每台螢幕、游標、依串流的輸入），鎖定、登入畫面、
沒人登入時用顯示硬體，切換只是顯示器清單改變，檢視端不斷線；GNOME 鎖定結束分享 → 看到鎖定畫面、遠端輸入密碼解鎖 → portal 自動重開，
Linux 測試機實測在同一條連線裡走完。portal 只在不會詢問時開（有使用者的同意，或 KDE Plasma 5），同意由設定頁經 IPC（32／33）請引擎→代理問一次。
與原計畫「另起一個引擎」不同：一個 ID、一個網路端點，機器的身分金鑰不必交給使用者的行程。細節、實機踩到的三件事與限制在
`docs/unattended-linux.md` J 節。**H6f**：代理讀 `org.gnome.Mutter.DisplayConfig` 回報每台螢幕在桌面上的位置（`AgentWire` 10），daemon 經 encoder
說出 scanout 是哪個接頭（DrmWire 畫面資訊 88–95），引擎把硬體畫面上的點換到整個桌面，GNOME 鎖定畫面兩台螢幕時的點擊不再偏（Linux 測試機實測）；
KDE 5.27 重測全部通過；重測抓到代理在登入瞬間啟動、讀不到桌面名稱的競態，supervisor 改成等這個工作階段的桌面起來才啟動代理。

### macOS (`Platform.MacOS`；**ObjC shim dylib `SunlloMacShim.dylib` 提供 C ABI**，C# P/Invoke)

不用手刻 `objc_msgSend` (block/delegate/CMSampleBuffer 與 arm64/x86_64 struct ABI 太脆弱)；shim 只做 marshaling，政策在 C#。

| 項目 | API | 備註 |
|---|---|---|
| 擷取 | ScreenCaptureKit `SCStream` (12.3+)；`CGDisplayStream` 後備 | 需 Screen Recording TCC |
| 編碼/解碼 | VideoToolbox `VTCompressionSession`/`VTDecompressionSession`，IOSurface 進出 | |
| 輸入 | `CGEventCreateMouseEvent`/`CGEventCreateKeyboardEvent` + `CGEventPost` | 需 Accessibility TCC |
| 游標 | `NSCursor.currentSystemCursor` + seed | |
| 音訊 | ScreenCaptureKit 系統音訊 (13+) / CoreAudio 播放 | |
| 剪貼簿 | `NSPasteboard.changeCount` 200 ms 輪詢 | |
| 服務/Session | 沒有 LaunchDaemon。只有一個**使用者層級**的 LaunchAgent (`~/Library/LaunchAgents/com.sunllo.deskpair.login.plist`，`LimitLoadToSessionType = Aqua`，無 KeepAlive)，由設定頁的「登入時啟動」寫入，不需要管理員權限。單一執行檔的理由是 TCC 逐執行檔授權，子程序引擎會變成第二個沒被授權的 client | 舊版留下的 `~/Library/LaunchAgents/com.sunllo.deskpair.plist` (不同 label) 由 `LegacyInstall` 自動移除；TCC 無法靜默授權，需穩定 Developer ID + notarization |
| 密文儲存 | Keychain (shim) | |

### 軟體後備編解碼 (三平台)

`DeskPair.Codec.OpenH264` 仍在（OpenH264 的 P/Invoke 與動態載入器），但**本專案不再隨附任何編譯好的二進位檔**。BSD-2 授權的是原始碼；H.264 的專利費另計，而 Cisco 只為「它自己散布、執行期從 `ciscobinary.openh264.org` 下載」的那份代付。自建的二進位檔不在保護範圍，所以不散布。

載入器依序找 `SUNLLO_OPENH264_PATH` → 執行檔旁 → `runtimes/<rid>/native/`；自備一份（自有 AVC 授權，或使用 Cisco 官方下載的那份）即可恢復，不需重建。找不到時 `Probe()` 回報 `None`，`FallbackVideoEncoderFactory` 直接跳過。見 `native/openh264/README.md`。

Windows 上實際走 Media Foundation（授權含在 Windows 裡）。**Windows N/KN 版**未裝 Media Feature Pack 則沒有 H.264 編解碼器：`MfVideoDecoderFactory.Probe()` 會如實回報 `None`，主機端以 `CloseReason` 明確告知對方，控制端則退回無損區塊路徑（不經過解碼器）而不是死當。H.265 與 AV1 沒有 in-box 編碼器，只有廠商 MFT（隨驅動程式安裝）或 Store 的 HEVC/AV1 Video Extensions，所以它們能不能用是逐機器探測出來的，不是寫死的。

編碼器與解碼器都以 `MFTEnumEx` 對 **每一種編碼格式**各列舉一次（`MfVideoEncoder` / `MfVideoDecoder`），硬體優先；`MfVideoEncoderFactory.Describe()` 回傳每一個 `EncoderDescriptor`（格式、後端、廠商、友善名稱、是否硬體）。解碼器的輸入型別必須帶 `MF_MT_FRAME_SIZE`：H.264 的 MFT 沒帶時會自己補 1920x1080，H.265/AV1 的不會，它們會給出一個 `SetOutputType` 會拒絕的輸出型別，讀起來就像「沒有這個解碼器」。我們先帶一個名義尺寸，第一個關鍵幀再透過 `MF_E_TRANSFORM_STREAM_CHANGE` 修正。

### 免權利金的軟體編碼器：VP9

`src/DeskPair.Codec.Vpx` 綁 libvpx 的 C API。**這是與 OpenH264 最大的不同**：libvpx 是 BSD-3，而 VP9 的專利由 Google 免權利金授予任何人、任何用途，沒有「只限 Cisco 自己散布的二進位檔」那種條件，所以**這份二進位檔是可以隨產品散布的**。這是 H.264/H.265 之外唯一一條不依賴硬體、也不依賴別人授權的路：沒有 VAAPI 的 Linux、VM、精簡版 Windows 都靠它。

綁定層以**位移**讀寫 libvpx 的結構，因為 `vpx_codec_enc_cfg_t` 尾端會隨版本成長而前綴自 1.8 後沒動過。兩道防線：`VpxLayoutTests` 從 C 宣告推導每一個位移（而不是拄常數）；編碼器在寫入任何欄位前，先讀回 libvpx 剛填的預設值（320x240、timebase 分母 30、`kf_max_dist` 9999）確認佈局，不對就拒絕使用——否則錯誤會以「畫質很奇怪」而非錯誤的形式出現。ABI 版本是編譯期常數，不匹配會被干淨地拒絕（`VPX_CODEC_ABI_MISMATCH`），所以初始化時會在預期值周圍試一小段範圍。

編碼參數是桁面而非影片：CBR、`g_lag_in_frames = 0`（預設 25 幀，對遠端桌面就是將近一秒的延遲；設 0 也讓每次編碼恰好產生一個封包）、VBV 緩衝 1000/500/600 ms、`VP9E_SET_TUNE_CONTENT = SCREEN`、cpu-used 7、row-mt 與依寬度算出來的 tile columns。關鍵幀只在 session 要求時發。見 `native/libvpx/README.md`。



**Windows 能不能自己編 VP9？結論：無法組態，不能當成基礎。**（`MfDiagnosticsTests.Report_whether_media_foundation_offers_vp8_or_vp9` 與 `..._can_be_configured`，保留為診斷測試）

量到的事實：

- 登錄檔的 MFT 類別下沒有 VP9，但 `MFTEnumEx` **找得到** `VP9VideoExtensionEncoder` 與 `VP9VideoExtensionDecoder`（商店的 VP9 Video Extensions 套件）。
- 編碼器可 `ActivateObject`（1 進 1 出），**是同步 MFT**（不需 `MF_TRANSFORM_ASYNC_UNLOCK`）。
- 它宣告輸出型別 VP80 與 VP90，輸入型別 IYUV/NV12 等四種，而且 **`SetInputType(NV12)` 是成功的**。
- 問題在輸出型別：拿它自己 `GetOutputAvailableType` 給的型別，逐一加屬性逐步測試得到——
  - 只加 `MF_MT_FRAME_SIZE` → `MF_E_ATTRIBUTENOTFOUND`（還缺東西）
  - **只要含有 `MF_MT_FRAME_RATE` → 一律 `E_FAIL`**，與數值（30 或 25）、順序（先設輸入也一樣）、是否加 PAR／interlace／bitrate 皆無關。同一套寫法在 H.264 MFT 上是正常的。

所以這**不是「它拒絕任意呼叫者」**，而是它的輸出型別契約與文件化的編碼器模式不同，而這個元件沒有公開文件（它是給 Edge/WebRTC 管道用的商店元件）。我無法靠猫測湊出它要的屬性組合。

傑論：不走這條路——不是因為做不到，而是因為它**未公開且是商店交付**：契約靠逆向猜測、隨時可能因服務更新而變、且不能假設每台機器都裝了。軟體免權利金路徑還是自己做（libvpx）。

**尚未試過的一條路**：不碰原始 `IMFTransform`，改用 `IMFSinkWriter`（我們錄影已經在用）要求 VP9 輸出，讓 Media Foundation 自己去協商型別。即時串流需要的是封包而非檔案，要搭自訂 byte stream，不是小工程；但如果以後要重新評估，這是下一個該試的方向。

---

## 9. 程序模型、常駐服務、IPC

- **三平台都是單一執行檔**（不是單一程序——這句話以前寫錯了）。理由是 macOS：TCC 逐執行檔授予「畫面錄製」與「輔助使用」，第二個執行檔就是第二份同意書。同一個 Mach-O 以不同 argv 扮演 App、引擎、daemon，簽章、bundle id、TCC 授權都只有一份；改變的是誰執行它、在哪個 session，那兩者都不是 TCC 的身分。
- **無人值守是安裝上去的角色**（`--install-service`），三平台形狀不同：Windows 是服務（SYSTEM）把引擎生在 console session；macOS 是一個 `LimitLoadToSessionType = [LoginWindow, Aqua]` 的 LaunchAgent，由 launchd 搬進有螢幕的 session（未修改的 .NET apphost 就進得去，`__CGPreLoginApp` 不需要）；Linux 是 root daemon（`sunllo-deskpair.service`）用 DRM/KMS 讀 scanout、用 uinput 注入，引擎以固定的 `deskpair` 帳號跑，session 變動時引擎 pid 不變、連線不斷。GNOME 的鎖定畫面與登入畫面都**不在 X 裡**（合成器直接畫進 framebuffer），所以 Linux 沒有 X11 的便宜路。實測紀錄：`docs/unattended-windows.md`、`docs/unattended-linux.md`。沒安裝時，開機後要能被連只靠「登入時啟動」（Windows HKCU `Run`，macOS 使用者自己的 LaunchAgent，`StartupEntry`）。
- `DeskPair` 角色：無參數＝App (主視窗 + 工作列 + 連線管理員 + 引擎)、`--server`＝只跑引擎不開視窗、`--allow-firewall`/`--remove-firewall`、`--version`。引擎在 `Desktop/Engine/ServerRole.cs`，由 `Services/EngineHost.cs` 在背景 Task 上啟動。
- IPC (`Core/Ipc`)：Windows `NamedPipeServerStream` `\\.\pipe\DeskPair\<role>`，`PipeSecurity` 只允許 SYSTEM + 互動使用者 SID，`GetNamedPipeClientProcessId` 驗證對端 exe 路徑；
  Unix `UnixDomainSocketEndPoint`：使用者引擎在 `$XDG_RUNTIME_DIR/deskpair/DeskPair.sock`（沒有 XDG 時 `$TMPDIR/deskpair-$USER/`），root 或 `--ipc-system` 的引擎在 `/run/deskpair/DeskPair.sock`（macOS：`/Library/Application Support/Sunllo/DeskPair/DeskPair.sock`），用戶端先試系統路徑再試使用者路徑；`IpcCaller.Of(Socket)` 以 `SO_PEERCRED`（Linux）／`LOCAL_PEERCRED`（macOS）取得對端 uid/pid，分類與 Windows 同一份 `IpcAuthority`；另加啟動時傳入的一次性 token (`FixedTimeEquals`)，daemon 下的引擎把 token 發佈在 `/run/deskpair/<uid>/ipc.token`。
- 設定：引擎端為權威 (Windows `%ProgramData%\Sunllo\DeskPair\config.json`；macOS/Linux 因為以登入使用者身分執行，預設落在家目錄下，只有 root 程序才用系統路徑——見 `ServerRole.DefaultDataDir`)，UI 透過 `ConfigSnapshot` 同步；identity key 走 `ISecretStore`。
  存檔後 `ServerRole` 訂閱 `HostIpcBridge.ConfigChanged` **即時套用**：`HostRuntime.Policy`（核准模式、權限、逾時、裝置名稱，指派時會重算所有連線中的 `PermissionSet`）、`PreferRelay`、`HostPasswords.Configure`（臨時密碼長度／輪換門檻／啟用）。
  只有燒進傳輸層的欄位需要重啟引擎：中繼伺服器、公鑰、直連開關與埠號、UDP 媒體、編碼器偏好；`HostConfig.RequiresEngineRestart` 判斷，`ConfigSnapshot.restart_required` 回報，UI 顯示提示。
  兩個設定檔都以「先預設值、再覆蓋檔案內容」的方式讀取（`HostConfig.FromJson`、`DesktopConfig.FromJson`）：JSON 來源產生器建立物件時不會執行屬性初始設定式，舊版或手改過的檔案若缺欄位，會把權限開關讀成關閉。
- 臨時密碼：`HostPasswords` 支援停用（`TemporaryEnabled`）、使用者自訂並釘選（`SetTemporaryAsync`，存在 `ISecretStore` 的 `password-temporary-pinned`，釘選後失敗次數不會換掉它）、每次連線結束後更換（`HostConfig.RotateTemporaryAfterSession`）。IPC 新增 `GetPasswordState` / `PasswordState` / `RotateTemporaryPassword` / `SetTemporaryPassword`（欄位 23–26）。
- 沒有 UI 時 (`--server` 無人值守角色)：密碼模式照常運作；Click 模式一律拒絕，因為沒有人可以問。
- **本機 ID（沒有外網也能連）**：`PeerIdentityStore` 在 rendezvous 尚未指派 ID 時，以身分金鑰指紋前 8 碼產生 `LAN-xxxxxxxx`（`IsLocalId`）。交握拒絕空 ID，所以沒有這個本機 ID 時，完全無外網的站點連不起來；直連本來就是以 TOFU 驗金鑰，ID 只用於顯示。`HostRendezvousClient` 遇到本機 ID 時，`RegisterIdentity` 送空 ID（即「請伺服器指派」），指派後覆寫。
- **防火牆規則**：Windows 的提示來自引擎開啟的 21118 監聽埠，程式無法自行消除，只能事先建規則。`Engine/FirewallRules.cs` 以 `netsh advfirewall` 建立「私人＋網域」的 TCP/UDP 允許規則（名稱 `Sunllo DeskPair`，不含公用網路，程式路徑取 `Environment.ProcessPath`，也就是這個唯一的執行檔）；`--allow-firewall` / `--remove-firewall` 需要提權，設定頁的按鈕以 `runas` 重新啟動自己來做，並等結束後回報結果。`Add` 會先依規則名稱刪除再新增，所以升級後按一次就會把舊的引擎規則換掉。
- **來電廣播順序**：`HostSession` 在對方表明身分的當下就發出 `HostEventKind.Identified`，`HostIpcBridge` 將它對應成 `ConnectionOpened{Authorized=false}` 廣播給所有角色，主 UI 因此能在核准前就浮出「來自 XXX 的連線·驗證中」。`ApprovalRequest` 也廣播，但 `ApprovalDecision` **只接受 `IpcRoles.ConnectionManager` 送的**，主 UI 看得到却不能代替使用者決定。連線管理員現在是同一個程序裡的視窗，以第二個 IPC client 身分連回本機——所以這條界線是「哪個視窗」而不是「哪個程序」。它本來也不是安全邊界：token 就放在 `ipc.token`，同一個使用者的任何程序都讀得到；它擋的是「主視窗替使用者按下同意」這類錯誤。
- `IpcServer.BroadcastAsync` 接受 `except`，讓設定儲存的快照不會回送給剛剛送出變更的那個客戶端，以免覆寫正在編輯的欄位。

---

## 10. 桌面 App (`DeskPair.Desktop`, Avalonia + MVVM)

| View | ViewModel | 職責 |
|---|---|---|
| `HomeView` | `HomeViewModel` | 我的 ID、臨時密碼（一律明碼顯示、重新產生、自訂、停用）、輸入對方 ID 連線、最近連線 |
| `DeviceListView` | `DeviceListViewModel` | 設備清單：依群組顯示、搜尋、手動新增（ID 或 IP）、從最近連線加入、編輯刪除、遠端控制與檔案傳輸；在頁面上時每 15 秒更新一次在線狀態 |
| `MainWindow`（底部） | `IncomingConnectionsViewModel` | 來電浮動通知：`ConnectionOpened{Authorized=false}` 就浮出「驗證中」，授權後改成「已連線」並在 6 秒後收起 |
| `SettingsView` | `SettingsViewModel` | 七個分頁的外框，**沒有儲存鈕**：每個分頁是一個 `ISettingsSection`（`ViewModels/Settings/*`），變更時發出 `Changed`，外框 debounce 500 毫秒後依序 `Apply(DesktopConfig)` → `Apply(HostConfig)` 寫回；只有內容真的不同才送 IPC。自己送出的設定會記下來（`_sentHostJson`），相同內容的推播不重新載入，以免洗掉正在打的字。分頁本身不碰 `App.Config` 靜態狀態，因此可單獨測試 |
| `ViewModels/Settings/NumericField` | — | 正在輸入的數字：`Text` 是盒子裡的內容，`Value` 是最後一個合法值。即時儲存下，空字串或「打到一半」的數字不算錯誤，也永遠不會進入設定檔 |
| `Views/Settings/*View` | `*SettingsViewModel` | 每頁有標題與一行說明。基本（語言、登入啟動、最小化到系統列、裝置名稱、檔案起始資料夾、最近連線筆數、錄影資料夾）／顯示（畫質預設與自訂碼率 fps、游標、無損精修、縮放、平滑播放、編碼器偏好）／**聲音**（傳輸：禁用／以所選裝置；播放：禁用／標準裝置／所選裝置＋獨佔播放）／鍵鼠（鍵盤模式、結束後鎖定對方、允許鍵鼠剪貼簿重啟）／安全（**驗證方式**與逾時、臨時密碼政策、固定密碼、檔案權限、**接受區域網路連線與防火牆按鈕**、TOFU 已信任主機）／網路（伺服器與公鑰、直連埠號、UDP、強制中繼）／**關於 DeskPair**（版本、建置日期、伺服器、設定檔與記錄資料夾、第三方元件授權） |
| `RemoteSessionWindow` | `RemoteSessionViewModel` | 內含 `RemoteDisplayView`；工具列：切換顯示器、品質/fps、鍵盤模式、Ctrl+Alt+Del、檔案傳輸、聊天、音訊、**錄影**、全螢幕；擁有解碼器與鍵盤 hook |
| `FileTransferWindow` | `FileTransferViewModel` | 雙欄本地/遠端瀏覽、傳輸佇列進度、取消 |
| `ConnectionManagerWindow` | `ConnectionManagerViewModel` | 來電核准、即時權限切換、聊天、檔案記錄。ViewModel 從啟動就掛著 (第一秒進來的連線也有人核准)，視窗則等收到 `ApprovalRequest` 或 `ConnectionOpened{Authorized=true}` 才建立並顯示；閒置 1.5 秒後**關閉視窗** (不是結束 App)，下次再開新的。可收合成右下角 44 px 的箭頭（同一個視窗改寬度，不進工作列） |
| `ChatView` | `ChatViewModel` | 共用於 session 與 CM |
| `TrayIcon` | — | 顯示主視窗、設定、結束；由 `App.SetupTray` 直接建立 |
| `PermissionOnboardingView` | `PermissionsViewModel` | macOS TCC / Linux Wayland 引導 |

**`Controls/RemoteDisplayView.cs` 兩層渲染**：
- Tier 1 GPU 零複製：`Compositor.TryGetCompositionGpuInterop()` + `CompositionDrawingSurface`，`ImportImage` D3D11 shared handle (Windows) / DMA-BUF 或 GL texture (Linux) / IOSurface-Metal (macOS，若 Avalonia 版本不穩則退 Tier 2)，`UpdateWithKeyedMutexAsync`。
- Tier 2 CPU：`WriteableBitmap.Lock()` 複製 BGRA → `InvalidateVisual`；永遠實作，為保證路徑。
- 縮放模式 Original/Fit/Stretch；以 `RenderScaling` 處理 DPI；滑鼠座標 = 渲染轉換反矩陣 → 遠端像素，含 letterbox 位移。
- Controller 端鍵盤 hook：Windows `WH_KEYBOARD_LL` (擷取 Win 鍵)、macOS CGEventTap、Linux XGrabKeyboard，僅在 session 視窗聚焦時啟用。

**錄影（`Platform.Abstractions/Recording`，實作 `Platform.Windows/Recording/MfSessionRecorder`）**：
- 影像**不重新編碼**：`ControllerSession` 在解碼前分一份已編碼封包給錄影器，`IMFSinkWriter` 的輸入與輸出媒體型別相同（直通封裝）；聲音是解碼後的 float PCM，交給 sink writer 轉成 AAC。
- 一定從關鍵幀開始（`RecorderWrite.WaitingForKeyFrame`）；解析度或編碼格式變更回報 `SizeChanged`，由 `SessionRecordingController` 收尾舊檔、開新檔並在檔名加序號。
- 時間戳以第一張關鍵幀為零點重算（100 奧秒單位），單幀時長用前後 pts 差補（所以會多押一張）；聲音的空檔以 `SendStreamTick` 對齊，不補零。
- 檔名 `yyyy-MM-dd_HH-mm-ss_<對方>.mp4`，資料夾為 `DesktopConfig.RecordingFolder`（預設 `影片\Sunllo DeskPair`）。只支援 MP4（H.264）。

---

## 11. 實作階段與驗證

Phase 0~2 為單軌；Phase 3 起 **三平台三軌並行** (Windows 軌先於其他軌約 1~2 週起跑以先打通 E2E 並固定介面)。

### Phase 0 — 骨架 (1 週)
`git init`、`.gitignore(reference/)`、sln、`Directory.*.props`、`CLAUDE.md`、三個 proto 經 `Grpc.Tools` 編譯、CI (`dotnet build/test` win/linux/mac runner)、
`Protocol`：`FramedStream`、`SessionCipher`、`IdentityKey/EphemeralKey/Handshake`、`PasswordProof`、`ProtocolConstants`；`Platform.Abstractions` 全部介面。
**驗證**：`Protocol.Tests` — 每個訊息 round-trip；framing fuzz (任意位元組邊界切割、超大幀拒絕、heartbeat、GCM tag 竄改)；兩條記憶體串流交握得到相同金鑰、竄改 HostHello 被拒；密碼 proof 向量；`AesGcm.IsSupported` guard。

### Phase 1 — 伺服器 + 無頭 E2E (2.5 週)
`Relay` 完整 (限制、/healthz、Docker)；`Rendezvous` UDP 註冊/ID 配發/SQLite/`PunchHoleRequest` **一律走中繼**/HTTP API/Docker；
`Core`：`RendezvousClient`、`RelayClient`、`PeerConnector` (relay only)、`HostListener`、`SessionMessagePump`、`MessageDispatcher`、`HostSession` 到 Authorized (密碼模式)、`ChatHandler`、`ControllerSession` 到登入；`Tools.PeerCli host|connect`。
**驗證**：`Relay.Tests` 兩客戶端同 uuid 對接各傳 10 MB 校驗、未配對逾時、第三者同 uuid 拒絕；`docker compose up` 後 `PeerCli host` 印出 9 位 ID 與臨時密碼，`PeerCli connect <id> -p` 經中繼完成交握登入、聊天雙向；錯密碼 6 次 → `TOO_MANY_ATTEMPTS`；`GET /api/peers/<id>` online；`Integration.Tests` in-process 重現同流程 (之後每階段的回歸基準)。

### Phase 2 — Session Core 完整 (3.5 週)
`PublisherService` 系列 + fake 媒體、`InputHandler`、`PermissionSet`、`IConnectionApprover` (console)、Click/Both 模式、`VideoQosController`、`TestDelay`/`VideoAck`、`SwitchDisplay`、`SessionOptions`、`CloseReason`、逾時；
`FileTransferEngine` (Read/Write/digest/續傳/取消/目錄操作/`PathGuard`)、`ClipboardSync`、`ClipboardService`、Opus (Concentus)、IPC server/client + `ipc.proto`。
**驗證**：整合測試 — controller 收到 ≥25 fps 合成視訊並 ack；人工延遲 200 ms 的慢 controller 觸發 QoS 降 fps 且 in-flight 不超上限；鍵盤權限關閉後按鍵被忽略並收到 `PermissionInfo`；核准拒絕 → `REJECTED_BY_USER`；讀逾時雙方乾淨關閉；
`InMemoryFileSystem` 1 GB 稀疏檔雙向、中途中斷後續傳、同檔略過、路徑穿越拒絕；剪貼簿 A→B 不回彈；`PeerCli --send-file/--get-file` 經 compose 中繼 sha256 一致；`PeerCli --dump-frames` 輸出 PNG。

### Phase 3 — 平台層 + 桌面 App，三軌並行 (Windows 6 週 / Linux 6 週 / macOS 7 週)

**共同 (Desktop 殼，隨 Windows 軌先做)**：Avalonia 專案、MVVM、`HomeView`/`SettingsView`/`RemoteSessionWindow`/`RemoteDisplayView` Tier 2、i18n (zh-TW/en)、IPC client、連線管理員卡片與工作列圖示。

**Windows 軌**：M-W1 DXGI+GDI 擷取+顯示器列舉 → M-W2 MF H.264 SW/HW 編解碼 → M-W3 `RemoteDisplayView` Tier 1 (D3D11 shared texture) → M-W4 `SendInput`+游標+WASAPI+剪貼簿 → M-W5 Windows Service + session 啟動 + CM + DPAPI。
**驗證**：`PlatformHarness capture-to-png` 1080p@30fps 10 s 正確 (含次螢幕、旋轉)；`encode-roundtrip` SSIM > 0.95、`RequestKeyFrame` 生效、改 bitrate 生效；GPU 路徑 profiler 無 CPU 複製；`inject-and-verify` 座標回讀一致、Notepad 打字正確；
**E2E**：乾淨 Windows 機器安裝服務、重開機，從第二台連入**看見登入畫面**、登入、操作、UAC 提示可見可點、剪貼簿/音訊/聊天/檔案傳輸 (經 CM) 全通。

**Linux 軌**：M-L1 XShm 擷取+XRandR → M-L2 FFmpeg VAAPI 編解碼 + 免權利金軟體後備 → M-L3 XTest+XFixes 游標+Pulse 音訊+X11 剪貼簿 → M-L4 systemd 服務 + DM display 登入畫面 → M-L5 Wayland portal 盡力。
**驗證**：Ubuntu X11 host 被 Windows 控制；gdm X11 登入畫面可見；無 VAAPI 的 VM 需要免權利金的軟體編碼器（VP8/VP9），見 §軟體後備編解碼。

**macOS 軌**：M-M1 `SunlloMacShim.dylib` 建置流程 + 簽章 → M-M2 ScreenCaptureKit 擷取 → M-M3 VideoToolbox 編解碼 → M-M4 CGEvent 輸入+游標+音訊+剪貼簿 → M-M5 launchd daemon/agent + TCC 引導。
**驗證**：授權 Screen Recording + Accessibility 後 macOS host 被 Windows 控制，VideoToolbox 硬體編碼生效；(登入畫面不列 v1 驗收)。

### Phase 4 — NAT 穿透與直連 (2 週)
`NatTypeDetector` (21115/21116 同本地埠比對)、`SocketFactory` (Windows `ReuseAddress`；Linux/macOS 另加 `SO_REUSEPORT` raw option)、`TcpPuncher` (host：由打洞埠連 rendezvous 送 `PunchHoleSent`、預先 30 ms 開洞、同埠 listen + 150 ms×1.5 退避連線競速；失敗的 socket 必須重建)、
Rendezvous `PunchRegistry`/`PunchHoleSent`/`LocalAddr`/host 主動 `RelayResponse`、controller 打洞 vs 中繼競速 (中繼晚 1 s 起跑、以**完成交握**者為勝)、LAN `FetchLocalAddr`、直連 21118 + TOFU。
**驗證**：loopback 整合測試跑完整訊號狀態機；`docs/nat-test-matrix.md` 手動矩陣：家用 NAT↔家用 NAT (打洞)、symmetric (手機熱點)↔家用 (中繼)、同 LAN (`is_local`)、三平台交叉；`SessionStats.Transport` 顯示勝出路徑。

### Phase 5 — 強化與出貨 (3 週)
防暴力邊界、臨時密碼輪換、白名單、metrics、結構化日誌、優雅關閉、controller 自動重連退避、設定驗證、admin API key、`network_mode: host` compose、
負載測試 (單一 relay 容器 500 條 2 Mbps session 量測 CPU/RSS)、單檔發佈 + 原生庫 (`NativeLibrary.SetDllImportResolver`)、Windows EV 簽章 + 安裝程式 (防火牆規則)、macOS Developer ID + notarization + stapling、Linux .deb/.rpm、三平台互控矩陣、HDR/混合 DPI 測試。

### 後期 (v1.5+)
TOTP 2FA、`SslStream` + SPKI pin 於伺服器 TCP 段、`QuicPeerTransport` (msquic；注意 macOS 支援狀況)、LAN 探索 21119、Postgres `IPeerStore`、中繼頻寬上限、AV1/VP9、檔案剪貼簿、Wayland 完整支援。

---

## 12. 風險與待決事項

**已決定**
1. **H.264 專利（軟體後備）**：不散布自建的 OpenH264 二進位檔；需要的人自備（見 §軟體後備編解碼）。這解掉的是「散布自建二進位檔」的曝險；**不等於 AVC 問題全部結案**——預設編碼器仍是 H.264，只是改由 OS 廠商授權的 MF/VideoToolbox 執行，那是業界標準論述而非授權本身。

**待使用者/法務決定**
2. Linux 編碼採 FFmpeg 封裝 VAAPI (LGPL 動態連結) 而非直接 libva — 若不接受 FFmpeg 相依，Linux 軌工時 +2 週。

**技術風險**
- Relay 首幀不可預讀；Windows `SO_REUSEADDR` 可搶埠、失敗的連線 socket 不可重用；Docker bridge 會改寫 UDP 來源埠破壞 NAT 測試 (正式用 host network)；Windows `SIO_UDP_CONNRESET`。
- `ByteString` 複製與 pooled buffer 生命週期 (視訊資料須在 Frame Dispose 前送入解碼器)；AES-GCM nonce 紀律 (每方向獨立、永不重設)；所有時間邏輯注入 `TimeProvider`。
- Windows：混合 DPI 需 PerMonitorV2 manifest；HDR 輸出格式；安全桌面注入前必呼叫 `EnsureInputDesktop`；EDR 對 winlogon token 啟動的誤判 → 簽章與白名單。
- macOS：TCC 無法靜默授權、bundle id/簽章變更會重置授權；shim dylib 需 hardened runtime 簽章；登入畫面擷取脆弱。
- Linux：Wayland 無法無人值守；VAAPI 依賴 GPU 與驅動 (VM 常無) → 軟體後備必須穩固；uinput 需 udev rule。
- Avalonia GPU interop 各後端支援度不一 → 啟動時能力檢查，缺即退 `WriteableBitmap`。
- 三平台同步開發的整合風險：介面 (`Platform.Abstractions`) 在 Phase 0 凍結，Windows 軌先跑通 E2E 後其他軌只填實作。

---

## 13. 移植時應對照的參考檔案

| 主題 | 檔案 (相對 `reference/rustdesk-master/`) |
|---|---|
| 訊息定義 | `libs/base/protos/message.proto` |
| Publisher service 模式 | `src/server/service.rs` |
| Host 連線建立、密碼驗證、登入範圍 | `src/server/connection.rs` 416–560、1719–2071、2321–2830、3323–3660 (FileAction) |
| 打洞 / 中繼 / mediator | `src/rendezvous_mediator.rs` 274–388 (UDP 註冊)、579–660、895–1045、1278–1440；`src/client.rs` 405–1000、1353–1420、1609–1800 |
| 視訊服務迴圈 / QoS | `src/server/video_service.rs`、`src/server/video_qos.rs` (+ `video_qos/tests/`) |
| 編碼協商 / bitrate 表 | `libs/scrap/src/common/codec.rs` |
| DXGI / GDI 擷取、顯示器排序 | `libs/scrap/src/dxgi/mod.rs`、`gdi.rs`、`libs/scrap/src/common/dxgi.rs` |
| 輸入注入、鍵盤模式、游標 | `src/server/input_service.rs`、`libs/enigo/src/win/win_impl.rs`、`src/platform/windows.rs` 207–300 (cursor)、949 (SAS)、1036–1058 (desktop) |
| Windows 服務 / session 啟動 | `src/platform/windows.rs` 665–830 (`run_service`)、`launch_server`、`LaunchProcessWin` |
| 檔案傳輸 job 引擎 | `libs/base/src/fs.rs` |
| 剪貼簿 | `src/clipboard.rs`、`src/server/clipboard_service.rs` |
| 音訊 | `src/server/audio_service.rs` + `audio_service/`、`src/client/audio_playback.rs` |
| IPC 訊息集 | `src/ipc.rs` 323–560 |
| macOS / Linux 平台 | `src/platform/macos.rs`、`src/platform/linux.rs`、`libs/scrap/src/quartz/`、`libs/scrap/src/x11/`、`libs/scrap/src/wayland/` |
