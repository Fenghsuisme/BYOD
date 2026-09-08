using System;
using System.IO;
using System.Threading.Tasks;
using ByodKioskBrowser.Interfaces;
using ByodKioskBrowser.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ByodKioskBrowser.Services;

/// <summary>
/// WebView2 安全加固服務。
/// 集中負責：環境建立（含自訂 user data folder 以達零殘留）、
/// CoreWebView2Settings 加固、導航白名單攔截、彈窗 / 新視窗攔截。
/// </summary>
public sealed class SecureBrowserService
{
    private readonly IUrlWhitelistValidator _whitelist;
    private readonly ICheatingDetector _detector;

    public SecureBrowserService(IUrlWhitelistValidator whitelist, ICheatingDetector detector)
    {
        _whitelist = whitelist ?? throw new ArgumentNullException(nameof(whitelist));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
    }

    /// <summary>
    /// 以指定的 user data folder 建立 WebView2 環境。
    /// 將快取導向臨時資料夾，配合關閉時清除即可達成「零殘留」。
    /// </summary>
    public static Task<CoreWebView2Environment> CreateEnvironmentAsync(string userDataFolder)
    {
        Directory.CreateDirectory(userDataFolder);

        var options = new CoreWebView2EnvironmentOptions
        {
            // 關閉可能外連的功能，收斂封閉環境。
            AdditionalBrowserArguments = "--disable-features=msSmartScreenProtection,EdgeCollections --disable-background-networking"
        };

        return CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder,
            options: options);
    }

    /// <summary>
    /// 初始化並加固一個 WebView2 控制項。
    /// </summary>
    /// <param name="webView">目標控制項。</param>
    /// <param name="environment">共用的 WebView2 環境。</param>
    /// <param name="isEditor">是否為本地 Monaco 編輯器（僅允許導航至本地虛擬主機）。</param>
    /// <param name="assetsFolder">isEditor 為 true 時，掛載至虛擬主機的本地資源資料夾。</param>
    public async Task HardenAsync(
        WebView2 webView,
        CoreWebView2Environment environment,
        bool isEditor,
        string? assetsFolder = null)
    {
        await webView.EnsureCoreWebView2Async(environment);
        var core = webView.CoreWebView2;

        ApplySettings(core.Settings);

        if (isEditor)
        {
            if (string.IsNullOrEmpty(assetsFolder))
            {
                throw new ArgumentException("編輯器 WebView 需要提供 assetsFolder。", nameof(assetsFolder));
            }

            // 本地 Monaco 靜態資源掛載至 appassets.local，執行期不外連 CDN。
            core.SetVirtualHostNameToFolderMapping(
                UrlWhitelistValidator.LocalAppHost,
                assetsFolder,
                CoreWebView2HostResourceAccessKind.Allow);
        }

        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.WindowCloseRequested += OnWindowCloseRequested;
        core.ContextMenuRequested += OnContextMenuRequested;
    }

    private static void ApplySettings(CoreWebView2Settings settings)
    {
        settings.AreDevToolsEnabled = false;                 // 禁止 F12 開發者工具
        settings.AreDefaultContextMenusEnabled = false;      // 禁用右鍵選單
        settings.AreBrowserAcceleratorKeysEnabled = false;   // 阻斷 Ctrl+N/T/W/R、F5 等
        settings.IsStatusBarEnabled = false;                 // 隱藏狀態列（避免洩漏 URL）
        settings.IsZoomControlEnabled = false;               // 禁止 Ctrl+滾輪縮放
        settings.AreDefaultScriptDialogsEnabled = true;      // 保留必要的 alert/confirm
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsSwipeNavigationEnabled = false;           // 禁止觸控左右滑動上一頁/下一頁
        settings.IsPinchZoomEnabled = false;
        settings.AreHostObjectsAllowed = false;              // 不對頁面暴露 host object
    }

    /// <summary>導航白名單攔截：不在清單內的請求一律取消並記錄。</summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_whitelist.IsAllowed(e.Uri))
        {
            return;
        }

        e.Cancel = true;
        _detector.Record(
            CheatingEventType.BlockedNavigation,
            "已攔截未授權的導航請求。",
            e.Uri);
    }

    /// <summary>
    /// 彈窗 / 新視窗攔截：一律 Handled = true。
    /// 若目標在白名單內，強制由原 WebView 導航；否則直接丟棄並記錄。
    /// </summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;

        if (sender is CoreWebView2 core && _whitelist.IsAllowed(e.Uri))
        {
            // 白名單內：在同一實例導航，杜絕另開視窗/分頁。
            core.Navigate(e.Uri);
            return;
        }

        _detector.Record(
            CheatingEventType.BlockedNewWindow,
            "已攔截開啟新視窗 / 新分頁 / 彈窗的嘗試。",
            e.Uri);
    }

    /// <summary>阻擋頁面 JS 透過 window.close() 關閉視窗。</summary>
    private void OnWindowCloseRequested(object? sender, object e)
    {
        _detector.Record(
            CheatingEventType.Info,
            "已忽略頁面要求關閉視窗的請求。");
        // 不做任何事即可阻止關閉。
    }

    /// <summary>雙重保險：即使設定被繞過，也強制不顯示自訂內容選單。</summary>
    private static void OnContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        e.Handled = true;
    }
}
