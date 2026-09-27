# Phase 9：Linux 的鎖定畫面與登入畫面

## 為什麼要做

`docs/unattended-windows.md` 把 Windows 的無人值守做完並實機驗證：遠端鎖定 → 看到鎖定畫面 → 輸入 PIN → 解鎖，
同一條連線。Linux 原本的計畫是「M1：root daemon + X11 擷取，今天就能涵蓋 X11 greeter」。

2026-09-23 在一台 Linux 測試機（Ubuntu 24.04.2、GNOME Shell 46、VMware `vmwgfx`）量到的事實推翻了它：

| 事實 | 怎麼量的 |
|---|---|
| **鎖定畫面不是一個 X 視窗** | 鎖定時 `xwininfo -root -children` 只列出原本的應用視窗；`xwd -root` 拿到上一畫面的殘影；`xset dpms force on` 沒有改變任何事 |
| **登入畫面根本沒有 Xorg** | greeter 是 `Type=wayland Class=greeter`，`pgrep Xorg` 空；只有 uid 120 的 rootless `Xwayland :1024` |
| **讀別人的 scanout 需要 root** | kernel `drm_framebuffer.c`：`GETFB2` 的 handle 只給 DRM master 或 `CAP_SYS_ADMIN`；DRM 裝置的 ACL 又跟著 active session 走（登入時 `user:alice:rw-`，greeter 時 `user:gdm:rw-`） |

GNOME 的鎖定畫面與登入畫面都是合成器直接畫進 framebuffer 的東西，X 裡沒有那份畫面。
**唯一看得到它們的方法是讀真正被掃描輸出的 buffer——DRM/KMS——而那需要 root。**
「只做登入後的無人值守」等於重開機後連不進去，所以這不是加分項。

使用者定調（2026-09-23）：真的看到鎖定畫面並遠端解鎖；完整的登入畫面（重開機後沒人登入也連得上、遠端登入）；
只用現有的 VMware VM 驗證。

## A 架構

### A1 形狀（被事實逼出來的，不是選的）

```
systemd: sunllo-deskpair.service   (root)
  └─ 只做四件「非 root 做不到」的事，沒有 GPU userspace，沒有網路：
     1. 開 /dev/dri/cardN → drmModeGetPlaneResources → drmModeGetFB2 → drmPrimeHandleToFD：scanout 的 dma-buf fd
     2. 開 /dev/uinput，建三個虛擬裝置（鍵盤／絕對座標指標／相對移動與滾輪），開機建一次
     3. 看 seat0（logind）：只為了 IPC 的 console 使用者與 token 發佈，不為了搬引擎
     4. 替引擎向 logind 鎖定 session（polkit 不讓引擎帳號鎖別人的 session）
     5. seat0 上是已登入的 Wayland 桌面時，以那個使用者身分起一個工作階段代理（J 節），socket 留給引擎來領
        └─ 子行程：DeskPair --server --drop-uid/--drop-gid/--drop-user deskpair --ipc-system
                            --supervisor-fd N --keyboard-fd N --pointer-fd N --relative-fd N
             引擎（固定的 deskpair 系統帳號，Detached 模式）：
               - 透過 socketpair 向 daemon 要 scanout（SCM_RIGHTS 收 fd → mmap → BGRA → 既有編碼管線）
               - 用繼承的 uinput fd 注入輸入
               - 直接讀 /var/lib/deskpair/secrets（它自己擁有）
```

**Detached 是 Windows 那條教訓的最強形式。** Windows 學到「不要在桌面切換時重啟引擎」；這裡 session 變動時根本沒有東西
需要移動：鎖定、登出、登入、mode change，引擎 pid 不變、連線不斷。整輪驗證中引擎 pid 從 greeter 撐到登入再到鎖定解鎖。

### A2 root 那一半為什麼要這麼小

Mesa（`gbm_create_device()` 本身就會載入 DRI 驅動）、更不用說 NVIDIA 閉源驅動，絕對不能載進 root 行程。
root 只准 `libdrm.so.2`（薄薄一層 ioctl 包裝）與 libc。線性格式直接 `mmap` dma-buf，vmwgfx 就是這樣；
`DrmFormatSupport.Classify` 對不認得的 fourcc／modifier **大聲失敗**（例外裡印出實際數值），不會輸出黑畫面或亂掉的畫面。
`DRM_FORMAT_MOD_INVALID` 只在「只會是線性」的驅動白名單（vmwgfx、virtio_gpu、qxl、bochs、simpledrm、ast、mgag200）上當成線性。

### A3 檔案佈局

| 路徑 | 擁有者／模式 | 內容 |
|---|---|---|
| `/opt/deskpair/DeskPair` | root 0755 | unit 指向的執行檔副本。**必須是副本**：Ubuntu 24.04 家目錄是 0750，從 `/home/…` 起的引擎降權後連自己的執行檔都讀不到，單檔 runtime 延遲載入的第一個組件就 `FileNotFoundException`，每 5 秒一次 |
| `/var/lib/deskpair` | deskpair 0750 | 引擎帳號的家；`config.json`、`logs/`、`ipc.token` |
| `/var/lib/deskpair/secrets` | deskpair 0700 | 這台機器的身分：`identity-key`、`peer-id`、`machine-id`、`password-salt`、`password-permanent-h1` |
| `/run/deskpair` | deskpair 0755（systemd 建、daemon chown） | `DeskPair.sock`（引擎以 `--ipc-system` 綁在系統路徑）、`<uid>/ipc.token` |
| `/etc/systemd/system/sunllo-deskpair.service` | root 0644 | 由 `SystemdUnit.Daemon()` 產生，測試釘住每一條指令 |

