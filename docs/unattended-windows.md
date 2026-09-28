# Phase 7：Windows 無人值守與鎖定畫面控制

## 為什麼要做

被控端停在鎖定畫面或登入畫面時，連上去是黑屏。使用者按到 UAC 提權對話框時，也是黑屏。

這不是 bug。Windows 的鎖定畫面、登入畫面與 UAC 對話框都畫在 **Winlogon secure desktop** 上，
一般使用者權限的行程既讀不到、也不能往那裡送輸入。現有的日誌把這件事講得很清楚：

```
DxgiScreenCapturer: Desktop duplication unavailable on \.\DISPLAY209 ... E_ACCESSDENIED
GdiScreenCapturer: BitBlt cannot read \.\DISPLAY209 (error 0); the desktop is locked, switched
                   or not this process's to read
```

`CLAUDE.md` 目前寫著「There is no OS service and no unattended access」。**這一階段推翻它**，
而且要清楚知道推翻的是什麼：鎖定畫面控制與無人值守在技術上是同一件事。能在沒有人坐在電腦前的
情況下擷取 secure desktop，就等於能在沒有人按下「接受」的情況下被連線。

## A 架構

### A1 目標形狀

| | 現在 | 之後 |
|---|---|---|
| 行程數 | 一個（UI + 引擎同行程） | 三個：UI、服務、引擎 |
| 引擎身分 | 登入使用者 | **SYSTEM**，在 console session |
| 可見桌面 | Default（使用者桌面） | Default **與** Winlogon |
| 電腦鎖定時 | 引擎還在跑，但抓不到畫面 | 照常 |
| 沒有人登入時 | 引擎沒在跑 | 照常 |

三個行程的分工：

- **`DeskPair.exe`（無參數）** — Avalonia UI。跟現在一樣，以登入使用者身分執行，
  透過具名管線連到引擎。**不再內含引擎。**
- **`DeskPair.exe --service`（新）** — Windows 服務，LocalSystem，開機自動啟動。
  它自己不擷取畫面，只負責一件事：確保 console session 裡隨時有一個引擎在跑，
  而且跑在使用者當下看的那個桌面上。
- **`DeskPair.exe --server`（已存在）** — 引擎。由服務以 SYSTEM 權杖啟動進 console session。
  這個角色已經寫好了，這一階段不改它做的事，只改誰啟動它、用什麼身分。

`--server` 已經存在且已經是「引擎、無視窗」，這是這個計畫成立的主要原因——**要搬的是啟動方式，
不是引擎本身**。

### A2 服務怎麼把引擎放進正確的桌面

參考實作（`reference/rustdesk-master/src/platform/windows.rs:820`）的做法，我們照抄結構：

1. `WTSGetActiveConsoleSessionId()` 取得目前實體螢幕對應的 session。
2. 在那個 session 裡找到 **`winlogon.exe`**，`OpenProcessToken` 取得它的權杖並複製。
   用 winlogon 的權杖而不是自己的，是因為它已經附著在 secure desktop 上。
3. `CreateProcessAsUser` 以那個權杖啟動 `DeskPair.exe --server`，
   `lpDesktop` 指向 `"winsta0\default"`。
4. **桌面切換要跟著走。** 使用者鎖定螢幕、切換使用者、或跳出 UAC 對話框時，
   輸入桌面會從 `Default` 換成 `Winlogon`。服務用 `OpenInputDesktop()` 輪詢
   （或監聽 `WTSRegisterSessionNotification`），發現桌面換了就把引擎重啟到新桌面上。

> **重啟引擎會中斷進行中的連線。** 這是這個做法的已知代價，RustDesk 也是如此：
> 遠端使用者按下 UAC 的那一刻，畫面會黑一下再回來。替代方案是引擎自己
> `SetThreadDesktop` 到新桌面而不重啟，省掉那一次中斷，但要保證擷取與輸入兩條路徑上
> **每一個**執行緒都跟著換——漏掉一個就是難以重現的靜默失效。
> **先做重啟版，把桌面切換的偵測與記錄做對**；`SetThreadDesktop` 版本等到重啟版穩定後再說，
> 它是最佳化，不是前提。

### A2b 服務看不到互動 session 的桌面（實測才發現）

