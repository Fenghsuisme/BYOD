using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ByodKioskBrowser.Execution;

/// <summary>編譯 / 執行的結果，序列化為 JSON 回傳給編輯器頁面。</summary>
public sealed class RunResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    /// <summary>compile | run | error</summary>
    [JsonPropertyName("phase")] public string Phase { get; set; } = "error";
    [JsonPropertyName("stdout")] public string Stdout { get; set; } = string.Empty;
    [JsonPropertyName("stderr")] public string Stderr { get; set; } = string.Empty;
    [JsonPropertyName("exitCode")] public int ExitCode { get; set; }
    [JsonPropertyName("timeMs")] public long TimeMs { get; set; }
    [JsonPropertyName("timedOut")] public bool TimedOut { get; set; }
    [JsonPropertyName("stopped")] public bool Stopped { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);
}

/// <summary>
/// 受控的本地 C / C++ 編譯執行器。將程式碼寫入暫存目錄、以 gcc/g++ 編譯，
/// 執行後擷取輸出並刪除暫存（零殘留）。不提供任意 shell，僅固定呼叫編譯器與產生的執行檔。
/// 兩種輸入方式：
///   批次：執行前已備妥 stdin → 一次寫入並關閉（程式讀到 EOF）
///   互動：stdin 保持開啟，執行中以 SendInput 送入、CloseInput 送出 EOF
/// 無窮迴圈以「CPU 時間」判定（等待輸入不耗 CPU，不會誤殺）；另有牆鐘時間上限。
/// </summary>
public sealed class CodeRunnerService
{
    private readonly int _compileTimeoutMs;
    private readonly int _cpuLimitMs;
    private readonly int _batchWallLimitMs;
    private readonly int _interactiveWallLimitMs;

    private readonly object _sync = new();
    private RunControl? _current;

    public CodeRunnerService(int compileTimeoutMs = 20000, int cpuLimitMs = 5000,
        int batchWallLimitMs = 10000, int interactiveWallLimitMs = 600000)
    {
        _compileTimeoutMs = compileTimeoutMs;
        _cpuLimitMs = cpuLimitMs;
        _batchWallLimitMs = batchWallLimitMs;
        _interactiveWallLimitMs = interactiveWallLimitMs;
    }

    // 每次執行保留的最大輸出字元數；無窮輸出迴圈不致吃光記憶體或卡死編輯器頁面
    private const int MaxOutputChars = 1_000_000;

    /// <summary>互動模式：送入一段輸入（編譯期間送入的會排隊，程式啟動後依序寫入）。</summary>
    public void SendInput(string text) => Current()?.Input.Writer.TryWrite(text ?? string.Empty);

    /// <summary>互動模式：關閉 stdin（程式讀到 EOF）。</summary>
    public void CloseInput() => Current()?.Input.Writer.TryWrite(null);

    /// <summary>強制停止目前的編譯 / 執行。</summary>
    public void Stop()
    {
        var control = Current();
        if (control != null) control.StopRequested = true;
    }

    private RunControl? Current()
    {
        lock (_sync) { return _current; }
    }

    /// <param name="interactive">true：stdin 保持開啟供 SendInput；false：寫入 stdin 後立即關閉。</param>
    /// <param name="onOutput">
    /// 選填。執行期輸出即時回呼 (stream, text)，stream 為 stdout / stderr / info。
    /// 提供時輸出不再累積到 RunResult.Stdout / Stderr（避免重複顯示）。
    /// </param>
    public async Task<RunResult> RunAsync(string language, string code, string stdin,
        IReadOnlyList<(string name, string content)>? extraFiles = null, string? mainName = null,
        bool interactive = false, Action<string, string>? onOutput = null)
    {
        var control = new RunControl();
        lock (_sync)
        {
            if (_current != null)
            {
                return new RunResult { Ok = false, Phase = "error", Message = "已有程式在執行中。" };
            }
            _current = control;
        }

        try
        {
            return await CompileAndRunAsync(language, code, stdin, extraFiles, mainName, interactive, onOutput, control);
        }
        finally
        {
            lock (_sync) { _current = null; }
        }
    }

