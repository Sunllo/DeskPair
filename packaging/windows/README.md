# Windows 安裝程式（MSI）

每個 Windows 架構（x64、ARM64、x86）除了免安裝的 `.zip`，另有一個 Windows Installer 套件
`DeskPair-<version>-win-<arch>.msi`。兩者裝的是同一份程式：MSI 的內容就是 zip 裡那個資料夾，
再加上兩個檔案——`packaged`（內容 `msi`）與 `LICENSE.txt`。

## 裝了什麼

- 程式放在 `C:\Program Files\Sunllo\DeskPair`（x86 版在 `Program Files (x86)`）。**只有全機安裝**：
  無人值守服務以 LocalSystem 執行這個資料夾裡的 `DeskPair.exe`，所以它必須放在只有系統管理員能寫入的地方。
  zip 版解壓到「下載」之類的資料夾再開服務，等於讓任何以那個使用者身分執行的程式都能換掉 SYSTEM 會執行的檔案；
  要無人值守，安裝版才是對的選擇。
- 「開始」功能表與桌面的捷徑（`msiexec ... DESKTOP_SHORTCUT=0` 不建桌面捷徑），以及「設定 › 應用程式」裡的項目。
- 精靈只有歡迎、準備安裝、進度、完成四頁，完成頁預設勾選「開啟 DeskPair」。**沒有授權同意頁**：
  AGPL 第 9 節明說使用者不必接受授權就能執行程式，所以安裝程式也不要求。
- **精靈跟著 Windows 的顯示語言**（0.4.7 起）：與 App 同樣十種語言，其他語言顯示英文。見下一節。
- `packaged` 告訴 App 這份是 Windows Installer 裝的（`PackagedInstall`）。App 內更新因此改走下一版 MSI，
  而不是把檔案複製到 Windows Installer 擁有的資料夾裡。

## 一個 MSI、十種語言

Windows Installer 沒有「一個套件內含多種語言」的格式，常見做法（本專案也是）是**內嵌的語言轉換**：

