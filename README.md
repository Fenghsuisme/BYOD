# BYOD 程式競賽防弊瀏覽器（Kiosk Lockdown Client）

適用於學生自備電腦（BYOD）的免安裝綠色桌面客戶端。限制學生僅能造訪指定評測網域
（`judge.gai.tw`），阻斷外部通訊、新增分頁、開發者工具，並封鎖來自本機 IDE / Copilot
的外部剪貼簿貼入。

**跨平台**：以 Avalonia + CEF 一套程式碼支援 **Windows x64** 與 **Linux x64**
（macOS Apple Silicon 可用於開發）。

## 技術架構

- **框架**：.NET 8.0（C#）
- **UI**：[Avalonia UI](https://avaloniaui.net/) 11（跨平台 XAML）
- **瀏覽器核心**：[CefGlue](https://github.com/OutSystems/CefGlue)（Chromium Embedded Framework）
- **程式碼編輯器**：Monaco Editor（完全本地化，執行期不外連 CDN）
- **發佈方式**：自包含綠色資料夾（CEF 為多檔原生，非單一執行檔）

## ⚠️ 各平台防弊強度差異（務必理解）

防弊鎖定的強度依作業系統而不同，這是 OS 本質限制、非程式缺陷：

| 平台 | 鎖定強度 | 說明 |
|---|---|---|
| **Windows** | 強 | 全螢幕置頂 + 快速鍵/彈窗/導航攔截；失焦偵測完整 |
| **Linux (X11)** | 中 | 同上，但攔截全域快速鍵（Super、Alt+Tab）受限 |
| **Linux (Wayland)** | 弱 | **無法**從應用程式攔截 Super/Alt+Tab/切換工作區（Wayland 安全設計）；僅能全螢幕置頂 + 失焦偵測 + 記錄 |

> Linux（尤其 Wayland）**擋不住決心切走的人**，只能「盡力而為 + 完整記錄」。若需硬鎖，需在 OS 層設定專用 kiosk session，但這與 BYOD（學生自有機）相衝突。

## 專案結構

```
BYOD/
├── ByodKioskBrowser.sln
├── App/                              # Avalonia + CEF 客戶端（跨平台）
│   ├── ByodKioskBrowser.App.csproj
│   ├── Program.cs                    # Avalonia 進入點 + CEF 初始化 + TEMP 快取清理
│   ├── App.axaml(.cs)
│   ├── MainWindow.axaml(.cs)         # Kiosk 佈局、失焦偵測、JS 橋接、日誌導出
│   ├── app.manifest                  # Windows asInvoker + 高 DPI
│   ├── Browser/
│   │   ├── SecurityHandlers.cs       # 導航白名單 / 擋彈窗 / 禁右鍵 / 封鎖快速鍵
│   │   ├── LocalAssetSchemeHandlerFactory.cs  # app:// 提供本地 Monaco 資源
│   │   └── EditorBridge.cs           # 編輯器 JS ↔ C# 橋接
│   └── Assets/webapp/                # 本地 Monaco 網頁層
│       ├── index.html / app.js / styles.css
│       └── vs/                        # ← 需放入 Monaco min/vs（見下方）
├── Core/                             # 可攜邏輯層（net8.0，無 UI）
│   ├── Interfaces/ Models/ Services/  # 白名單、事件偵測、模型
├── tests/ByodKioskBrowser.Tests/     # xUnit 測試
├── run.sh                            # 啟動器（dotnet run）
└── Scripts/
    ├── setup-exam.sh                  # 一鍵建置考試環境碟（SDK/依賴/Monaco/建置/EXAM 圖示）
    ├── install-linux-deps.sh          # 安裝 CEF 系統函式庫 + g++
    ├── install-shortcut.sh            # 建立桌面 EXAM 圖示
    └── fetch-monaco.sh                # 下載並佈署 Monaco
```

## 部署：考試環境碟（Linux）

做法為「**先弄好一隻黃金環境碟，之後整碟複製**」。目標為 x64 的 Linux（開發於 GNOME/Wayland 驗證）。

### 一鍵建置（在黃金碟上跑一次）
```bash
cd ~/BYOD
./Scripts/setup-exam.sh
```
會自動完成：安裝 .NET 8 SDK（若無）→ 安裝 CEF 系統函式庫與 g++ → 佈署 Monaco 並預先建置（還原 CEF 套件）→ 桌面建立 **EXAM** 圖示。

完成後，桌面雙擊 **EXAM** 即可開始考試。之後**整碟完整複製**，所有複本開箱即用（環境已內含、免再下載）。

> 考試機需有網路以安裝系統依賴（走 apt/dnf/pacman）。
> CEF 在 Linux 以免 root 的 `NoSandbox` 執行；Wayland 下無法硬鎖 Alt+Tab（見上方強度表）。

### 除錯
- 一般啟動：`./run.sh`（離開：`Ctrl+Alt+Shift+Q` 或右上「結束考試」）
- 視窗模式（有邊框方便觀察）：`BYOD_WINDOWED=1 ./run.sh`
- 終端輸出會同時寫入 `logs/terminal_<時間>.log`

## 在 macOS 上開發

macOS（Apple Silicon）可**直接編譯並執行**用於開發（csproj 依主機 OS 自動選用 `CefGlue.Avalonia.ARM64`）：
```bash
dotnet build App/ByodKioskBrowser.App.csproj    # 編譯
dotnet test  tests/ByodKioskBrowser.Tests/ByodKioskBrowser.Tests.csproj   # 邏輯測試
```
> 部署仍以 Windows/Linux **x64** 為準（考試機）。arm64 部署需改用對應 `.ARM64` 套件。

## 安全設計對照

| 需求 | 實作位置 |
| --- | --- |
| 導航白名單（比對 URL） | `WhitelistRequestHandler.OnBeforeBrowse` + `UrlWhitelistValidator` |
| 彈窗 / 新分頁攔截 | `BlockPopupLifeSpanHandler.OnBeforePopup` |
| 禁右鍵選單 | `NoContextMenuHandler`（清空選單模型） |
| 禁 F12 / F5 / Ctrl+R,N,T,W,U,P / DevTools | `LockdownKeyboardHandler.OnPreKeyEvent` |
| 禁下載 | 不設定 `DownloadHandler`（CEF 預設拒絕） |
| Kiosk 外觀（全螢幕 / 無邊框 / 置頂） | `MainWindow.axaml` |
| 失焦防弊偵測 + 警示遮罩 | `MainWindow` `Deactivated` + `WarningOverlay` |
| 本地 Monaco + 雙向同步 | `app.js` ↔ `EditorBridge`（`RegisterJavascriptObject` / `ExecuteJavaScript`） |
| 封鎖外部剪貼簿貼入 | `app.js` 剪貼簿隔離（攔截原生 copy/cut/paste/drop） |
| 本地資源不外連 CDN | `app://` 自訂 scheme（`LocalAssetSchemeHandlerFactory`） |
| 結構化 JSON 事件日誌 | `CheatingDetector` → `exams/<日期>/events.json` |
| 零系統破壞 / 零殘留 | asInvoker manifest + CEF 快取導向 TEMP，關閉時 `CefRuntime.Shutdown()` + 刪除 |

## 操作說明

- **結束考試**：右上「結束考試」→ 確認 → 存所有分頁檔案與紀錄至本場資料夾後關閉。
- **監考人員離開**：`Ctrl + Alt + Shift + Q`（記錄並導出日誌後關閉）。
- 其他關閉方式會被攔截，維持 Kiosk 封閉狀態。
- 每場考試輸出於 `exams/<日期>/`：`events.jsonl`/`events.json`（紀錄）與 `files/`（各分頁）。

## 白名單網域

`judge.gai.tw`、`cdn.jsdelivr.net`、`cdnjs.cloudflare.com`、
`fonts.googleapis.com`、`fonts.gstatic.com`，本地資源走 `app://` scheme。
如需調整，修改 `Core/Services/UrlWhitelistValidator.cs` 的 `DefaultHosts()`。
