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

/// <summary>"About Utylix": the logo, the version, who made it, the link to its page, and the free tools it is built on.</summary>
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
        Title = "About Utylix";
        Width = 520; SizeToContent = SizeToContent.Height; MaxHeight = 760; ResizeMode = ResizeMode.NoResize;
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
        names.Children.Add(new TextBlock { Text = "Utylix", FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = text });
        var version = new TextBlock { Text = "Version " + AppUpdater.CurrentText, Foreground = muted, Margin = new Thickness(0, 2, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(version, "AboutVersion");
        names.Children.Add(version);
        head.Children.Add(names);
        root.Children.Add(head);

        var made = new TextBlock { Text = "Made by " + AppInfo.Author, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = text, Margin = new Thickness(0, 20, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(made, "AboutAuthor");
        root.Children.Add(made);
        root.Children.Add(new TextBlock { Text = AppInfo.Tagline + ": downloads from the web, videos and torrents, converting files, a PDF editor, screen recorder and snip tool, video, music and photo viewers, archives, background remover, brightness and fan control.", TextWrapping = TextWrapping.Wrap, Foreground = muted, Margin = new Thickness(0, 6, 0, 0) });

        var link = new Button { Content = "Open Utylix's page on GitHub", Style = (Style)Application.Current.FindResource("DialogPrimary"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(link, "AboutGithub");
        link.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(AppInfo.Page) { UseShellExecute = true }); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { } };
        root.Children.Add(link);
        root.Children.Add(new TextBlock { Text = AppInfo.Page, Foreground = muted, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });

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
        Content = root;
    }
}
