using System.Text.Json;
using ByodKioskBrowser.Models;
using ByodKioskBrowser.Services;
using Xunit;

namespace ByodKioskBrowser.Tests;

public class CheatingDetectorTests
{
    [Fact]
    public void RecordDeactivation_IncrementsCountAndReturnsIt()
    {
        var detector = new CheatingDetector();

        Assert.Equal(1, detector.RecordDeactivation());
        Assert.Equal(2, detector.RecordDeactivation());
        Assert.Equal(3, detector.RecordDeactivation());
        Assert.Equal(3, detector.DeactivationCount);
    }

    [Fact]
    public void RecordDeactivation_StoresEventWithOccurrence()
    {
        var detector = new CheatingDetector();
        detector.RecordDeactivation();
        detector.RecordDeactivation();

        var events = detector.Events;
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(CheatingEventType.WindowDeactivated, e.Type));
        Assert.Equal(1, events[0].Occurrence);
        Assert.Equal(2, events[1].Occurrence);
    }

    [Fact]
    public void Record_AddsEventWithTypeMessageAndDetail()
    {
        var detector = new CheatingDetector();
        detector.Record(CheatingEventType.BlockedNavigation, "擋下導航", "https://evil.com/");

        var evt = Assert.Single(detector.Events);
        Assert.Equal(CheatingEventType.BlockedNavigation, evt.Type);
        Assert.Equal("擋下導航", evt.Message);
        Assert.Equal("https://evil.com/", evt.Detail);
        Assert.Null(evt.Occurrence);
    }

    [Fact]
    public void Events_ReturnsImmutableSnapshot()
    {
        var detector = new CheatingDetector();
        detector.Record(CheatingEventType.Info, "第一筆");

        var snapshot = detector.Events;
        detector.Record(CheatingEventType.Info, "第二筆");

        // 先前取得的快照不應受後續新增影響
        Assert.Single(snapshot);
        Assert.Equal(2, detector.Events.Count);
    }

    [Fact]
    public void EventRecorded_IsRaisedForEachEvent()
    {
        var detector = new CheatingDetector();
        var received = new List<CheatingEvent>();
        detector.EventRecorded += (_, e) => received.Add(e);

        detector.Record(CheatingEventType.BlockedNewWindow, "彈窗");
        detector.RecordDeactivation();

        Assert.Equal(2, received.Count);
        Assert.Equal(CheatingEventType.BlockedNewWindow, received[0].Type);
        Assert.Equal(CheatingEventType.WindowDeactivated, received[1].Type);
    }

    [Fact]
    public async Task ExportJsonAsync_WritesParseableJsonArray()
    {
        var detector = new CheatingDetector();
        detector.Record(CheatingEventType.BlockedExternalPaste, "封鎖外部貼上", "external-paste-blocked");
        detector.RecordDeactivation();

        var path = Path.Combine(Path.GetTempPath(), $"byod_test_{Guid.NewGuid():N}.json");
        try
        {
            await detector.ExportJsonAsync(path);

            Assert.True(File.Exists(path));
            var json = await File.ReadAllTextAsync(path);
            using var doc = JsonDocument.Parse(json);

            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
            Assert.Equal(2, doc.RootElement.GetArrayLength());

            // 列舉型別應序列化為字串（便於人工稽核）
            var firstType = doc.RootElement[0].GetProperty("type").GetString();
            Assert.Equal("BlockedExternalPaste", firstType);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void LiveLog_AppendsOneJsonLinePerEvent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"byod_live_{Guid.NewGuid():N}.jsonl");
        try
        {
            var detector = new CheatingDetector(path);
            detector.Record(CheatingEventType.Info, "啟動");
            detector.RecordDeactivation();

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);

            // 每一行都必須是有效的獨立 JSON 物件（JSON Lines）
            foreach (var line in lines)
            {
                using var doc = JsonDocument.Parse(line);
                Assert.True(doc.RootElement.TryGetProperty("timestamp", out _));
                Assert.True(doc.RootElement.TryGetProperty("type", out _));
            }
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
