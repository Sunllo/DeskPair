# 遠端終端機

在另一台電腦上開一個命令列（shell）。它就是整台電腦：連線者能做 shell 那個帳號能做的任何事，
而在無人值守的主機上，那個帳號預設是 SYSTEM 或 root。所以它**預設關閉**，每條連線都要兩個刻意的動作才開得起來，
而且每開一個 shell 都留紀錄。

這份文件講它怎麼運作、以什麼身分執行、留下什麼、有什麼限制、壞了怎麼查。協定、流量控制與各平台怎麼開 pty，
寫在 [architecture.md §6.7](architecture.md)；安全回歸要驗的項目在 [hardening.md](hardening.md)。

## 兩個刻意的動作

1. **擁有者在主機上開啟。** 設定 › 安全 ›「開啟終端機…」，讀完說明再按「開啟」。刻意不做成勾選框。
   已安裝無人值守存取而沒有永久密碼（介面上叫「固定密碼」）時開不起來：那樣只剩一次性密碼擋在一個 SYSTEM／root shell 前面。
2. **這一條連線被允許。**
   - **以密碼登入**：開啟之後，拿到密碼的人就被允許——開啟時的說明寫的正是這句話。
   - **要核准的連線**：連線管理員的卡片寫「X 想要在這台電腦上開終端機」，下面有一個**預設不勾**的
     「允許終端機，以 {身分} 身分執行」，沒勾之前「接受」按不下去。已連上的終端機連線，這個勾選框就是即時開關。

控制端不能「要求」這個權限：連線選項與登入請求裡都沒有它，只有主機給了才有。
沒有這個權限的連線，控制端在送出任何終端機訊息之前就會說「這台電腦不允許終端機」——
主機沒開、核准時沒勾、主機太舊（沒有終端機的版本）三種情況，看到的是同一句話。

## 以什麼身分執行

設定頁的「shell 的執行身分」有兩個選項。實際得到的帳號取決於主機怎麼跑：

| 主機怎麼跑 | 這台電腦允許的最高身分（預設） | 目前登入的使用者 |
|---|---|---|
| Windows，App 裡的引擎 | 目前使用者 | 目前使用者 |
| Windows，無人值守服務 | `NT AUTHORITY\SYSTEM` | 主控台上登入的使用者（沒有就找任一個使用中的工作階段）；**沒有人登入就拒絕**，不會改給 SYSTEM |
| Linux，App 裡的引擎 | 目前使用者 | 目前使用者 |
| Linux daemon，`terminal-root = yes` | `root` | 座位上登入的使用者（鎖定中也算）；沒有人登入就拒絕 |
| Linux daemon，閘門不是 `yes` | `deskpair` | `deskpair` |
| macOS（App 或 launchd agent） | 登入使用者 | 登入使用者 |

- 身分在核准卡片上就寫出來（「以 root 身分執行」），連上後固定顯示在控制端頂端的黃字 `身分@主機`。
- **Linux 的 root 由 daemon 決定，不由引擎**：`/etc/deskpair/daemon.conf` 的 `terminal-root`，root 擁有、引擎寫不到，
  每次開 shell 都重讀。第一次 `--install-service` 寫入 `yes` 並警告「deskpair 帳號在這台機器上等同 root」，之後的升級不覆寫。
  詳見 [unattended-linux.md H](unattended-linux.md)。
- **shell 由主機決定，控制端不能指定**：Windows 用 Windows PowerShell，沒有就用 `cmd.exe`；Linux 與 macOS 用該帳號的登入 shell
  （`nologin`／`false` 退到 bash，再退到 sh），環境變數從零建立（`TERM=xterm-256color`）。Windows 的登入使用者 shell 用那個使用者自己的環境。
  請求裡只有身分種類與欄列數，沒有路徑、參數、工作目錄或環境變數。

## 控制端