    private async Task<RunResult> CompileAndRunAsync(string language, string code, string stdin,
        IReadOnlyList<(string name, string content)>? extraFiles, string? mainName,
        bool interactive, Action<string, string>? onOutput, RunControl control)
    {
        var isC = string.Equals(language, "c", StringComparison.OrdinalIgnoreCase);
        var isCpp = language is "cpp" or "c++" or "cc"
                    || string.Equals(language, "cpp", StringComparison.OrdinalIgnoreCase);

        if (!isC && !isCpp)
        {
            return new RunResult { Ok = false, Phase = "error", Message = $"不支援的語言：{language}（僅支援 C / C++）。" };
        }

        var compiler = isC ? "gcc" : "g++";
        var srcExts = isC
            ? new[] { ".c" }
            : new[] { ".cpp", ".cc", ".cxx", ".c++" };
        // 主檔沿用分頁檔名（編譯錯誤訊息才對得上），避免與同大題的其他分頁撞名
        var srcName = Path.GetFileName(mainName ?? string.Empty);
        if (!Array.Exists(srcExts, e => e == Path.GetExtension(srcName).ToLowerInvariant()))
        {
            srcName = isC ? "main.c" : "main.cpp";
        }
        var std = isC ? "-std=c11" : "-std=c++17";

        var workDir = Path.Combine(Path.GetTempPath(), "byod_run_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var srcPath = Path.Combine(workDir, srcName);
        var exeName = OperatingSystem.IsWindows() ? "prog.exe" : "prog";
        var exePath = Path.Combine(workDir, exeName);

        try
        {
            await File.WriteAllTextAsync(srcPath, code ?? string.Empty);

            // 把輔助分頁（.h 等）一起寫進同一個 workDir，讓 #include "xxx.h" 能找到
            if (extraFiles != null)
            {
                foreach (var (name, content) in extraFiles)
                {
                    var safeName = Path.GetFileName(name); // 只取檔名，防止路徑穿越
                    if (!string.IsNullOrEmpty(safeName) && safeName != srcName)
                        await File.WriteAllTextAsync(Path.Combine(workDir, safeName), content ?? string.Empty);
                }
            }

            // ---- 禁用標頭檢查（<bits/stdc++.h> 等 <bits/…> 內部標頭）----
            var banned = FindBannedInclude(srcName, code)
                ?? extraFiles?.Select(f => FindBannedInclude(Path.GetFileName(f.name), f.content))
                    .FirstOrDefault(m => m != null);
            if (banned != null)
            {
                return new RunResult { Ok = false, Phase = "compile", Stderr = banned, Message = "使用了禁用的標頭。" };
            }

            // ---- 編譯 ----
            // 收集 workDir 中所有輔助 .c/.cpp 來源檔（.h 不列入，由 #include 引入）
            // -lm：確保 C 的 sqrt/pow/sin 等數學函式正確連結（Linux 需明確指定）
            // 以 ArgumentList 逐一傳參（不經字串拼接），檔名含引號或空白也不會被拆成額外參數
            // -MD：順便輸出相依標頭清單（*.d），用來抓以巨集間接引入的禁用標頭
            var compileArgs = new List<string> { "-O2", std, "-MD", "-o", exePath, srcPath };
            if (extraFiles != null)
            {
                // 同大題其他定義了 main() 的來源檔（另一份解答 / 草稿）不連結，避免 multiple definition of main
                var added = new HashSet<string> { srcName };
                foreach (var (name, content) in extraFiles)
                {
                    var safeName = Path.GetFileName(name);
                    var ext = Path.GetExtension(safeName).ToLowerInvariant();
                    if (Array.Exists(srcExts, e => e == ext) && !DefinesMain(content) && added.Add(safeName))
                        compileArgs.Add(Path.Combine(workDir, safeName));
                }
            }
            // 互動模式：stdout 是管線時預設為全緩衝，printf("請輸入:") 的提示要到程式結束才出現。
            // 加入一個在 main 前執行的小檔，把 stdout / stdin 改為行緩衝：
            // libc 只在「stdin 為行緩衝」時，讀取前自動 flush 所有行緩衝輸出（含不換行的提示）
            if (interactive)
            {
                var linebufPath = Path.Combine(workDir, "__byod_linebuf" + (isC ? ".c" : ".cpp"));
                await File.WriteAllTextAsync(linebufPath,
                    "#include <stdio.h>\n" +
                    "__attribute__((constructor)) static void byod_linebuf(void) { setvbuf(stdout, NULL, _IOLBF, 0); setvbuf(stdin, NULL, _IOLBF, 0); }\n");
                compileArgs.Add(linebufPath);
            }
            compileArgs.Add("-lm");
            Console.WriteLine($"[BYOD-run] 編譯：{compiler} {string.Join(' ', compileArgs)}");
            ProcessOutcome compile;
            try
            {
                compile = await RunProcessAsync(compiler, compileArgs, workDir, stdin: null,
                    cpuLimitMs: 0, wallLimitMs: _compileTimeoutMs, onOutput: null, inputControl: null, control);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Console.WriteLine($"[BYOD-run] 找不到編譯器 {compiler}");
                return new RunResult
                {
                    Ok = false,
                    Phase = "error",
                    Message = $"找不到編譯器 {compiler}。請先安裝（Ubuntu: sudo apt install build-essential）。"
                };
            }

            // 以「執行檔是否產生」判定編譯成功，不依賴 exit code（CEF 併存時 exit 偵測不可靠）
            var compiled = File.Exists(exePath);
            Console.WriteLine($"[BYOD-run] 編譯結束 killed={compile.Killed} 產生執行檔={compiled}");

            if (compiled && UsesBannedHeaderViaDeps(workDir))
            {
                return new RunResult
                {
                    Ok = false,
                    Phase = "compile",
                    Stderr = BannedHeaderMessage + "（偵測到以巨集等方式間接引入）",
                    Message = "使用了禁用的標頭。"
                };
            }

            if (control.StopRequested)
            {
                return new RunResult { Ok = false, Phase = "run", Stopped = true, Message = "已手動停止。" };
            }

            if (!compiled)
            {
                if (compile.Killed == KillReason.WallLimit)
                {
                    return new RunResult { Ok = false, Phase = "compile", TimedOut = true, Message = "編譯逾時。", Stderr = compile.Stderr };
                }
                return new RunResult
                {
                    Ok = false,
                    Phase = "compile",
                    Stderr = compile.Stderr,
                    Stdout = compile.Stdout,
                    Message = "編譯錯誤。"
                };
            }

            // ---- 執行 ----
            Console.WriteLine($"[BYOD-run] 執行：{exePath} interactive={interactive}");
            var run = await RunProcessAsync(exePath, Array.Empty<string>(), workDir, stdin ?? string.Empty,
                cpuLimitMs: _cpuLimitMs,
                wallLimitMs: interactive ? _interactiveWallLimitMs : _batchWallLimitMs,
                onOutput, interactive ? control : null, control);
            Console.WriteLine($"[BYOD-run] 執行結束 exit={run.ExitCode} killed={run.Killed} timeMs={run.ElapsedMs}");
            return new RunResult
            {
                Ok = run.Killed == KillReason.None,
                Phase = "run",
                Stdout = run.Stdout,
                Stderr = run.Stderr,
                ExitCode = run.ExitCode,
                TimeMs = run.ElapsedMs,
                TimedOut = run.Killed is KillReason.CpuLimit or KillReason.WallLimit,
                Stopped = run.Killed == KillReason.Stopped,
                Message = run.Killed switch
                {
                    KillReason.CpuLimit => $"執行逾時（CPU 時間超過 {_cpuLimitMs / 1000} 秒，可能是無窮迴圈），已強制結束。",
                    KillReason.WallLimit => interactive
                        ? $"執行超過 {_interactiveWallLimitMs / 60000} 分鐘，已強制結束。"
                        : $"執行超過 {_batchWallLimitMs / 1000} 秒，已強制結束。",
                    KillReason.Stopped => "已手動停止。",
                    _ => string.Empty
                }
            };
        }
        catch (Exception ex)
        {
            return new RunResult { Ok = false, Phase = "error", Message = "執行器內部錯誤：" + ex.Message };
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* 零殘留：忽略清理失敗 */ }
        }
    }