第一版的 supervisor 自己呼叫 `OpenInputDesktop()` 來判斷要把引擎放哪裡。**那行不通，而且是靜默地行不通。**

服務在 session 0，它的 window station 是 `Service-0x0-3e7$`，而 `OpenInputDesktop` 只回答呼叫端自己
window station 上的事。從服務問，互動 session 的桌面不是看不見，是**問不到**，而且永遠不會有錯誤。

改成**引擎自己回報**：它在 session 裡面，同一個呼叫就是真的。發現自己所在的桌面不再是輸入桌面時，
就帶著一個指名桌面的結束碼退出，服務讀結束碼再把它放到對的桌面重生（`DesktopWatch`）。
結束碼：64 = Default、65 = Winlogon、66 = Screen-saver。

第一次啟動先試 `Default`；如果當下是鎖定的，那個引擎會立刻回報 `Winlogon` 並退出。
**一次浪費的啟動，換掉所有猜測。** 實測：

```
09:06:32.772  Engine 41064 started in session 9 on Default
09:06:33.783  Engine 41064 stood down for desktop Winlogon
09:06:33.789  Engine 8512 started in session 9 on Winlogon
```

> **連帶的教訓：任何「什麼都不做」的分支都要能說出原因。**
> 這個 bug 的外觀跟「服務正常運作但就是沒反應」一模一樣。supervisor 現在會在原因**改變時**
> 寫一行（`Not running an engine: ...`），不會洗掉 log，也不會默不作聲。

### A2c 一台機器只能有一個引擎（實測才發現）

服務裝上去之後，它的引擎跟使用者正在開的 app 內含的引擎同時跑，三個症狀同時出現：

| 症狀 | 原因 |
|---|---|
| 兩個引擎拿到同一個 rendezvous ID | 共用 `ProgramData` 資料目錄，所以共用身分 |
| `Cannot listen on direct-access port 21118` | 同一個連接埠 |
| `IPC accept failed: Access denied`（每秒約 5 次） | 同一個具名管線名稱，先啟動的贏 |

先啟動的贏不是行為，是意外。**服務裝著時，服務擁有引擎，app 不再啟動自己的**，
透過既有的 `ipc.token`（寫在 `ProgramData`，app 本來就讀那裡）連上去。
這個性質由 `EngineHostTests` 釘住。

### A3 UI 與引擎之間

`IpcEndpoint` 已經支援帶 ACL 的具名管線（`NamedPipeServerStreamAcl.Create`，`IpcEndpoint.cs:78`），
所以跨身分的通道**機制上已經在了**，缺的是正確的 ACL。

- 引擎現在是 SYSTEM，UI 是一般使用者：管線要明確允許 `Interactive`（已登入的互動使用者）讀寫，
  **不是** `Everyone`。
- **這是這一階段最大的新攻擊面。** 一個以 SYSTEM 執行、接受本機任何互動使用者指令的管線，
  等於把 SYSTEM 權限交給那個管線的協定正確性。`ipc.proto` 的每一個指令都要重新檢視一次：
  現在哪些是「改設定」，哪些其實是「以 SYSTEM 執行某件事」。
- 標準使用者不應該能透過 UI 關掉無人值守、改永久密碼、或停用服務。
  **需要哪些操作要提權，要先列出來再實作**，不能事後補。

### A4 密碼與同意

無人值守的意思是「沒有人會按接受」，所以：

- **永久密碼從可選變成必要。** 開啟無人值守時若沒有設定永久密碼，就不能開啟。
- `HostConfig.ApproveMode` 現在是 `ApprovePassword`；要新增一個明確的無人值守模式，
  而不是讓「沒人按就逾時通過」偷偷發生。
- `HostPasswords` 的暴力破解輪替邏輯要重新檢視：現在輪替的是暫時密碼，
  而無人值守下真正被攻擊的是永久密碼，**永久密碼不能自動輪替**（輪掉就再也連不上），
  所以需要別的防線——失敗次數上限、來源鎖定、或兩者。
- 被連線時本機要有可見的痕跡。現在有連線管理卡片，但鎖定畫面上看不到它；
  至少要確保**解鎖後看得到剛才發生過什麼**。

### A4 訂正：防線大多已經在了，缺的是別的