1. `DeskPair.wixproj` 的 `Cultures` 列出十種文化（英文在第一個），WiX 為每一種各建一份 MSI，放在輸出資料夾的
   `<culture>\` 底下。精靈的文字 WiX 自己就有這十種的翻譯；我們自己的幾句（完成頁的「開啟 DeskPair」、已安裝較新版時的
   訊息）在 `Localization/<culture>.wxl`，`InstallerLocalizationTests` 檢查每種文化都有、而且沒有留英文。
2. `tools/package.ps1` 的 `Add-LanguageTransforms` 以英文那份為本體，把其他九份各做成一個轉換（`.mst`，二三十 KB，
   只有文字、字型、字碼頁與 `ProductLanguage` 不同），以語言代碼為名存進本體的子儲存區（`1028`、`2052`…），
   再把摘要資訊的 Template 改成 `x64;1033,1028,2052,…`。最後才簽章，簽章涵蓋這些轉換。
3. 使用者開啟 MSI 時，Windows Installer 依 Windows 的顯示語言**自動套用**同名的轉換。清單裡沒有那個語言時，
   它會改用主要語言相同的（墨西哥西文用西文、葡萄牙葡文用巴西葡文，與 App 一致）；連主要語言都沒有就用第一個，英文。
   這個行為 Windows Installer 的官方文件沒有寫，但很多安裝程式都靠它。2026-09-29 在繁中 Windows 上實測：
   直接開啟 MSI，歡迎頁是「歡迎使用 DeskPair 安裝精靈」、按鈕是「下一步」「取消」。
4. 中文例外：繁中與簡中的主要語言相同，缺了確切的語言時會任選一個（實測：沒有 1028 的套件在繁中 Windows 上用了簡中）。
   所以其他華語地區直接列名，對照 App 的規則（`AppLanguages.Match`）：香港 3076、澳門 5124 用繁中的轉換，新加坡 4100 用簡中的。

用 `TRANSFORMS=:<語言代碼>` 強制某個語言可以，但不要指定跟 Windows 顯示語言相同的那個：它已經自動套用過一次，
再套一次會出現「使用 transforms 時發生錯誤」。

兩個讓轉換只改語言的細節：

- `Package` 不寫 `Language`：每種文化的語言與字碼頁由 WiX 的翻譯決定（繁中 1028／950、日文 1041／932…），
  寫死 1033 會讓每一份都自稱英文。
- **ProductCode 由專案產生**（每次建置一個新的，十種文化共用）：交給 WiX 的話每種文化各產生一個，繁中的轉換就會把
  安裝的產品換成另一個產品。

字型基本上用 WiX 各語言的設定（`Advanced_Font_*`）：西文與俄文是 Tahoma，繁中也是 Tahoma（9 點，中文字由 Windows 的
字型連結補上，顯示出來是正黑體）。三個語言在 `.wxl` 裡改掉：日文 WiX 給的字型名稱超過 TextStyle 欄位的 32 字元（ICE03）；
韓文的돋움與簡中的宋体是舊字型，돋움在非韓文的 Windows 上根本沒有（實測顯示成襯線的替代字）。改成各語言在現在的 Windows 上
的介面字型：日文 `Yu Gothic UI`、韓文 `Malgun Gothic`、簡中 `Microsoft YaHei UI`，任何語言的 Windows 10／11 都有裝。

## 升級與移除

- **升級**：每一版都有同一個 UpgradeCode（`Package.wxs`，永遠不能改），新版先把舊版整個移除再裝（`MajorUpgrade`
  的預設排程），所以不必依賴檔案版本規則。服務若已註冊，會在換檔案前停止、裝完後再啟動；服務的註冊本身保留。
- **執行中的 App**：Windows Installer 透過 Restart Manager 請它結束，App 把這當成真正的結束（不縮到系統匣）；
  裝完後 Restart Manager 會以 `--minimised` 把它重新開回系統匣（`RestartAfterUpdate`，App 啟動時向 Windows 登記），
  所以遠端大量升級後電腦仍連得到。當機、無回應與重開機不在此列，重開機後照「登入時啟動」的設定。
  2026-09-28 實測：App 開著時升級與移除都不留下要重開機才能刪的檔案。
- **App 內更新**：安裝版會下載清單裡同架構的 `.msi`，跟其他下載一樣先驗簽章清單與 SHA-256，然後由 `update.cmd`
  等 App 結束、執行 `msiexec /i <msi> /passive /norestart`（Windows 會要求系統管理員同意），最後重新開啟 App。
  使用者拒絕同意時，舊版照樣重新開啟。msiexec 的紀錄留在 `%TEMP%\deskpair-update\install.log`。
- **移除**：刪檔案之前，以 LocalSystem 執行 `DeskPair.exe --remove-system-changes`，拆掉 App 在自己資料夾以外做的事：
  - 無人值守服務，以及它打開的「允許軟體送出 Ctrl+Alt+Del」原則
  - App 自己建的防火牆規則（`Sunllo DeskPair`）
  - **Windows 自己建的防火牆規則**（0.4.6 起）：程式第一次監聽時，使用者在 Windows 的「允許存取」提示按下允許，
    Windows 會以程式路徑建兩條輸入規則（私人與公用網路），名稱是「DeskPair」。依路徑刪，其他位置的 DeskPair 不受影響。
    用 PowerShell 的防火牆模組而不用 netsh：Windows 存的路徑是小寫，模組的比對不分大小寫（實測過），netsh 沒有文件說明。
  - **「登入時啟動」的項目**（0.4.6 起）：每個已登入使用者的 `HKCU\...\Run` 裡啟動這個路徑的項目。沒有登入的使用者不動
    （他們的登錄檔是磁碟上的檔案，移除程式不該去開）。
  - 虛擬顯示器驅動與它的設定資料夾

  這一步不開任何視窗（安裝程式底下沒有人能回答，工作階段 0 的對話框只會讓移除卡住），拆不掉的寫進
  `%ProgramData%\Sunllo\DeskPair\logs\service.log`，不讓整個移除失敗。
  **升級時不跑這一步**（條件是 `REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE`）。
- **UAC 顯示的名稱**：移除時 Windows 執行的是它自己留在 `C:\Windows\Installer` 的安裝檔複本，檔名是隨機的（例如
  `1a2b3c4d.msi`）。簽章帶了描述，UAC 才會顯示名稱，否則顯示檔名。所以 `package.ps1` 簽 MSI 時加 `/d DeskPair`（0.4.6 起）。
- **資料刻意保留**（使用者 2026-09-29 決定），重新安裝後還是同一台電腦、同一個 ID 與永久密碼：
  - `%ProgramData%\Sunllo\DeskPair`：本機 ID 與身分金鑰、永久密碼（雜湊）、主機設定、連線紀錄、服務記錄檔
  - `%AppData%\Sunllo\DeskPair`：每個使用者自己的設定與裝置清單
  - `%LocalAppData%\Sunllo\DeskPair`：每個使用者的 App 記錄檔

  要完全清除，移除之後再刪這三個資料夾（第一個要系統管理員；另外兩個每個用過的使用者各有一份）。
  重新安裝後會拿到新的 ID。

## 大量部署

```
msiexec /i DeskPair-<version>-win-x64.msi /qn
msiexec /x DeskPair-<version>-win-x64.msi /qn
```

## 建置

`tools/package.ps1` 在發版時自動建置。單獨建置一個：

```
dotnet build packaging/windows/DeskPair.wixproj -c Release -p:Platform=x64 -p:PayloadDir=<publish.ps1 的輸出資料夾>
```

- 這樣建出來的是十份各自一種語言的 MSI（每種文化一個資料夾，約五分鐘：每種語言都重新壓縮一次內容），
  不是發行用的那一份；合成一個要經過 `package.ps1`。只看一種語言時加 `-p:Cultures=zh-TW`。
- 只能在 Windows 上建置（MSI 由 Windows Installer 的 API 產生），所以這個專案不在 `DeskPair.slnx` 裡。
- **WiX v7 的授權**：WiX 原始碼是 MS-RL，但官方的二進位發行版附帶 Open Source Maintenance Fee EULA：
  在營利活動中使用、而且年總營收達 US$10,000 的使用者要按月付維護費（贊助 wixtoolset）。每台建置機要先接受一次：
  `dotnet build packaging/windows/DeskPair.wixproj -t:AcceptEula -p:EulaId=wix7`（紀錄在 `%USERPROFILE%\.wix\`）。
  這是人的決定，所以專案檔不代為接受；`package.ps1` 在還沒接受的機器上會先停下來說明。EULA 全文在
  `%USERPROFILE%\.nuget\packages\wixtoolset.sdk\7.0.0\OSMFEULA.txt`。
- `SuppressIces` 關掉 ICE38、ICE43、ICE57：這三項假設「開始」功能表和桌面可能是個人資料夾，全機安裝的套件不會是，
  捷徑的 KeyPath 也應該在 HKLM。其餘 ICE 都照常驗證，警告視為錯誤。

## 限制

- 精靈的語言跟著 Windows 的顯示語言，不是 App 設定裡選的語言；十種以外的語言顯示英文。
- 0.4.5 起已做程式碼簽章（Microsoft Artifact Signing，`package.ps1 -CodeSigning`）：`DeskPair.exe`、資料夾裡沒有別人簽過的 DLL
  與 MSI 本身。新版本在 SmartScreen 累積足夠信譽之前，第一次執行時仍可能詢問，但會顯示發行者名稱。
- 安裝程式不會替你開無人值守服務：那需要先設定固定密碼，仍是 App 設定頁的開關（`docs/unattended-windows.md`）。
