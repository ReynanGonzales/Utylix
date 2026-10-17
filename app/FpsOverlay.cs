using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
/// The on-screen display on top of a program ("Show FPS" in Memory and CPU limits), in the style of RivaTuner / MSI Afterburner: either only the frames per second,
/// or the full display (graphics card, its memory, processor, RAM, graphics API and frames per second). The frame rate is what Utylix's frame hook inside the
/// program counts (see FpsHook); the display itself is a small transparent window of Utylix that mouse clicks go through, in the top-left corner of the
/// program's window while that program is in front. Nothing runs while no row asks for it, and the sensors (OsdSensors) are only open while a full display
/// is on screen. A program in true exclusive full screen can cover it (borderless / windowed full screen is fine).
/// </summary>
internal static class FpsOverlay
{
    private static DispatcherTimer? _timer;
    private static readonly Dictionary<int, FpsOverlayWindow> _windows = new();
    private static List<MemoryRule> _rules = new();
    private static DateTime _rulesAt = DateTime.MinValue;
    private static OsdSensors? _sensors;
    private static DateTime _sensorsAt = DateTime.MinValue, _fullSeenAt = DateTime.MinValue;
    private static OsdReading? _reading;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    internal delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    private static IntPtr _foregroundHook;
    private static readonly WinEventProc _foregroundProc = (_, _, _, _, _, _, _) => { foreach (var w in _windows.Values) w.Follow(); };       // (the window in front changed: show / hide at once)

    /// <summary>Starts or stops the displays to match the list (called at start and whenever the list is saved).</summary>
    public static void Apply(string dataDir)
    {
        _rulesAt = DateTime.MinValue;
        bool want = FpsHook.Available && MemoryLimits.Load(dataDir).Any(r => r.Enabled && r.ShowFps);
        if (want && _timer == null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (_, _) => Tick(dataDir);
            _timer.Start();
            _foregroundHook = SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, _foregroundProc, 0, 0, 0);       // EVENT_SYSTEM_FOREGROUND, called on this thread
        }
        else if (!want && _timer != null)
        {
            _timer.Stop(); _timer = null;
            if (_foregroundHook != IntPtr.Zero) { UnhookWinEvent(_foregroundHook); _foregroundHook = IntPtr.Zero; }
            foreach (var w in _windows.Values) w.Close();
            _windows.Clear();
            _sensors?.Dispose(); _sensors = null;
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
                    if (hwnd == IntPtr.Zero || FpsHook.ReadFps(dataDir, p.Id) is not { } frame) continue;
                    seen.Add(p.Id);
                    int mode = rule.OsdMode == 1 ? 1 : 0;
                    if (_windows.TryGetValue(p.Id, out var old) && (old.Mode != mode || old.Game != hwnd)) { old.Close(); _windows.Remove(p.Id); }
                    if (!_windows.TryGetValue(p.Id, out var w)) _windows[p.Id] = w = new FpsOverlayWindow(mode, hwnd, (uint)p.Id);
                    OsdReading? reading = null;
                    if (mode == 1 && front == (uint)p.Id) reading = Sensors(p.Id);
                    w.Update(frame.Fps, frame.Api, reading);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                finally { p.Dispose(); }
            }
        }
        foreach (int gone in _windows.Keys.Where(k => !seen.Contains(k)).ToList()) { _windows[gone].Close(); _windows.Remove(gone); }
        // the sensors are closed again a little after the last full display was on screen
        if (_sensors != null && DateTime.UtcNow - _fullSeenAt > TimeSpan.FromSeconds(20)) { _sensors.Dispose(); _sensors = null; _reading = null; }
    }

    /// <summary>The sensors, read once a second (opened when the first full display needs them).</summary>
    private static OsdReading Sensors(int pid)
    {
        _fullSeenAt = DateTime.UtcNow;
        if (_reading == null || DateTime.UtcNow - _sensorsAt >= TimeSpan.FromSeconds(1))
        {
            _sensors ??= new OsdSensors();
            _reading = _sensors.Read(pid);
            _sensorsAt = DateTime.UtcNow;
        }
        return _reading;
    }
}

