using System;
using System.Collections.Generic;
using System.Globalization;
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
/// Two small dialogs of the editor's page tools: "Split into several PDFs" (every N pages, by ranges, or one per page) and "Save pages as
/// pictures" (PNG / JPG at a chosen resolution). They read the open document (with the changes made to its pages so far) and write new files
/// into a folder; the open PDF itself is not changed.
/// </summary>
public sealed class PdfPagesDialog : Window
{
    private readonly PdfFile _pdf;
    private readonly string _path;
    private readonly bool _split;
    private readonly IReadOnlyList<int> _selected;
    private readonly TextBox _folder = new() { Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center, MinWidth = 330 };
    private readonly ProgressBar _bar = new() { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), MinHeight = 20 };
    private readonly Button _go = new() { MinWidth = 120, Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button _close = new() { Content = "Close", MinWidth = 96 };
    private CancellationTokenSource? _cts;
    private string? _firstFolder;

    // split
    private RadioButton _every = null!, _ranges = null!, _each = null!;
    private readonly TextBox _everyN = new() { Width = 56, Text = "2", Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    private readonly TextBox _rangeText = new() { Width = 220, Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    // pictures
    private RadioButton _png = null!, _jpg = null!, _chosen = null!, _all = null!;
    private readonly List<(RadioButton Chip, int Dpi)> _dpis = new();

    public PdfPagesDialog(Window owner, PdfFile pdf, string path, IReadOnlyList<int> selected, bool split)
    {
        _pdf = pdf; _path = path; _split = split; _selected = selected;
        Owner = owner;
        Title = (split ? "Split into several PDFs" : "Save pages as pictures") + " - Utylix Editor";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13.5;
        WindowTheme.DarkTitleBar(this);
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/pdf.ico")); } catch (Exception e) when (e is IOException or UriFormatException) { }
        var chipStyle = (Style)Application.Current.FindResource("ChipButton");

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock { Text = split ? "Split into several PDFs" : "Save pages as pictures", FontSize = 20, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text = $"{Path.GetFileName(path)}   ·   {pdf.PageCount} page{(pdf.PageCount == 1 ? "" : "s")}", Opacity = 0.75, Margin = new Thickness(0, 4, 0, 14) });

        if (split)
        {
            _every = new RadioButton { Content = "Every", Style = chipStyle, GroupName = "how", IsChecked = true };
            _ranges = new RadioButton { Content = "These pages", Style = chipStyle, GroupName = "how" };
            _each = new RadioButton { Content = "One PDF for each page", Style = chipStyle, GroupName = "how", HorizontalAlignment = HorizontalAlignment.Left };
            System.Windows.Automation.AutomationProperties.SetAutomationId(_every, "PdfSplitEvery");
            System.Windows.Automation.AutomationProperties.SetAutomationId(_ranges, "PdfSplitRanges");
            System.Windows.Automation.AutomationProperties.SetAutomationId(_each, "PdfSplitEach");
            System.Windows.Automation.AutomationProperties.SetAutomationId(_everyN, "PdfSplitN");
            System.Windows.Automation.AutomationProperties.SetAutomationId(_rangeText, "PdfSplitRangeText");
            var rowA = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            rowA.Children.Add(_every); rowA.Children.Add(_everyN); rowA.Children.Add(new TextBlock { Text = "pages", VerticalAlignment = VerticalAlignment.Center });
            var rowB = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            rowB.Children.Add(_ranges); rowB.Children.Add(_rangeText);
            rowB.Children.Add(new TextBlock { Text = "like 1-3, 4-6, 9", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6, Margin = new Thickness(10, 0, 0, 0), FontSize = 12 });
            _everyN.GotKeyboardFocus += (_, _) => { _every.IsChecked = true; _everyN.SelectAll(); };
            _rangeText.GotKeyboardFocus += (_, _) => _ranges.IsChecked = true;
            if (selected.Count > 0) _rangeText.Text = selected.Count == 1 ? $"{selected[0] + 1}" : PdfPageTools.Label(selected);
            root.Children.Add(rowA); root.Children.Add(rowB); root.Children.Add(_each);
            root.Children.Add(new TextBlock { Text = "Each piece is a new PDF named like \"name (pages 1-3).pdf\". Your open PDF is not changed.", Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
            _go.Content = "Split";
        }
        else
        {
            root.Children.Add(new TextBlock { Text = "Kind", FontWeight = FontWeights.SemiBold });
            var kind = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 10) };
            _png = new RadioButton { Content = "PNG (sharp, bigger)", Style = chipStyle, GroupName = "kind", IsChecked = true };
            _jpg = new RadioButton { Content = "JPG (smaller)", Style = chipStyle, GroupName = "kind" };
            System.Windows.Automation.AutomationProperties.SetAutomationId(_png, "PdfPicPng");
            System.Windows.Automation.AutomationProperties.SetAutomationId(_jpg, "PdfPicJpg");
            kind.Children.Add(_png); kind.Children.Add(_jpg);
            root.Children.Add(kind);
            root.Children.Add(new TextBlock { Text = "Sharpness (dots per inch)", FontWeight = FontWeights.SemiBold });
            var dpi = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 10) };
            foreach (var (label, value) in new[] { ("72 · small", 72), ("150 · good", 150), ("300 · print", 300), ("600 · very big", 600) })
            {
                var chip = new RadioButton { Content = label, Style = chipStyle, GroupName = "dpi", IsChecked = value == 150 };
                System.Windows.Automation.AutomationProperties.SetAutomationId(chip, "PdfPicDpi" + value);
                _dpis.Add((chip, value)); dpi.Children.Add(chip);
            }
            root.Children.Add(dpi);
            root.Children.Add(new TextBlock { Text = "Pages", FontWeight = FontWeights.SemiBold });
            var which = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            _chosen = new RadioButton { Content = selected.Count == 1 ? "The chosen page" : $"The {selected.Count} chosen pages", Style = chipStyle, GroupName = "which", IsChecked = selected.Count > 0 && selected.Count < pdf.PageCount };
            _all = new RadioButton { Content = $"All {pdf.PageCount} page{(pdf.PageCount == 1 ? "" : "s")}", Style = chipStyle, GroupName = "which", IsChecked = !(selected.Count > 0 && selected.Count < pdf.PageCount) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(_chosen, "PdfPicChosen");
            System.Windows.Automation.AutomationProperties.SetAutomationId(_all, "PdfPicAll");
            if (selected.Count == 0 || selected.Count >= pdf.PageCount) _chosen.IsEnabled = false;
            which.Children.Add(_chosen); which.Children.Add(_all);
            root.Children.Add(which);
            _go.Content = "Save pictures";
        }

        root.Children.Add(new TextBlock { Text = "Save into this folder", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) });
        _folder.Text = Path.GetDirectoryName(path) ?? "";
        System.Windows.Automation.AutomationProperties.SetAutomationId(_folder, "PdfPagesFolder");
        var choose = new Button { Content = "Choose…", Style = (Style)Application.Current.FindResource("DialogButton"), Margin = new Thickness(8, 0, 0, 0), MinWidth = 90 };
        choose.Click += (_, _) =>
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = _folder.Text, Description = "Choose the folder for the new files", UseDescriptionForTitle = true };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) _folder.Text = dlg.SelectedPath;
        };
        var folderRow = new DockPanel();
        DockPanel.SetDock(choose, Dock.Right);
        folderRow.Children.Add(choose); folderRow.Children.Add(_folder);
        root.Children.Add(folderRow);
        root.Children.Add(_bar);
        root.Children.Add(_status);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_status, "PdfPagesStatus");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        _go.Style = (Style)Application.Current.FindResource("DialogPrimary");
        _close.Style = (Style)Application.Current.FindResource("DialogButton");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_go, "PdfPagesGo");
        buttons.Children.Add(_go); buttons.Children.Add(_close);
        root.Children.Add(buttons);
        Content = root;

        _go.Click += async (_, _) => await GoAsync();
        _close.Click += (_, _) => Close();
        Closing += (_, _) => _cts?.Cancel();
    }

    private async Task GoAsync()
    {
        if (_firstFolder != null) { try { System.Diagnostics.Process.Start("explorer.exe", _firstFolder); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { } Close(); return; }
        string folder = _folder.Text.Trim();
        if (folder.Length == 0) { _status.Text = "Choose a folder."; return; }
        List<List<int>>? pieces = null;
        List<int>? pages = null;
        try
        {
            if (_split)
            {
                if (_every.IsChecked == true)
                {
                    if (!int.TryParse(_everyN.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 1) throw new FormatException("Type how many pages go in each piece (1 or more).");
                    pieces = PdfPageTools.Every(n, _pdf.PageCount);
                }
                else if (_ranges.IsChecked == true) pieces = PdfPageTools.ParseRanges(_rangeText.Text, _pdf.PageCount);
                else pieces = PdfPageTools.Every(1, _pdf.PageCount);
                if (pieces.Count < 2 && pieces[0].Count == _pdf.PageCount) throw new FormatException("That is the whole PDF: nothing to split.");
            }
            else pages = _all.IsChecked == true ? Enumerable.Range(0, _pdf.PageCount).ToList() : _selected.ToList();
        }
        catch (FormatException e) { _status.Text = e.Message; return; }

        _go.IsEnabled = false; _bar.Visibility = Visibility.Visible; _bar.Value = 0; _status.Text = "Working…";
        var cts = _cts = new CancellationTokenSource();
        var progress = new Progress<(int Done, int Total)>(p => _bar.Value = p.Total == 0 ? 0 : (double)p.Done / p.Total);
        string stem = Path.GetFileNameWithoutExtension(_path);
        bool png = _png?.IsChecked == true;
        int dpi = _dpis.FirstOrDefault(d => d.Chip.IsChecked == true).Dpi;
        try
        {
            var made = await Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                if (!_split) return PdfPageTools.SavePictures(_pdf, pages!, dpi == 0 ? 150 : dpi, png, folder, stem, progress, cts.Token);
                var files = new List<string>();
                for (int i = 0; i < pieces!.Count; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    ((IProgress<(int, int)>)progress).Report((i, pieces.Count));
                    byte[] bytes = PdfPageTools.Extract(_pdf, pieces[i]);
                    string target = Free(Path.Combine(folder, PdfPageTools.Safe($"{stem} ({(pieces[i].Count == 1 ? "page" : "pages")} {PdfPageTools.Label(pieces[i])})") + ".pdf"));
                    File.WriteAllBytes(target, bytes);
                    files.Add(target);
                }
                ((IProgress<(int, int)>)progress).Report((pieces.Count, pieces.Count));
                return files;
            }, cts.Token);
            _bar.Value = 1;
            _firstFolder = folder;
            _status.Text = _split ? $"Done: {made.Count} PDFs made." : $"Done: {made.Count} picture{(made.Count == 1 ? "" : "s")} saved.";
            _go.Content = "Show the folder"; _go.IsEnabled = true;
        }
        catch (OperationCanceledException) { _status.Text = "Stopped."; _go.IsEnabled = true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PdfProtectedException or OutOfMemoryException or ObjectDisposedException or ArgumentException or NotSupportedException)
        {
            _status.Text = "Couldn't finish: " + e.Message; _go.IsEnabled = true;
        }
        finally { _cts = null; }
    }

    /// <summary>"name.pdf" or, when it exists, "name (2).pdf".</summary>
    private static string Free(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++) { string p = Path.Combine(dir, $"{stem} ({i}){ext}"); if (!File.Exists(p)) return p; }
    }
}