    /// <summary>單次執行的控制通道（輸入佇列 + 停止旗標）。</summary>
    private sealed class RunControl
    {
        /// <summary>互動輸入佇列；null 代表 EOF。</summary>
        public Channel<string?> Input { get; } = Channel.CreateUnbounded<string?>();
        public volatile bool StopRequested;
    }

    private enum KillReason { None, CpuLimit, WallLimit, Stopped }

    private readonly struct ProcessOutcome
    {
        public int ExitCode { get; init; }
        public string Stdout { get; init; }
        public string Stderr { get; init; }
        public KillReason Killed { get; init; }
        public long ElapsedMs { get; init; }
    }

    // 以「輸出管線關閉(EOF)」作為行程完成訊號，而非仰賴行程結束偵測。
    // 因為與 CEF 併存時，CEF 可能攔截 SIGCHLD 回收子行程，使 WaitForExit(Async) 永遠等不到；
    // 但子行程結束時核心會關閉其 stdout/stderr 管線，讀取便會完成。
    // stdin == null：不重導向；inputControl != null：寫完 stdin 後保持開啟，持續轉送互動輸入。
    private static Task<ProcessOutcome> RunProcessAsync(
        string fileName, IReadOnlyList<string> arguments, string workDir, string? stdin,
        int cpuLimitMs, int wallLimitMs, Action<string, string>? onOutput,
        RunControl? inputControl, RunControl control)
    {
        return Task.Run(async () =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workDir,
                RedirectStandardInput = stdin != null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in arguments)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = psi };
            var output = new OutputCollector(onOutput);

