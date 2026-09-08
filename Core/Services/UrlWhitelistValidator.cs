using System;
using System.Collections.Generic;
using System.Linq;
using ByodKioskBrowser.Interfaces;

namespace ByodKioskBrowser.Services;

/// <summary>
/// 以主機名稱為基準的導航白名單（僅適用於 https 遠端網域）。
/// 允許條件：host 完全等於清單項目，或為清單項目的子網域（host 以 ".&lt;entry&gt;" 結尾）。
/// 比對時忽略大小寫，且僅接受 https。
/// 本地 Monaco 資源改由 app:// scheme 提供，於瀏覽器 handler 層直接放行，不經此驗證器。
/// </summary>
public sealed class UrlWhitelistValidator : IUrlWhitelistValidator
{
    private readonly HashSet<string> _allowedHosts;

    /// <summary>依 CLAUDE.md 與使用者確認的預設白名單建立驗證器。</summary>
    public UrlWhitelistValidator() : this(DefaultHosts())
    {
    }

    public UrlWhitelistValidator(IEnumerable<string> allowedHosts)
    {
        _allowedHosts = new HashSet<string>(
            allowedHosts.Select(h => h.Trim().ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>預設白名單：評測網域 + MathJax CDN + Google Fonts。</summary>
    public static IReadOnlyList<string> DefaultHosts() => new[]
    {
        // 主要評測網域
        "judge.gai.tw",
        // MathJax / 題目公式與靜態資源常用 CDN
        "cdn.jsdelivr.net",
        "cdnjs.cloudflare.com",
        // Google Fonts
        "fonts.googleapis.com",
        "fonts.gstatic.com"
    };

    public bool IsAllowed(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return false;
        }

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        // 僅允許 https（本地虛擬主機亦以 https 提供）。阻擋 http/file/data/blob 等。
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsHostAllowed(parsed.Host);
    }

    public bool IsHostAllowed(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        host = host.Trim().ToLowerInvariant();

        foreach (var allowed in _allowedHosts)
        {
            if (host == allowed || host.EndsWith("." + allowed, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
