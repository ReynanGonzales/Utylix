using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>One row of the file list.</summary>
public sealed class ConvItem : INotifyPropertyChanged
{
    public ConvItem(string path, MediaKind kind)
    {
        Path = path;
        Kind = kind;
        Name = System.IO.Path.GetFileName(path);
        try { SizeText = Format.Bytes(new FileInfo(path).Length); } catch (IOException) { SizeText = ""; }
        _status = "Ready";
        _brush = Find("MutedBrush");
    }

    public string Path { get; }
    public MediaKind Kind { get; }
    public string Glyph => Kind switch { MediaKind.Video => "🎬", MediaKind.Audio => "🎵", _ => "🖼" };
    public string Name { get; }
    public string SizeText { get; }
    public string? Output { get; set; }

    private string _status;
    private Brush _brush;
    public string Status { get => _status; set { _status = value; Raise(nameof(Status)); } }
    public Brush StatusBrush { get => _brush; set { _brush = value; Raise(nameof(StatusBrush)); } }

    public static Brush Find(string key) => (Brush)Application.Current.FindResource(key);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Multi Convert: pictures, videos and music into another format.</summary>
public partial class ConverterPage : UserControl
{
    private readonly ObservableCollection<ConvItem> _items = new();
    private volatile bool _running;
    private CancellationTokenSource? _cts;
    private string? _lastOutput;
    private const int MaxFiles = 500;

    private MediaKind _kind = MediaKind.Image;
    private string _target = "jpg";
    private string _videoQuality = "balanced";
    private int _maxHeight;
    private int _kbps = 192;
    private bool _building;

    public ImageSource AppIcon => WindowTheme.ConvertIcon;

    public ConverterPage()
    {
        InitializeComponent();
        FileList.ItemsSource = _items;

        foreach (var (label, kind) in new[] { ("Images", MediaKind.Image), ("Video", MediaKind.Video), ("Audio", MediaKind.Audio) })
            AddChip(KindChips, label, "kind", () => { if (!_building) { _kind = kind; BuildFormats(); } }, kind == MediaKind.Image);
        foreach (var (label, q) in new[] { ("High", "high"), ("Balanced", "balanced"), ("Small file", "small") })
            AddChip(VideoQualityChips, label, "vq", () => _videoQuality = q, q == "balanced");
        foreach (var (label, h) in new[] { ("Original size", 0), ("1080p", 1080), ("720p", 720), ("480p", 480) })
            AddChip(VideoSizeChips, label, "vs", () => _maxHeight = h, h == 0);
        foreach (int k in new[] { 128, 192, 256, 320 })
            AddChip(AudioQualityChips, $"{k} kbps", "aq", () => _kbps = k, k == 192);

        BuildFormats();
        UpdateState();
    }

    private void AddChip(WrapPanel host, string text, string group, Action onChecked, bool isChecked = false)
    {
        var chip = new RadioButton { Content = text, GroupName = group, Style = (Style)FindResource("ChipButton"), IsChecked = isChecked };
        chip.Checked += (_, _) => onChecked();
        host.Children.Add(chip);
    }

    // ---------- adding files ----------
    public void AddFiles(IEnumerable<string> paths)
    {
        bool wasEmpty = _items.Count == 0;
        int added = 0, skipped = 0;
        foreach (var raw in paths)
        {
            string path;
            try { path = Path.GetFullPath(raw); } catch (Exception) { skipped++; continue; }
            if (Directory.Exists(path))                                      // a dropped folder: take the files inside it
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(path).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                        if (MediaConverter.IsSupported(f)) { if (TryAdd(f)) added++; else skipped++; }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
                continue;
            }
            if (!File.Exists(path) || !MediaConverter.IsSupported(path)) { skipped++; continue; }
            if (TryAdd(path)) added++; else skipped++;
        }

        // first files into an empty list: switch to what they are (drop a video and "Video" is already chosen)
        if (wasEmpty && _items.Count > 0 && _items[0].Kind != _kind) SelectKind(_items[0].Kind);

        UpdateState();
        if (_running) return;
        if (skipped > 0)
            SetStatus($"{added} added, {skipped} skipped (not a picture, video or music file, already in the list, or the list is full).", error: added == 0);
        else
            SetStatus(added > 0 ? $"{added} file{(added == 1 ? "" : "s")} added." : "", error: false);
    }

    private bool TryAdd(string path)
    {
        if (_items.Count >= MaxFiles || _items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) return false;
        _items.Add(new ConvItem(path, MediaConverter.KindOf(path)!.Value));
        return true;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var all = ImageConverter.InputExtensions.Concat(MediaConverter.VideoExtensions).Concat(MediaConverter.AudioExtensions).Select(x => "*." + x);
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose files", Multiselect = true,
            Filter = "Pictures, videos and music|" + string.Join(";", all) + "|All files|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) AddFiles(dlg.FileNames);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        foreach (var item in FileList.SelectedItems.Cast<ConvItem>().ToList()) _items.Remove(item);
        UpdateState();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        _items.Clear();
        ShowBtn.Visibility = Visibility.Collapsed;
        SetStatus("", false);
        UpdateState();
    }

