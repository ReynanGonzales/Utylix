using System;
using System.Collections.Generic;
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

/// <summary>
/// "Combine into one PDF" / "Convert to PDF": a list of PDFs and pictures, put in order, becomes one new PDF (the files themselves are
/// not changed). Opened from Explorer's right-click menu or from the PDF window. Everything runs on this PC.
/// </summary>
public sealed class PdfCombineWindow : Window
{
    private static readonly List<PdfCombineWindow> Open_ = new();
    public static int Count => Open_.Count;
    public static event Action? AnyClosed;

    private readonly List<string> _files = new();
    private readonly StackPanel _rows = new();
    private readonly TextBlock _empty = new() { Text = "Add PDFs or pictures with the button below, or drop them here.", Opacity = 0.6, Margin = new Thickness(4, 10, 4, 10), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _count = new() { Opacity = 0.75, Margin = new Thickness(0, 4, 0, 12) };
    private readonly ComboBox _paper = new() { Width = 230, Margin = new Thickness(10, 0, 0, 0) };
    private readonly StackPanel _paperRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
    private readonly ProgressBar _bar = new() { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), MinHeight = 20 };
    private readonly Button _go = new() { Content = "Combine", MinWidth = 120, Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button _add = new() { Content = "Add files…", MinWidth = 110, Margin = new Thickness(0, 0, 10, 0) };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private CancellationTokenSource? _cts;
    private string? _saved;

    /// <summary>Opens the window with these files (PDFs and pictures, in this order).</summary>
    public static void Show(IEnumerable<string> files)
    {
        var w = new PdfCombineWindow(files);
        w.Show();
        w.Activate();
    }

    private PdfCombineWindow(IEnumerable<string> files)
    {
        var start = files.Where(f => File.Exists(f) && (PdfCombiner.IsPdf(f) || PdfCombiner.IsPicture(f))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool pictures = start.Count > 0 && start.All(PdfCombiner.IsPicture);
        string heading = pictures ? "Convert to PDF" : "Combine into one PDF";
        Title = heading + " - Utylix Editor";
        Width = 600; Height = 600; MinWidth = 480; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13.5;
        AllowDrop = true;
        WindowTheme.DarkTitleBar(this);
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/pdf.ico")); } catch (Exception e) when (e is IOException or UriFormatException) { }

        var root = new DockPanel { Margin = new Thickness(24, 20, 24, 20) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = heading, FontSize = 20, FontWeight = FontWeights.SemiBold });
        top.Children.Add(_count);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var bottom = new StackPanel();
        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        _add.Style = (Style)Application.Current.FindResource("DialogButton");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_add, "CombineAdd");
        addRow.Children.Add(_add);
        addRow.Children.Add(new TextBlock { Text = "The pages go in this order. Use the arrows to move a file.", Opacity = 0.6, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        bottom.Children.Add(addRow);

        _paperRow.Children.Add(new TextBlock { Text = "Put pictures on", VerticalAlignment = VerticalAlignment.Center });
        foreach (var (label, paper) in new[] { ("Pages the shape of the picture", PdfPaper.Picture), ("A4", PdfPaper.A4), ("Letter (8.5 × 11 in)", PdfPaper.Letter), ("Long (8.5 × 13 in)", PdfPaper.Long) })
            _paper.Items.Add(new ComboBoxItem { Content = label, Tag = paper });
        _paper.SelectedIndex = 0;
        System.Windows.Automation.AutomationProperties.SetAutomationId(_paper, "CombinePaper");
        _paperRow.Children.Add(_paper);
        bottom.Children.Add(_paperRow);
        bottom.Children.Add(_bar);
        bottom.Children.Add(_status);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_status, "CombineStatus");

        _go.Style = (Style)Application.Current.FindResource("DialogPrimary");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_go, "CombineGo");
        var close = new Button { Content = "Close", MinWidth = 96, Style = (Style)Application.Current.FindResource("DialogButton") };
        _buttons.Margin = new Thickness(0, 16, 0, 0);
        _buttons.Children.Add(_go);
        _buttons.Children.Add(close);
        bottom.Children.Add(_buttons);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        var list = new Border
        {
            BorderBrush = (Brush)Application.Current.FindResource("LineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _rows, Padding = new Thickness(6) },
        };
        root.Children.Add(list);
        Content = root;

        _add.Click += (_, _) => AddFromDialog();
        _go.Click += async (_, _) => await GoAsync();
        close.Click += (_, _) => Close();
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped) AddFiles(dropped); };
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Closing += (_, _) => _cts?.Cancel();
        Closed += (_, _) => { Open_.Remove(this); AnyClosed?.Invoke(); };
        Open_.Add(this);