### A4 輸入：三個 uinput 裝置，而且是量出來的

一個同時宣告按鍵與 `ABS_X/ABS_Y` 的節點在 libinput 眼裡是繪圖板；一個同時有絕對與相對軸的指標會被當成相對滑鼠、絕對軸被忽略——
對 (640,350) 的點擊落在不知道哪裡。所以：鍵盤、絕對指標（帶按鈕）、相對指標（滾輪、相對移動、以及叫醒 DPMS-off 顯示器的那一下 nudge）。
evdev 鍵碼就是協定的 Map code（X 那邊是 +8）；`Translate` 模式用內建的 US ASCII 表，非 US 配置的符號密碼會打錯，
而密碼欄不回顯——遠端登入時**先打使用者名稱**（會回顯）再相信密碼欄。

`SendCtrlAltDel()` 是空的，不宣告 `SecureAttention`：uinput 送出的是真的按鍵，Linux 主控台的 `ctrl-alt-del.target` 就是重開機。
`LockWorkstation()` 每次問 seat 上現在是誰，透過 daemon 的通道（`DrmWire.KindLock`）請 root 呼叫 logind。

## B 實測推翻的判斷

這一輪被實機推翻的判斷，每一條都是「會過建置、會過測試、然後在真機上咬人」：

1. **「引擎的 session id 在 spawn 時決定」**。Detached 引擎在 greeter 時生出來、之後一直活著，`--session` 從沒被傳過，
   `LockWorkstation` 永遠是「沒有 session 可鎖」。改成每次請求時問 seat。
2. **「`deskpair` 帳號可以 `loginctl lock-session`」**。polkit：`Interactive authentication required`，rc=1。而
   `LinuxSessions.Run` 沒看 exit code，`Lock` 回 true、什麼都沒記——「成功地失敗」。兩件修法：`Run` 非零 exit 回 null；
   鎖定改走 daemon（root 是 polkit 唯一不問的帳號）。
3. **「`NoNewPrivileges=true` 是安全的，放棄權限不受影響」**。實測引擎 `setresuid(997)` 得到 EPERM，daemon 每 5 秒重啟一次。
   對 unit 的五個強化指令逐一移除 bisect：只有拿掉這一個引擎才起得來。原因未查清，先拿掉並在 unit 註解與測試裡記下。
4. **「絕不覆蓋已存在的鍵」對 salt 不成立**。daemon 先於安裝跑過的機器，機器層 store 已有自己鑄的 `password-salt` 但沒有永久密碼；
   只搬 h1 過去會對著另一個 salt 永遠比對不上。salt 與 h1 是一對：機器層沒有 h1 時一起搬（salt 是唯一會被覆蓋的鍵）。
5. **「daemon 什麼時候開 card 都一樣」**。**開機時第一個開 DRM 裝置的行程會被 kernel 直接指定為 DRM master**，而 daemon 比
   GDM 早起。結果 `gnome-shell: Failed to open gpu '/dev/dri/card1': EBUSY`、`gdm3: Session never registered`，機器開機停在
   一片黑——而 daemon 忠實地把那片黑送給檢視端。修法一行：開完就 `drmDropMaster`（master 從來不需要，`GETFB2` 的 handle
   靠 `CAP_SYS_ADMIN` 就拿得到）。log 會記 `was made DRM master; dropped it`。
6. **「daemon 死了引擎會跟著死」**。不會：孤兒引擎繼續佔著 21118 與 IPC socket，下一個 daemon 的引擎每 5 秒 bind 失敗一次。
   引擎啟動時 `prctl(PR_SET_PDEATHSIG, SIGTERM)`（`ParentDeath.FollowParent`），systemd 之外的每一種死法都涵蓋。
7. **「閒置的 greeter 抓得到畫面」**。閒置的 greeter 會把 CRTC 整個關掉，8 個 primary plane 全是 `crtc=0 fb=0`——沒有東西可抓，
   不是抓到黑。`DrmDisplayEnumerator` 拿到 NoScanout 時先用相對指標 nudge 一下，100 ms 後 `Scanout generation 1`。
8. **「`loginctl terminate-session` 等於登出」**。它不切 VT：greeter 停在 `online`、VT 留在死掉的 tty2，CRTC 沒東西可掃，
   `displays: 0`。`chvt 1` 之後才 `active`。真正的登出（GDM 自己做）會切 VT；這是測試法的 artifact。
9. **「uinput 點擊沒有效果」**。第一次量到 diff 0.0000 是因為 harness 在抓圖之前先按了 Escape 把畫面復原。量測要在復原之前。
10. **「framebuffer id 可以當快取的鍵」**（第二台 VM，Wayland session，使用者在本機解鎖後回報：遠端畫面在鎖定畫面與桌面之間反覆跳）。
    kernel 會回收 fb id：合成器釋放鎖定畫面的兩個 buffer、配置桌面的兩個，新的常常拿到舊的號碼；daemon 與引擎都只認號碼，
    引擎繼續用舊的 mmap（而 daemon 自己持有的 dma-buf fd 讓那塊舊 buffer 一直活著），於是每隔一張就送出鎖定畫面。
    修法：每次 poll 都匯出 dma-buf、`fstat` 取 inode 當 buffer 的身分；同一個 id 換了 inode 就重送 fd，引擎丟掉舊 map。
    實測解鎖那 100 ms 內 id 111/112 在 inode 3/4/5 之間換了六次，之後影格穩定。

