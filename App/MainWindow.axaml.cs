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
    private bool _proctorExit;
    private bool _logExported;

    // 診斷用：BYOD_WINDOWED=1 → 一般視窗模式（有邊框、不置頂），方便確認視窗能否顯示
    private readonly bool _windowed = Environment.GetEnvironmentVariable("BYOD_WINDOWED") == "1";

    // 本次考試工作階段資料夾（每次啟動新建）；檔案與紀錄都存在此
    private readonly string _sessionFolder;
    private readonly string _liveLogPath;
    private readonly string _exportLogPath;

    public MainWindow()
    {
        InitializeComponent();

        // 資料夾以日期時間為名（HH-mm-ss 避免同日多場衝突）
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        _sessionFolder = Path.Combine(AppContext.BaseDirectory, "exams", stamp);
        Directory.CreateDirectory(_sessionFolder);
        Console.WriteLine($"[BYOD] 本場考試資料夾：{_sessionFolder}");
        _liveLogPath = Path.Combine(_sessionFolder, "events.jsonl");
        _exportLogPath = Path.Combine(_sessionFolder, "events.json");
        _detector = new CheatingDetector(_liveLogPath);

        _bridge = new EditorBridge(_runner);
        _bridge.Ready += OnEditorReady;
        _bridge.CodeChanged += code => _currentCode = code;
        _bridge.CheatReported += OnCheatReported;
        _bridge.RunCompleted += OnRunCompleted;

        // 判題頁複製 → 推入編輯器內部剪貼簿
        _hostBridge.PageCopied += OnPageCopied;

        Activated += (_, _) => Dispatcher.UIThread.Post(() => WarningOverlay.IsVisible = false);
        Deactivated += (_, _) => Dispatcher.UIThread.Post(OnWindowDeactivated);

        // 結束考試按鈕與確認遮罩
        EndExamButton.Click += (_, _) => ConfirmOverlay.IsVisible = true;
        ConfirmNoButton.Click += (_, _) => ConfirmOverlay.IsVisible = false;
        ConfirmYesButton.Click += async (_, _) => await EndExamAsync();

        // 判題站導覽鍵（作用於左側瀏覽器）
        BackButton.Click += (_, _) => { if (_judgeBrowser?.CanGoBack == true) _judgeBrowser.GoBack(); };
        ForwardButton.Click += (_, _) => { if (_judgeBrowser?.CanGoForward == true) _judgeBrowser.GoForward(); };
        ReloadButton.Click += (_, _) => _judgeBrowser?.Reload();
        HomeButton.Click += (_, _) => { if (_judgeBrowser != null) _judgeBrowser.Address = JudgeStartUrl; };

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
        Console.WriteLine("[BYOD] 步驟2：設定 judge Address…");
        _judgeBrowser.Address = JudgeStartUrl;
        JudgeHost.Child = _judgeBrowser;
        Console.WriteLine("[BYOD] 步驟3：judge 瀏覽器已就緒。");

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
            RequestHandler = new WhitelistRequestHandler(_whitelist, _detector),
            LifeSpanHandler = new BlockPopupLifeSpanHandler(_whitelist, _detector),
            ContextMenuHandler = new NoContextMenuHandler(),
            KeyboardHandler = new LockdownKeyboardHandler(_detector)
            // DownloadHandler 保持 null → 一律不允許下載
        };

        ((LockdownKeyboardHandler)browser.KeyboardHandler).ProctorExitRequested += RequestProctorExit;
        return browser;
    }

    #region 失焦偵測

    private void OnWindowDeactivated()
    {
        // 仍記錄失焦事件（寫入紀錄），但不再於畫面顯示計數
        _detector.RecordDeactivation();
        WarningOverlay.IsVisible = true;
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

    private void RequestProctorExit()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _proctorExit = true;
            _detector.Record(CheatingEventType.Info, "監考人員以熱鍵結束程式。");
            Close();
        });
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // 非監考熱鍵觸發的關閉一律阻擋，維持 Kiosk 封閉
        if (!_proctorExit)
        {
            e.Cancel = true;
            return;
        }

        if (!_logExported)
        {
            e.Cancel = true;
            await ExportLogsAsync();
            _logExported = true;
            Close();
        }
    }

    private async Task ExportLogsAsync()
    {
        try
        {
            _detector.Record(CheatingEventType.Info, "防弊瀏覽器正常關閉。");
            await _detector.ExportJsonAsync(_exportLogPath);
        }
        catch
        {
            // 導出失敗不阻擋關閉；即時 JSONL 仍保有紀錄
        }
    }

    #endregion

    #region 結束考試（繳交）

    /// <summary>學生按下「結束考試」：儲存所有分頁檔案與紀錄至本次資料夾，然後關閉。</summary>
    private async Task EndExamAsync()
    {
        ConfirmOverlay.IsVisible = false;
        StatusText.Text = "正在儲存與繳交…";

        _detector.Record(CheatingEventType.Info, "學生按下結束考試，開始繳交。");

        try
        {
            await SaveAllFilesAsync();
        }
        catch
        {
            // 存檔失敗不阻擋後續流程
        }

        await ExportLogsAsync();
        _logExported = true;
        Console.WriteLine($"[BYOD] 已繳交至：{_sessionFolder}");
        _proctorExit = true; // 放行關閉
        Close();
    }

    /// <summary>向編輯器索取所有分頁內容，逐一寫入本次資料夾的 files 子目錄（由 C# 存檔，編輯器不碰檔案系統）。</summary>
    private async Task SaveAllFilesAsync()
    {
        if (_editorBrowser == null)
        {
            return;
        }

        string json;
        try
        {
            json = await _editorBrowser.EvaluateJavaScript<string>(
                "window.__collectFiles ? window.__collectFiles() : '[]'");
        }
        catch
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        var filesDir = Path.Combine(_sessionFolder, "files");
        Directory.CreateDirectory(filesDir);

        using var doc = JsonDocument.Parse(json);
        var index = 0;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            index++;
            var name = el.TryGetProperty("name", out var n) ? n.GetString() : null;
            var content = el.TryGetProperty("content", out var c) ? c.GetString() : string.Empty;

            var safeName = SanitizeFileName(name, index);
            var path = Path.Combine(filesDir, safeName);
            if (File.Exists(path))
            {
                // 檔名重複時附加序號避免覆蓋
                path = Path.Combine(filesDir,
                    Path.GetFileNameWithoutExtension(safeName) + "_" + index + Path.GetExtension(safeName));
            }

            await File.WriteAllTextAsync(path, content ?? string.Empty);
        }
    }

    private static string SanitizeFileName(string? name, int index)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return $"file{index}.txt";
        }

        name = Path.GetFileName(name); // 去除任何目錄成分，防目錄穿越
        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(ch, '_');
        }

        return string.IsNullOrWhiteSpace(name) ? $"file{index}.txt" : name;
    }

    #endregion
}
