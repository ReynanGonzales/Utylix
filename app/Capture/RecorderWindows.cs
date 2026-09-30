using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace IdmClone;

/// <summary>Small helpers for the little windows that appear while recording.</summary>
internal static class RecWin
{
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    /// <summary>Keep this window out of screen recordings and screenshots (Windows 10 2004 and later): you can use it, the video never shows it.</summary>
    public static void HideFromCapture(Window window) =>
        window.SourceInitialized += (_, _) => SetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, 0x11);

    /// <summary>Hide or show one window for the capture (used by windows that are only hidden while they are inside the recorded picture).</summary>
    /// <summary>Draw this window in software: on some graphics setups (virtual display drivers, remote-desktop tools) hardware-drawn overlay windows randomly stay blank.</summary>
    public static void SoftwareDraw(Window window) =>
        window.SourceInitialized += (_, _) =>
        {
            var target = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.CompositionTarget;
            if (target != null) target.RenderMode = RenderMode.SoftwareOnly;
        };

    public static void SetHidden(Window window, bool hidden)
    {
        var h = new WindowInteropHelper(window).Handle;
        if (h != IntPtr.Zero) SetWindowDisplayAffinity(h, hidden ? 0x11u : 0u);
    }

    /// <summary>Mouse clicks go straight through this window and it never takes the focus.</summary>
    public static void ClickThrough(Window window) =>
        window.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(window).Handle;
            SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x20 | 0x80 | 0x08000000);      // TRANSPARENT | TOOLWINDOW | NOACTIVATE
        };

    /// <summary>Mouse clicks go through this window (asked when Windows tests what is under the pointer), also when it is not layered.</summary>
    public static void PassClicks(Window window) =>
        window.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(window).Handle;
            SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x80 | 0x08000000);                       // TOOLWINDOW | NOACTIVATE
            HwndSource.FromHwnd(h)?.AddHook((IntPtr _, int msg, IntPtr __, IntPtr ___, ref bool handled) =>
            {
                if (msg != 0x0084) return IntPtr.Zero;                                              // WM_NCHITTEST
                handled = true;
                return new IntPtr(-1);                                                              // HTTRANSPARENT
            });
        };

    public static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    public static Style S(string key) => (Style)Application.Current.FindResource(key);
}

/// <summary>The little bar shown while recording: time, pause, stop. It is not part of the recording.</summary>
public sealed class RecorderBar : Window
{
    private readonly TextBlock _time = new() { Text = "00:00", FontSize = 15, FontWeight = FontWeights.SemiBold, MinWidth = 52, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _dot = new() { Width = 11, Height = 11, Fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _pause = new() { Content = "⏸  Pause", MinWidth = 92, Margin = new Thickness(12, 0, 0, 0) };
    private readonly Button _stop = new() { Content = "⏹  Stop", MinWidth = 84, Margin = new Thickness(8, 0, 0, 0) };
    private bool _paused;

    public event Action? PauseClicked;
    public event Action? StopClicked;

    public RecorderBar()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Utylix recording";
        Background = RecWin.B("CardBrush");
        Foreground = RecWin.B("TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13.5;
        AutomationProperties.SetAutomationId(_pause, "RecPause");
        AutomationProperties.SetAutomationId(_stop, "RecStop");
        AutomationProperties.SetAutomationId(_time, "RecTime");
        _pause.Style = RecWin.S("DialogButton");
        _stop.Style = RecWin.S("DialogPrimary");

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 9, 12, 9) };
        row.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/logo.png")), Width = 20, Height = 20, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(_dot);
        row.Children.Add(_time);
        row.Children.Add(_pause);
        row.Children.Add(_stop);
        Content = new Border { BorderBrush = RecWin.B("LineBrush"), BorderThickness = new Thickness(1), Child = row, Cursor = Cursors.SizeAll };

        _pause.Click += (_, _) => PauseClicked?.Invoke();
        _stop.Click += (_, _) => StopClicked?.Invoke();
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not (Button or System.Windows.Documents.Run) && e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch (InvalidOperationException) { } };
        RecWin.SoftwareDraw(this);
        RecWin.HideFromCapture(this);                                            // until we know where the recording is
        Loaded += (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            Left = work.Left + (work.Width - ActualWidth) / 2;
            Top = work.Bottom - ActualHeight - 28;
            Pulse(true);
            UpdateHiding();
        };
        LocationChanged += (_, _) => UpdateHiding();
    }

    private Int32Rect? _area;

    /// <summary>Tell the bar what is being recorded: it is only hidden from the capture while it sits over that picture, so it is always plainly visible elsewhere.</summary>
    public void SetArea(Int32Rect area) { _area = area; UpdateHiding(); }

    private void UpdateHiding()
    {
        if (_area is not { } a || !IsLoaded) return;
        double scale = ScreenGrab.Scale;
        var mine = new Rect(Left * scale, Top * scale, ActualWidth * scale, ActualHeight * scale);
        RecWin.SetHidden(this, mine.IntersectsWith(new Rect(a.X, a.Y, a.Width, a.Height)));
    }

    private void Pulse(bool on)
    {
        if (on) _dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        else { _dot.BeginAnimation(OpacityProperty, null); _dot.Opacity = 1; }
    }

    /// <param name="busy">Saving the file: the buttons wait.</param>
    public void Update(TimeSpan elapsed, bool paused, bool busy)
    {
        _time.Text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");
        if (paused != _paused)
        {
            _paused = paused;
            _pause.Content = paused ? "⏺  Resume" : "⏸  Pause";
            _dot.Fill = new SolidColorBrush(paused ? Color.FromRgb(0xF5, 0xA5, 0x24) : Color.FromRgb(0xE5, 0x48, 0x4D));
            Pulse(!paused);
        }
        UpdateHiding();
        _pause.IsEnabled = _stop.IsEnabled = !busy;
        if (busy) _time.Text += "  saving…";
    }
}

