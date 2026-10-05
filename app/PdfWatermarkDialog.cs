using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// "Remove a watermark": looks through the pages for pieces that repeat in the same place (a big diagonal text, a logo, a form every page draws) and for
/// pieces marked as watermarks, lists them with a picture of where each one is, and the ones that are ticked are taken out of every page.
/// </summary>
public sealed class PdfWatermarkDialog : Window
{
    private readonly PdfFile _pdf;
    private readonly List<WatermarkCandidate> _found = new();
    private readonly List<CheckBox> _boxes = new();
    private readonly StackPanel _list = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly (Grid Box, Image Picture, Canvas Overlay) _preview;
    private readonly TextBlock _where = PdfDialogKit.Muted("");
    private readonly Button _go = new() { Content = "Remove the ticked ones", MinWidth = 190, IsEnabled = false };
    private const double PreviewWidth = 300;

    /// <summary>The keys of the pieces to take out.</summary>
    public List<string> Chosen { get; private set; } = new();
    public string Summary { get; private set; } = "";

    public PdfWatermarkDialog(Window owner, PdfFile pdf)
    {
        _pdf = pdf;
        PdfDialogKit.Setup(this, owner, "Remove a watermark", 800);
        _preview = PdfDialogKit.PagePreview(PreviewWidth, 400);
        _preview.Box.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 1, Opacity = 0.3 };

        _status.Text = "Looking through the pages…";
        var left = new StackPanel { Width = 400, Margin = new Thickness(0, 0, 22, 0) };
        left.Children.Add(_status);
        var scroll = new ScrollViewer { Content = _list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        left.Children.Add(scroll);
        left.Children.Add(PdfDialogKit.Muted("Only remove watermarks from documents that are yours or that you are allowed to change. A watermark that is part of a scanned picture can't be taken out cleanly: it is the picture."));
        ((TextBlock)left.Children[^1]).Margin = new Thickness(0, 12, 0, 0);

        var right = new StackPanel();
        right.Children.Add(PdfDialogKit.Muted("Where it is (click a line on the left)"));
        right.Children.Add(new Border { Margin = new Thickness(0, 6, 0, 0), Child = _preview.Box });
        _where.Margin = new Thickness(0, 6, 0, 0);
        right.Children.Add(_where);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 1);
        body.Children.Add(left); body.Children.Add(right);

        System.Windows.Automation.AutomationProperties.SetAutomationId(_go, "PdfWatermarkRemove");
        _go.Click += (_, _) =>
        {
            Chosen = _found.Where((_, i) => _boxes[i].IsChecked == true).Select(c => c.Key).ToList();
            Summary = string.Join(", ", _found.Where((_, i) => _boxes[i].IsChecked == true).Select(c => c.Title));
            if (Chosen.Count > 0) DialogResult = true;
        };
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(body);
        root.Children.Add(PdfDialogKit.Buttons(_go, new Button { Content = "Cancel", MinWidth = 96 }));
        Content = root;
        Loaded += async (_, _) => await LookAsync();
    }

    private async Task LookAsync()
    {
        List<WatermarkCandidate> found;
        try { found = await Task.Run(() => PdfWatermarks.Find(_pdf)); }
        catch (Exception e) when (e is System.IO.IOException or ObjectDisposedException or InvalidOperationException) { _status.Text = "Couldn't read the pages: " + e.Message; return; }
        _found.AddRange(found);
        if (found.Count == 0)
        {
            _status.Text = "No watermark was found. A watermark that is part of a scanned picture, or one that is different on every page, can't be told apart from the page. (Pages looked at: " + Math.Min(_pdf.PageCount, 80) + ".)";
            return;
        }
        _status.Text = found.Any(c => c.Marked)
            ? "These are marked as watermarks, or repeat in the same place on the pages. Tick what should go."
            : "These repeat in the same place on the pages. Tick what should go (a footer or a logo you want to keep is in this list too: leave it unticked).";
        foreach (var c in found)
        {
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = c.Title, TextWrapping = TextWrapping.Wrap, FontWeight = c.Marked ? FontWeights.SemiBold : FontWeights.Normal });
            text.Children.Add(PdfDialogKit.Muted((c.Marked ? "Marked as a watermark  ·  " : "") + (c.PagesLooked > 1 ? $"on {c.PagesWith} of {c.PagesLooked} pages" : "on this page")));
            var box = new CheckBox { Content = text, IsChecked = c.Marked, Margin = new Thickness(0, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Top };
            box.Checked += (_, _) => Refresh(); box.Unchecked += (_, _) => Refresh();
            var row = new Border { Padding = new Thickness(8, 7, 8, 7), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 4), BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand, Child = box };
            row.SetResourceReference(Border.BackgroundProperty, "CardBrush");
            row.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            var candidate = c;
            row.MouseLeftButtonUp += (_, _) => Show(candidate);
            box.GotFocus += (_, _) => Show(candidate);
            _boxes.Add(box); _list.Children.Add(row);
        }
        Refresh();
        Show(found[0]);
    }

    private void Refresh() => _go.IsEnabled = _boxes.Any(b => b.IsChecked == true);

    private void Show(WatermarkCandidate c)
    {
        try
        {
            var size = _pdf.PageSize(c.FirstPage);
            double h = Math.Round(PreviewWidth * size.Height / size.Width);
            _preview.Box.Height = h;
            _preview.Picture.Source = _pdf.Render(c.FirstPage, (int)(PreviewWidth * 2), (int)(h * 2));
            double k = PreviewWidth / size.Width;
            _preview.Overlay.Children.Clear();
            if (c.Kind != "annotation")
            {
                var frame = new System.Windows.Shapes.Rectangle { Width = Math.Max(6, c.Box.Width * k), Height = Math.Max(6, c.Box.Height * k), Stroke = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)), StrokeThickness = 1.6, StrokeDashArray = new DoubleCollection { 4, 2 }, Fill = new SolidColorBrush(Color.FromArgb(30, 0xE5, 0x48, 0x4D)) };
                Canvas.SetLeft(frame, c.Box.X * k); Canvas.SetTop(frame, c.Box.Y * k);
                _preview.Overlay.Children.Add(frame);
            }
            _where.Text = $"Page {c.FirstPage + 1}" + (c.Kind == "annotation" ? "" : ": the red frame");
        }
        catch (Exception e) when (e is System.IO.IOException or ObjectDisposedException or OutOfMemoryException) { }
    }
}
