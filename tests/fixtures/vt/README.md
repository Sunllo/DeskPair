# 終端機測試向量

「shell 送出這些位元組 → 畫面應該長這樣」。桌面（C#，`tests/DeskPair.Core.Tests/VtVectorTests.cs`）與手機（Kotlin，
`mobile/shared`，E6 階段接上）**讀同一批檔案**：兩份 VT 剖析器會永遠並存，這是讓它們不漂移的唯一辦法。
改了其中一邊的行為，就在這裡加一筆，兩邊一起變綠。

每個檔案是一個陣列，每筆：

| 欄位 | 必填 | 意思 |
|---|---|---|
| `name` | ✅ | 測試名稱，寫成一句話 |
| `columns`、`rows` | ✅ | 畫面大小 |
| `input` 或 `inputHex` | ✅ | 餵進去的位元組：`input` 是 UTF-8 文字（JSON 的 `\u001b` 就是 ESC）；要測非法 UTF-8 時用 `inputHex` |
| `lines` | ✅ | 從第 0 列起每列的文字，去掉行尾空白；沒列出的列必須是空的。寬字元只算一次（右半格不輸出） |
| `cursor` | ✅ | `[欄, 列]`，從 0 起算；最後一欄的待換行狀態仍報最後一欄 |
| `scrollback` | | 捲出畫面的列，舊的在前 |
| `replies` | | 終端機應回給 shell 的位元組（DSR、DA） |
| `title` | | OSC 0／2 設定的標題 |
| `modes` | | `alternateScreen`、`bracketedPaste`、`applicationCursorKeys`、`cursorVisible` |
| `cells` | | 個別格子的顏色與屬性。顏色寫成 `default`、`palette:N`（0–255）、`rgb:rrggbb`；屬性是 `bold` `faint` `italic` `underline` `blink` `inverse` `invisible` `strike` |

每一筆都會被餵三次：整段一次、一次一個位元組、以及固定種子的隨機切段。三次結果必須完全相同——
pty 與網路會在任何位置把資料切開，包括 UTF-8 字元與跳脫序列的中間。
