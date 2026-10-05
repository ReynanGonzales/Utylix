using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>"Border around the pages": colour, thickness, distance from the edge, solid / dashed / double, square or rounded corners, and which pages, with a preview.</summary>
public sealed class PdfBorderDialog : Window
{
    private readonly PdfFile _pdf;
    private readonly int _count, _current;
    private readonly (Grid Box, Image Picture, Canvas Overlay) _preview;
    private readonly Slider _width = PdfDialogKit.Bar(0.5, 12, 2), _margin = PdfDialogKit.Bar(6, 90, 24);
    private readonly TextBlock _widthText = new(), _marginText = new();
    private readonly List<(RadioButton Chip, Color Color)> _colors = new();
    private readonly List<(RadioButton Chip, PdfBorderStyle Style)> _styles = new();
    private RadioButton _square = null!, _rounded = null!;
    private readonly List<(RadioButton Chip, string Which)> _which = new();
    private readonly TextBox _from = new() { Width = 52, Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    private readonly TextBox _to = new() { Width = 52, Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 0) };
    private readonly TextBlock _problem = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, MinHeight = 20, Margin = new Thickness(0, 8, 0, 0) };
    private const double PreviewWidth = 280;

    public PdfBorder Border { get; private set; } = new();
    public List<int> Pages { get; private set; } = new();

    public PdfBorderDialog(Window owner, PdfFile pdf, int currentPage)
    {
        _pdf = pdf; _count = pdf.PageCount; _current = Math.Clamp(currentPage, 0, _count - 1);
        PdfDialogKit.Setup(this, owner, "Border around the pages", 760);
        var size = pdf.PageSize(_current);
        _preview = PdfDialogKit.PagePreview(PreviewWidth, Math.Round(PreviewWidth * size.Height / size.Width));
        _preview.Box.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 1, Opacity = 0.3 };
        _from.Text = "1"; _to.Text = _count.ToString(CultureInfo.InvariantCulture);

        var left = new StackPanel { Width = 380, Margin = new Thickness(0, 0, 22, 0) };
        left.Children.Add(PdfDialogKit.Heading("Colour", 0));
        var colours = new WrapPanel();
        foreach (var c in new[] { Colors.Black, Color.FromRgb(0x55, 0x55, 0x55), Color.FromRgb(0x10, 0x2A, 0x8C), Color.FromRgb(0x1E, 0x6F, 0xE0), Color.FromRgb(0xD3, 0x2F, 0x2F), Color.FromRgb(0x2E, 0x7D, 0x32), Color.FromRgb(0xB8, 0x86, 0x0B) })
        {
            var chip = PdfDialogKit.Swatch(c, "bordercolor", c == Colors.Black, Changed);
            _colors.Add((chip, c)); colours.Children.Add(chip);
        }
        left.Children.Add(colours);

        left.Children.Add(PdfDialogKit.Heading("Line"));
        var styles = new WrapPanel();
        foreach (var (label, style) in new[] { ("Solid", PdfBorderStyle.Solid), ("Dashed", PdfBorderStyle.Dashed), ("Double", PdfBorderStyle.Double) })
        {
            var chip = PdfDialogKit.Chip(label, "borderstyle", style == PdfBorderStyle.Solid, Changed, "PdfBorderStyle" + style);
            _styles.Add((chip, style)); styles.Children.Add(chip);
        }
        left.Children.Add(styles);
        var corners = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        _square = PdfDialogKit.Chip("Square corners", "bordercorner", true, Changed);
        _rounded = PdfDialogKit.Chip("Rounded corners", "bordercorner", false, Changed, "PdfBorderRounded");
        corners.Children.Add(_square); corners.Children.Add(_rounded);
        left.Children.Add(corners);
        left.Children.Add(PdfDialogKit.LabelRow("Thickness", _width, _widthText, 170));
        left.Children.Add(PdfDialogKit.LabelRow("Distance from the edge", _margin, _marginText, 170));
        _width.ValueChanged += (_, _) => Changed();
        _margin.ValueChanged += (_, _) => Changed();

        left.Children.Add(PdfDialogKit.Heading("Pages"));
        var pages = new WrapPanel();
        foreach (var (label, which) in new[] { ("All pages", "all"), ("This page", "this"), ("Odd pages", "odd"), ("Even pages", "even"), ("From…", "range") })
        {
            var chip = PdfDialogKit.Chip(label, "borderpages", which == "all", () => { UpdateRangeBoxes(); Changed(); }, "PdfBorderPages" + which);
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
        _from.TextChanged += (_, _) => Changed(); _to.TextChanged += (_, _) => Changed();
        left.Children.Add(_problem);

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        right.Children.Add(PdfDialogKit.Muted("Preview (page " + (_current + 1) + ")", 12));
        var holder = new Border { Margin = new Thickness(0, 6, 0, 0), Child = _preview.Box };
        right.Children.Add(holder);
        right.Children.Add(PdfDialogKit.Muted("The border is drawn on top of the page, as a real line in the PDF. Undo takes it away until you save.", 12));
        ((TextBlock)right.Children[^1]).Margin = new Thickness(0, 10, 0, 0);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 1);
        body.Children.Add(left); body.Children.Add(right);

        var go = new Button { Content = "Add the border", MinWidth = 150 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(go, "PdfBorderAdd");
        go.Click += (_, _) => { if (Collect()) DialogResult = true; };
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(body);
        root.Children.Add(PdfDialogKit.Buttons(go, new Button { Content = "Cancel", MinWidth = 96 }));
        Content = root;

        try { _preview.Picture.Source = pdf.Render(_current, (int)(PreviewWidth * 2), (int)(_preview.Box.Height * 2)); } catch (Exception e) when (e is System.IO.IOException or ObjectDisposedException or OutOfMemoryException) { }
        UpdateRangeBoxes();
        Loaded += (_, _) => Changed();
    }

    private void UpdateRangeBoxes() => _from.IsEnabled = _to.IsEnabled = _which.First(w => w.Which == "range").Chip.IsChecked == true;

    private PdfBorder Current() => new()
    {
        Color = _colors.First(c => c.Chip.IsChecked == true).Color,
        Style = _styles.First(s => s.Chip.IsChecked == true).Style,
        Rounded = _rounded.IsChecked == true,
        Width = Math.Round(_width.Value * 2) / 2,
        Margin = Math.Round(_margin.Value),
    };

    private void Changed()
    {
        if (_styles.Count == 0 || _colors.Count == 0 || _rounded == null) return;
        var b = Current();
        _widthText.Text = b.Width.ToString("0.#", CultureInfo.InvariantCulture) + " pt";
        _marginText.Text = b.Margin.ToString("0", CultureInfo.InvariantCulture) + " pt";
        var page = _pdf.PageSize(_current);
        double k = PreviewWidth / page.Width;
        _preview.Overlay.Children.Clear();
        foreach (var fig in PdfBorders.Figures(page, b))
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(fig.Start.X * k, fig.Start.Y * k), false, fig.Closed);
                foreach (var s in fig.Segments)
                {
                    if (s.Curve) ctx.BezierTo(new Point(s.C1.X * k, s.C1.Y * k), new Point(s.C2.X * k, s.C2.Y * k), new Point(s.To.X * k, s.To.Y * k), true, false);
                    else ctx.LineTo(new Point(s.To.X * k, s.To.Y * k), true, false);
                }
            }
            geo.Freeze();
            _preview.Overlay.Children.Add(new System.Windows.Shapes.Path { Data = geo, Stroke = new SolidColorBrush(b.Color), StrokeThickness = Math.Max(0.8, b.Width * k), StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Flat, StrokeEndLineCap = PenLineCap.Flat });
        }
    }

    private bool Collect()
    {
        string which = _which.First(w => w.Chip.IsChecked == true).Which;
        IEnumerable<int> pages;
        switch (which)
        {
            case "this": pages = new[] { _current }; break;
            case "odd": pages = Enumerable.Range(0, _count).Where(i => i % 2 == 0); break;          // (page 1, 3, 5 ...)
            case "even": pages = Enumerable.Range(0, _count).Where(i => i % 2 == 1); break;
            case "range":
                if (!int.TryParse(_from.Text, out int a) || !int.TryParse(_to.Text, out int z) || a < 1 || z < a || z > _count) { _problem.Text = $"Type pages from 1 to {_count}, the first not after the last."; return false; }
                pages = Enumerable.Range(a - 1, z - a + 1); break;
            default: pages = Enumerable.Range(0, _count); break;
        }
        Pages = pages.ToList();
        if (Pages.Count == 0) { _problem.Text = "No pages match."; return false; }
        Border = Current();
        return true;
    }
}