/// <summary>The display: bold text with a thin dark shadow, no frame, never takes the focus or a click. Orange values, coloured labels (like Afterburner's).</summary>
internal sealed class FpsOverlayWindow : Window
{
    private static readonly Brush Orange = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x1A)));
    private static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x62, 0xD0, 0x5A)));
    private static readonly Brush Blue = Freeze(new SolidColorBrush(Color.FromRgb(0x4F, 0x9B, 0xEA)));
    private static readonly Brush Pink = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0x70, 0x8A)));
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    public int Mode { get; }
    public IntPtr Game { get; }                                     // the program's window this display sits on
    private readonly uint _pid;
    private IntPtr _moveHook;
    private FpsOverlay.WinEventProc? _moveProc;                     // (kept: the hook calls it)
    private readonly TextBlock _fpsOnly = new();
    private readonly Dictionary<string, TextBlock[]> _cells = new();
    private readonly TextBlock _apiLabel = new();

    [DllImport("user32.dll")] private static extern IntPtr SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr h, int index, int value);

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, FpsOverlay.WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    public FpsOverlayWindow(int mode, IntPtr game, uint pid)
    {
        Mode = mode; Game = game; _pid = pid;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = true;
        ShowActivated = false; Focusable = false; ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Consolas"); FontSize = 16; FontWeight = FontWeights.Bold;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Content = new Border { Padding = new Thickness(2), Child = mode == 1 ? BuildFull() : BuildFpsOnly(), Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 0, ShadowDepth = 1.5, Direction = 315, Opacity = 1 } };
        SourceInitialized += (_, _) =>
        {
            IntPtr h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x20 | 0x08000000 | 0x80);       // click-through, never active, not in Alt+Tab
        };
        // the display follows the program's window the moment it moves, is resized, minimised or restored (not on the next look of a timer)
        _moveProc = (_, _, hwnd, idObject, _, _, _) => { if (hwnd == Game && idObject == 0) Follow(); };
        _moveHook = SetWinEventHook(0x800B, 0x800B, IntPtr.Zero, _moveProc, pid, 0, 0);     // EVENT_OBJECT_LOCATIONCHANGE of that program, called on this thread
        Closed += (_, _) => { if (_moveHook != IntPtr.Zero) { UnhookWinEvent(_moveHook); _moveHook = IntPtr.Zero; } };
    }

    /// <summary>Puts the display in the corner of the program's window, or hides it when that program is not in front / is minimised.</summary>
    public void Follow()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint front);
        var corner = new POINT();
        bool show = front == _pid && !IsIconic(Game) && ClientToScreen(Game, ref corner);
        if (!show) { if (IsVisible) Hide(); return; }
        if (!IsVisible) Show();
        IntPtr h = new WindowInteropHelper(this).Handle;
        SetWindowPos(h, new IntPtr(-1), corner.X + 12, corner.Y + 10, 0, 0, 0x0001 | 0x0010);   // top-left of the content, on top, no size change, no activation
    }

    private UIElement BuildFpsOnly()
    {
        _fpsOnly.FontSize = 22; _fpsOnly.Foreground = Orange;
        return _fpsOnly;
    }

    private UIElement BuildFull()
    {
        var grid = new Grid();
        int rowNo = 0;
        void Row(string key, string label, Brush color, int cellCount)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = key == "api" ? _apiLabel : new TextBlock();
            l.Text = label; l.Foreground = color; l.Margin = new Thickness(0, 0, 10, 0);
            Grid.SetRow(l, rowNo); Grid.SetColumn(l, 0); grid.Children.Add(l);
            var cells = new TextBlock[cellCount];
            for (int i = 0; i < cellCount; i++)
            {
                var c = new TextBlock { Foreground = Orange, TextAlignment = TextAlignment.Right, MinWidth = 84, Margin = new Thickness(6, 0, 0, 0) };
                Grid.SetRow(c, rowNo); Grid.SetColumn(c, i + 1); grid.Children.Add(c);
                cells[i] = c;
            }
            _cells[key] = cells; rowNo++;
        }
        for (int i = 0; i < 6; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Row("gpu", "GPU", Green, 5);
        Row("mem", "MEM", Green, 3);
        Row("cpu", "CPU", Blue, 4);
        Row("ram", "RAM", Blue, 2);
        Row("api", "D3D", Pink, 1);
        return grid;
    }

    private static void Set(TextBlock cell, double? value, string unit, string format = "0")
    {
        cell.Inlines.Clear();
        cell.Inlines.Add(new System.Windows.Documents.Run(value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) : "--"));
        cell.Inlines.Add(new System.Windows.Documents.Run(" " + unit) { FontSize = 12, FontWeight = FontWeights.Normal });
    }

    public void Update(double fps, string api, OsdReading? r)
    {
        if (Mode == 0) Set(_fpsOnly, fps, "FPS");
        else
        {
            if (api.Length > 0) _apiLabel.Text = api;
            Set(_cells["api"][0], fps, "FPS");
            if (r != null)
            {
                var g = _cells["gpu"]; Set(g[0], r.GpuTemp, "°C"); Set(g[1], r.GpuLoad, "%"); Set(g[2], r.GpuClock, "MHz"); Set(g[3], r.GpuPower, "W", "0.0"); Set(g[4], r.GpuFan, "%");
                var m = _cells["mem"]; Set(m[0], r.VramUsedMb, "MB"); Set(m[1], r.VramGameMb, "MB"); Set(m[2], r.VramClock, "MHz");
                var c = _cells["cpu"]; Set(c[0], r.CpuTemp, "°C"); Set(c[1], r.CpuLoad, "%"); Set(c[2], r.CpuClock, "MHz"); Set(c[3], r.CpuPower, "W", "0.0");
                var ram = _cells["ram"]; Set(ram[0], r.RamUsedMb, "MB"); Set(ram[1], r.RamGameMb, "MB");
            }
        }
        Follow();                                                                           // (also re-asserts "on top": a game can push its own window above)
    }
}
