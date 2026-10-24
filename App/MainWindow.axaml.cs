using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ByodKioskBrowser.Browser;
using ByodKioskBrowser.Execution;
using ByodKioskBrowser.Interfaces;
using ByodKioskBrowser.Models;
using ByodKioskBrowser.Services;
using Xilium.CefGlue.Avalonia;

namespace ByodKioskBrowser;

public partial class MainWindow : Window
{
    private const string JudgeStartUrl = "https://judge.gai.tw/";

    private readonly IUrlWhitelistValidator _whitelist = new UrlWhitelistValidator();
    private readonly IUrlWhitelistValidator _navigationWhitelist =
        new UrlWhitelistValidator(UrlWhitelistValidator.NavigationHosts());
    private readonly ICheatingDetector _detector;
    private readonly CodeRunnerService _runner = new();
    private readonly EditorBridge _bridge;
    private readonly HostBridge _hostBridge = new();

    // 判題頁複製監聽：於頁面載入後注入，攔截複製並回報給 hostBridge
    private const string JudgeCopyListenerScript =
        "(function(){document.addEventListener('copy',function(){try{" +
        "var t=window.getSelection?window.getSelection().toString():'';" +
        "if(t&&window.hostBridge&&window.hostBridge.copyFromPage){window.hostBridge.copyFromPage(t);}" +
        "}catch(e){}},true);})();";

    private AvaloniaCefBrowser? _judgeBrowser;
    private AvaloniaCefBrowser? _editorBrowser;

    private string _currentCode = string.Empty;
    // 編輯器自動同步來的最新分頁 JSON（繳交時若即時請求逾時則以此為準）
    private volatile string _latestFilesJson = "[]";
    private TaskCompletionSource<string>? _submitWaiter;
    // 考試類別：用於存檔資料夾命名
    private readonly bool _pdfMode = Environment.GetEnvironmentVariable("BYOD_MODE") == "pdf";
    private string ExamCategory => _pdfMode ? "OOP&DS" : "程式設計";
    private bool _ending;
    // WarningOverlay 防抖：Deactivated 觸發後等 200ms，
    // 若 Activated 在此期間到達（CEF 內部焦點轉移）則取消，不顯示警示
    private bool _deactivatePending;
    // 導航至 local page 期間的額外 3 秒壓制視窗（雙重保護）
    private DateTime _suppressDeactivateUntil = DateTime.MinValue;
    private bool _oopMode;

    // 診斷用：BYOD_WINDOWED=1 → 一般視窗模式（有邊框、不置頂），方便確認視窗能否顯示
    private readonly bool _windowed = Environment.GetEnvironmentVariable("BYOD_WINDOWED") == "1";

    // 本次考試工作階段資料夾（每次啟動新建）；檔案與紀錄都存在此
    private readonly string _sessionFolder;
    // 程式碼備份：<log 資料夾>/<類別>_<本場開始時間>/（judge 模式按「結束考試」只存檔不關閉）
    private readonly string _logCodeFolder;
    private readonly string _liveLogPath;
    private readonly string _exportLogPath;