        _files.AddRange(start);
        Refresh();
    }

    private void AddFromDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add PDFs or pictures",
            Multiselect = true,
            Filter = "PDFs and pictures|*.pdf;" + string.Join(";", PdfCombiner.PictureExtensions.Select(e => "*." + e)) + "|All files|*.*",
            InitialDirectory = _files.Count > 0 ? Path.GetDirectoryName(_files[^1]) : null,
        };
        if (dlg.ShowDialog(this) == true) AddFiles(dlg.FileNames);
    }

    private void AddFiles(IEnumerable<string> files)
    {
        if (_cts != null) return;                               // not while it is being made
        int skipped = 0;
        foreach (var f in files)
        {
            if (!File.Exists(f) || !(PdfCombiner.IsPdf(f) || PdfCombiner.IsPicture(f))) { skipped++; continue; }
            if (!_files.Contains(f, StringComparer.OrdinalIgnoreCase)) _files.Add(f);
        }
        ResetSaved();
        Refresh();
        if (skipped > 0) _status.Text = skipped == 1 ? "One file was left out: only PDFs and pictures can be added." : $"{skipped} files were left out: only PDFs and pictures can be added.";
    }

    /// <summary>After a change, the button makes a new PDF again (instead of opening the last one).</summary>
    private void ResetSaved()
    {
        if (_saved == null) return;
        _saved = null;
        _go.Content = "Combine";
        if (_buttons.Children.Count > 2) _buttons.Children.RemoveAt(0);         // "Show in folder"
        _bar.Visibility = Visibility.Collapsed;
        _status.Text = "";
    }

    private void Refresh()
    {
        _rows.Children.Clear();
        if (_files.Count == 0) _rows.Children.Add(_empty);
        for (int i = 0; i < _files.Count; i++) _rows.Children.Add(Row(i));
        int pdfs = _files.Count(PdfCombiner.IsPdf), pics = _files.Count - pdfs;
        var parts = new List<string>();
        if (pdfs > 0) parts.Add(pdfs == 1 ? "1 PDF" : $"{pdfs} PDFs");
        if (pics > 0) parts.Add(pics == 1 ? "1 picture" : $"{pics} pictures");
        _count.Text = parts.Count == 0 ? "Nothing added yet" : string.Join(" and ", parts) + " → one PDF";
        _paperRow.Visibility = pics > 0 ? Visibility.Visible : Visibility.Collapsed;
        _go.IsEnabled = _files.Count > 0 && _cts == null;
    }

    private UIElement Row(int index)
    {
        string file = _files[index];
        bool pdf = PdfCombiner.IsPdf(file);
        long size = 0;
        try { size = new FileInfo(file).Length; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        var grid = new Grid { Margin = new Thickness(4, 3, 4, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var number = new TextBlock { Text = (index + 1).ToString(), Opacity = 0.55, Width = 26, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(number);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = Path.GetFileName(file), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = file });
        text.Children.Add(new TextBlock { Text = (pdf ? "PDF" : "Picture") + "   ·   " + PdfReduceWindow.Bytes(size) + "   ·   " + Path.GetDirectoryName(file), Opacity = 0.6, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
        Button Tool(string glyph, string tip, string id, bool enabled, Action act)
        {
            var b = new Button
            {
                Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12 },
                Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(4, 0, 0, 0), ToolTip = tip, IsEnabled = enabled,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, id + index);
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            b.Click += (_, _) => { if (_cts != null) return; act(); ResetSaved(); Refresh(); };
            tools.Children.Add(b);
            return b;
        }
        Tool("", "Move up", "CombineUp", index > 0, () => (_files[index - 1], _files[index]) = (_files[index], _files[index - 1]));
        Tool("", "Move down", "CombineDown", index < _files.Count - 1, () => (_files[index + 1], _files[index]) = (_files[index], _files[index + 1]));
        Tool("", "Take out of the list", "CombineRemove", true, () => _files.RemoveAt(index));
        Grid.SetColumn(tools, 2);
        grid.Children.Add(tools);

        return new Border
        {
            Child = grid, Padding = new Thickness(4), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.FindResource("CardBrush"),
        };
    }

    private async Task GoAsync()
    {
        if (_saved != null) { PdfWindow.Open(new[] { _saved }); Close(); return; }       // (after saving, the button opens the new PDF)
        if (_files.Count == 0) return;

        string first = _files[0];
        bool allPictures = _files.All(PdfCombiner.IsPicture);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the new PDF",
            Filter = "PDF|*.pdf",
            FileName = Path.GetFileNameWithoutExtension(first) + (allPictures ? "" : " (combined)") + ".pdf",
            InitialDirectory = Path.GetDirectoryName(first),
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        string target = dlg.FileName;
        if (_files.Any(f => string.Equals(Path.GetFullPath(f), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)))
        {
            _status.Text = "Choose another name: that file is one of the files being combined.";
            return;
        }

        _cts = new CancellationTokenSource();
        var files = _files.ToList();
        var paper = _paper.SelectedItem is ComboBoxItem { Tag: PdfPaper p } ? p : PdfPaper.Picture;
        _go.IsEnabled = false; _add.IsEnabled = false; _paper.IsEnabled = false;
        _bar.Visibility = Visibility.Visible; _bar.Value = 0;
        var progress = new Progress<(int Done, int Total)>(x =>
        {
            _bar.Value = x.Total == 0 ? 0 : (double)x.Done / x.Total;
            _status.Text = x.Done < x.Total ? $"Adding {Path.GetFileName(files[x.Done])} ({x.Done + 1} of {x.Total})…" : "Saving…";
        });
        try
        {
            byte[] pdf = await Task.Run(() => PdfCombiner.Combine(files, paper, progress, _cts.Token));
            await Task.Run(() =>
            {
                string temp = target + ".utylix-tmp";
                File.WriteAllBytes(temp, pdf);
                File.Move(temp, target, overwrite: true);
            });
            _saved = target;
            _bar.Value = 1;
            int pages = 0;
            try { using var made = PdfFile.Open(target); pages = made.PageCount; } catch (Exception e) when (e is IOException or PdfPasswordException) { }
            _status.Text = $"Done: {Path.GetFileName(target)}" + (pages > 0 ? $", {pages} page{(pages == 1 ? "" : "s")}" : "") + $", {PdfReduceWindow.Bytes(pdf.Length)}."
                + (pdf.Length > 10_000_000 ? "\nToo big to send? Open it and use Reduce file size." : "");
            _go.Content = "Open it";
            var folder = new Button { Content = "Show in folder", MinWidth = 120, Margin = new Thickness(0, 0, 10, 0), Style = (Style)Application.Current.FindResource("DialogButton") };
            System.Windows.Automation.AutomationProperties.SetAutomationId(folder, "CombineFolder");
            folder.Click += (_, _) => { try { Process.Start("explorer.exe", $"/select,\"{target}\""); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { } };
            _buttons.Children.Insert(0, folder);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PdfPasswordException or PdfProtectedException or OutOfMemoryException or DllNotFoundException)
        {
            _status.Text = "Couldn't make the PDF: " + e.Message;
            _bar.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _cts = null;
            _add.IsEnabled = true; _paper.IsEnabled = true;
            Refresh();
        }
    }
}
