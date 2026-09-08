# BYOD 程式競賽防弊瀏覽器（Kiosk Lockdown Client）

適用於學生自備電腦（BYOD）的免安裝綠色桌面客戶端。在無網路硬體控制權的實體教室中，
限制學生僅能造訪指定評測網域（`judge.gai.tw`），並阻斷外部通訊、新增分頁、開發者工具，
以及封鎖來自本機 IDE / Copilot 的外部剪貼簿貼入。

## 技術架構

- **框架**：.NET 8.0（C# / WPF）
- **瀏覽器核心**：Microsoft.Web.WebView2
- **程式碼編輯器**：Monaco Editor（完全本地化，執行期不外連 CDN）
- **發佈方式**：自包含單一執行檔（Windows x64）

> ⚠️ WPF 僅能在 **Windows** 上建置與執行。macOS / Linux 無法 `dotnet build` 本專案。

## 專案結構

分為三個專案：WPF App（Windows-only）、可攜的 Core 邏輯層（net8.0，可在 macOS 建置/測試）、xUnit 測試。

```
BYOD/
├── ByodKioskBrowser.sln          # 方案檔（三個專案）
├── ByodKioskBrowser.csproj       # WPF App（net8.0-windows，Windows-only）
├── app.manifest                  # asInvoker 權限 + 高 DPI（零系統破壞）
├── App.xaml / App.xaml.cs         # 生命週期；TEMP 快取清理（零殘留）
├── MainWindow.xaml / .cs          # Kiosk 佈局、失焦偵測、WebMessage 同步、日誌導出
├── Services/
│   └── SecureBrowserService.cs    # WebView2 加固、白名單、彈窗攔截（依賴 WebView2）
├── Core/                          # ← 可攜邏輯層（net8.0，無 WPF/WebView2）
│   ├── ByodKioskBrowser.Core.csproj
│   ├── Interfaces/                # IUrlWhitelistValidator, ICheatingDetector
│   ├── Models/                    # CheatingEvent, CheatingEventType
│   └── Services/
│       ├── UrlWhitelistValidator.cs  # 主機白名單（比對 Uri.Host）
│       └── CheatingDetector.cs       # 結構化事件紀錄 + JSON 導出
├── tests/ByodKioskBrowser.Tests/  # ← xUnit 測試（net8.0，macOS 可跑）
├── Assets/webapp/                 # 本地 Monaco 網頁層
│   ├── index.html / app.js / styles.css
│   └── vs/                         # ← 需放入 Monaco min/vs（見下方）
└── Scripts/fetch-monaco.ps1       # 一鍵下載並佈署 Monaco 本地資源
```

## 建置步驟（Windows）

1. 安裝 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 與
   [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)（Win10/11 多已內建）。
2. 佈署 Monaco 本地資源（不外連 CDN）：
   ```powershell
   powershell -ExecutionPolicy Bypass -File Scripts\fetch-monaco.ps1
   ```
3. 除錯執行：
   ```bash
   dotnet run
   ```
4. 發佈免安裝單一執行檔：
   ```bash
   dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
   ```
   輸出資料夾內含 `ByodKioskBrowser.exe` 與 `Assets\`（Monaco 本地資源），整包即為綠色可攜版。

## 在 macOS 上開發與測試

WPF/WebView2 無法在 macOS 執行，但可做兩件事：

- **編譯檢查**（catch 語法/型別/XAML 錯誤）：
  ```bash
  dotnet build ByodKioskBrowser.csproj   # csproj 已設 EnableWindowsTargeting（僅非 Windows 生效）
  ```
- **單元測試可攜邏輯**（白名單、事件偵測）：
  ```bash
  dotnet test tests/ByodKioskBrowser.Tests/ByodKioskBrowser.Tests.csproj
  ```
  > 若本機僅有 .NET 9 runtime，測試專案已設 `RollForward=Major` 可直接執行；否則安裝 .NET 8 runtime。

實際執行 Kiosk 畫面、驗證導航攔截 / 失焦遮罩 / 剪貼簿封鎖等**行為**，仍需在 Windows（實機或 VM）上 `dotnet run`。

## 安全設計對照（CLAUDE.md）

| 需求 | 實作位置 |
| --- | --- |
| 禁 F12 / 右鍵 / Ctrl+N,T,W,R,F5 / 狀態列 | `SecureBrowserService.ApplySettings` |
| 導航白名單（`e.Cancel`） | `SecureBrowserService.OnNavigationStarting` + `UrlWhitelistValidator` |
| 彈窗 / 新分頁攔截（`e.Handled`） | `SecureBrowserService.OnNewWindowRequested` |
| Kiosk 外觀（None / Maximized / NoResize / Topmost） | `MainWindow.xaml` |
| 失焦防弊偵測 + 警示遮罩 | `MainWindow.OnWindowDeactivated` + `WarningOverlay` |
| 本地 Monaco + WebMessage 雙向同步 | `Assets/webapp/app.js` ↔ `MainWindow.OnEditorMessageReceived` |
| 封鎖外部剪貼簿貼入 | `app.js` 剪貼簿隔離 + `MainWindow` 全域 `ApplicationCommands.Paste` |
| 結構化 JSON 事件日誌 | `CheatingDetector` → `logs/events_*.json` |
| 零系統破壞 / 零殘留 | `app.manifest`（asInvoker）+ `App` TEMP 快取清理 |

## 操作說明

- **監考人員離開**：`Ctrl + Alt + Shift + Q`（會記錄並導出日誌後關閉）。
- 一般 `Alt + F4` 與視窗關閉會被攔截，維持 Kiosk 封閉狀態。
- 日誌輸出於執行檔旁的 `logs\` 資料夾（即時 `.jsonl` + 關閉時完整 `.json`）。

## 白名單網域

`judge.gai.tw`、`cdn.jsdelivr.net`、`cdnjs.cloudflare.com`、
`fonts.googleapis.com`、`fonts.gstatic.com`、`appassets.local`（本地 Monaco）。
如需調整，修改 `UrlWhitelistValidator.DefaultHosts()`。
