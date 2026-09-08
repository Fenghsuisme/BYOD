namespace ByodKioskBrowser.Models;

/// <summary>
/// 作弊 / 安全事件的分類。序列化為 JSON 時以字串輸出，方便人工稽核。
/// </summary>
public enum CheatingEventType
{
    /// <summary>視窗失去焦點（例如 Alt+Tab 切換至本機 IDE）。</summary>
    WindowDeactivated,

    /// <summary>嘗試導航至白名單以外的網域，已被攔截。</summary>
    BlockedNavigation,

    /// <summary>嘗試開啟新視窗 / 新分頁 / 彈窗，已被攔截。</summary>
    BlockedNewWindow,

    /// <summary>嘗試自系統剪貼簿貼入外部內容，已被封鎖。</summary>
    BlockedExternalPaste,

    /// <summary>嘗試使用被禁用的瀏覽器快速鍵或開發者工具。</summary>
    BlockedShortcut,

    /// <summary>一般資訊性事件（例如程式啟動 / 關閉）。</summary>
    Info
}
