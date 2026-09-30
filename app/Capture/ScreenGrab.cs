using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IdmClone;

/// <summary>A window on screen (bounds in screen pixels).</summary>
public sealed record ScreenWindow(System.Windows.Rect Bounds, string Title);

/// <summary>Taking pictures of the screen, and finding the windows on it.</summary>
public static class ScreenGrab
{
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT rect, int size);

    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private delegate bool EnumProc(IntPtr h, IntPtr l);

    /// <summary>Make a window the active one even when the shortcut that opened it gave us no "input rights".</summary>
    public static void ForceForeground(IntPtr window)
    {
        uint me = GetCurrentThreadId();
        GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint owner = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        bool attached = owner != 0 && owner != me && AttachThreadInput(me, owner, true);
        try { BringWindowToTop(window); SetForegroundWindow(window); }
        finally { if (attached) AttachThreadInput(me, owner, false); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    /// <summary>Everything on all screens, in screen pixels (the top-left can be negative with several monitors).</summary>
    public static Int32Rect VirtualScreen =>
        new(GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    /// <summary>Screen pixels per WPF unit (1.0 at 100% scaling, 1.25 at 125% ...).</summary>
    public static double Scale => Math.Max(1.0, GetDpiForSystem() / 96.0);

    /// <summary>One picture of the whole desktop, including semi-transparent windows.</summary>
    public static BitmapSource CaptureVirtualScreen(out Int32Rect area)
    {
        area = VirtualScreen;
        var fromGdi = CopyWithBitBlt(area);
        if (fromGdi != null) return fromGdi;

        // fallback: the managed copy (leaves out semi-transparent windows)
        using var bmp = new Bitmap(area.Width, area.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(area.X, area.Y, 0, 0, new System.Drawing.Size(area.Width, area.Height), CopyPixelOperation.SourceCopy);
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        try
        {
            var source = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgr32, null, data.Scan0, data.Stride * bmp.Height, data.Stride);
            var opaque = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            opaque.Freeze();
            return opaque;
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>The classic screen copy: BitBlt with CAPTUREBLT (which is what includes layered windows).</summary>
    private static BitmapSource? CopyWithBitBlt(Int32Rect area)
    {
        IntPtr screen = GetDC(IntPtr.Zero), mem = IntPtr.Zero, hbmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            mem = CreateCompatibleDC(screen);
            hbmp = CreateCompatibleBitmap(screen, area.Width, area.Height);
            if (mem == IntPtr.Zero || hbmp == IntPtr.Zero) return null;
            old = SelectObject(mem, hbmp);
            if (!BitBlt(mem, 0, 0, area.Width, area.Height, screen, area.X, area.Y, 0x00CC0020 | 0x40000000)) return null;
            SelectObject(mem, old); old = IntPtr.Zero;
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var flat = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);      // fully opaque: a screen has no transparency
            flat.Freeze();
            return flat;
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Top-level windows a person would call "a window", top-most first.</summary>
    public static List<ScreenWindow> VisibleWindows()
    {
        var list = new List<ScreenWindow>();
        uint me = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == me && GetTitle(h).Length == 0) return true;                      // our own overlay
            if ((GetWindowLong(h, -20) & 0x80) != 0) return true;                         // tool windows (tray pop-ups)
            if (DwmGetWindowAttribute(h, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;   // on another desktop / hidden Store app
            if (DwmGetWindowAttribute(h, 9, out RECT r, Marshal.SizeOf<RECT>()) != 0) return true;              // real frame without the invisible resize border
            var bounds = new System.Windows.Rect(r.L, r.T, r.R - r.L, r.B - r.T);
            if (bounds.Width < 60 || bounds.Height < 40) return true;
            list.Add(new ScreenWindow(bounds, GetTitle(h)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static string GetTitle(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>The part of a desktop picture inside a rectangle given in screen pixels.</summary>
    public static BitmapSource Crop(BitmapSource shot, Int32Rect screenArea, System.Windows.Rect wanted)
    {
        int x = (int)Math.Round(wanted.X) - screenArea.X, y = (int)Math.Round(wanted.Y) - screenArea.Y;
        int w = (int)Math.Round(wanted.Width), h = (int)Math.Round(wanted.Height);
        x = Math.Max(0, x); y = Math.Max(0, y);
        w = Math.Min(w, shot.PixelWidth - x); h = Math.Min(h, shot.PixelHeight - y);
        var crop = new CroppedBitmap(shot, new Int32Rect(x, y, Math.Max(1, w), Math.Max(1, h)));
        crop.Freeze();
        return crop;
    }
}
