using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IdmClone;

/// <summary>An open program window a person could choose to record.</summary>
public sealed class OpenWindow
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; } = "";
    /// <summary>"Brave Browser", "Notepad" ... (the program's own name).</summary>
    public string App { get; init; } = "";
    public ImageSource? Icon { get; init; }
    public bool Minimized { get; init; }
    public string Subtitle => Minimized ? App + "  ·  minimized" : App;
}

/// <summary>Finds the windows that are open (what Alt+Tab would list) and brings one to the front.</summary>
public static class WindowList
{
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attribute, out RECT rect, int size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr ExtractIcon(IntPtr instance, string file, int index);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    private static readonly Dictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);

    public static List<OpenWindow> Enumerate()
    {
        var list = new List<OpenWindow>();
        uint me = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            try
            {
                if (!IsWindowVisible(h)) return true;
                if (GetWindow(h, 4) != IntPtr.Zero) return true;                                        // owned pop-ups and dialogs belong to another window
                if ((GetWindowLong(h, -20) & 0x80) != 0 && (GetWindowLong(h, -20) & 0x40000) == 0) return true;   // tool windows (tray pop-ups)
                if (DwmGetWindowAttribute(h, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;   // on another desktop / hidden Store app
                string title = TextOf(h);
                if (title.Length == 0) return true;
                string cls = ClassOf(h);
                if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow") return true;
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == me) return true;
                bool minimized = IsIconic(h);
                if (!minimized)
                {
                    var b = BoundsOf(h);
                    if (b == null || b.Value.Width < 80 || b.Value.Height < 50) return true;
                }
                var (app, exe) = AppOf(pid, title);
                if (exe != null && Path.GetFileName(exe).Equals("Utylix.exe", StringComparison.OrdinalIgnoreCase)) return true;      // any Utylix window (also another copy)
                list.Add(new OpenWindow { Handle = h, Title = title, App = app, Icon = IconOf(exe), Minimized = minimized });
            }
            catch (Exception) { /* a window that vanished while we looked: skip it */ }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static bool Exists(IntPtr h) => IsWindow(h) && IsWindowVisible(h);

    /// <summary>The window's real frame in screen pixels (without the invisible resize border), or null.</summary>
    public static Int32Rect? BoundsOf(IntPtr h)
    {
        if (DwmGetWindowAttribute(h, 9, out RECT r, Marshal.SizeOf<RECT>()) != 0) return null;
        return new Int32Rect(r.L, r.T, r.R - r.L, r.B - r.T);
    }

    /// <summary>Un-minimize the window and put it in front, so that what is on screen there is that window.</summary>
    public static void Show(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, 9);                                                              // SW_RESTORE
        ScreenGrab.ForceForeground(h);
    }

    private static string TextOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString().Trim();
    }

    private static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(128);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static (string App, string? Exe) AppOf(uint pid, string title)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            if (process.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) return ("Windows app", null);
            string? exe = null;
            try { exe = process.MainModule?.FileName; } catch (Exception) { /* a program running as administrator: its file name is not readable */ }
            if (exe != null)
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                string name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription! : !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName! : Path.GetFileNameWithoutExtension(exe);
                return (name.Trim(), exe);
            }
            return (process.ProcessName, null);
        }
        catch (Exception) { return ("", null); }
    }

    private static ImageSource? IconOf(string? exe)
    {
        if (exe == null) return null;
        if (Icons.TryGetValue(exe, out var cached)) return cached;
        ImageSource? source = null;
        IntPtr icon = ExtractIcon(IntPtr.Zero, exe, 0);
        if (icon.ToInt64() > 1)
        {
            try
            {
                source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
            }
            finally { DestroyIcon(icon); }
        }
        Icons[exe] = source;
        return source;
    }
}
