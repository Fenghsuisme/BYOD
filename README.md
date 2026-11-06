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
    ├── setup-exam.sh                  # 一鍵建置考試母碟（依賴/下載預建成品/EXAM 圖示/桌面設定）
    ├── setup-chrome.sh                # 安裝 Chrome 並鎖定只能開 Moodle（交作業）
    ├── prepare-image.sh               # 母碟去個人化（Clonezilla 擷取前執行）
    ├── check-exam.sh                  # 複製機健康檢查
    ├── install-linux-deps.sh          # 安裝 CEF 系統函式庫 + g++
    ├── install-shortcut.sh            # 建立桌面 EXAM 圖示
    └── fetch-monaco.sh                # 下載並佈署 Monaco
```

## 部署：Windows（免安裝，建議）

教室 Windows 電腦直接執行，不動系統、不需系統管理員權限、不需安裝任何東西。

1. 從 Release [`windows-latest`](https://github.com/Fenghsuisme/BYOD/releases/tag/windows-latest) 下載 `byod-windows-x64.zip`（每次推到 main 由 `.github/workflows/release-windows.yml` 自動重建，打包前會在 Windows 上實測編譯器與啟動）。
2. 解壓，得到 `BYOD-Exam/` 資料夾；OOP 場先編輯其中的 `pdf-id.txt`。
3. 把 `BYOD-Exam/` 複製到每台電腦（桌面或任一可寫入的位置）。**經由 USB 隨身碟（exFAT / FAT32）複製**可去除「從網路下載」標記，避免每台都跳 SmartScreen 警告。
4. 考試時雙擊 `程式設計.cmd` 或 `OOP資料結構.cmd`。

- 內附免安裝 C/C++ 編譯器 `w64devkit/`，學生程式以 `-static` 連結。
- 考試中於程式內攔截 Win 鍵、Alt+Tab、Alt+Esc、Ctrl+Esc、Ctrl+Shift+Esc、Alt+F4（低階鍵盤掛鉤，程式結束即失效、零殘留）；Ctrl+Alt+Del、Win+L 無法攔截，由失焦偵測記錄。
- 考試紀錄在 `BYOD-Exam/exams/` 與 `BYOD-Exam/logs/`；教室若有還原卡，重開機前要先收走。

## 部署：考試環境碟（Linux）

做法為「**一台母碟 → Clonezilla 複製到整間教室**」。目標為 x64 的 Linux（開發於 GNOME/Wayland 驗證），複製機可為不同型號。

### 1. 製作母碟（以考試帳號在桌面登入後執行，不要加 sudo）
```bash
git clone https://github.com/Fenghsuisme/BYOD.git ~/BYOD
cd ~/BYOD
./Scripts/setup-exam.sh
```
會自動完成：關閉閒置休眠 → 安裝 g++ / gcc 與 CEF 系統函式庫（已裝的略過）→ 下載 GitHub Actions 預先建好的考試程式到 `dist/`（約 200 MB，不需 .NET SDK、不需建置）→ 安裝 Google Chrome 並以企業政策鎖定只能開 Moodle（`elearningv4.nuk.edu.tw`，交作業用）→ 建立桌面捷徑（程式設計 / OOP & DS / 交作業）→ GNOME 桌面設定（新酷音、1 個工作區、Dock 只留垃圾桶並自動隱藏、閒置不休眠不鎖定）。

完成後登出再登入一次，**兩個桌面圖示都實際開一次、測過再繼續**。

### 2. 去個人化並擷取
```bash
./Scripts/prepare-image.sh --shutdown
```
清除母碟的考試紀錄、還原快速鍵鎖定、關閉自動更新、網路設定解除綁定網卡、清空 `machine-id`、補上 UEFI 備援開機檔，然後關機。
**之後不要再開機進入母碟**（會重新產生 `machine-id`；要修改母碟就改完再重跑一次）。

用 Clonezilla 擷取整顆硬碟（savedisk）。還原到比母碟小的硬碟時，進階選項選 `-icds` 與 `-k1`。
整間教室可用 Clonezilla Lite Server 群播同時還原。

> 沒跑 `prepare-image.sh` 就複製的話，所有機器共用同一個 `machine-id`，DHCP 可能發同一個 IP 給整間教室 → 網路時通時斷。

### 3. 複製機檢查（每台開機後）
```bash
~/BYOD/Scripts/check-exam.sh
```
檢查考試程式、g++、新酷音、IP 與 judge 連線；最後印出的「機器 ID / IP」各台不可重複。

更新考試程式：在母碟（或單台）`git pull && ./Scripts/fetch-release.sh`（已是最新版就不會重新下載），母碟更新後須重跑步驟 2。
每次推到 main，`.github/workflows/release-linux.yml` 會自動更新 Release `linux-latest`。

> CEF 在 Linux 以免 root 的 `NoSandbox` 執行；Wayland 下無法硬鎖 Alt+Tab（見上方強度表）。

### 除錯
- 一般啟動：`./run.sh`（離開：`Ctrl+Alt+Shift+Q` 或右上「結束並退出」）
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

- **結束考試（程式設計）**：右上「結束並退出」→ 頂部列「確定」→ 存所有分頁至本場資料夾與 `logs/程式設計_<時間>/`，存完**自動關閉程式**。
- **結束並退出（OOP）**：右上「結束並退出」→ 頂部列「確定」→ 另存至桌面 `OOP&DS_<日期>/` 後關閉程式。
- **監考人員離開**：`Ctrl + Alt + Shift + Q`（存檔、導出日誌後關閉）。
- 其他關閉方式會被攔截，維持 Kiosk 封閉狀態。
- 每場考試輸出於 `exams/<類別>_<日期時間>/`：`events.jsonl`/`events.json`（紀錄）與 `files/`（各分頁）。

## 繳交與事後比對

OOP 場「結束並退出」後，學生用桌面「交作業（Moodle）」圖示上傳 `Desktop/OOP&DS_<日期>/`。
Chrome 由 `/etc/opt/chrome/policies/managed/byod-moodle.json` 鎖定只能開 Moodle、關閉即清除登入狀態。
每個繳交檔案的 SHA-256 記在 `events.json`（「繳交檔案 …」），老師可對 Moodle 收到的檔案跑 `sha256sum` 比對，察覺退出後是否被修改。

## 白名單網域

`judge.gai.tw`、`cdn.jsdelivr.net`、`cdnjs.cloudflare.com`、
`fonts.googleapis.com`、`fonts.gstatic.com`，本地資源走 `app://` scheme。
如需調整，修改 `Core/Services/UrlWhitelistValidator.cs` 的 `DefaultHosts()`。
