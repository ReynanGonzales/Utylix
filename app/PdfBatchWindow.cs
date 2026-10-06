using System;
using System.Collections.Generic;
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
/// "Do one job to many PDFs" (Tools menu of the editor): a list of PDFs (add files, a folder, or drop them), one job (make smaller, page numbers, watermark, add / remove a password, page size,
/// save as Word) and where the new files go (next to each original, or one folder). The originals are never changed; a file that can't be done is marked and the others carry on.
/// </summary>
internal sealed class PdfBatchWindow : Window
{
    private sealed class Row
    {
        public string Path = "";
        public ListBoxItem Item = null!;
        public TextBlock Status = null!;
    }

    private readonly ListBox _list = new() { Height = 190, AllowDrop = true, Padding = new Thickness(2), BorderThickness = new Thickness(1), SelectionMode = SelectionMode.Extended };
    private readonly List<Row> _rows = new();
    private readonly List<(RadioButton Chip, PdfBatchKind Kind, StackPanel Options)> _kinds = new();
    private readonly TextBox _numberText = new() { Text = "Page {n} of {total}", Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBox _numberSize = new() { Text = "10", Width = 60, Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _markText = new() { Text = "CONFIDENTIAL", Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _slanted = new() { Content = "Slanted across the page", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
    private readonly PasswordBox _addPassword = new() { Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 6, 0, 0) };
    private readonly PasswordBox _removePassword = new() { Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _keepDirection = new() { Content = "Keep each page's direction (wide pages stay wide)", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
    private readonly List<(RadioButton Chip, PdfReduceLevel Level)> _levels = new();
    private readonly List<(RadioButton Chip, PdfSpot Spot)> _spots = new();
    private readonly List<(RadioButton Chip, PaperSize Paper)> _papers = new();
    private readonly RadioButton _nextTo = new() { Content = "Next to each original (the name says what was done)", GroupName = "batchout", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
    private readonly RadioButton _inFolder = new() { Content = "In one folder:", GroupName = "batchout", Margin = new Thickness(0, 6, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _folderText = new() { Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 };
    private string? _folder;
    private readonly ProgressBar _bar = new() { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), MinHeight = 20 };
    private readonly Button _go = new() { Content = "Start", MinWidth = 120, Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button _close = new() { Content = "Close", MinWidth = 96 };
    private CancellationTokenSource? _cts;

    public static void ShowFor(Window owner, string? firstFile)
    {
        var w = new PdfBatchWindow(firstFile) { Owner = owner };
        w.Show();
    }

    private PdfBatchWindow(string? firstFile)
    {
        Title = "Many PDFs at once - Utylix Editor";
        Width = 660;
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
        foreach (var box in new[] { _addPassword, _removePassword })                         // (the stock password box is white in the dark theme)
        {
            box.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            box.SetResourceReference(Control.BorderBrushProperty, "MutedBrush");
            box.Background = new SolidColorBrush(Color.FromArgb(34, 128, 128, 128));
        }

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock { Text = "Many PDFs at once", FontSize = 20, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text = "Choose the PDFs, choose one job, and every file gets it. The originals are never changed.", Opacity = 0.75, Margin = new Thickness(0, 4, 0, 12), TextWrapping = TextWrapping.Wrap });

        // the files
        _list.ItemContainerStyle = (Style)System.Windows.Markup.XamlReader.Parse(
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListBoxItem'><Setter Property='Foreground' Value='{DynamicResource TextBrush}' /><Setter Property='Template'><Setter.Value>" +
            "<ControlTemplate TargetType='ListBoxItem'><Border x:Name='bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' CornerRadius='6' Margin='2,1' Padding='6,3' Background='Transparent'><ContentPresenter /></Border>" +
            "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#22808080' /></Trigger>" +
            "<Trigger Property='IsSelected' Value='True'><Setter TargetName='bd' Property='Background' Value='#55808080' /></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>");
        _list.Background = Brushes.Transparent;
        _list.Foreground = Foreground;
        _list.SetResourceReference(Control.BorderBrushProperty, "MutedBrush");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_list, "PdfBatchFiles");
        _list.Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped) AddPaths(dropped); };
        _list.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Delete) RemoveChosen(); };
        root.Children.Add(_list);
        var fileButtons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        Button Small(string text, string id, Action click)
        {
            var b = new Button { Content = text, Style = (Style)Application.Current.FindResource("DialogButton"), Margin = new Thickness(0, 0, 8, 4) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, id);
            b.Click += (_, _) => click();
            fileButtons.Children.Add(b);
            return b;
        }
        Small("Add PDFs…", "PdfBatchAddFiles", AddFiles);
        Small("Add a folder…", "PdfBatchAddFolder", AddFolder);
        Small("Remove the chosen", "PdfBatchRemove", RemoveChosen);
        Small("Clear the list", "PdfBatchClear", () => { _rows.Clear(); _list.Items.Clear(); UpdateStart(); });
        root.Children.Add(fileButtons);

        // the job
        root.Children.Add(new TextBlock { Text = "What should be done to every file", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        var chips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var optionHost = new StackPanel();

        StackPanel Options() { var s = new StackPanel { Visibility = Visibility.Collapsed }; optionHost.Children.Add(s); return s; }
        void Kind(string label, string id, PdfBatchKind kind, StackPanel options)
        {
            var chip = new RadioButton { Content = label, Style = chipStyle, GroupName = "batchkind" };
            System.Windows.Automation.AutomationProperties.SetAutomationId(chip, id);
            chip.Checked += (_, _) => { foreach (var k in _kinds) k.Options.Visibility = k.Chip == chip ? Visibility.Visible : Visibility.Collapsed; UpdateStart(); };
            _kinds.Add((chip, kind, options)); chips.Children.Add(chip);
        }
        RadioButton Choice<T>(List<(RadioButton, T)> into, string label, string id, T value, WrapPanel host, string group)
        {
            var chip = new RadioButton { Content = label, Style = chipStyle, GroupName = group };
            System.Windows.Automation.AutomationProperties.SetAutomationId(chip, id);
            into.Add((chip, value)); host.Children.Add(chip);
            return chip;
        }

        var smaller = Options();
        smaller.Children.Add(new TextBlock { Text = "Pictures are made lighter; text, links and fields stay as they are. A file that doesn't get smaller is left out.", Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        var levelChips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        Choice(_levels, "Recommended", "PdfBatchLevelRecommended", PdfReduceLevel.Recommended, levelChips, "batchlevel").IsChecked = true;
        Choice(_levels, "Smaller (lower quality)", "PdfBatchLevelSmaller", PdfReduceLevel.Smaller, levelChips, "batchlevel");
        smaller.Children.Add(levelChips);
        Kind("Make smaller", "PdfBatchKindSmaller", PdfBatchKind.Smaller, smaller);

        var numbers = Options();
        numbers.Children.Add(new TextBlock { Text = "Text on every page ({n} = page number, {total} = number of pages, {file} = the file's name, {date})", Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        numbers.Children.Add(_numberText);
        var spotChips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        Choice(_spots, "Top left", "PdfBatchSpotTopLeft", PdfSpot.TopLeft, spotChips, "batchspot");
        Choice(_spots, "Top middle", "PdfBatchSpotTopCenter", PdfSpot.TopCenter, spotChips, "batchspot");
        Choice(_spots, "Top right", "PdfBatchSpotTopRight", PdfSpot.TopRight, spotChips, "batchspot");
        Choice(_spots, "Bottom left", "PdfBatchSpotBottomLeft", PdfSpot.BottomLeft, spotChips, "batchspot");
        Choice(_spots, "Bottom middle", "PdfBatchSpotBottomCenter", PdfSpot.BottomCenter, spotChips, "batchspot").IsChecked = true;
        Choice(_spots, "Bottom right", "PdfBatchSpotBottomRight", PdfSpot.BottomRight, spotChips, "batchspot");
        numbers.Children.Add(spotChips);
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal };
        sizeRow.Children.Add(new TextBlock { Text = "Size", VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 8, 4) });
        sizeRow.Children.Add(_numberSize);
        numbers.Children.Add(sizeRow);
        Kind("Page numbers", "PdfBatchKindNumbers", PdfBatchKind.PageNumbers, numbers);

        var mark = Options();
        mark.Children.Add(new TextBlock { Text = "Words across every page", Opacity = 0.75, Margin = new Thickness(0, 4, 0, 0) });
        mark.Children.Add(_markText); mark.Children.Add(_slanted);
        Kind("Watermark", "PdfBatchKindWatermark", PdfBatchKind.Watermark, mark);

        var add = Options();
        add.Children.Add(new TextBlock { Text = "Password to open the files (the same for all). Write it down: a lost password can't be recovered.", Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        add.Children.Add(_addPassword);
        Kind("Add a password", "PdfBatchKindAddPassword", PdfBatchKind.AddPassword, add);

        var remove = Options();
        remove.Children.Add(new TextBlock { Text = "The password of these files (they all need the same one)", Opacity = 0.75, Margin = new Thickness(0, 4, 0, 0) });
        remove.Children.Add(_removePassword);
        Kind("Remove a password", "PdfBatchKindRemovePassword", PdfBatchKind.RemovePassword, remove);

        var size = Options();
        var paperChips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var paper in PdfResizer.Papers) Choice(_papers, paper.Name.Split(' ')[0] == "Long" ? "Long" : paper.Name.Split(' ')[0], "PdfBatchPaper" + paper.Name.Split(' ')[0], paper, paperChips, "batchpaper");
        _papers[0].Chip.IsChecked = true;
        size.Children.Add(paperChips); size.Children.Add(_keepDirection);
        size.Children.Add(new TextBlock { Text = "Every page is put on a sheet of that size, shrunk or enlarged to fit. Links, form fields and comments are not kept; protected files are skipped.", Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        Kind("Page size", "PdfBatchKindSize", PdfBatchKind.PageSize, size);

        var word = Options();
        word.Children.Add(new TextBlock { Text = "Each PDF becomes a Word document (.docx) with its text, tables and pictures. Scanned pages have no text and come out empty.", Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        Kind("Save as Word", "PdfBatchKindWord", PdfBatchKind.ToWord, word);

        root.Children.Add(chips);
        root.Children.Add(optionHost);

        // where the new files go
        root.Children.Add(new TextBlock { Text = "Where do the new files go", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) });
        root.Children.Add(_nextTo);
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal };
        var choose = new Button { Content = "Choose…", Style = (Style)Application.Current.FindResource("DialogButton"), Margin = new Thickness(8, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetAutomationId(choose, "PdfBatchChooseFolder");
        choose.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Where should the new files go?" };
            if (dlg.ShowDialog(this) == true) { _folder = dlg.FolderName; _folderText.Text = _folder; _inFolder.IsChecked = true; }
        };
        folderRow.Children.Add(_inFolder); folderRow.Children.Add(_folderText); folderRow.Children.Add(choose);
        root.Children.Add(folderRow);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_nextTo, "PdfBatchNextTo");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_inFolder, "PdfBatchInFolder");

        root.Children.Add(_bar);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_status, "PdfBatchStatus");
        root.Children.Add(_status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        _go.Style = (Style)Application.Current.FindResource("DialogPrimary");
        _close.Style = (Style)Application.Current.FindResource("DialogButton");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_go, "PdfBatchStart");
        buttons.Children.Add(_go); buttons.Children.Add(_close);
        root.Children.Add(buttons);
        Content = root;

        _kinds[0].Chip.IsChecked = true;
        if (firstFile != null && File.Exists(firstFile)) AddPaths(new[] { firstFile });
        UpdateStart();

        _go.Click += async (_, _) => { if (_cts != null) _cts.Cancel(); else await RunAsync(); };
        _close.Click += (_, _) => Close();
        Closing += (_, _) => _cts?.Cancel();
    }

    private PdfBatchKind CurrentKind => _kinds.First(k => k.Chip.IsChecked == true).Kind;

    private void UpdateStart() => _go.IsEnabled = _cts != null || _rows.Count > 0;

    private void AddFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose the PDFs", Filter = "PDF|*.pdf", Multiselect = true, CheckFileExists = true };
        if (dlg.ShowDialog(this) == true) AddPaths(dlg.FileNames);
    }

    private void AddFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder: every PDF in it is added" };
        if (dlg.ShowDialog(this) == true) AddPaths(new[] { dlg.FolderName });
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (string p in paths)
        {
            IEnumerable<string> files;
            try { files = Directory.Exists(p) ? Directory.EnumerateFiles(p, "*.pdf", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList() : new[] { p }; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (string f in files)
            {
                if (!f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || !File.Exists(f)) continue;
                if (_rows.Any(r => string.Equals(r.Path, f, StringComparison.OrdinalIgnoreCase))) continue;
                var status = new TextBlock { Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 250, Margin = new Thickness(10, 0, 0, 0) };
                var name = new TextBlock { Text = System.IO.Path.GetFileName(f), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = f };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(status, 1);
                grid.Children.Add(name); grid.Children.Add(status);
                var item = new ListBoxItem { Content = grid };
                _list.Items.Add(item);
                _rows.Add(new Row { Path = f, Item = item, Status = status });
                added++;
            }
        }
        if (added == 0 && paths.Any()) _status.Text = "No new PDFs there.";
        else if (added > 0) _status.Text = $"{_rows.Count} PDF{(_rows.Count == 1 ? "" : "s")} in the list.";
        UpdateStart();
    }

    private void RemoveChosen()
    {
        if (_cts != null) return;
        foreach (var item in _list.SelectedItems.Cast<ListBoxItem>().ToList())
        {
            _rows.RemoveAll(r => r.Item == item);
            _list.Items.Remove(item);
        }
        UpdateStart();
    }

    private PdfBatchJob? BuildJob()
    {
        var job = new PdfBatchJob { Kind = CurrentKind };
        switch (job.Kind)
        {
            case PdfBatchKind.Smaller: job.Level = _levels.First(l => l.Chip.IsChecked == true).Level; break;
            case PdfBatchKind.PageNumbers:
                job.Text = _numberText.Text;
                job.Spot = _spots.First(s => s.Chip.IsChecked == true).Spot;
                if (job.Text.Trim().Length == 0) { _status.Text = "Write the text for the pages (for example: Page {n} of {total})."; return null; }
                if (!double.TryParse(_numberSize.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out double size) || size < 4 || size > 72) { _status.Text = "The size is a number between 4 and 72."; return null; }
                job.Size = size;
                break;
            case PdfBatchKind.Watermark:
                job.Text = _markText.Text; job.Slanted = _slanted.IsChecked == true;
                if (job.Text.Trim().Length == 0) { _status.Text = "Write the words for the watermark."; return null; }
                break;
            case PdfBatchKind.AddPassword:
                job.Password = _addPassword.Password;
                if (job.Password.Length == 0) { _status.Text = "Type the password first."; return null; }
                break;
            case PdfBatchKind.RemovePassword:
                job.Password = _removePassword.Password;
                if (job.Password.Length == 0) { _status.Text = "Type the password of the files first."; return null; }
                break;
            case PdfBatchKind.PageSize:
                job.Paper = _papers.First(p => p.Chip.IsChecked == true).Paper; job.KeepDirection = _keepDirection.IsChecked == true;
                break;
        }
        return job;
    }

    private async Task RunAsync()
    {
        if (_rows.Count == 0) return;
        if (BuildJob() is not { } job) return;
        if (_inFolder.IsChecked == true && (_folder == null || !Directory.Exists(_folder))) { _status.Text = "Choose the folder for the new files first."; return; }
        string? outFolder = _inFolder.IsChecked == true ? _folder : null;
        var cts = _cts = new CancellationTokenSource();
        _go.Content = "Stop";
        _bar.Visibility = Visibility.Visible; _bar.Value = 0;
        foreach (var r in _rows) r.Status.Text = "";
        int done = 0, left = 0, failed = 0;
        var rows = _rows.ToList();
        try
        {
            for (int i = 0; i < rows.Count; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var row = rows[i];
                row.Status.Text = "Working…";
                _status.Text = $"{System.IO.Path.GetFileName(row.Path)}  ({i + 1} of {rows.Count})…";
                _bar.Value = (double)i / rows.Count;
                try
                {
                    string path = row.Path;
                    var result = await Task.Run(() => PdfBatch.Process(path, job, cts.Token));
                    if (result.Output == null) { row.Status.Text = "Left out: " + result.Note; left++; continue; }
                    string folder = outFolder ?? System.IO.Path.GetDirectoryName(path)!;
                    string target = PdfBatch.Target(folder, System.IO.Path.GetFileNameWithoutExtension(path), PdfBatch.Suffix(job.Kind, job), result.Extension);
                    await Task.Run(() => File.WriteAllBytes(target, result.Output));
                    row.Status.Text = "Done: " + System.IO.Path.GetFileName(target) + (result.Note.Length > 0 ? " (" + result.Note + ")" : "");
                    row.Status.ToolTip = target;
                    done++;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    string why = e is PdfPasswordException ? "needs a password" : e.Message.Split('\n')[0];
                    row.Status.Text = "Couldn't: " + why; failed++;
                }
            }
            _status.Text = $"Finished: {done} done" + (left > 0 ? $", {left} left out" : "") + (failed > 0 ? $", {failed} couldn't be done" : "") + ". The originals are untouched.";
        }
        catch (OperationCanceledException) { _status.Text = $"Stopped after {done} file{(done == 1 ? "" : "s")}."; }
        finally
        {
            _bar.Value = 1; _cts = null; _go.Content = "Start"; UpdateStart();
        }
    }
}
