using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>Who made Utylix and where to find it (shown in About, the installer and Windows' list of apps).</summary>
internal static class AppInfo
{
    public const string Author = "Reynan Gonzales";
    public const string Page = "https://github.com/ReynanGonzales/Utylix";
    public const string Tagline = "A personal all-in-one toolbox for Windows";
}

/// <summary>"About Utylix": the logo, the version, who made it, a way to support it, and the free tools it is built on.</summary>
public sealed class AboutWindow : Window
{
    private static AboutWindow? _open;

    public static void ShowIt(Window? owner)
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new AboutWindow();
        if (owner != null && owner.IsVisible) _open.Owner = owner;
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    private AboutWindow()
    {
        Title = "About Utylix Pro";
        Width = 680; SizeToContent = SizeToContent.Height; MaxHeight = Math.Min(980, SystemParameters.WorkArea.Height - 30); ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13.5;
        WindowTheme.DarkTitleBar(this);
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/logo.png")); } catch (Exception e) when (e is IOException or UriFormatException) { }
        var text = (Brush)Application.Current.FindResource("TextBrush");
        var muted = (Brush)Application.Current.FindResource("MutedBrush");

        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 22) };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/logo.png")), Width = 72, Height = 72 });
        var names = new StackPanel { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = "Utylix Pro", FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = text });
        var version = new TextBlock { Text = "Version " + AppUpdater.CurrentText, Foreground = muted, Margin = new Thickness(0, 2, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(version, "AboutVersion");
        names.Children.Add(version);
        head.Children.Add(names);
        root.Children.Add(head);

        var made = new TextBlock { Text = "Made by " + AppInfo.Author, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = text, Margin = new Thickness(0, 20, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(made, "AboutAuthor");
        root.Children.Add(made);
        root.Children.Add(new TextBlock { Text = AppInfo.Tagline + ": downloads, converting files, PDF and video editors, screen recorder and snip tool, video, music and photo players, archives, background remover, brightness, fan and RAM-light control, and memory / CPU / frame-rate limits.", TextWrapping = TextWrapping.Wrap, Foreground = muted, Margin = new Thickness(0, 6, 0, 0) });

        // support: a GCash QR code (scanned with the GCash app)
        var support = new Border { Margin = new Thickness(0, 18, 0, 0), Padding = new Thickness(14), CornerRadius = new CornerRadius(10), Style = (Style)Application.Current.FindResource("Section") };
        System.Windows.Automation.AutomationProperties.SetAutomationId(support, "AboutSupport");
        var supportRow = new Grid();
        supportRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        supportRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var qr = new Border { CornerRadius = new CornerRadius(8), Background = Brushes.White, Padding = new Thickness(4), VerticalAlignment = VerticalAlignment.Top,
                              Child = new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/gcash.png")), Width = 324, Stretch = Stretch.Uniform } };
        var supportText = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        supportText.Children.Add(new TextBlock { Text = "Support me", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = text });
        supportText.Children.Add(new TextBlock { Text = "If Utylix Pro helps you, you can say thanks with a small tip. Scan the QR with the GCash app.", TextWrapping = TextWrapping.Wrap, Foreground = muted, Margin = new Thickness(0, 6, 0, 0) });
        supportText.Children.Add(new TextBlock { Text = "Salamat po!", Foreground = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
        Grid.SetColumn(qr, 0); Grid.SetColumn(supportText, 1);
        supportRow.Children.Add(qr); supportRow.Children.Add(supportText);
        support.Child = supportRow;
        root.Children.Add(support);

        root.Children.Add(new TextBlock { Text = "Built with free tools", FontWeight = FontWeights.SemiBold, Foreground = text, Margin = new Thickness(0, 22, 0, 6) });
        var credits = new StackPanel();
        foreach (var (name, what) in new[]
        {
            ("PDFium", "reads, draws and edits PDFs (the PDF engine of Chrome); PDFsharp writes them"),
            ("yt-dlp and FFmpeg", "download videos and convert video, music and pictures"),
            ("VLC (LibVLC)", "plays videos and music"),
            ("MonoTorrent", "torrents"),
            ("SharpCompress", "ZIP, RAR, 7z and other archives"),
            ("NAudio", "audio"),
            ("ONNX Runtime and rembg", "removes picture backgrounds"),
            ("LibreHardwareMonitor", "temperatures and fan control"),
            ("Windows", "reads text from scans (OCR) and draws everything"),
        })
        {
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = muted, Margin = new Thickness(0, 0, 0, 3) };
            line.Inlines.Add(new System.Windows.Documents.Run(name) { FontWeight = FontWeights.SemiBold, Foreground = text });
            line.Inlines.Add(new System.Windows.Documents.Run("  " + what));
            credits.Children.Add(line);
        }
        root.Children.Add(credits);

        var close = new Button { Content = "Close", Style = (Style)Application.Current.FindResource("DialogButton"), HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 96, Margin = new Thickness(0, 18, 0, 0), IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();
        root.Children.Add(close);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };       // (a small screen scrolls instead of cutting it off)
    }
}
