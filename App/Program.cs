using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using ByodKioskBrowser.Browser;
using Xilium.CefGlue;
using Xilium.CefGlue.Common;
using Xilium.CefGlue.Common.Shared;

namespace ByodKioskBrowser;

internal static class Program
{
    /// <summary>本次執行的 CEF 快取資料夾（位於 TEMP，關閉時刪除以達零殘留）。</summary>
    public static string CachePath { get; private set; } = string.Empty;

    [STAThread]
    public static int Main(string[] args)
    {
        CachePath = Path.Combine(
            Path.GetTempPath(), "ByodKiosk_" + Guid.NewGuid().ToString("N"));

        // 即使異常結束也盡量清除快取
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Cleanup();
        }

        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .AfterSetup(_ => InitializeCef());

        // Linux 於虛擬機 / 無獨立 GPU 環境，Avalonia 以 OpenGL 合成常導致 segfault。
        // 預設改用軟體渲染（kiosk 不需 GPU 效能）；設 BYOD_GPU=1 可在真實硬體上改回 GPU。
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("BYOD_GPU") != "1")
        {
            builder = builder.With(new X11PlatformOptions
            {
                RenderingMode = new[] { X11RenderingMode.Software }
            });
        }

        return builder;
    }

    private static void InitializeCef()
    {
        var debug = Environment.GetEnvironmentVariable("BYOD_DEBUG") == "1";

        // CEF 自身日誌，方便診斷 Linux 啟動崩潰（segfault 時仍可能留下線索）
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        var cefLog = Path.Combine(logDir, "cef.log");

        var settings = new CefSettings
        {
            RootCachePath = CachePath,
            WindowlessRenderingEnabled = false,
            PersistSessionCookies = false,
            // Linux 免 root 設定 chrome-sandbox 的必要條件（可攜綠色程式標準做法）。
            // 本 App 僅載入受控評測網域，關閉 renderer sandbox 對防弊無實質影響。
            NoSandbox = true,
            LogFile = cefLog,
            LogSeverity = debug ? CefLogSeverity.Verbose : CefLogSeverity.Warning
        };

        // 以環境變數切換 Linux 常見的相容性旗標（無須重新編譯）：
        //   BYOD_DISABLE_GPU=1  在 VM / 無 GPU 環境避免 GPU 初始化崩潰
        //   BYOD_NO_ZYGOTE=1    停用 zygote 行程（部分發行版需要）
        var flags = new List<KeyValuePair<string, string>>();
        if (Environment.GetEnvironmentVariable("BYOD_DISABLE_GPU") == "1")
        {
            flags.Add(new KeyValuePair<string, string>("disable-gpu", ""));
            flags.Add(new KeyValuePair<string, string>("disable-gpu-compositing", ""));
        }
        if (Environment.GetEnvironmentVariable("BYOD_NO_ZYGOTE") == "1")
        {
            flags.Add(new KeyValuePair<string, string>("no-zygote", ""));
        }

        CefRuntimeLoader.Initialize(
            settings: settings,
            flags: flags.Count > 0 ? flags.ToArray() : null,
            customSchemes: new[]
            {
                new CustomScheme
                {
                    SchemeName = LocalAssetSchemeHandlerFactory.Scheme,
                    DomainName = LocalAssetSchemeHandlerFactory.Host,
                    SchemeHandlerFactory = new LocalAssetSchemeHandlerFactory()
                }
            });
    }

    private static bool _cleaned;

    private static void Cleanup()
    {
        if (_cleaned)
        {
            return;
        }
        _cleaned = true;

        // 題目 PDF 暫存檔（放在 CEF 關閉前：CEF 關閉可能卡住而被保底 Kill）
        try
        {
            var pdf = LocalAssetSchemeHandlerFactory.PdfCachePath;
            if (!string.IsNullOrEmpty(pdf) && File.Exists(pdf))
            {
                File.Delete(pdf);
            }
        }
        catch
        {
            // 忽略
        }

        try
        {
            CefRuntime.Shutdown(); // 先關閉 CEF 才能釋放快取檔案鎖
        }
        catch
        {
            // 忽略
        }

        DeleteCacheDir();
    }

    private static void DeleteCacheDir()
    {
        try
        {
            if (!string.IsNullOrEmpty(CachePath) && Directory.Exists(CachePath))
            {
                Directory.Delete(CachePath, recursive: true);
            }
        }
        catch
        {
            // 檔案可能仍被釋放中的行程鎖定；忽略
        }
    }

    /// <summary>
    /// 立即結束整個程式，不經過 CefRuntime.Shutdown（它在 Linux 上可能卡住）。
    /// 先刪暫存（Linux 可刪除仍開啟中的檔案），再殺 CEF 子行程，最後殺自己。
    /// 注意：Process.Kill(entireProcessTree: true) 不可用在自己身上（會丟 InvalidOperationException），
    /// 所以子行程逐一以 tree 方式殺，自己用一般 Kill()。
    /// </summary>
    public static void HardExit()
    {
        try
        {
            var pdf = LocalAssetSchemeHandlerFactory.PdfCachePath;
            if (!string.IsNullOrEmpty(pdf) && File.Exists(pdf))
            {
                File.Delete(pdf);
            }
        }
        catch
        {
            // 忽略
        }

        foreach (var child in ChildProcessIds())
        {
            try { System.Diagnostics.Process.GetProcessById(child).Kill(entireProcessTree: true); } catch { }
        }

        DeleteCacheDir();

        try { Console.Out.Flush(); } catch { }
        System.Diagnostics.Process.GetCurrentProcess().Kill();
    }

    /// <summary>Linux：掃描 /proc 找出父行程為自己的子行程（CEF renderer / gpu / zygote 等）。</summary>
    private static List<int> ChildProcessIds()
    {
        var result = new List<int>();
        if (!OperatingSystem.IsLinux())
        {
            return result;
        }

        var self = Environment.ProcessId;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == self)
                {
                    continue;
                }
                try
                {
                    // /proc/<pid>/stat：「pid (comm) state ppid …」，comm 可能含空白，故從最後的 ')' 之後解析
                    var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                    var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                    if (fields.Length > 1 && int.TryParse(fields[1], out var ppid) && ppid == self)
                    {
                        result.Add(pid);
                    }
                }
                catch
                {
                    // 行程可能已結束
                }
            }
        }
        catch
        {
            // 忽略
        }
        return result;
    }
}
