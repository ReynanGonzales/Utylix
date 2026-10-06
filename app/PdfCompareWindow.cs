using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Compares two PDFs page by page: what is different is drawn in colour on the page (red = only in the first / old one, green = only in the second / new one), side by side, and as the
/// words that were taken out and put in. The list of pages shows which pages differ. Nothing is changed in either file.
/// </summary>
public sealed class PdfCompareWindow : Window
{
    private readonly PdfFile _a, _b;
    private readonly string _nameA, _nameB;
    private readonly ListBox _pages = new();
    private readonly Image _view = new() { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
    private readonly Image _viewB = new() { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
    private readonly Grid _pictureHost = new();
    private readonly ScrollViewer _textScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _words = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 22, Margin = new Thickness(18) };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly RadioButton _modeDiff, _modeSide, _modeText;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<int, string> _marks = new();
    private int _current = -1;
    private int _generation;

    private sealed record PageRow(int Index, string Label, string Mark);

    public PdfCompareWindow(Window owner, PdfFile a, string nameA, PdfFile b, string nameB)
    {
        _a = a; _b = b; _nameA = nameA; _nameB = nameB;
        Owner = owner;
        Title = "Compare PDFs - Utylix Editor";
        Width = 1180; Height = 820; MinWidth = 760; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13.5;
        WindowTheme.DarkTitleBar(this);
        try { Icon = new BitmapImage(new Uri("pack://application:,,,/pdf.ico")); } catch (Exception e) when (e is IOException or UriFormatException) { }
        var chip = (Style)Application.Current.FindResource("ChipButton");

        _modeDiff = new RadioButton { Content = "Differences", Style = chip, GroupName = "cmpmode", IsChecked = true };
        _modeSide = new RadioButton { Content = "Side by side", Style = chip, GroupName = "cmpmode" };
        _modeText = new RadioButton { Content = "Words changed", Style = chip, GroupName = "cmpmode" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_modeDiff, "CmpModeDiff");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_modeSide, "CmpModeSide");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_modeText, "CmpModeText");
        foreach (var r in new[] { _modeDiff, _modeSide, _modeText }) r.Checked += (_, _) => _ = ShowPageAsync(_current);

