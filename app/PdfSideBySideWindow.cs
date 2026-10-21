using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Two PDFs next to each other that scroll (and zoom) together, to read a new version against the old one. Each is opened as a copy of its own, so nothing is changed;
/// only the pages near the screen are drawn.
/// </summary>
public sealed class PdfSideBySideWindow : Window
{
    private sealed class Pane
    {
        public string Path = "";
        public PdfFile? Pdf;
        public readonly ScrollViewer Scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        public readonly StackPanel Column = new() { Margin = new Thickness(12, 12, 12, 4) };
        public readonly TextBlock Message = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, TextAlignment = TextAlignment.Center };
        public readonly List<Border> Pages = new();
        public Size[] Sizes = Array.Empty<Size>();
        public readonly HashSet<int> Drawn = new();
        public readonly HashSet<int> Drawing = new();
        public int Generation;
    }

    private readonly Pane _a = new(), _b = new();
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private readonly CheckBox _together = new() { Content = "Scroll together", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
    private double _zoom = 1;
    private bool _syncing, _closed;

    public static void Start(Window owner, string first, string second) => new PdfSideBySideWindow(first, second) { Owner = owner }.Show();

    private PdfSideBySideWindow(string first, string second)
    {
        _a.Path = first; _b.Path = second;
        Title = $"Side by side: {System.IO.Path.GetFileName(first)}  and  {System.IO.Path.GetFileName(second)}";
        Width = 1300; Height = 800; MinWidth = 700; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("BgBrush"); Foreground = (Brush)Application.Current.FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        WindowTheme.DarkTitleBar(this);

        Button Small(string text, string tip, string id, Action click)
        {
            var b = new Button { Content = text, ToolTip = tip, Style = (Style)Application.Current.FindResource("DialogButton"), Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(0, 0, 6, 0), MinWidth = 36 };
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, id);
            b.Click += (_, _) => click();
            return b;
        }
        System.Windows.Automation.AutomationProperties.SetAutomationId(_together, "PdfSideTogether");
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 8, 12, 8) };
        bar.Children.Add(_together);
        bar.Children.Add(Small("−", "Smaller", "PdfSideZoomOut", () => SetZoom(_zoom / 1.2)));
        bar.Children.Add(Small("+", "Bigger", "PdfSideZoomIn", () => SetZoom(_zoom * 1.2)));
        bar.Children.Add(Small("Fit", "Fit each PDF to its side", "PdfSideFit", () => SetZoom(1)));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left = Side(_a, 0); var right = Side(_b, 2);
        grid.Children.Add(left); grid.Children.Add(right);
        var divider = new Border { Background = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)) }; Grid.SetColumn(divider, 1); grid.Children.Add(divider);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar);
        root.Children.Add(grid);
        Content = root;

        _renderTimer.Tick += (_, _) => { _renderTimer.Stop(); DrawVisible(_a); DrawVisible(_b); };
        SizeChanged += (_, _) => { LayoutPane(_a); LayoutPane(_b); };
        Closed += (_, _) => { _closed = true; _renderTimer.Stop(); Release(_a); Release(_b); };
        Loaded += async (_, _) => await Task.WhenAll(Load(_a), Load(_b));
    }

    private FrameworkElement Side(Pane pane, int column)
    {
        var name = new TextBlock { Text = System.IO.Path.GetFileName(pane.Path), FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 6, 12, 6), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = pane.Path };
        var dock = new DockPanel(); Grid.SetColumn(dock, column);
        DockPanel.SetDock(name, Dock.Top); dock.Children.Add(name);
        pane.Message.Text = "Opening…";
        pane.Scroll.Content = pane.Column;
        var host = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2F, 0x3A)) };
        host.Children.Add(pane.Scroll); host.Children.Add(pane.Message);
        dock.Children.Add(host);
        pane.Scroll.ScrollChanged += (_, e) =>
        {
            if (e.VerticalChange != 0 && _together.IsChecked == true && !_syncing)
            {
                var other = pane == _a ? _b : _a;
                _syncing = true;
                double fraction = pane.Scroll.ScrollableHeight > 0 ? pane.Scroll.VerticalOffset / pane.Scroll.ScrollableHeight : 0;
                other.Scroll.ScrollToVerticalOffset(fraction * other.Scroll.ScrollableHeight);
                _syncing = false;
            }
            if (e.ViewportWidthChange != 0) LayoutPane(pane);
            _renderTimer.Stop(); _renderTimer.Start();
        };
        return dock;
    }

    private async Task Load(Pane pane)
    {
        try
        {
            string path = pane.Path;
            var (pdf, sizes) = await Task.Run(() => { var p = PdfFile.Open(path); return (p, Enumerable.Range(0, p.PageCount).Select(p.PageSize).ToArray()); });
            if (_closed) { pdf.Dispose(); return; }
            pane.Pdf = pdf; pane.Sizes = sizes;
            pane.Message.Text = "";
            foreach (var _ in sizes) { var border = new Border { Background = Brushes.White, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Center, Child = new Image { Stretch = Stretch.Fill } }; pane.Pages.Add(border); pane.Column.Children.Add(border); }
            LayoutPane(pane);
        }
        catch (PdfPasswordException) { pane.Message.Text = "This PDF needs a password. Open it in the editor and save a copy without one."; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OutOfMemoryException or InvalidOperationException) { pane.Message.Text = "Couldn't open it:\n" + e.Message; }
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.4, 4);
        LayoutPane(_a); LayoutPane(_b);
    }

    /// <summary>Every page gets the width of its side (times the zoom) and the height its own shape needs; pictures drawn at another size are dropped and drawn again.</summary>
    private void LayoutPane(Pane pane)
    {
        if (pane.Pages.Count == 0) return;
        double width = Math.Max(120, (pane.Scroll.ViewportWidth > 0 ? pane.Scroll.ViewportWidth : ActualWidth / 2) - 36) * _zoom;
        for (int i = 0; i < pane.Pages.Count; i++)
        {
            var size = pane.Sizes[i];
            pane.Pages[i].Width = width; pane.Pages[i].Height = width * size.Height / Math.Max(1, size.Width);
            ((Image)pane.Pages[i].Child).Source = null;
        }
        pane.Drawn.Clear(); pane.Generation++;
        _renderTimer.Stop(); _renderTimer.Start();
    }

    private async void DrawVisible(Pane pane)
    {
        if (pane.Pdf == null || _closed) return;
        double view = pane.Scroll.ViewportHeight;
        int gen = pane.Generation;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        for (int i = 0; i < pane.Pages.Count; i++)
        {
            double top;
            try { top = pane.Pages[i].TranslatePoint(new Point(0, 0), pane.Scroll).Y; } catch (InvalidOperationException) { return; }
            double height = pane.Pages[i].ActualHeight;
            bool near = top + height > -view * 0.6 && top < view * 1.6;
            bool far = top + height < -view * 3 || top > view * 4;
            var image = (Image)pane.Pages[i].Child;
            if (far && pane.Drawn.Remove(i)) { image.Source = null; continue; }
            if (!near || pane.Drawn.Contains(i) || !pane.Drawing.Add(i)) continue;
            int pixelWidth = Math.Clamp((int)(pane.Pages[i].Width * scale), 50, 4000), pixelHeight = Math.Clamp((int)(pane.Pages[i].Height * scale), 50, 6000);
            int index = i; var pdf = pane.Pdf;
            try
            {
                var picture = await Task.Run(() => pdf.Render(index, pixelWidth, pixelHeight, 0, forScreen: false));
                if (gen == pane.Generation && !_closed) { image.Source = picture; pane.Drawn.Add(i); }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            finally { pane.Drawing.Remove(i); }
        }
    }

    private static void Release(Pane pane)
    {
        var pdf = pane.Pdf; pane.Pdf = null;
        if (pdf != null) Task.Run(() => { try { pdf.Dispose(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { } });
    }
}
