# Virtual Display Driver（Windows 虛擬顯示器）

控制端可以要求被控端「新增一台不存在的螢幕」。在 Windows 上，這台螢幕由一支 IddCx（Indirect Display）驅動提供：
[VDD](https://github.com/VirtualDrivers/Virtual-Display-Driver)（MIT）。

## 散布的是什麼

**原封不動的官方發行版**。驅動由 VDD 專案自己建置，由 SignPath Foundation 以公開信任的 Authenticode 憑證簽 catalog。
這裡不建置、不重簽。

- `tools/fetch-vdd.ps1` 下載釘死版本的官方壓縮檔（網址與 SHA-256 都寫在腳本裡），放進 `native/vdd/<rid>/`：
  `MttVDD.inf`、`MttVDD.dll`、`mttvdd.cat`，以及同一版原始碼 tag 的 `LICENSE`。
- 這個資料夾**不進 git**（`.gitignore`）。原因只是「它是下載來的」，不是授權問題：MIT 允許散布，而且簽章是對方的。
- `tools/publish.ps1` 看到這個資料夾，就把它複製到發佈目錄的 `vdd\`。沒有它的 build 仍然可以用，只是不能新增螢幕，
  主機會如實告訴控制端。

目前釘的是 release 25.7.23 的「Driver.Only」壓縮檔。裡面的驅動其實是 2024-12-24 簽的那一版
（`DriverVer 12/24/2024,11.30.4.434`），原始碼對應 repo 的 `24.12.24` tag。x64 的檔名上游寫成 `x86`。

## 為什麼不用它自己的 pipe

驅動提供 `\\.\pipe\MTTVirtualDisplayPipe`，支援 `SETDISPLAYCOUNT`、`RELOAD_DRIVER` 等指令。我們**完全不用**，原因如下：

- 驅動只在裝置加入時（DeviceAdd）讀一次 `vdd_settings.xml`。`RELOAD_DRIVER` 會重新初始化 adapter，但不會重讀設定檔。
- 這支 pipe 的 ACL 是 `D:(A;;GA;;;WD)`：任何本機使用者都能改螢幕數量、重新載入驅動。
  只要裝置在執行，這一點就改不掉。影響範圍僅限虛擬螢幕本身，文件裡要寫清楚。

DeskPair 的做法和它自己的工具（nefcon／devcon）一樣：安裝時建一個根列舉（root-enumerated）裝置，硬體 ID 為 `MttVDD`，
裝好驅動後就停用（連重開機後都維持停用）。要新增或移除螢幕時：停用裝置 → 寫設定檔（螢幕數、提供的尺寸）→ 啟用。
每次啟用，所有虛擬螢幕都會重新接上一次，而且名字會變（`\\.\DISPLAY10` 回來變成 `\\.\DISPLAY11`）。
所以移除拿掉的是最後一台，而已經接上的螢幕不會再教新尺寸；新增時要求的尺寸會先寫進設定檔。
引擎當掉時裝置會維持啟用，下一個引擎啟動時會先把它停用。

**為什麼不用 `SwDeviceCreate`**（生命週期可以綁在 handle 上，本來比較乾淨）：它對 LocalSystem 一律回
`0x8007007E`（ERROR_MOD_NOT_FOUND），在工作階段 0 或使用者的工作階段都一樣，而且任何裝置都一樣，連不需要驅動的測試裝置也是。
這是 2026-09-25 在 Windows 11 25H2 上以服務的終端機實測的結果。服務的引擎就是 LocalSystem。

## 安裝與移除

兩者都需要系統管理員權限：

```
DeskPair --install-virtual-display
DeskPair --remove-virtual-display
```

- **設定檔目錄**：`%ProgramData%\Sunllo\DeskPair\vdd`。只有 SYSTEM 與 Administrators 可寫，其他人（包括以 LocalService 執行的驅動）只能讀；設定頁從這裡讀安裝紀錄。
  驅動預設讀的是 `C:\VirtualDisplayDriver`，那是任何使用者都能建立的資料夾，所以改用這個目錄，
  由 `HKLM\SOFTWARE\MikeTheTech\VirtualDisplayDriver\VDDPATH` 指過去。
  如果這個值已經指向別處（機器上已經有別人裝的 VDD），安裝會拒絕，不會接管。
- **發行者信任**：不是微軟簽的驅動包，只有在發行者列在 Trusted Publishers 時，Windows 才會不問就裝；
  沒有人能回答時（服務、腳本），安裝就會失敗。所以安裝期間把 catalog 的簽章憑證暫時放進
  `LocalMachine\TrustedPublisher`，裝完立刻移除。常駐的話，等於讓這台機器默默接受 SignPath 替任何人簽的驅動。
  實測：信任移除之後，從 driver store 把驅動裝到裝置上不會再詢問。
- **驅動包**：用 `SetupCopyOEMInf` 放進 driver store，記下它在那裡的名字（`oemNN.inf`），移除時用 `SetupUninstallOEMInf` 拿掉。
- **裝置**：SetupAPI 建立（`ROOT\DISPLAY\000N`）、`UpdateDriverForPlugAndPlayDevices` 從 driver store 安裝、`CM_Disable_DevNode(PERSIST)` 停用。
  裝驅動時裝置一定會啟動（先標停用再註冊沒有用），所以安裝時會閃出一台螢幕約一秒。instance ID 記在安裝紀錄裡。
- **移除時一併清掉**：裝置（`DIF_REMOVE`，不留停用項目或幽靈裝置）、設定檔目錄、`VDDPATH`。

## 限制

- 只有以系統管理員身分執行的引擎能建立裝置，也就是無人值守服務。一般使用者模式的 App 會回報「不可用」並說明原因。
- Windows 10 2004 以上。S 模式不能裝第三方驅動。ARM64 在 24H2 上游說明可能需要測試模式，尚未實測。
- 桌面拓樸若是「僅第一個畫面」，Windows 不會把新螢幕接上桌面，主機等不到它出現。