        // top: which files, what the colours mean, the three views
        var legend = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
        legend.Children.Add(Swatch(Color.FromRgb(0xE0, 0x3C, 0x3C)));
        legend.Children.Add(new TextBlock { Text = "only in " + Short(nameA), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 14, 0) });
        legend.Children.Add(Swatch(Color.FromRgb(0x2B, 0xA8, 0x55)));
        legend.Children.Add(new TextBlock { Text = "only in " + Short(nameB), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) });
        var modes = new StackPanel { Orientation = Orientation.Horizontal };
        modes.Children.Add(_modeDiff); modes.Children.Add(_modeSide); modes.Children.Add(_modeText); modes.Children.Add(legend);
        var head = new StackPanel { Margin = new Thickness(18, 14, 18, 8) };
        head.Children.Add(new TextBlock { Text = "Compare", FontSize = 20, FontWeight = FontWeights.SemiBold });
        head.Children.Add(new TextBlock { Text = $"{nameA}  ({a.PageCount} page{(a.PageCount == 1 ? "" : "s")})    vs    {nameB}  ({b.PageCount} page{(b.PageCount == 1 ? "" : "s")})", Opacity = 0.75, Margin = new Thickness(0, 2, 0, 10), TextTrimming = TextTrimming.CharacterEllipsis });
        head.Children.Add(modes);

        // left: the pages, with the ones that differ marked
        _pages.Width = 170;
        _pages.BorderThickness = new Thickness(0, 0, 1, 0);
        _pages.SetResourceReference(Control.BackgroundProperty, "BgBrush");
        _pages.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        _pages.SetResourceReference(Control.BorderBrushProperty, "LineBrush");
        _pages.ItemContainerStyle = (Style)System.Windows.Markup.XamlReader.Parse(
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListBoxItem'><Setter Property='Foreground' Value='{DynamicResource TextBrush}' /><Setter Property='Template'><Setter.Value>" +
            "<ControlTemplate TargetType='ListBoxItem'><Border x:Name='bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' CornerRadius='6' Margin='4,1' Background='Transparent'><ContentPresenter /></Border>" +
            "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#22808080' /></Trigger>" +
            "<Trigger Property='IsSelected' Value='True'><Setter TargetName='bd' Property='Background' Value='{DynamicResource AccentBrush}' /><Setter Property='Foreground' Value='White' /></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>");
        _pages.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><DockPanel Margin='8,4'><TextBlock DockPanel.Dock='Right' Text='{Binding Mark}' FontSize='11' Margin='10,0,0,0' VerticalAlignment='Center' /><TextBlock Text='{Binding Label}' /></DockPanel></DataTemplate>");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_pages, "CmpPages");
        int count = Math.Max(a.PageCount, b.PageCount);
        _pages.ItemsSource = Enumerable.Range(0, count).Select(i => new PageRow(i, "Page " + (i + 1), "…")).ToList();
        _pages.SelectionChanged += (_, _) => { if (_pages.SelectedIndex >= 0 && _pages.SelectedIndex != _current) _ = ShowPageAsync(_pages.SelectedIndex); };

        _pictureHost.ColumnDefinitions.Add(new ColumnDefinition());
        _pictureHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _pictureHost.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_view, 0); Grid.SetColumn(_viewB, 2);
        var divider = new Border { Width = 1, Background = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)), Margin = new Thickness(6, 0, 6, 0) };
        Grid.SetColumn(divider, 1);
        _pictureHost.Children.Add(_view); _pictureHost.Children.Add(divider); _pictureHost.Children.Add(_viewB);
        _textScroll.Content = _words;
        var stage = new Grid { Margin = new Thickness(14) };
        stage.Children.Add(_pictureHost); stage.Children.Add(_textScroll);

        var bottom = new DockPanel { Margin = new Thickness(18, 8, 18, 14) };
        var close = new Button { Content = "Close", MinWidth = 96, Style = (Style)Application.Current.FindResource("DialogButton"), IsCancel = true };
        DockPanel.SetDock(close, Dock.Right);
        close.Click += (_, _) => Close();
        bottom.Children.Add(close);
        bottom.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top); root.Children.Add(head);
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        DockPanel.SetDock(_pages, Dock.Left); root.Children.Add(_pages);
        root.Children.Add(stage);
        Content = root;

        Loaded += async (_, _) => { _pages.SelectedIndex = 0; await MarkPagesAsync(); };
        Closed += (_, _) => _cts.Cancel();
    }

    private static string Short(string name) => name.Length <= 26 ? name : name[..23] + "…";
    private static Border Swatch(Color c) => new() { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(c), VerticalAlignment = VerticalAlignment.Center };

    // ---------- what differs ----------
    private sealed record Rendered(int Width, int Height, byte[] Pixels);

    private static Rendered? RenderGrey(PdfFile pdf, int page, int width, int height)
    {
        if (page >= pdf.PageCount) return null;
        var bmp = pdf.Render(page, width, height, forScreen: false);
        int stride = width * 4;
        var pixels = new byte[stride * height];
        bmp.CopyPixels(pixels, stride, 0);
        return new Rendered(width, height, pixels);
    }

    /// <summary>Which pixels are ink (dark enough) in a rendering.</summary>
    private static bool[] Ink(Rendered r)
    {
        var ink = new bool[r.Width * r.Height];
        for (int i = 0, p = 0; i < ink.Length; i++, p += 4)
            ink[i] = (r.Pixels[p] * 114 + r.Pixels[p + 1] * 587 + r.Pixels[p + 2] * 299) / 1000 < 205;
        return ink;
    }

    /// <summary>The ink and its neighbours one pixel round it (so a letter drawn half a pixel differently does not count as a difference).</summary>
    private static bool[] Near(bool[] ink, int w, int h)
    {
        var near = new bool[ink.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (!ink[y * w + x]) continue;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx >= 0 && xx < w && yy >= 0 && yy < h) near[yy * w + xx] = true;
                    }
            }
        return near;
    }

    private sealed record PixelDiff(BitmapSource Picture, double Share, int Changed);

    private PixelDiff? DiffPixels(int page, int width)
    {
        var sizeA = page < _a.PageCount ? _a.PageSize(page) : (Size?)null;
        var sizeB = page < _b.PageCount ? _b.PageSize(page) : (Size?)null;
        var like = sizeA ?? sizeB ?? new Size(595, 842);
        int height = Math.Max(1, (int)Math.Round(width * like.Height / like.Width));
        var ra = RenderGrey(_a, page, width, height);
        var rb = RenderGrey(_b, page, width, height);
        if (ra == null && rb == null) return null;
        var inkA = ra != null ? Ink(ra) : new bool[width * height];
        var inkB = rb != null ? Ink(rb) : new bool[width * height];
        var nearA = Near(inkA, width, height);
        var nearB = Near(inkB, width, height);
        var outPixels = new byte[width * height * 4];
        var markA = new bool[inkA.Length]; var markB = new bool[inkA.Length];
        int onlyA = 0, onlyB = 0, ink = 0;
        for (int i = 0, p = 0; i < inkA.Length; i++, p += 4)
        {
            byte bl, gr, rd;
            bool a = inkA[i], b = inkB[i];
            if (a && !nearB[i]) { bl = 0x3C; gr = 0x3C; rd = 0xE0; onlyA++; ink++; markA[i] = true; }       // only in the first: red
            else if (b && !nearA[i]) { bl = 0x55; gr = 0xA8; rd = 0x2B; onlyB++; ink++; markB[i] = true; }  // only in the second: green
            else if (a || b) { bl = gr = rd = 0xA0; ink++; }                                                // in both: grey
            else { bl = gr = rd = 0xFF; }
            outPixels[p] = bl; outPixels[p + 1] = gr; outPixels[p + 2] = rd; outPixels[p + 3] = 0xFF;
        }
        // (the differences are made one pixel thicker, so they are easy to see)
        for (int pass = 0; pass < 2; pass++)
        {
            var mark = pass == 0 ? markA : markB;
            var grown = Near(mark, width, height);
            byte bl = pass == 0 ? (byte)0x3C : (byte)0x55, gr = pass == 0 ? (byte)0x3C : (byte)0xA8, rd = pass == 0 ? (byte)0xE0 : (byte)0x2B;
            for (int i = 0, p = 0; i < grown.Length; i++, p += 4)
                if (grown[i] && !markA[i] && !markB[i]) { outPixels[p] = bl; outPixels[p + 1] = gr; outPixels[p + 2] = rd; }
        }
        var picture = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, outPixels, width * 4);
        picture.Freeze();
        int changed = onlyA + onlyB;
        return new PixelDiff(picture, ink == 0 ? 0 : (double)changed / ink, changed);
    }

    // ---------- the words ----------
    private enum Op { Same, Removed, Added }
    private static readonly Regex WordRegex = new(@"\S+", RegexOptions.Compiled);

    private static List<(Op Op, string Word)> DiffWords(string a, string b)
    {
        var wa = WordRegex.Matches(a).Select(m => m.Value).ToArray();
        var wb = WordRegex.Matches(b).Select(m => m.Value).ToArray();
        var result = new List<(Op, string)>();
        // (the common start and end are skipped first: most pages differ in the middle only)
        int start = 0;
        while (start < wa.Length && start < wb.Length && wa[start] == wb[start]) start++;
        int endA = wa.Length, endB = wb.Length;
        while (endA > start && endB > start && wa[endA - 1] == wb[endB - 1]) { endA--; endB--; }
        for (int i = 0; i < start; i++) result.Add((Op.Same, wa[i]));
        int n = endA - start, m = endB - start;
        if ((long)n * m > 16_000_000)                                         // (too much text to compare word by word: the middle is shown as taken out and put in)
        {
            for (int i = start; i < endA; i++) result.Add((Op.Removed, wa[i]));
            for (int j = start; j < endB; j++) result.Add((Op.Added, wb[j]));
        }
        else
        {
            var lcs = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    lcs[i, j] = wa[start + i] == wb[start + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int x = 0, y = 0;
            while (x < n && y < m)
            {
                if (wa[start + x] == wb[start + y]) { result.Add((Op.Same, wa[start + x])); x++; y++; }
                else if (lcs[x + 1, y] >= lcs[x, y + 1]) { result.Add((Op.Removed, wa[start + x])); x++; }
                else { result.Add((Op.Added, wb[start + y])); y++; }
            }
            while (x < n) { result.Add((Op.Removed, wa[start + x])); x++; }
            while (y < m) { result.Add((Op.Added, wb[start + y])); y++; }
        }
        for (int i = endA; i < wa.Length; i++) result.Add((Op.Same, wa[i]));
        return result;
    }

    private string PageText(PdfFile pdf, int page)
    {
        if (page >= pdf.PageCount) return "";
        try { return pdf.GetText(page).Text.Replace('\r', ' ').Replace('\n', ' '); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { return ""; }
    }

    // ---------- the list of pages ----------
    private async Task MarkPagesAsync()
    {
        int count = Math.Max(_a.PageCount, _b.PageCount);
        int different = 0;
        var token = _cts.Token;
        for (int i = 0; i < count && !token.IsCancellationRequested; i++)
        {
            int page = i;
            string mark = await Task.Run(() =>
            {
                try
                {
                    if (page >= _a.PageCount) return "only in B";
                    if (page >= _b.PageCount) return "only in A";
                    var words = DiffWords(PageText(_a, page), PageText(_b, page));
                    int changes = words.Count(w => w.Op != Op.Same);
                    if (changes > 0) return changes + " changed";
                    var pixels = DiffPixels(page, 260);                   // (no change in the words: maybe pictures or scans)
                    return pixels != null && pixels.Share > 0.01 ? "looks different" : "same";
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { return "?"; }
            }, token).ConfigureAwait(true);
            if (token.IsCancellationRequested) return;
            _marks[page] = mark;
            if (mark != "same") different++;
            if (page % 4 == 0 || page == count - 1 || mark != "same") RefreshRows();
        }
        RefreshRows();
        if (_current >= 0) await ShowPageAsync(_current, refreshOnly: true);
        _status.Text = different == 0 ? "No differences found." : $"{different} of {count} page{(count == 1 ? "" : "s")} differ.";
    }

    private void RefreshRows()
    {
        int selected = _pages.SelectedIndex;
        _pages.ItemsSource = Enumerable.Range(0, Math.Max(_a.PageCount, _b.PageCount)).Select(i => new PageRow(i, "Page " + (i + 1), _marks.TryGetValue(i, out var m) ? (m == "same" ? "✓" : m) : "…")).ToList();
        _pages.SelectedIndex = selected;
    }

    // ---------- the page being looked at ----------
    private async Task ShowPageAsync(int page, bool refreshOnly = false)
    {
        if (page < 0) return;
        _current = page;
        int generation = ++_generation;
        bool diff = _modeDiff.IsChecked == true, side = _modeSide.IsChecked == true;
        _pictureHost.Visibility = diff || side ? Visibility.Visible : Visibility.Collapsed;
        _textScroll.Visibility = diff || side ? Visibility.Collapsed : Visibility.Visible;
        _viewB.Visibility = side ? Visibility.Visible : Visibility.Collapsed;
        _pictureHost.ColumnDefinitions[1].Width = side ? GridLength.Auto : new GridLength(0);
        _pictureHost.ColumnDefinitions[2].Width = side ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        if (!refreshOnly) _status.Text = "Page " + (page + 1) + "…";
        try
        {
            if (diff)
            {
                var d = await Task.Run(() => DiffPixels(page, 1000));
                if (generation != _generation) return;
                _view.Source = d?.Picture;
                _status.Text = d == null ? "Page " + (page + 1) + ": nothing to show" : d.Changed == 0 ? $"Page {page + 1}: looks the same." : $"Page {page + 1}: {d.Share:P0} of what is drawn differs ({(page >= _a.PageCount ? "only in the second" : page >= _b.PageCount ? "only in the first" : "red / green")}).";
            }
            else if (side)
            {
                var pair = await Task.Run(() =>
                {
                    var like = page < _a.PageCount ? _a.PageSize(page) : page < _b.PageCount ? _b.PageSize(page) : new Size(595, 842);
                    int w = 800, h = Math.Max(1, (int)Math.Round(w * like.Height / like.Width));
                    BitmapSource? left = page < _a.PageCount ? _a.Render(page, w, h, forScreen: false) : null;
                    BitmapSource? right = page < _b.PageCount ? _b.Render(page, w, h, forScreen: false) : null;
                    return (left, right);
                });
                if (generation != _generation) return;
                _view.Source = pair.left; _viewB.Source = pair.right;
                _status.Text = $"Page {page + 1}: {Short(_nameA)} on the left, {Short(_nameB)} on the right.";
            }
            else
            {
                var words = await Task.Run(() => DiffWords(PageText(_a, page), PageText(_b, page)));
                if (generation != _generation) return;
                _words.Inlines.Clear();
                int removed = words.Count(w => w.Op == Op.Removed), added = words.Count(w => w.Op == Op.Added);
                var plain = Foreground is SolidColorBrush tb ? Color.FromArgb(150, tb.Color.R, tb.Color.G, tb.Color.B) : Colors.Gray;      // (the text colour of the theme, softer)
                if (words.Count == 0) _words.Inlines.Add(new Run("There are no words on this page in either file (it may be a scan or a picture): use Differences.") { Foreground = new SolidColorBrush(plain) });
                foreach (var (op, word) in words)
                {
                    var run = new Run(word + " ");
                    if (op == Op.Removed) { run.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x3C, 0x3C)); run.TextDecorations = TextDecorations.Strikethrough; }
                    else if (op == Op.Added) { run.Foreground = new SolidColorBrush(Color.FromRgb(0x2B, 0xA8, 0x55)); run.FontWeight = FontWeights.SemiBold; }
                    else run.Foreground = new SolidColorBrush(plain);
                    _words.Inlines.Add(run);
                }
                _status.Text = removed + added == 0 ? $"Page {page + 1}: the words are the same." : $"Page {page + 1}: {removed} word{(removed == 1 ? "" : "s")} taken out (red, struck through), {added} put in (green).";
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OutOfMemoryException)
        {
            if (generation == _generation) _status.Text = "Couldn't compare this page: " + e.Message;
        }
    }
}
