using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>The "New download" window shown when the browser hands a download over (like IDM's Download File Info).</summary>
public partial class CaptureWindow : Window
{
    private readonly Manager _manager;
    private readonly Download _download;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private string _category = Categories.Other;
    private bool _nameEdited;      // the user typed a file name
    private bool _catChosen;       // the user clicked a type chip
    private bool _dirEdited;       // the user typed/browsed a folder
    private bool _programmatic;    // we are filling the boxes ourselves
    private bool _finished;

    private string _origExt = "";      // extension the file has on the server (".png"), what actually gets downloaded
    private ImgFormat? _convert;       // "Convert to" chip: turn the finished picture into this format

    public CaptureWindow(Manager manager, Download download)
    {
        InitializeComponent();
        _manager = manager;
        _download = download;

        string host = Uri.TryCreate(download.Url, UriKind.Absolute, out var u) ? u.Host : download.Url;
        SubText.Text = host;
        SizeHeadline.ToolTip = download.Url;
        SubText.ToolTip = download.Url;

        // type chips: Compressed, Picture, ... Other. Hidden when sorting by type is switched off.
        bool sort = manager.Config.SortByType;
        CatLabel.Visibility = CatPanel.Visibility = sort ? Visibility.Visible : Visibility.Collapsed;
        foreach (var category in new System.Collections.Generic.List<string>(Categories.All) { Categories.Other })
        {
            var chip = new RadioButton
            {
                Content = category,
                Tag = category,
                GroupName = "type",
                Style = (Style)FindResource("ChipButton"),
            };
            chip.Checked += Chip_Checked;
            CatPanel.Children.Add(chip);
        }

        // "When done" chips: nothing / open it / show in its folder (the last choice is remembered)
        string remembered = manager.Config.AfterDownload;
        AddAfterChip("Just notify me", "none", remembered is not ("open" or "folder"));
        AddAfterChip("Open it", "open", remembered == "open", "Opens the file with its usual program. Programs and scripts are never opened by themselves.");
        AddAfterChip("Show in folder", "folder", remembered == "folder");

        // "Convert to" chips (pictures only): keep as is, JPG, PNG, ...
        AddConvertChip("Keep as is", null);
        foreach (ImgFormat f in Enum.GetValues<ImgFormat>()) AddConvertChip(ImageConverter.Label(f), f);

        // a video-site download has no file name yet: suggest "<title>.mp4" (or .m4a for audio only)
        SetName(download.IsMedia
            ? Util.Sanitize(download.MediaTitle ?? download.Info().FileName) + download.MediaExt
            : download.Info().FileName);
        _origExt = Path.GetExtension(NameBox.Text);
        UpdateConvertRow();
        ApplyCategory(Categories.Of(NameBox.Text), fromUser: false);
        SetDir(download.Dir);

        _timer.Tick += (_, _) => CheckProbe();
        _timer.Start();
        SourceInitialized += (_, _) =>
        {
            if (App.IsDarkTheme)
            {
                int on = 1;
                DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, sizeof(int));
            }
        };
    }

    /// <summary>Show above the browser even though we can't take focus from it.</summary>
    public void ShowOnTop()
    {
        Topmost = true;
        Show();
        Activate();
    }

    // ---------- fields ----------
    private void SetName(string name)
    {
        _programmatic = true;
        NameBox.Text = name;
        _programmatic = false;
        UpdateTile();
        UpdateHeadline();
    }

    private string _sizeText = "getting size…";

    /// <summary>Headline: the file name with its size at the end, e.g. "World (v.18).rar (5.4 GB)".</summary>
    private void UpdateHeadline()
    {
        string name = NameBox.Text.Trim();
        SizeHeadline.Inlines.Clear();
        SizeHeadline.Inlines.Add(new System.Windows.Documents.Run(Shorten(name, 34)) { FontWeight = FontWeights.SemiBold });
        var size = new System.Windows.Documents.Run($"  ({_sizeText})") { FontSize = 16 };
        size.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "MutedBrush");
        SizeHeadline.Inlines.Add(size);
        SizeHeadline.ToolTip = name.Length > 0 ? $"{name}  ·  {_download.Url}" : _download.Url;
    }

    /// <summary>Shorten a long name in the middle but keep the extension, so the size after it stays visible.</summary>
    private static string Shorten(string name, int max)
    {
        if (name.Length <= max) return name;
        string ext = System.IO.Path.GetExtension(name);
        if (ext.Length > 8) ext = "";
        int keep = Math.Max(6, max - ext.Length - 1);
        return name[..keep].TrimEnd() + "…" + ext;
    }

    /// <summary>The file's own icon (WinRAR's for .rar, ...) if Windows has one, else our category symbol.</summary>
    private void UpdateTile()
    {
        var icon = ShellIcons.ForFile(NameBox.Text);
        if (icon != null)
        {
            IconImage.Source = icon;
            IconImage.Visibility = Visibility.Visible;
            GlyphText.Visibility = Visibility.Collapsed;
            Tile.SetResourceReference(Border.BackgroundProperty, "CardBrush");
            Tile.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        }
        else
        {
            IconImage.Visibility = Visibility.Collapsed;
            GlyphText.Visibility = Visibility.Visible;
            Tile.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            Tile.BorderBrush = System.Windows.Media.Brushes.Transparent;
        }
    }

    private void SetDir(string dir)
    {
        _programmatic = true;
        DirBox.Text = dir;
        _programmatic = false;
    }

    /// <summary>Select a type chip; the folder follows unless the user already typed one.</summary>
    private void ApplyCategory(string category, bool fromUser)
    {
        _category = category;
        GlyphText.Text = Categories.Glyph(category);
        _programmatic = true;
        foreach (RadioButton chip in CatPanel.Children) chip.IsChecked = (string)chip.Tag == category;
        _programmatic = false;
        if (fromUser || !_dirEdited)
        {
            SetDir(category == Categories.Other || !_manager.Config.SortByType ? _manager.Config.DownloadDir : _manager.CategoryDir(category));   // sorting off: everything goes to the download folder
            if (fromUser) _dirEdited = false;
        }
    }

    private void Chip_Checked(object sender, RoutedEventArgs e)
    {
        if (_programmatic) return;
        _catChosen = true;
        ApplyCategory((string)((RadioButton)sender).Tag, fromUser: true);
    }

    // ---------- when done ----------
    private string _after = "none";

    private void AddAfterChip(string text, string value, bool isChecked, string? tip = null)
    {
        var chip = new RadioButton { Content = text, GroupName = "after", Style = (Style)FindResource("ChipButton"), IsChecked = isChecked, ToolTip = tip };
        if (isChecked) _after = value;
        chip.Checked += (_, _) => _after = value;
        AfterChips.Children.Add(chip);
    }

    // ---------- convert to another picture format ----------
    private void AddConvertChip(string label, ImgFormat? format)
    {
        var chip = new RadioButton
        {
            Content = label, Tag = format, GroupName = "conv", Style = (Style)FindResource("ChipButton"), IsChecked = format == null,
        };
        chip.Checked += (_, _) =>
        {
            if (_programmatic) return;
            _convert = (ImgFormat?)chip.Tag;
            SetName(ComposeName(Path.GetFileNameWithoutExtension(NameBox.Text)));
            UpdateConvertRow();
        };
        ConvChips.Children.Add(chip);
    }

    /// <summary>"photo" -> "photo.png" (as downloaded) or "photo.ico" (when converting).</summary>
    private string ComposeName(string baseName) =>
        baseName + (_convert is { } f ? "." + ImageConverter.Extension(f) : _origExt);

    /// <summary>Only pictures can be converted. The chip for the picture's own format is not offered.</summary>
    private void UpdateConvertRow()
    {
        string ext = _origExt.TrimStart('.').ToLowerInvariant();
        bool picture = !_download.IsMedia && ImageConverter.IsPicture("x." + ext) &&
                       (Tools.HasFfmpeg || ext is not ("webp" or "avif" or "heic"));   // those need ffmpeg to be read
        ConvLabel.Visibility = ConvPanel.Visibility = picture ? Visibility.Visible : Visibility.Collapsed;
        KeepBox.Visibility = picture && _convert != null ? Visibility.Visible : Visibility.Collapsed;
        bool own = ImageConverter.TryParseFormat(ext, out var ownFormat);
        foreach (RadioButton chip in ConvChips.Children)
        {
            var f = (ImgFormat?)chip.Tag;
            bool offered = f == null || !(own && f == ownFormat);
            if (f == ImgFormat.WebP && !Tools.HasFfmpeg) { offered = false; chip.ToolTip = "WebP needs ffmpeg (Settings → Video sites)"; }
            chip.Visibility = offered ? Visibility.Visible : Visibility.Collapsed;
        }
        if (!picture && _convert != null) SelectConvert(null);
    }

    private void SelectConvert(ImgFormat? format)
    {
        _convert = format;
        _programmatic = true;
        foreach (RadioButton chip in ConvChips.Children) chip.IsChecked = (ImgFormat?)chip.Tag == format;
        _programmatic = false;
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_programmatic) return;
        _nameEdited = true;
        if (_convert == null) { _origExt = Path.GetExtension(NameBox.Text); UpdateConvertRow(); }   // renamed to .png => convertible
        UpdateTile();                                                                     // renaming to .png => image icon
        UpdateHeadline();
        if (!_catChosen) ApplyCategory(Categories.Of(NameBox.Text), fromUser: false);   // renaming to .png => Picture
    }

    private void DirBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_programmatic) _dirEdited = true;
    }

    private void CheckProbe()
    {
        if (!_download.ProbeDone) return;
        _timer.Stop();
        var info = _download.Info();
        if (_download.ProbeError != null && !_download.Probed)
        {
            _sizeText = "size unknown";
            UpdateHeadline();
            SubText.Text = "Couldn't read file info: " + _download.ProbeError + "  ·  you can still try to download it";
            SubText.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["ErrBrush"];
        }
        else
        {
            if (!_nameEdited && !_download.IsMedia)
            {
                _origExt = Path.GetExtension(info.FileName);
                if (_convert is { } picked && ImageConverter.TryParseFormat(_origExt, out var own) && own == picked) SelectConvert(null);
                SetName(ComposeName(Path.GetFileNameWithoutExtension(info.FileName)));
                UpdateConvertRow();
            }
            if (!_catChosen) ApplyCategory(Categories.Of(NameBox.Text), fromUser: false);
            _sizeText = info.Size > 0 ? (_download.IsMedia ? "≈ " : "") + Format.Bytes(info.Size) : "unknown size";
            UpdateHeadline();
            string mode = _download.IsMedia ? "video site (picture and sound joined into one file)"
                : info.Resumable ? "fast download (multi-connection)" : "single connection only";
            string host = Uri.TryCreate(_download.Url, UriKind.Absolute, out var u) ? u.Host : _download.Url;
            SubText.Text = $"{mode}  ·  {host}";
        }
        StartBtn.IsEnabled = true;
        LaterBtn.IsEnabled = true;
        StartBtn.Focus();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = DirBox.Text, Title = "Save to" };
        if (dlg.ShowDialog(this) != true) return;
        SetDir(dlg.FolderName);
        _dirEdited = true;
    }

    // ---------- buttons ----------
    private void Start_Click(object sender, RoutedEventArgs e) => Commit(start: true);
    private void Later_Click(object sender, RoutedEventArgs e) => Commit(start: false);

    private void Commit(bool start)
    {
        string dir = DirBox.Text.Trim();
        string name = NameBox.Text.Trim();
        if (name.Length == 0) { ShowError("Enter a file name."); return; }
        try
        {
            dir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dir));
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            ShowError("That folder can't be used: " + ex.Message);
            return;
        }

        // Picked a different folder for this type (say a png -> D:\Pics)? Make it the new default for the type.
        if (_manager.Config.SortByType && _category != Categories.Other && _dirEdited &&
            !string.Equals(Path.TrimEndingDirectorySeparator(dir),
                           Path.TrimEndingDirectorySeparator(Path.GetFullPath(_manager.CategoryDir(_category))),
                           StringComparison.OrdinalIgnoreCase))
            _manager.RememberCategoryDir(_category, dir);

        if (_convert is { } target)
        {
            // download the picture under its own type, convert it when it has arrived
            string targetExt = "." + ImageConverter.Extension(target);
            string baseName = name.EndsWith(targetExt, StringComparison.OrdinalIgnoreCase) ? name[..^targetExt.Length] : name;
            _download.SetTarget(Util.Sanitize(baseName + _origExt), dir);
            _download.SetConvert(ImageConverter.Extension(target), KeepBox.IsChecked == true);
        }
        else _download.SetTarget(name, dir);
        _download.SetAfterDone(_after);
        _manager.UpdateConfig(c => c.AfterDownload = _after);      // remembered for next time
        if (start) _download.Begin(); else _download.Later();
        _manager.Save();
        _finished = true;
        Close();
    }

    /// <summary>The error line takes no room until there is something to say.</summary>
    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        if (!_finished) _manager.Remove(_download.Id, false);   // cancelled: forget the download
        base.OnClosed(e);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
