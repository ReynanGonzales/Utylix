using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using IdmClone.Engine;

namespace IdmClone;

public partial class SettingsWindow : Window
{
    private readonly Manager _manager;
    private readonly Dictionary<string, TextBox> _catBoxes = new();
    private readonly HashSet<string> _catEdited = new();   // only folders the user set are stored; the rest follow the download folder
    private bool _loading = true;

    /// <param name="tool">The tab Settings was opened from ("downloads", "converter", "capture"): only that tool's settings show at first.</param>
    public SettingsWindow(Manager manager, string? tool = null)
    {
        InitializeComponent();
        _manager = manager;
        MaxHeight = SystemParameters.WorkArea.Height * 0.94;

        var c = manager.Config;
        BuildCategoryRows(c);
        SortBox.IsChecked = c.SortByType;
        Sort_Changed(null, null!);
        _loading = false;
        Loaded += (_, _) => RefreshTools();
        DirBox.Text = c.DownloadDir;
        ConnBox.Text = c.Connections.ToString();
        MaxBox.Text = c.MaxActive.ToString();
        AutoStartBox.IsChecked = App.AutoStartEnabled;
        ConfirmBox.IsChecked = c.ConfirmCaptured;
        ShowOnDownloadBox.IsChecked = c.ShowOnDownload;
        VideoButtonBox.IsChecked = c.VideoButton;
        ClipboardBox.IsChecked = c.WatchClipboard;
        BrowserStartBox.IsChecked = c.AllowBrowserStart;
        AutoUpdateBox.IsChecked = c.AutoUpdateYtDlp;
        AutoUpdateAppBox.IsChecked = c.AutoUpdateApp;
        TorrentHandlerBox.IsChecked = c.TorrentHandler;
        TorrentSeedBox.IsChecked = c.TorrentSeed;
        TorrentDownBox.Text = c.TorrentDownKb.ToString();
        TorrentUpBox.Text = c.TorrentUpKb.ToString();
        WinFBox.IsChecked = c.OpenWinF;
        DisableWinFBox.IsChecked = ExplorerHotkeys.IsDisabled('F');
        DisableWinSBox.IsChecked = ExplorerHotkeys.IsDisabled('S');
        VersionLabel.Text = "Utylix " + AppUpdater.CurrentText;
        ExplorerMenuBox.IsChecked = c.ExplorerMenu;
        ArchiveMenuBox.IsChecked = c.ExplorerArchiveMenu;
        BgMenuBox.IsChecked = c.ExplorerBgMenu;
        PlayMenuBox.IsChecked = c.ExplorerPlayMenu;
        PdfMenuBox.IsChecked = c.ExplorerPdfMenu;
        RefreshPlayerEngine();
        RefreshBgModel();
        ShotWinSBox.IsChecked = c.ShotWinS;
        ShotCtrlAltBox.IsChecked = c.ShotCtrlAltS;
        ShotCopyBox.IsChecked = c.ShotCopy;
        ShotAutoSaveBox.IsChecked = c.ShotAutoSave;
        ShotDirBox.Text = c.ShotDir;
        RecHotkeyBox.IsChecked = c.RecHotkey;
        RecDirBox.Text = c.RecDir;
        (c.SubMode == "off" ? SubOffRadio : c.SubMode == "embed" ? SubEmbedRadio : SubFileRadio).IsChecked = true;
        SubLangsBox.Text = c.SubLangs;
        SubAutoBox.IsChecked = c.SubAuto;
        TypesBox.Text = c.CaptureTypes;
        MinBox.Text = c.CaptureMinKb.ToString();
        ExcludeBox.Text = c.CaptureExclude;
        (c.CaptureTypesOnly ? TypesRadio : AllRadio).IsChecked = true;
        Mode_Changed(null, null!);
        ShowSettingsFor(tool);
    }

    /// <summary>Show just the settings of one tool (null = everything). Backup is always there.</summary>
    private void ShowSettingsFor(string? tool)
    {
        bool all = tool is not ("downloads" or "converter" or "capture" or "recorder" or "player");
        GroupDownloads.Visibility = all || tool == "downloads" ? Visibility.Visible : Visibility.Collapsed;
        GroupConverter.Visibility = all || tool == "converter" ? Visibility.Visible : Visibility.Collapsed;
        GroupCapture.Visibility = all || tool == "capture" ? Visibility.Visible : Visibility.Collapsed;
        GroupRecorder.Visibility = all || tool == "recorder" ? Visibility.Visible : Visibility.Collapsed;
        GroupPlayer.Visibility = all || tool == "player" ? Visibility.Visible : Visibility.Collapsed;
        GroupArchive.Visibility = all || tool == "converter" ? Visibility.Visible : Visibility.Collapsed;   // (both are Explorer right-click options)
        ShowAllBtn.Visibility = all ? Visibility.Collapsed : Visibility.Visible;
        Title = tool switch
        {
            "downloads" => "Settings - Downloads",
            "converter" => "Settings - Multi Convert",
            "capture" => "Settings - Screen Capture",
            "recorder" => "Settings - Screen Recorder",
            "player" => "Settings - Video Player",
            _ => "Settings",
        };
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e) => ShowSettingsFor(null);

