using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>"Crop pages": cut the same amount off every edge you choose, or trim the empty edges of each page; with a preview, and which pages.</summary>
public sealed class PdfCropDialog : Window
{
    private readonly PdfFile _pdf;
    private readonly int _count, _current;
    private readonly (Grid Box, Image Picture, Canvas Overlay) _preview;
    private readonly Slider _top = Bar(), _bottom = Bar(), _left = Bar(), _right = Bar(), _pad = PdfDialogKit.Bar(0, 40, 6, 200);
    private readonly TextBlock _topText = new(), _bottomText = new(), _leftText = new(), _rightText = new(), _padText = new();
    private readonly CheckBox _linked = new() { Content = "Move all four together", Margin = new Thickness(0, 8, 0, 0) };
    private RadioButton _manual = null!, _trim = null!;
    private readonly StackPanel _manualPanel = new(), _trimPanel = new();
    private readonly List<(RadioButton Chip, string Which)> _which = new();
    private readonly TextBox _from = new() { Width = 52, Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    private readonly TextBox _to = new() { Width = 52, Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _problem = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, MinHeight = 20, Margin = new Thickness(0, 8, 0, 0) };
    private const double PreviewWidth = 280;
    private const double MmToPt = 72 / 25.4;
    private bool _syncing;

    /// <summary>true = every page is trimmed to its own content; false = the margins below are cut from every chosen page.</summary>
    public bool Trim { get; private set; }
    public PdfPageTools.CropMargins Margins { get; private set; }
    /// <summary>Space left around the content when trimming, points.</summary>
    public double Pad { get; private set; }
    public List<int> Pages { get; private set; } = new();

    private static Slider Bar() => PdfDialogKit.Bar(0, 100, 0, 200);

    public PdfCropDialog(Window owner, PdfFile pdf, int currentPage)
    {
        _pdf = pdf; _count = pdf.PageCount; _current = Math.Clamp(currentPage, 0, _count - 1);
        PdfDialogKit.Setup(this, owner, "Crop pages", 760);
        var size = pdf.PageSize(_current);
        _preview = PdfDialogKit.PagePreview(PreviewWidth, Math.Round(PreviewWidth * size.Height / size.Width));
        _preview.Box.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 1, Opacity = 0.3 };
        _from.Text = "1"; _to.Text = _count.ToString(CultureInfo.InvariantCulture);
        foreach (var s in new[] { _top, _bottom }) s.Maximum = Math.Floor(size.Height / MmToPt * 0.45);
        foreach (var s in new[] { _left, _right }) s.Maximum = Math.Floor(size.Width / MmToPt * 0.45);

        var left = new StackPanel { Width = 380, Margin = new Thickness(0, 0, 22, 0) };
        left.Children.Add(PdfDialogKit.Heading("How", 0));
        var how = new WrapPanel();
        _manual = PdfDialogKit.Chip("Cut from the edges", "cropmode", true, () => { UpdateMode(); Changed(); }, "PdfCropManual");
        _trim = PdfDialogKit.Chip("Trim the empty edges", "cropmode", false, () => { UpdateMode(); Changed(); }, "PdfCropTrim");
        how.Children.Add(_manual); how.Children.Add(_trim);
        left.Children.Add(how);

        _manualPanel.Children.Add(PdfDialogKit.LabelRow("Top", _top, _topText, 70));
        _manualPanel.Children.Add(PdfDialogKit.LabelRow("Bottom", _bottom, _bottomText, 70));
        _manualPanel.Children.Add(PdfDialogKit.LabelRow("Left", _left, _leftText, 70));
        _manualPanel.Children.Add(PdfDialogKit.LabelRow("Right", _right, _rightText, 70));
        _manualPanel.Children.Add(_linked);
        left.Children.Add(_manualPanel);

        _trimPanel.Children.Add(PdfDialogKit.Muted("Each chosen page is cut down to what is on it (text, pictures, lines). A page with nothing on it is left as it is.", 12));
        _trimPanel.Children.Add(PdfDialogKit.LabelRow("Space around", _pad, _padText, 110));
        _trimPanel.Visibility = Visibility.Collapsed;
        left.Children.Add(_trimPanel);

        left.Children.Add(PdfDialogKit.Heading("Pages"));
        var pages = new WrapPanel();
        foreach (var (label, which) in new[] { ("All pages", "all"), ("This page", "this"), ("Odd pages", "odd"), ("Even pages", "even"), ("From…", "range") })
        {
            var chip = PdfDialogKit.Chip(label, "croppages", which == "all", () => { UpdateRangeBoxes(); Changed(); }, "PdfCropPages" + which);
            _which.Add((chip, which)); pages.Children.Add(chip);
        }
        left.Children.Add(pages);
        var range = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        range.Children.Add(new TextBlock { Text = "page", VerticalAlignment = VerticalAlignment.Center });
        range.Children.Add(_from);
        range.Children.Add(new TextBlock { Text = "to", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        range.Children.Add(_to);
        range.Children.Add(new TextBlock { Text = $"of {_count}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Opacity = 0.7 });
        left.Children.Add(range);
        left.Children.Add(_problem);
        _problem.SetResourceReference(TextBlock.ForegroundProperty, "ErrBrush");

        foreach (var s in new[] { _top, _bottom, _left, _right }) s.ValueChanged += (_, _) => { LinkAll(s); Changed(); };
        _pad.ValueChanged += (_, _) => Changed();
        _from.TextChanged += (_, _) => Changed(); _to.TextChanged += (_, _) => Changed();

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        right.Children.Add(PdfDialogKit.Muted("Preview (page " + (_current + 1) + ")", 12));
        right.Children.Add(new Border { Margin = new Thickness(0, 6, 0, 0), Child = _preview.Box });
        right.Children.Add(PdfDialogKit.Muted("The darker part is cut off. It is hidden in every PDF reader, but it stays inside the file; to remove something for good, use Redact.", 12));
        ((TextBlock)right.Children[^1]).Margin = new Thickness(0, 10, 0, 0);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 1);
        body.Children.Add(left); body.Children.Add(right);

        var go = new Button { Content = "Crop", MinWidth = 150 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(go, "PdfCropGo");
        go.Click += (_, _) => { if (Collect()) DialogResult = true; };
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(body);
        root.Children.Add(PdfDialogKit.Buttons(go, new Button { Content = "Cancel", MinWidth = 96 }));
        Content = root;

        try { _preview.Picture.Source = pdf.Render(_current, (int)(PreviewWidth * 2), (int)(_preview.Box.Height * 2)); } catch (Exception e) when (e is System.IO.IOException or ObjectDisposedException or OutOfMemoryException) { }
        UpdateRangeBoxes();
        Loaded += (_, _) => Changed();
    }

    private void LinkAll(Slider moved)
    {
        if (_syncing || _linked.IsChecked != true) return;
        _syncing = true;
        try { foreach (var s in new[] { _top, _bottom, _left, _right }) if (s != moved) s.Value = Math.Min(s.Maximum, moved.Value); }
        finally { _syncing = false; }
    }

    private void UpdateMode()
    {
        bool trim = _trim.IsChecked == true;
        _manualPanel.Visibility = trim ? Visibility.Collapsed : Visibility.Visible;
        _trimPanel.Visibility = trim ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateRangeBoxes() => _from.IsEnabled = _to.IsEnabled = _which.First(w => w.Which == "range").Chip.IsChecked == true;

    private PdfPageTools.CropMargins ManualMargins() => new(Math.Round(_left.Value) * MmToPt, Math.Round(_top.Value) * MmToPt, Math.Round(_right.Value) * MmToPt, Math.Round(_bottom.Value) * MmToPt);

    private void Changed()
    {
        if (_syncing || _manual == null || _trim == null) return;
        _problem.Text = "";
        _topText.Text = Math.Round(_top.Value) + " mm"; _bottomText.Text = Math.Round(_bottom.Value) + " mm";
        _leftText.Text = Math.Round(_left.Value) + " mm"; _rightText.Text = Math.Round(_right.Value) + " mm";
        _padText.Text = Math.Round(_pad.Value) + " mm";
        var page = _pdf.PageSize(_current);
        PdfPageTools.CropMargins? cut;
        if (_trim.IsChecked == true)
        {
            try { cut = PdfPageTools.ContentMargins(_pdf, _current, Math.Round(_pad.Value) * MmToPt); }
            catch (Exception e) when (e is System.IO.IOException or ObjectDisposedException or OutOfMemoryException) { cut = null; }
        }
        else cut = ManualMargins();
        _preview.Overlay.Children.Clear();
        if (cut is not PdfPageTools.CropMargins m) return;
        double k = PreviewWidth / page.Width, w = page.Width * k, h = page.Height * k;
        double l = m.Left * k, t = m.Top * k, r = m.Right * k, b = m.Bottom * k;
        var shade = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        void Cover(double x, double y, double cw, double ch)
        {
            if (cw <= 0 || ch <= 0) return;
            var rect = new Rectangle { Width = cw, Height = ch, Fill = shade };
            Canvas.SetLeft(rect, x); Canvas.SetTop(rect, y);
            _preview.Overlay.Children.Add(rect);
        }
        Cover(0, 0, w, t); Cover(0, h - b, w, b); Cover(0, t, l, h - t - b); Cover(w - r, t, r, h - t - b);
        var frame = new Rectangle { Width = Math.Max(1, w - l - r), Height = Math.Max(1, h - t - b), Stroke = new SolidColorBrush(Color.FromRgb(0x2F, 0x6B, 0xEA)), StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 4, 3 } };
        Canvas.SetLeft(frame, l); Canvas.SetTop(frame, t);
        _preview.Overlay.Children.Add(frame);
        if (w - l - r < PdfPageTools.MinCrop * k || h - t - b < PdfPageTools.MinCrop * k) _problem.Text = "That would leave almost nothing of the page. Cut less.";
    }

    private bool Collect()
    {
        string which = _which.First(w => w.Chip.IsChecked == true).Which;
        IEnumerable<int> pages;
        switch (which)
        {
            case "this": pages = new[] { _current }; break;
            case "odd": pages = Enumerable.Range(0, _count).Where(i => i % 2 == 0); break;
            case "even": pages = Enumerable.Range(0, _count).Where(i => i % 2 == 1); break;
            case "range":
                if (!int.TryParse(_from.Text, out int a) || !int.TryParse(_to.Text, out int z) || a < 1 || z < a || z > _count) { _problem.Text = $"Type pages from 1 to {_count}, the first not after the last."; return false; }
                pages = Enumerable.Range(a - 1, z - a + 1); break;
            default: pages = Enumerable.Range(0, _count); break;
        }
        Pages = pages.ToList();
        if (Pages.Count == 0) { _problem.Text = "No pages match."; return false; }
        Trim = _trim.IsChecked == true;
        Pad = Math.Round(_pad.Value) * MmToPt;
        Margins = ManualMargins();
        if (!Trim && Margins.Left + Margins.Top + Margins.Right + Margins.Bottom < 1) { _problem.Text = "Move a slider to say how much to cut."; return false; }
        return true;
    }
}