    private void FileList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Delete) Remove_Click(sender, e);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !_running ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!_running && e.Data.GetData(DataFormats.FileDrop) is string[] files) AddFiles(files);
    }

    // ---------- what to convert to ----------
    private static string KindLabel(MediaKind kind) => kind == MediaKind.Image ? "Images" : kind.ToString();

    private void SelectKind(MediaKind kind)
    {
        _building = true;
        foreach (RadioButton chip in KindChips.Children) chip.IsChecked = (string)chip.Content == KindLabel(kind);
        _building = false;
        _kind = kind;
        BuildFormats();
    }

    /// <summary>The format chips for the chosen kind (pictures / video / music).</summary>
    private void BuildFormats()
    {
        string[] targets = _kind == MediaKind.Image ? MediaConverter.ImageTargets : _kind == MediaKind.Video ? MediaConverter.VideoTargets : MediaConverter.AudioTargets;
        string keep = targets.Contains(_target) ? _target : targets[_kind == MediaKind.Image ? 1 : 0];      // JPG / MP4 / MP3 to start with
        _building = true;
        FormatChips.Children.Clear();
        foreach (var t in targets)
            AddChip(FormatChips, MediaConverter.Label(t), "fmt", () => { if (!_building) { _target = t; FormatChanged(); } }, t == keep);
        _building = false;
        _target = keep;
        FormatChanged();
    }

    private void FormatChanged()
    {
        if (QualityRow == null) return;
        bool image = _kind == MediaKind.Image;
        QualityRow.Visibility = image && (_target == "jpg" || _target == "webp") ? Visibility.Visible : Visibility.Collapsed;
        SizeRow.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
        SizeRow.IsEnabled = _target != "ico";
        SizeRow.Opacity = SizeRow.IsEnabled ? 1 : 0.5;
        VideoRow.Visibility = _kind == MediaKind.Video ? Visibility.Visible : Visibility.Collapsed;
        VideoQualityRow.Visibility = _target == "gif" ? Visibility.Collapsed : Visibility.Visible;
        AudioRow.Visibility = _kind == MediaKind.Audio && (_target == "mp3" || _target == "m4a" || _target == "ogg") ? Visibility.Visible : Visibility.Collapsed;
        FormatNote.Text = NoteFor(_target);
        RefreshFfmpegBanner();
    }

    private string NoteFor(string target) => target switch
    {
        "jpg" => "Small files, best for photos. Transparent areas become white.",
        "png" => "Sharp and lossless, keeps transparency. Best for screenshots and logos.",
        "webp" => Tools.HasFfmpeg ? "Small files for the web, keeps transparency." : "Small files for the web. Needs ffmpeg (install it below).",
        "bmp" => "Uncompressed, large files. Transparent areas become white.",
        "gif" when _kind == MediaKind.Video => "An animated GIF: a short silent clip, 12 pictures a second, 480 pixels high unless you choose a size. Large for long videos.",
        "gif" => "Limited to 256 colors and no soft edges, so best for simple graphics.",
        "tiff" => "Lossless, used for printing and archives.",
        "ico" => "A Windows icon with the standard sizes (16 up to 256 pixels). Best made from a square picture.",
        "mp4" => "Plays everywhere: phones, TVs, browsers (H.264 + AAC).",
        "mkv" => "A flexible container that plays in VLC and most players (H.264 + AAC).",
        "webm" => "Small files for the web (VP9 + Opus). Slower to make.",
        "mov" => "For Apple devices and editing programs (H.264 + AAC).",
        "avi" => "An old format for old players. Files are large.",
        "mp3" => "Plays everywhere. From a video this saves just the sound.",
        "m4a" => "Good quality at a small size (AAC), friendly with Apple devices. From a video this saves just the sound.",
        "wav" => "Uncompressed and perfect, but large. From a video this saves just the sound.",
        "flac" => "Lossless: perfect quality at about half the size of WAV. From a video this saves just the sound.",
        _ => "An open format (Opus) that sounds very good even at small sizes. From a video this saves just the sound.",
    };

    private void Quality_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (QualityText != null) QualityText.Text = ((int)QualitySlider.Value).ToString();
    }

    private void Resize_Changed(object sender, RoutedEventArgs e) => SideBox.IsEnabled = ResizeBox.IsChecked == true;

    private void Dest_Changed(object sender, RoutedEventArgs e)
    {
        if (OutBox == null) return;
        OutBox.IsEnabled = OutBrowse.IsEnabled = OtherFolderRadio.IsChecked == true;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Save converted files in…", InitialDirectory = OutBox.Text };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) OutBox.Text = dlg.FolderName;
    }

    // ---------- ffmpeg ----------
    private bool FfmpegNeeded => (_kind != MediaKind.Image || _target == "webp") && !Tools.HasFfmpeg;

    private void RefreshFfmpegBanner()
    {
        if (FfmpegBanner == null) return;
        FfmpegBanner.Visibility = FfmpegNeeded ? Visibility.Visible : Visibility.Collapsed;
        InstallBtn.IsEnabled = true;
    }

    private async void InstallFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        InstallBtn.IsEnabled = false;
        try
        {
            await Tools.InstallFfmpegAsync(s => Dispatcher.Invoke(() => FfmpegText.Text = s), CancellationToken.None);
            FormatChanged();
        }
        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or InvalidOperationException or UnauthorizedAccessException)
        {
            FfmpegText.Text = "Couldn't install ffmpeg: " + ex.Message;
            InstallBtn.IsEnabled = true;
        }
    }

    // ---------- converting ----------
    private void UpdateState()
    {
        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConvertBtn.IsEnabled = _items.Count > 0 && !_running;
        ConvertBtn.Content = _running ? "Converting…" : "Convert";
        CancelBtn.Visibility = _running ? Visibility.Visible : Visibility.Collapsed;
        RemoveBtn.IsEnabled = ClearBtn.IsEnabled = !_running;
    }

    private void SetStatus(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrBrush" : "TextBrush");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _items.Count == 0) return;

        int maxSide = 0;
        if (_kind == MediaKind.Image && ResizeBox.IsChecked == true && _target != "ico")
        {
            if (!int.TryParse(SideBox.Text, out maxSide) || maxSide < 16 || maxSide > 20000)
            {
                SetStatus("Enter a size from 16 to 20000 pixels.", true);
                return;
            }
        }
        string? outDir = null;
        if (OtherFolderRadio.IsChecked == true)
        {
            if (OutBox.Text.Trim().Length == 0) { SetStatus("Choose a folder to save into.", true); return; }
            try { outDir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(OutBox.Text.Trim())); }
            catch (Exception) { SetStatus("That folder name isn't valid.", true); return; }
        }

        string target = _target;
        var mediaOptions = new MediaOptions(_videoQuality, _maxHeight, _kbps);
        ImgFormat imageFormat = ImgFormat.Jpg;
        bool imageTarget = MediaConverter.ImageTargets.Contains(target) && ImageConverter.TryParseFormat(target, out imageFormat);
        var imageOptions = new ConvertOptions(imageFormat, (int)QualitySlider.Value, maxSide, outDir);

        var work = _items.ToList();
        foreach (var item in work) { item.Status = "Waiting…"; item.StatusBrush = ConvItem.Find("MutedBrush"); item.Output = null; }
        _running = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        ShowBtn.Visibility = Visibility.Collapsed;
        SetStatus("Converting…", false);
        UpdateState();

        int ok = 0, failed = 0, skipped = 0, cancelled = 0;
        foreach (var item in work)
        {
            if (ct.IsCancellationRequested) { item.Status = "Cancelled"; cancelled++; continue; }
            if (!MediaConverter.Compatible(item.Kind, target))
            {
                skipped++;
                item.Status = MediaConverter.WhyNot(item.Kind, target);
                continue;
            }
            item.Status = "Converting…";
            item.StatusBrush = ConvItem.Find("TextBrush");
            try
            {
                string output;
                if (item.Kind == MediaKind.Image && imageTarget)
                    output = await Sta.Run(() => ImageConverter.Convert(item.Path, imageOptions));       // Windows' picture code wants an STA thread
                else
                {
                    var progress = new Progress<double>(p => item.Status = $"Converting… {(int)Math.Round(p * 100)}%");    // (reports on this UI thread)
                    output = await MediaConverter.ConvertAsync(item.Path, target, mediaOptions, outDir, progress, ct);
                }
                ok++;
                long size = new FileInfo(output).Length;
                item.Output = output;
                item.Status = $"✓ {Path.GetFileName(output)} · {Format.Bytes(size)}";
                item.StatusBrush = ConvItem.Find("OkBrush");
            }
            catch (OperationCanceledException)
            {
                cancelled++;
                item.Status = "Cancelled";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException
                                           or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
            {
                failed++;
                item.Status = "✗ " + ex.Message;
                item.StatusBrush = ConvItem.Find("ErrBrush");
            }
        }

        _running = false;
        _cts.Dispose();
        _cts = null;
        var first = work.FirstOrDefault(i => i.Output != null);
        _lastOutput = first?.Output;
        ShowBtn.Visibility = first != null ? Visibility.Visible : Visibility.Collapsed;

        var parts = new List<string>();
        if (failed > 0) parts.Add($"{failed} failed");
        if (skipped > 0) parts.Add($"{skipped} skipped");
        if (cancelled > 0) parts.Add($"{cancelled} cancelled");
        SetStatus(parts.Count == 0 ? $"Done: {ok} converted." : $"{ok} converted, {string.Join(", ", parts)}.", ok == 0 && failed > 0);
        UpdateState();
    }

    private void ShowFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutput != null && File.Exists(_lastOutput)) Process.Start("explorer.exe", $"/select,\"{_lastOutput}\"");
    }
}