/// <summary>"3 … 2 … 1" in the middle of the screen before recording starts. Click it to cancel.</summary>
public sealed class CountdownWindow : Window
{
    private readonly TextBlock _number = new() { FontSize = 96, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TaskCompletionSource<bool> _done = new();

    private CountdownWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = Height = 190;
        Title = "Utylix countdown";
        Background = RecWin.B("CardBrush");
        Foreground = RecWin.B("TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        WindowStartupLocation = WindowStartupLocation.Manual;
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
        var hint = new TextBlock { Text = "click to cancel", FontSize = 11.5, Foreground = RecWin.B("MutedBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 10) };
        Content = new Border { BorderBrush = RecWin.B("LineBrush"), BorderThickness = new Thickness(1), Child = new Grid { Children = { _number, hint } } };
        MouseLeftButtonDown += (_, _) => _done.TrySetResult(false);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) _done.TrySetResult(false); };
        RecWin.SoftwareDraw(this);
        RecWin.HideFromCapture(this);
    }

    /// <summary>Counts down; false when the person cancelled it.</summary>
    public static async Task<bool> RunAsync(int seconds)
    {
        if (seconds <= 0) return true;
        var w = new CountdownWindow();
        w.Show();
        try
        {
            for (int n = seconds; n > 0; n--)
            {
                w._number.Text = n.ToString();
                var tick = Task.Delay(1000);
                if (await Task.WhenAny(tick, w._done.Task) != tick) return false;
            }
            return true;
        }
        finally { w.Close(); }
    }
}

/// <summary>A ring that flashes where you click while recording (the video shows it, so viewers see what you pressed).</summary>
public sealed class ClickEffects : IDisposable
{
    private const int WH_MOUSE_LL = 14, WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204;
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT Pt; public uint Data, Flags, Time; public UIntPtr Extra; }

    private readonly HookProc _proc;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Int32Rect _area;
    private IntPtr _hook;

    public ClickEffects(Int32Rect area)
    {
        _area = area;
        _proc = Hook;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
    }

    private IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN)
            {
                var pt = System.Runtime.InteropServices.Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).Pt;
                bool right = msg == WM_RBUTTONDOWN;
                if (pt.X >= _area.X && pt.X < _area.X + _area.Width && pt.Y >= _area.Y && pt.Y < _area.Y + _area.Height)
                    _dispatcher.BeginInvoke(new Action(() => Ripple(pt.X, pt.Y, right)));      // keep the hook quick
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static void Ripple(int x, int y, bool right)
    {
        double scale = ScreenGrab.Scale;
        const double size = 96;
        var color = right ? Color.FromRgb(0x4F, 0xA3, 0xFF) : Color.FromRgb(0xFF, 0xC8, 0x2E);
        var ring = new Ellipse
        {
            Width = size - 8, Height = size - 8, Stroke = new SolidColorBrush(color), StrokeThickness = 5,
            Fill = new SolidColorBrush(Color.FromArgb(70, color.R, color.G, color.B)),
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.35, 0.35),
        };
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false,
            Topmost = true, ResizeMode = ResizeMode.NoResize, Width = size, Height = size, Title = "",
            WindowStartupLocation = WindowStartupLocation.Manual, Left = x / scale - size / 2, Top = y / scale - size / 2,
            Content = new Grid { Children = { ring } },
        };
        RecWin.ClickThrough(w);
        w.Show();
        var time = TimeSpan.FromMilliseconds(480);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.35, 1, time) { EasingFunction = ease });
        ((ScaleTransform)ring.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.35, 1, time) { EasingFunction = ease });
        var fade = new DoubleAnimation(1, 0, time);
        fade.Completed += (_, _) => w.Close();
        ring.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}

