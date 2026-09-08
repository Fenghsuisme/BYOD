using System;
using System.IO;
using Xilium.CefGlue;
using Xilium.CefGlue.Common.Handlers;

namespace ByodKioskBrowser.Browser;

/// <summary>
/// 自訂 <c>app://local/…</c> scheme，從磁碟上的 Assets/webapp 提供本地 Monaco 資源。
/// 取代 Windows 版 WebView2 的 SetVirtualHostNameToFolderMapping，確保執行期不外連 CDN。
/// </summary>
public sealed class LocalAssetSchemeHandlerFactory : CefSchemeHandlerFactory
{
    public const string Scheme = "app";
    public const string Host = "local";

    /// <summary>編輯器起始頁的完整 URL。</summary>
    public static string EditorStartUrl => $"{Scheme}://{Host}/index.html";

    private static readonly string WebRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets", "webapp"));

    protected override CefResourceHandler Create(
        CefBrowser browser, CefFrame frame, string schemeName, CefRequest request)
    {
        var handler = new DefaultResourceHandler();

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            handler.Status = 400;
            handler.StatusText = "Bad Request";
            return handler;
        }

        // app://local/vs/loader.js → 相對路徑 vs/loader.js
        var relative = uri.AbsolutePath.TrimStart('/');
        if (string.IsNullOrEmpty(relative))
        {
            relative = "index.html";
        }

        var fullPath = Path.GetFullPath(Path.Combine(WebRoot, relative));

        // 防目錄穿越：解析後路徑必須仍在 WebRoot 之內
        if (!fullPath.StartsWith(WebRoot, StringComparison.Ordinal) || !File.Exists(fullPath))
        {
            handler.Status = 404;
            handler.StatusText = "Not Found";
            handler.MimeType = "text/plain";
            handler.Response = new MemoryStream(Array.Empty<byte>());
            return handler;
        }

        handler.Status = 200;
        handler.StatusText = "OK";
        handler.MimeType = GetMimeType(Path.GetExtension(fullPath));
        // 以唯讀方式開啟，交由 handler 讀取後釋放
        handler.Response = File.OpenRead(fullPath);
        return handler;
    }

    private static string GetMimeType(string extension) => extension.ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html",
        ".js" or ".mjs" => "text/javascript",
        ".css" => "text/css",
        ".json" => "application/json",
        ".map" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".wasm" => "application/wasm",
        _ => "application/octet-stream"
    };
}
