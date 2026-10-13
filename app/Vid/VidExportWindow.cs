using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace IdmClone.Vid;

/// <summary>The "Export" window: format, range, picture size, quality, where to put the file, a progress bar and Cancel.</summary>
public sealed class VidExportWindow : Window
{
    private readonly VidProject _project;
    private readonly (double From, double To)? _work;
    private readonly TextBox _file = new() { Padding = new Thickness(8, 5, 8, 5), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _bar = new() { Height = 8, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 14, 0, 4), Visibility = Visibility.Collapsed };
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _go, _cancel, _show, _play;
    private readonly CheckBox _gpu = new() { Content = "Use the video card when there is one (much faster)", IsChecked = VidSettings.Current.UseGpu, Margin = new Thickness(0, 4, 0, 0) };
    private readonly List<(RadioButton Chip, int Height)> _sizes = new();
    private readonly List<(RadioButton Chip, int Level)> _quality = new();
    private readonly List<(RadioButton Chip, VidFormat Format)> _formats = new();
    private readonly List<(RadioButton Chip, bool Work)> _ranges = new();
    private readonly WrapPanel _sizePanel = new();
    private CancellationTokenSource? _cts;
    private bool _running;

    public VidExportWindow(VidProject project, string suggestedName, (double From, double To)? work)
    {
        _project = project;
        _work = work;
        Title = "Export - Utylix Vid Editor";
        Width = 600; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13.5;
        WindowTheme.DarkTitleBar(this);

        var chip = (Style)FindResource("ChipButton");
        var plain = (Style)FindResource("DialogButton");
        var primary = (Style)FindResource("DialogPrimary");
        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        stack.Children.Add(new TextBlock { Text = "Export", FontSize = 18, FontWeight = FontWeights.SemiBold });
        var sub = new TextBlock { Text = $"{VidTimeline.TimeText(project.Duration)} long  ·  {project.Width} × {project.Height}  ·  {project.Fps:0.##} fps", Margin = new Thickness(0, 4, 0, 14) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        stack.Children.Add(sub);

        // format
        stack.Children.Add(new TextBlock { Text = "Format", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        var formats = new WrapPanel();
        foreach (var (f, label) in new[]
        {
            (VidFormat.Mp4H264, "MP4 · H.264"), (VidFormat.Mp4H265, "MP4 · H.265 (smaller)"), (VidFormat.WebmVp9, "WebM · VP9"),
            (VidFormat.Gif, "GIF (no sound)"), (VidFormat.Mp3, "MP3 (sound only)"), (VidFormat.Wav, "WAV (sound only)"),
        })
        {
            var rb = new RadioButton { Content = label, Style = chip, GroupName = "format", IsChecked = f == VidFormat.Mp4H264 };
            System.Windows.Automation.AutomationProperties.SetAutomationId(rb, "VidFmt" + f);
            rb.Checked += (_, _) => FormatChanged();
            _formats.Add((rb, f));
            formats.Children.Add(rb);
        }
        stack.Children.Add(formats);

        // range
        stack.Children.Add(new TextBlock { Text = "Range", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 6) });
        var ranges = new WrapPanel();
        var whole = new RadioButton { Content = "The whole sequence", Style = chip, GroupName = "range", IsChecked = _work == null };
        _ranges.Add((whole, false));
        ranges.Children.Add(whole);
        if (_work != null)
        {
            var wa = new RadioButton { Content = $"In to Out  ({VidTimeline.TimeText(_work.Value.From)} – {VidTimeline.TimeText(_work.Value.To)})", Style = chip, GroupName = "range", IsChecked = true };
            System.Windows.Automation.AutomationProperties.SetAutomationId(wa, "VidRangeWork");
            _ranges.Add((wa, true));
            ranges.Children.Add(wa);
        }
        stack.Children.Add(ranges);

        // picture size
        stack.Children.Add(new TextBlock { Text = "Picture size", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 6) });
        double aspect = (double)project.Width / Math.Max(1, project.Height);
        bool portrait = project.Height > project.Width;
        int shortSide = Math.Min(project.Width, project.Height);
        foreach (int h in new[] { 0, 2160, 1080, 720, 480 })
        {
            if (h > 0 && h >= shortSide) continue;                                 // (no point making a 720p project 1080p; "same as the project" is that size)
            string label;
            if (h == 0) label = $"Same as the sequence ({project.Width}×{project.Height})";
            else
            {
                int w = portrait ? h / 2 * 2 : (int)Math.Round(h * aspect) / 2 * 2;
                int hh = portrait ? (int)Math.Round(h / aspect) / 2 * 2 : h;
                label = $"{h}p  ({w}×{hh})";
            }
            var rb = new RadioButton { Content = label, Style = chip, GroupName = "size", IsChecked = h == 0 };
            _sizes.Add((rb, h));
            _sizePanel.Children.Add(rb);
        }
        stack.Children.Add(_sizePanel);

        stack.Children.Add(new TextBlock { Text = "Quality", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 6) });
        var quality = new WrapPanel();
        foreach (var (label, level) in new[] { ("High (big file)", 0), ("Balanced", 1), ("Small file", 2) })
        {
            var rb = new RadioButton { Content = label, Style = chip, GroupName = "quality", IsChecked = level == VidSettings.Current.Quality };
            _quality.Add((rb, level));
            quality.Children.Add(rb);
        }
        stack.Children.Add(quality);
        stack.Children.Add(_gpu);

        stack.Children.Add(new TextBlock { Text = "Save as", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) });
        string folder = VidSettings.Current.ExportFolder;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (!Directory.Exists(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _file.Text = Path.Combine(folder, Sanitize(suggestedName) + ".mp4");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_file, "VidExportFile");
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var choose = new Button { Content = "Choose…", Style = plain, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 5, 12, 5) };
        choose.Click += (_, _) =>
        {
            string ext = VidExportOptions.Extension(CurrentFormat);
            var dlg = new SaveFileDialog { Title = "Save as", Filter = $"{ext.TrimStart('.').ToUpperInvariant()} file (*{ext})|*{ext}", DefaultExt = ext, FileName = Path.GetFileName(_file.Text), InitialDirectory = Path.GetDirectoryName(_file.Text) };
            if (dlg.ShowDialog(this) == true) _file.Text = dlg.FileName;
        };
        Grid.SetColumn(choose, 1);
        row.Children.Add(_file); row.Children.Add(choose);
        stack.Children.Add(row);

