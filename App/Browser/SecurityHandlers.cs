using ByodKioskBrowser.Interfaces;
using ByodKioskBrowser.Models;
using Xilium.CefGlue;
using Xilium.CefGlue.Common.Handlers;

namespace ByodKioskBrowser.Browser;

/// <summary>
/// 導航白名單攔截。比對 URL；本地 app:// scheme 一律放行，其餘不在白名單即取消並記錄。
/// </summary>
public sealed class WhitelistRequestHandler : RequestHandler
{
    private readonly IUrlWhitelistValidator _whitelist;
    private readonly ICheatingDetector _detector;

    public WhitelistRequestHandler(IUrlWhitelistValidator whitelist, ICheatingDetector detector)
    {
        _whitelist = whitelist;
        _detector = detector;
    }

    protected override bool OnBeforeBrowse(
        CefBrowser browser, CefFrame frame, CefRequest request, bool userGesture, bool isRedirect)
    {
        var url = request.Url;

        // 本地編輯器資源放行
        if (url.StartsWith(LocalAssetSchemeHandlerFactory.Scheme + "://", System.StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_whitelist.IsAllowed(url))
        {
            return false; // 允許導航
        }

        _detector.Record(CheatingEventType.BlockedNavigation, "已攔截未授權的導航請求。", url);
        return true; // 取消導航
    }
}

/// <summary>
/// 彈窗 / 新視窗攔截。一律不開新視窗；白名單目標改由原 browser 導航，否則丟棄並記錄。
/// </summary>
public sealed class BlockPopupLifeSpanHandler : LifeSpanHandler
{
    private readonly IUrlWhitelistValidator _whitelist;
    private readonly ICheatingDetector _detector;

    public BlockPopupLifeSpanHandler(IUrlWhitelistValidator whitelist, ICheatingDetector detector)
    {
        _whitelist = whitelist;
        _detector = detector;
    }

    protected override bool OnBeforePopup(
        CefBrowser browser, CefFrame frame, string targetUrl, string targetFrameName,
        CefWindowOpenDisposition targetDisposition, bool userGesture, CefPopupFeatures popupFeatures,
        CefWindowInfo windowInfo, ref CefClient client, CefBrowserSettings settings,
        ref CefDictionaryValue extraInfo, ref bool noJavascriptAccess)
    {
        if (_whitelist.IsAllowed(targetUrl))
        {
            // 白名單內：於同一實例導航，杜絕另開視窗 / 分頁
            browser.GetMainFrame()?.LoadUrl(targetUrl);
        }
        else
        {
            _detector.Record(CheatingEventType.BlockedNewWindow, "已攔截開啟新視窗 / 分頁 / 彈窗的嘗試。", targetUrl);
        }

        return true; // 阻止建立彈窗視窗
    }
}

/// <summary>清空右鍵選單模型，等同禁用內容選單。</summary>
public sealed class NoContextMenuHandler : ContextMenuHandler
{
    protected override void OnBeforeContextMenu(
        CefBrowser browser, CefFrame frame, CefContextMenuParams state, CefMenuModel model)
    {
        model.Clear();
    }
}

/// <summary>
/// 封鎖開發者工具與危險快速鍵（F12、重新整理、新視窗/分頁、檢視原始碼、列印等）。
/// </summary>
public sealed class LockdownKeyboardHandler : KeyboardHandler
{
    private readonly ICheatingDetector _detector;

    /// <summary>偵測到監考離開熱鍵（Ctrl+Alt+Shift+Q）時觸發。</summary>
    public event System.Action? ProctorExitRequested;

    public LockdownKeyboardHandler(ICheatingDetector detector)
    {
        _detector = detector;
    }

    // Windows Virtual-Key codes（CEF 於各平台統一提供）
    private const int VK_F5 = 0x74;
    private const int VK_F12 = 0x7B;
    private const int VK_R = 0x52;
    private const int VK_I = 0x49;
    private const int VK_J = 0x4A;
    private const int VK_C = 0x43;
    private const int VK_N = 0x4E;
    private const int VK_T = 0x54;
    private const int VK_W = 0x57;
    private const int VK_U = 0x55;
    private const int VK_P = 0x50;
    private const int VK_Q = 0x51;

    protected override bool OnPreKeyEvent(
        CefBrowser browser, CefKeyEvent keyEvent, nint osEvent, out bool isKeyboardShortcut)
    {
        isKeyboardShortcut = false;

        if (keyEvent.EventType != CefKeyEventType.RawKeyDown && keyEvent.EventType != CefKeyEventType.KeyDown)
        {
            return false;
        }

        var ctrl = keyEvent.Modifiers.HasFlag(CefEventFlags.ControlDown)
                   || keyEvent.Modifiers.HasFlag(CefEventFlags.CommandDown); // macOS 的 Cmd
        var shift = keyEvent.Modifiers.HasFlag(CefEventFlags.ShiftDown);
        var alt = keyEvent.Modifiers.HasFlag(CefEventFlags.AltDown);
        var key = keyEvent.WindowsKeyCode;

        // 監考離開熱鍵：Ctrl+Alt+Shift+Q
        if (ctrl && alt && shift && key == VK_Q)
        {
            ProctorExitRequested?.Invoke();
            return true;
        }

        bool blocked =
            key == VK_F12 ||                                   // 開發者工具
            key == VK_F5 ||                                    // 重新整理
            (ctrl && key == VK_R) ||                           // 重新整理
            (ctrl && shift && (key == VK_I || key == VK_J || key == VK_C)) || // DevTools
            (ctrl && (key == VK_N || key == VK_T || key == VK_W)) ||          // 新視窗 / 分頁 / 關閉
            (ctrl && key == VK_U) ||                           // 檢視原始碼
            (ctrl && key == VK_P);                             // 列印

        if (blocked)
        {
            _detector.Record(CheatingEventType.BlockedShortcut, "已攔截被禁用的快速鍵。",
                $"key=0x{key:X2} ctrl={ctrl} shift={shift}");
            return true; // 事件已處理，不傳遞給頁面
        }

        return false;
    }
}
