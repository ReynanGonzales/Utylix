using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace IdmClone;

/// <summary>
/// Scrolling the mouse wheel over the browser's floating "Picture in picture" window means "next / previous video" (Reels, Shorts...).
/// The browser's own floating window never passes the wheel on to the page, so the program watches for it: while the browser
/// extension says a video is floating, a mouse hook looks at wheel turns, and only those over a window titled "Picture in picture"
/// are noted (and kept from going anywhere else). Nothing else is looked at, recorded or stored. The extension collects the notes
/// through /api/pip/wait and tells the page to move to the next or previous video.
/// </summary>
internal static class PipWheel
{
    private const int WH_MOUSE_LL = 14, WM_MOUSEWHEEL = 0x020A, GA_ROOT = 2;
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT Pt; public uint MouseData, Flags, Time; public UIntPtr Extra; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);

    private static readonly object Lock = new();
    private static IntPtr _hook;
    private static HookProc? _proc;
    private static long _id;
    private static int _dir;
    private static long _lastTick;
    private static Timer? _expire;

    /// <summary>The extension says a video is (or is no longer) floating. The hook only exists while one is.</summary>
    public static void SetActive(bool on)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        dispatcher.BeginInvoke(new Action(() =>
        {
            lock (Lock)
            {
                if (on && _hook == IntPtr.Zero)
                {
                    _proc = Hook;
                    _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
                }
                else if (!on && _hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
                _expire?.Dispose();
                _expire = on ? new Timer(_ => SetActive(false), null, TimeSpan.FromHours(2), Timeout.InfiniteTimeSpan) : null;     // never stay hooked for ever
            }
        }));
    }

    private static IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam.ToInt32() == WM_MOUSEWHEEL)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (OverFloatingWindow(data.Pt))
            {
                short delta = (short)(data.MouseData >> 16);
                long now = Environment.TickCount64;
                lock (Lock)
                {
                    if (now - _lastTick > 450)                                  // a wheel turn sends several notches: one step per flick
                    {
                        _lastTick = now;
                        _dir = delta < 0 ? 1 : -1;                              // wheel down = next video, wheel up = previous
                        _id++;
                    }
                }
                return (IntPtr)1;                                               // the floating window has no use for it
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool OverFloatingWindow(POINT pt)
    {
        try
        {
            var hwnd = WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return false;
            var root = GetAncestor(hwnd, GA_ROOT);
            if (root == IntPtr.Zero) root = hwnd;
            var cls = new StringBuilder(64); GetClassName(root, cls, 64);
            if (!cls.ToString().StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)) return false;      // Chrome, Edge, Brave ...
            var title = new StringBuilder(128); GetWindowText(root, title, 128);
            return title.ToString().Contains("picture in picture", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ExternalException or InvalidOperationException) { return false; }
    }

    /// <summary>Waits (up to <paramref name="wait"/>) for a wheel turn after the one numbered <paramref name="after"/>; after = -1 answers at once with the current number.</summary>
    public static (long Id, int Dir) Wait(long after, TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            lock (Lock)
            {
                if (after < 0) return (_id, 0);
                if (_id > after) return (_id, _dir);
            }
            if (DateTime.UtcNow >= until) { lock (Lock) return (_id, 0); }
            Thread.Sleep(60);
        }
    }
}
