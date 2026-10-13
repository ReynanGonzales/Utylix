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

/// <summary>The "Export" window: picture size, quality, where to put the file, a progress bar and Cancel.</summary>
public sealed class VidExportWindow : Window
{
    private readonly VidProject _project;
    private readonly TextBox _file = new() { Padding = new Thickness(8, 5, 8, 5), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _bar = new() { Height = 8, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 14, 0, 4), Visibility = Visibility.Collapsed };
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _go, _cancel, _show, _play;
    private readonly CheckBox _gpu = new() { Content = "Use the video card when there is one (much faster)", IsChecked = VidSettings.Current.UseGpu, Margin = new Thickness(0, 4, 0, 0) };
    private readonly List<(RadioButton Chip, int Height)> _sizes = new();
    private readonly List<(RadioButton Chip, int Level)> _quality = new();
    private CancellationTokenSource? _cts;
    private bool _running;

    public VidExportWindow(VidProject project, string suggestedName)
    {
        _project = project;
        Title = "Export - Utylix Vid Editor";
        Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13.5;
        WindowTheme.DarkTitleBar(this);

        var chip = (Style)FindResource("ChipButton");
        var plain = (Style)FindResource("DialogButton");
        var primary = (Style)FindResource("DialogPrimary");
        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        stack.Children.Add(new TextBlock { Text = "Export the video", FontSize = 18, FontWeight = FontWeights.SemiBold });
        var sub = new TextBlock { Text = $"{VidTimeline.TimeText(project.Duration)} long  ·  {project.Width} × {project.Height}  ·  {project.Fps:0.##} fps", Margin = new Thickness(0, 4, 0, 14) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        stack.Children.Add(sub);

        stack.Children.Add(new TextBlock { Text = "Picture size", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        var sizes = new WrapPanel();
        double aspect = (double)project.Width / Math.Max(1, project.Height);
        bool portrait = project.Height > project.Width;
        int shortSide = Math.Min(project.Width, project.Height);
        foreach (int h in new[] { 0, 1080, 720, 480 })
        {
            if (h > 0 && h >= shortSide) continue;                                 // (no point making a 720p project 1080p; "same as the project" is that size)
            string label;
            if (h == 0) label = $"Same as the project ({project.Width}×{project.Height})";
            else
            {
                int side = portrait ? (int)Math.Round(h * aspect) / 2 * 2 : h;     // "1080p" means the short side
                int w = portrait ? side : (int)Math.Round(h * aspect) / 2 * 2;
                int hh = portrait ? h : h;
                label = portrait ? $"{h}p  ({w}×{(int)Math.Round(w / aspect) / 2 * 2})" : $"{h}p  ({w}×{hh})";
            }
            var rb = new RadioButton { Content = label, Style = chip, GroupName = "size", IsChecked = h == 0 };
            _sizes.Add((rb, h));
            sizes.Children.Add(rb);
        }
        stack.Children.Add(sizes);

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
            var dlg = new SaveFileDialog { Title = "Save the video as", Filter = "MP4 video (*.mp4)|*.mp4", DefaultExt = ".mp4", FileName = Path.GetFileName(_file.Text), InitialDirectory = Path.GetDirectoryName(_file.Text) };
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
    }

    private static string Sanitize(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? "My video" : s;
    }

    private async System.Threading.Tasks.Task RunAsync()
    {
        string path = _file.Text.Trim();
        if (path.Length == 0 || !Path.IsPathRooted(path)) { _state.Text = "Choose where to save the video."; return; }
        if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) path += ".mp4";
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
        var opts = new VidExportOptions
        {
            Path = path, Width = w, Height = h, Quality = _quality.First(q => q.Chip.IsChecked == true).Level, Gpu = _gpu.IsChecked == true,
        };
        _running = true;
        _go.IsEnabled = false;
        _cancel.Content = "Cancel";
        foreach (var s in _sizes) s.Chip.IsEnabled = false;
        foreach (var q in _quality) q.Chip.IsEnabled = false;
        _gpu.IsEnabled = false; _file.IsEnabled = false;
        _bar.Visibility = Visibility.Visible; _bar.Value = 0;
        _show.Visibility = _play.Visibility = Visibility.Collapsed;
        _state.Text = "Making the video…";
        _cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        var progress = new Progress<double>(p =>
        {
            _bar.Value = p * 100;
            string eta = "";
            if (p > 0.03) { var left = TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds * (1 - p) / p); eta = "  ·  about " + (left.TotalMinutes >= 1 ? $"{(int)left.TotalMinutes} min {left.Seconds} s" : $"{left.Seconds} s") + " left"; }
            _state.Text = $"Making the video…  {p * 100:0} %{eta}";
        });
        string? error;
        try { error = await VidExport.RunAsync(_project, opts, progress, _cts.Token); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { error = e.Message; }
        _running = false;
        _cancel.Content = "Close";
        _go.IsEnabled = true;
        foreach (var s in _sizes) s.Chip.IsEnabled = true;
        foreach (var q in _quality) q.Chip.IsEnabled = true;
        _gpu.IsEnabled = true; _file.IsEnabled = true;
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
}
