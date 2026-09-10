using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
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
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);
}

/// <summary>
/// 受控的本地 C / C++ 編譯執行器。將程式碼寫入暫存目錄、以 gcc/g++ 編譯，
/// 帶 stdin 執行（有 timeout），擷取輸出後刪除暫存（零殘留）。
/// 不提供任意 shell，僅固定呼叫編譯器與產生的執行檔。
/// </summary>
public sealed class CodeRunnerService
{
    private readonly int _compileTimeoutMs;
    private readonly int _runTimeoutMs;

    public CodeRunnerService(int compileTimeoutMs = 20000, int runTimeoutMs = 5000)
    {
        _compileTimeoutMs = compileTimeoutMs;
        _runTimeoutMs = runTimeoutMs;
    }

    public async Task<RunResult> RunAsync(string language, string code, string stdin)
    {
        var isC = string.Equals(language, "c", StringComparison.OrdinalIgnoreCase);
        var isCpp = language is "cpp" or "c++" or "cc"
                    || string.Equals(language, "cpp", StringComparison.OrdinalIgnoreCase);

        if (!isC && !isCpp)
        {
            return new RunResult { Ok = false, Phase = "error", Message = $"不支援的語言：{language}（僅支援 C / C++）。" };
        }

        var compiler = isC ? "gcc" : "g++";
        var srcName = isC ? "main.c" : "main.cpp";
        var std = isC ? "-std=c11" : "-std=c++17";

        var workDir = Path.Combine(Path.GetTempPath(), "byod_run_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var srcPath = Path.Combine(workDir, srcName);
        var exeName = OperatingSystem.IsWindows() ? "prog.exe" : "prog";
        var exePath = Path.Combine(workDir, exeName);

        try
        {
            await File.WriteAllTextAsync(srcPath, code ?? string.Empty);

            // ---- 編譯 ----
            var compileArgs = $"-O2 {std} -o \"{exePath}\" \"{srcPath}\"";
            Console.WriteLine($"[BYOD-run] 編譯：{compiler} {compileArgs}");
            ProcessOutcome compile;
            try
            {
                compile = await RunProcessAsync(compiler, compileArgs, workDir, stdin: null, _compileTimeoutMs);
                Console.WriteLine($"[BYOD-run] 編譯結束 exit={compile.ExitCode} timedOut={compile.TimedOut}");
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

            if (compile.TimedOut)
            {
                return new RunResult { Ok = false, Phase = "compile", TimedOut = true, Message = "編譯逾時。" };
            }
            if (compile.ExitCode != 0)
            {
                return new RunResult
                {
                    Ok = false,
                    Phase = "compile",
                    ExitCode = compile.ExitCode,
                    Stderr = compile.Stderr,
                    Stdout = compile.Stdout,
                    Message = "編譯錯誤。"
                };
            }

            // ---- 執行 ----
            Console.WriteLine($"[BYOD-run] 執行：{exePath}");
            var run = await RunProcessAsync(exePath, string.Empty, workDir, stdin ?? string.Empty, _runTimeoutMs);
            Console.WriteLine($"[BYOD-run] 執行結束 exit={run.ExitCode} timedOut={run.TimedOut} timeMs={run.ElapsedMs}");
            return new RunResult
            {
                Ok = !run.TimedOut && run.ExitCode == 0,
                Phase = "run",
                Stdout = run.Stdout,
                Stderr = run.Stderr,
                ExitCode = run.ExitCode,
                TimeMs = run.ElapsedMs,
                TimedOut = run.TimedOut,
                Message = run.TimedOut ? $"執行逾時（>{_runTimeoutMs} ms），已強制結束。" : string.Empty
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

    private readonly struct ProcessOutcome
    {
        public int ExitCode { get; init; }
        public string Stdout { get; init; }
        public string Stderr { get; init; }
        public bool TimedOut { get; init; }
        public long ElapsedMs { get; init; }
    }

    private static async Task<ProcessOutcome> RunProcessAsync(
        string fileName, string arguments, string workDir, string? stdin, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workDir,
            RedirectStandardInput = stdin != null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        var sw = Stopwatch.StartNew();
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (stdin != null)
        {
            try
            {
                await process.StandardInput.WriteAsync(stdin);
                process.StandardInput.Close();
            }
            catch { /* 程式可能未讀 stdin 即結束 */ }
        }

        using var cts = new CancellationTokenSource(timeoutMs);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(); } catch { }
        }
        sw.Stop();

        // 確保非同步輸出讀取完成
        try { process.WaitForExit(500); } catch { }

        return new ProcessOutcome
        {
            ExitCode = timedOut ? -1 : SafeExitCode(process),
            Stdout = stdout.ToString(),
            Stderr = stderr.ToString(),
            TimedOut = timedOut,
            ElapsedMs = sw.ElapsedMilliseconds
        };
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }
}