## C 安裝與移除

`DeskPair --install-service`（以自己的身分，不要 sudo）：

1. 使用者半段：`LegacyIdentity.Migrate`；沒有機器層 store 時檢查使用者 store 有永久密碼（沒有就拒絕，理由說出口）；
   `pkexec <self> --install-service --system-stage --from <userDataDir>`。
   pkexec 126（取消）→ 退出碼 **3**，設定頁顯示「已取消」；127 或沒有 pkexec → 退出碼 **4**，log 與設定頁都印出可以手動執行的
   `sudo … --system-stage --from …`（SSH 上就是這條路）。
2. root 半段：`useradd --system --user-group --home-dir /var/lib/deskpair --no-create-home --shell /usr/sbin/nologin deskpair`
   （已存在就跳過）→ 執行檔複製到 `/opt/deskpair/DeskPair`（寫 `.new` 再 rename，正在跑的 daemon 保有舊檔）→ 目錄與權限 →
   HandOver（五把鍵，salt+h1 成對）→ `chown -R deskpair` → 驗機器層 store 有永久密碼（沒有就不寫 unit）→ 寫 unit →
   `systemctl daemon-reload && enable && restart`。

就地升級 = 新 build 再跑一次同一件事。移除：`systemctl disable --now`、刪 unit 與 `/opt/deskpair`；
`/var/lib/deskpair` 與 `deskpair` 帳號**刻意留著**，它們是這台機器的身分。

安裝之後 app 端 `UnattendedInstall.IsInstalled()`（unit 檔存在）為真，app 不再自己跑引擎，透過 `/run/deskpair/DeskPair.sock` 連上 daemon 的引擎。

### 用 .deb／.rpm／Arch 套件安裝時（0.4.3 起）

套件把程式放在 `/usr/lib/deskpair`（`/usr/bin/deskpair` 是連結），刻意不用 `/opt/deskpair`——那是上面 root 半段自己複製的服務用副本，
兩者分開，套件管理員才不會和服務搶同一個檔案。描述檔是 `packaging/linux/nfpm.yaml`，腳本在 `packaging/linux/scripts/`：

- **安裝或升級之後**（deb postinst、rpm %post、Arch post_install／post_upgrade）：unit 檔存在才以 root 跑
  `/usr/lib/deskpair/DeskPair --install-service --system-stage`（不帶 `--from`，機器層 store 已經有了），讓服務換成剛裝好的版本，
  和 App 內建更新做的事一樣。失敗只印一行提示，不讓套件安裝失敗。本來沒開無人值守就什麼都不做。
- **移除之前**（deb prerm、rpm %preun、Arch pre_remove）：只有真的移除才跑 `--uninstall-service`；升級時各家傳的參數不同
  （deb 是 `upgrade`，rpm 是剩下的版本數，Arch 的 pre_remove 只在移除時呼叫），腳本逐一分辨。理由：留下一個還連得到網路、
  卻已經沒有 App 可以關掉它的服務，比一起移除更糟。`/var/lib/deskpair` 與帳號同樣留著。
- **App 不自己更新套件裝的版本**：`/usr/lib/deskpair/packaged` 寫著格式，有這個檔案時通知只提供「前往下載」，
  `--update` 也說明要用套件管理員更新（`PackagedInstall`）。否則 App 換掉套件管理員擁有的檔案，下一次套件升級又會蓋回來。

2026-09-28 在容器裡驗證過安裝 → 引擎啟動 → 升級（服務保留並重新 stage）→ 移除（服務、`/opt/deskpair`、`/usr/lib/deskpair` 都清掉）：
Debian 12、Ubuntu 22.04／24.04、Fedora 44、Rocky 9、openSUSE Tumbleweed／Leap 15.6、Arch。

### 字型（介面語言）

桌面程式的介面有十種語言；拉丁與西里爾字母由內建的 Inter 字型顯示，中日韓文字靠系統字型。Ubuntu／Debian 的最小安裝沒有，
要 `sudo apt install fonts-noto-cjk`，否則簡中、繁中、日文、韓文介面會是一格格方框。`Program.cs` 列出的候選字型
依序是 Noto Sans CJK TC／SC／JP／KR。

## D 驗證矩陣（2026-09-23，全部在同一台 VM，`tools/DeskPair.Tools.PeerCli`）

判定用數字，不用截圖：PeerCli `--frame-stats` 每張影格印 `sha256 / nonBlack% / diffFromPrev`，
`:save PATH.png` 在腳本的每一步存圖。殘影 = 畫面明明變了 `diffFromPrev` 卻是 0；全黑 = `nonBlack=0%`；抓錯畫面 = 兩個狀態同一個 sha256。

