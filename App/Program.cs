using System;
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
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .AfterSetup(_ => InitializeCef());

    private static void InitializeCef()
    {
        CefRuntimeLoader.Initialize(
            settings: new CefSettings
            {
                RootCachePath = CachePath,
                // 使用視窗化渲染（效能較佳、相容性較好）
                WindowlessRenderingEnabled = false,
                // 不持久化任何工作階段資料
                PersistSessionCookies = false
            },
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

        try
        {
            CefRuntime.Shutdown(); // 先關閉 CEF 才能釋放快取檔案鎖
        }
        catch
        {
            // 忽略
        }

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
}
