using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace ByodKioskBrowser.Browser;

/// <summary>
/// 直接以 DBus 連 ibus 的輸入法用戶端。
/// CEF 在 Linux 視窗模式沒有接輸入法（Chromium 只放測試用的空殼），所以由我們把按鍵送給 ibus，
/// 再把 ibus 回傳的文字（CommitText）寫入網頁。注音組字與選字窗都由 GNOME Shell 的 ibus 面板顯示。
/// 任何一步失敗都退回「不處理」，按鍵照常交給 CEF，不影響英文輸入。
/// </summary>
public sealed class IBusInputMethod : IDisposable
{
    private const string Service = "org.freedesktop.IBus";
    private const string ContextInterface = "org.freedesktop.IBus.InputContext";

    // IBusCapabilite：只宣告 FOCUS。宣告 PREEDIT_TEXT / LOOKUP_TABLE / AUXILIARY_TEXT 代表「由 App 自己畫」，
    // ibus 就不會交給系統面板（GNOME Shell）顯示，選字窗會消失
    private const uint Capabilities = 8;

    /// <summary>ProcessKeyEvent 的 state：放開按鍵時加上 IBUS_RELEASE_MASK。</summary>
    public const uint ReleaseMask = 1u << 30;

    private readonly Connection _connection;
    private readonly string _contextPath;
    private IDisposable? _commitMatch;
    private IDisposable? _forwardMatch;

    /// <summary>ibus 送出確定的文字（選好的中文字）。可能在任意執行緒觸發。</summary>
    public event Action<string>? Committed;

    /// <summary>ibus 不處理、轉送回來的按鍵 (keyval, state)。可能在任意執行緒觸發。</summary>
    public event Action<uint, uint>? KeyForwarded;

    private IBusInputMethod(Connection connection, string contextPath)
    {
        _connection = connection;
        _contextPath = contextPath;
    }

    /// <summary>連線 ibus 並建立輸入環境；ibus 未執行或任何失敗時回傳 null。</summary>
    public static async Task<IBusInputMethod?> TryCreateAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            var address = Environment.GetEnvironmentVariable("IBUS_ADDRESS");
            if (string.IsNullOrWhiteSpace(address))
            {
                address = await QueryAddressAsync();
            }
            if (string.IsNullOrWhiteSpace(address))
            {
                Console.WriteLine("[BYOD] 輸入法：找不到 ibus 位址，略過中文輸入支援。");
                return null;
            }

            var connection = new Connection(new ClientConnectionOptions(address) { AutoConnect = false });
            await connection.ConnectAsync();

            var path = await connection.CallMethodAsync(
                CreateInputContextMessage(connection),
                (m, _) => m.GetBodyReader().ReadObjectPathAsString().ToString());

