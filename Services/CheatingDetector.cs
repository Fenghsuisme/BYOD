using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ByodKioskBrowser.Interfaces;
using ByodKioskBrowser.Models;

namespace ByodKioskBrowser.Services;

/// <summary>
/// 執行緒安全的作弊事件偵測器。事件保存在記憶體中，並可即時附加寫入 JSON Lines 日誌檔，
/// 以及在關閉時導出完整的標準 JSON 陣列供稽核。
/// </summary>
public sealed class CheatingDetector : ICheatingDetector
{
    private readonly object _sync = new();
    private readonly List<CheatingEvent> _events = new();
    private readonly string? _liveLogPath;
    private int _deactivationCount;

    /// <summary>當有新事件被記錄時觸發（於呼叫端執行緒引發，UI 需自行切回主執行緒）。</summary>
    public event EventHandler<CheatingEvent>? EventRecorded;

    /// <param name="liveLogPath">
    /// 選填。若提供，每筆事件會即時以 JSON Lines 附加寫入此檔案，避免程式異常結束時遺失紀錄。
    /// </param>
    public CheatingDetector(string? liveLogPath = null)
    {
        _liveLogPath = liveLogPath;
    }

    public int DeactivationCount
    {
        get { lock (_sync) { return _deactivationCount; } }
    }

    public IReadOnlyList<CheatingEvent> Events
    {
        get { lock (_sync) { return _events.ToArray(); } }
    }

    public void Record(CheatingEventType type, string message, string? detail = null)
    {
        var evt = new CheatingEvent
        {
            Type = type,
            Message = message,
            Detail = detail
        };
        Append(evt);
    }

    public int RecordDeactivation()
    {
        int count;
        CheatingEvent evt;
        lock (_sync)
        {
            count = ++_deactivationCount;
            evt = new CheatingEvent
            {
                Type = CheatingEventType.WindowDeactivated,
                Message = "視窗失去焦點，可能切換至外部應用程式。",
                Occurrence = count
            };
            _events.Add(evt);
        }

        WriteLiveLine(evt);
        EventRecorded?.Invoke(this, evt);
        return count;
    }

    private void Append(CheatingEvent evt)
    {
        lock (_sync)
        {
            _events.Add(evt);
        }

        WriteLiveLine(evt);
        EventRecorded?.Invoke(this, evt);
    }

    private void WriteLiveLine(CheatingEvent evt)
    {
        if (string.IsNullOrEmpty(_liveLogPath))
        {
            return;
        }

        try
        {
            // 即時附加寫入；失敗不可影響主流程（防弊工具本身不得成為當機來源）。
            File.AppendAllText(_liveLogPath, evt.ToJsonLine() + Environment.NewLine);
        }
        catch
        {
            // 靜默忽略 I/O 例外。
        }
    }

    private static readonly JsonSerializerOptions ExportOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task ExportJsonAsync(string filePath)
    {
        CheatingEvent[] snapshot;
        lock (_sync)
        {
            snapshot = _events.ToArray();
        }

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, snapshot, ExportOptions, CancellationToken.None);
    }
}