| # | 狀態 | 結果 | 數字 |
|---|---|---|---|
| 1 | 已登入桌面 | ✅ | 1920x1080 VP9，靜態時 0 fps（畫面沒變就 `Timeout`），mode change 後 ~3 fps 重畫 |
| 2 | 遠端鎖定 | ✅ | `:lock` → `LockedHint` no→**yes**；daemon log `Locked session 128 (uid 1000) for the viewer` |
| 3 | **看到鎖定畫面** | ✅ | frame #7 `nonBlack=100% diffFromPrev=1.0000`，存下來的是 GNOME 鎖定畫面（頭像、名字、密碼欄） |
| 4 | **遠端解鎖** | ✅ | `:key escape` `:type …` `:key return` → `LockedHint` **no**；frame #16 `diff=0.9807`；frame #21 的 sha256 **等於**鎖定前 frame #3——桌面原樣回來；同一條連線，只有一次 `Connecting` |
| 5 | 登出到 greeter | ✅ | `loginctl terminate-session`：SSH 還在，`pgrep Xorg` 0，引擎 pid 不變（需要 `chvt 1`，見 B8） |
| 6 | **看到 GDM** | ✅ | 1280x800 `nonBlack=100%`，scanout generation 2，時鐘與抓取時間一致 |
| 7 | **遠端登入** | ✅ | `:move 640 350` `:click left` `:type …` `:key return` → `loginctl` 出現 `128 1000 alice seat0 tty2 active`；引擎 pid **13267 不變**；連線跨過 1280x800→1920x1080 沒有斷 |
| 8 | 重開機後 | ✅（第二次） | 第一次：unit 開機 5 秒後起來、**永久密碼**通過驗證、但畫面全黑——B5 的 DRM master 問題。修掉後第二次重開機見下一節 |
| 9 | 快速使用者切換 | 未做 | Detached 模式下引擎不跟著 session 走，機制上與 5/7 相同；這台 VM 只有一個使用者 |
| 10 | 就地升級 | ✅ | 新 build 再跑 root 半段：`/opt/deskpair/DeskPair` 換新、unit 重寫、引擎起來、id 不變 |

**#4 與 #7 是 Phase 9 的驗收條件，兩個都過了。**

### 重開機（修掉 DRM master 之後，04:26）

`reboot` → 開機 3 秒後 `sunllo-deskpair` 起來，log 兩行 `was made DRM master; dropped it`（`FindCard` 的探測與正式的 reader 各一次）
→ GDM greeter `c1` active、gnome-shell 在跑、整個 journal 裡 `Failed to open gpu` 出現 **0** 次 → 沒人碰機器，
從 Windows 用**永久密碼**連上 → 三張 1280x800 影格 `nonBlack=100%`，是登入畫面。**#8 過了。**

## E 不做、已知限制

- 不做「連線驗證通過就自動解鎖」——使用者已明確否決。
- 不依賴 `libdrmtap`（授權、上游、打包三件事都不清楚）；不走 GNOME 的 `RemoteDisplayFactory`（bus policy 只授權
  `gnome-remote-desktop`，而且它開的是虛擬顯示器）；不用 GStreamer。
- **NVIDIA 閉源驅動未驗證**：`nvidia-drm.modeset=1` 是前提，很多設定下拒絕匯出 scanout。在真實硬體上驗證之前不承諾支援。
- **Intel／AMD 的壓縮或 tiled scanout（CCS、DCC）目前是大聲失敗**，不是黑畫面：訊息裡有驅動名與 fourcc/modifier 的實際數值。
  vmwgfx 是線性的，這台 VM 永遠測不到那一階；`DrmFormatSupportTests` 用真實的 modifier 組合釘住「永遠不會被當成線性」。
- 音訊、剪貼簿在 daemon 模式下是 fake（沒有 session 就沒有來源）。游標是 `ScanoutCursorProvider`：一個固定的箭頭形狀、不回報位置。
  原本的 fake 給的是全透明 16x16，檢視端自己的指標會跟著變成隱形（使用者回報「連 Linux 沒有本地游標」）。
  **vmwgfx 沒有 cursor plane**（8 個 plane 全是 PRIMARY，實測），mutter 把指標畫進畫面裡，所以遠端看得到主機的指標在畫面中移動；
  「遠程游標」開關在這種機器上沒有位置可畫。有 cursor plane 的顯示卡（Intel/AMD）可以讀 plane 的 fb 當形狀、`CRTC_X/Y` 當位置，
  等有硬體可量再做。
- **單檔發布必須內嵌原生程式庫**（`IncludeNativeLibrariesForSelfExtract`，`publish.ps1` 對非 Windows RID 已開）：
  只複製 `DeskPair` 一個檔案去別台機器時，`libSkiaSharp.so` 留在發布資料夾裡，App 開視窗前就死（第二台 VM 實測）。daemon 沒有 UI 所以沒踩到。
- L2 的決策點（scanout 要不要取代已登入桌面的 X11 擷取）還沒定：靜態桌面上引擎 CPU 累計約 15%（ps 的行程平均，含啟動），
  連續動態的 fps 還沒量。目前 daemon 模式一律 scanout，app 自己跑引擎時一律 X11。
- `pkexec` 的提示是 polkit 的通用句子（沒有 `.policy` 檔）；設定頁在按下去之前先說明會安裝一個 root 服務與一個 `deskpair` 帳號。
- **macOS 的 `HandOver` 沒有 salt+h1 成對的規則**（B4）；那邊 store 是安裝程式建的，順序上踩不到，沒改。
- **daemon 模式不能從遠端改解析度**（2026-09-24）：`--service` 走 DRM scanout，沒有 `IDisplayModeSwitcher`，`DisplayInfo.modes` 為空，
  控制端不會出現選單；硬要送 `display_resolution` 會收到「This computer cannot change its resolution from here」（PeerCli 對 Linux 測試機實測）。
  Wayland 使用者工作階段同樣為空（Xwayland 的 RandR 是假的）。只有 Xorg 工作階段（app 自己跑引擎，`X11DisplayModes` 走 libXrandr 列舉、`xrandr` 切換）才有。
  之後若要在 daemon 下支援，是 DRM `drmModeSetCrtc` 那一階的事，跟虛擬顯示器一起看。

