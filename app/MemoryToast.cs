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
/// screen with what it freed, and ends. It does not touch the Utylix that may be running in the tray.
/// </summary>
public static class MemoryToast
{
    public static void Run(bool deep)
    {
        var app = Application.Current;
        // started as administrator for the deep clean: do it at once; otherwise a quick trim first (no permission needed)
        Task.Run(() => MemoryCleaner.Free(deep && MemoryCleaner.IsElevated)).ContinueWith(t =>
            app.Dispatcher.Invoke(() => Show(t.IsFaulted ? null : t.Result, deep)));
    }

    private static void Show(MemoryCleaner.Result? r, bool askedDeep)
    {
        var accent = (Brush)Application.Current.FindResource("AccentBrush");
        var bg = (Brush)Application.Current.FindResource("CardBrush");
        var line = (Brush)Application.Current.FindResource("LineBrush");
        var text = (Brush)Application.Current.FindResource("TextBrush");
        var muted = (Brush)Application.Current.FindResource("MutedBrush");

        string title, body;
        bool offerDeep = false;
        if (r == null) { title = "Couldn't free memory"; body = "Windows did not let Utylix reach the running programs."; }
        else
        {
            long freed = Math.Max(0, r.FreeAfter - r.FreeBefore);
            title = freed >= 1 << 20 ? "Freed " + MemoryCleaner.Bytes(freed) : "Memory is already tidy";
            body = $"{MemoryCleaner.Bytes(r.FreeAfter)} free of {MemoryCleaner.Bytes(r.Total)}  ·  {r.Trimmed} programs trimmed";
            if (r.Deep) body += "\nWindows' cached memory was cleared too.";
            else if (!MemoryCleaner.IsElevated) offerDeep = true;
        }

        var panel = new StackPanel { Margin = new Thickness(18, 14, 18, 14) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = text });
        panel.Children.Add(new TextBlock { Text = body, FontSize = 12.5, Foreground = muted, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        if (offerDeep)
        {
            var deepLink = new TextBlock
            {
                Text = "Also clear Windows' cached memory (asks for permission)", FontSize = 12.5, Foreground = accent, Margin = new Thickness(0, 8, 0, 0),
                Cursor = Cursors.Hand, TextDecorations = TextDecorations.Underline,
            };
            panel.Children.Add(deepLink);
            deepLink.MouseLeftButtonUp += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--free-memory-deep") { UseShellExecute = true, Verb = "runas" });
                }
                catch (System.ComponentModel.Win32Exception) { /* permission was refused */ }
                Application.Current.Shutdown();
            };
        }

        var window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, ShowActivated = false,
            SizeToContent = SizeToContent.WidthAndHeight, MinWidth = 300, MaxWidth = 460, Background = Brushes.Transparent, AllowsTransparency = true,
            Content = new Border { Background = bg, BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = panel },
            FontFamily = new FontFamily("Segoe UI"),
        };
        window.Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            window.Left = wa.Right - window.ActualWidth - 16;
            window.Top = wa.Bottom - window.ActualHeight - 16;
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(offerDeep ? 9 : 4.5) };
        timer.Tick += (_, _) => { timer.Stop(); Application.Current.Shutdown(); };
        window.MouseLeftButtonUp += (_, _) => { if (!offerDeep) Application.Current.Shutdown(); };
        window.Show();
        timer.Start();
    }
}