    private void ExtensionHelp_Click(object sender, RoutedEventArgs e) => ExtensionFiles.ShowHelp(this);

    private void DefaultApps_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private void CheckUpdates_Click(object sender, RoutedEventArgs e) => AppUpdateWindow.ShowWindow(_manager);

    private void UpdateToken_Click(object sender, RoutedEventArgs e)
    {
        string? token = TextPrompt.Ask(this, "GitHub access token",
            "Only needed while the Utylix repository on GitHub is private. Paste a fine-grained personal access token with read-only permission for the repository's Contents. It is stored encrypted for this Windows user; leave it empty to remove it.",
            "");
        if (token == null) return;
        string stored = AppUpdater.Protect(token);
        _manager.UpdateConfig(c => c.UpdateToken = stored);
    }

    // ---------- video helpers (yt-dlp, ffmpeg) ----------
    private readonly System.Threading.CancellationTokenSource _toolCts = new();
    private bool _busy;

    protected override void OnClosed(EventArgs e)
    {
        _toolCts.Cancel();      // stop a download in progress if the window is closed
        base.OnClosed(e);
    }

    private async void RefreshTools()
    {
        string? version = await Tools.YtDlpVersionAsync();
        YtStatus.Text = version != null ? $"Installed (version {version})" : Tools.HasYtDlp ? "Installed (won't start)" : "Not installed";
        YtBtn.Content = Tools.HasYtDlp ? "Update" : "Install";
        FfStatus.Text = Tools.HasFfmpeg ? "Installed" : "Not installed (videos are limited to about 720p)";
        FfBtn.Content = Tools.HasFfmpeg ? "Update" : "Install";
    }

    private async System.Threading.Tasks.Task RunToolInstall(Func<Action<string>, System.Threading.CancellationToken, System.Threading.Tasks.Task> install)
    {
        if (_busy) return;
        _busy = true;
        YtBtn.IsEnabled = FfBtn.IsEnabled = false;
        ToolMessage.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        try
        {
            await install(s => Dispatcher.Invoke(() => ToolMessage.Text = s), _toolCts.Token);
        }
        catch (OperationCanceledException) { ToolMessage.Text = "Cancelled."; }
        catch (Exception ex)
        {
            ToolMessage.Text = "Couldn't finish: " + ex.Message;
            ToolMessage.SetResourceReference(TextBlock.ForegroundProperty, "ErrBrush");
        }
        finally
        {
            _busy = false;
            YtBtn.IsEnabled = FfBtn.IsEnabled = true;
            RefreshTools();
        }
    }

    private void YtBtn_Click(object sender, RoutedEventArgs e) => _ = RunToolInstall(Tools.InstallYtDlpAsync);
    private void FfBtn_Click(object sender, RoutedEventArgs e) => _ = RunToolInstall(Tools.InstallFfmpegAsync);

    private void BuildCategoryRows(Config c)
    {
        foreach (var category in Categories.All)
        {
            var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock { Text = $"{Categories.Glyph(category)}  {category}", VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            var box = new TextBox { Text = _manager.CategoryDir(category), Style = (Style)FindResource("Field") };
            var browse = new Button { Content = "Browse…", Style = (Style)FindResource("DialogButton"), Margin = new Thickness(10, 0, 0, 0) };

            if (c.CategoryDirs.ContainsKey(category)) _catEdited.Add(category);
            box.TextChanged += (_, _) => { if (!_loading) _catEdited.Add(category); };
            browse.Click += (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = box.Text, Title = $"Folder for {category} files" };
                if (dlg.ShowDialog(this) == true) box.Text = dlg.FolderName;
            };

            Grid.SetColumn(box, 1);
            Grid.SetColumn(browse, 2);
            row.Children.Add(label);
            row.Children.Add(box);
            row.Children.Add(browse);
            CatRows.Children.Add(row);
            _catBoxes[category] = box;
        }
    }

    private void Sort_Changed(object? sender, RoutedEventArgs e)
    {
        if (CatRows == null) return;   // fires while the XAML is still loading
        bool on = SortBox.IsChecked == true;
        CatRows.IsEnabled = on;
        CatRows.Opacity = on ? 1 : 0.5;
    }

