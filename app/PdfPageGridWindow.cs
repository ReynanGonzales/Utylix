using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// "Page grid": every page as a big picture in a grid. Click to choose (Ctrl / Shift for several), drag to move, and turn, delete or add blank pages;
/// nothing touches the PDF until "Apply". Double-click a page to go to it.
/// </summary>
public sealed class PdfPageGridWindow : Window
{
    private sealed class Tile
    {
        public int Source;                 // the page of the document, or -1 for a new blank page
        public int Turns;                  // quarter turns clockwise
        public Border Frame = null!;       // the whole tile (what is highlighted)
        public Border PageBox = null!;     // the page picture area
        public Image Picture = null!;
        public TextBlock Label = null!;
        public bool Selected;
    }

    private readonly PdfFile _pdf;
    private readonly List<Tile> _tiles = new();
    private readonly WrapPanel _panel = new() { Margin = new Thickness(14) };
    private readonly Canvas _bar = new() { IsHitTestVisible = false };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly Slider _zoom = new() { Minimum = 90, Maximum = 260, Value = 150, Width = 130, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
    private readonly Button _apply = new() { Content = "Apply", MinWidth = 90 };
    private readonly CancellationTokenSource _cts = new();
    private readonly int _originalCount;
    private Rectangle? _insertLine;

    // dragging
    private Tile? _pressed;
    private Point _pressAt;
    private bool _dragging, _pressedWasSelected;
    private int _anchor = -1;                                // (the tile Shift measures from)

    public List<PdfPageTools.PagePlan>? Result { get; private set; }
    /// <summary>A page the person double-clicked: the editor goes there (0-based), or -1.</summary>
    public int GoTo { get; private set; } = -1;

    public PdfPageGridWindow(Window owner, PdfFile pdf, int currentPage)
    {
        _pdf = pdf; _originalCount = pdf.PageCount;
        PdfDialogKit.Setup(this, owner, "Page grid", 1000);
        SizeToContent = SizeToContent.Manual; ResizeMode = ResizeMode.CanResizeWithGrip;
        Height = 700; MinWidth = 560; MinHeight = 380;
        System.Windows.Automation.AutomationProperties.SetAutomationId(_panel, "PdfGridPanel");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");

        for (int i = 0; i < pdf.PageCount; i++) _tiles.Add(MakeTile(i));
        var root = new DockPanel();

        // the buttons
        var bar = new Border { Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(0, 0, 0, 1) };
        bar.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        var buttons = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        Button Make(string text, string tip, Action action, string id)
        {
            var b = new Button { Content = text, ToolTip = tip, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0), Style = (Style)PdfDialogKit.Res("DialogButton") };
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, id);
            b.Click += (_, _) => action();
            buttons.Children.Add(b);
            return b;
        }
        Make("Turn left", "Turn the chosen pages a quarter to the left", () => Turn(-1), "PdfGridTurnLeft");
        Make("Turn right", "Turn the chosen pages a quarter to the right", () => Turn(1), "PdfGridTurnRight");
        Make("Delete", "Take the chosen pages out (Delete key)", DeleteChosen, "PdfGridDelete");
        Make("Add a blank page", "A blank page after the chosen page (or at the end)", AddBlank, "PdfGridBlank");
        Make("Choose all", "Ctrl+A", () => { foreach (var t in _tiles) t.Selected = true; Refresh(); }, "PdfGridAll");
        buttons.Children.Add(new TextBlock { Text = "Size", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0), Opacity = 0.7 });
        buttons.Children.Add(_zoom);
        bar.Child = buttons;
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        // the bottom: what will happen, Apply, Cancel
        var foot = new DockPanel { Margin = new Thickness(14, 10, 14, 12) };
        var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        _apply.Style = (Style)PdfDialogKit.Res("DialogPrimary"); cancel.Style = (Style)PdfDialogKit.Res("DialogButton"); cancel.IsCancel = true;
        System.Windows.Automation.AutomationProperties.SetAutomationId(_apply, "PdfGridApply");
        _apply.Click += (_, _) => Accept();
        DockPanel.SetDock(cancel, Dock.Right); DockPanel.SetDock(_apply, Dock.Right);
        foot.Children.Add(cancel); foot.Children.Add(_apply); foot.Children.Add(_status);
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(foot);

        // the pages (and, on top, the line that shows where a dragged page will land)
        var host = new Grid();
        _scroll.Content = new Grid { Children = { _panel, _bar } };
        host.Children.Add(_scroll);
        root.Children.Add(host);
        Content = root;

        _panel.MouseLeftButtonDown += PanelDown;
        _panel.MouseMove += PanelMove;
        _panel.MouseLeftButtonUp += PanelUp;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete) { DeleteChosen(); e.Handled = true; }
            else if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { foreach (var t in _tiles) t.Selected = true; Refresh(); e.Handled = true; }
        };
        _zoom.ValueChanged += (_, _) => Refresh();
        Closed += (_, _) => _cts.Cancel();
        Loaded += (_, _) =>
        {
            int cur = Math.Clamp(currentPage, 0, _tiles.Count - 1);
            _tiles[cur].Selected = true; _anchor = cur;
            Refresh();
            _ = LoadPicturesAsync();
            _tiles[cur].Frame.BringIntoView();
        };
        Refresh();
    }

    // ---------- the tiles ----------
    private Tile MakeTile(int source)
    {
        var t = new Tile { Source = source };
        t.Picture = new Image { Stretch = Stretch.Uniform, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(t.Picture, BitmapScalingMode.HighQuality);
        t.PageBox = new Border { Background = Brushes.White, Child = t.Picture, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.35 }, ClipToBounds = true, IsHitTestVisible = false };
        t.Label = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), FontSize = 12, IsHitTestVisible = false };
        t.Label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var stack = new StackPanel { IsHitTestVisible = false };
        stack.Children.Add(t.PageBox); stack.Children.Add(t.Label);
        t.Frame = new Border { Child = stack, Padding = new Thickness(8), Margin = new Thickness(5), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2), Background = Brushes.Transparent, Cursor = Cursors.Hand, Tag = t };
        return t;
    }

    private async Task LoadPicturesAsync()
    {
        var token = _cts.Token;
        var wanted = _tiles.Where(t => t.Source >= 0).Select(t => t.Source).Distinct().ToList();
        foreach (int index in wanted)
        {
            if (token.IsCancellationRequested) return;
            BitmapSource? picture = null;
            try
            {
                picture = await Task.Run(() =>
                {
                    var size = _pdf.PageSize(index);
                    int w = 300, h = Math.Max(1, (int)Math.Round(w * size.Height / Math.Max(1, size.Width)));
                    return _pdf.Render(index, w, Math.Min(h, 800), 0, forScreen: false);
                }, token);
            }
            catch (Exception e) when (e is OperationCanceledException or System.IO.IOException or ObjectDisposedException or OutOfMemoryException) { }
            if (picture == null) continue;
            foreach (var t in _tiles.Where(t => t.Source == index)) t.Picture.Source = picture;
        }
    }

    private void Refresh()
    {
        double w = _zoom.Value;
        _panel.Children.Clear();
        for (int i = 0; i < _tiles.Count; i++)
        {
            var t = _tiles[i];
            bool turned = t.Turns % 2 != 0;
            // (the picture area is a box that fits a page, upright or on its side; a turned page is drawn turned inside it)
            double areaW = w, areaH = w * 1.3;
            t.PageBox.Width = areaW; t.PageBox.Height = areaH;
            t.PageBox.Background = t.Source < 0 ? Brushes.White : Brushes.Transparent;
            t.Picture.LayoutTransform = t.Turns % 4 == 0 ? Transform.Identity : new RotateTransform(((t.Turns % 4) + 4) % 4 * 90);
            _ = turned;
            t.Label.Text = (i + 1).ToString() + (t.Source < 0 ? "  (blank)" : t.Source != i ? $"  (was {t.Source + 1})" : "");
            t.Frame.Width = areaW + 16 + 10;
            var accent = (Brush)PdfDialogKit.Res("AccentBrush");
            t.Frame.BorderBrush = t.Selected ? accent : Brushes.Transparent;
            t.Frame.Background = t.Selected ? new SolidColorBrush(Color.FromArgb(40, ((SolidColorBrush)accent).Color.R, ((SolidColorBrush)accent).Color.G, ((SolidColorBrush)accent).Color.B)) : Brushes.Transparent;
            _panel.Children.Add(t.Frame);
        }
        int chosen = _tiles.Count(t => t.Selected);
        bool changed = HasChanges();
        _status.Text = $"{_tiles.Count} page{(_tiles.Count == 1 ? "" : "s")}" + (chosen > 0 ? $" · {chosen} chosen" : "") + (changed ? " · not applied yet" : "") + "   Drag to move · double-click to open a page";
        _apply.IsEnabled = changed;
    }

    private bool HasChanges() => _tiles.Count != _originalCount || _tiles.Select((t, i) => t.Source != i || t.Turns % 4 != 0).Any(x => x);

    // ---------- choosing ----------
    private Tile? TileAt(Point p)
    {
        foreach (var t in _tiles)
        {
            var top = t.Frame.TranslatePoint(new Point(0, 0), _panel);
            if (new Rect(top, new Size(t.Frame.ActualWidth, t.Frame.ActualHeight)).Contains(p)) return t;
        }
        return null;
    }

    private void PanelDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_panel);
        var t = TileAt(p);
        Focus();
        if (t == null) { foreach (var x in _tiles) x.Selected = false; Refresh(); return; }
        int index = _tiles.IndexOf(t);
        if (e.ClickCount == 2) { GoTo = t.Source >= 0 ? t.Source : -1; if (GoTo >= 0) { Result = null; DialogResult = false; } return; }
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        _pressedWasSelected = t.Selected;
        if (shift && _anchor >= 0)
        {
            int a = Math.Min(_anchor, index), b = Math.Max(_anchor, index);
            if (!ctrl) foreach (var x in _tiles) x.Selected = false;
            for (int i = a; i <= b; i++) _tiles[i].Selected = true;
        }
        else if (ctrl) { t.Selected = !t.Selected; _anchor = index; }
        else
        {
            if (!t.Selected) { foreach (var x in _tiles) x.Selected = false; t.Selected = true; }    // (pressing one of several chosen pages keeps them: they are about to be dragged)
            _anchor = index;
        }
        _pressed = t; _pressAt = p; _dragging = false;
        _panel.CaptureMouse();
        Refresh();
    }

    // ---------- dragging ----------
    private void PanelMove(object sender, MouseEventArgs e)
    {
        if (_pressed == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_panel);
        if (!_dragging)
        {
            if ((p - _pressAt).Length < 8) return;
            _dragging = true;
            _panel.Cursor = Cursors.SizeAll;
        }
        ShowInsertLine(DropIndex(p, out var line));
        _ = line;
    }

    /// <summary>Where the dragged pages would go: the index among the pages that stay put.</summary>
    private int DropIndex(Point p, out Rect line)
    {
        var rest = _tiles.Where(t => !t.Selected).ToList();
        line = Rect.Empty;
        for (int i = 0; i < rest.Count; i++)
        {
            var t = rest[i];
            var origin = t.Frame.TranslatePoint(new Point(0, 0), _panel);
            var r = new Rect(origin, new Size(t.Frame.ActualWidth, t.Frame.ActualHeight));
            if (p.Y < r.Top || (p.Y <= r.Bottom && p.X < r.X + r.Width / 2))
            {
                line = new Rect(r.X - 3, r.Y + 4, 4, Math.Max(10, r.Height - 8));
                return i;
            }
        }
        if (rest.Count > 0)
        {
            var last = rest[^1];
            var origin = last.Frame.TranslatePoint(new Point(0, 0), _panel);
            line = new Rect(origin.X + last.Frame.ActualWidth - 1, origin.Y + 4, 4, Math.Max(10, last.Frame.ActualHeight - 8));
        }
        return rest.Count;
    }

    private void ShowInsertLine(int index)
    {
        _ = index;
        DropIndex(Mouse.GetPosition(_panel), out var line);
        _bar.Children.Clear();
        if (line.IsEmpty) return;
        _insertLine = new Rectangle { Width = line.Width, Height = line.Height, Fill = (Brush)PdfDialogKit.Res("AccentBrush"), RadiusX = 2, RadiusY = 2 };
        Canvas.SetLeft(_insertLine, line.X); Canvas.SetTop(_insertLine, line.Y);
        _bar.Children.Add(_insertLine);
    }

    private void PanelUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressed == null) return;
        _panel.ReleaseMouseCapture();
        _panel.Cursor = null;
        _bar.Children.Clear();
        var pressed = _pressed; _pressed = null;
        if (_dragging)
        {
            _dragging = false;
            int at = DropIndex(e.GetPosition(_panel), out _);
            var moving = _tiles.Where(t => t.Selected).ToList();
            var rest = _tiles.Where(t => !t.Selected).ToList();
            rest.InsertRange(Math.Clamp(at, 0, rest.Count), moving);
            _tiles.Clear(); _tiles.AddRange(rest);
            Refresh();
            return;
        }
        // a click without moving on one of several chosen pages chooses only that one
        if (_pressedWasSelected && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0 && _tiles.Count(t => t.Selected) > 1)
        {
            foreach (var x in _tiles) x.Selected = false;
            pressed.Selected = true; _anchor = _tiles.IndexOf(pressed);
            Refresh();
        }
    }

    // ---------- what the buttons do ----------
    private List<Tile> Chosen() => _tiles.Where(t => t.Selected).ToList();

    private void Turn(int by)
    {
        var chosen = Chosen();
        if (chosen.Count == 0) { _status.Text = "Choose a page first"; return; }
        foreach (var t in chosen) t.Turns = ((t.Turns + by) % 4 + 4) % 4;
        Refresh();
    }

    private void DeleteChosen()
    {
        var chosen = Chosen();
        if (chosen.Count == 0) return;
        if (_tiles.Count(t => t.Source >= 0 && !t.Selected) == 0) { _status.Text = "A PDF needs at least one of its own pages: they can't all be deleted."; return; }
        int first = _tiles.IndexOf(chosen[0]);
        foreach (var t in chosen) _tiles.Remove(t);
        if (_tiles.Count > 0) { var next = _tiles[Math.Clamp(first, 0, _tiles.Count - 1)]; next.Selected = true; _anchor = _tiles.IndexOf(next); }
        Refresh();
    }

    private void AddBlank()
    {
        var chosen = Chosen();
        int at = chosen.Count > 0 ? _tiles.IndexOf(chosen[^1]) + 1 : _tiles.Count;
        var blank = MakeTile(-1);
        foreach (var t in _tiles) t.Selected = false;
        blank.Selected = true;
        _tiles.Insert(Math.Clamp(at, 0, _tiles.Count), blank);
        _anchor = _tiles.IndexOf(blank);
        Refresh();
        blank.Frame.BringIntoView();
    }

    private void Accept()
    {
        Result = _tiles.Select(t => new PdfPageTools.PagePlan(t.Source, t.Turns)).ToList();
        DialogResult = true;
    }
}