            var sw = Stopwatch.StartNew();
            process.Start();

            var stdoutTask = PumpAsync(process.StandardOutput, "stdout", output);
            var stderrTask = PumpAsync(process.StandardError, "stderr", output);
            var stdinTask = stdin != null
                ? FeedStdinAsync(process.StandardInput, stdin, inputControl?.Input.Reader)
                : Task.CompletedTask;

            // 每 50ms：推送累積輸出、檢查停止 / CPU / 牆鐘上限
            var ioTask = Task.WhenAll(stdoutTask, stderrTask);
            var killed = KillReason.None;
            while (!ioTask.IsCompleted)
            {
                await Task.WhenAny(ioTask, Task.Delay(50));
                output.Flush();
                if (ioTask.IsCompleted) break;

                if (control.StopRequested) killed = KillReason.Stopped;
                else if (sw.ElapsedMilliseconds > wallLimitMs) killed = KillReason.WallLimit;
                else if (cpuLimitMs > 0 && CpuTimeMs(process) > cpuLimitMs) killed = KillReason.CpuLimit;

                if (killed != KillReason.None)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    try { await ioTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
                    break;
                }
            }
            sw.Stop();
            output.Flush();

            // 結束輸入轉送（佇列完成 → FeedStdinAsync 關閉 stdin 後返回）
            inputControl?.Input.Writer.TryComplete();
            try { await stdinTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }

            var exitCode = -1;
            try { if (process.HasExited) exitCode = process.ExitCode; } catch { /* 可能已被 CEF 回收 */ }

