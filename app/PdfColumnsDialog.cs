using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>"Text in columns": the words, how many columns, the space between them, how wide the block is, and the font; with a preview of the page.</summary>
public sealed class PdfColumnsDialog : Window
{
    private readonly ColumnsSpec _spec;
    private readonly double _pageWidth;
    private readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, MaxHeight = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8, 6, 8, 6), VerticalContentAlignment = VerticalAlignment.Top };
    private readonly Slider _gap = PdfDialogKit.Bar(6, 60, 18), _width = PdfDialogKit.Bar(25, 100, 80), _size = PdfDialogKit.Bar(7, 28, 11);
    private readonly TextBlock _gapText = new(), _widthText = new(), _sizeText = new();
    private readonly List<(RadioButton Chip, int Count)> _counts = new();
    private readonly List<(RadioButton Chip, PdfFontKind Kind)> _fonts = new();
    private readonly CheckBox _bold = new() { Content = "Bold", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly List<(RadioButton Chip, Color Color)> _colors = new();
    private readonly (Grid Box, Image Picture, Canvas Overlay) _preview;
    private const double PreviewWidth = 300;
    private bool _ready;

    public ColumnsSpec Spec { get; private set; }

    public PdfColumnsDialog(Window owner, ColumnsSpec start, double pageWidth, bool editing)
    {
        _spec = start.Clone(); Spec = _spec; _pageWidth = pageWidth;
        PdfDialogKit.Setup(this, owner, "Text in columns", 780);
        _preview = PdfDialogKit.PagePreview(PreviewWidth, 330);
        _preview.Box.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 1, Opacity = 0.3 };
        _text.Text = _spec.Text;
        _text.SetResourceReference(TextBox.BackgroundProperty, "CardBrush");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_text, "PdfColumnsText");

        var left = new StackPanel { Width = 400, Margin = new Thickness(0, 0, 22, 0) };
        left.Children.Add(PdfDialogKit.Heading("The words", 0));
        left.Children.Add(_text);
        left.Children.Add(PdfDialogKit.Muted("An empty line starts a new paragraph. The text flows from one column to the next, shared out evenly."));
        ((TextBlock)left.Children[^1]).Margin = new Thickness(0, 4, 0, 0);

        left.Children.Add(PdfDialogKit.Heading("Columns"));
        var counts = new WrapPanel();
        foreach (int n in new[] { 1, 2, 3, 4 })
        {
            var chip = PdfDialogKit.Chip(n == 1 ? "1 column" : n + " columns", "colcount", n == _spec.Columns, () => Changed(), "PdfColumns" + n);
            _counts.Add((chip, n)); counts.Children.Add(chip);
        }
        left.Children.Add(counts);
        left.Children.Add(PdfDialogKit.LabelRow("Space between", _gap, _gapText, 110));
        left.Children.Add(PdfDialogKit.LabelRow("How wide", _width, _widthText, 110));

        left.Children.Add(PdfDialogKit.Heading("Letters"));
        var fonts = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, kind) in new[] { ("Sans", PdfFontKind.Sans), ("Serif", PdfFontKind.Serif), ("Mono", PdfFontKind.Mono) })
        {
            var chip = PdfDialogKit.Chip(label, "colfont", kind == _spec.Font, () => Changed());
            chip.Content = new TextBlock { Text = label, FontFamily = PdfPageMarks.Family(kind), Foreground = (Brush)PdfDialogKit.Res("TextBrush") };
            _fonts.Add((chip, kind)); fonts.Children.Add(chip);
        }
        fonts.Children.Add(_bold);
        left.Children.Add(fonts);
        left.Children.Add(PdfDialogKit.LabelRow("Size", _size, _sizeText, 110));
        var colours = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var c in new[] { Colors.Black, Color.FromRgb(0x55, 0x55, 0x55), Color.FromRgb(0x10, 0x2A, 0x8C), Color.FromRgb(0xD3, 0x2F, 0x2F), Color.FromRgb(0x2E, 0x7D, 0x32) })
        {
            var chip = PdfDialogKit.Swatch(c, "colcolor", c == _spec.Color || (c == Colors.Black && !new[] { Color.FromRgb(0x55, 0x55, 0x55), Color.FromRgb(0x10, 0x2A, 0x8C), Color.FromRgb(0xD3, 0x2F, 0x2F), Color.FromRgb(0x2E, 0x7D, 0x32) }.Contains(_spec.Color)), () => Changed());
            _colors.Add((chip, c)); colours.Children.Add(chip);
        }
        left.Children.Add(colours);

        _gap.Value = _spec.Gap; _width.Value = Math.Clamp(_spec.Width / _pageWidth * 100, 25, 100); _size.Value = _spec.Size; _bold.IsChecked = _spec.Bold;
        _gap.ValueChanged += (_, _) => Changed(); _width.ValueChanged += (_, _) => Changed(); _size.ValueChanged += (_, _) => Changed();
        _bold.Click += (_, _) => Changed(); _text.TextChanged += (_, _) => Changed();

        var right = new StackPanel();
        right.Children.Add(PdfDialogKit.Muted("Preview (the block, a little larger than on the page)"));
        right.Children.Add(new Border { Margin = new Thickness(0, 6, 0, 0), Child = _preview.Box });

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 1);
        body.Children.Add(left); body.Children.Add(right);

        var go = new Button { Content = editing ? "Save the changes" : "Add to the page", MinWidth = 150 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(go, "PdfColumnsAdd");
        go.Click += (_, _) => { Collect(); if (_spec.Text.Trim().Length > 0) DialogResult = true; };
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(body);
        root.Children.Add(PdfDialogKit.Buttons(go, new Button { Content = "Cancel", MinWidth = 96 }));
        Content = root;
        _ready = true;
        Loaded += (_, _) => { Changed(); _text.Focus(); _text.CaretIndex = _text.Text.Length; };
    }

    private void Collect()
    {
        _spec.Text = _text.Text;
        _spec.Columns = _counts.FirstOrDefault(c => c.Chip.IsChecked == true).Count is int n and > 0 ? n : 2;
        _spec.Gap = Math.Round(_gap.Value);
        _spec.Width = Math.Round(_pageWidth * _width.Value / 100);
        _spec.Font = _fonts.FirstOrDefault(f => f.Chip.IsChecked == true).Kind;
        _spec.FontName = null;
        _spec.Bold = _bold.IsChecked == true;
        _spec.Size = Math.Round(_size.Value * 2) / 2;
        _spec.Color = _colors.FirstOrDefault(c => c.Chip.IsChecked == true).Color;
        if (_spec.Color == default) _spec.Color = Colors.Black;
    }

    private void Changed()
    {
        if (!_ready) return;
        Collect();
        _gapText.Text = _spec.Gap.ToString("0", CultureInfo.InvariantCulture) + " pt";
        _widthText.Text = $"{_width.Value:0} %";
        _sizeText.Text = _spec.Size.ToString("0.#", CultureInfo.InvariantCulture) + " pt";
        var shown = _spec.Clone();
        if (shown.Text.Trim().Length == 0) shown.Text = "Type the words above and they appear here, flowing from one column into the next like in a newspaper or a Word document.";
        double k = (PreviewWidth - 36) / Math.Max(60, shown.Width);
        var (columns, height, _) = PdfColumns.Flow(shown);
        double colW = PdfColumns.ColumnWidth(shown);
        _preview.Overlay.Children.Clear();
        _preview.Box.Height = Math.Max(100, Math.Min(560, height * k + 36));
        double x0 = 18;
        for (int c = 0; c < columns.Count; c++)
        {
            var t = new TextBlock
            {
                Text = string.Join("\n", columns[c]), FontFamily = PdfColumns.Family(shown), FontSize = shown.Size * k, FontWeight = shown.Bold ? FontWeights.Bold : FontWeights.Normal,
                Foreground = new SolidColorBrush(shown.Color),
            };
            Canvas.SetLeft(t, x0 + c * (colW + shown.Gap) * k); Canvas.SetTop(t, 18);
            _preview.Overlay.Children.Add(t);
        }
    }
}