規劃時列的三件事，實際查過之後兩件已經是對的：

- **永久密碼不會被失敗輪替** —— `HostPasswords.Verify` 只輪替臨時密碼與連結密碼。
- **核准逾時往拒絕倒** —— `ApprovalTimeout` 到期回 `ApprovalTimeout` 錯誤，不是放行。
- 每來源的暴力破解追蹤（`LoginFailureTracker`，短/長雙窗口）也已存在。

真正缺的是：**沒有永久密碼時，裝服務是無意義的**。鎖定畫面上沒人讀得到輪替的臨時密碼，
而點選接受需要連線管理員（即 app），而沒人登入時 app 不在跑。所以 `--install-service`
在沒有**可讀取的**永久密碼與 salt 時直接拒絕安裝。

### A6 IPC 指令盤點與呼叫端識別

每個 IPC 指令分類為 `Any` 或 `Owner`，**未列出的一律當 `Owner`**，並由測試走過 proto 裡每一個
case 檢查完整性。身分不看 hello 裡的角色、也不看權杖檔，而是讀**呼叫端行程的權杖**：
帳號、是否管理員、在哪個 session。

實作途中被「實際跑一次」抓到的三個錯：`RunAsClient` 需要用戶端授予模擬權限（預設不給，
會讓所有呼叫端都變成無法識別）；延遲讀取「引擎自己的帳號」會在模擬區塊內執行，
使第一個連進來的人變成「引擎的帳號」；**只擋請求沒擋推播**，密碼每次輪替都會推給所有用戶端。

### A7 祖密儲存的加密範圍（實測才發現，而且造成實際損害）

`WindowsSecretStore` 用 DPAPI，而所有呼叫點都傳 `machineScope: false`。服務的 SYSTEM 引擎
讀不到使用者加密的祖密，而 `GetAsync` 把「解不開」當成「不存在」，於是產生新的並覆寫。

**實際結果：一台主機失去了 `identity-key`、`password-salt`、`peer-id`，不可復原。**

三層修正：

1. `GetAsync` 解不開時丟 `SecretUnreadableException`，不再回 null。**這是根因，而且跟服務無關。**
2. 加密範圍跟著執行身分：LocalSystem 用 machine scope（才能與 app 共用同一個身分），
   其他用 user scope（更強）。讀取時兩個範圍都試。
3. 遷移不能自動：LocalSystem 根本解不開使用者的 blob。由 `--install-service` 在**提權但仍是該使用者**
   的那一刻做一次性交接。

> **還沒做的：`secrets/` 目錄的 ACL。** machine scope 的 blob 能被機器上任何讀得到檔案的行程解開，
> 而 `ProgramData` 預設給 `Users` 讀取權。要鎖成 SYSTEM + Administrators + 該使用者，而且不能鎖到
> 一般使用者的 app 連不上。**排入第 7 步。**

### A5 安裝與移除

- 新增 `--install-service` / `--uninstall-service` 兩個角色，沿用 `--allow-firewall` 的提權慣例
  （`SecuritySettingsViewModel.cs:267` 的 `Verb = "runas"`）。
- 服務不是預設安裝。使用者要在「設定 → 安全性」明確開啟「允許在鎖定畫面時連線」，
  那個開關才去裝服務。**預設維持現在的行為。**
- 移除程式時要移除服務。當時沒有安裝程式，所以 `--uninstall-service` 必須能獨立執行（zip 版至今仍靠它）。
  0.4.4 起有 MSI（`packaging/windows/README.md`）：移除時它以 LocalSystem 執行 `--remove-system-changes`，
  拆掉服務、防火牆規則與虛擬顯示器驅動；升級時只在換檔案前停止服務、裝完再啟動，服務的註冊保留。
  要無人值守的機器應該用安裝版：服務執行的 `DeskPair.exe` 放在 Program Files，只有系統管理員能換。

## B 只有 Windows

| 平台 | 可行性 |
|---|---|
| Windows | 本計畫，可行 |
| macOS | 登入視窗需要 LaunchDaemon，且 ScreenCaptureKit 對登入視窗有系統層限制；**這一階段不做** |
| Linux | 取決於 display manager 與 greeter；Wayland 下更受限；**這一階段不做** |

