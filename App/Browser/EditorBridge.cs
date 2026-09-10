using System;
using System.Threading.Tasks;
using ByodKioskBrowser.Execution;

namespace ByodKioskBrowser.Browser;

/// <summary>
/// 註冊到編輯器頁面的 JS 物件（透過 RegisterJavascriptObject 掛為 window.editorBridge）。
/// JS 端以 camelCase 呼叫：ready() / codeChanged(code) / notifyCheat(reason) / runCode(...)。
/// 事件方法可能在 CEF 執行緒被呼叫，事件訂閱者需自行切回 UI 執行緒。
/// runCode 為 fire-and-forget：背景編譯執行完成後，透過 RunCompleted 事件把結果 JSON 推回頁面。
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

    /// <summary>編譯 / 執行完成，帶入結果 JSON（RunResult）。</summary>
    public event Action<string>? RunCompleted;

    // 以下為 JS 可呼叫的方法（皆為 void；結果另以事件回推，避免依賴回傳值 marshaling）
    public void ready() => Ready?.Invoke();

    public void codeChanged(string code) => CodeChanged?.Invoke(code ?? string.Empty);

    public void notifyCheat(string reason) => CheatReported?.Invoke(reason ?? string.Empty);

    /// <summary>
    /// 觸發編譯並執行（不等待回傳）。完成後以 RunCompleted 事件回推 JSON 結果，
    /// 由 C# 呼叫 window.__runResult 顯示。
    /// </summary>
    public void runCode(string language, string code, string stdin)
    {
        Console.WriteLine($"[BYOD-run] runCode 進入：lang={language} codeLen={(code?.Length ?? 0)} stdinLen={(stdin?.Length ?? 0)}");
        _ = Task.Run(async () =>
        {
            string json;
            try
            {
                var result = await _runner.RunAsync(language ?? "cpp", code ?? string.Empty, stdin ?? string.Empty);
                json = result.ToJson();
            }
            catch (Exception ex)
            {
                json = new RunResult { Ok = false, Phase = "error", Message = "執行器例外：" + ex.Message }.ToJson();
            }

            Console.WriteLine($"[BYOD-run] runCode 完成，推回結果（{json.Length} 字元）");
            RunCompleted?.Invoke(json);
        });
    }
}