/// <summary>
/// A red frame around what is being recorded, with a small "REC" tag carrying the Utylix logo, so it is always plain that a
/// recording is running. Windows keeps these out of the video (the same way as the control bar).
/// </summary>
public sealed class RecordingFrame
{
    private static readonly Color Red = Color.FromRgb(0xE5, 0x48, 0x4D), Amber = Color.FromRgb(0xF5, 0xA5, 0x24);
    private readonly List<Window> _strips = new();
    private readonly SolidColorBrush _brush = new(Red);
    private readonly Window _tag;
    private readonly TextBlock _tagText = new() { Text = "REC", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };

    public RecordingFrame(Int32Rect area)
    {
        // Sides that have room are drawn just OUTSIDE the recorded area: the video never contains them, and they do not depend on
        // Windows hiding a window from capture (which some screens / drivers get wrong). Where there is no room (full screen,
        // area at a screen edge) that side is drawn inside the edge and hidden from the capture instead.
        double scale = ScreenGrab.Scale;
        var vs = ScreenGrab.VirtualScreen;
        const int px = 4;                                                       // thickness, in screen pixels
        double d = px / scale;
        double x = area.X / scale, y = area.Y / scale, w = area.Width / scale, h = area.Height / scale;
        bool top = area.Y - px >= vs.Y, bottom = area.Y + area.Height + px <= vs.Y + vs.Height;
        bool left = area.X - px >= vs.X, right = area.X + area.Width + px <= vs.X + vs.Width;
        _strips.Add(top ? Strip(x - (left ? d : 0), y - d, w + (left ? d : 0) + (right ? d : 0), d, false) : Strip(x, y, w, d, true));
        _strips.Add(bottom ? Strip(x - (left ? d : 0), y + h, w + (left ? d : 0) + (right ? d : 0), d, false) : Strip(x, y + h - d, w, d, true));
        _strips.Add(left ? Strip(x - d, y, d, h, false) : Strip(x, y, d, h, true));
        _strips.Add(right ? Strip(x + w, y, d, h, false) : Strip(x + w - d, y, d, h, true));

        var tagRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 3, 10, 3) };
        tagRow.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/logo.png")), Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
        tagRow.Children.Add(_tagText);
        bool tagOutside = area.Y - px - 26 * scale >= vs.Y;
        _tag = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight, Title = "Utylix recording tag", WindowStartupLocation = WindowStartupLocation.Manual,
            Background = _brush, Content = tagRow, Left = x, Top = tagOutside ? y - d - 26 : y + d,
        };
        if (!tagOutside) RecWin.HideFromCapture(_tag);                          // inside the picture: Windows keeps it out of the video
        RecWin.SoftwareDraw(_tag);
        RecWin.PassClicks(_tag);
    }

    private Window Strip(double left, double top, double width, double height, bool hide)
    {
        var w = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            Title = "Utylix recording frame", WindowStartupLocation = WindowStartupLocation.Manual,
            Left = left, Top = top, Width = width, Height = height, MinWidth = 1, MinHeight = 1, Background = _brush,
        };
        if (hide) RecWin.HideFromCapture(w);
        RecWin.SoftwareDraw(w);
        RecWin.PassClicks(w);
        return w;
    }

    public void Show()
    {
        foreach (var w in _strips) w.Show();
        _tag.Show();
    }

    public void SetPaused(bool paused)
    {
        _brush.Color = paused ? Amber : Red;
        _tagText.Text = paused ? "PAUSED" : "REC";
    }

    public void Close()
    {
        foreach (var w in _strips) w.Close();
        _tag.Close();
    }
}