`ServerRole.cs:17` 的註解說明了引擎為什麼跟 UI 同行程：
「macOS grants Screen Recording and Accessibility per executable, so a second binary is a second consent」。
**那個理由在 Windows 不成立，但在 macOS 成立**，所以拆行程只能是 Windows 的條件式行為，
不能三個平台一起改。這會讓 `Desktop` 專案出現一條平台分支——是刻意的。

## C 測試

- **`The_service_follows_the_input_desktop`** — 模擬桌面切換，斷言引擎被重啟到新桌面。
- **`Unattended_requires_a_permanent_password`** — 沒有永久密碼時開啟無人值守必須失敗。
- **`The_pipe_refuses_a_non_interactive_caller`** — ACL 的實際驗證，不是看設定值。
- **`Every_privileged_ipc_command_is_listed`** — 反射列出所有 IPC 指令，
  對照一份明確的「這個指令需要什麼權限」清單。**少了這個，日後新增一個指令不會有人發現它變成了提權管道。**
- 現有的擷取測試在無互動桌面時會跳過（`InteractiveDesktop.IsAvailable`）。
  服務版的擷取**不該**跳過，因為它正是為了沒有互動桌面的情況而存在——需要另一組標記。

## D 順序

1. ~~`--install-service` / `--uninstall-service` 與服務骨架~~ **完成**
2. ~~winlogon 權杖 + `CreateProcessAsUser`，把 `--server` 放進 console session~~ **完成**
3. ~~輸入桌面追蹤與引擎重啟~~ **完成**（改由引擎回報，見 A2b）
4. ~~app 在服務存在時交出引擎~~ **完成**（見 A2c）
5. ~~管線 ACL 與 IPC 指令權限盤點~~ **完成**（見 A6）
6. ~~永久密碼強制~~ **完成**；無人值守模式與失敗次數防線**已經存在**（見 A4 訂正）
7. ~~`secrets/` 目錄 ACL~~ **完成**；~~UI 開關與提權流程~~ **完成**（開啟後自動交接引擎，見下）
8. ~~實機驗證~~ **完成**

## 結果

實機端到端：從遠端鎖定機器 → 看到即時的鎖定畫面 → 按鍵叫出懑證畫面 → 輸入 PIN →
**機器解鎖**（`logonui` 1 → 0）→ 解鎖後畫面繼續。全程同一條連線，沒有重連。

### 三個被實測推翻的判斷

**一、「引擎跟著桌面重啟」不是代價，是阻礙。** 輸入桌面切換必然發生在要打密碼的前一刻，
所以密碼永遠打不完。已移除整套機制（`DesktopWatch`、結束碼協定、supervisor 的桌面追蹤）。

**二、擷取也是綁桌面的。** 我曾根據一次成功的截圖說「擷取讀的是顯示器」——這是錯的。
鎖定畫面的**時鐘狀態**還在 `Default`，按鍵後的**懑證畫面**才在 `Winlogon`。

**三、輸入一直是對的。** 失敗是因為我輸入帳戶密碼，而那台機器用 Windows Hello PIN 登入。

### 最終的形狀

| 路徑 | 做法 | 理由 |
|---|---|---|
| 輸入 | 專屬執行緒，**每一批**都重新附著 | 低頻，而且必須到達正確桌面 |
| 擷取 | **只在 BitBlt 失敗時**附著並重試一次 | 每秒 60 張，不該為永遠不發生的事多付代價 |
| 引擎 | 永遠啟動在 `winsta0\Default` | 不再需要知道輸入桌面是哪個 |

