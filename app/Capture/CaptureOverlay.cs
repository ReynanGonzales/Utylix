using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace IdmClone;

/// <summary>
/// The "select what to capture" screen: the desktop frozen and dimmed, drag a rectangle (or click a window).
/// Esc or right-click cancels. The result is cut from the frozen picture, so it is exactly what you saw.
/// </summary>
public sealed class CaptureOverlay : Window
{
    public enum Kind { Rectangle, Window, FreeForm, Full }

    private readonly BitmapSource _shot;
    private readonly Int32Rect _area;
    private Kind _kind;
    private List<ScreenWindow> _windows;
    private readonly string _what;
    private readonly TextBlock _hintText = new() { Foreground = Brushes.White, FontSize = 13.5, FontFamily = new FontFamily("Segoe UI") };
    private readonly Dictionary<Kind, RadioButton> _modeButtons = new();
    private readonly double _scale = ScreenGrab.Scale;

    private readonly Canvas _canvas = new();
    private readonly Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)) };
    private readonly Rectangle _frame = new() { Stroke = new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF)), StrokeThickness = 2, Visibility = Visibility.Collapsed };
    private readonly Border _label = new() { Background = new SolidColorBrush(Color.FromArgb(220, 20, 24, 34)), CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 3, 8, 3), Visibility = Visibility.Collapsed };
    private readonly TextBlock _labelText = new() { Foreground = Brushes.White, FontSize = 12.5, FontFamily = new FontFamily("Segoe UI") };

    private readonly List<Point> _points = new();                                  // free-form: the line drawn so far (window units)
    private readonly Polyline _line = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round,
        Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
    };
    private Point? _start;
    private Rect _selection = Rect.Empty;          // in window units
    private bool _done;

    /// <summary>The captured picture, or null when cancelled.</summary>
    public BitmapSource? Result { get; private set; }

    /// <summary>The chosen area in screen pixels, or null when cancelled.</summary>
    public Int32Rect? SelectedArea { get; private set; }

    /// <summary>The kind of snip in use when the overlay closed (the person can switch it with the bar at the top).</summary>
    public Kind ChosenKind => _kind;

    /// <param name="what">What is being chosen, for the hint at the top ("capture" or e.g. "record").</param>
    public CaptureOverlay(BitmapSource shot, Int32Rect area, Kind kind, string what = "capture", bool modeBar = false)
    {
        _what = what;
        _shot = shot;
        _area = area;
        _kind = kind;
        _windows = kind == Kind.Window ? ScreenGrab.VisibleWindows() : new();

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = area.X / _scale; Top = area.Y / _scale;
        Width = area.Width / _scale; Height = area.Height / _scale;
        Background = Brushes.Black;
        Cursor = kind == Kind.Window ? Cursors.Hand : Cursors.Cross;
        Title = "";

        var picture = new Image { Source = shot, Width = Width, Height = Height, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor);
        _label.Child = _labelText;
        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(225, 20, 24, 34)), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 7, 14, 7),
            Child = _hintText,
        };
        _hintText.Text = HintFor(kind);
        FrameworkElement? bar = modeBar ? BuildModeBar() : null;
        _canvas.Children.Add(picture);
        _canvas.Children.Add(_dim);
        _canvas.Children.Add(_frame);
        _canvas.Children.Add(_line);
        _canvas.Children.Add(_label);
        _canvas.Children.Add(hint);
        if (bar != null) _canvas.Children.Add(bar);
        hint.SizeChanged += (_, _) => { Canvas.SetLeft(hint, Math.Max(0, (Width - hint.ActualWidth) / 2)); Canvas.SetTop(hint, bar == null ? 24 : 78); };
        if (bar != null) bar.SizeChanged += (_, _) => { Canvas.SetLeft(bar, Math.Max(0, (Width - bar.ActualWidth) / 2)); Canvas.SetTop(bar, 20); };
        Content = _canvas;
        UpdateDim();

        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, _) => Finish(null);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Finish(null); };
        Loaded += (_, _) => { ScreenGrab.ForceForeground(new System.Windows.Interop.WindowInteropHelper(this).Handle); Activate(); Focus(); };
        Deactivated += (_, _) => { if (IsLoaded) Finish(null); };     // clicked elsewhere / Alt+Tab: never leave a dimmed screen behind
    }

    private string HintFor(Kind k) => k == Kind.Window ? $"Click a window to {_what} it   ·   Esc to cancel"
        : k == Kind.FreeForm ? $"Draw around the object to {_what}   ·   Esc to cancel"
        : $"Drag to select the area to {_what}   ·   Esc to cancel";

    /// <summary>The bar at the top, like the Snipping Tool's: change the kind of snip while choosing.</summary>
    private FrameworkElement BuildModeBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 20, 24, 34)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x31, 0x45)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(6), Child = row, Cursor = Cursors.Arrow,
        };
        border.PreviewMouseLeftButtonDown += BarPressed;
        border.PreviewMouseMove += BarMoved;
        border.PreviewMouseLeftButtonUp += (_, _) => _barPress = null;
        void Add(Kind k, string glyph, string label)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            content.Children.Add(new TextBlock { Text = label, FontFamily = new FontFamily("Segoe UI"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            var b = new RadioButton { Content = content, IsChecked = k == _kind, Template = ModeButtonTemplate(), Margin = new Thickness(2, 0, 2, 0), Cursor = Cursors.Hand, Focusable = false };
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, "OverlayMode" + k);
            System.Windows.Automation.AutomationProperties.SetName(b, label);
            b.Checked += (_, _) => { if (k != _kind || k == Kind.Full) SwitchTo(k); };
            _modeButtons[k] = b;
            row.Children.Add(b);
        }
        Add(Kind.FreeForm, "", "Free-form");
        Add(Kind.Rectangle, "", "Rectangle");
        Add(Kind.Window, "", "Window");
        Add(Kind.Full, "", "Full screen");
        return border;
    }

    private static ControlTemplate ModeButtonTemplate()
    {
        const string xaml = "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='RadioButton'>" +
            "<Border x:Name='bd' CornerRadius='7' Padding='12,7' Background='Transparent'><ContentPresenter TextElement.Foreground='White' /></Border>" +
            "<ControlTemplate.Triggers>" +
            "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#33FFFFFF' /></Trigger>" +
            "<Trigger Property='IsChecked' Value='True'><Setter TargetName='bd' Property='Background' Value='#FF5B8DEF' /></Trigger>" +
            "</ControlTemplate.Triggers></ControlTemplate>";
        return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    // The bar sits on top of the screen, so a drag that happens to start on one of its buttons would otherwise do nothing. A press that
    // moves a few pixels is treated as the start of the selection (the button is not clicked); a plain click still switches the kind.
    private Point? _barPress;

    private void BarPressed(object sender, MouseButtonEventArgs e) => _barPress = e.GetPosition(this);

    private void BarMoved(object sender, MouseEventArgs e)
    {
        if (_barPress is not { } from) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _barPress = null; return; }
        var now = e.GetPosition(this);
        if ((now - from).Length < 5) return;
        _barPress = null;
        if (_kind == Kind.Window || _done) return;                       // (windows are picked by clicking)
        Mouse.Capture(null);                                              // the button had the mouse: the picture takes it, so the button is never clicked
        _start = from;
        if (_kind == Kind.FreeForm) { _points.Clear(); _points.Add(from); _line.Points = new PointCollection(_points); }
        CaptureMouse();
        OnMove(this, e);
        e.Handled = true;
    }

    private void SwitchTo(Kind k)
    {
        if (_done) return;
        if (k == Kind.Full) { _kind = k; Finish(new Rect(0, 0, Width, Height)); return; }
        _kind = k;
        _windows = k == Kind.Window ? ScreenGrab.VisibleWindows() : new();
        _start = null; _points.Clear(); _line.Points = new PointCollection(); _selection = Rect.Empty;
        _frame.Visibility = _label.Visibility = Visibility.Collapsed;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Cursor = k == Kind.Window ? Cursors.Hand : Cursors.Cross;
        _hintText.Text = HintFor(k);
        foreach (var (key, b) in _modeButtons) b.IsChecked = key == k;
        UpdateDim();
    }

    private void UpdateDim()
    {
        var all = new RectangleGeometry(new Rect(0, 0, Width, Height));
        _dim.Data = _selection.IsEmpty ? all : new CombinedGeometry(GeometryCombineMode.Exclude, all, new RectangleGeometry(_selection));
    }

    private void Highlight(Rect r)
    {
        _selection = r;
        Canvas.SetLeft(_frame, r.X); Canvas.SetTop(_frame, r.Y);
        _frame.Width = Math.Max(1, r.Width); _frame.Height = Math.Max(1, r.Height);
        _frame.Visibility = Visibility.Visible;
        _labelText.Text = $"{(int)Math.Round(r.Width * _scale)} × {(int)Math.Round(r.Height * _scale)}";
        _label.Visibility = Visibility.Visible;
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double ly = r.Y - _label.DesiredSize.Height - 6;
        Canvas.SetLeft(_label, Math.Max(0, Math.Min(r.X, Width - _label.DesiredSize.Width)));
        Canvas.SetTop(_label, ly < 0 ? r.Y + 6 : ly);
        UpdateDim();
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_kind == Kind.Window)
        {
            if (!_selection.IsEmpty) Finish(_selection);
            return;
        }
        _start = e.GetPosition(this);
        if (_kind == Kind.FreeForm) { _points.Clear(); _points.Add(_start.Value); _line.Points = new PointCollection(_points); }
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_kind == Kind.Window)
        {
            // window under the pointer (list is top-most first), shown in window units
            double sx = p.X * _scale + _area.X, sy = p.Y * _scale + _area.Y;
            var hit = _windows.FirstOrDefault(w => w.Bounds.Contains(sx, sy));
            if (hit == null) { _selection = Rect.Empty; _frame.Visibility = _label.Visibility = Visibility.Collapsed; UpdateDim(); return; }
            Highlight(new Rect((hit.Bounds.X - _area.X) / _scale, (hit.Bounds.Y - _area.Y) / _scale, hit.Bounds.Width / _scale, hit.Bounds.Height / _scale));
            return;
        }
        if (_start is not { } s) return;
        if (_kind == Kind.FreeForm) { _points.Add(p); _line.Points.Add(p); return; }
        Highlight(new Rect(new Point(Math.Min(s.X, p.X), Math.Min(s.Y, p.Y)), new Point(Math.Max(s.X, p.X), Math.Max(s.Y, p.Y))));
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_kind == Kind.Window || _start == null) return;
        ReleaseMouseCapture();
        _start = null;
        if (_kind == Kind.FreeForm)
        {
            var box = new Rect(new Point(_points.Min(q => q.X), _points.Min(q => q.Y)), new Point(_points.Max(q => q.X), _points.Max(q => q.Y)));
            if (_points.Count < 6 || box.Width * _scale < 6 || box.Height * _scale < 6) { _points.Clear(); _line.Points = new PointCollection(); return; }   // a click, not a drawing: try again
            Finish(box);
            return;
        }
        if (_selection.IsEmpty || _selection.Width * _scale < 4 || _selection.Height * _scale < 4)
        {
            _selection = Rect.Empty; _frame.Visibility = _label.Visibility = Visibility.Collapsed; UpdateDim();     // a click, not a drag: try again
            return;
        }
        Finish(_selection);
    }

    /// <summary>Free-form: everything outside the line becomes see-through.</summary>
    private BitmapSource CutOut(BitmapSource cropped, Rect box)
    {
        int w = cropped.PixelWidth, h = cropped.PixelHeight;
        var figure = new PathFigure { StartPoint = new Point((_points[0].X - box.X) * _scale, (_points[0].Y - box.Y) * _scale), IsClosed = true, IsFilled = true };
        foreach (var q in _points.Skip(1)) figure.Segments.Add(new LineSegment(new Point((q.X - box.X) * _scale, (q.Y - box.Y) * _scale), true));
        var shape = new PathGeometry(); shape.Figures.Add(figure);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushClip(shape);
            dc.DrawImage(cropped, new Rect(0, 0, w, h));
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private void Finish(Rect? selection)
    {
        if (_done) return;
        _done = true;
        if (selection is { } r)
        {
            var px = new Rect(r.X * _scale + _area.X, r.Y * _scale + _area.Y, r.Width * _scale, r.Height * _scale);
            SelectedArea = new Int32Rect((int)Math.Round(px.X), (int)Math.Round(px.Y), (int)Math.Round(px.Width), (int)Math.Round(px.Height));
            Result = ScreenGrab.Crop(_shot, _area, px);
            if (_kind == Kind.FreeForm && Result != null) Result = CutOut(Result, r);
        }
        Close();
    }
}
