using System.Runtime.InteropServices;

namespace WinBoost.Services;

/// <summary>
/// Enforces a minimum window size (WinAppSDK 1.6 has no PreferredMinimumWidth): subclasses the HWND with
/// comctl32 SetWindowSubclass and answers WM_GETMINMAXINFO with the DIP minimum scaled by GetDpiForWindow.
/// </summary>
public static class WindowMinSize
{
    private const uint WM_GETMINMAXINFO = 0x0024;
    private const int OffsetMinTrack = 24; // MINMAXINFO.ptMinTrackSize (after ptReserved, ptMaxSize, ptMaxPosition)
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, IntPtr id, IntPtr data);
    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private static SubclassProc? _proc; // kept alive for the lifetime of the window
    private static int _minW, _minH;

    public static double Scale(IntPtr hwnd) { try { uint d = GetDpiForWindow(hwnd); return d == 0 ? 1 : d / 96.0; } catch { return 1; } }

    public static void Apply(IntPtr hwnd, int minWidthDip, int minHeightDip)
    {
        try
        {
            _minW = minWidthDip; _minH = minHeightDip;
            _proc = Proc;
            if (!SetWindowSubclass(hwnd, _proc, (IntPtr)0x57B0, IntPtr.Zero))
                AppLog.Warn($"SetWindowSubclass failed ({Marshal.GetLastWin32Error()})");
            else AppLog.Info($"Minimum window size {minWidthDip}x{minHeightDip} DIP enforced");
        }
        catch (Exception ex) { AppLog.Warn("Min size hook failed: " + ex.Message); }
    }

    private static IntPtr Proc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        if (msg == WM_GETMINMAXINFO && lParam != IntPtr.Zero)
        {
            try
            {
                double s = Scale(hWnd);
                Marshal.WriteInt32(lParam, OffsetMinTrack, (int)Math.Ceiling(_minW * s));
                Marshal.WriteInt32(lParam, OffsetMinTrack + 4, (int)Math.Ceiling(_minH * s));
            }
            catch { }
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }
}
