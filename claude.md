BYOD 程式競賽防弊瀏覽器專案指引

本專案為適用於學生自備電腦（BYOD）的免安裝綠色桌面客戶端（Kiosk Lockdown Client）。核心目標為在無網路硬體控制權的實體教室中，限制學生僅能造訪指定評測網域（如 DMOJ），並阻斷外部通訊、新增分頁、開發者工具與 IDE AI 輔助。

核心設計原則 (Core Directives)

零系統破壞 (Zero OS Mutation)：
嚴禁修改 Windows 系統全域註冊表（Registry）。
嚴禁安裝常駐 Windows 服務或驅動程式。
嚴禁修改 Windows 全域防火牆規則。
程式關閉後必須達成 100% 無痕、零殘留。
防弊與封閉環境 (Enclosed Environment)：
透過應用程式內部邏輯接管視窗與網路導航。
杜絕外部文字貼入（防範本機 VS Code Copilot 生成後複製進來）。
嚴格攔截任何形式的新開視窗（彈窗、新分頁、Ctrl+點擊、target="_blank"）。
技術架構與環境 (Tech Stack)

框架：.NET 8.0 (C# / WPF)
瀏覽器核心：Microsoft.Web.WebView2 (WPF 控制項)
程式碼編輯器：Monaco Editor（完全打包為本地靜態資源，嚴禁外連 CDN）
打包發佈方式：自包含單一執行檔（Self-contained Single File Executable）
常用命令 (Build & Run Commands)

編譯專案：
dotnet build
本機除錯執行：
dotnet run
發佈免安裝單一執行檔 (Windows x64)：
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
核心模組與實作規範 (Coding Standards & Security Rules)

1. WebView2 安全加固 (SecureBrowserService)

初始化必須配置 CoreWebView2Settings：

AreDevToolsEnabled = false（禁止 F12 開發者工具）

AreDefaultContextMenusEnabled = false（禁用右鍵選單）

IsBrowserAcceleratorKeysEnabled = false（阻斷 Ctrl+N, Ctrl+T, Ctrl+W, Ctrl+R, F5）

IsStatusBarEnabled = false

導航白名單控制 (NavigationStarting)：

網域白名單：https://judge.gai.tw 以及必要靜態 CDN（如字型、MathJax）。

比對 Uri.Host，未在白名單內的請求一律執行 e.Cancel = true。

彈窗阻斷 (NewWindowRequested)：

必須設定 e.Handled = true。

若目標 URI 屬於白名單，強制由原 WebView2 實例導航（Navigate）；若非白名單則直接丟棄並記錄日誌。

2. 介面佈局與焦點監控 (MainWindow)

Kiosk 外觀：

WindowStyle = WindowStyle.None

WindowState = WindowState.Maximized

ResizeMode = ResizeMode.NoResize

Topmost = true

失焦防弊偵測：

監聽 Window.Deactivated 事件。

當視窗失去焦點時（例如 Alt+Tab 切換至本機 IDE），遞增計數並產生包含時間戳記的日誌，UI 需顯示警示遮罩。

3. 本地 Monaco Editor 整合

HTML/JS/CSS 必須作為內嵌資源或隨附本地靜態檔案載入，不得依賴外部網路。
透過 CoreWebView2.PostWebMessageAsString 或 WebMessage 機制與 C# 進行程式碼雙向同步。
限制貼上功能：攔截全域 ApplicationCommands.Paste，僅允許編輯器內部暫存區的複製貼上，封鎖來自系統剪貼簿的外部資料。
程式碼風格與架構慣例 (Code Style & Patterns)

遵循標準 C# 命名慣例（PascalCase 用於方法/類別，camelCase 用於局部變數，_camelCase 用於私有欄位）。
採用介面導向設計（如 IUrlWhitelistValidator, ICheatingDetector），便於單元測試與日後擴充。
所有非同步作業應使用 async/await，避免阻塞 WPF UI 執行緒。
日誌結構化：異常或作弊事件應包裝為物件，支援導出標準 JSON 格式。
如何使用此檔案

在你的 C# WPF 專案根目錄下新增 CLAUDE.md 並貼上上方內容。
在終端機執行 claude 進入 Claude Code。
你可以直接下達任務，例如：
請依照 CLAUDE.md 的規範，建立 SecureBrowserService.cs 並實作白名單驗證與彈窗攔截
請實作 MainWindow.xaml 與失焦偵測防弊邏輯