using System;
using System.Collections.Generic;
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
    /// filesJson：所有分頁 [{name, content, lang}] 的 JSON，用於將 .h 等輔助檔案寫入 workDir。
    /// </summary>
    public void runCode(string language, string code, string stdin, string? filesJson = null)
    {
        Console.WriteLine($"[BYOD-run] runCode 進入：lang={language} codeLen={(code?.Length ?? 0)} stdinLen={(stdin?.Length ?? 0)}");
        _ = Task.Run(async () =>
        {
            string json;
            try
            {
                var extraFiles = ParseExtraFiles(language, filesJson);
                var result = await _runner.RunAsync(language ?? "cpp", code ?? string.Empty, stdin ?? string.Empty, extraFiles);
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

    // 解析所有分頁 JSON，排除當前正在編譯的主檔（避免重複寫入），回傳輔助檔（.h 等）
    private static List<(string name, string content)> ParseExtraFiles(string? mainLang, string? filesJson)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrEmpty(filesJson)) return result;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(filesJson);
            var mainExt = (mainLang ?? "cpp") switch { "c" => ".c", "h" => ".h", _ => ".cpp" };
            bool mainSeen = false;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var content = el.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                var lang = el.TryGetProperty("lang", out var l) ? l.GetString() ?? "" : "";
                // 跳過第一個符合主語言副檔名的分頁（那是 runCode 的 code 參數，已傳入）
                var ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
                bool isMain = !mainSeen && (ext == mainExt || (mainLang == "cpp" && ext == ".cc"));
                if (isMain) { mainSeen = true; continue; }
                if (!string.IsNullOrEmpty(name)) result.Add((name, content));
            }
        }
        catch { /* 解析失敗不影響主流程 */ }
        return result;
    }
}
