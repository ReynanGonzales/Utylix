using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>What was chosen in the setup wizard.</summary>
internal sealed record Choices(string Dir, bool AllUsers, bool Desktop, bool AutoStart,
                               bool Convert, bool Archive, bool RemoveBg, bool Play, bool Pdf,
                               bool YtDlp, bool Ffmpeg, bool Vlc, bool Model)
{
    /// <summary>Command line for the administrator copy of the wizard (started when "all users" needs UAC).</summary>
    public List<string> ToArgs()
    {
        var a = new List<string> { "--setup-auto", "--dir", Dir };
        if (AllUsers) a.Add("--all-users");
        if (Desktop) a.Add("--desktop");
        if (AutoStart) a.Add("--autostart");
        if (!Convert) a.Add("--no-convert");
        if (!Archive) a.Add("--no-archive");
        if (!RemoveBg) a.Add("--no-bg");
        if (!Play) a.Add("--no-play");
        if (!Pdf) a.Add("--no-pdf-menu");
        var get = new List<string>();
        if (YtDlp) get.Add("yt"); if (Ffmpeg) get.Add("ff"); if (Vlc) get.Add("vlc"); if (Model) get.Add("ai");
        if (get.Count > 0) { a.Add("--get"); a.Add(string.Join(",", get)); }
        return a;
    }

    public static Choices FromArgs(string[] args)
    {
        int d = Array.IndexOf(args, "--dir"), g = Array.IndexOf(args, "--get");
        var get = g >= 0 && g + 1 < args.Length ? args[g + 1].Split(',') : Array.Empty<string>();
        return new Choices(d >= 0 && d + 1 < args.Length ? args[d + 1] : Installer.AllUsersDir, args.Contains("--all-users"), args.Contains("--desktop"), args.Contains("--autostart"),
                           !args.Contains("--no-convert"), !args.Contains("--no-archive"), !args.Contains("--no-bg"), !args.Contains("--no-play"), !args.Contains("--no-pdf-menu"),
                           get.Contains("yt"), get.Contains("ff"), get.Contains("vlc"), get.Contains("ai"));
    }
}

internal static partial class Installer
{
    private sealed class ScaleProgress : IProgress<(double, string)>
    {
        private readonly IProgress<(double, string)> _inner; private readonly double _share;
        public ScaleProgress(IProgress<(double, string)> inner, double share) { _inner = inner; _share = share; }
        public void Report((double, string) v) => _inner.Report((v.Item1 * _share / 100.0, v.Item2));
    }