            var ime = new IBusInputMethod(connection, path);
            await ime.SubscribeAsync();
            await ime.CallAsync("SetCapabilities", "u", (ref MessageWriter w) => w.WriteUInt32(Capabilities));
            Console.WriteLine($"[BYOD] 輸入法：已連上 ibus（{path}）。");
            return ime;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BYOD] 輸入法：連線 ibus 失敗（{ex.GetType().Name}: {ex.Message}），略過中文輸入支援。");
            return null;
        }
    }

    private static MessageBuffer CreateInputContextMessage(Connection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, "/org/freedesktop/IBus", "org.freedesktop.IBus",
            "CreateInputContext", "s");
        writer.WriteString("byod-kiosk");
        return writer.CreateMessage();
    }

    /// <summary>以 `ibus address` 取得 ibus 私有匯流排位址（它會依 WAYLAND_DISPLAY / DISPLAY 找對應的位址檔）。</summary>
    private static async Task<string?> QueryAddressAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("ibus", "address")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = Process.Start(psi);
            if (process == null)
            {
                return null;
            }
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            output = output.Trim();
            return output.Length == 0 || output == "(null)" ? null : output;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 只在本地比對訊號（subscribe: false）：ibus 的輸入環境訊號本來就直接送給建立它的連線，
    /// 而且 ibus 不接受這個函式庫產生的 AddMatch 規則（值沒加引號）。
    /// </summary>
    private async Task SubscribeAsync()
    {
        _commitMatch = await _connection.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Path = _contextPath, Interface = ContextInterface, Member = "CommitText" },
            (m, _) => ReadIBusText(m),
            (ex, text, _, _) =>
            {
                if (ex == null && !string.IsNullOrEmpty(text))
                {
                    Committed?.Invoke(text);
                }
            },
            emitOnCapturedContext: false,
            subscribe: false);

        _forwardMatch = await _connection.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Path = _contextPath, Interface = ContextInterface, Member = "ForwardKeyEvent" },
            (m, _) =>
            {
                var reader = m.GetBodyReader();
                var keyval = reader.ReadUInt32();
                reader.ReadUInt32(); // keycode
                return (keyval, reader.ReadUInt32());
            },
            (ex, key, _, _) =>
            {
                if (ex == null)
                {
                    KeyForwarded?.Invoke(key.keyval, key.Item2);
                }
            },
            emitOnCapturedContext: false,
            subscribe: false);
    }

    /// <summary>CommitText 的參數是 variant 包著的 IBusText 結構：(s 名稱, a{sv} 附件, s 文字, v 屬性)。</summary>
    private static string ReadIBusText(Message message)
    {
        var reader = message.GetBodyReader();
        reader.ReadSignature(); // variant 的型別簽章
        reader.AlignStruct();
        reader.ReadString();    // "IBusText"
        var attachments = reader.ReadArrayStart(DBusType.Struct);
        while (reader.HasNext(attachments))
        {
            reader.AlignStruct();
            reader.ReadString();
            reader.ReadVariant();
        }
        return reader.ReadString();
    }

    /// <summary>
    /// 把按鍵交給 ibus（同步等待結果）。回傳 true 表示輸入法吃掉了這個鍵，不可再交給網頁。
    /// keycode 為 X11 硬體鍵碼；ibus 要的是減 8 的 evdev 鍵碼。逾時或失敗一律視為未處理。
    /// </summary>
    public bool ProcessKey(uint keyval, uint x11Keycode, uint state, int timeoutMs = 400)
    {
        try
        {
            // 在執行緒池上等待：呼叫端可能是帶有 SynchronizationContext 的 UI 執行緒，直接 Wait 會鎖死
            var task = Task.Run(() => _connection.CallMethodAsync(
                ProcessKeyMessage(keyval, x11Keycode >= 8 ? x11Keycode - 8 : 0, state),
                (m, _) => m.GetBodyReader().ReadBool()));
            return task.Wait(timeoutMs) && task.Result;
        }
        catch
        {
            return false;
        }
    }

    private MessageBuffer ProcessKeyMessage(uint keyval, uint keycode, uint state)
    {
        using var writer = _connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, _contextPath, ContextInterface, "ProcessKeyEvent", "uuu");
        writer.WriteUInt32(keyval);
        writer.WriteUInt32(keycode);
        writer.WriteUInt32(state);
        return writer.CreateMessage();
    }

    public void FocusIn() => Fire("FocusIn");

    public void FocusOut() => Fire("FocusOut");

    public void Reset() => Fire("Reset");

    /// <summary>選字窗位置（螢幕座標，實體像素）。</summary>
    public void SetCursorLocation(int x, int y, int width, int height) =>
        _ = CallAsync("SetCursorLocation", "iiii", (ref MessageWriter w) =>
        {
            w.WriteInt32(x);
            w.WriteInt32(y);
            w.WriteInt32(width);
            w.WriteInt32(height);
        }).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

    private void Fire(string member) =>
        _ = CallAsync(member, null, null).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

    private delegate void ArgsWriter(ref MessageWriter writer);

    private Task CallAsync(string member, string? signature, ArgsWriter? writeArgs)
    {
        var writer = _connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Service, _contextPath, ContextInterface, member, signature);
            writeArgs?.Invoke(ref writer);
            return _connection.CallMethodAsync(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    public void Dispose()
    {
        _commitMatch?.Dispose();
        _forwardMatch?.Dispose();
        try
        {
            using var writer = _connection.GetMessageWriter();
            writer.WriteMethodCallHeader(Service, _contextPath, ContextInterface, "Destroy");
            _connection.TrySendMessage(writer.CreateMessage());
        }
        catch
        {
            // 忽略
        }
        _connection.Dispose();
    }
}
