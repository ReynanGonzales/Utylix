using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// "Page size": a copy of the PDF with every page on a sheet of another size (A4, Letter, Long ...), scaled to fit and centred. The original
/// is never changed; the person chooses where the copy is saved. Everything runs on this PC.
/// </summary>
public sealed class PdfResizeWindow : Window
{
    private readonly string _path;
    private readonly string? _password;
    private readonly List<(RadioButton Chip, PaperSize? Paper)> _choices = new();
    private readonly RadioButton _custom;
    private readonly TextBox _customW = new() { Width = 72, Text = "210", Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _customH = new() { Width = 72, Text = "297", Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly RadioButton _mm, _inch;
    private readonly CheckBox _keep = new() { Content = "Keep each page's direction (wide pages stay wide, tall pages stay tall)", IsChecked = true, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _detail = new() { Opacity = 0.75, FontSize = 12.5, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _bar = new() { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 16, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), MinHeight = 20 };
    private readonly Button _go = new() { Content = "Make a copy…", MinWidth = 140, Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button _close = new() { Content = "Close", MinWidth = 96 };
    private CancellationTokenSource? _cts;
    private string? _saved;
    private readonly int _pages;
    private readonly bool _protected;

    public PdfResizeWindow(string path, string? password)
    {
        _path = path; _password = password;
        Title = "Page size - Utylix Editor";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13.5;
        WindowTheme.DarkTitleBar(this);
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/pdf.ico")); } catch (Exception e) when (e is IOException or UriFormatException) { }

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock { Text = "Page size", FontSize = 20, FontWeight = FontWeights.SemiBold });

        // what the PDF has now
        string now; bool form = false;
        try
        {
            using var pdf = PdfFile.Open(path, password);
            _pages = pdf.PageCount; _protected = pdf.IsProtected;
            now = Describe(pdf);
            form = PdfResizer.HasForm(pdf);
        }
        catch (Exception e) when (e is IOException or PdfPasswordException) { now = "The PDF can't be read."; }
        root.Children.Add(new TextBlock { Text = Path.GetFileName(path) + "   ·   " + now, Opacity = 0.75, Margin = new Thickness(0, 4, 0, 14), TextWrapping = TextWrapping.Wrap });

        root.Children.Add(new TextBlock { Text = "Put every page on", FontWeight = FontWeights.SemiBold });
        var chips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var chipStyle = (Style)Application.Current.FindResource("ChipButton");
        foreach (var paper in PdfResizer.Papers)
        {
            var chip = new RadioButton { Content = ShortName(paper), Style = chipStyle, GroupName = "paper", Tag = paper };
            System.Windows.Automation.AutomationProperties.SetAutomationId(chip, "PdfSize" + ShortName(paper).Split(' ')[0]);
            chip.Checked += (_, _) => ShowDetail();
            _choices.Add((chip, paper)); chips.Children.Add(chip);
        }
        _custom = new RadioButton { Content = "Other size", Style = chipStyle, GroupName = "paper" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_custom, "PdfSizeOther");
        _custom.Checked += (_, _) => ShowDetail();
        chips.Children.Add(_custom);
        _choices[0].Chip.IsChecked = true;
        root.Children.Add(chips);
        root.Children.Add(_detail);

        var custom = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_customW, "PdfSizeWidth");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_customH, "PdfSizeHeight");
        _customW.GotKeyboardFocus += (_, _) => { _custom.IsChecked = true; _customW.SelectAll(); };
        _customH.GotKeyboardFocus += (_, _) => { _custom.IsChecked = true; _customH.SelectAll(); };
        _customW.TextChanged += (_, _) => ShowDetail(); _customH.TextChanged += (_, _) => ShowDetail();
        _mm = new RadioButton { Content = "mm", Style = chipStyle, GroupName = "unit", IsChecked = true, Margin = new Thickness(10, 0, 4, 0) };
        _inch = new RadioButton { Content = "inches", Style = chipStyle, GroupName = "unit", Margin = new Thickness(0, 0, 0, 0) };
        _mm.Checked += (_, _) => { _custom.IsChecked = true; ConvertCustom(true); }; _inch.Checked += (_, _) => { _custom.IsChecked = true; ConvertCustom(false); };
        custom.Children.Add(new TextBlock { Text = "Width", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        custom.Children.Add(_customW);
        custom.Children.Add(new TextBlock { Text = "Height", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 6, 0) });
        custom.Children.Add(_customH);
        custom.Children.Add(_mm); custom.Children.Add(_inch);
        root.Children.Add(custom);

        System.Windows.Automation.AutomationProperties.SetAutomationId(_keep, "PdfSizeKeep");
        root.Children.Add(_keep);
        root.Children.Add(new TextBlock
        {
            Text = "Pages are made bigger or smaller to fit and centred, never stretched. Text stays text. Your original file is not changed: you choose where the copy is saved.",
            Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
        });
        string warning = _protected ? "This PDF is protected with a password, so a copy with other page sizes can't be made."
            : form ? "This PDF has form fields. They are not kept in the new copy (the page looks the same, but the fields can no longer be filled in). Links and comments are not kept either."
            : "Links and comments of the original are not kept in the new copy.";
        var warn = new TextBlock { Text = warning, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = (Brush)Application.Current.FindResource("TextBrush"), FontWeight = form || _protected ? FontWeights.SemiBold : FontWeights.Normal, Opacity = form || _protected ? 1 : 0.6 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(warn, "PdfSizeWarning");
        root.Children.Add(warn);
        root.Children.Add(_bar);
        root.Children.Add(_status);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_status, "PdfSizeStatus");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        _go.Style = (Style)Application.Current.FindResource("DialogPrimary");
        _close.Style = (Style)Application.Current.FindResource("DialogButton");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_go, "PdfSizeGo");
        buttons.Children.Add(_go); buttons.Children.Add(_close);
        root.Children.Add(buttons);
        Content = root;
        _go.IsEnabled = !_protected && _pages > 0;
        ShowDetail();

        _go.Click += async (_, _) => await GoAsync();
        _close.Click += (_, _) => Close();
        Closing += (_, _) => _cts?.Cancel();
    }

    private static string ShortName(PaperSize p) => p.Name.Contains(" (") ? p.Name.Replace(" / Folio", "") : p.Name;

    /// <summary>"12 pages: 11 × A4, 1 × 297 × 420 mm"</summary>
    private static string Describe(PdfFile pdf)
    {
        var groups = new Dictionary<string, int>();
        for (int i = 0; i < pdf.PageCount; i++)
        {
            var s = pdf.PageSize(i);
            string label = PdfResizer.Papers.Where(p => Near(s.Width, s.Height, p.Width, p.Height)).Select(p => p.Name.Split(' ')[0]).FirstOrDefault()
                           ?? $"{s.Width / 72 * 25.4:0} × {s.Height / 72 * 25.4:0} mm";
            groups[label] = groups.GetValueOrDefault(label) + 1;
        }
        string pages = pdf.PageCount == 1 ? "1 page" : pdf.PageCount + " pages";
        return groups.Count == 1 ? $"{pages}, {groups.Keys.First()}" : $"{pages}: " + string.Join(", ", groups.OrderByDescending(g => g.Value).Take(4).Select(g => $"{g.Value} × {g.Key}")) + (groups.Count > 4 ? ", …" : "");
    }

    private static bool Near(double w, double h, double pw, double ph) =>
        (Math.Abs(w - pw) < 3 && Math.Abs(h - ph) < 3) || (Math.Abs(w - ph) < 3 && Math.Abs(h - pw) < 3);

    private bool Parse(TextBox box, out double value) => double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0;

    /// <summary>The chosen sheet in points, or null when the "other size" boxes don't hold sensible numbers.</summary>
    private (double Width, double Height)? Chosen()
    {
        foreach (var (chip, paper) in _choices) if (chip.IsChecked == true && paper is { } p) return (p.Width, p.Height);
        if (!Parse(_customW, out double w) || !Parse(_customH, out double h)) return null;
        double k = _mm.IsChecked == true ? 72 / 25.4 : 72;
        double wp = w * k, hp = h * k;
        return wp is < 72 or > 14400 || hp is < 72 or > 14400 ? null : (wp, hp);       // between 1 inch and 200 inches
    }

    private void ConvertCustom(bool toMm)
    {
        if (!Parse(_customW, out double w) || !Parse(_customH, out double h)) return;
        double k = 25.4;
        _customW.Text = (toMm ? w * k : w / k).ToString(toMm ? "0.#" : "0.##", CultureInfo.InvariantCulture);
        _customH.Text = (toMm ? h * k : h / k).ToString(toMm ? "0.#" : "0.##", CultureInfo.InvariantCulture);
    }

    private void ShowDetail()
    {
        if (_detail == null || _custom == null) return;
        var size = Chosen();
        bool usable = !_protected && _pages > 0 && _cts == null;
        if (size is not { } s) { _detail.Text = "Type a width and a height (between 1 and 200 inches)."; if (_saved == null) _go.IsEnabled = false; return; }
        _detail.Text = $"{s.Width / 72 * 25.4:0.#} × {s.Height / 72 * 25.4:0.#} mm   ·   {s.Width / 72:0.##} × {s.Height / 72:0.##} inches";
        if (_saved == null) _go.IsEnabled = usable;
    }

    private async Task GoAsync()
    {
        if (_saved != null) { PdfWindow.Open(new[] { _saved }); Close(); return; }       // (after saving, the button opens the new copy)
        if (Chosen() is not { } size) return;
        string stem = Path.GetFileNameWithoutExtension(_path);
        string tag = _choices.FirstOrDefault(c => c.Chip.IsChecked == true).Paper is { } p ? p.Name.Split(' ')[0] : $"{size.Width / 72 * 25.4:0}x{size.Height / 72 * 25.4:0}mm";
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the copy with the new page size", Filter = "PDF|*.pdf", FileName = $"{stem} ({tag}).pdf",
            InitialDirectory = Path.GetDirectoryName(_path), OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        string target = dlg.FileName;
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase)) { _status.Text = "Choose another name: this copy can't replace the original while it is being made from it."; return; }

