using System;

namespace ByodKioskBrowser.Browser;

/// <summary>
/// 註冊到編輯器頁面的 JS 物件（透過 RegisterJavascriptObject 掛為 window.editorBridge）。
/// JS 端以 camelCase 呼叫：editorBridge.ready() / codeChanged(code) / notifyCheat(reason)。
/// 這些方法可能在 CEF 執行緒被呼叫，事件訂閱者需自行切回 UI 執行緒。
/// </summary>
public sealed class EditorBridge
{
    /// <summary>編輯器初始化完成，可回送初始程式碼與語言。</summary>
    public event Action? Ready;

    /// <summary>編輯器內容變更（去抖後）。</summary>
    public event Action<string>? CodeChanged;

    /// <summary>編輯器回報一次被封鎖的外部剪貼簿 / 拖放行為。</summary>
    public event Action<string>? CheatReported;

    // 以下為 JS 可呼叫的方法（回傳 void；CefGlue 會包成 resolved promise）
    public void ready() => Ready?.Invoke();

    public void codeChanged(string code) => CodeChanged?.Invoke(code ?? string.Empty);

    public void notifyCheat(string reason) => CheatReported?.Invoke(reason ?? string.Empty);
}
