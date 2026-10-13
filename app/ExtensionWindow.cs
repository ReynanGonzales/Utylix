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

    /// <summary>The extension as real files next to the program (the installed copy, a program folder, a test build); null for a single-file build.</summary>
    private static string? ProgramCopy
    {
        get
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "extension");
            return File.Exists(Path.Combine(dir, "manifest.json")) ? dir : null;
        }
    }

    /// <summary>Where the extension used to be unpacked: the settings folder. Still kept current when it exists, so a browser that was pointed at it is not left behind.</summary>
    private static string LegacyDir => Path.Combine(App.DataDir, "extension");

    /// <summary>The folder the browser should load: the one in the program's own folder, else the one in the settings folder.</summary>
    public static string Dir => ProgramCopy ?? LegacyDir;

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

    /// <summary>Makes sure the extension folder is there and current. Returns its path.</summary>
    public static string Ensure()
    {
        if (ProgramCopy is { } own)
        {
            // the files came with the program (and are replaced by its updates); only an older, already-used copy in the settings folder is refreshed
            if (Directory.Exists(LegacyDir)) Unpack(LegacyDir);
            return own;
        }
        Unpack(LegacyDir);
        return LegacyDir;
    }

    /// <summary>Writes the embedded extension files into a folder (only the ones that differ).</summary>
    private static void Unpack(string Dir)
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

    /// <summary>The installed Firefox program, or null.</summary>
    private static string? FirefoxExe()
    {
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe");
                if (key?.GetValue("") is string exe && File.Exists(exe)) return exe;
            }
            catch (Exception e) when (e is System.Security.SecurityException or IOException) { }
        }
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) })
        {
            string exe = Path.Combine(dir, "Mozilla Firefox", "firefox.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    /// <summary>The extension's files as one zip with manifest.json at its root: what Mozilla's signing page wants.</summary>
    private static void SaveFirefoxZip(string folder)
    {
        var dlg = new SaveFileDialog { Title = "Save the extension for Mozilla signing", FileName = "Utylix-extension.zip", Filter = "Zip file (*.zip)|*.zip", OverwritePrompt = true };
        if (dlg.ShowDialog() != true) return;
        try
        {
            if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
            System.IO.Compression.ZipFile.CreateFromDirectory(folder, dlg.FileName, System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: false);
            UMessage.Show("Saved. Upload this zip at addons.mozilla.org/developers (Submit a New Add-on > On your own).", "Utylix browser extension");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            UMessage.Show("The zip could not be saved: " + e.Message, "Utylix browser extension");
        }
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
        if (!string.Equals(Path.GetFullPath(folder), Path.GetFullPath(LegacyDir), StringComparison.OrdinalIgnoreCase) && Directory.Exists(LegacyDir))
            panel.Children.Add(new TextBlock
            {
                Text = "The extension now lives in Utylix's own folder (above). If you added it before from the old place (" + LegacyDir + "), it keeps working, but you can remove it in the browser and add this folder instead.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), FontSize = 12, Foreground = (Brush)R("MutedBrush"),
            });
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var open = new Button { Content = "Open the folder", Style = (Style)R("DialogButton"), Margin = new Thickness(0, 0, 8, 0) };
        open.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false }); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { } };
        var copy = new Button { Content = "Copy the folder path", Style = (Style)R("DialogButton") };
        copy.Click += (_, _) => { try { Clipboard.SetText(folder); } catch (System.Runtime.InteropServices.COMException) { } };
        row.Children.Add(open); row.Children.Add(copy);
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock { Text = "4.  The Utylix icon appears next to the address bar. Click the puzzle piece and pin it if you like.", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 0), TextWrapping = TextWrapping.Wrap });
        // ---- Firefox: a different way of adding (and Mozilla has to sign an add-on before Firefox keeps it) ----
        panel.Children.Add(new TextBlock { Text = "Firefox", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 24, 0, 0) });
        panel.Children.Add(new TextBlock
        {
            Text = "1.  Open Firefox's add-on debugging page, click \"Load Temporary Add-on…\" and choose the file manifest.json inside the folder above. " +
                   "That works at once, but Firefox forgets it when it is closed.\n" +
                   "2.  To keep it for good, Mozilla has to sign it (free, takes a few minutes): save the zip below, sign in at addons.mozilla.org/developers, " +
                   "choose \"Submit a New Add-on\" > \"On your own\", upload the zip, then drag the .xpi you get back onto Firefox.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8),
        });
        var ffRow = new WrapPanel();
        var ffExe = FirefoxExe();
        var ffOpen = new Button { Content = "Open Firefox's debugging page", Style = (Style)R("DialogButton"), Margin = new Thickness(0, 0, 8, 6), IsEnabled = ffExe != null };
        AutomationProperties("OpenFirefox", ffOpen);
        ffOpen.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(ffExe!) { ArgumentList = { "about:debugging#/runtime/this-firefox" }, UseShellExecute = false }); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { } };
        var ffZip = new Button { Content = "Save a zip for Mozilla signing…", Style = (Style)R("DialogButton"), Margin = new Thickness(0, 0, 8, 6) };
        AutomationProperties("SaveFirefoxZip", ffZip);
        ffZip.Click += (_, _) => SaveFirefoxZip(folder);
        ffRow.Children.Add(ffOpen); ffRow.Children.Add(ffZip);
        panel.Children.Add(ffRow);
        if (ffExe == null)
            panel.Children.Add(new TextBlock { Text = "Firefox was not found on this PC (the first button needs it).", FontSize = 12, Foreground = (Brush)R("MutedBrush"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = "In Firefox the extension takes the downloads, the video button and the right-click entries; Picture in Picture is Firefox's own (the button on the video, or Ctrl+Shift+]). " +
                   "If a site does not work, check in Firefox's Add-ons page > Utylix > Permissions that it may access your data for all websites.",
            FontSize = 12, Foreground = (Brush)R("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        });

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
