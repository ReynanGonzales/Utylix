using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// The "Free up memory" entry of the desktop right-click menu: this copy of Utylix only does that, shows a small notice in the corner of the
/// screen (at once "Freeing up memory…", then what it freed) and ends. It does not touch the Utylix that may be running in the tray.
/// </summary>
public static class MemoryToast
{
    private static Window? _window;
    private static Border? _card;
    private static DispatcherTimer? _timer;
    private static bool _closeOnClick;

    public static void Run(bool deep, string? rememberSid = null)
    {
        var app = Application.Current;
        Open(deep ? "Freeing up memory…" : "Freeing up memory…", deep ? "Clearing Windows' cached memory too. This can take a few seconds." : "");
        // started as administrator for the deep clean: do it at once; otherwise a quick trim first (no permission needed)
        Task.Run(() =>
        {
            var result = MemoryCleaner.Free(deep && MemoryCleaner.IsElevated);
            bool silent = false;
            // the person said yes once: from now on the desktop entry does this without asking
            if (deep && MemoryCleaner.IsElevated && !string.IsNullOrEmpty(rememberSid) && MemoryTask.CanBeSilent) silent = MemoryTask.Install(rememberSid);
            return (result, silent);
        }).ContinueWith(t => app.Dispatcher.Invoke(() => ShowResult(t.IsFaulted ? null : t.Result.result, t.IsFaulted ? false : t.Result.silent)));
    }

    private static Border Card(UIElement content)
    {
        var bg = (Brush)Application.Current.FindResource("CardBrush");
        var line = (Brush)Application.Current.FindResource("LineBrush");
        return new Border { Background = bg, BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = content };
    }

    private static StackPanel Lines(string title, string body)
    {
        var text = (Brush)Application.Current.FindResource("TextBrush");
        var muted = (Brush)Application.Current.FindResource("MutedBrush");
        var panel = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = text });
        if (body.Length > 0)
            panel.Children.Add(new TextBlock { Text = body, FontSize = 12.5, Foreground = muted, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private static void Open(string title, string body)
    {
        _card = Card(Lines(title, body));
        _window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, ShowActivated = false,
            SizeToContent = SizeToContent.WidthAndHeight, MinWidth = 300, MaxWidth = 460, Background = Brushes.Transparent, AllowsTransparency = true,
            Content = _card, FontFamily = new FontFamily("Segoe UI"),
        };
        void Place()
        {
            var wa = SystemParameters.WorkArea;
            _window!.Left = wa.Right - _window.ActualWidth - 16;
            _window.Top = wa.Bottom - _window.ActualHeight - 16;
        }
        _window.Loaded += (_, _) => Place();
        _window.SizeChanged += (_, _) => Place();
        _window.MouseLeftButtonUp += (_, _) => { if (_closeOnClick) Application.Current.Shutdown(); };
        _window.Show();
    }

    private static void ShowResult(MemoryCleaner.Result? r, bool nowSilent)
    {
        var accent = (Brush)Application.Current.FindResource("AccentBrush");
        string title, body;
        bool offerDeep = false;
        if (r == null) { title = "Couldn't free memory"; body = "Windows did not let Utylix reach the running programs."; }
        else
        {
            long freed = Math.Max(0, r.FreeAfter - r.FreeBefore);
            title = freed >= 1 << 20 ? "Freed " + MemoryCleaner.Bytes(freed) : "Memory is already tidy";
            body = $"{MemoryCleaner.Bytes(r.FreeAfter)} free of {MemoryCleaner.Bytes(r.Total)}  ·  {r.Trimmed} programs trimmed";
            if (r.Deep) body += "\nWindows' cached memory was cleared too." + (nowSilent ? "\nNext time it will not ask again." : "");
            else if (!MemoryCleaner.IsElevated) offerDeep = true;
        }

        var panel = Lines(title, body);
        if (offerDeep)
        {
            var deepLink = new TextBlock
            {
                Text = "Also clear Windows' cached memory (asks for permission once)", FontSize = 12.5, Foreground = accent, Margin = new Thickness(0, 8, 0, 0),
                Cursor = Cursors.Hand, TextDecorations = TextDecorations.Underline,
            };
            panel.Children.Add(deepLink);
            deepLink.MouseLeftButtonUp += (_, _) =>
            {
                try
                {
                    string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
                    Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--free-memory-deep" + (sid.Length > 0 ? " --remember " + sid : "")) { UseShellExecute = true, Verb = "runas" });
                }
                catch (System.ComponentModel.Win32Exception) { /* permission was refused */ }
                Application.Current.Shutdown();
            };
        }
        _card!.Child = panel;
        _closeOnClick = !offerDeep;
        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(offerDeep ? 9 : 4.5) };
        _timer.Tick += (_, _) => { _timer.Stop(); Application.Current.Shutdown(); };
        _timer.Start();
    }
}
