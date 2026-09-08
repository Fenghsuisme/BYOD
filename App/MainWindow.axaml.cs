using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ByodKioskBrowser.Browser;
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
    private readonly EditorBridge _bridge = new();

    private AvaloniaCefBrowser? _judgeBrowser;
    private AvaloniaCefBrowser? _editorBrowser;

    private string _currentCode = string.Empty;
    private bool _proctorExit;
    private bool _logExported;

    // 診斷用：BYOD_WINDOWED=1 → 一般視窗模式（有邊框、不置頂），方便確認視窗能否顯示
    private readonly bool _windowed = Environment.GetEnvironmentVariable("BYOD_WINDOWED") == "1";

    // 稽核日誌路徑（保留於執行檔旁 logs 資料夾）
    private readonly string _liveLogPath;
    private readonly string _exportLogPath;

    public MainWindow()
    {
        InitializeComponent();

        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _liveLogPath = Path.Combine(logDir, $"events_{stamp}.jsonl");
        _exportLogPath = Path.Combine(logDir, $"events_{stamp}.json");
        _detector = new CheatingDetector(_liveLogPath);

        _bridge.Ready += OnEditorReady;
        _bridge.CodeChanged += code => _currentCode = code;
        _bridge.CheatReported += OnCheatReported;

        Activated += (_, _) => Dispatcher.UIThread.Post(() => WarningOverlay.IsVisible = false);
        Deactivated += (_, _) => Dispatcher.UIThread.Post(OnWindowDeactivated);

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

    private void OnOpened(object? sender, EventArgs e)
    {
        Console.WriteLine($"[BYOD] Window Opened. windowed={_windowed} " +
            $"DISPLAY={Environment.GetEnvironmentVariable("DISPLAY")} " +
            $"WAYLAND_DISPLAY={Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")} " +
            $"bounds={Bounds}");

        if (!_windowed)
        {
            // 版面完成後再次套用全螢幕（GNOME Wayland/部分 WM 需顯示後才生效）
            Dispatcher.UIThread.Post(() =>
            {
                WindowState = WindowState.FullScreen;
                Topmost = true;
                Console.WriteLine($"[BYOD] 重新套用全螢幕。bounds={Bounds} state={WindowState}");
            }, DispatcherPriority.Background);
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
        var count = _detector.RecordDeactivation();
        DeactivationCountText.Text = count.ToString();
        OverlayCountText.Text = $"本次已累計失焦 {count} 次";
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

    private void RunInEditor(string script) => _editorBrowser?.ExecuteJavaScript(script);

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
}
