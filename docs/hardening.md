# 安全回歸清單

每次發版前對照一次。每一項寫的是「怎麼驗」，不是「做了什麼」；做了什麼在 git 記錄裡。
能用 log 或一個數字判定的，優先於截圖。

## 用戶端（桌面 App）

| 項目 | 怎麼驗 |
|---|---|
| 以 ID 連線時沒有伺服器公鑰就拒絕，不退到「接受任何身分」 | `PeerConnectorVerifierTests`；實機：清掉 `ServerPublicKeyBase64`、擋掉 21114 後以 ID 連線，狀態列要出現「public key is not configured」而不是連上 |
| 從 `/key` 取回的公鑰會顯示指紋並釘住 | 設定 › 網路 › 從伺服器取得：通知帶 16 位指紋；與伺服器啟動記錄 `Server public key: … (fingerprint …)` 前 16 位相同 |
| 臨時密碼不寫進 log | `grep -i "temp-password" server.log` 為零；`grep -rn "{Password}" src` 為零 |
| 密碼猜測退避 2^n 秒（上限 30 s） | `LoginFailureTrackerTests`；實機：同一來源連錯 5 次，第 5 次錯誤訊息帶 `retry_after_ms` ≥ 16000 |
| 單一來源猜錯不會讓臨時密碼換掉 | `HostSettingsTests.One_address_guessing_wrong_does_not_move_the_password…`；實機：同一台猜錯 10 次，首頁的臨時密碼不變 |
| Windows `secrets/` 只有 SYSTEM、Administrators 與本帳號 | `icacls C:\ProgramData\Sunllo\DeskPair\secrets` 沒有 `BUILTIN\Users`；ACL 設不上時 `server.log` 有 `Could not restrict the secrets directory` |
| Linux 引擎的 IPC token 不在命令列 | `tr '\0' ' ' < /proc/<engine pid>/cmdline` 不含 `--ipc-token`；`stat -c '%U %a' /proc/<pid>/environ` 是 `root 400`（引擎 setuid 後不可 dump，一般帳號 `cat` 會被拒；`environ` 是 exec 當下的快照，root 仍看得到變數，這是 `/proc` 的行為，不是外洩）；`SessionEngineLauncherTests`。2026-09-23 在 Linux 測試機驗過 |
| 密碼欄只收 ASCII | `PasswordTextTests`（桌面）、`PasswordTextTest`（手機） |

## 遠端終端機

行為與風險見 [terminal.md](terminal.md)。測試名稱沒寫類別的，在 `tests/DeskPair.Integration.Tests/TerminalSessionTests.cs`。

| 項目 | 怎麼驗 |
|---|---|
| 預設關閉；沒有這個設定的舊設定檔讀成關閉 | `HostSettingsTests.The_terminal_is_off_until_the_owner_turns_it_on`；實機：全新安裝的主機以 `peercli connect … --terminal` 登入，`granted:` 那一行沒有 `PermTerminal` |
| 開啟要按一次確認；已裝無人值守而沒有固定密碼時開不起來 | `TerminalUiTests.Turning_the_terminal_on_takes_a_confirmation`、`TerminalUiTests.An_unattended_machine_needs_a_permanent_password_first` |
| 控制端不能要求這個權限 | `A_viewer_cannot_ask_for_the_terminal_permission_in_its_options` |
| 只有終端機連線、且持有權限，才開得了 shell；違規就斷線，而且不會先開出 shell | `Without_the_permission_a_terminal_action_ends_the_session_and_no_shell_starts`、`A_remote_control_session_cannot_open_a_terminal_even_with_the_permission`、`A_terminal_response_from_the_viewer_is_a_violation` |
| 核准沒有點名終端機就不給；連線管理員沒勾就按不下接受 | `IpcApprovalTests.A_terminal_is_granted_only_when_the_acceptance_names_it`、`TerminalUiTests.A_terminal_request_cannot_be_accepted_until_its_box_is_ticked`、`TerminalUiTests.A_desktop_request_never_grants_a_terminal` |
| 撤權殺掉所有 shell，連線保留，晚到的按鍵不算違規 | `Withdrawing_the_permission_kills_every_shell`；實機：在 shell 裡跑 `sleep 600`，連線管理員取消勾選後主機上 `pgrep -x sleep` 為空（Windows 用 `ping -t` 與 `Get-Process ping`） |
| 連線斷了，shell 跟著結束 | `A_dropped_session_takes_its_shells_with_it`；實機：在 shell 裡跑 `sleep 600`，`kill` 控制端的 PeerCli 後主機上 `pgrep -x sleep` 為空 |
| 本機 IPC 開不了 shell | `TerminalIpcTests.No_ipc_message_is_a_terminal_command`、`TerminalIpcTests.The_ipc_bridge_holds_no_way_to_start_a_shell` |
| Linux 的 root 閘門在 `/etc`、引擎寫不到、只認明確的 `yes`；關閉時什麼都不開 | `DaemonTerminalTests.The_gate_is_a_file_the_engine_cannot_write`、`DaemonTerminalTests.The_gate_opens_only_on_an_explicit_yes`、`DaemonTerminalTests.With_the_gate_closed_the_answer_is_a_refusal_and_nothing_starts`；實機：`stat -c '%U %a' /etc/deskpair/daemon.conf` 為 `root 644`；改成 `no` 後開 shell，`id -un` 為 `deskpair` |
| 送進 root daemon 與 `systemd-run` 的只有數字、常數與 passwd 的值 | `DaemonTerminalTests.A_shell_request_round_trips_and_carries_only_numbers`、`DaemonTerminalTests.A_malformed_shell_request_is_not_read`、`DaemonTerminalTests.Systemd_is_handed_constants_numbers_and_passwd_values_only` |
| Linux daemon 的 shell 不會比連線或引擎活得久 | 實機：root shell 裡 `setsid -f sleep 600`，關閉後 `systemctl list-units 'deskpair-shell-*'` 為空、`pgrep -x sleep` 為空；`kill -9` 引擎後同樣為空。2026-09-25 在 Linux 測試機驗過 |
| Windows 的 shell 與它啟動的一切一起結束 | `WindowsTerminalTests.Disposing_ends_the_shell_and_its_children`；實機：shell 裡 `Start-Process ping -ArgumentList '-t','127.0.0.1'`，關閉視窗後 `Get-Process ping` 為空 |
| Windows 服務下的身分 | `WindowsTerminalTests.The_identity_follows_the_request_and_the_engine`；實機：服務模式開最高身分，`whoami` 為 `nt authority\system`；改成登入使用者且沒人登入時被拒絕，**不會**退回 SYSTEM。**尚未在服務下實測** |
| 每個 shell 都記下來 | `A_terminal_connection_opens_a_shell_types_into_it_and_the_record_says_who_it_ran_as`；實機：連線紀錄頁有「終端機：開了 N 個 shell，身分 X」 |

