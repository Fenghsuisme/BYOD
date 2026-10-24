using System;
using System.Runtime.InteropServices;

namespace ByodKioskBrowser;

/// <summary>
/// 查詢 X 的鍵盤焦點是否仍在本 App 視窗內。
/// 兩個 CEF 瀏覽器是主視窗的 X 子視窗；把鍵盤焦點交給它們時，Avalonia 主視窗會收到 Deactivated，
/// 但這不是真的離開 App，不能算失焦。
/// </summary>
internal static class X11Focus
{
    private static IntPtr _display;
    private static bool _failed;

    /// <summary>焦點在 toplevel 或其子孫視窗內回傳 true；不在回傳 false；無法判斷（非 Linux / X 錯誤）回傳 null。</summary>
    public static bool? IsInside(IntPtr toplevel)
    {
        if (!OperatingSystem.IsLinux() || toplevel == IntPtr.Zero || _failed)
        {
            return null;
        }

        try
        {
            if (_display == IntPtr.Zero)
            {
                _display = XOpenDisplay(IntPtr.Zero);
                if (_display == IntPtr.Zero)
                {
                    _failed = true;
                    return null;
                }
            }

            XGetInputFocus(_display, out var window, out _);
            // 0 = None、1 = PointerRoot：焦點不在任何視窗
            for (var depth = 0; window != IntPtr.Zero && window != (IntPtr)1 && depth < 64; depth++)
            {
                if (window == toplevel)
                {
                    return true;
                }
                if (XQueryTree(_display, window, out var root, out var parent, out var children, out _) == 0)
                {
                    return null;
                }
                if (children != IntPtr.Zero)
                {
                    XFree(children);
                }
                if (parent == root)
                {
                    break;
                }
                window = parent;
            }
            return false;
        }
        catch
        {
            _failed = true;
            return null;
        }
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr name);

    [DllImport("libX11.so.6")]
    private static extern int XGetInputFocus(IntPtr display, out IntPtr focus, out int revertTo);

    [DllImport("libX11.so.6")]
    private static extern int XQueryTree(IntPtr display, IntPtr window, out IntPtr root, out IntPtr parent,
        out IntPtr children, out uint childCount);

    [DllImport("libX11.so.6")]
    private static extern int XFree(IntPtr data);
}