        stack.Children.Add(_bar);
        stack.Children.Add(_state);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        _show = new Button { Content = "Show in folder", Style = plain, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6) };
        _play = new Button { Content = "Play it", Style = plain, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6) };
        _cancel = new Button { Content = "Close", Style = plain, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(16, 6, 16, 6), IsCancel = true };
        _go = new Button { Content = "Export", Style = primary, Padding = new Thickness(18, 6, 18, 6), IsDefault = true };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_go, "VidExportGo");
        buttons.Children.Add(_show); buttons.Children.Add(_play); buttons.Children.Add(_cancel); buttons.Children.Add(_go);
        stack.Children.Add(buttons);
        Content = stack;

        _go.Click += async (_, _) => await RunAsync();
        _cancel.Click += (_, _) => { if (_running) _cts?.Cancel(); else Close(); };
        _show.Click += (_, _) => { try { Process.Start("explorer.exe", "/select,\"" + _file.Text + "\""); } catch (System.ComponentModel.Win32Exception) { } };
        _play.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(_file.Text) { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { } };
        Closing += (_, e) => { if (_running) { e.Cancel = true; _cts?.Cancel(); } };
        FormatChanged();
    }

    private VidFormat CurrentFormat => _formats.First(f => f.Chip.IsChecked == true).Format;

    /// <summary>A sound-only format has no picture size; the file name gets the extension that goes with the format.</summary>
    private void FormatChanged()
    {
        var f = CurrentFormat;
        _sizePanel.IsEnabled = VidExportOptions.HasPicture(f);
        _gpu.IsEnabled = f is VidFormat.Mp4H264 or VidFormat.Mp4H265;
        if (_file != null && _file.Text.Length > 0)
            _file.Text = Path.ChangeExtension(_file.Text, VidExportOptions.Extension(f));
    }

    private static string Sanitize(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? "My video" : s;
    }

    private async System.Threading.Tasks.Task RunAsync()
    {
        string path = _file.Text.Trim();
        var format = CurrentFormat;
        string ext = VidExportOptions.Extension(format);
        if (path.Length == 0 || !Path.IsPathRooted(path)) { _state.Text = "Choose where to save the file."; return; }
        if (!path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) path = Path.ChangeExtension(path, ext);
        _file.Text = path;
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _state.Text = "That folder can't be used: " + e.Message; return; }
        if (File.Exists(path))
        {
            var r = UMessage.Ask(this, Path.GetFileName(path) + " already exists. Replace it?", "Export", MessageBoxImage.Question, MessageBoxResult.No, MessageBoxResult.No,
                ("Replace", MessageBoxResult.Yes), ("Choose another name", MessageBoxResult.No));
            if (r != MessageBoxResult.Yes) return;
        }
        int height = _sizes.First(s => s.Chip.IsChecked == true).Height;
        double aspect = (double)_project.Width / Math.Max(1, _project.Height);
        int w = _project.Width, h = _project.Height;
        if (height > 0)
        {
            if (_project.Height > _project.Width) { w = height; h = (int)Math.Round(w / aspect) / 2 * 2; w = w / 2 * 2; }
            else { h = height; w = (int)Math.Round(h * aspect) / 2 * 2; }
        }
        if (format == VidFormat.Gif && w > 720) { h = (int)Math.Round(720.0 * h / w) / 2 * 2; w = 720; }      // a GIF of full HD would be huge
        bool useWork = _ranges.First(r => r.Chip.IsChecked == true).Work && _work != null;
        var opts = new VidExportOptions
        {
            Path = path, Width = w, Height = h, Quality = _quality.First(q => q.Chip.IsChecked == true).Level, Gpu = _gpu.IsChecked == true, Format = format,
            From = useWork ? _work!.Value.From : 0, To = useWork ? _work!.Value.To : 0,
        };
        _running = true;
        _go.IsEnabled = false;
        _cancel.Content = "Cancel";
        SetEnabled(false);
        _bar.Visibility = Visibility.Visible; _bar.Value = 0;
        _show.Visibility = _play.Visibility = Visibility.Collapsed;
        _state.Text = "Making the file…";
        _cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        var progress = new Progress<double>(p =>
        {
            _bar.Value = p * 100;
            string eta = "";
            if (p > 0.03) { var left = TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds * (1 - p) / p); eta = "  ·  about " + (left.TotalMinutes >= 1 ? $"{(int)left.TotalMinutes} min {left.Seconds} s" : $"{left.Seconds} s") + " left"; }
            _state.Text = $"Making the file…  {p * 100:0} %{eta}";
        });
        string? error;
        try { error = await VidExport.RunAsync(_project, opts, progress, _cts.Token); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { error = e.Message; }
        _running = false;
        _cancel.Content = "Close";
        _go.IsEnabled = true;
        SetEnabled(true);
        FormatChanged();
        if (error == null)
        {
            // what was chosen here is what the next export starts with
            var s = VidSettings.Current;
            s.Quality = opts.Quality; s.UseGpu = opts.Gpu; s.ExportFolder = Path.GetDirectoryName(path) ?? "";
            s.Save();
            long size = new FileInfo(path).Length;
            _state.Text = $"Done in {sw.Elapsed.TotalSeconds:0} s  ·  {size / 1048576.0:0.#} MB  ·  {path}";
            _bar.Value = 100;
            _show.Visibility = _play.Visibility = Visibility.Visible;
        }
        else if (error == "Cancelled") { _state.Text = "Cancelled. Nothing was written."; _bar.Visibility = Visibility.Collapsed; }
        else { _state.Text = error; _bar.Visibility = Visibility.Collapsed; }
        _cts.Dispose(); _cts = null;
    }

    private void SetEnabled(bool on)
    {
        foreach (var f in _formats) f.Chip.IsEnabled = on;
        foreach (var r in _ranges) r.Chip.IsEnabled = on;
        foreach (var s in _sizes) s.Chip.IsEnabled = on;
        foreach (var q in _quality) q.Chip.IsEnabled = on;
        _gpu.IsEnabled = on; _file.IsEnabled = on;
    }
}
