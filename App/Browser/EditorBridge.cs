using System;
using System.Threading.Tasks;
using ByodKioskBrowser.Execution;

namespace ByodKioskBrowser.Browser;

/// <summary>
/// 註冊到編輯器頁面的 JS 物件（透過 RegisterJavascriptObject 掛為 window.editorBridge）。
/// JS 端以 camelCase 呼叫：editorBridge.ready() / codeChanged(code) / notifyCheat(reason) / runCode(...)。
/// 事件方法可能在 CEF 執行緒被呼叫，事件訂閱者需自行切回 UI 執行緒。
/// runCode 為長時間作業，需以背景執行緒的呼叫處理器執行（見 MainWindow 註冊）。
/// </summary>
public sealed class EditorBridge
{
    private readonly CodeRunnerService _runner;

    public EditorBridge(CodeRunnerService runner)
    {
        _runner = runner;
    }

    /// <summary>編輯器初始化完成，可回送初始程式碼與語言。</summary>
    public event Action? Ready;

    /// <summary>編輯器內容變更（去抖後）。</summary>
    public event Action<string>? CodeChanged;

    /// <summary>編輯器回報一次被封鎖的外部剪貼簿 / 拖放行為。</summary>
    public event Action<string>? CheatReported;

    /// <summary>編譯 / 執行被觸發（供 UI 顯示狀態或記錄）。</summary>
    public event Action<string>? RunStarted;

    // 以下為 JS 可呼叫的方法
    public void ready() => Ready?.Invoke();

    public void codeChanged(string code) => CodeChanged?.Invoke(code ?? string.Empty);

    public void notifyCheat(string reason) => CheatReported?.Invoke(reason ?? string.Empty);

    /// <summary>
    /// 編譯並執行程式碼，回傳 JSON 結果字串（RunResult）。
    /// JS 端：const json = await editorBridge.runCode(lang, code, stdin);
    /// </summary>
    public async Task<string> runCode(string language, string code, string stdin)
    {
        RunStarted?.Invoke(language ?? string.Empty);
        var result = await _runner.RunAsync(language ?? "cpp", code ?? string.Empty, stdin ?? string.Empty);
        return result.ToJson();
    }
}
