using System;
using System.IO;
using System.Windows;

namespace ByodKioskBrowser;

public partial class App : Application
{
    /// <summary>本次執行的 WebView2 使用者資料夾（位於 TEMP，關閉時刪除以達零殘留）。</summary>
    public string UserDataFolder { get; private set; } = string.Empty;

    /// <summary>本地網頁靜態資源（Monaco）資料夾，位於執行檔旁的 Assets\webapp。</summary>
    public string WebAppFolder { get; private set; } = string.Empty;

    /// <summary>即時 JSON Lines 日誌檔路徑（保留為稽核用途）。</summary>
    public string LiveLogPath { get; private set; } = string.Empty;

    /// <summary>關閉時導出的完整 JSON 事件檔路徑。</summary>
    public string ExportLogPath { get; private set; } = string.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var baseDir = AppContext.BaseDirectory;

        // WebView2 快取導向 TEMP，避免在系統留下瀏覽殘留。
        UserDataFolder = Path.Combine(
            Path.GetTempPath(),
            "ByodKioskBrowser",
            "wv2_" + Guid.NewGuid().ToString("N"));

        WebAppFolder = Path.Combine(baseDir, "Assets", "webapp");

        // 稽核日誌保存在執行檔旁的 logs 資料夾（刻意保留供監考人員檢視）。
        var logDir = Path.Combine(baseDir, "logs");
        Directory.CreateDirectory(logDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        LiveLogPath = Path.Combine(logDir, $"events_{stamp}.jsonl");
        ExportLogPath = Path.Combine(logDir, $"events_{stamp}.json");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 零殘留：刪除本次的 WebView2 快取資料夾。
        TryDeleteUserDataFolder();
        base.OnExit(e);
    }

    private void TryDeleteUserDataFolder()
    {
        try
        {
            if (!string.IsNullOrEmpty(UserDataFolder) && Directory.Exists(UserDataFolder))
            {
                Directory.Delete(UserDataFolder, recursive: true);
            }
        }
        catch
        {
            // 檔案可能仍被釋放中的 WebView2 行程鎖定；靜默忽略，不影響關閉流程。
        }
    }
}