## G 無螢幕的主機（2026-09-25，遠端終端機的前置 E0）

純 CLI 的伺服器——沒有顯示卡（或有卡但沒接螢幕）、`uinput` 模組沒載——以前根本啟動不了 daemon：找不到 DRM card 就 `return 2`，
`UinputDevice.Create` 也是無條件的。而「用 `apt` 修一台沒有螢幕的機器」正是遠端終端機要的場景，所以這兩樣都改成**可缺席**：

- **daemon**：找不到 card 只記一行 warning，`reader = null`；`/dev/uinput` 開不了（不存在、或 ioctl 失敗）記一行 warning、`input = null`，
  引擎的命令列拿到 `--keyboard-fd -1 --pointer-fd -1 --relative-fd -1`。socketpair 照建：它除了送畫面還載著鎖定請求，之後還會載終端機。
- **`DrmCaptureServer`**：`reader` 為 null 時每次 poll 都回 `KindNoScanout` 加新旗標 `FlagNoHardware = 4`（`DrmWire`）。
  舊引擎讀成普通的「沒有 scanout」；新引擎（`DrmPollResult.NoHardware`）知道**不要去推滑鼠喚醒螢幕**——`DrmDisplayEnumerator` 原本會 nudge 之後等 4 秒。
- **引擎**：`UinputInputInjector` 任一 fd 為 -1 就把所有事件丟掉（`HasDevices = false`，開機記一行），鎖定請求仍走 daemon；
  `PlatformServices.LinuxScanout` 的 wake 只在有裝置時接上，並在零顯示器時記「viewers will be told so」。
  `HostMediaModule` 本來就在 `displays.Count == 0` 時不訂閱任何串流，`PeerInfo.displays` 是空的。
- **控制端**：桌面工作階段畫面中央顯示「這台電腦沒有螢幕／這裡不會有畫面；檔案傳輸與聊天仍可使用」（`session.noDisplays*`，十種語言）；
  PeerCli 印 `displays: 0`。手機端目前只是空畫面，字串在 E 的手機階段一起補。

實測（Linux 測試機，vmwgfx VM，用 `mount --bind /tmp/empty /dev/dri` 與 `mount --bind /dev/null /dev/uinput` 模擬，不必重開機也不改設定）：

```
daemon: No DRM card with a connected display under /dev/dri: this machine has no screen to show. It stays reachable; viewers are told there is no display.
daemon: No virtual input devices (uinput ioctl 0x40045564 (1) failed: errno 25): keyboard and pointer from viewers are ignored on this machine.
platform: Linux host under the daemon: scanout capture on fd 87, no input devices (viewers' input is ignored), encoders: H264Software, Vp9Software.
platform: This machine has no display to capture; viewers will be told so. File transfer and chat still work.
server: Host running: id=986444629
```

PeerCli 從外網打洞連上、永久密碼登入：`Logged in to test-vm (Ubuntu 24.04.2 LTS, deskpair); displays: 0; granted: PermKeyboard,…`。
umount 兩個 bind mount 再重啟 daemon，`Reading the screen from /dev/dri/card1 (vmwgfx)` 與 uinput 三個裝置都回來。

沒有真正的無 GPU 伺服器 VM，所以「真機」這一格還是 bind mount 模擬；DRM 的失敗路徑（有卡但 `HasConnectedDisplay()` 為假、或 `drmOpen` EACCES）走的是同一個 `FindCard` 回 null 的分支。

## I 任意解析度與沒有 GPU 的畫面（2026-09-25，虛擬顯示器 G2）

Linux **不能新增顯示器**（沒有等同 Windows IddCx 的機制），但 X11 能教一個輸出它沒宣告過的尺寸：
控制端要求的尺寸不在清單上時，`X11ModeTeacher` 以 CVT reduced blanking 時序 `xrandr --newmode deskpair-WxH …`、
`--addmode <輸出> deskpair-WxH`，之後就和一般改解析度一樣（記原值、確認、結束還原）。最後一位連線者離開、螢幕都還原之後，
先 `--delmode` 再 `--rmmode` 把教過的模式拿掉——順序是必要的，使用中的模式 `--rmmode` 會得到 `BadAccess`。

- **只有 X11 使用者工作階段。** Wayland 下 RandR 是 Xwayland 的假象；DRM daemon 沒有 X。這兩種情況控制端的解析度清單照舊。
- **時序在 C# 算**（`Cvt`，照 libxcvt 移植，連 float/int 的截斷與 hsync 起點的進位怪癖都照搬），測試逐字比對 `cvt`／`cvt -r` 在
  Ubuntu 24.04 的輸出 18 組。寬度不是 8 的倍數時（1366、1234），時序照進位後的寬度算、可見寬度保留要求的值，所以拿到的是剛好的尺寸。
- **名字**：模式叫 `deskpair-WxH`，一眼看得出是誰建的，也保證 `--rmmode` 永遠不會碰到使用者自己建的模式。
  `xrandr --mode` 是依**名字**找模式，所以切換時先查該輸出上這個尺寸的模式叫什麼（通常就是 `WxH`，教過的則是 `deskpair-WxH`）。
