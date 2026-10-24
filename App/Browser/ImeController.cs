using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Xilium.CefGlue;

namespace ByodKioskBrowser.Browser;

/// <summary>
/// 把 CEF（兩個瀏覽器）的按鍵轉給 ibus，並把 ibus 確定的文字插入目前有焦點的網頁欄位。
/// 由 LockdownKeyboardHandler.OnPreKeyEvent 呼叫 FilterKey（CEF UI 執行緒）。
/// </summary>
public sealed class ImeController : IDisposable
{
    // 回報游標位置用：注入到兩個頁面，焦點欄位位置變動時呼叫 bridge.caret(x, y, h, dpr)
    public const string CaretReporterScript =
        "(function(){if(window.__byodCaretHooked){return;}window.__byodCaretHooked=true;var last='';" +
        "function rep(){try{var a=document.activeElement;if(!a||a===document.body){return;}" +
        "var r=a.getBoundingClientRect();var x=r.left,y=r.top,h=Math.min(r.height,24);" +
        "var s=window.getSelection&&window.getSelection();" +
        "if(a.isContentEditable&&s&&s.rangeCount){var q=s.getRangeAt(0).getBoundingClientRect();if(q.height){x=q.left;y=q.top;h=q.height;}}" +
        "var k=[x|0,y|0,h|0].join();if(k===last){return;}last=k;" +
        "var b=window.editorBridge||window.hostBridge;if(b&&b.caret){b.caret(x,y,h,window.devicePixelRatio||1);}}catch(e){}}" +
        "['keyup','click','focusin','input'].forEach(function(t){document.addEventListener(t,function(){setTimeout(rep,0);},true);});" +
        "})();";

    private IBusInputMethod? _ime;
    private volatile CefBrowser? _target;
    private bool _swallowChar;
    private int _timeouts;
    private readonly bool _debug = Environment.GetEnvironmentVariable("BYOD_IME_DEBUG") == "1";

    public bool IsActive => _ime != null;

    public async Task StartAsync()
    {
        _ime = await IBusInputMethod.TryCreateAsync();
        if (_ime == null)
        {
            return;
        }
        _ime.Committed += InsertText;
        _ime.KeyForwarded += (keyval, state) =>
        {
            // 輸入法轉送回來的一般字元（少數引擎會這樣送出未處理的英文字）
            if ((state & IBusInputMethod.ReleaseMask) == 0 && keyval >= 0x20 && keyval < 0x7f)
            {
                InsertText(((char)keyval).ToString());
            }
        };
        _ime.FocusIn();
    }

    public void FocusIn()
    {
        if (_debug) Console.WriteLine("[BYOD] IME FocusIn");
        _ime?.FocusIn();
    }

    public void FocusOut()
    {
        if (_debug) Console.WriteLine("[BYOD] IME FocusOut");
        _ime?.FocusOut();
    }

    /// <summary>選字窗位置（螢幕實體像素）。</summary>
    public void SetCaret(int x, int y, int height) => _ime?.SetCursorLocation(x, y, 1, Math.Max(height, 1));

    /// <summary>回傳 true 表示此按鍵已交給輸入法處理，CEF 不可再處理。</summary>
    public bool FilterKey(CefBrowser browser, CefKeyEvent e)
    {
        var ime = _ime;
        if (ime == null)
        {
            return false;
        }

        if (e.EventType == CefKeyEventType.Char)
        {
            // 按下時已被輸入法吃掉的鍵，接著產生的字元事件也要吃掉
            var swallow = _swallowChar;
            _swallowChar = false;
            return swallow;
        }

        var mods = e.Modifiers;
        var ctrlOrAlt = mods.HasFlag(CefEventFlags.ControlDown) || mods.HasFlag(CefEventFlags.AltDown)
                        || mods.HasFlag(CefEventFlags.CommandDown);
        // 快捷鍵（Ctrl/Alt 組合）與非輸入欄位的按鍵不經輸入法
        if (ctrlOrAlt || !e.FocusOnEditableField)
        {
            return false;
        }

        var keyval = ToKeysym(e);
        if (keyval == 0)
        {
            return false;
        }

        uint state = 0;
        if (mods.HasFlag(CefEventFlags.ShiftDown)) state |= 1;
        // CEF 在 Linux 不一定帶 CapsLock 狀態；新酷音靠它切換中英文（預設），所以向 X server 查
        if (mods.HasFlag(CefEventFlags.CapsLockOn) || X11CapsLockOn()) state |= 2;
        var release = e.EventType == CefKeyEventType.KeyUp;
        if (release) state |= IBusInputMethod.ReleaseMask;

        _target = browser;
        var started = Environment.TickCount64;
        var handled = ime.ProcessKey(keyval, (uint)e.NativeKeyCode, state);
        if (Environment.TickCount64 - started >= 350)
        {
            // ibus 沒回應：連續數次就停用，避免每個按鍵都卡住
            if (++_timeouts >= 3)
            {
                Console.WriteLine("[BYOD] 輸入法：ibus 多次無回應，停用中文輸入支援。");
                _ime = null;
                ime.Dispose();
            }
        }
        else
        {
            _timeouts = 0;
        }

        if (_debug)
        {
            Console.WriteLine($"[BYOD] IME key {e.EventType} vk=0x{e.WindowsKeyCode:X2} native={e.NativeKeyCode} " +
                $"keyval=0x{keyval:X} state=0x{state:X} → {(handled ? "輸入法處理" : "交給網頁")}");
        }

        if (!release)
        {
            _swallowChar = handled;
        }
        return handled;
    }