    private void Mode_Changed(object? sender, RoutedEventArgs e)
    {
        if (TypesBox == null) return;   // fires while the XAML is still loading
        TypesBox.IsEnabled = TypesRadio.IsChecked == true;
        TypesBox.Opacity = TypesBox.IsEnabled ? 1 : 0.5;
    }

    private void ResetTypes_Click(object sender, RoutedEventArgs e) => TypesBox.Text = Config.DefaultTypes;

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = DirBox.Text, Title = "Choose download folder" };
        if (dlg.ShowDialog(this) == true) DirBox.Text = dlg.FolderName;
    }

    /// <summary>Saves the settings and makes the browser/Explorer registrations match them at once.</summary>
    private void ApplyAndRegister(Config config)
    {
        var applied = _manager.ApplyConfig(config);
        App.ApplyShortcuts();                       // capture shortcuts follow the checkboxes at once
        if (App.NoRegister) return;
        NativeHost.Register(App.DataDir, applied.AllowBrowserStart);
        ShellMenu.Register(App.DataDir, applied.ExplorerMenu);
        ShellMenu.RegisterArchive(App.DataDir, applied.ExplorerArchiveMenu);
        ShellMenu.RegisterBackground(App.DataDir, applied.ExplorerBgMenu);
        ShellMenu.RegisterPlayer(App.DataDir, applied.ExplorerPlayMenu);
        ShellMenu.RegisterPdfTools(App.DataDir, applied.ExplorerPdfMenu);
        ShellMenu.RegisterTorrent(applied.TorrentHandler);
    }

    private void RefreshPlayerEngine()
    {
        PlayerEngineStatus.Text = VlcEngine.UsingOwnCopy ? "Playback engine: installed" : VlcEngine.Available ? "Playback engine: using the VLC installed on this PC" : "Playback engine: not downloaded yet (asked for on first use)";
        PlayerEngineBtn.Visibility = VlcEngine.UsingOwnCopy ? Visibility.Collapsed : Visibility.Visible;
        PlayerEngineBtn.Content = VlcEngine.Available ? "Download Utylix's own copy" : "Download the player engine";
    }

    private void PlayerEngine_Click(object sender, RoutedEventArgs e)
    {
        var window = new ToolDownloadWindow("Video Player", "Download the player engine",
            "The same engine VLC is made of. About 80 MB, from videolan.org, checked against the checksum VideoLAN publishes, kept on this PC.",
            "Download (80 MB)", VlcEngine.InstallAsync) { Owner = this };
        window.ShowDialog();
        RefreshPlayerEngine();
    }

    private void RefreshBgModel()
    {
        BgModelStatus.Text = BackgroundRemover.HasModel ? "AI model: installed" : "AI model: not downloaded yet (asked for on first use)";
        BgModelBtn.Visibility = BackgroundRemover.HasModel ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BgModel_Click(object sender, RoutedEventArgs e)
    {
        App.EnsureBackgroundModel();
        RefreshBgModel();
    }

    private void RecBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for recordings", InitialDirectory = RecDirBox.Text };
        if (dlg.ShowDialog(this) == true) RecDirBox.Text = dlg.FolderName;
    }

    private void ShotBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder for screenshots", InitialDirectory = ShotDirBox.Text };
        if (dlg.ShowDialog(this) == true) ShotDirBox.Text = dlg.FolderName;
    }

    // ---------- backup ----------
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export settings", FileName = "utylix-settings.json", Filter = "Utylix settings|*.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            System.IO.File.WriteAllText(dlg.FileName, System.Text.Json.JsonSerializer.Serialize(_manager.Config,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            ErrorText.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush");
            ErrorText.Text = "Settings saved to " + dlg.FileName;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            ErrorText.SetResourceReference(TextBlock.ForegroundProperty, "ErrBrush");
            ErrorText.Text = "Couldn't save: " + ex.Message;
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Import settings", Filter = "Utylix settings|*.json|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            if (new System.IO.FileInfo(dlg.FileName).Length > 1024 * 1024) throw new System.IO.IOException("That file is too big to be a settings file.");
            var config = System.Text.Json.JsonSerializer.Deserialize<Config>(System.IO.File.ReadAllText(dlg.FileName))
                         ?? throw new System.IO.IOException("That file is empty.");
            ApplyAndRegister(config);
            UMessage.Show(this, "Settings imported.", "Utylix", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ErrorText.SetResourceReference(TextBlock.ForegroundProperty, "ErrBrush");
            ErrorText.Text = "That isn't a Utylix settings file (" + ex.Message + ")";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ConnBox.Text, out int conn) || conn < 1 || conn > 32)
        {
            ErrorText.Text = "Connections must be a number from 1 to 32.";
            return;
        }
        if (!int.TryParse(MaxBox.Text, out int max) || max < 1 || max > 10)
        {
            ErrorText.Text = "Simultaneous downloads must be a number from 1 to 10.";
            return;
        }
        if (!int.TryParse(MinBox.Text, out int minKb) || minKb < 0)
        {
            ErrorText.Text = "Minimum file size must be a number, 0 or more.";
            return;
        }
        if (!int.TryParse(TorrentDownBox.Text, out int tDown) || tDown < 0 || !int.TryParse(TorrentUpBox.Text, out int tUp) || tUp < 0)
        {
            ErrorText.Text = "The torrent speed limits must be numbers, 0 or more (0 = no limit).";
            return;
        }
        bool typesOnly = TypesRadio.IsChecked == true;
        if (typesOnly && Config.SplitList(TypesBox.Text).Count == 0)
        {
            ErrorText.Text = "Add at least one file type, or choose \"Capture every download\".";
            return;
        }
        try
        {
            ApplyAndRegister(new Config
            {
                DownloadDir = DirBox.Text,
                Connections = conn,
                MaxActive = max,
                SortByType = SortBox.IsChecked == true,
                CategoryDirs = _catBoxes.Where(kv => _catEdited.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value.Text))
                                        .ToDictionary(kv => kv.Key, kv => kv.Value.Text.Trim()),
                ConfirmCaptured = ConfirmBox.IsChecked == true,
                ShowOnDownload = ShowOnDownloadBox.IsChecked == true,
                VideoButton = VideoButtonBox.IsChecked == true,
                WatchClipboard = ClipboardBox.IsChecked == true,
                AllowBrowserStart = BrowserStartBox.IsChecked == true,
                AutoUpdateYtDlp = AutoUpdateBox.IsChecked == true,
                AutoUpdateApp = AutoUpdateAppBox.IsChecked == true,
                TorrentHandler = TorrentHandlerBox.IsChecked == true,
                TorrentSeed = TorrentSeedBox.IsChecked == true,
                TorrentDownKb = tDown,
                TorrentUpKb = tUp,
                SubMode = SubOffRadio.IsChecked == true ? "off" : SubEmbedRadio.IsChecked == true ? "embed" : "file",
                SubLangs = SubLangsBox.Text,
                SubAuto = SubAutoBox.IsChecked == true,
                CaptureTypesOnly = typesOnly,
                CaptureTypes = TypesBox.Text,
                CaptureMinKb = minKb,
                CaptureExclude = ExcludeBox.Text,
                ExplorerMenu = ExplorerMenuBox.IsChecked == true,
                ExplorerArchiveMenu = ArchiveMenuBox.IsChecked == true,
                ExplorerBgMenu = BgMenuBox.IsChecked == true,
                ExplorerPlayMenu = PlayMenuBox.IsChecked == true,
                ExplorerPdfMenu = PdfMenuBox.IsChecked == true,
                ShotWinS = ShotWinSBox.IsChecked == true,
                OpenWinF = WinFBox.IsChecked == true,
                ShotCtrlAltS = ShotCtrlAltBox.IsChecked == true,
                ShotCopy = ShotCopyBox.IsChecked == true,
                ShotAutoSave = ShotAutoSaveBox.IsChecked == true,
                ShotDir = ShotDirBox.Text,
                RecHotkey = RecHotkeyBox.IsChecked == true,
                RecDir = RecDirBox.Text,
            });
            App.SetAutoStart(AutoStartBox.IsChecked == true);
            // Windows' own Win + F (Feedback Hub) and Win + S (Search) can be switched off for this account
            var changes = new List<string>();
            bool failed = false;
            foreach (var (letter, box, what) in new[] { ('F', DisableWinFBox, "Win + F (the Feedback Hub)"), ('S', DisableWinSBox, "Win + S (Windows Search)") })
            {
                bool want = box.IsChecked == true;
                if (want == ExplorerHotkeys.IsDisabled(letter)) continue;
                if (ExplorerHotkeys.Set(letter, want)) changes.Add(want ? $"Windows' own {what} is switched off." : $"Windows' own {what} is switched on again.");
                else failed = true;
            }
            if (changes.Count > 0)
                UMessage.Show(string.Join(Environment.NewLine, changes) + Environment.NewLine + Environment.NewLine + "It takes effect after you sign out of Windows and in again.",
                                "Utylix", MessageBoxButton.OK, MessageBoxImage.Information);
            if (failed) UMessage.Show("Windows would not let Utylix change this setting.", "Utylix", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            return;
        }
        DialogResult = true;
    }
}
