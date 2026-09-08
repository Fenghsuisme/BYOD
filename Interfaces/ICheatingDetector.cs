using System.Collections.Generic;
using System.Threading.Tasks;
using ByodKioskBrowser.Models;

namespace ByodKioskBrowser.Interfaces;

/// <summary>
/// 作弊 / 安全事件的偵測與紀錄。事件以結構化物件保存，支援導出標準 JSON。
/// </summary>
public interface ICheatingDetector
{
    /// <summary>目前為止的失焦次數。</summary>
    int DeactivationCount { get; }

    /// <summary>已記錄的所有事件（唯讀快照）。</summary>
    IReadOnlyList<CheatingEvent> Events { get; }

    /// <summary>記錄一筆一般安全事件。</summary>
    void Record(CheatingEventType type, string message, string? detail = null);

    /// <summary>記錄一次視窗失焦事件，並回傳更新後的累計次數。</summary>
    int RecordDeactivation();

    /// <summary>將所有事件以標準 JSON 陣列導出至指定檔案路徑。</summary>
    Task ExportJsonAsync(string filePath);
}