            return new ProcessOutcome
            {
                ExitCode = killed != KillReason.None ? -1 : exitCode,
                Stdout = output.Stdout,
                Stderr = output.Stderr,
                Killed = killed,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        });
    }

    private const string BannedHeaderMessage =
        "禁止使用 <bits/stdc++.h>（及其他 <bits/…> 標頭），請改用個別標頭，例如 <iostream>、<vector>、<algorithm>、<string>。";

    // 直接引入 <bits/…> 或 "bits/…"（允許 # 與 include 之間有空白）
    private static readonly System.Text.RegularExpressions.Regex BannedInclude = new(
        @"^\s*#\s*include\s*[<""]\s*bits/", System.Text.RegularExpressions.RegexOptions.Multiline);

    private static readonly System.Text.RegularExpressions.Regex Comments = new(
        @"//[^\n]*|/\*.*?\*/", System.Text.RegularExpressions.RegexOptions.Singleline);

    /// <summary>找出原始碼中直接引入的禁用標頭，回傳「檔名:行號: 訊息」；沒有則回傳 null。註解中的不算。</summary>
    internal static string? FindBannedInclude(string fileName, string? source)
    {
        if (string.IsNullOrEmpty(source)) return null;
        // 註解換成等量換行，保留行號
        var stripped = Comments.Replace(source, m => new string('\n', m.Value.Count(ch => ch == '\n')));
        var match = BannedInclude.Match(stripped);
        if (!match.Success) return null;
        var line = stripped.Take(match.Index).Count(ch => ch == '\n') + 1;
        return $"{fileName}:{line}: {BannedHeaderMessage}";
    }

    // 聚合標頭：真正引入「整包」標準庫的那幾個（一般標頭內部也會引用其他 bits/*.h，不能全擋）
    private static readonly string[] BannedAggregateHeaders = { "bits/stdc++.h", "bits/extc++.h", "bits/stdtr1c++.h" };

    /// <summary>讀取編譯器 -MD 產生的 *.d 相依清單，檢查是否實際引入了聚合標頭。</summary>
    private static bool UsesBannedHeaderViaDeps(string workDir)
    {
        try
        {
            foreach (var dep in Directory.EnumerateFiles(workDir, "*.d"))
            {
                var text = File.ReadAllText(dep);
                if (BannedAggregateHeaders.Any(h => text.Contains(h, StringComparison.Ordinal))) return true;
            }
        }
        catch { /* 讀不到相依檔不影響執行 */ }
        return false;
    }

    // 去除註解與字串 / 字元常值後，判斷是否出現 main(
    private static readonly System.Text.RegularExpressions.Regex CommentsAndLiterals = new(
        @"//[^\n]*|/\*.*?\*/|""(?:\\.|[^""\\\n])*""|'(?:\\.|[^'\\\n])*'",
        System.Text.RegularExpressions.RegexOptions.Singleline);

    private static readonly System.Text.RegularExpressions.Regex MainCall = new(@"\bmain\s*\(");

    internal static bool DefinesMain(string? source) =>
        !string.IsNullOrEmpty(source) && MainCall.IsMatch(CommentsAndLiterals.Replace(source, " "));

    private static double CpuTimeMs(Process process)
    {
        try { return process.TotalProcessorTime.TotalMilliseconds; }
        catch { return 0; } // 行程已結束 / 被回收
    }

    private static async Task PumpAsync(StreamReader reader, string stream, OutputCollector output)
    {
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            output.Append(stream, buffer, read);
        }
    }

    /// <summary>寫入初始 stdin；有互動佇列時持續轉送，直到收到 EOF（null）或佇列完成，最後關閉 stdin。</summary>
    private static async Task FeedStdinAsync(StreamWriter writer, string initial, ChannelReader<string?>? more)
    {
        try
        {
            if (initial.Length > 0)
            {
                await writer.WriteAsync(initial);
                await writer.FlushAsync();
            }
            if (more != null)
            {
                await foreach (var text in more.ReadAllAsync())
                {
                    if (text == null) break;
                    await writer.WriteAsync(text);
                    await writer.FlushAsync();
                }
            }
        }
        catch { /* 程式可能未讀 stdin 即結束 */ }
        finally
        {
            try { writer.Close(); } catch { }
        }
    }

    /// <summary>
    /// 收集 stdout / stderr，總量上限 MaxOutputChars；超過的部分丟棄（但持續讀取，避免子行程卡在管線寫入）。
    /// 有 onOutput 時改為批次即時推送，不累積。
    /// </summary>
    private sealed class OutputCollector
    {
        private readonly Action<string, string>? _onOutput;
        private readonly object _lock = new();
        private readonly StringBuilder _stdout = new();
        private readonly StringBuilder _stderr = new();
        private readonly List<(string stream, StringBuilder text)> _pending = new();
        private int _total;
        private bool _truncated;

        public OutputCollector(Action<string, string>? onOutput)
        {
            _onOutput = onOutput;
        }

        private string TruncatedNotice => $"\n…（輸出超過 {MaxOutputChars} 字元，已截斷）\n";

        public string Stdout
        {
            get { lock (_lock) { return _truncated && _onOutput == null ? _stdout + TruncatedNotice : _stdout.ToString(); } }
        }

        public string Stderr
        {
            get { lock (_lock) { return _stderr.ToString(); } }
        }

        public void Append(string stream, char[] buffer, int count)
        {
            lock (_lock)
            {
                var n = Math.Min(count, MaxOutputChars - _total);
                if (n > 0)
                {
                    _total += n;
                    if (_onOutput == null)
                    {
                        (stream == "stderr" ? _stderr : _stdout).Append(buffer, 0, n);
                    }
                    else
                    {
                        Pending(stream).Append(buffer, 0, n);
                    }
                }
                if (n < count && !_truncated)
                {
                    _truncated = true;
                    if (_onOutput != null) Pending("info").Append(TruncatedNotice);
                }
            }
        }

        private StringBuilder Pending(string stream)
        {
            if (_pending.Count > 0 && _pending[^1].stream == stream) return _pending[^1].text;
            var sb = new StringBuilder();
            _pending.Add((stream, sb));
            return sb;
        }

        public void Flush()
        {
            if (_onOutput == null) return;
            (string stream, StringBuilder text)[] batch;
            lock (_lock)
            {
                if (_pending.Count == 0) return;
                batch = _pending.ToArray();
                _pending.Clear();
            }
            foreach (var (stream, text) in batch)
            {
                _onOutput(stream, text.ToString());
            }
        }
    }
}
