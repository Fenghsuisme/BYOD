using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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

    /// <summary>執行中的即時輸出 (stream, text)；stream 為 stdout / stderr / info。</summary>
    public event Action<string, string>? RunOutput;

    /// <summary>編譯 / 執行完成，帶入結果 JSON（RunResult）。</summary>
    public event Action<string>? RunCompleted;

    /// <summary>分頁內容變更（去抖後），帶入所有大題分頁 JSON；供繳交時取用最新版本。</summary>
    public event Action<string>? FilesChanged;

    /// <summary>回應 C# 的 __submitFiles() 請求，帶入所有大題分頁 JSON。</summary>
    public event Action<string>? FilesSubmitted;

    /// <summary>考試類別（程式設計 / OOP&amp;DS），用於桌面存檔資料夾名稱。</summary>
    public string ExamCategory { get; set; } = "程式設計";

    /// <summary>儲存至桌面完成，帶入顯示訊息。</summary>
    public event Action<string>? SaveCompleted;

    // 以下為 JS 可呼叫的方法（皆為 void；結果另以事件回推，避免依賴回傳值 marshaling）
    public void ready() => Ready?.Invoke();

    public void codeChanged(string code) => CodeChanged?.Invoke(code ?? string.Empty);

    public void notifyCheat(string reason) => CheatReported?.Invoke(reason ?? string.Empty);

    public void filesChanged(string filesJson) => FilesChanged?.Invoke(filesJson ?? "[]");

    public void submitFiles(string filesJson) => FilesSubmitted?.Invoke(filesJson ?? "[]");

    /// <summary>
    /// 觸發編譯並執行（不等待回傳）。完成後以 RunCompleted 事件回推 JSON 結果，
    /// 由 C# 呼叫 window.__runResult 顯示。
    /// filesJson：同一大題所有分頁 [{name, content, lang}] 的 JSON，用於將 .h 等輔助檔案寫入 workDir。
    /// mainName：主檔（code）的分頁名稱，用來把它從輔助檔中排除。
    /// interactive：true 時 stdin 保持開啟，之後以 sendInput / closeInput 輸入；輸出以 RunOutput 即時推送。
    /// </summary>
    public void runCode(string language, string code, string stdin, string? filesJson, string? mainName, bool interactive)
    {
        Console.WriteLine($"[BYOD-run] runCode 進入：lang={language} codeLen={(code?.Length ?? 0)} stdinLen={(stdin?.Length ?? 0)}");
        _ = Task.Run(async () =>
        {
            string json;
            try
            {
                var extraFiles = ParseExtraFiles(mainName, filesJson);
                var result = await _runner.RunAsync(language ?? "cpp", code ?? string.Empty, stdin ?? string.Empty,
                    extraFiles, mainName, interactive, (stream, text) => RunOutput?.Invoke(stream, text));
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

    /// <summary>互動模式：送入一段輸入。</summary>
    public void sendInput(string text) => _runner.SendInput(text ?? string.Empty);

    /// <summary>互動模式：結束輸入（EOF）。</summary>
    public void closeInput() => _runner.CloseInput();

    /// <summary>停止目前的編譯 / 執行。</summary>
    public void stopRun() => _runner.Stop();

    /// <summary>
    /// 把所有分頁存至桌面 Desktop/<類別>_yyyy-MM-dd/（若同名資料夾已存在則直接覆蓋檔案）。
    /// JS 傳入格式：[{name, content}, ...]
    /// </summary>
    public void saveFiles(string filesJson)
    {
        _ = Task.Run(async () =>
        {
            string msg;
            try   { msg = await DoSaveToDesktop(filesJson, ExamCategory); }
            catch (Exception ex) { msg = "存檔失敗：" + ex.Message; }
            SaveCompleted?.Invoke(msg);
        });
    }

    /// <summary>將 filesJson 寫入 Desktop/&lt;類別&gt;_yyyy-MM-dd/，回傳狀態訊息。可供 C# 直接呼叫。</summary>
    internal static async Task<string> DoSaveToDesktop(string? filesJson, string category)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (string.IsNullOrEmpty(desktop))
            desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");

        var folderName = category + "_" + DateTime.Now.ToString("yyyy-MM-dd");
        var targetDir  = Path.Combine(desktop, folderName);
        Directory.CreateDirectory(targetDir); // 已存在也安全

        using var doc  = JsonDocument.Parse(filesJson ?? "[]");
        var count = 0;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var name    = el.TryGetProperty("name",    out var n) ? n.GetString() ?? "" : "";
            var content = el.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(name)) continue;

            var relPath = SanitizeRelativePath(name, count + 1);
            var fullPath = Path.Combine(targetDir, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, content);
            count++;
        }
        return $"已存至 Desktop/{folderName}/ （{count} 個檔案）";
    }

    /// <summary>
    /// 處理 "Q1/main.cpp" 格式的相對路徑：只允許一層子目錄（大題名稱），防止路徑穿越。
    /// 無斜線時退化為純檔名處理。
    /// </summary>
    internal static string SanitizeRelativePath(string? name, int index)
    {
        if (string.IsNullOrWhiteSpace(name)) return $"file{index}.txt";
        name = name.Replace('\\', '/').Trim('/');
        var slash = name.IndexOf('/');
        if (slash > 0 && slash < name.Length - 1)
        {
            var dir  = SanitizeFileName(name[..slash],      0);
            var file = SanitizeFileName(name[(slash + 1)..], index);
            if (!string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(file))
                return Path.Combine(dir, file);
        }
        return SanitizeFileName(name, index);
    }

    private static string SanitizeFileName(string? name, int index)
    {
        if (string.IsNullOrWhiteSpace(name)) return $"file{index}.txt";
        name = Path.GetFileName(name);
        foreach (var ch in Path.GetInvalidFileNameChars())
            name = name.Replace(ch, '_');
        // "." / ".." 會被 Path.Combine 解讀為目前 / 上層目錄（路徑穿越）
        if (string.IsNullOrWhiteSpace(name) || name.Trim('.').Length == 0) return $"file{index}.txt";
        return name;
    }

    // 解析同一大題的分頁 JSON，排除主檔（已由 runCode 的 code 參數傳入），回傳輔助檔（.h / 其他 .cpp 等）
    private static List<(string name, string content)> ParseExtraFiles(string? mainName, string? filesJson)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrEmpty(filesJson)) return result;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(filesJson);
            bool mainSeen = false;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var content = el.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                if (!mainSeen && name == mainName) { mainSeen = true; continue; }
                if (!string.IsNullOrEmpty(name)) result.Add((name, content));
            }
        }
        catch { /* 解析失敗不影響主流程 */ }
        return result;
    }
}