    private void InsertText(string text)
    {
        var frame = _target?.GetFocusedFrame();
        if (frame == null || string.IsNullOrEmpty(text))
        {
            return;
        }
        // 以 insertText 插入：Monaco、ACE、一般輸入框都會當成打字處理
        frame.ExecuteJavaScript(
            $"document.execCommand('insertText', false, {JsonSerializer.Serialize(text)});", "", 0);
    }

    private static IntPtr _display;
    private static bool _displayFailed;

    /// <summary>目前 X 的 CapsLock 鎖定狀態（XQueryPointer 回傳的修飾鍵遮罩含 LockMask）。</summary>
    private static bool X11CapsLockOn()
    {
        if (_displayFailed)
        {
            return false;
        }
        try
        {
            if (_display == IntPtr.Zero)
            {
                _display = XOpenDisplay(IntPtr.Zero);
                if (_display == IntPtr.Zero)
                {
                    _displayFailed = true;
                    return false;
                }
            }
            XQueryPointer(_display, XDefaultRootWindow(_display), out _, out _, out _, out _, out _, out _, out var mask);
            return (mask & 2) != 0;
        }
        catch
        {
            _displayFailed = true;
            return false;
        }
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr name);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XQueryPointer(IntPtr display, IntPtr window, out IntPtr root, out IntPtr child,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);

    /// <summary>CEF 按鍵 → X11 keysym（ibus 使用的鍵值）。</summary>
    private static uint ToKeysym(CefKeyEvent e)
    {
        switch (e.WindowsKeyCode)
        {
            case 0x08: return 0xff08; // BackSpace
            case 0x09: return 0xff09; // Tab
            case 0x0D: return 0xff0d; // Return
            case 0x10: return e.NativeKeyCode == 62 ? 0xffe2u : 0xffe1u; // Shift_R / Shift_L
            case 0x14: return 0xffe5; // Caps_Lock
            case 0x1B: return 0xff1b; // Escape
            case 0x20: return 0x20;   // space
            case 0x21: return 0xff55; // Page_Up
            case 0x22: return 0xff56; // Page_Down
            case 0x23: return 0xff57; // End
            case 0x24: return 0xff50; // Home
            case 0x25: return 0xff51; // Left
            case 0x26: return 0xff52; // Up
            case 0x27: return 0xff53; // Right
            case 0x28: return 0xff54; // Down
            case 0x2E: return 0xffff; // Delete
        }

        // 可列印字元：Latin-1 範圍 keysym 等於字碼，其餘為 0x01000000 + 字碼
        var ch = e.Character != 0 ? e.Character : e.UnmodifiedCharacter;
        if (ch >= 0x20 && ch != 0x7f)
        {
            return ch <= 0xff ? ch : 0x01000000u + ch;
        }

        // 放開按鍵時 CEF 可能不帶字元，依美式鍵盤配置從虛擬鍵碼換算
        var shift = e.Modifiers.HasFlag(CefEventFlags.ShiftDown);
        var vk = e.WindowsKeyCode;
        if (vk >= 0x41 && vk <= 0x5A) return (uint)(shift ? vk : vk + 0x20);
        if (vk >= 0x30 && vk <= 0x39) return shift ? ")!@#$%^&*("[vk - 0x30] : (uint)vk;
        return vk switch
        {
            0xBA => shift ? ':' : ';',
            0xBB => shift ? '+' : '=',
            0xBC => shift ? '<' : ',',
            0xBD => shift ? '_' : '-',
            0xBE => shift ? '>' : '.',
            0xBF => shift ? '?' : '/',
            0xC0 => shift ? '~' : '`',
            0xDB => shift ? '{' : '[',
            0xDC => shift ? '|' : '\\',
            0xDD => shift ? '}' : ']',
            0xDE => shift ? '"' : '\'',
            _ => 0
        };
    }

    public void Dispose()
    {
        _ime?.Dispose();
        _ime = null;
    }
}
