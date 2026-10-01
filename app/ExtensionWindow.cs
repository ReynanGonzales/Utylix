using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace IdmClone;

/// <summary>
/// The browser extension is inside Utylix.exe. Utylix unpacks it into its data folder (kept up to date), and the window below shows
/// how to add it to Chrome, Edge or Brave: the browser has to be told once to load that folder.
/// </summary>
internal static class ExtensionFiles
{
    private const string Prefix = "ext/";
    public static string Dir => Path.Combine(App.DataDir, "extension");

    /// <summary>The version of the extension inside this program (from its manifest), so the extension can tell when it is out of date.</summary>
    public static string EmbeddedVersion { get; } = ReadEmbeddedVersion();

    private static string ReadEmbeddedVersion()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(Prefix + "manifest.json");
            if (s == null) return "";
            using var doc = System.Text.Json.JsonDocument.Parse(s);
            return doc.RootElement.GetProperty("version").GetString() ?? "";
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or KeyNotFoundException or IOException) { return ""; }
    }

    /// <summary>Unpacks (or refreshes) the extension folder. Returns its path.</summary>
    public static string Ensure()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            string relative = name[Prefix.Length..].Replace('\\', '/');
            if (relative.Split('/').Any(part => part is ".." or "")) continue;
            string target = Path.GetFullPath(Path.Combine(Dir, relative));
            if (!target.StartsWith(Path.GetFullPath(Dir), StringComparison.OrdinalIgnoreCase)) continue;      // never outside the folder
            using var src = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            src.CopyTo(ms);
            var bytes = ms.ToArray();
            try
            {
                if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* try again next start */ }
        }
        return Dir;
    }

    /// <summary>Installed browsers that can load the extension: name and the program to start.</summary>
    public static List<(string Name, string Exe, string Page)> Browsers()
    {
        var list = new List<(string, string, string)>();
        foreach (var (name, file, page) in new[] { ("Chrome", "chrome.exe", "chrome://extensions"), ("Edge", "msedge.exe", "edge://extensions"), ("Brave", "brave.exe", "brave://extensions") })
        {
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try
                {
                    using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + file);
                    if (key?.GetValue("") is string exe && File.Exists(exe)) { list.Add((name, exe, page)); break; }
                }
                catch (Exception e) when (e is System.Security.SecurityException or IOException) { }
            }
        }
        return list;
    }

    public static void ShowHelp(Window? owner = null)
    {
        string folder = Ensure();
        var R = (string key) => Application.Current.FindResource(key);
        var panel = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
        panel.Children.Add(new TextBlock { Text = "Add the Utylix extension to your browser", FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = "The extension sends your browser's downloads to Utylix, adds the video button and the right-click \"Picture in Picture\". It comes with Utylix: it only has to be told once to load this folder.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = (Brush)R("MutedBrush"),
        });

        panel.Children.Add(new TextBlock { Text = "1.  Open your browser's extensions page", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) });
        var browsers = new WrapPanel();
        foreach (var (name, exe, page) in Browsers())
        {
            var b = new Button { Content = "Open " + name + " extensions", Style = (Style)R("DialogButton"), Margin = new Thickness(0, 0, 8, 6) };
            AutomationProperties("OpenBrowser" + name, b);
            b.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(exe) { ArgumentList = { page }, UseShellExecute = false }); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { } };
            browsers.Children.Add(b);
        }
        if (browsers.Children.Count == 0) browsers.Children.Add(new TextBlock { Text = "Chrome, Edge or Brave was not found: open the browser and type chrome://extensions (or edge:// / brave://) in the address bar.", TextWrapping = TextWrapping.Wrap, Foreground = (Brush)R("MutedBrush") });
        panel.Children.Add(browsers);

        panel.Children.Add(new TextBlock { Text = "2.  Turn on \"Developer mode\" (a switch at the top right of that page)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(new TextBlock { Text = "3.  Click \"Load unpacked\" and choose this folder:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) });
        var path = new TextBox { Text = folder, IsReadOnly = true, Style = (Style)R("Field") };
        AutomationProperties("ExtensionPath", path);
        panel.Children.Add(path);
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var open = new Button { Content = "Open the folder", Style = (Style)R("DialogButton"), Margin = new Thickness(0, 0, 8, 0) };
        open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false }); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { } };
        var copy = new Button { Content = "Copy the folder path", Style = (Style)R("DialogButton") };
        copy.Click += (_, _) => { try { Clipboard.SetText(folder); } catch (System.Runtime.InteropServices.COMException) { } };
        row.Children.Add(open); row.Children.Add(copy);
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock { Text = "4.  The Utylix icon appears next to the address bar. Click the puzzle piece and pin it if you like.", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 0), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = "After Utylix is updated, the extension reloads itself the next time the browser talks to Utylix. If it ever does not, press the round reload arrow on the Utylix extension (same page) once.",
            FontSize = 12, Foreground = (Brush)R("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0),
        });
        var close = new Button { Content = "Close", Style = (Style)R("DialogPrimary"), IsDefault = true, IsCancel = true, MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        panel.Children.Add(close);

        var window = new Window
        {
            Title = "Utylix browser extension", Width = 640, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner, Owner = owner,
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/logo.png")),
            Background = (Brush)R("BgBrush"), Foreground = (Brush)R("TextBrush"), FontFamily = new FontFamily("Segoe UI"), FontSize = 13.5, MaxHeight = SystemParameters.WorkArea.Height * 0.95,
        };
        WindowTheme.DarkTitleBar(window);
        close.Click += (_, _) => window.Close();
        window.Show();
        window.Activate();
    }

    private static void AutomationProperties(string id, DependencyObject element) => System.Windows.Automation.AutomationProperties.SetAutomationId(element, id);
}