> **第 7 步原本不做「開啟後讓 app 當下交出引擎」，只叫使用者重開。2026-09-25 在 Windows 11 測試機實測證明這樣不行，已改成自動交接。**
> 重開之前兩個引擎並存、用同一個 ID：設定頁存下的設定寫進即將被換掉的那個引擎（「允許新增顯示器」因此沒生效），
> 服務的引擎搶不到管線，每 200 ms 記一次含堆疊的錯誤（80 秒 409 次）。反方向也有洞：服務停了（無論誰停的），
> app 仍掛在不存在的引擎上，電腦離線，只能把「鎖定時仍可被連線」關掉再打開。現在 `EngineHost` 每 5 秒看一次服務：
> - 服務在**執行**（不只是已安裝）→ app 自己的引擎立刻停掉，HostLink 下一次重連時改讀服務的權杖。
> - app 沒有自己的引擎而服務連續 4 次（約 20 秒）都不在 → app 自己跑引擎，直到服務回來再交還。
>   等 20 秒是因為服務當掉時自己的復原會在 5 秒後重啟，太早接手只會馬上再交回去。
> - 設定頁的開關看「有沒有在執行」：已安裝但停止的服務顯示為關，打開就啟動它（安裝角色本來就會啟動既有的服務）。
> 交接時服務的引擎可能搶不到 direct-access 埠（app 的引擎還沒放手），以前那一輪就沒有直連；現在埠被占用時每 5 秒再試，
> 一放手就開始聽（實測交接 4 秒內完成；`DirectAccessTests` 釘住）。中繼與打洞本來就不受影響。
> IPC 接受失敗現在只記第一次，之後只計數，恢復時記一行。

> **實測要用獨立的 `--data` 目錄。** 這一輪直接在工作機上裝服務，結果引擎頂替了使用者正在用的
> 主機身分約四十秒。

## E 過渡期

服務做好之前——以及在 macOS 與 Linux 上永遠——主機抓不到畫面時仍然會是黑屏。
**這件事現在就該修**，而且跟服務無關：

`GdiScreenCapturer.Unreadable()`（`GdiScreenCapturer.cs:53`）目前回傳 `CaptureResult.TimedOut`，
跟「畫面沒變」無法區分，所以檢視端只看到一片黑而沒有任何說明。
需要一個明確的狀態，一路傳到檢視端顯示成一句話——
例如「對方電腦在鎖定畫面，需要有人解鎖後才能看到畫面」。

`message.proto` 已經有 `MessageBox`（kind/title/text），目前沒有任何地方送或收。
那是現成的管道。

## F 不做

- **macOS 與 Linux 的登入畫面** — 見 B
- **`SetThreadDesktop` 免重啟切換** — 見 A2，最佳化不是前提
- **預設安裝服務** — 使用者要明確開啟
- **永久密碼自動輪替** — 無人值守下會把自己鎖在外面

## G 遠端終端機（2026-09-25）

行為、紀錄與排錯見 [terminal.md](terminal.md)，實作見 [architecture.md §6.7](architecture.md)。這裡只記服務特有的部分。

- **預設關閉。** 服務已安裝時，設定頁要先有固定密碼才開得起來：只剩一次性密碼擋在一個 SYSTEM shell 前面，而那個密碼要從沒人在看的螢幕上讀。
- **身分**：服務的引擎本來就持有 winlogon 的 LocalSystem token（見 A2），所以「最高身分」直接就是 `NT AUTHORITY\SYSTEM`。
  「目前登入的使用者」用 `WTSQueryUserToken` 取 token（先找主控台，再找任一個使用中的工作階段），
  `CreateEnvironmentBlock` 與 `GetUserProfileDirectory` 備好環境，`CreateProcessAsUserW` 啟動。**沒有人登入就拒絕並說原因，不會改給 SYSTEM。**
  引擎在 App 裡執行時，兩個選項都是目前使用者。
- **ConPTY ＋ Job object**：shell 以 `CREATE_SUSPENDED` 建立，放進 `KILL_ON_JOB_CLOSE` 的 Job object 之後才恢復執行，
  所以它啟動的任何東西都跟它一起結束——否則 `cmd` 結束後，孫行程會以 SYSTEM 身分繼續活著。引擎當掉時 Job 的 handle 關閉，結果相同。
  服務與測試主機的行程帶著會被繼承的「忽略 Ctrl+C」旗標，建立 shell 前要先 `SetConsoleCtrlHandler(NULL, FALSE)` 清掉，否則 shell 裡的 Ctrl+C 完全無效（實測）。
- **A3 那個攻擊面**：以 SYSTEM 執行、接受本機使用者指令的管線，加上終端機就更尖銳了。所以 **IPC 沒有「開終端機」的指令**，
  連線管理員的勾選框只能收緊（撤權），不能放寬；`TerminalIpcTests` 釘住這兩件事。
