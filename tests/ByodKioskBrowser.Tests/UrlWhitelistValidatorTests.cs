using ByodKioskBrowser.Services;
using Xunit;

namespace ByodKioskBrowser.Tests;

public class UrlWhitelistValidatorTests
{
    private readonly UrlWhitelistValidator _validator = new();

    [Theory]
    [InlineData("https://judge.gai.tw/")]
    [InlineData("https://judge.gai.tw/problem/123")]
    [InlineData("https://judge.gai.tw/user/login?next=/")]
    [InlineData("https://sub.judge.gai.tw/")]           // 子網域
    [InlineData("https://cdn.jsdelivr.net/npm/mathjax")]
    [InlineData("https://cdnjs.cloudflare.com/ajax/libs/x")]
    [InlineData("https://fonts.googleapis.com/css2")]
    [InlineData("https://fonts.gstatic.com/s/font.woff2")]
    [InlineData("https://appassets.local/index.html")]  // 本地 Monaco
    public void IsAllowed_WhitelistedHttpsUris_ReturnsTrue(string uri)
    {
        Assert.True(_validator.IsAllowed(uri));
    }

    [Theory]
    [InlineData("https://evil.com/")]
    [InlineData("https://www.google.com/")]
    [InlineData("https://github.com/")]
    // 相似但非白名單子網域的偽裝，必須擋下（安全關鍵）
    [InlineData("https://judge.gai.tw.evil.com/")]
    [InlineData("https://notjudge.gai.tw/")]
    [InlineData("https://gai.tw/")]                      // 上層網域不等於白名單項目
    public void IsAllowed_NonWhitelistedHosts_ReturnsFalse(string uri)
    {
        Assert.False(_validator.IsAllowed(uri));
    }

    [Theory]
    [InlineData("http://judge.gai.tw/")]                 // 非 https 一律拒絕
    [InlineData("ftp://judge.gai.tw/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<h1>x</h1>")]
    public void IsAllowed_NonHttpsSchemes_ReturnsFalse(string uri)
    {
        Assert.False(_validator.IsAllowed(uri));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a uri")]
    [InlineData("judge.gai.tw")]                         // 缺少 scheme，非絕對 URI
    public void IsAllowed_InvalidOrRelative_ReturnsFalse(string? uri)
    {
        Assert.False(_validator.IsAllowed(uri!));
    }

    [Theory]
    [InlineData("JUDGE.GAI.TW", true)]                   // 大小寫不敏感
    [InlineData("Fonts.GStatic.com", true)]
    [InlineData("EVIL.com", false)]
    public void IsHostAllowed_IsCaseInsensitive(string host, bool expected)
    {
        Assert.Equal(expected, _validator.IsHostAllowed(host));
    }

    [Fact]
    public void CustomWhitelist_OnlyAllowsProvidedHosts()
    {
        var validator = new UrlWhitelistValidator(new[] { "example.org" });

        Assert.True(validator.IsAllowed("https://example.org/"));
        Assert.True(validator.IsAllowed("https://api.example.org/"));
        Assert.False(validator.IsAllowed("https://judge.gai.tw/"));
    }
}