## 伺服器

| 項目 | 怎麼驗 |
|---|---|
| admin API 沒有金鑰就整個 401（只有 Development 例外） | `AdminApiTests`；部署後 `curl -s -o /dev/null -w '%{http_code}' http://HOST:21114/api/stats` 是 401，帶對的 Bearer 是 200 |
| 範例設定的 `CHANGE-ME` 不是金鑰 | `AdminApiTests.The_placeholder…`；啟動記錄沒有 `Admin:ApiKey is not set` 的 error |
| relay 的 admin 埠只綁 loopback | `ss -ltnp | grep 21124` 顯示 `127.0.0.1:21124`；從外部 `curl http://HOST:21124/healthz` 連不上 |
| 21114 依部署方式開關：位址與公鑰由目錄發給用戶端的部署**對外關閉**，使用者自己輸入位址的自架伺服器**對外開放** `/key` 供使用者手動取鑰 | 關閉時：從外部 `curl -m 5 http://HOST:21114/key` 連不上；伺服器上 `ss -ltnp \| grep 21114` 只有 `127.0.0.1:21114`（`Admin:BindAddress`，防火牆規則消失也不會對外）；目錄回的公鑰等於伺服器上 `curl http://127.0.0.1:21114/key` 的結果。開放時：從外部 `curl http://HOST:21114/key` 回 base64 |
| relay 只收 rendezvous 簽的票券（協定 2） | `RelayAdmissionTests`；部署後 relay 的 `appsettings.json` 有 `RendezvousPublicKey` 且等於 `/key`；`journalctl -u deskpair-relay` 啟動時沒有 `RendezvousPublicKey is not set`；用 0.2.x 的 PeerCli `--force-relay` 連線會被拒（journal 出現 `no ticket`） |
| 協定版本協商（協定 2） | `ProtocolTwoTests`；舊版 0.2.x 桌面連新主機看到「Unsupported protocol version 2」，新版連舊主機看到「Update DeskPair on the computer you are connecting to」 |
| 永久密碼以 PBKDF2 儲存 | `ProtocolTwoTests.Pbkdf2_*`；主機 secrets 目錄多出 `password-permanent-kdf`；重新設定一次永久密碼後，`AuthChallenge.kdf` 為 1 |
| `server.key` 不會被靜默重生 | `deploy/install.sh rendezvous` 在沒有鑰時退出並印出說明（`DESKPAIR_NEW_KEY=yes` 才會建新的） |

## 金鑰與工作樹

| 項目 | 怎麼驗 |
|---|---|
| 工作樹裡沒有私鑰 | `git ls-files | grep -Ei '\.(key|p8)$'` 為空；`ls *.key *.p8` 為空（金鑰放在工作樹之外） |
| 發行簽章 | `release.json.sig` 由離線保存的發行金鑰簽；App 內建公鑰指紋 `D0721541…`；未簽署的清單只通知、不安裝（`SignedReleaseTests`） |