- **桌面**：首頁的「終端機」、裝置清單的每一列、遠端控制工具列。開在獨立視窗。
  - Ctrl+C 永遠是中斷。複製與貼上是 Ctrl+Shift+C／V、Ctrl+Insert／Shift+Insert、macOS 的 Cmd+C／V，或右鍵選單。
  - 滾輪與 Shift+PageUp／PageDown 看捲動紀錄；在 vim、less 這類全螢幕程式裡，滾輪改送方向鍵。
- **Android／iOS**：連線頁輸入框下方的「開啟終端機」，密碼規則與遠端桌面相同。
  - 按鍵列有 Esc、Tab、Ctrl、Alt、方向鍵、Home／End／PgUp／PgDn、常用符號與貼上。Ctrl 與 Alt 是黏著的：按一下 Ctrl 再按 C，就是 Ctrl+C。
  - 上下拖曳看捲動紀錄。接實體鍵盤也能用，方向鍵、Esc 與 Ctrl 組合都會送出。
- **兩者共同**：多行貼上會先問「每一行都會當成指令執行」；shell 結束但連線還在時，可以「開一個新的 shell」。

## 留下什麼紀錄

- **主機的連線紀錄**（導覽列「連線紀錄」）：終端機連線一列，寫「終端機：開了 N 個 shell，身分 X」；
  一個都沒開則寫「終端機連線；沒有開任何 shell」。**不記輸入與輸出。**
- **帳號**：主機連結了帳號且開著上傳時，同一列（包括 shell 數與身分）會同步上去，保留 90 天。
- **主機記錄檔**：`Terminal 1 opened as alice (/bin/zsh)`、`Terminal 1 ended (the shell exited, exit code 7)`。
- **Linux daemon**：`journalctl -u sunllo-deskpair` 記 root 與登入使用者 shell 的開始與結束；正在跑的看
  `systemctl list-units 'deskpair-shell-*'`，也可以用 `systemctl stop` 手動結束。
- **不會出現在的地方**：Linux 沒有走 PAM，所以不在 `who`／`last`；Windows 沒有登入動作（沿用引擎或使用者既有的 token），
  所以不產生新的登入事件。稽核靠上面這幾項。

## 什麼時候結束

- 控制端關閉視窗或畫面：shell 被結束。
- **連線斷了：shell 跟著結束。** 沒有 detach／reattach——那會留下一個沒有任何已核准連線看管的 root shell。
  控制端寫「連線中斷了，shell 也隨之結束」。
- 擁有者在連線管理員取消勾選：**所有 shell 立刻被殺**，連線保留。控制端看到「shell 已結束：terminal permission was withdrawn」，
  之後要開新的會被拒絕。在途的按鍵被丟掉，不算違規，不會斷線。
- 怎麼殺：
  - Windows：shell 與它啟動的一切在同一個 Job object 裡，一起結束，引擎當掉也一樣。
  - Linux 與 macOS：先送 SIGHUP，兩秒後把同一個 session 剩下的行程全部 SIGKILL。自己 `setsid` 出去的行程會逃掉，與 ssh 登出相同。
  - Linux daemon 開的 shell：停掉整個 systemd unit 的 cgroup，連 `setsid` 出去的也一起。引擎當掉時，daemon 停掉它開的所有 unit。

## 限制

- 桌面與手機一條連線同時只開一個 shell（結束後可以再開）；主機端的上限是每條連線 4 個，擋的是自訂的客戶端。
  Linux daemon 每個引擎同時最多 8 個 root／使用者 shell，每分鐘最多開 20 個。
- 改變視窗大小不重排：已經印出來的行維持原本的寬度。
- 不支援程式的滑鼠回報（例如 vim 的 `set mouse=a`）。
- 手機上還不能選取、複製畫面上的文字。
- shell 開好之前打的字會被丟掉。
- Windows 需要 10 1809 以上（ConPTY）。
- 慢的連線不會撐爆主機記憶體：每個 shell 未確認的輸出最多 256 KiB，超過就讓 shell 的寫入停下來等。
  代價是 `cat` 一個大檔在慢線路上會慢，而不是被截斷。

## 風險，以及能做什麼

