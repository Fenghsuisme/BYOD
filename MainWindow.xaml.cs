using System;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ByodKioskBrowser.Interfaces;
using ByodKioskBrowser.Models;
using ByodKioskBrowser.Services;
using Microsoft.Web.WebView2.Core;

namespace ByodKioskBrowser;

public partial class MainWindow : Window
{
    private const string JudgeStartUrl = "https://judge.gai.tw/";
    private const string EditorStartUrl = "https://appassets.local/index.html";

    private readonly IUrlWhitelistValidator _whitelist = new UrlWhitelistValidator();
    private ICheatingDetector _detector = null!;
    private SecureBrowserService _browserService = null!;
    private CoreWebView2Environment? _environment;

    private App AppInstance => (App)Application.Current;

    // 目前編輯器內的程式碼（記憶體暫存，供後續提交或稽核）。
    private string _currentCode = string.Empty;

    private bool _proctorExit;
    private bool _logExported;

    public MainWindow()
    {
        InitializeComponent();

        _detector = new CheatingDetector(AppInstance.LiveLogPath);

        Loaded += OnLoaded;
        Deactivated += OnWindowDeactivated;
        Activated += OnWindowActivated;
        Closing += OnWindowClosing;
        PreviewKeyDown += OnPreviewKeyDown;

        // 全域封鎖 WPF 層級的貼上命令（僅允許編輯器內部暫存區複製貼上）。
        CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Paste,
            (_, e) => e.Handled = true,
            (_, e) => { e.CanExecute = false; e.Handled = true; }));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _browserService = new SecureBrowserService(_whitelist, _detector);
            _environment = await SecureBrowserService.CreateEnvironmentAsync(AppInstance.UserDataFolder);

            // 左：評測網站（加固 + 白名單）
            await _browserService.HardenAsync(JudgeWebView, _environment, isEditor: false);

            // 右：本地 Monaco 編輯器（掛載本地資源）
            await _browserService.HardenAsync(
                EditorWebView, _environment, isEditor: true, assetsFolder: AppInstance.WebAppFolder);

            EditorWebView.CoreWebView2.WebMessageReceived += OnEditorMessageReceived;

            JudgeWebView.CoreWebView2.Navigate(JudgeStartUrl);
            EditorWebView.CoreWebView2.Navigate(EditorStartUrl);

            _detector.Record(CheatingEventType.Info, "防弊瀏覽器已啟動。");
            StatusText.Text = "安全環境已就緒 — 僅允許造訪指定評測網域。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "初始化失敗：" + ex.Message;
            MessageBox.Show(
                "WebView2 初始化失敗，請確認已安裝 WebView2 Runtime。\n\n" + ex,
                "初始化錯誤",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    #region 失焦偵測

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        var count = _detector.RecordDeactivation();

        DeactivationCountText.Text = count.ToString();
        OverlayCountText.Text = $"本次已累計失焦 {count} 次";
        WarningOverlay.Visibility = Visibility.Visible;
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        WarningOverlay.Visibility = Visibility.Collapsed;
    }

    #endregion

    #region 編輯器 WebMessage 雙向同步

    private void OnEditorMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try
        {
            json = e.TryGetWebMessageAsString();
        }
        catch
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp))
            {
                return;
            }

            switch (typeProp.GetString())
            {
                case "ready":
                    // 編輯器就緒：回送初始程式碼與語言設定。
                    PostToEditor(new { type = "setLanguage", language = "cpp" });
                    PostToEditor(new { type = "setCode", code = _currentCode });
                    break;

                case "codeChanged":
                    if (root.TryGetProperty("code", out var codeProp))
                    {
                        _currentCode = codeProp.GetString() ?? string.Empty;
                    }
                    break;

                case "cheat":
                    var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
                    _detector.Record(
                        CheatingEventType.BlockedExternalPaste,
                        "編輯器已封鎖外部剪貼簿操作。",
                        reason);
                    break;
            }
        }
        catch (JsonException)
        {
            // 非預期訊息，忽略。
        }
    }

    private void PostToEditor(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        EditorWebView.CoreWebView2?.PostWebMessageAsString(json);
    }

    #endregion

    #region 快速鍵封鎖與監考離開

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;

        // 監考人員離開熱鍵：Ctrl+Alt+Shift+Q
        if (e.Key == Key.Q &&
            mods == (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift))
        {
            _proctorExit = true;
            _detector.Record(CheatingEventType.Info, "監考人員以熱鍵結束程式。");
            Close();
            e.Handled = true;
            return;
        }

        // 阻擋 Alt+F4 及其他透過系統鍵觸發的關閉行為。
        if (e.SystemKey == Key.F4 && mods.HasFlag(ModifierKeys.Alt))
        {
            _detector.Record(CheatingEventType.BlockedShortcut, "已攔截 Alt+F4 關閉視窗。");
            e.Handled = true;
        }
    }

    #endregion

    #region 關閉與日誌導出

    private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 非監考熱鍵觸發的關閉一律阻擋，維持 Kiosk 封閉狀態。
        if (!_proctorExit)
        {
            e.Cancel = true;
            return;
        }

        if (!_logExported)
        {
            e.Cancel = true;              // 先取消，待日誌導出完成後再真正關閉。
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
            await _detector.ExportJsonAsync(AppInstance.ExportLogPath);
        }
        catch
        {
            // 導出失敗不阻擋關閉；即時 JSONL 日誌仍保有紀錄。
        }
    }

    #endregion
}
