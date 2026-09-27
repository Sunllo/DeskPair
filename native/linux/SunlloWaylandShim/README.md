# SunlloWaylandShim（Linux Wayland 擷取的 C shim）

`DeskPair.Platform.Linux` 在 Wayland 上經由 xdg-desktop-portal 拿到的是 PipeWire 串流。PipeWire 的格式協商由 SPA pod
組成，而 pod 的建構與剖析全是標頭檔裡的 `static inline` 函式，沒有可以 P/Invoke 的符號；純 C# 綁定等於重寫 POD 序列化。
所以串流放在這支小 C 函式庫裡、跑在 PipeWire 自己的執行緒上，C# 只用「拉」的方式問它：

| 函式 | 做什麼 |
|---|---|
| `dp_pw_abi` | 回報 ABI 版本與兩個結構的大小；載入器不符就當作不可用 |
| `dp_pw_open` | 以 portal 的 `OpenPipeWireRemote` 給的 fd（shim 自己 dup 一份）連上並開始串流 |
| `dp_pw_wait` | 等到有還沒取走的畫面、逾時，或串流結束 |
| `dp_pw_lock_frame` | 取最新的一張，同時把上一張還給串流 |
| `dp_pw_release_frame` | 還回目前取走的那張 |
| `dp_pw_cursor` | 串流最後描述的游標（位置與圖，metadata 模式） |
| `dp_pw_state` / `dp_pw_close` | 狀態與原因；停止並釋放一切 |

C# 永遠不在 PipeWire 的執行緒上跑，PipeWire 也永遠不呼叫 C#。結構在 C# 的鏡像是 `Native/WaylandShim.cs`，
`WaylandShimLayoutTests` 直接讀這裡的 `shim.h` 算出每個欄位的位移來比對。

## 為什麼自己 mmap

緩衝區是合成器給的 memfd。shim 不讓 `pw_stream` 幫忙映射，而是自己 `mmap`：串流重新協商（螢幕換解析度）時
`pw_stream` 會把緩衝區連同映射一起拿走，而那時 C# 可能還在編碼上一張。自己的映射讓記憶體活到那張被還回來為止，
所以畫面可以**不複製**直接交給編碼器（`DP_FRAME_BORROWABLE`）。至少要三個緩衝區（C# 拿著一張、等著一張、合成器畫一張），
少於三個或不是 memfd 時，C# 改成複製後立刻還回。

## 建置

```
pwsh tools/build-wayland-shim.ps1
```

在 Docker 的 Ubuntu 22.04 容器裡跑 `build.sh`，產出 `artifacts/wayland-shim/linux-x64/libSunlloWaylandShim.so`。
選 22.04 是為了相容性：結果只需要 glibc 2.35 與 PipeWire 0.3.48，比這新的發行版都能用。任何裝了
`build-essential pkg-config libpipewire-0.3-dev` 的 Linux 也可以直接跑 `build.sh`。

之後 Linux 版的 `publish.ps1` 會經由 `src/DeskPair.Platform.Linux/WaylandShim.targets` 把它包進單一執行檔
（執行時解到 `~/.net/<app>/<hash>/`），安裝程式只複製那一個檔案也不會漏掉它。沒有建置 shim 時產品照樣能建置與執行，
只是 Wayland 工作階段的畫面擷取不可用。

## 載入順序

1. `SUNLLO_WAYLANDSHIM_PATH`
2. 執行檔旁邊
3. `runtimes/<rid>/native/`
4. 系統載入器（單檔發佈解出來的位置就在這裡被找到）

`libpipewire-0.3.so.0` 是直接連結的；沒有 PipeWire 的機器也就沒有 portal 螢幕分享，兩者答案相同。
