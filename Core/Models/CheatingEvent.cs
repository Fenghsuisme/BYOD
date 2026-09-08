using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ByodKioskBrowser.Models;

/// <summary>
/// 單一作弊 / 安全事件。所有欄位皆可直接序列化為標準 JSON 供稽核導出。
/// </summary>
public sealed class CheatingEvent
{
    /// <summary>事件發生的當地時間戳記（含時區）。</summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>事件類型。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    [JsonPropertyName("type")]
    public CheatingEventType Type { get; init; }

    /// <summary>人類可讀的描述。</summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    /// <summary>相關的 URI 或附加細節（可為 null）。</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    /// <summary>失焦事件的累計次數（僅 WindowDeactivated 使用，其餘為 null）。</summary>
    [JsonPropertyName("occurrence")]
    public int? Occurrence { get; init; }

    private static readonly JsonSerializerOptions SingleLineOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>輸出為單行 JSON，用於逐行寫入日誌檔（JSON Lines）。</summary>
    public string ToJsonLine() => JsonSerializer.Serialize(this, SingleLineOptions);
}