        _cts = new CancellationTokenSource();
        _go.IsEnabled = false; _keep.IsEnabled = false;
        foreach (var (chip, _) in _choices) chip.IsEnabled = false;
        _custom.IsEnabled = false;
        _bar.Visibility = Visibility.Visible; _bar.Value = 0; _status.Text = "Making the copy…";
        var progress = new Progress<(int Done, int Total)>(x => { _bar.Value = x.Total == 0 ? 0 : (double)x.Done / x.Total; _status.Text = x.Done < x.Total ? $"Page {x.Done + 1} of {x.Total}…" : "Saving…"; });
        bool keep = _keep.IsChecked == true;
        try
        {
            byte[] bytes = await Task.Run(() =>
            {
                using var pdf = PdfFile.Open(_path, _password);
                byte[] made = PdfResizer.Resize(pdf, size.Width, size.Height, keep, progress, _cts.Token);
                string temp = target + ".utylix-tmp";
                File.WriteAllBytes(temp, made);
                File.Move(temp, target, overwrite: true);
                return made;
            });
            _saved = target;
            _bar.Value = 1;
            int pages = 0;
            try { using var made = PdfFile.Open(target); pages = made.PageCount; } catch (Exception e) when (e is IOException or PdfPasswordException) { }
            _status.Text = $"Done: {Path.GetFileName(target)}" + (pages > 0 ? $", {pages} page{(pages == 1 ? "" : "s")}" : "") + $", {PdfReduceWindow.Bytes(bytes.Length)}.";
            _go.Content = "Open it"; _go.IsEnabled = true;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PdfPasswordException or PdfProtectedException or OutOfMemoryException or DllNotFoundException)
        {
            _status.Text = "Couldn't make the copy: " + e.Message;
            _bar.Visibility = Visibility.Collapsed;
            _go.IsEnabled = true; _keep.IsEnabled = true;
            foreach (var (chip, _) in _choices) chip.IsEnabled = true;
            _custom.IsEnabled = true;
        }
        finally { _cts = null; }
    }
}