    public MainWindow()
    {
        InitializeComponent();

        // 資料夾以「類別_日期時間」為名（HH-mm-ss 避免同日多場衝突）
        var stamp = ExamCategory + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        _sessionFolder = Path.Combine(AppContext.BaseDirectory, "exams", stamp);
        // run.sh 會以 BYOD_LOG_DIR 指向專案根目錄的 logs/（與終端紀錄同處）
        var logDir = Environment.GetEnvironmentVariable("BYOD_LOG_DIR");
        if (string.IsNullOrWhiteSpace(logDir)) logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        _logCodeFolder = Path.Combine(logDir, stamp);
        Directory.CreateDirectory(_sessionFolder);
        Console.WriteLine($"[BYOD] 本場考試資料夾：{_sessionFolder}");
        _liveLogPath = Path.Combine(_sessionFolder, "events.jsonl");
        _exportLogPath = Path.Combine(_sessionFolder, "events.json");
        _detector = new CheatingDetector(_liveLogPath);

        _bridge = new EditorBridge(_runner) { ExamCategory = ExamCategory };
        _bridge.FilesChanged += json => _latestFilesJson = json;
        _bridge.FilesSubmitted += json =>
        {
            _latestFilesJson = json;
            _submitWaiter?.TrySetResult(json);
        };
        _bridge.Ready += OnEditorReady;
        _bridge.CodeChanged += code => _currentCode = code;
        _bridge.CheatReported += OnCheatReported;
        _bridge.RunOutput += (stream, text) => Dispatcher.UIThread.Post(() =>
            RunInEditor($"window.__runOutput && window.__runOutput({JsString(stream)}, {JsString(text)})"));
        _bridge.RunCompleted += OnRunCompleted;
        _bridge.SaveCompleted += msg => Dispatcher.UIThread.Post(() =>
            RunInEditor($"window.__saveResult && window.__saveResult({JsString(msg)})"));

        // 判題頁複製 → 推入編輯器內部剪貼簿
        _hostBridge.PageCopied += OnPageCopied;

        // Activated：取消 pending 防抖計時（代表是 CEF 內部焦點轉移，非真正離開應用程式）
        Activated += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _deactivatePending = false;
            WarningOverlay.IsVisible = false;
        });
        Deactivated += (_, _) => Dispatcher.UIThread.Post(OnWindowDeactivated);

        // 結束考試按鈕與確認列。確認放在頂部狀態列：置中遮罩會被 CEF 原生視窗蓋住（Linux 上看不到也點不到）
        EndExamButton.Click += (_, _) => ShowConfirm(true);
        ConfirmNoButton.Click += (_, _) => ShowConfirm(false);
        ConfirmYesButton.Click += async (_, _) => await SubmitAsync(exit: _oopMode, "學生按下結束考試，開始繳交。");

        // 判題站導覽鍵（作用於左側瀏覽器）
        BackButton.Click += (_, _) => { if (_judgeBrowser?.CanGoBack == true) _judgeBrowser.GoBack(); };
        ForwardButton.Click += (_, _) => { if (_judgeBrowser?.CanGoForward == true) _judgeBrowser.GoForward(); };
        ReloadButton.Click += (_, _) => _judgeBrowser?.Reload();
        HomeButton.Click += (_, _) => { if (_judgeBrowser != null) _judgeBrowser.Address = JudgeStartUrl; };
        LibraryButton.Click += (_, _) => NavigateToLocalPage("library.html");
        DictButton.Click    += (_, _) => NavigateToLocalPage("dictionary.html");

        Opened += OnOpened;
        Closing += OnWindowClosing;

        ConfigureWindowChrome();
    }

    /// <summary>依模式設定視窗外觀：Kiosk（全螢幕）或診斷用一般視窗。</summary>
    private void ConfigureWindowChrome()
    {
        if (_windowed)
        {
            SystemDecorations = SystemDecorations.Full;
            Topmost = false;
            CanResize = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Width = 1280;
            Height = 800;
            WindowState = WindowState.Normal;
        }
        else
        {
            // Kiosk：在視窗顯示「前」即設定全螢幕，GNOME Wayland 才會正確全螢幕
            SystemDecorations = SystemDecorations.None;
            CanResize = false;
            WindowState = WindowState.FullScreen;
            Topmost = true;
        }
    }

    /// <summary>
    /// Linux 後備方案：GNOME Wayland 不接受 FullScreen，改以螢幕尺寸手動鋪滿並置頂。
    /// 可用環境變數 BYOD_SIZE=WxH（例 1920x1080）覆寫尺寸。
    /// </summary>
    private void ApplyKioskSizeLinux()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        double w = 1920, h = 1080;
        var pos = new PixelPoint(0, 0);
        if (screen != null)
        {
            var scaling = screen.Scaling <= 0 ? 1 : screen.Scaling;
            w = screen.Bounds.Width / scaling;
            h = screen.Bounds.Height / scaling;
            pos = screen.Bounds.Position;
        }

        var sizeEnv = Environment.GetEnvironmentVariable("BYOD_SIZE");
        if (!string.IsNullOrWhiteSpace(sizeEnv) && sizeEnv.Contains('x'))
        {
            var parts = sizeEnv.Split('x');
            if (double.TryParse(parts[0], out var ew) && double.TryParse(parts[1], out var eh))
            {
                w = ew;
                h = eh;
            }
        }

        WindowState = WindowState.Normal;
        Position = pos;
        Width = w;
        Height = h;
        Topmost = true;
        Console.WriteLine($"[BYOD] Linux 手動鋪滿。bounds={Bounds} target={w}x{h} pos={pos}");
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Console.WriteLine($"[BYOD] Window Opened. windowed={_windowed} " +
            $"DISPLAY={Environment.GetEnvironmentVariable("DISPLAY")} " +
            $"WAYLAND_DISPLAY={Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")} " +
            $"bounds={Bounds}");

        if (!_windowed && OperatingSystem.IsLinux())
        {
            // GNOME Wayland 忽略 FullScreen；版面完成後以螢幕尺寸手動鋪滿
            Dispatcher.UIThread.Post(ApplyKioskSizeLinux, DispatcherPriority.Background);
        }

        // 診斷開關：定位崩潰發生在哪一步
        var noBrowser = Environment.GetEnvironmentVariable("BYOD_NO_BROWSER") == "1";
        var oneBrowser = Environment.GetEnvironmentVariable("BYOD_ONE_BROWSER") == "1";

        if (noBrowser)
        {
            Console.WriteLine("[BYOD] NO_BROWSER：不建立任何瀏覽器（只開殼視窗）。");
            StatusText.Text = "診斷模式：未載入瀏覽器。";
            return;
        }

        // 左：評測網站
        Console.WriteLine("[BYOD] 步驟1：建立 judge 瀏覽器…");
        _judgeBrowser = CreateBrowser();
        _judgeBrowser.RegisterJavascriptObject(_hostBridge, "hostBridge");
        _judgeBrowser.LoadEnd += (_, e) =>
        {
            if (e.Frame != null && e.Frame.IsMain)
            {
                _judgeBrowser?.ExecuteJavaScript(JudgeCopyListenerScript);
            }
        };
        // 依模式決定左側內容：judge（程式設計）或 題目 PDF（OOP/資料結構）
        if (_pdfMode)
        {
            _oopMode = true;
            EndExamButton.Content = "結束並退出";
            ConfirmDetailText.Text = "將存檔至桌面並關閉程式，確定？";
            HomeButton.IsVisible = false;   // OOP 模式不允許導回 judge 首頁
            Console.WriteLine("[BYOD] 步驟2：PDF 模式，載入題目檢視器…");
            _judgeBrowser.Address = LocalAssetSchemeHandlerFactory.PdfViewerUrl;
            JudgeHost.Child = _judgeBrowser;
            StartExamPdfDownload();
            Console.WriteLine("[BYOD] 步驟3：PDF 檢視器已載入（背景下載題目）。");
        }
        else
        {
            Console.WriteLine("[BYOD] 步驟2：設定 judge Address…");
            _judgeBrowser.Address = JudgeStartUrl;
            JudgeHost.Child = _judgeBrowser;
            Console.WriteLine("[BYOD] 步驟3：judge 瀏覽器已就緒。");
        }

        if (oneBrowser)
        {
            Console.WriteLine("[BYOD] ONE_BROWSER：略過編輯器。");
            StatusText.Text = "診斷模式：僅載入評測站。";
            return;
        }

        // 右：本地 Monaco 編輯器（掛 JS 橋接 + 本地 app:// 資源）
        Console.WriteLine("[BYOD] 步驟4：建立 editor 瀏覽器…");
        _editorBrowser = CreateBrowser();
        Console.WriteLine("[BYOD] 步驟5：註冊 JS 橋接…");
        _editorBrowser.RegisterJavascriptObject(_bridge, "editorBridge");
        Console.WriteLine("[BYOD] 步驟6：設定 editor Address（app://）…");
        _editorBrowser.Address = LocalAssetSchemeHandlerFactory.EditorStartUrl;
        EditorHost.Child = _editorBrowser;
        Console.WriteLine("[BYOD] 步驟7：editor 瀏覽器已就緒。");

        _detector.Record(CheatingEventType.Info, "防弊瀏覽器已啟動。");
        StatusText.Text = "安全環境已就緒 — 僅允許造訪指定評測網域。";
    }

    /// <summary>建立一個已完成安全加固的瀏覽器實例（共用白名單與偵測器）。</summary>
    private AvaloniaCefBrowser CreateBrowser()
    {
        var browser = new AvaloniaCefBrowser
        {
            RequestHandler = new WhitelistRequestHandler(_navigationWhitelist, _whitelist, _detector),
            LifeSpanHandler = new BlockPopupLifeSpanHandler(_navigationWhitelist, _detector),
            ContextMenuHandler = new NoContextMenuHandler(),
            DialogHandler = new BlockFileDialogHandler(_detector),
            KeyboardHandler = new LockdownKeyboardHandler(_detector)
            // DownloadHandler 保持 null → 一律不允許下載
        };

        ((LockdownKeyboardHandler)browser.KeyboardHandler).ProctorExitRequested += RequestProctorExit;
        return browser;
    }

    #region 失焦偵測

    private void NavigateToLocalPage(string page)
    {
        if (_judgeBrowser == null) return;
        // 設定 3 秒壓制視窗；Activated 不會重置此時間戳，所以 CEF 多次 Activated/Deactivated 均安全
        _suppressDeactivateUntil = DateTime.Now.AddSeconds(3);
        WarningOverlay.IsVisible = false;
        _judgeBrowser.Address = LocalAssetSchemeHandlerFactory.Scheme + "://local/" + page;
    }

    private void OnWindowDeactivated()
    {
        // local page 導航的 3 秒壓制視窗
        if (DateTime.Now < _suppressDeactivateUntil) return;
        // 防抖：已有 pending，不重複計時
        if (_deactivatePending) return;
        _deactivatePending = true;
        // 等 200ms；若 Activated 先到（CEF 內部焦點轉移）則 _deactivatePending=false → 取消
        Task.Delay(200).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (!_deactivatePending) return;   // Activated 已取消
            _deactivatePending = false;
            _detector.RecordDeactivation();
            WarningOverlay.IsVisible = true;
        }));
    }

    #endregion

    #region 編輯器橋接

    private void OnEditorReady()
    {
        Dispatcher.UIThread.Post(() =>
        {
            RunInEditor($"window.__setLanguage({JsString("cpp")})");
            RunInEditor($"window.__setCode({JsString(_currentCode)})");
        });
    }

    private void OnCheatReported(string reason)
    {
        _detector.Record(
            CheatingEventType.BlockedExternalPaste,
            "編輯器已封鎖外部剪貼簿 / 拖放操作。",
            reason);
    }

    /// <summary>判題頁複製的文字 → 推入編輯器的內部剪貼簿（允許貼上）。</summary>
    private void OnPageCopied(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _editorBrowser?.ExecuteJavaScript($"window.__setClipboard && window.__setClipboard({JsString(text)})");
        });
    }

    /// <summary>PDF 模式：背景由 Google Drive 下載題目 PDF，完成後設定快取路徑供 app://local/exam.pdf 提供。</summary>
    private void StartExamPdfDownload()
    {
        var source = Environment.GetEnvironmentVariable("BYOD_PDF_ID");
        if (string.IsNullOrWhiteSpace(source))
        {
            Console.WriteLine("[BYOD] 未設定題目 PDF 來源（請在專案根目錄的 pdf-id.txt 填入 Drive 資料夾/檔案連結）。");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var path = await PdfService.DownloadExamPdfAsync(source.Trim());
                LocalAssetSchemeHandlerFactory.PdfCachePath = path;
                Console.WriteLine($"[BYOD] 題目 PDF 已下載：{path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[BYOD] 題目 PDF 下載失敗：" + ex.Message);
            }
        });
    }

    private void RunInEditor(string script) => _editorBrowser?.ExecuteJavaScript(script);

    /// <summary>編譯 / 執行完成 → 把結果 JSON 推回編輯器頁面顯示。</summary>
    private void OnRunCompleted(string json)
    {
        Console.WriteLine("[BYOD-run] 推回頁面 __runResult");
        Dispatcher.UIThread.Post(() =>
        {
            _editorBrowser?.ExecuteJavaScript($"window.__runResult && window.__runResult({JsString(json)})");
        });
    }

    /// <summary>將字串安全轉為 JS 字面值（含引號）。</summary>
    private static string JsString(string value) => JsonSerializer.Serialize(value);

    #endregion

    #region 監考離開與關閉

    /// <summary>監考熱鍵：存檔 + 紀錄後退出（兩種模式皆退出）。</summary>
    private void RequestProctorExit()
    {
        Dispatcher.UIThread.Post(async () =>
        {
            await SubmitAsync(exit: true, "監考人員以熱鍵結束程式。");
        });
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // 一律阻擋（Alt+F4 等），維持 Kiosk 封閉；
        // 需要退出時一律走 Program.HardExit()，不經過視窗關閉
        e.Cancel = true;
    }

    private async Task ExportLogsAsync(string message = "防弊瀏覽器正常關閉。")
    {
        try
        {
            _detector.Record(CheatingEventType.Info, message);
            await _detector.ExportJsonAsync(_exportLogPath);
        }
        catch
        {
            // 導出失敗不阻擋關閉；即時 JSONL 仍保有紀錄
        }
    }

    #endregion

    #region 結束考試（繳交）

    private void ShowConfirm(bool show)
    {
        ConfirmPanel.IsVisible = show;
        EndExamButton.IsVisible = !show;
    }

    /// <summary>
    /// 繳交：儲存所有分頁至本場資料夾與 log 資料夾（OOP 另存桌面）並導出紀錄。
    /// exit = false（judge 模式）：存完維持畫面，學生直接關機；可再按一次覆蓋為最新版本。
    /// exit = true（OOP 模式 / 監考熱鍵）：存完立即結束程式。
    /// </summary>
    private async Task SubmitAsync(bool exit, string reason)
    {
        if (_ending) return;   // 防止連點「確定」或與監考熱鍵同時觸發
        _ending = true;
        ShowConfirm(false);
        StatusText.Text = "正在儲存…";
        _detector.Record(CheatingEventType.Info, reason);

        // 保底：5 秒後無論如何強制結束（含 CEF 子行程），避免存檔卡住而無法退出
        if (exit)
            _ = Task.Delay(5000).ContinueWith(_ => Program.HardExit());

        var saved = false;
        try
        {
            var filesJson = await CollectFilesJsonAsync();
            try { saved = await SaveAllFilesAsync(filesJson, Path.Combine(_sessionFolder, "files")); } catch { }
            try { await SaveAllFilesAsync(filesJson, _logCodeFolder); } catch { }
            if (_oopMode)
                try { await EditorBridge.DoSaveToDesktop(filesJson, ExamCategory); } catch { }
            try { await ExportLogsAsync(exit ? "防弊瀏覽器正常關閉。" : "已儲存作答（未關閉程式）。"); } catch { }
            Console.WriteLine($"[BYOD] 已繳交至：{_sessionFolder}，程式碼備份：{_logCodeFolder}");
        }
        catch { }

        // 不走 CEF 關閉流程（在 Linux 上會卡住），存檔完成即直接結束
        if (exit)
            Program.HardExit();

        StatusText.Text = saved
            ? $"作答已儲存（{DateTime.Now:HH:mm:ss}），請直接關機。"
            : "儲存失敗，請通知監考人員。";
        _ending = false;
    }

    /// <summary>
    /// 請編輯器經 JS 橋接回傳所有分頁 JSON（與「⬇ 下載」相同管道），最多等 2 秒；
    /// 逾時則用自動同步來的最新版本。不使用 EvaluateJavaScript：在 Linux 上常逾時而回傳空結果。
    /// </summary>
    private async Task<string> CollectFilesJsonAsync()
    {
        if (_editorBrowser == null) return _latestFilesJson;
        var waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _submitWaiter = waiter;
        try
        {
            RunInEditor("window.__submitFiles && window.__submitFiles()");
            var winner = await Task.WhenAny(waiter.Task, Task.Delay(2000));
            if (winner == waiter.Task) return waiter.Task.Result;
        }
        catch { }
        finally
        {
            _submitWaiter = null;
        }
        Console.WriteLine("[BYOD] 編輯器未即時回傳分頁，改用最近一次自動同步的內容。");
        return _latestFilesJson;
    }

    /// <summary>
    /// 將已收集的 filesJson 寫入 filesDir（先清空，使內容與目前分頁一致）。
    /// 收集失敗（空陣列）時不動既有檔案，回傳 false。
    /// </summary>
    private static async Task<bool> SaveAllFilesAsync(string json, string filesDir)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.GetArrayLength() == 0) return false;

        if (Directory.Exists(filesDir)) Directory.Delete(filesDir, recursive: true);
        Directory.CreateDirectory(filesDir);

        var index = 0;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            index++;
            var name    = el.TryGetProperty("name",    out var n) ? n.GetString() : null;
            var content = el.TryGetProperty("content", out var c) ? c.GetString() : string.Empty;

            var relPath = EditorBridge.SanitizeRelativePath(name, index);
            var fullPath = Path.Combine(filesDir, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, content ?? string.Empty);
        }
        return true;
    }

    #endregion
}
