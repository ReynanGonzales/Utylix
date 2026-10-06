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
        Height = Math.Min(Height, MaxHeight);

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
        ShotHideBox.IsChecked = c.ShotHideWindows;
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
        // looks, notices
        TrayNoticeBox.IsChecked = c.TrayNotice;
        _theme = c.Theme is "dark" or "light" ? c.Theme : "system"; _accent = c.Accent;
        (_theme == "dark" ? ThemeDarkRadio : _theme == "light" ? ThemeLightRadio : ThemeSystemRadio).IsChecked = true;
        BuildAccentChips();
        _themeLoading = false;
        BuildPdfKeys();
        BuildMusicKeys();
        ShowPage(PageFor(tool));
    }

    // ---------- the list of pages on the left ----------
    private static string PageFor(string? tool) => tool switch
    {
        "downloads" => "Downloads", "converter" => "RightClick", "capture" => "Capture", "recorder" => "Recorder", "player" => "Player", _ => "General",
    };

    private bool _showing;

    private void ShowPage(string name)
    {
        if (_showing) return;
        _showing = true;
        try
        {
            foreach (var child in ((Grid)PageScroll.Content).Children.OfType<StackPanel>())
                child.Visibility = child.Name == "Page" + name ? Visibility.Visible : Visibility.Collapsed;
            foreach (var tab in SideNav.Children.OfType<RadioButton>())
                tab.IsChecked = (string?)tab.Tag == name;
            PageScroll.ScrollToTop();
            string label = SideNav.Children.OfType<RadioButton>().FirstOrDefault(t => (string?)t.Tag == name)?.Content as string ?? "";
            Title = name == "General" ? "Settings" : "Settings - " + label;
        }
        finally { _showing = false; }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string name } && !_showing) ShowPage(name);
    }

    // ---------- theme and accent (tried at once; Cancel puts them back) ----------
    private string _theme = "system", _accent = "blue";
    private bool _themeLoading = true;
    private readonly List<(RadioButton Chip, string Key)> _accentChips = new();

    private void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (_themeLoading) return;
        _theme = ThemeDarkRadio.IsChecked == true ? "dark" : ThemeLightRadio.IsChecked == true ? "light" : "system";
        App.PreviewTheme(_theme, _accent);
        ColourAccentChips();
    }

    private void BuildAccentChips()
    {
        const string xaml = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='RadioButton'>" +
            "<Grid><Ellipse x:Name='ring' Stroke='Transparent' StrokeThickness='2.5' /><Ellipse Margin='5' Fill='{TemplateBinding Background}' /></Grid>" +
            "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='ring' Property='Stroke' Value='{DynamicResource MutedBrush}' /></Trigger>" +
            "<Trigger Property='IsChecked' Value='True'><Setter TargetName='ring' Property='Stroke' Value='{DynamicResource TextBrush}' /></Trigger>" +
            "</ControlTemplate.Triggers></ControlTemplate>";
        var template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
        foreach (var a in App.Accents)
        {
            var chip = new RadioButton { GroupName = "accent", Width = 38, Height = 38, Margin = new Thickness(0, 0, 8, 4), Template = template, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = a.Name, IsChecked = a.Key == _accent, Focusable = false };
            System.Windows.Automation.AutomationProperties.SetName(chip, a.Name + " accent");
            System.Windows.Automation.AutomationProperties.SetAutomationId(chip, "SettingsAccent" + a.Key);
            string key = a.Key;
            chip.Checked += (_, _) => { if (_themeLoading) return; _accent = key; App.PreviewTheme(_theme, _accent); };
            AccentPanel.Children.Add(chip);
            _accentChips.Add((chip, a.Key));
        }
        if (!_accentChips.Any(x => x.Chip.IsChecked == true)) { _accent = "blue"; _accentChips[0].Chip.IsChecked = true; }
        ColourAccentChips();
    }

    /// <summary>Each chip shows the colour it gives in the theme that is on now.</summary>
    private void ColourAccentChips()
    {
        foreach (var (chip, key) in _accentChips)
        {
            var a = Array.Find(App.Accents, x => x.Key == key);
            chip.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(App.IsDarkTheme ? a.Dark : a.Light));
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        // Cancel (or the X): the saved theme comes back; Save already stored the new one
        if (DialogResult != true) App.ReapplyTheme();
    }

    private void BuildMusicKeys()
    {
        foreach (var (keys, what) in new[] { ("Space", "Play / pause"), ("N  /  P", "Next / previous song"), ("Left  /  Right", "Back / forward 5 seconds"), ("Up  /  Down", "Volume"),
                                             ("S", "Shuffle on / off"), ("R", "Repeat: off, all songs, this song"), ("F", "Mark the playing song with the heart (favourite)"),
                                             ("F2", "Edit the info of the chosen songs (title, artist, album, cover ...)"), ("L", "Next look (classic, wide card, dark, frosted, light, waveform)"),
                                             ("Ctrl + O", "Add songs"), ("Delete", "Remove the chosen songs from the playlist"), ("Esc", "Close the player") })
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var chip = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(1) };
            chip.SetResourceReference(Border.BackgroundProperty, "BgBrush");
            chip.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            chip.Child = new TextBlock { Text = keys, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12.5 };
            var text = new TextBlock { Text = what, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1);
            row.Children.Add(chip); row.Children.Add(text);
            MusicKeysPanel.Children.Add(row);
        }
    }

    // ---------- the PDF editor's keys, for reference ----------
    private void BuildPdfKeys()
    {
        void Heading(string text) => PdfKeysPanel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, PdfKeysPanel.Children.Count == 0 ? 0 : 16, 0, 6) });
        void Row(string keys, string what)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var chip = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(1) };
            chip.SetResourceReference(Border.BackgroundProperty, "BgBrush");
            chip.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            chip.Child = new TextBlock { Text = keys, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12.5 };
            var text = new TextBlock { Text = what, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1);
            row.Children.Add(chip);
            row.Children.Add(text);
            PdfKeysPanel.Children.Add(row);
        }
        // (sorted like the tabs of the editor's tool bar)
        void Tools(string heading, params (string Key, string What)[] list)
        {
            Heading(heading);
            foreach (var (key, what) in list) Row("Alt + " + key, what);
        }
        Tools("Tools while editing (hold Alt) - everywhere", ("V", "Select, move, resize"));
        Tools("Tab Add", ("T", "Text"), ("E", "Edit text already in the PDF"), ("D", "Date"), ("I", "Picture"), ("G", "Sign"), ("M", "Stamp"), ("J", "Link"));
        Tools("Tab Mark up", ("H", "Highlight"), ("U", "Underline"), ("K", "Strike"), ("N", "Note"), ("P", "Pen"), ("S", "Shapes"), ("L", "Table"), ("C", "Check mark"), ("X", "Cross"));
        Tools("Tab Forms", ("F", "Fillable text box"), ("B", "Fillable check box"), ("O", "Fillable round option"), ("Y", "Drop-down list"), ("Q", "Signature box"));
        Row("Enter / double-click a field", "Field options (name, must be filled in, several lines, choices ...)");
        Tools("Tab Page", ("W", "White-out"), ("R", "Redact"));
        Heading("Choosing and arranging");
        Row("Ctrl + Z", "Undo (also after saving)");
        Row("Drag a box with Select", "Choose several things: move or delete them together");
        Row("Ctrl + ]  /  Ctrl + [", "Bring forward / send backward (Shift: to the front / back)");
        Row("Ctrl + Shift + G  /  Ctrl + Shift + U", "Group / ungroup the chosen things (a group moves, copies and deletes as one)");
        Heading("File");
        foreach (var (keys, what) in new[] { ("Ctrl + O", "Open a PDF"), ("Ctrl + S", "Save"), ("Ctrl + Shift + S", "Save as"), ("Ctrl + P", "Print"), ("Ctrl + W", "Close the window") }) Row(keys, what);
        Heading("Editing");
        foreach (var (keys, what) in new[] { ("Ctrl + E", "Turn Edit on or off"), ("Ctrl + Z", "Undo"), ("Ctrl + Y  /  Ctrl + Shift + Z", "Redo"), ("Delete", "Remove what is selected"),
                                             ("Arrow keys", "Nudge what is selected (Shift = bigger steps)"), ("Enter", "Change the selected text"), ("Esc", "Let go of the selection / finish typing"),
                                             ("Shift + Enter", "New line while changing a line of the PDF's own text") }) Row(keys, what);
        Heading("Looking");
        foreach (var (keys, what) in new[] { ("Ctrl + F", "Search"), ("F3  /  Shift + F3", "Next / previous result"), ("Ctrl + A", "Select all text on the page"), ("Ctrl + C", "Copy the selected text"),
                                             ("Ctrl + +  /  Ctrl + -", "Zoom in / out"), ("Ctrl + 0", "Whole page"), ("Ctrl + 1", "100 %"), ("Ctrl + 2", "Fit the width"), ("Ctrl + R", "Turn the pages (for looking only)"),
                                             ("Ctrl + G", "Go to a page"), ("Home  /  End", "First / last page"), ("F4", "Pages at the side") }) Row(keys, what);
    }


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
        FfStatus.Text = Tools.HasFfmpeg ? "Installed" : "Not installed (video limited to ~720p)";
        FfBtn.Content = Tools.HasFfmpeg ? "Update" : "Install";
        bool pawn = PawnIoSetup.Installed;
        PawnStatus.Text = pawn ? "Installed" : "Not installed (fan control and RAM lighting need it)";
        PawnBtn.Content = pawn ? "Installed" : "Install";
        PawnBtn.IsEnabled = !pawn && !_busy;
    }

    private async System.Threading.Tasks.Task RunToolInstall(Func<Action<string>, System.Threading.CancellationToken, System.Threading.Tasks.Task> install)
    {
        if (_busy) return;
        _busy = true;
        YtBtn.IsEnabled = FfBtn.IsEnabled = PawnBtn.IsEnabled = false;
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
            RefreshTools();                                   // (also sets PawnBtn: off once PawnIO is there)
        }
    }

    private void YtBtn_Click(object sender, RoutedEventArgs e) => _ = RunToolInstall(Tools.InstallYtDlpAsync);
    private void FfBtn_Click(object sender, RoutedEventArgs e) => _ = RunToolInstall(Tools.InstallFfmpegAsync);
    private void PawnBtn_Click(object sender, RoutedEventArgs e) => _ = RunToolInstall(PawnIoSetup.InstallAsync);

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

    private void About_Click(object sender, RoutedEventArgs e) => AboutWindow.ShowIt(this);

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
                ShotHideWindows = ShotHideBox.IsChecked == true,
                Theme = _theme,
                Accent = _accent,
                TrayNotice = TrayNoticeBox.IsChecked == true,
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
