using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// The frames-per-second counter on top of a program ("Show FPS" in Memory and CPU limits), like RivaTuner's. The number is what Utylix's frame hook inside the
/// program counts (see FpsHook); the counter itself is a small transparent window of Utylix that mouse clicks go through, put in the top-left corner of the
/// program's window while that program is in front. Nothing runs while no row asks for it (the timer is only there while a row has "Show FPS" on).
/// A program in true exclusive full screen can cover it (borderless / windowed full screen is fine).
/// </summary>
internal static class FpsOverlay
{
    private static DispatcherTimer? _timer;
    private static readonly Dictionary<int, FpsOverlayWindow> _windows = new();
    private static List<MemoryRule> _rules = new();
    private static DateTime _rulesAt = DateTime.MinValue;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    /// <summary>Starts or stops the counters to match the list (called at start and whenever the list is saved).</summary>
    public static void Apply(string dataDir)
    {
        _rulesAt = DateTime.MinValue;
        bool want = FpsHook.Available && MemoryLimits.Load(dataDir).Any(r => r.Enabled && r.ShowFps);
        if (want && _timer == null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (_, _) => Tick(dataDir);
            _timer.Start();
        }
        else if (!want && _timer != null)
        {
            _timer.Stop(); _timer = null;
            foreach (var w in _windows.Values) w.Close();
            _windows.Clear();
        }
    }

    private static void Tick(string dataDir)
    {
        if (DateTime.UtcNow - _rulesAt > TimeSpan.FromSeconds(3)) { _rules = MemoryLimits.Load(dataDir).Where(r => r.Enabled && r.ShowFps).ToList(); _rulesAt = DateTime.UtcNow; }
        GetWindowThreadProcessId(GetForegroundWindow(), out uint front);
        var seen = new HashSet<int>();
        foreach (var rule in _rules)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(rule.Exe)); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { continue; }
            foreach (var p in procs)
            {
                try
                {
                    IntPtr hwnd = p.MainWindowHandle;
                    if (hwnd == IntPtr.Zero || FpsHook.ReadFps(dataDir, p.Id) is not { } fps) continue;
                    seen.Add(p.Id);
                    var corner = new POINT();                                                 // the top-left of the window's content (below a title bar, if it has one)
                    bool show = front == (uint)p.Id && !IsIconic(hwnd) && ClientToScreen(hwnd, ref corner);
                    if (!_windows.TryGetValue(p.Id, out var w)) _windows[p.Id] = w = new FpsOverlayWindow();
                    w.Update(fps, show, corner.X + 12, corner.Y + 10);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                finally { p.Dispose(); }
            }
        }
        foreach (int gone in _windows.Keys.Where(k => !seen.Contains(k)).ToList()) { _windows[gone].Close(); _windows.Remove(gone); }
    }
}

/// <summary>The counter: bold text with a dark outline, no frame, never takes the focus or a click.</summary>
internal sealed class FpsOverlayWindow : Window
{
    private readonly TextBlock _text = new()
    {
        FontFamily = new FontFamily("Segoe UI Semibold"), FontSize = 24, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0xF0, 0x6B)),
        Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 5, ShadowDepth = 0, Opacity = 1 },
    };

    [DllImport("user32.dll")] private static extern IntPtr SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr h, int index, int value);

    public FpsOverlayWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = true;
        ShowActivated = false; Focusable = false; ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight;
        Content = new Border { Padding = new Thickness(2), Child = _text };
        SourceInitialized += (_, _) =>
        {
            IntPtr h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x20 | 0x08000000 | 0x80);       // click-through, never active, not in Alt+Tab
        };
    }

    public void Update(double fps, bool visible, int x, int y)
    {
        _text.Text = $"{fps:0} FPS";
        if (!visible) { if (IsVisible) Hide(); return; }
        if (!IsVisible) Show();
        IntPtr h = new WindowInteropHelper(this).Handle;
        SetWindowPos(h, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010);                      // keep on top: no size change, no activation
    }
}
