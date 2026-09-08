using System;

namespace ByodKioskBrowser.Interfaces;

/// <summary>
/// 導航白名單驗證器。僅允許清單內的網域（比對 Uri.Host）。
/// </summary>
public interface IUrlWhitelistValidator
{
    /// <summary>
    /// 判斷指定的 URI 字串是否允許導航。
    /// 無法解析的 URI 一律視為不允許。
    /// </summary>
    bool IsAllowed(string uri);

    /// <summary>判斷指定的主機名稱是否在白名單內。</summary>
    bool IsHostAllowed(string host);
}