    private static bool ConfigFlag(string key, bool fallback)
    {
        try
        {
            string path = Path.Combine(App.DataDir, "config.json");
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o && o[key] is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        return fallback;
    }

    /// <summary>The right-click choices are kept in Utylix's settings file; the installed program applies them when it starts.</summary>
    private static void WriteConfig(Choices c)
    {
        string path = Path.Combine(App.DataDir, "config.json");
        Directory.CreateDirectory(App.DataDir);
        JsonObject o;
        try { o = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject() : new JsonObject(); }
        catch (System.Text.Json.JsonException) { o = new JsonObject(); }
        o["explorer_convert_menu"] = c.Convert;
        o["explorer_archive_menu"] = c.Archive;
        o["explorer_bg_menu"] = c.RemoveBg;
        o["explorer_play_menu"] = c.Play;
        o["explorer_pdf_menu"] = c.Pdf;
        File.WriteAllText(path, o.ToJsonString());
    }

    /// <summary>Installs the program, writes the choices and downloads the chosen parts. Failed downloads do not stop the installation.</summary>
    private static async Task<(string Exe, List<string> Failed)> InstallAll(Choices c, IProgress<(double, string)> progress)
    {
        var downloads = new List<(string Name, Func<Action<string>, CancellationToken, Task> Run)>();
        if (c.YtDlp) downloads.Add(("yt-dlp (video sites)", Tools.InstallYtDlpAsync));
        if (c.Ffmpeg) downloads.Add(("ffmpeg (convert, record, merge)", Tools.InstallFfmpegAsync));
        if (c.Vlc) downloads.Add(("the video player engine", VlcEngine.InstallAsync));
        if (c.Model) downloads.Add(("the background remover's AI model", BackgroundRemover.InstallModelAsync));

        double filesShare = downloads.Count > 0 ? 35 : 100;
        string exe = await Task.Run(() =>
        {
            string installed = Install(c, new ScaleProgress(progress, filesShare));
            WriteConfig(c);
            return installed;
        });

        var failed = new List<string>();
        double slice = downloads.Count > 0 ? (97 - filesShare) / downloads.Count : 0;
        for (int i = 0; i < downloads.Count; i++)
        {
            double start = filesShare + slice * i;
            var (name, run) = downloads[i];
            progress.Report((start, $"Downloading {name}…"));
            try
            {
                await run(s =>
                {
                    var m = Regex.Match(s, @"(\d+(?:\.\d+)?) MB / (\d+(?:\.\d+)?) MB");
                    double frac = m.Success && double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var total) && total > 0
                        ? Math.Min(1, double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) / total) : 0;
                    progress.Report((start + slice * 0.9 * frac, s));
                }, CancellationToken.None);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                failed.Add($"{name}: {e.Message}");
            }
        }
        progress.Report((100, "Finishing…"));
        return (exe, failed);
    }

    // ------------------------------------------------------------------------------------------------ the wizard

    private static TextBlock Text(string text, double size = 13.5, bool bold = false, bool muted = false, Thickness? margin = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0) };
        if (bold) t.FontWeight = FontWeights.SemiBold;
        if (muted) t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return t;
    }

    private static CheckBox Check(string text, string id, bool on, string? note = null, bool enabled = true)
    {
        var box = new CheckBox { IsChecked = on, IsEnabled = enabled, Margin = new Thickness(0, 10, 0, 0) };
        var stack = new StackPanel();
        stack.Children.Add(Text(text));
        if (note != null) stack.Children.Add(Text(note, 12, muted: true, margin: new Thickness(0, 1, 0, 0)));
        box.Content = stack;
        System.Windows.Automation.AutomationProperties.SetAutomationId(box, id);
        System.Windows.Automation.AutomationProperties.SetName(box, text);
        return box;
    }

    /// <summary>Shows the setup wizard. Returns true when the app should carry on running from where it is (the "just run it" choice).</summary>
    public static bool RunSetup(string[] args)
    {
        if (args.Contains("--setup-update")) { RunUpdate(args); return false; }          // an update: only the files, no wizard
        bool auto = args.Contains("--setup-auto");           // the administrator copy: installs at once with what was chosen
        var start = auto ? Choices.FromArgs(args) : null;
        bool runHere = false;
        var R = (string key) => Application.Current.FindResource(key);
        var A = System.Windows.Automation.AutomationProperties.SetAutomationId;

        // ---- page 1: welcome ----
        var welcome = new StackPanel();
        welcome.Children.Add(Text("Utylix puts the programs you normally install one by one after reformatting Windows into one small app:", margin: new Thickness(0, 0, 0, 6)));
        foreach (var (name, what) in new[]
        {
            ("Downloads", "a fast download manager that takes downloads from your browser, plus video sites"),
            ("Multi Convert", "pictures, videos and music from one format to another"),
            ("Screen Capture and Screen Recorder", "screenshots, and screen videos with sound, pause and GIF"),
            ("Video Player", "plays practically every video and music file, with subtitles"),
            ("Utylix Archive", "opens, makes and extracts ZIP (with password), RAR, 7z and more"),
            ("Background Remover", "right-click a picture to cut out its background"),
            ("Brightness", "one slider for every monitor, from the tray"),
        })
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            line.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = (Brush)R("AccentBrush"), Margin = new Thickness(2, 6, 10, 0), VerticalAlignment = VerticalAlignment.Top });
            var t = Text(name, bold: true); t.Inlines.Add(new System.Windows.Documents.Run("  -  " + what) { FontWeight = FontWeights.Normal, Foreground = (Brush)R("MutedBrush") });
            t.MaxWidth = 500;
            line.Children.Add(t);
            welcome.Children.Add(line);
        }
        welcome.Children.Add(Text("Setup takes about a minute. On the next pages you choose where it goes, which right-click menus to turn on, and which extra parts to download.", muted: true, margin: new Thickness(0, 18, 0, 0)));

        // ---- page 2: directory ----
        bool defaultAll = start?.AllUsers ?? false;
        var scopeMe = new RadioButton { Content = "Just for me  (no administrator permission needed)", IsChecked = !defaultAll, GroupName = "scope", Margin = new Thickness(0, 14, 0, 0) };
        var scopeAll = new RadioButton { Content = "For all users of this PC  (asks Windows for administrator permission)", IsChecked = defaultAll, GroupName = "scope", Margin = new Thickness(0, 8, 0, 0) };
        var dirBox = new TextBox { Text = start?.Dir ?? Installed()?.Dir ?? UserDir, Style = (Style)R("Field"), Margin = new Thickness(0, 6, 8, 0) };
        var browse = new Button { Content = "Browse…", Style = (Style)R("DialogButton"), Margin = new Thickness(0, 6, 0, 0) };
        A(dirBox, "SetupDir"); A(scopeAll, "SetupAllUsers"); A(scopeMe, "SetupJustMe");
        var pathRow = new Grid();
        pathRow.ColumnDefinitions.Add(new ColumnDefinition());
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathRow.Children.Add(dirBox); Grid.SetColumn(browse, 1); pathRow.Children.Add(browse);
        var where = new StackPanel();
        where.Children.Add(Text("Choose who can use Utylix, and the folder it is installed into.", muted: true));
        where.Children.Add(scopeMe); where.Children.Add(scopeAll);
        where.Children.Add(Text("Install into", margin: new Thickness(0, 22, 0, 0)));
        where.Children.Add(pathRow);
        where.Children.Add(Text("Utylix brings everything it needs in this folder (about 215 MB), so nothing else has to be installed. Your settings and history are kept separately in your user profile, so uninstalling and installing again does not lose them unless you ask.", 12, muted: true, margin: new Thickness(0, 14, 0, 0)));

        // ---- page 3: right-click features and startup ----
        var cConvert = Check("Convert", "SetupConvert", start?.Convert ?? ConfigFlag("explorer_convert_menu", true), "Right-click a picture, video or song -> Convert to another format");
        var cArchive = Check("Utylix Archive", "SetupArchive", start?.Archive ?? ConfigFlag("explorer_archive_menu", true), "Right-click files or folders -> Add to ZIP / Add to archive, and open or extract ZIP, RAR, 7z ...");
        var cBg = Check("Remove background", "SetupBg", start?.RemoveBg ?? ConfigFlag("explorer_bg_menu", true), "Right-click a picture -> Remove background");
        var cPlay = Check("Play with Utylix", "SetupPlay", start?.Play ?? ConfigFlag("explorer_play_menu", true), "Right-click a video or song -> Play with Utylix, and Utylix in \"Open with\"");
        var cPdf = Check("PDF tools", "SetupPdf", start?.Pdf ?? ConfigFlag("explorer_pdf_menu", true), "Right-click PDFs -> Reduce file size / Combine into one PDF; pictures -> Convert to PDF");
        var cAuto = Check("Run Utylix when Windows starts", "SetupAutostart", start?.AutoStart ?? true, "It waits quietly in the tray so it can take your browser's downloads and answer the hotkeys");
        var cDesk = Check("Put a Utylix shortcut on the desktop", "SetupDesktop", start?.Desktop ?? false);
        var all3 = new Button { Content = "Turn all on", Style = (Style)R("LinkButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        all3.Click += (_, _) => { foreach (var b in new[] { cConvert, cArchive, cBg, cPlay, cPdf, cAuto, cDesk }) b.IsChecked = true; };
        var features = new StackPanel();
        features.Children.Add(Text("Right-click menus in Explorer", bold: true));
        features.Children.Add(cConvert); features.Children.Add(cArchive); features.Children.Add(cBg); features.Children.Add(cPlay); features.Children.Add(cPdf);
        features.Children.Add(Text("Startup and shortcuts", bold: true, margin: new Thickness(0, 22, 0, 0)));
        features.Children.Add(cAuto); features.Children.Add(cDesk);
        features.Children.Add(all3);
        features.Children.Add(Text("Uninstalling removes every one of these again. You can change the right-click menus later in Settings.", 12, muted: true, margin: new Thickness(0, 10, 0, 0)));

        // ---- page 4: extra downloads ----
        string have = "  -  already on this PC (tick to download it again)";
        var dYt = Check("Video sites: yt-dlp" + (Tools.HasYtDlp ? have : ""), "SetupYtDlp", !Tools.HasYtDlp, "Downloads videos from YouTube and hundreds of other sites. About 20 MB.");
        var dFf = Check("Convert, record and merge: ffmpeg" + (Tools.HasFfmpeg ? have : ""), "SetupFfmpeg", !Tools.HasFfmpeg, "Needed for video and music conversion, the screen recorder and best-quality video downloads. About 100 MB.");
        var dVlc = Check("Video player engine" + (VlcEngine.Find() != null ? have : ""), "SetupVlc", VlcEngine.Find() == null, "The engine that lets the Utylix player play almost any file (the same one VLC is made of). About 80 MB.");
        var dAi = Check("Background remover: AI model" + (BackgroundRemover.HasModel ? have : ""), "SetupModel", false, "Runs on your PC; nothing is uploaded. About 170 MB, so it is off unless you want it now.");
        if (start != null) { dYt.IsChecked = start.YtDlp; dFf.IsChecked = start.Ffmpeg; dVlc.IsChecked = start.Vlc; dAi.IsChecked = start.Model; }
        var total = Text("", 12.5, bold: true, margin: new Thickness(0, 18, 0, 0));
        void UpdateTotal()
        {
            int mb = (dYt.IsChecked == true ? 20 : 0) + (dFf.IsChecked == true ? 100 : 0) + (dVlc.IsChecked == true ? 80 : 0) + (dAi.IsChecked == true ? 170 : 0);
            total.Text = mb == 0 ? "Nothing will be downloaded now. Utylix asks the first time a feature needs one of these." : $"About {mb} MB will be downloaded while installing.";
        }
        foreach (var b in new[] { dYt, dFf, dVlc, dAi }) { b.Checked += (_, _) => UpdateTotal(); b.Unchecked += (_, _) => UpdateTotal(); }
        var selectAll = new Button { Content = "Select all", Style = (Style)R("LinkButton"), Margin = new Thickness(0, 0, 14, 0) };
        var selectNone = new Button { Content = "Select none", Style = (Style)R("LinkButton") };
        selectAll.Click += (_, _) => { foreach (var b in new[] { dYt, dFf, dVlc, dAi }) if (b.IsEnabled) b.IsChecked = true; };
        selectNone.Click += (_, _) => { foreach (var b in new[] { dYt, dFf, dVlc, dAi }) if (b.IsEnabled) b.IsChecked = false; };
        var downloadsPage = new StackPanel();
        downloadsPage.Children.Add(Text("Utylix does not carry these inside it (they are other people's programs, and they go out of date). Choose which to download now; each comes from its official source and is checked against its published fingerprint. Anything you skip is offered later, the first time it is needed.", muted: true));
        downloadsPage.Children.Add(dYt); downloadsPage.Children.Add(dFf); downloadsPage.Children.Add(dVlc); downloadsPage.Children.Add(dAi);
        var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        links.Children.Add(selectAll); links.Children.Add(selectNone);
        downloadsPage.Children.Add(links);
        downloadsPage.Children.Add(total);
        UpdateTotal();

        // ---- page 5: installing / finished ----
        var bar = new ProgressBar { Height = 12, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 26, 0, 0), Foreground = (Brush)R("AccentBrush") };
        var status = Text("Getting ready…", margin: new Thickness(0, 12, 0, 0)); status.MinHeight = 22;
        var startNow = Check("Start Utylix now", "SetupStartNow", true);
        startNow.Visibility = Visibility.Collapsed;
        var showExtension = Check("Show me how to add the browser extension", "SetupExtension", true, "Downloads from the browser go to Utylix through it. It is built into Utylix: this shows the three clicks.");
        showExtension.Visibility = Visibility.Collapsed;
        A(bar, "SetupProgress"); A(status, "SetupStatus");
        var installing = new StackPanel();
        installing.Children.Add(Text("Please wait while Utylix is set up. You can keep using your PC.", muted: true));
        installing.Children.Add(bar); installing.Children.Add(status); installing.Children.Add(startNow); installing.Children.Add(showExtension);

        var pages = new[] { welcome, where, features, downloadsPage, installing };
        var titles = new[] { "Welcome to Utylix", "Where to install", "Right-click menus and startup", "Extra parts to download", "Installing" };

        // ---- frame: header with steps, page area, buttons ----
        var title = Text("", 22, bold: true); title.VerticalAlignment = VerticalAlignment.Center;
        var stepText = Text("", 12, muted: true); stepText.VerticalAlignment = VerticalAlignment.Center;
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var dotShapes = pages.Select(_ => new Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 0, 7, 0) }).ToList();
        foreach (var d in dotShapes) dots.Children.Add(d);
        var titleBlock = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titleBlock.Children.Add(title); titleBlock.Children.Add(dots);
        titleBlock.Children.Add(Text("Made by " + AppInfo.Author, 11.5, muted: true, margin: new Thickness(0, 5, 0, 0)));
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/logo.png")), Width = 52, Height = 52 });
        head.Children.Add(titleBlock);
        A(title, "SetupTitle");

        var next = new Button { Style = (Style)R("DialogPrimary"), IsDefault = true, MinWidth = 110 };
        var back = new Button { Content = "Back", Style = (Style)R("DialogButton"), Margin = new Thickness(0, 0, 10, 0) };
        var cancel = new Button { Content = "Cancel", Style = (Style)R("DialogButton"), IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        var portable = new Button { Content = "Just run it, don't install", Style = (Style)R("LinkButton"), VerticalAlignment = VerticalAlignment.Center };
        A(next, "SetupNext"); A(back, "SetupBack"); A(cancel, "SetupCancel"); A(portable, "SetupPortable");
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(back); right.Children.Add(cancel); right.Children.Add(next);
        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.Children.Add(portable); footer.Children.Add(right);

        var area = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        foreach (var p in pages) { p.Visibility = Visibility.Collapsed; area.Children.Add(p); }

        var root = new DockPanel { Margin = new Thickness(28, 22, 28, 20) };
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(area);

        var window = new Window
        {
            Title = "Utylix Setup", Width = 640, Height = 600, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = root, Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/logo.png")),
            Background = (Brush)R("BgBrush"), Foreground = (Brush)R("TextBrush"), FontFamily = new FontFamily("Segoe UI"), FontSize = 13.5,
        };

        int page = 0;
        bool installing_ = false;
        string? installedExe = null;
        void Show(int i)
        {
            page = i;
            for (int k = 0; k < pages.Length; k++) pages[k].Visibility = k == i ? Visibility.Visible : Visibility.Collapsed;
            title.Text = titles[i];
            for (int k = 0; k < dotShapes.Count; k++) dotShapes[k].Fill = (Brush)R(k <= i ? "AccentBrush" : "LineBrush");
            back.Visibility = i is >= 1 and <= 3 ? Visibility.Visible : Visibility.Collapsed;
            portable.Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
            next.Content = i == 3 ? "Install" : i == 4 ? (installedExe != null ? "Finish" : "Installing…") : "Next";
        }
        Show(auto ? 4 : 0);

        scopeAll.Checked += (_, _) => { if (SameFolder(dirBox.Text, UserDir)) dirBox.Text = AllUsersDir; };
        scopeMe.Checked += (_, _) => { if (SameFolder(dirBox.Text, AllUsersDir)) dirBox.Text = UserDir; };
        browse.Click += (_, _) =>
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose the folder to install Utylix into", UseDescriptionForTitle = true };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) dirBox.Text = Path.Combine(dlg.SelectedPath, "Utylix");
        };
        portable.Click += (_, _) => { runHere = true; window.Close(); };
        back.Click += (_, _) => { if (page > 0 && !installing_) Show(page - 1); };
        window.Closing += (_, e) => { if (installing_) e.Cancel = true; };      // not while files are being written

        Choices Current() => new(dirBox.Text.Trim(), scopeAll.IsChecked == true, cDesk.IsChecked == true, cAuto.IsChecked == true,
                                 cConvert.IsChecked == true, cArchive.IsChecked == true, cBg.IsChecked == true, cPlay.IsChecked == true, cPdf.IsChecked == true,
                                 dYt.IsChecked == true, dFf.IsChecked == true, dVlc.IsChecked == true, dAi.IsChecked == true);

        async Task DoInstall(Choices c)
        {
            status.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            if (c.AllUsers && !IsAdmin)
            {
                // administrator permission: a second copy of this wizard does the installing with the same choices
                try
                {
                    var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                    foreach (var a in c.ToArgs()) psi.ArgumentList.Add(a);
                    Process.Start(psi);
                    window.Close();
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    UMessage.Show("Administrator permission was not given, so nothing was installed.\n\nChoose \"Just for me\" on the second page, or try again.", "Utylix Setup", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return;
            }
            Show(4);
            installing_ = true; back.Visibility = cancel.Visibility = portable.Visibility = Visibility.Collapsed; next.IsEnabled = false;
            try
            {
                var progress = new Progress<(double Percent, string Text)>(p => { bar.Value = p.Percent; status.Text = p.Text; });
                var (exe, failed) = await InstallAll(c, progress);
                installedExe = exe;
                bar.Value = 100;
                title.Text = "Utylix is installed";
                status.Text = "All done." + (c.AutoStart ? " Utylix will start with Windows." : "") + " Explorer's right-click menus appear as soon as Utylix starts."
                              + (failed.Count > 0 ? "\n\nThese could not be downloaded (you can get them later inside Utylix):\n - " + string.Join("\n - ", failed) : "");
                startNow.Visibility = Visibility.Visible;
                showExtension.Visibility = Visibility.Visible;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                title.Text = "Setup could not finish";
                status.SetResourceReference(TextBlock.ForegroundProperty, "ErrBrush");
                status.Text = "Couldn't install: " + e.Message;
                cancel.Content = "Close"; cancel.Visibility = Visibility.Visible;
                installing_ = false;
                return;
            }
            installing_ = false;
            next.IsEnabled = true;
            next.Content = "Finish";
            Show(4); title.Text = "Utylix is installed"; next.Content = "Finish";
        }

        next.Click += async (_, _) =>
        {
            if (page == 4 && installedExe != null)
            {
                if (showExtension.IsChecked == true) Launch(installedExe, "--extension-help");       // starts Utylix too (or tells the running one)
                else if (startNow.IsChecked == true) Launch(installedExe);
                window.Close(); return;
            }
            if (page == 1 && string.IsNullOrWhiteSpace(dirBox.Text)) { UMessage.Show("Choose a folder to install into.", "Utylix Setup"); return; }
            if (page < 3) { Show(page + 1); return; }
            if (page == 3) await DoInstall(Current());
        };
        if (auto) window.Loaded += async (_, _) => { cancel.Visibility = Visibility.Collapsed; await DoInstall(start!); };
        window.ShowDialog();
        return runHere;
    }
}