- **引擎當掉留下的模式**：同名 `--newmode` 會得到 `BadName`，此時直接 `--addmode` 沿用它，結束時一樣拿掉。

### 沒有 GPU 的主機：Xorg + dummy 驅動

DRM daemon 讀的是 scanout，沒有顯示卡就沒有東西可讀，也沒有 X 可以 `xrandr`。要讓這種機器有畫面可看，部署一個
**用 dummy 驅動的 Xorg**：它給出任意大小的真 X 螢幕，擷取走既有的 X11 路徑，上面的任意解析度也照樣能用。

```
sudo apt install xserver-xorg-video-dummy
```

`/etc/X11/xorg.conf.d/10-dummy.conf`（或啟動 Xorg 時 `-config` 指定）：

```
Section "Device"
  Identifier "dummy"
  Driver "dummy"
  VideoRam 256000
EndSection
Section "Monitor"
  Identifier "dummymon"
  HorizSync 5.0 - 1000.0
  VertRefresh 5.0 - 200.0
EndSection
Section "Screen"
  Identifier "screen"
  Device "dummy"
  Monitor "dummymon"
  DefaultDepth 24
  SubSection "Display"
    Depth 24
    Virtual 4096 2160
  EndSubSection
EndSection
```

`Virtual` 是這個螢幕能到的最大尺寸；`VideoRam`（KB）要夠放下它。輸出叫 `DUMMY0`，預設 2048x1536。

**實測（Linux 測試機，Ubuntu 24.04，xserver-xorg-video-dummy 1:0.4.0）**：以上設定起 `Xorg :50`，
1. 手動照 `X11ModeTeacher` 的順序下指令：`--newmode` 成功、重複一次得到 `BadName`、`--addmode` 可重複、
   `--mode 1234x567` 找不到（必須用名字）、`--mode deskpair-1234x567` 得到剛好 1234x567、使用中 `--rmmode` 得到 `BadAccess`、
   還原後 `--delmode`、`--rmmode` 都成功，沒有留下任何 `deskpair-` 模式。
2. 用真的 `DisplayModeService`＋`X11DisplayModes`＋`X11ModeTeacher`（一次性的 linux-x64 小程式）：2048x1536 → 教並切到 1234x567
   （確認通過、清單列得出來）→ 切到 1920x1080 → 還原 2048x1536，教過的尺寸不再列出，`xrandr` 裡 0 個 `deskpair-` 模式。

還沒做的：在這種 Xorg 上跑整個 DeskPair 引擎（`--server`）當作無螢幕主機的正式部署方式，以及 systemd 讓它開機就起來。

## H 遠端終端機（2026-09-25）

- 終端機預設關閉；擁有者要在設定頁明確開啟（見 `docs/architecture.md` §6.7）。
- **root 由 daemon 決定，不由引擎**：`/etc/deskpair/daemon.conf` 的 `terminal-root`。`--install-service` 第一次安裝時寫入 `yes` 並警告；之後的升級不覆寫。
  管理者不願意「deskpair 帳號等同 root」就改成 `no`（不必重啟，每次開 shell 都重讀），之後的終端機就是 `deskpair` 帳號本身（可以自行把它放進 sudoers，sudo 自己的政策與紀錄就會生效）。
- root 與「登入使用者」的 shell 是暫時性的 systemd unit：`systemctl list-units 'deskpair-shell-*'` 看得到正在跑的，`systemctl stop deskpair-shell-…` 可以手動結束。
  它們不在 daemon 的沙箱裡（理由見 architecture.md），`journalctl -u sunllo-deskpair` 會記「Terminal … started as root」與結束代碼。
- **v1 沒有 PAM**：這些 shell 不會出現在 `who`／`last`；稽核靠 DeskPair 自己的連線紀錄（每條連線開過幾個 shell、以什麼身分）與 journal。
- **身分**：閘門是 `yes` 時，最高身分是 `root`，「目前登入的使用者」是座位上登入的使用者（鎖定中也算），沒有人登入就拒絕。
  閘門不是 `yes` 時，兩個選項都退回 `deskpair`，而且身分老實寫 `deskpair`，不假裝。完整的對照表在 [terminal.md](terminal.md)。
- **上限**：每個引擎同時最多 8 個 shell、每分鐘最多開 20 個；超過時 journal 有 `Refused a terminal: N running, M opened in the last minute`。
- **引擎死掉時**：daemon 與引擎之間的通道一關閉（引擎結束或當掉），daemon 就停掉它開的所有 unit。
- **實測（Linux 測試機，2026-09-25）**：root 的 `/usr` 可寫、可以建 setuid 檔（證明不在 daemon 的沙箱裡）；登入使用者有 8 個群組與家目錄；
  閘門改成 `no` 後退回 `deskpair`；Ctrl+C 與結束代碼正確；`setsid -f` 出去的行程隨 unit 一起被停掉；`kill -9` 引擎後 unit 與行程歸零；
  連線紀錄寫下 `TerminalOpens`／`TerminalIdentity`。

## J 已登入的 Wayland 桌面：工作階段代理（2026-09-26，H6）

**為什麼要有**：daemon 讀的是顯示硬體。看得到登入畫面和鎖定畫面，但只有第一個 scanout（第一台螢幕）、沒有游標，
KDE 放在其他 plane 的東西（面板）是黑的。portal 看得到每一台螢幕、有游標、輸入依串流換算，但只在解鎖的使用者工作階段裡，
而且只接受那個使用者 session bus 上的呼叫——引擎是 `deskpair` 帳號，沒有那條 bus。

