# DeskPair 虛擬顯示器驅動（Windows）

控制端可以要求被控端新增一台它沒有的螢幕；「符合視窗解析度」也要一台可以即時變成視窗大小的螢幕。在 Windows 上，這些螢幕由這支
IddCx（Indirect Display）使用者模式驅動提供。它取代之前原封不動散布的 VDD（MttVDD，見 `docs/architecture.md` §6.4c 的沿革）。

原始碼是自己依 IddCx 的 API 文件寫的，不是從微軟的 IndirectDisplay 範例（MS-PL）改來的，授權與專案其他部分相同。

## 為什麼自己寫

- **每台螢幕各自來去**：一台螢幕一個「槽」，有固定的身分（connector index 與 container ID），接上、拔掉都只動它自己。
  MttVDD 每次增減都停用整個裝置、重寫設定檔、再啟用，所有虛擬螢幕重接一次而且換名字。
- **每台螢幕有自己的尺寸清單**：最多 199 個，由要這台螢幕的觀看端決定，之後可以即時換成清單裡任何一個，不必重接。
  MttVDD 只在裝置啟動時讀一次設定檔，最多 100 個尺寸，所有螢幕共用。
- **控制通道只有 SYSTEM 與 Administrators 打得開**：裝置介面，INF 設 `D:P(A;;GA;;;SY)(A;;GA;;;BA)` 與 `FILE_DEVICE_SECURE_OPEN`。
  MttVDD 的 pipe 任何本機使用者都能寫，它還會讀一個設定檔。這支驅動不讀任何檔案、不寫記錄。
- **看門狗**：引擎餵它，引擎不在了（被結束、當掉）它就在時限到時拔掉所有螢幕。

## Windows 對 IDD 螢幕的規矩（2026-09-29 在 Windows 10 22H2 實測）

- 一台螢幕的「螢幕模式」與「目標模式」**合計最多 200 個**。多一個，`IddCxMonitorArrival` 就回 `STATUS_NOT_SUPPORTED`，有沒有 EDID 都一樣。
- 能用的尺寸是兩份清單的**交集**。螢幕模式在接上時就固定；目標模式隨時可以用 `IddCxMonitorUpdateModes` 換。
- 在目前的尺寸**旁邊加上**新尺寸、再用 `ChangeDisplaySettingsEx` 切過去：第一次可以，之後 Windows 一直回 `DISP_CHANGE_BADMODE`。
- 把目標模式換成**只有**新尺寸：目前的尺寸不再提供，Windows 自己把螢幕換過去。
  - 199 個隨機尺寸之間換 300 次，全部成功：中位數 14–56 ms（依量法），最慢 77 ms。
  - 接上後立刻就能換，不必等。
  - 不動任何視窗，GDI 名稱（`\\.\DISPLAYn`）不變，顯示設定資料庫（`GraphicsDrivers\Configuration`／`Connectivity`／`ScaleFactors`）不增長。
- 所以每台螢幕接上時帶**最多 199 個尺寸、一個目標**（它現在的尺寸），換尺寸＝把另一個尺寸變成那一個目標。
- IDD 不支援縮放的來源解析度（桌面比訊號小、再放大）：`SetDisplayConfig` 驗證就失敗。
- 在「只留虛擬螢幕」的配置下換尺寸，Windows 會重新套用它為**這組螢幕**記住的配置（通常是延伸），實體螢幕就亮回來。
  那組配置若是以 `SDC_SAVE_TO_DATABASE` 存的「只留虛擬螢幕」，就維持不變。拔掉虛擬螢幕後，Windows 套用「只有實體螢幕」那組的配置，
  實體螢幕回到原本的樣子；同一台虛擬螢幕再接上，又立刻只留它。

## 控制協定（`src/Control.h`）

引擎用 `CM_Get_Device_Interface_List` 找到介面 `{27F43708-94D2-4E46-8F95-22C373861DF4}`，開啟後以 `DeviceIoControl` 下指令。
每個請求的第一個欄位是協定版本（目前 1），不符回 `ERROR_REVISION_MISMATCH`；格式或數值不對回 `ERROR_INVALID_PARAMETER`。

| IOCTL | 輸入 | 輸出 | 作用 |
|---|---|---|---|
| `INFO` | — | 版本、槽數、每台最多幾個尺寸、哪些槽有螢幕、看門狗時限 | 查詢 |
| `PLUG` | 槽、起始尺寸的索引、尺寸清單（每個尺寸一次，320–8192 px、24–240 Hz） | adapter LUID、target ID | 接上一台螢幕 |
| `UNPLUG` | 槽 | — | 拔掉；沒有螢幕回 `ERROR_NOT_FOUND` |
| `SELECT` | 槽、尺寸的索引 | — | 換成清單裡的另一個尺寸 |
| `WATCHDOG` | 時限（0＝關閉，或 1–600 秒） | — | 設定並餵看門狗 |

- 共 4 個槽，槽 n 的 container ID 是 `{28405050-3667-4E45-9708-BDD87092D5nn}`，connector index 是 n，所以 Windows 記得每一台放在哪裡。
- `PLUG` 回的 adapter LUID 與 target ID 在 `QueryDisplayConfig` 裡找得到這台螢幕，由此得到它的 GDI 名稱。target ID 是 256＋槽。
- 接上、拔掉、換尺寸一次只做一件（看門狗的拔除也一樣），所以不會有兩件事同時動到同一台螢幕。

## 建置

```
powershell -File tools/build-idd.ps1                  # x64 與 arm64
powershell -File tools/build-idd.ps1 -Architectures x64 -Version 0.4.8.1
powershell -File tools/build-idd.ps1 -CodeSigning <metadata.json>
```

- 不需要安裝 WDK：標頭、兩個 stub 程式庫與 Inf2Cat 取自 WDK 的 NuGet 套件（版本與 SHA-256 釘在腳本裡，快取在 `artifacts/cache/wdk`）。
  編譯器是 Visual Studio 的（要有「使用 C++ 的桌面開發」）。
- UMDF 2.25、IddCx 1.4：Windows 10 1809 以上可以執行。x64 與 ARM64；IddCx 沒有 32 位元版本，win-x86 的 DeskPair 沒有這支驅動。
- 輸出在 `native/idd/out/<rid>/`：`DeskPairDisplay.dll`、`DeskPairDisplay.inf`、`deskpairdisplay.cat`，`tools/publish.ps1` 把它複製到發佈目錄的 `idd\`。
- 帶 `-CodeSigning` 時以 Artifact Signing 先簽 DLL、再產生並簽 catalog。沒簽的建置可以看，Windows 不會安裝。

## 安裝與移除

見 `docs/unattended-windows.md` H。
