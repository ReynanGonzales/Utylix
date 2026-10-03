using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// "Page numbers, header, footer and watermark": a line of text at the top or bottom of the pages ("Page {n} of {total}", the date, the file's
/// name ...) and / or a big pale watermark (text or a picture), on all pages or a range, with a preview of a page that follows every change.
/// The caller writes the result into the open document.
/// </summary>
public sealed class PdfPageMarksDialog : Window
{
    private readonly PdfFile _pdf;
    private readonly string _stem;
    private readonly int _count;
    private int _previewPage;
    private readonly DispatcherTimer _delay = new() { Interval = TimeSpan.FromMilliseconds(130) };

    // text line
    private readonly CheckBox _useLine = new() { Content = "Text at the top or bottom of the pages", IsChecked = true, FontWeight = FontWeights.SemiBold };
    private readonly TextBox _text = new() { Text = "Page {n} of {total}", Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly List<(RadioButton Chip, PdfSpot Spot)> _spots = new();
    private readonly List<(RadioButton Chip, PdfFontKind Kind)> _fonts = new();
    private readonly CheckBox _bold = new() { Content = "Bold", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _size = new() { Text = "10", Width = 52, Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private readonly List<(RadioButton Chip, Color Color)> _lineColors = new();
    private readonly TextBox _start = new() { Text = "1", Width = 52, Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private readonly CheckBox _skipFirst = new() { Content = "No number on the first page (a cover)", Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _linePanel = new();

    // watermark
    private readonly CheckBox _useMark = new() { Content = "Watermark across the pages", FontWeight = FontWeights.SemiBold };
    private readonly TextBox _markText = new() { Text = "CONFIDENTIAL", Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly RadioButton _kindText, _kindPicture;
    private readonly Button _pickPicture = new() { Content = "Choose a picture…", MinWidth = 140, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock _pictureName = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
    private PdfPicture? _picture;
    private readonly Slider _markWidth = new() { Minimum = 20, Maximum = 100, Value = 70, Width = 200, TickFrequency = 5, IsSnapToTickEnabled = true };
    private readonly Slider _markOpacity = new() { Minimum = 5, Maximum = 80, Value = 25, Width = 200, TickFrequency = 5, IsSnapToTickEnabled = true };
    private readonly RadioButton _diagonal, _straight;
    private readonly List<(RadioButton Chip, Color Color)> _markColors = new();
    private readonly StackPanel _markPanel = new();

    // pages
    private readonly RadioButton _allPages, _somePages;
    private readonly TextBox _from = new() { Text = "1", Width = 52, Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    private readonly TextBox _to = new() { Width = 52, Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };

    // preview
    private readonly Image _previewImage = new() { Stretch = System.Windows.Media.Stretch.Fill };
    private readonly Canvas _previewMarks = new() { ClipToBounds = true, IsHitTestVisible = false };
    private readonly Grid _previewBox = new() { Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _previewLabel = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<int, System.Windows.Media.Imaging.BitmapSource> _renders = new();
    private readonly TextBlock _problem = new() { Foreground = (Brush)Application.Current.FindResource("TextBrush"), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, MinHeight = 20, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _go = new() { Content = "Add to the pages", MinWidth = 150, Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button _cancel = new() { Content = "Cancel", MinWidth = 96 };
    private const double PreviewWidth = 300;

    public PdfHeaderFooter? Line { get; private set; }
    public PdfWatermark? Mark { get; private set; }
    public int FirstPage { get; private set; }
    public int LastPage { get; private set; }

    public PdfPageMarksDialog(Window owner, PdfFile pdf, string path, int currentPage)
    {
        _pdf = pdf; _count = pdf.PageCount; _stem = Path.GetFileNameWithoutExtension(path);
        _previewPage = Math.Clamp(currentPage, 0, _count - 1);
        Owner = owner;
        Title = "Page numbers, header, footer and watermark - Utylix Editor";
        Width = 860; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13.5;
        WindowTheme.DarkTitleBar(this);
        try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/pdf.ico")); } catch (Exception e) when (e is IOException or UriFormatException) { }
        var chip = (Style)Application.Current.FindResource("ChipButton");
        _to.Text = _count.ToString(CultureInfo.InvariantCulture);

        // ----- the text line
        var line = _linePanel;
        line.Margin = new Thickness(0, 8, 0, 0);
        line.Children.Add(_text);
        var insert = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (label, token) in new[] { ("Page number", "{n}"), ("Number of pages", "{total}"), ("Date", "{date}"), ("File name", "{file}") })
        {
            var b = new Button { Content = label, Style = (Style)Application.Current.FindResource("DialogButton"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 4), FontSize = 12 };
            b.Click += (_, _) => { int at = _text.SelectionStart; _text.Text = _text.Text.Remove(at, _text.SelectionLength).Insert(at, token); _text.SelectionStart = at + token.Length; _text.Focus(); };
            insert.Children.Add(b);
        }
        line.Children.Add(insert);
        line.Children.Add(new TextBlock { Text = "{n} = page number, {total} = how many pages, {date} = today, {file} = this file's name", Opacity = 0.6, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        line.Children.Add(new TextBlock { Text = "Where", Margin = new Thickness(0, 10, 0, 4) });
        var grid = new UniformGrid3();
        foreach (var (label, spot) in new[] { ("Top left", PdfSpot.TopLeft), ("Top middle", PdfSpot.TopCenter), ("Top right", PdfSpot.TopRight), ("Bottom left", PdfSpot.BottomLeft), ("Bottom middle", PdfSpot.BottomCenter), ("Bottom right", PdfSpot.BottomRight) })
        {
            var r = new RadioButton { Content = label, Style = chip, GroupName = "spot", IsChecked = spot == PdfSpot.BottomCenter, HorizontalContentAlignment = HorizontalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetAutomationId(r, "PdfMarkSpot" + spot);
            r.Checked += (_, _) => Changed();
            _spots.Add((r, spot)); grid.Children.Add(r);
        }
        line.Children.Add(grid);
        var look = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (label, kind) in new[] { ("Sans", PdfFontKind.Sans), ("Serif", PdfFontKind.Serif), ("Mono", PdfFontKind.Mono) })
        {
            var r = new RadioButton { Content = new TextBlock { Text = label, FontFamily = PdfPageMarks.Family(kind), Foreground = (Brush)Application.Current.FindResource("TextBrush") }, Style = chip, GroupName = "font", IsChecked = kind == PdfFontKind.Sans };
            r.Checked += (_, _) => Changed();
            _fonts.Add((r, kind)); look.Children.Add(r);
        }
        look.Children.Add(_bold);
        look.Children.Add(new TextBlock { Text = "Size", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) });
        look.Children.Add(_size);
        line.Children.Add(look);
        var colours = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        colours.Children.Add(new TextBlock { Text = "Colour", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        foreach (var c in new[] { Colors.Black, Color.FromRgb(0x55, 0x55, 0x55), Color.FromRgb(0x10, 0x2A, 0x8C), Color.FromRgb(0xD3, 0x2F, 0x2F), Color.FromRgb(0x2E, 0x7D, 0x32) })
            colours.Children.Add(ColorChip(c, _lineColors, "linecolor", c == Colors.Black));
        line.Children.Add(colours);
        var numbering = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        numbering.Children.Add(new TextBlock { Text = "The first page is numbered", VerticalAlignment = VerticalAlignment.Center });
        numbering.Children.Add(_start);
        line.Children.Add(numbering);
        line.Children.Add(_skipFirst);

        // ----- the watermark
        var mark = _markPanel;
        mark.Margin = new Thickness(0, 8, 0, 0);
        var kinds = new StackPanel { Orientation = Orientation.Horizontal };
        _kindText = new RadioButton { Content = "Words", Style = chip, GroupName = "markkind", IsChecked = true };
        _kindPicture = new RadioButton { Content = "A picture", Style = chip, GroupName = "markkind" };
        kinds.Children.Add(_kindText); kinds.Children.Add(_kindPicture);
        mark.Children.Add(kinds);
        _markText.Margin = new Thickness(0, 8, 0, 0);
        mark.Children.Add(_markText);
        var pic = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        pic.Children.Add(_pickPicture); pic.Children.Add(_pictureName);
        mark.Children.Add(pic);
        var wRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        wRow.Children.Add(new TextBlock { Text = "Size", Width = 96, VerticalAlignment = VerticalAlignment.Center }); wRow.Children.Add(_markWidth);
        mark.Children.Add(wRow);
        var oRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        oRow.Children.Add(new TextBlock { Text = "See-through", Width = 96, VerticalAlignment = VerticalAlignment.Center }); oRow.Children.Add(_markOpacity);
        mark.Children.Add(oRow);
        var angle = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _diagonal = new RadioButton { Content = "Slanted", Style = chip, GroupName = "markangle", IsChecked = true };
        _straight = new RadioButton { Content = "Straight", Style = chip, GroupName = "markangle" };
        angle.Children.Add(new TextBlock { Text = "Words are", Width = 96, VerticalAlignment = VerticalAlignment.Center }); angle.Children.Add(_diagonal); angle.Children.Add(_straight);
        mark.Children.Add(angle);
        var mcolours = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        mcolours.Children.Add(new TextBlock { Text = "Colour", Width = 96, VerticalAlignment = VerticalAlignment.Center });
        foreach (var c in new[] { Color.FromRgb(0xD3, 0x2F, 0x2F), Color.FromRgb(0x77, 0x77, 0x77), Colors.Black, Color.FromRgb(0x1E, 0x63, 0xE9), Color.FromRgb(0x2E, 0x7D, 0x32) })
            mcolours.Children.Add(ColorChip(c, _markColors, "markcolor", c == Color.FromRgb(0xD3, 0x2F, 0x2F)));
        mark.Children.Add(mcolours);
        _kindText.Checked += (_, _) => { _markText.Visibility = Visibility.Visible; pic.Visibility = Visibility.Collapsed; _diagonal.IsEnabled = _straight.IsEnabled = true; Changed(); };
        _kindPicture.Checked += (_, _) => { _markText.Visibility = Visibility.Collapsed; pic.Visibility = Visibility.Visible; _diagonal.IsEnabled = _straight.IsEnabled = false; if (_picture == null) ChoosePicture(); Changed(); };
        _pickPicture.Click += (_, _) => ChoosePicture();

        // ----- pages
        var pages = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        _allPages = new RadioButton { Content = $"All {_count} page{(_count == 1 ? "" : "s")}", Style = chip, GroupName = "range", IsChecked = true };
        _somePages = new RadioButton { Content = "Pages", Style = chip, GroupName = "range" };
        pages.Children.Add(_allPages); pages.Children.Add(_somePages); pages.Children.Add(_from);
        pages.Children.Add(new TextBlock { Text = "to", VerticalAlignment = VerticalAlignment.Center }); pages.Children.Add(_to);
        _from.GotKeyboardFocus += (_, _) => _somePages.IsChecked = true; _to.GotKeyboardFocus += (_, _) => _somePages.IsChecked = true;

        // ----- layout
        var left = new StackPanel { Margin = new Thickness(0, 0, 20, 0) };
        left.Children.Add(new TextBlock { Text = "Page numbers, header, footer and watermark", FontSize = 20, FontWeight = FontWeights.SemiBold });
        left.Children.Add(new TextBlock { Text = Path.GetFileName(path) + "   ·   " + _count + " page" + (_count == 1 ? "" : "s"), Opacity = 0.75, Margin = new Thickness(0, 4, 0, 12) });
        left.Children.Add(_useLine); left.Children.Add(line);
        left.Children.Add(new Separator { Margin = new Thickness(0, 14, 0, 12), Opacity = 0.5 });
        left.Children.Add(_useMark); left.Children.Add(mark);
        left.Children.Add(new Separator { Margin = new Thickness(0, 14, 0, 12), Opacity = 0.5 });
        left.Children.Add(new TextBlock { Text = "On", FontWeight = FontWeights.SemiBold });
        left.Children.Add(pages);
        left.Children.Add(_problem);

        _previewBox.Children.Add(_previewImage); _previewBox.Children.Add(_previewMarks);
        var previewBorder = new Border { Child = _previewBox, BorderBrush = (Brush)Application.Current.FindResource("LineBrush"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.35 } };
        var nav = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        var prev = new Button { Content = "◀", Style = (Style)Application.Current.FindResource("DialogButton"), Width = 36, Padding = new Thickness(0, 3, 0, 3) };
        var next = new Button { Content = "▶", Style = (Style)Application.Current.FindResource("DialogButton"), Width = 36, Padding = new Thickness(0, 3, 0, 3) };
        prev.Click += (_, _) => { _previewPage = Math.Max(0, _previewPage - 1); Changed(); };
        next.Click += (_, _) => { _previewPage = Math.Min(_count - 1, _previewPage + 1); Changed(); };
        nav.Children.Add(prev); nav.Children.Add(_previewLabel); nav.Children.Add(next);
        var right = new StackPanel { Width = PreviewWidth + 10 };
        right.Children.Add(new TextBlock { Text = "Preview", Opacity = 0.75, Margin = new Thickness(0, 4, 0, 8) });
        right.Children.Add(previewBorder); right.Children.Add(nav);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        _go.Style = (Style)Application.Current.FindResource("DialogPrimary");
        _cancel.Style = (Style)Application.Current.FindResource("DialogButton");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_go, "PdfMarksGo");
        buttons.Children.Add(_go); buttons.Children.Add(_cancel);

        var body = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        body.Children.Add(right); body.Children.Add(left);
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(body); root.Children.Add(buttons);
        Content = root;
        _useMark.IsChecked = false;

        System.Windows.Automation.AutomationProperties.SetAutomationId(_text, "PdfMarkText");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_markText, "PdfMarkWatermarkText");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_useLine, "PdfMarkUseLine");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_useMark, "PdfMarkUseWatermark");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_problem, "PdfMarksProblem");
        foreach (var tb in new[] { _text, _markText, _size, _start, _from, _to }) tb.TextChanged += (_, _) => Changed();
        foreach (var cb in new[] { _useLine, _useMark, _bold, _skipFirst }) { cb.Checked += (_, _) => Changed(); cb.Unchecked += (_, _) => Changed(); }
        foreach (var sl in new[] { _markWidth, _markOpacity }) sl.ValueChanged += (_, _) => Changed();
        foreach (var r in new[] { _diagonal, _straight, _allPages, _somePages }) r.Checked += (_, _) => Changed();
        _delay.Tick += (_, _) => { _delay.Stop(); Refresh(); };
        _go.Click += (_, _) => { if (TryRead(out var l, out var m, out int f, out int t, out _)) { Line = l; Mark = m; FirstPage = f; LastPage = t; DialogResult = true; } };
        _cancel.Click += (_, _) => DialogResult = false;
        Loaded += (_, _) => Refresh();
    }

    /// <summary>A small coloured square that can be chosen.</summary>
    private RadioButton ColorChip(Color c, List<(RadioButton, Color)> into, string group, bool chosen)
    {
        var r = new RadioButton { Content = new TextBlock { Text = "■", FontSize = 17, Foreground = new SolidColorBrush(c) }, Style = (Style)Application.Current.FindResource("ChipButton"), GroupName = group, IsChecked = chosen, MinWidth = 38, Padding = new Thickness(6, 2, 6, 2) };
        r.Checked += (_, _) => Changed();
        into.Add((r, c));
        return r;
    }

    /// <summary>Three chips per row.</summary>
    private sealed class UniformGrid3 : System.Windows.Controls.Primitives.UniformGrid { public UniformGrid3() { Columns = 3; } }

    private void ChoosePicture()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose the picture for the watermark", Filter = "Pictures|" + string.Join(";", PdfCombiner.PictureExtensions.Select(e => "*." + e)) };
        if (dlg.ShowDialog(this) != true) { if (_picture == null) _kindText.IsChecked = true; return; }
        try { _picture = PdfCombiner.LoadPicture(File.ReadAllBytes(dlg.FileName), 1600); _pictureName.Text = Path.GetFileName(dlg.FileName); }
        catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException or OverflowException)
        {
            _problem.Text = "That picture can't be read: " + e.Message;
            if (_picture == null) _kindText.IsChecked = true;
        }
        Changed();
    }

    private void Changed() { if (_delay != null) { _delay.Stop(); _delay.Start(); } }

    private bool Number(TextBox box, out double value) => double.TryParse(box.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>What the controls say now. False (with the reason shown) when something isn't usable yet.</summary>
    private bool TryRead(out PdfHeaderFooter? line, out PdfWatermark? mark, out int first, out int last, out string problem)
    {
        line = null; mark = null; first = 0; last = _count - 1; problem = "";
        if (_somePages.IsChecked == true)
        {
            if (!int.TryParse(_from.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int a) || !int.TryParse(_to.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int b) || a < 1 || b < 1 || a > _count || b > _count)
                problem = $"Type the pages as numbers from 1 to {_count}.";
            else { first = Math.Min(a, b) - 1; last = Math.Max(a, b) - 1; }
        }
        if (problem.Length == 0 && _useLine.IsChecked == true)
        {
            if (_text.Text.Trim().Length == 0) problem = "Type the text (for page numbers: Page {n} of {total}).";
            else if (!Number(_size, out double size) || size < 4 || size > 120) problem = "The size should be a number between 4 and 120.";
            else if (!int.TryParse(_start.Text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int start) || start < 0 || start > 100000) problem = "The first page number should be a whole number (0 or more).";
            else
                line = new PdfHeaderFooter
                {
                    Text = _text.Text, Spot = _spots.First(s => s.Chip.IsChecked == true).Spot, Font = _fonts.First(f => f.Chip.IsChecked == true).Kind, Bold = _bold.IsChecked == true,
                    Size = size, Color = _lineColors.First(c => c.Chip.IsChecked == true).Color, Start = start, SkipFirst = _skipFirst.IsChecked == true,
                };
        }
        if (problem.Length == 0 && _useMark.IsChecked == true)
        {
            bool picture = _kindPicture.IsChecked == true;
            if (picture && _picture == null) problem = "Choose the picture for the watermark.";
            else if (!picture && _markText.Text.Trim().Length == 0) problem = "Type the words of the watermark.";
            else
                mark = new PdfWatermark
                {
                    Text = _markText.Text, Picture = picture ? _picture : null, Width = _markWidth.Value / 100, Opacity = _markOpacity.Value / 100,
                    Angle = _diagonal.IsChecked == true ? -45 : 0, Color = _markColors.First(c => c.Chip.IsChecked == true).Color,
                };
        }
        if (problem.Length == 0 && line == null && mark == null) problem = "Tick what to add: text at the top or bottom, or a watermark.";
        return problem.Length == 0;
    }

    private void Refresh()
    {
        bool ok = TryRead(out var line, out var mark, out int first, out int last, out string problem);
        _linePanel.IsEnabled = _useLine.IsChecked == true; _linePanel.Opacity = _linePanel.IsEnabled ? 1 : 0.45;
        _markPanel.IsEnabled = _useMark.IsChecked == true; _markPanel.Opacity = _markPanel.IsEnabled ? 1 : 0.45;
        _problem.Text = problem;
        _go.IsEnabled = ok;
        _previewPage = Math.Clamp(_previewPage, 0, _count - 1);
        _previewLabel.Text = $"page {_previewPage + 1} of {_count}";

        var size = _pdf.PageSize(_previewPage);
        double k = PreviewWidth / size.Width;
        _previewBox.Width = PreviewWidth; _previewBox.Height = Math.Round(size.Height * k);
        if (!_renders.TryGetValue(_previewPage, out var picture))
        {
            try { picture = _pdf.Render(_previewPage, (int)(PreviewWidth * 2), (int)Math.Round(size.Height * k * 2), 0, forScreen: false); _renders[_previewPage] = picture; }
            catch (Exception e) when (e is IOException or OutOfMemoryException or ObjectDisposedException) { picture = null!; }
        }
        _previewImage.Source = picture;
        _previewMarks.Children.Clear();
        _previewMarks.Width = PreviewWidth; _previewMarks.Height = _previewBox.Height;
        if (!ok || _previewPage < first || _previewPage > last) return;                // (this page is outside the chosen pages: nothing is added to it)
        foreach (var m in PdfPageMarks.Build(_count, _pdf.PageSize, _stem, first, last, line, mark, _previewPage))
        {
            switch (m)
            {
                case PdfTextMark t:
                {
                    var tb = new TextBlock { Text = t.Text, FontFamily = PdfPageMarks.Family(t.Font), FontSize = t.Size * k, FontWeight = t.Bold ? FontWeights.Bold : FontWeights.Normal, Foreground = new SolidColorBrush(t.Color) };
                    Canvas.SetLeft(tb, t.TopLeft.X * k); Canvas.SetTop(tb, t.TopLeft.Y * k);
                    if (t.Angle != 0 && t.Pivot is Point p) tb.RenderTransform = new RotateTransform(t.Angle, (p.X - t.TopLeft.X) * k, (p.Y - t.TopLeft.Y) * k);
                    _previewMarks.Children.Add(tb);
                    break;
                }
                case PdfImageMark i when i.Pixels != null:
                {
                    var img = new Image { Source = i.Pixels, Width = i.Box.Width * k, Height = i.Box.Height * k, Stretch = System.Windows.Media.Stretch.Fill };
                    Canvas.SetLeft(img, i.Box.X * k); Canvas.SetTop(img, i.Box.Y * k);
                    _previewMarks.Children.Add(img);
                    break;
                }
            }
        }
    }
}