**最大的風險是永久密碼。** 永久密碼刻意不會因為猜錯而輪替（輪掉之後，沒人在場的機器就再也連不上），
所以在無人值守的主機上開了終端機，**一次外洩的永久密碼就是一個永久的 SYSTEM／root shell**，給任何連得到這台機器的人。
每個來源猜錯仍有 2^n 秒的退避（上限 30 秒），但那擋的是猜，不是外洩。

依代價由低到高：

1. 用不到就別開。它預設關著，關回去也只要一個按鈕。
2. 選「目前登入的使用者」而不是最高身分。Linux 上也可以把 `terminal-root` 改成 `no`，拿到的就是 `deskpair` 帳號
   （需要時自己把它放進 sudoers，sudo 自己的政策與紀錄就會生效）。
3. 設定 › 安全的「誰可以連線」：勾「只接受清單上的位址連線」與「拒絕透過中繼伺服器的連線」，讓只有已知的位址連得進來。
4. 永久密碼要長，而且不要和別處共用。
5. 定期看連線紀錄裡的終端機列。

其他設計上的防線（都有測試釘住，見 hardening.md）：

- 終端機訊息只在「終端機連線」且持有權限時才被接受；遠端桌面連線即使有這個權限也開不了 shell。違規就關閉連線。
- 核准時沒有明確點名終端機，就不給終端機——舊版的連線管理員或空的核准清單都不會意外放行。
- **本機的 IPC 沒有「開終端機」的指令。** 本機任何程式都無法透過 IPC 請 SYSTEM 引擎開 shell，唯一的路是已授權的遠端連線。
- 送到 Linux root daemon 的請求只有數字；daemon 交給 `systemd-run` 的參數只有常數、數字與 passwd 查到的值，沒有一個位元組來自連線者。

## 疑難排解

| 看到的 | 原因 | 怎麼辦 |
|---|---|---|
| 「這台電腦不允許終端機…」 | 主機沒開終端機、核准時沒勾，或主機是沒有終端機的舊版 | 在主機的設定 › 安全開啟；核准時勾選；更新主機 |
| 「無法開啟 shell：Nobody is signed in…」 | 選了「目前登入的使用者」，但主機上沒有人登入 | 等人登入，或改成最高身分 |
| Linux 上身分顯示 `deskpair` | daemon 的閘門不是 `yes` | `/etc/deskpair/daemon.conf` 寫 `terminal-root = yes`；每次開 shell 都重讀，不必重啟 |
| 「無法開啟 shell：This version of Windows has no pseudo console…」 | Windows 10 1809 以前 | 更新 Windows |
| Linux journal 有 `Refused a terminal: N running, M opened in the last minute` | daemon 的上限（8 個、每分鐘 20 個） | 結束用不到的 shell，或稍等 |
| 按「開啟終端機…」後出現「請先設定固定密碼」 | 已安裝無人值守存取，終端機在沒人在場時也連得到 | 先設定固定密碼 |

## 在實驗機上測

`tools/DeskPair.Tools.PeerCli` 可以當終端機主機，也可以當控制端，不必動到已安裝的產品。
它的 shell 以執行 PeerCli 的帳號執行，所以測不到 root 路徑；root 要在裝了 daemon 的機器上測。

```sh
peercli host --server HOST:21116 --key KEY --data ./lab --password … --approve password \
  --terminal [--terminal-as highest|user] --direct-port 21119
peercli connect TARGET --server HOST:21116 --key KEY --password … --terminal
```

連線後從標準輸入送腳本行：`:term 100x30`（開一個，印出 id）、`:tsend 1 echo hi\n`、`:expect 1 hi`（10 秒內輸出要符合）、
`:tsig 1 int`、`:tresize 1 120x40`、`:dump 1`、`:tclose 1`。

手機：iOS 模擬器啟動時設 `SIMCTL_CHILD_SUNLLO_TEST_TERMINAL`（`1`，或 shell 開好後要打的指令）；
Android 用 `adb shell am start -n com.sunllo.deskpair/.android.MainActivity -e host … -e password … -e terminal 1`，
再用 `adb shell input text` 打字。
