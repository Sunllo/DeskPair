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
- 精靈只有歡迎、準備安裝、進度、完成四頁，完成頁預設勾選「Open DeskPair」。**沒有授權同意頁**：
  AGPL 第 9 節明說使用者不必接受授權就能執行程式，所以安裝程式也不要求。對話框是英文（見「限制」）。
- `packaged` 告訴 App 這份是 Windows Installer 裝的（`PackagedInstall`）。App 內更新因此改走下一版 MSI，
  而不是把檔案複製到 Windows Installer 擁有的資料夾裡。

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
- **移除**：刪檔案之前，以 LocalSystem 執行 `DeskPair.exe --remove-system-changes`，拆掉 App 在自己資料夾以外做的事——
  無人值守服務、防火牆規則、虛擬顯示器驅動。這一步不開任何視窗（安裝程式底下沒有人能回答，工作階段 0 的對話框只會
  讓移除卡住），拆不掉的寫進 `%ProgramData%\Sunllo\DeskPair\logs\service.log`，不讓整個移除失敗。
  **升級時不跑這一步**（條件是 `REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE`）。
- 資料不刪：`%ProgramData%\Sunllo\DeskPair`（本機 ID、金鑰、密碼）與 `%AppData%\Sunllo\DeskPair`（設定、設備清單）
  都留著，重新安裝後還是同一台電腦。

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

- 只能在 Windows 上建置（MSI 由 Windows Installer 的 API 產生），所以這個專案不在 `DeskPair.slnx` 裡。
- **WiX v7 的授權**：WiX 原始碼是 MS-RL，但官方的二進位發行版附帶 Open Source Maintenance Fee EULA：
  在營利活動中使用、而且年總營收達 US$10,000 的使用者要按月付維護費（贊助 wixtoolset）。每台建置機要先接受一次：
  `dotnet build packaging/windows/DeskPair.wixproj -t:AcceptEula -p:EulaId=wix7`（紀錄在 `%USERPROFILE%\.wix\`）。
  這是人的決定，所以專案檔不代為接受；`package.ps1` 在還沒接受的機器上會先停下來說明。EULA 全文在
  `%USERPROFILE%\.nuget\packages\wixtoolset.sdk\7.0.0\OSMFEULA.txt`。
- `SuppressIces` 關掉 ICE38、ICE43、ICE57：這三項假設「開始」功能表和桌面可能是個人資料夾，全機安裝的套件不會是，
  捷徑的 KeyPath 也應該在 HKLM。其餘 ICE 都照常驗證，警告視為錯誤。

## 限制

- 安裝程式本身的對話框只有英文（App 仍依系統語言顯示）。要多語言得為每種語言建一份，再用語言轉換（transform）
  合成一個 MSI，之後再做。
- 0.4.5 起已做程式碼簽章（Microsoft Artifact Signing，`package.ps1 -CodeSigning`）：`DeskPair.exe`、資料夾裡沒有別人簽過的 DLL
  與 MSI 本身。新版本在 SmartScreen 累積足夠信譽之前，第一次執行時仍可能詢問，但會顯示發行者名稱。
- 安裝程式不會替你開無人值守服務：那需要先設定固定密碼，仍是 App 設定頁的開關（`docs/unattended-windows.md`）。
