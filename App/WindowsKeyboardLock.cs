using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ByodKioskBrowser;

/// <summary>
/// Windows：考試期間攔截 Win 鍵、Alt+Tab、Alt+Esc、Ctrl+Esc、Ctrl+Shift+Esc、Alt+F4。
/// 以本行程的低階鍵盤掛鉤（WH_KEYBOARD_LL）實作：不需系統管理員權限、不寫登錄檔，
/// 程式結束（含被強制結束）時由系統自動移除，零殘留。
/// Ctrl+Alt+Del 與 Win+L 由系統保護、無法攔截，改由失焦偵測記錄。
/// 掛鉤必須安裝在有訊息迴圈的執行緒（Avalonia UI 執行緒）。
/// </summary>
internal sealed class WindowsKeyboardLock : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int LlkhfAltDown = 0x20;

    private const int VkTab = 0x09;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkEscape = 0x1B;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int VkApps = 0x5D;
    private const int VkF4 = 0x73;

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int VkCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    // 委派須以欄位保存，避免被 GC 回收後系統呼叫到已釋放的函式
    private readonly LowLevelKeyboardProc _proc;
    private readonly Action<string> _onBlocked;
    private IntPtr _hook;

    public WindowsKeyboardLock(Action<string> onBlocked)
    {
        _proc = HookCallback;
        _onBlocked = onBlocked;
    }

    /// <summary>安裝掛鉤；失敗回傳 false（不影響其他防弊機制）。</summary>
    public bool Install()
    {
        if (!OperatingSystem.IsWindows() || _hook != IntPtr.Zero)
        {
            return _hook != IntPtr.Zero;
        }
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(module?.ModuleName), 0);
        return _hook != IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = (int)wParam;
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var blocked = Describe(info);
            if (blocked != null)
            {
                // 只在按下時記錄一次（放開事件同樣攔下，但不重複記錄）
                if (msg is WmKeyDown or WmSysKeyDown)
                {
                    _onBlocked(blocked);
                }
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static string? Describe(KbdLlHookStruct info)
    {
        var alt = (info.Flags & LlkhfAltDown) != 0;
        var ctrl = (GetAsyncKeyState(VkControl) & 0x8000) != 0;
        var shift = (GetAsyncKeyState(VkShift) & 0x8000) != 0;
        return info.VkCode switch
        {
            VkLWin or VkRWin => "Win 鍵",
            VkApps => "選單鍵",
            VkTab when alt => "Alt+Tab",
            VkEscape when alt => "Alt+Esc",
            VkEscape when ctrl && shift => "Ctrl+Shift+Esc",
            VkEscape when ctrl => "Ctrl+Esc",
            VkF4 when alt => "Alt+F4",
            _ => null
        };
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