```
daemon (root)
  ├─ 引擎 (deskpair)：DRM + uinput，永遠都在，網路、編碼、ID、密碼都在這裡
  └─ 工作階段代理 (那個使用者)：DeskPair --session-agent --agent-fd N --drop-uid U --drop-gid G --drop-user NAME
        只在 seat0 的使用中工作階段是某個使用者的 Wayland 桌面時存在；鎖定屬於同一個工作階段，所以鎖定時仍在；
        換人、登出就停；引擎重啟時也重起（socket 屬於領走它的那個引擎）
引擎 ↔ 代理：daemon 建的 socketpair。引擎比任何人登入都早啟動，所以不能在啟動時交接：代理那端先放在 daemon 的
AgentHandoff，引擎每秒以 DrmWire 11／12 詢問一次、領走後 daemon 就不再經手。
```

- **代理只做 portal**：開工作階段（token 隨請求帶來、隨回覆帶回，由引擎依 uid 存在 `/var/lib/deskpair/secrets/wayland-portal-<uid>`）；
  每條串流開一條 PipeWire 連線，以 SCM_RIGHTS 交給引擎，由引擎自己讀（memfd 零複製照舊）；把引擎的輸入轉成 portal 的 Notify*；
  portal 自己結束時通知引擎。協定是手寫的 `AgentWire`，引擎把代理送來的一切當成不可信輸入（長度、數量、種類、座標都檢查）。
  代理的環境取自那個使用者自己桌面行程的 environ（`LinuxSessions.EnvironmentOf`，包括 session bus 位址與 XDG_CURRENT_DESKTOP），
  除此之外一律從空白建起；日誌走 stderr，也就是 daemon 的 journal。只採用這個工作階段開始之後才起的行程（以 logind 記錄的
  leader 的啟動時間為準，`/run/systemd/sessions/<id>` 的 `LEADER=`），桌面還沒起來時 supervisor 每秒看一次，等到有了才啟動代理。
- **鎖定畫面的點擊（H6f）**：硬體的畫面只有一台螢幕，daemon 的 uinput 絕對座標卻被 mutter 分攤到整個桌面（所有邏輯螢幕合起來的範圍）。
  代理以使用者身分讀 `org.gnome.Mutter.DisplayConfig.GetCurrentState`（`MonitorsChanged` 時再讀），換成每台螢幕在桌面上的位置
  （目前模式的大小、轉 90／270 度時對調、logical 版面時除以縮放並照 mutter 的 roundf），以 `AgentWire` 10 告訴引擎；daemon 經 encoder
  找出 scanout 的 CRTC 真正接的接頭（kernel 的類型與編號，放在 DrmWire 畫面資訊的 88–95 位元組；mutter 的 HDMI-1 就是 kernel 的 HDMI-A-1），
  引擎把畫面上的點換成整個桌面上的點。桌面以畫面的像素為單位，所以縮放過的螢幕也不失精度；畫面與螢幕形狀不同（螢幕轉了 90 度而 scanout
  是橫的）時不換算。uinput 的座標也改成瞄準像素中心：libinput（mutter、Xorg 的 libinput 驅動）以 (值 − 最小) × 寬 / (最大 − 最小 + 1)
  換回，中心值換回來剛好是那個像素；原本瞄準像素邊緣，寬桌面左邊幾十個像素會少一個。
- **引擎的畫面是 `ScanoutPortalHost`**：portal 開著時用 portal，其餘時間用硬體。GNOME 鎖定會結束分享 → 切到硬體（檢視端只看到
  顯示器清單改變，沒有人被告知「停止分享」）→ 遠端輸入密碼解鎖 → portal 自動重開。鎖定（`LockWorkstation`）一律走 daemon；
  放開按鍵兩邊都做。
- **只在不會詢問時才開 portal**：有這個使用者的同意（token），或是 KDE Plasma 5（只通知不詢問）。無人值守的機器不該因為有人連線，
  就在坐在前面的人眼前跳出對話框——硬體本來就看得到畫面。沒有同意時用硬體；有 token 卻開始詢問（還原失敗，超過 1.5 秒），
  就立刻取消（對話框收掉）、忘掉 token、這個代理不再嘗試，也不告訴檢視端「正在詢問」。
- **同意**：設定 › 安全 › 「允許遠端分享螢幕」在裝了無人值守之後改經 IPC（`DesktopSharingRequest` 32、`DesktopSharingState` 33，
  只有 Owner 能用，而且只回答呼叫者自己帳號的桌面）請引擎→代理在使用者的螢幕上跳一次對話框。**不能在 App 行程裡直接問**：
  還原 token 綁定要求它的 app id，從桌面啟動的 App（`app-*.scope`）拿到的 token，代理（daemon 的 cgroup）用不了。
  問的時候要等人回答，所以引擎另外回覆（`IpcServer.SendToAsync`），不擋住同一個視窗的其他請求；有檢視端正在經由 portal 看時不問
  （代理一次只開一個工作階段，而那一個本來就已經被允許）。實驗機上可用 PeerCli 做同樣的事：
  `peercli ipc-sharing --token "$(cat /run/deskpair/<uid>/ipc.token)" --socket /run/deskpair/DeskPair.sock [--ask]`。