- **A4 那個取捨**：固定密碼刻意不因猜錯而輪替，所以**一次外洩的固定密碼就是一個永久的 SYSTEM shell**。
  用得到終端機的無人值守主機，建議選「目前登入的使用者」、開 IP 白名單並拒絕中繼連線（見 terminal.md「風險」）。
- **稽核**：shell 不是登入動作建立的（沿用引擎或使用者既有的 token），所以事件檢視器的安全性記錄不會有新的登入事件。
  紀錄在 `server.log`（`Terminal 1 opened as …`）與連線紀錄（每條連線開了幾個 shell、以什麼身分）。
- **尚未在服務下實測。** 開發機沒有提權，也沒有裝服務。要驗的是：
  1. 最高身分：`whoami` 為 `nt authority\system`。
  2. 登入使用者：`whoami` 為主控台上的使用者，`$env:USERPROFILE` 是他的設定檔目錄。
  3. 登出所有人後改開登入使用者：被拒絕，訊息是「Nobody is signed in…」，而不是得到 SYSTEM。
  4. 在 SYSTEM shell 裡 `Start-Process ping -ArgumentList '-t','127.0.0.1'`，關閉視窗後 `Get-Process ping` 為空。

## H 虛擬顯示器（2026-09-25）

驅動是什麼、為什麼這樣用，見 [native/vdd/README.md](../native/vdd/README.md)；實作見 [architecture.md §6.4c](architecture.md)。這裡只記服務特有的部分。

- **只有服務的引擎能新增螢幕。** 啟用／停用裝置需要系統管理員權限。App 模式的引擎是使用者身分，
  會回報「This computer can add a display only while DeskPair runs as its service」，控制端看到的就是這句。
- **驅動要另外裝**：`DeskPair --install-virtual-display`（提權），移除用 `--remove-virtual-display`。與服務互相獨立：
  移除服務不會移除驅動，反之亦然。記錄寫在服務的 `service.log`。
- **擁有者要允許**：「設定 › 顯示」的「允許連線者新增這台電腦沒有的顯示器」（`HostConfig.AllowVirtualDisplay`，預設關）。另外請求者要有鍵盤滑鼠權限。
  同一區塊顯示驅動是否已安裝、是否還缺服務，並提供安裝／移除按鈕（按下後跳 UAC，執行上面兩個角色）。
- **控制端**：主機是 Windows 且能同時串流多個螢幕時，工具列有「＋」：以控制端自己螢幕的尺寸要求一台，成功後直接在獨立視窗打開。
  顯示器清單會在新增的螢幕後面標「新增的」；分頁正在看新增的螢幕時，有「移除這個顯示器」。
- **引擎結束，虛擬螢幕就收回**：正常結束時停用裝置；當掉的話裝置維持啟用，下一個引擎（服務會自動重啟它）啟動時先停用。
  停用帶 `CM_DISABLE_PERSIST`，所以重開機也不會冒出沒人要的螢幕。
- **不用 `SwDeviceCreate`**：它對 LocalSystem 一律回 `0x8007007E`，工作階段 0 或 1 都一樣（2026-09-25 以本節的終端機實測）。
- **尚未在真機實測。** 要在 Windows 11 測試機上驗的是：
  1. `--install-virtual-display` 成功，`pnputil /enum-drivers` 看得到 `MttVDD`，裝置管理員的「顯示卡」有停用的「Virtual Display Driver」，
     `certlm.msc` 的「受信任的發行者」裡**沒有**留下 SignPath。
  2. PeerCli `:vdisplay add 1920x1080`：出現第三個螢幕，尺寸正確，`:subscribe 0,2` 收得到它的畫面。
  3. `:vdisplay remove 2`：螢幕消失；再斷線：沒有任何虛擬螢幕留著。
  4. 在工作階段中讓引擎當掉（工作管理員結束），虛擬螢幕跟著消失。
  5. `--remove-virtual-display` 之後：`pnputil /enum-drivers` 沒有 `MttVDD`，裝置管理員（含隱藏裝置）沒有「Virtual Display Driver」，
     `HKLM\SOFTWARE\MikeTheTech` 與 `%ProgramData%\Sunllo\DeskPair\vdd` 都不見了。
