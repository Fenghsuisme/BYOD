using System;

namespace ByodKioskBrowser.Browser;

/// <summary>
/// 註冊到判題網站頁面的 JS 物件（window.hostBridge）。
/// 用於把「在判題頁複製的文字」回報給 C#，再由 C# 推入編輯器的內部剪貼簿，
/// 讓學生能把題目 / 範例輸入複製進編輯器，同時仍封鎖來自 App 以外的系統剪貼簿貼入。
/// </summary>
public sealed class HostBridge
{
    /// <summary>使用者在判題頁執行複製時觸發，帶入被複製的文字。</summary>
    public event Action<string>? PageCopied;

    public void copyFromPage(string text) => PageCopied?.Invoke(text ?? string.Empty);
}