**實機第一次跑就踩到的三件事**（都已修，理由寫在程式旁）：
1. 單檔發布的原生函式庫在 Main 之前由 root 解壓到 0700 的目錄，降權後的引擎一個都載不到——H6 之前引擎從沒載過，
   Wayland shim 是第一個。現在降權前先把這個行程的解壓目錄交給要變成的帳號；代理不載入任何原生函式庫，共用引擎那份，不交。
2. portal 拒絕代理：「Unable to open /proc/&lt;pid&gt;/root」。降權後核心把行程標成 non-dumpable、/proc 歸 root，而 xdg-desktop-portal
   以使用者身分讀呼叫者的 `/proc/<pid>/root` 來判斷它是不是沙箱程式。代理降權後設回 dumpable（它從此就是使用者自己的行程）；引擎不變。
3. 代理啟動 45 秒後被 SIGTERM（exit 143）：`PR_SET_PDEATHSIG` 綁的是 fork 它的**執行緒**。supervisor 原本在執行緒集區上 tick，
   第一個引擎剛好由主執行緒啟動所以沒事，之後才啟動的代理在那條集區執行緒退休時就被殺；引擎當掉重啟時其實也有同樣的風險。
   supervisor 現在整個生命週期都在自己的專屬執行緒上。

**實測（Linux 測試機，GNOME 46，兩台 800×600，0.3.1+903fd55）**：代理以 uid 1000 啟動並交給引擎；portal 用記住的同意 6–14 ms 開啟，
引擎（deskpair）經代理給的 fd 讀 PipeWire「4 buffers, handed on uncopied」；同一條連線裡：portal 桌面 → 檢視端鎖定 →
GNOME 結束分享、切到硬體看到鎖定畫面 → 經 uinput 輸入密碼解鎖 → 2 秒內 portal 以 6 ms 重開。`ipc-sharing --ask` 讓對話框出現在使用者螢幕上，
經 daemon 點「Share」後回報「allowed; asked: allowed」；新的同意以 7 ms 還原（兩台沒有 EDID 的虛擬螢幕被 GNOME 還原成同一台兩次，去重成一個顯示器）。
代理連續執行 12 分鐘以上沒有再被殺。

**H6f 實測（Linux 測試機，GNOME 46，兩台 800×600）**：代理回報「Virtual-1 800x600+0+0, Virtual-2 800x600+800+0」，daemon 以 encoder 確認
scanout 是 Virtual-1；檢視端鎖定後在硬體畫面上點 (775,12)（頂列的狀態區），快速設定選單在第一台螢幕打開，引擎記錄「Clicks on the
hardware's picture go to Virtual-1 800x600+0+0 on a desktop of 1600x600」（修正前這一下會落在第二台的 x=1550）；接著輸入密碼解鎖，portal 24 ms 重開。

**KDE 重測（Plasma 5.27，同一版）**：代理以「KDE」啟動，沒有 token 也 95 ms 開 portal（兩台：wayland#1 在 0,0、wayland#2 在 800,0），
登入後 1.5 秒內檢視端就從硬體換到 portal；鎖定時分享不中斷，檢視端看到 kscreenlocker、經 portal 輸入密碼解鎖；`ipc-sharing` 回報 allowed；
在檢視端的第二個顯示器移動游標，KWin 的 `activeOutputName` 是 Virtual-2，回到第一個是 Virtual-1。KDE 沒有 Mutter，代理試 15 次（約 30 秒）
後留一行 Information，其他不受影響。

**同一次重測抓到的競態**（已修）：切回 GNOME 時代理在登入的瞬間就啟動，那時還沒有任何桌面行程帶著顯示環境，代理回報「desktop unknown」——
如果是 KDE，引擎就認不出它不會詢問，只能用硬體。現在 supervisor 等到這個工作階段的桌面行程出現才啟動代理，而且只採用 leader 之後才起的行程，
不會撿到上一個工作階段殘留的使用者服務（例如從 KDE 換到 GNOME 時還沒結束的 KDE 服務）的環境。重測：工作階段上線，1 秒後代理啟動，讀到 ubuntu:GNOME。

**限制**：
- 登入畫面、鎖定畫面仍走硬體，兩台螢幕時只看得到第一台。鎖定畫面的點擊由代理回報的 Mutter 配置換算（H6f）；**登入畫面沒有代理**，
  GDM 的 mutter 一樣把指標分攤到整個桌面，所以兩台螢幕時登入畫面的點擊仍會偏（兩台一樣寬時 x 放大一倍）。鍵盤不受影響：
  登入畫面按 Return 就選到使用者，再打密碼即可。轉了 90 度而 scanout 是橫的螢幕不換算。
- 剪貼簿、音訊在 daemon 下仍是 fake；之後可以由代理提供（Xwayland 剪貼簿、以使用者身分讀 PulseAudio）。
- X11 的使用者工作階段不起代理（仍是硬體路徑）。Plasma 6 若會詢問，第一次會被偵測到並取消，要到設定頁允許一次；未在 Plasma 6 實測。

## F 量測用的東西

- `tools/DeskPair.Tools.LinuxHarness`：`drm-grab`（root 端 + 降權子行程走產品自己的 `IScreenCapturer`，每張影格一行數字）、
  `uinput-click --x --y --text [--then escape]`。
- `tools/DeskPair.Tools.PeerCli connect … --frame-stats`、腳本 `:lock` `:save PATH.png` `:sleep N`。
- 探針留在 session 的 scratchpad（`drmprobe.py`、`uinputprobe.py`、`fdprobe/`），不進 repo；它們是日後請客戶跑的診斷工具雛形。
