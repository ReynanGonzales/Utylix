using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using Polygon = System.Windows.Shapes.Polygon;
using System.Windows.Threading;
using IdmClone.Engine;
using Pt = IdmClone.Engine.DocScan.Pt;

namespace IdmClone;

/// <summary>
/// "Clean up" a phone photo of a page: Utylix finds the paper, the four corners can be dragged to fit, the page is straightened and
/// the shadows are taken out (colour, grey or black and white). The result is a new picture file in a temporary folder that goes into
/// the PDF in place of the photo; the photo itself is never changed. Everything runs on this PC.
/// </summary>
public sealed class PhotoCleanWindow : Window
{
    private readonly BitmapSource _source;
    private readonly Pt[] _corners;
    private readonly Grid _left = new() { ClipToBounds = true, Background = Brushes.Transparent };
    private readonly Image _photo = new() { Stretch = Stretch.Uniform };
    private readonly Canvas _overlay = new();
    private readonly Polygon _outline = new() { Stroke = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(34, 0x3B, 0x82, 0xF6)), StrokeLineJoin = PenLineJoin.Round };
    private readonly Ellipse[] _handles = new Ellipse[4];
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontSize = 12 };
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly RadioButton[] _filters = new RadioButton[4];
    private readonly Button _use = new() { Content = "Use this picture", MinWidth = 150, Margin = new Thickness(0, 0, 10, 0) };
    private int _dragging = -1, _token;
    private string? _result;
    private readonly string _name;

    /// <summary>Opens the window for this picture file. Returns the cleaned picture's file (in the temp folder), or null if the person cancelled.</summary>
    public static string? Edit(Window? owner, string file)
    {
        BitmapSource picture;
        try { picture = PdfCombiner.LoadPicture(File.ReadAllBytes(file), 0).Pixels; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            UMessage.Show(owner, "That picture can't be opened: " + e.Message);
            return null;
        }
        var w = new PhotoCleanWindow(picture, Path.GetFileName(file)) { Owner = owner };
        w.ShowDialog();
        return w._result;
    }

    private PhotoCleanWindow(BitmapSource picture, string name)
    {
        _source = picture; _name = name;
        _corners = DocScan.FindCorners(picture);
        Title = "Clean up a photo of a page - Utylix Editor";
        Width = 1120; Height = 720; MinWidth = 820; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13.5;
        WindowTheme.DarkTitleBar(this);
        try { Icon = new BitmapImage(new Uri("pack://application:,,,/pdf.ico")); } catch (Exception e) when (e is IOException or UriFormatException) { }
        var card = (Brush)Application.Current.FindResource("CardBrush");
        var line = (Brush)Application.Current.FindResource("LineBrush");
        var text = (Brush)Application.Current.FindResource("TextBrush");
        var muted = (Brush)Application.Current.FindResource("MutedBrush");

        TextBlock T(string s, double size, FontWeight weight, Brush? brush = null) => new() { Text = s, FontSize = size, FontWeight = weight, Foreground = brush ?? text, TextWrapping = TextWrapping.Wrap };

        var root = new Grid { Margin = new Thickness(22, 18, 22, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new StackPanel();
        head.Children.Add(T("Clean up a photo of a page", 20, FontWeights.SemiBold));
        head.Children.Add(new TextBlock { Text = $"{name}   ·   Drag the four blue dots onto the corners of the page. The straightened page is on the right.", Foreground = muted, FontSize = 12.5, Margin = new Thickness(0, 4, 0, 12), TextWrapping = TextWrapping.Wrap });
        Grid.SetRow(head, 0); root.Children.Add(head);

        // ---- the photo with its four corners, and the result ----
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _photo.Source = picture;
        _left.Children.Add(_photo);
        _left.Children.Add(_overlay);
        _overlay.Children.Add(_outline);
        for (int i = 0; i < 4; i++)
        {
            int index = i;
            var h = new Ellipse { Width = 26, Height = 26, Fill = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), Stroke = Brushes.White, StrokeThickness = 3, Cursor = Cursors.SizeAll };
            System.Windows.Automation.AutomationProperties.SetAutomationId(h, "CleanCorner" + i);
            h.MouseLeftButtonDown += (_, e) => { _dragging = index; h.CaptureMouse(); e.Handled = true; };
            h.MouseMove += (_, e) => { if (_dragging == index) MoveCorner(index, e.GetPosition(_left)); };
            h.MouseLeftButtonUp += (_, _) => { _dragging = -1; h.ReleaseMouseCapture(); };
            _handles[i] = h; _overlay.Children.Add(h);
        }
        var leftBox = new Border { Background = card, BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = _left, Padding = new Thickness(8) };
        Grid.SetColumn(leftBox, 0); body.Children.Add(leftBox);

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var previewBox = new Border { Background = card, BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = _preview, Padding = new Thickness(8) };
        Grid.SetRow(previewBox, 0); right.Children.Add(previewBox);

        var controls = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        controls.Children.Add(T("How it looks", 13, FontWeights.SemiBold));
        var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var chipStyle = (Style)Application.Current.FindResource("ChipButton");
        (string Label, DocFilter Filter)[] kinds = { ("Colour", DocFilter.Colour), ("Grey", DocFilter.Grey), ("Black & white", DocFilter.BlackWhite), ("As photographed", DocFilter.Original) };
        for (int i = 0; i < kinds.Length; i++)
        {
            var chip = new RadioButton { Content = kinds[i].Label, Style = chipStyle, GroupName = "cleanfilter", Tag = kinds[i].Filter, IsChecked = i == 0 };
            System.Windows.Automation.AutomationProperties.SetAutomationId(chip, "CleanFilter" + kinds[i].Filter);
            chip.Checked += (_, _) => RefreshPreview();
            _filters[i] = chip; chips.Children.Add(chip);
        }
        controls.Children.Add(chips);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var again = new Button { Content = "Find the page again", Style = (Style)Application.Current.FindResource("DialogButton"), Margin = new Thickness(0, 0, 8, 0) };
        again.Click += (_, _) => { var found = DocScan.FindCorners(_source); for (int i = 0; i < 4; i++) _corners[i] = found[i]; PlaceHandles(); RefreshPreview(); };
        var whole = new Button { Content = "Use the whole picture", Style = (Style)Application.Current.FindResource("DialogButton") };
        whole.Click += (_, _) =>
        {
            double w = _source.PixelWidth, h = _source.PixelHeight;
            (_corners[0], _corners[1], _corners[2], _corners[3]) = (new Pt(0, 0), new Pt(w, 0), new Pt(w, h), new Pt(0, h));
            PlaceHandles(); RefreshPreview();
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(again, "CleanFindAgain");
        System.Windows.Automation.AutomationProperties.SetAutomationId(whole, "CleanWhole");
        tools.Children.Add(again); tools.Children.Add(whole);
        controls.Children.Add(tools);
        _note.Foreground = muted;
        System.Windows.Automation.AutomationProperties.SetAutomationId(_note, "CleanNote");
        controls.Children.Add(_note);
        Grid.SetRow(controls, 1); right.Children.Add(controls);
        Grid.SetColumn(right, 2); body.Children.Add(right);
        Grid.SetRow(body, 1); root.Children.Add(body);

        // ---- buttons ----
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        _use.Style = (Style)Application.Current.FindResource("DialogPrimary");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_use, "CleanUse");
        var cancel = new Button { Content = "Cancel", MinWidth = 100, Style = (Style)Application.Current.FindResource("DialogButton"), IsCancel = true };
        buttons.Children.Add(_use); buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 2); root.Children.Add(buttons);
        Content = root;

        _use.Click += async (_, _) => await UseAsync();
        _left.SizeChanged += (_, _) => PlaceHandles();
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = PreviewAsync(); };
        Loaded += (_, _) => { PlaceHandles(); RefreshPreview(); };
    }

    // ---------- the corners ----------
    private (double Scale, double Ox, double Oy) Map()
    {
        double w = Math.Max(1, _left.ActualWidth), h = Math.Max(1, _left.ActualHeight);
        double scale = Math.Min(w / _source.PixelWidth, h / _source.PixelHeight);
        return (scale, (w - _source.PixelWidth * scale) / 2, (h - _source.PixelHeight * scale) / 2);
    }

    private void PlaceHandles()
    {
        var (s, ox, oy) = Map();
        var pts = new PointCollection();
        for (int i = 0; i < 4; i++)
        {
            double x = ox + _corners[i].X * s, y = oy + _corners[i].Y * s;
            Canvas.SetLeft(_handles[i], x - 13); Canvas.SetTop(_handles[i], y - 13);
            pts.Add(new Point(x, y));
        }
        _outline.Points = pts;
    }

    private void MoveCorner(int index, Point at)
    {
        var (s, ox, oy) = Map();
        _corners[index] = new Pt(Math.Clamp((at.X - ox) / s, 0, _source.PixelWidth), Math.Clamp((at.Y - oy) / s, 0, _source.PixelHeight));
        PlaceHandles();
        RefreshPreview();
    }

    // ---------- the result ----------
    private DocFilter Chosen() => _filters.First(f => f.IsChecked == true).Tag is DocFilter d ? d : DocFilter.Colour;

    private void RefreshPreview() { _debounce.Stop(); _debounce.Start(); }

    private async Task PreviewAsync()
    {
        int token = ++_token;
        var corners = _corners.ToArray(); var filter = Chosen();
        if (!DocScan.IsSensible(corners, _source.PixelWidth, _source.PixelHeight))
        {
            _note.Text = "The four dots don't make a page shape (they cross over or are too close). Move them onto the page's corners.";
            return;
        }
        _note.Text = "";
        var shown = await Task.Run(() => DocScan.Apply(DocScan.Warp(_source, corners, 1100), filter));
        if (token == _token) _preview.Source = shown;
    }

    private async Task UseAsync()
    {
        var corners = _corners.ToArray(); var filter = Chosen();
        if (!DocScan.IsSensible(corners, _source.PixelWidth, _source.PixelHeight)) { _note.Text = "Move the four dots onto the page's corners first."; return; }
        _use.IsEnabled = false; _use.Content = "Making it…";
        try
        {
            string path = await Task.Run(() =>
            {
                var page = DocScan.Apply(DocScan.Warp(_source, corners, 3000), filter);
                string dir = Path.Combine(Path.GetTempPath(), "UtylixScan");
                Directory.CreateDirectory(dir);
                string stem = Path.GetFileNameWithoutExtension(_name);
                bool png = filter == DocFilter.BlackWhite;                           // black and white compresses best (and sharpest) without losses
                string file = Path.Combine(dir, $"{stem}-clean-{Guid.NewGuid().ToString("N")[..6]}{(png ? ".png" : ".jpg")}");
                BitmapEncoder enc = png ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 90 };
                enc.Frames.Add(BitmapFrame.Create(png ? new FormatConvertedBitmap(page, PixelFormats.Gray8, null, 0) : new FormatConvertedBitmap(page, PixelFormats.Bgr24, null, 0)));
                using (var fs = File.Create(file)) enc.Save(fs);
                return file;
            });
            _result = path;
            DialogResult = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OutOfMemoryException or NotSupportedException)
        {
            _note.Text = "Couldn't make the picture: " + e.Message;
            _use.IsEnabled = true; _use.Content = "Use this picture";
        }
    }
}
