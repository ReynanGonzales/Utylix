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
/// "Save as Word or Excel": the text of the open PDF (with its fonts, sizes, bold, italic, colours and pictures) becomes a .docx, or its
/// lines and columns become a .xlsx. Pages that are scans can be read first with Windows' text reader. The open PDF is not changed.
/// </summary>
public sealed class PdfExportDialog : Window
{
    private readonly PdfFile _pdf;
    private readonly string _path;
    private readonly IReadOnlyList<int> _selected;
    private readonly RadioButton _word, _excel, _all, _chosen, _perPage, _oneSheet;
    private readonly CheckBox _ocr = new() { IsChecked = true, Margin = new Thickness(0, 12, 0, 0) };
    private readonly StackPanel _excelOptions = new() { Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
    private readonly ProgressBar _bar = new() { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), MinHeight = 20 };
    private readonly Button _go = new() { MinWidth = 150, Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button _close = new() { Content = "Close", MinWidth = 96 };
    private int _noText;                      // pages without any text (scans)
    private string? _saved;
    private CancellationTokenSource? _cts;

    public PdfExportDialog(Window owner, PdfFile pdf, string path, IReadOnlyList<int> selected)
    {
        _pdf = pdf; _path = path; _selected = selected;
        Owner = owner;
        Title = "Save as Word or Excel - Utylix Editor";
        Width = 600; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13.5;
        WindowTheme.DarkTitleBar(this);
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/pdf.ico")); } catch (Exception e) when (e is IOException or UriFormatException) { }
        var chip = (Style)Application.Current.FindResource("ChipButton");

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock { Text = "Save as Word or Excel", FontSize = 20, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text = $"{Path.GetFileName(path)}   ·   {pdf.PageCount} page{(pdf.PageCount == 1 ? "" : "s")}", Opacity = 0.75, Margin = new Thickness(0, 4, 0, 14) });

        root.Children.Add(new TextBlock { Text = "Make a", FontWeight = FontWeights.SemiBold });
        var kinds = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _word = new RadioButton { Content = "Word document (.docx)", Style = chip, GroupName = "kind", IsChecked = true };
        _excel = new RadioButton { Content = "Excel workbook (.xlsx)", Style = chip, GroupName = "kind" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_word, "PdfExportWord");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_excel, "PdfExportExcel");
        kinds.Children.Add(_word); kinds.Children.Add(_excel);
        root.Children.Add(kinds);
        root.Children.Add(new TextBlock { Text = "Word keeps paragraphs, fonts, sizes, bold, italic, colours and pictures. Excel puts each line in a row and lines the pieces up in columns, with numbers as numbers.", Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });

        root.Children.Add(new TextBlock { Text = "Pages", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) });
        var which = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        bool some = selected.Count > 0 && selected.Count < pdf.PageCount;
        _all = new RadioButton { Content = $"All {pdf.PageCount} page{(pdf.PageCount == 1 ? "" : "s")}", Style = chip, GroupName = "which", IsChecked = !some };
        _chosen = new RadioButton { Content = selected.Count == 1 ? "The chosen page" : $"The {selected.Count} chosen pages", Style = chip, GroupName = "which", IsChecked = some, IsEnabled = some };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_all, "PdfExportAll");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_chosen, "PdfExportChosen");
        which.Children.Add(_all); which.Children.Add(_chosen);
        root.Children.Add(which);

        _perPage = new RadioButton { Content = "One sheet for each page", Style = chip, GroupName = "sheets", IsChecked = true };
        _oneSheet = new RadioButton { Content = "All pages on one sheet", Style = chip, GroupName = "sheets" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_oneSheet, "PdfExportOneSheet");
        var sheets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        sheets.Children.Add(_perPage); sheets.Children.Add(_oneSheet);
        _excelOptions.Children.Add(new TextBlock { Text = "Sheets", FontWeight = FontWeights.SemiBold });
        _excelOptions.Children.Add(sheets);
        root.Children.Add(_excelOptions);
        _excel.Checked += (_, _) => _excelOptions.Visibility = Visibility.Visible;
        _word.Checked += (_, _) => _excelOptions.Visibility = Visibility.Collapsed;

        // pages without text
        _noText = 0;
        try { for (int i = 0; i < pdf.PageCount; i++) if (pdf.GetAllTextRuns(i).Count == 0) _noText++; } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        if (_noText > 0)
        {
            string note = _noText == pdf.PageCount ? "This PDF has no text (scans or photos). Read it with Windows' text reader first (on this PC, a few seconds a page)" : $"{_noText} page{(_noText == 1 ? " has" : "s have")} no text (scans or photos). Read {(_noText == 1 ? "it" : "them")} with Windows' text reader first";
            _ocr.Content = new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, MaxWidth = 500, Foreground = (Brush)Application.Current.FindResource("TextBrush") };
            System.Windows.Automation.AutomationProperties.SetAutomationId(_ocr, "PdfExportOcr");
            root.Children.Add(_ocr);
        }
        root.Children.Add(new TextBlock { Text = "Fonts, columns and pictures come out close, not exactly: a complicated page (text in columns, text round a picture) will be simpler than the PDF. Your PDF is not changed.", Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        root.Children.Add(_bar);
        root.Children.Add(_status);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_status, "PdfExportStatus");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        _go.Content = "Save…";
        _go.Style = (Style)Application.Current.FindResource("DialogPrimary");
        _close.Style = (Style)Application.Current.FindResource("DialogButton");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_go, "PdfExportGo");
        buttons.Children.Add(_go); buttons.Children.Add(_close);
        root.Children.Add(buttons);
        Content = root;
        _go.IsEnabled = !pdf.IsProtected || pdf.CanEdit;
        if (pdf.IsProtected && !pdf.CanEdit) _status.Text = "This PDF is protected: its owner doesn't allow copying text out.";

        _go.Click += async (_, _) => await GoAsync();
        _close.Click += (_, _) => Close();
        Closing += (_, _) => _cts?.Cancel();
    }

    private async Task GoAsync()
    {
        if (_saved != null) { try { Process.Start(new ProcessStartInfo(_saved) { UseShellExecute = true }); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { _status.Text = "Couldn't open it: " + e.Message; return; } Close(); return; }
        bool word = _word.IsChecked == true;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = word ? "Save as a Word document" : "Save as an Excel workbook", Filter = word ? "Word document|*.docx" : "Excel workbook|*.xlsx", OverwritePrompt = true,
            FileName = Path.GetFileNameWithoutExtension(_path) + (word ? ".docx" : ".xlsx"), InitialDirectory = Path.GetDirectoryName(_path),
        };
        if (dlg.ShowDialog(this) != true) return;
        string target = dlg.FileName;
        var pages = _chosen.IsChecked == true ? _selected.OrderBy(i => i).ToList() : Enumerable.Range(0, _pdf.PageCount).ToList();
        bool oneSheet = _oneSheet.IsChecked == true, useOcr = _noText > 0 && _ocr.IsChecked == true;
        var pdf = _pdf;

        Windows.Media.Ocr.OcrEngine? engine = null;
        if (useOcr)
        {
            engine = PdfOcr.CreateEngine(out string problem);
            if (engine == null) { _status.Text = problem; return; }
        }
        _go.IsEnabled = false; _bar.Visibility = Visibility.Visible; _bar.Value = 0; _status.Text = "Working…";
        var cts = _cts = new CancellationTokenSource();
        try
        {
            var content = new List<ExportPage>();
            for (int k = 0; k < pages.Count; k++)
            {
                cts.Token.ThrowIfCancellationRequested();
                int page = pages[k];
                _bar.Value = (double)k / pages.Count;
                _status.Text = $"Page {page + 1}  ({k + 1} of {pages.Count})…";
                List<OcrWord>? words = null; (int, int) pixels = (0, 0);
                bool empty = await Task.Run(() => pdf.GetAllTextRuns(page).Count == 0);
                if (empty && engine != null)
                {
                    _status.Text = $"Reading page {page + 1} with Windows' text reader  ({k + 1} of {pages.Count})…";
                    var px = PdfOcr.PixelSize(pdf.PageSize(page));
                    var picture = await Task.Run(() => pdf.Render(page, px.Width, px.Height, 0, forScreen: false));
                    words = await PdfOcr.ReadAsync(engine, picture); pixels = px;
                }
                var ocrWords = words; var pix = pixels;
                content.Add(await Task.Run(() => PdfExport.Read(pdf, page, ocrWords, pix)));
            }
            _status.Text = "Writing the file…";
            byte[] bytes = await Task.Run(() => word ? PdfExport.ToDocx(content) : PdfExport.ToXlsx(content, oneSheet));
            await Task.Run(() => File.WriteAllBytes(target, bytes));
            int withText = content.Count(c => c.Runs.Count > 0);
            _bar.Value = 1; _saved = target;
            _status.Text = $"Saved {Path.GetFileName(target)}" + (withText < content.Count ? $". {content.Count - withText} page{(content.Count - withText == 1 ? " has" : "s have")} no text, so {(content.Count - withText == 1 ? "it is" : "they are")} empty there." : ".");
            _go.Content = "Open it"; _go.IsEnabled = true;
        }
        catch (OperationCanceledException) { _status.Text = "Stopped."; _go.IsEnabled = true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OutOfMemoryException or ObjectDisposedException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _status.Text = "Couldn't finish: " + e.Message; _go.IsEnabled = true;
        }
        finally { _cts = null; }
    }
}
