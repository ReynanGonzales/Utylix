using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace IdmClone.Vid;

/// <summary>
/// Direct manipulation in the Program monitor, like Premiere's: click a picture, video or title in the picture to choose it, drag it to move it,
/// drag a corner to make it bigger or smaller (the top handle turns it), double-click a title to type in it. With keyframes on, every drag sets one
/// at the playhead.
/// </summary>
public sealed partial class VidWindow
{
    private Canvas _overlay = null!;
    private enum MonitorDrag { None, Move, Scale, Rotate }
    private MonitorDrag _monDrag;
    private Point _monDown;
    private string? _monBefore;
    private List<Clip> _monClips = new();
    private readonly Dictionary<Clip, (double X, double Y, double Scale, double Rotation, double TextSize)> _monStart = new();
    private Point _monCentre;
    private double _monStartDistance, _monStartAngle;
    private bool _monSnapX, _monSnapY;
    private TextBox? _inplace;
    private const double HandleSize = 9;
    private Grid _monitorGrid = null!;

    private void ApplyMonitorZoom()
    {
        if (_monitorGrid == null) return;
        var p = _s.Project;
        double aw = _overlay?.ActualWidth ?? 0, ah = _overlay?.ActualHeight ?? 0;
        if (_monitorZoomPct <= 0 || aw <= 0 || ah <= 0 || p.Width <= 0) { _monitorGrid.RenderTransform = Transform.Identity; return; }
        double fit = Math.Min(aw / p.Width, ah / p.Height);
        double factor = (_monitorZoomPct / 100.0) / fit;
        _monitorGrid.RenderTransformOrigin = new Point(0.5, 0.5);
        _monitorGrid.RenderTransform = new ScaleTransform(factor, factor);
    }

    /// <summary>Where the sequence's picture sits inside the Program monitor: offset and size of one canvas pixel in monitor pixels.</summary>
    private (double Ox, double Oy, double K) CanvasMap()
    {
        var p = _s.Project;
        double aw = _overlay?.ActualWidth ?? 0, ah = _overlay?.ActualHeight ?? 0;
        if (aw <= 0 || ah <= 0 || p.Width <= 0 || p.Height <= 0) return (0, 0, 1);
        double k = Math.Min(aw / p.Width, ah / p.Height);
        return ((aw - p.Width * k) / 2, (ah - p.Height * k) / 2, k);
    }

    /// <summary>The frame of a picture / video / title clip in monitor pixels (centre, size, turn), or null when it is not in the picture at the playhead.</summary>
    private (Point Centre, double W, double H, double Angle)? FrameOf(Clip c)
    {
        var track = _s.Project.TrackOf(c);
        if (track == null || track.Kind != TrackKind.Video || track.Muted) return null;
        double t = _s.Playhead;
        if (t < c.Start - 1e-6 || t >= c.End + 1e-6) return null;
        var p = _s.Project;
        var (ox, oy, k) = CanvasMap();
        double cx = c.ValueAt("X", t, c.X) * p.Width, cy = c.ValueAt("Y", t, c.Y) * p.Height;
        double w, h, angle = 0;
        if (c.Kind == ClipKind.Text)
        {
            double size = Math.Max(8, p.Height * Math.Clamp(c.TextSize, 0.01, 0.5));
            var face = new Typeface(new FontFamily(c.TextFont), FontStyles.Normal, c.TextBold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            string[] lines = (string.IsNullOrEmpty(c.Text) ? " " : c.Text).Replace("\r\n", "\n").Split('\n');
            double maxW = 0;
            foreach (var line in lines)
            {
                var ft = new FormattedText(line.Length == 0 ? " " : line, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, Brushes.White, 1.0);
                maxW = Math.Max(maxW, ft.WidthIncludingTrailingWhitespace);
            }
            w = Math.Max(size, maxW);
            h = lines.Length * size * 1.2 + (lines.Length - 1) * 6;
            if (c.TextBoxOpacity > 0.01) { w += Math.Max(6, size / 3) * 2; h += Math.Max(6, size / 3) * 2; }
        }
        else
        {
            var m = p.MediaOf(c);
            if (m == null || m.Width <= 0 || m.Height <= 0) return null;
            double fit = Math.Min((double)p.Width / m.Width, (double)p.Height / m.Height);
            double scale = c.ValueAt("Scale", t, c.Scale);
            w = m.Width * fit * scale; h = m.Height * fit * scale;
            angle = c.ValueAt("Rotation", t, c.Rotation);
        }
        return (new Point(ox + cx * k, oy + cy * k), w * k, h * k, angle);
    }

    private static Point Rotate(Point p, Point about, double degrees)
    {
        double a = degrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        double dx = p.X - about.X, dy = p.Y - about.Y;
        return new Point(about.X + dx * cos - dy * sin, about.Y + dx * sin + dy * cos);
    }

    /// <summary>Picture and title clips visible at the playhead, top layer first.</summary>
    private IEnumerable<Clip> VisibleOnTop()
    {
        var vt = _s.Project.Tracks.Where(t => t.Kind == TrackKind.Video && !t.Muted).Reverse();
        foreach (var t in vt)
            foreach (var c in t.Clips.OrderByDescending(c => c.Start))
                if (c.Kind != ClipKind.Media || _s.Project.MediaOf(c)?.HasVideo == true)
                    if (_s.Playhead >= c.Start - 1e-6 && _s.Playhead < c.End + 1e-6) yield return c;
    }

    private Clip? HitClip(Point p)
    {
        foreach (var c in VisibleOnTop())
        {
            var f = FrameOf(c);
            if (f == null) continue;
            var local = Rotate(p, f.Value.Centre, -f.Value.Angle);
            if (Math.Abs(local.X - f.Value.Centre.X) <= f.Value.W / 2 + 3 && Math.Abs(local.Y - f.Value.Centre.Y) <= f.Value.H / 2 + 3) return c;
        }
        return null;
    }

    // ---------- drawing the frame and handles ----------
    private void RefreshOverlay()
    {
        if (_overlay == null) return;
        _overlay.Children.Clear();
        if (_player != null && _player.IsPlaying) return;
        if (_inplace != null) return;
        var accent = (Brush)FindResource("AccentBrush");
        if (_safeMargins)
        {
            var (sox, soy, sk) = CanvasMap();
            double cw = _s.Project.Width * sk, ch = _s.Project.Height * sk;
            foreach (double inset in new[] { 0.05, 0.10 })                    // title safe 90 %, action safe 95 %
            {
                var r = new Rectangle { Width = cw * (1 - 2 * inset), Height = ch * (1 - 2 * inset), Stroke = Brushes.White, StrokeThickness = 1, Opacity = 0.55, IsHitTestVisible = false, StrokeDashArray = new DoubleCollection { 4, 3 } };
                Canvas.SetLeft(r, sox + cw * inset); Canvas.SetTop(r, soy + ch * inset);
                _overlay.Children.Add(r);
            }
        }
        foreach (var c in _s.SelectedClips.Where(c => _s.Project.TrackOf(c)?.Kind == TrackKind.Video))
        {
            var f = FrameOf(c);
            if (f == null) continue;
            var (centre, w, h, angle) = f.Value;
            var frame = new Rectangle
            {
                Width = Math.Max(2, w), Height = Math.Max(2, h), Stroke = accent, StrokeThickness = 1.5, Fill = Brushes.Transparent,
                RenderTransform = new RotateTransform(angle, w / 2, h / 2), IsHitTestVisible = false,
            };
            Canvas.SetLeft(frame, centre.X - w / 2); Canvas.SetTop(frame, centre.Y - h / 2);
            _overlay.Children.Add(frame);
            foreach (var hp in Handles(c, f.Value))
            {
                var r = new Rectangle
                {
                    Width = HandleSize, Height = HandleSize, Fill = Brushes.White, Stroke = accent, StrokeThickness = 1.5, IsHitTestVisible = false,
                    RadiusX = hp.Kind == MonitorDrag.Rotate ? HandleSize / 2 : 1, RadiusY = hp.Kind == MonitorDrag.Rotate ? HandleSize / 2 : 1,
                };
                Canvas.SetLeft(r, hp.At.X - HandleSize / 2); Canvas.SetTop(r, hp.At.Y - HandleSize / 2);
                _overlay.Children.Add(r);
            }
        }
        // centre guides while a drag is snapped to the middle
        if (_monDrag == MonitorDrag.Move)
        {
            var (ox, oy, k) = CanvasMap();
            var pen = new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15));
            if (_monSnapX) { var l = new Line { X1 = ox + _s.Project.Width * k / 2, X2 = ox + _s.Project.Width * k / 2, Y1 = oy, Y2 = oy + _s.Project.Height * k, Stroke = pen, StrokeThickness = 1, IsHitTestVisible = false }; _overlay.Children.Add(l); }
            if (_monSnapY) { var l = new Line { Y1 = oy + _s.Project.Height * k / 2, Y2 = oy + _s.Project.Height * k / 2, X1 = ox, X2 = ox + _s.Project.Width * k, Stroke = pen, StrokeThickness = 1, IsHitTestVisible = false }; _overlay.Children.Add(l); }
        }
    }

    private IEnumerable<(Point At, MonitorDrag Kind, int Corner)> Handles(Clip c, (Point Centre, double W, double H, double Angle) f)
    {
        double hw = f.W / 2, hh = f.H / 2;
        var corners = new[] { new Point(f.Centre.X - hw, f.Centre.Y - hh), new Point(f.Centre.X + hw, f.Centre.Y - hh), new Point(f.Centre.X + hw, f.Centre.Y + hh), new Point(f.Centre.X - hw, f.Centre.Y + hh) };
        for (int i = 0; i < 4; i++) yield return (Rotate(corners[i], f.Centre, f.Angle), MonitorDrag.Scale, i);
        if (c.Kind == ClipKind.Media) yield return (Rotate(new Point(f.Centre.X, f.Centre.Y - hh - 22), f.Centre, f.Angle), MonitorDrag.Rotate, 4);
    }

    // ---------- the mouse ----------
    private void BuildMonitorOverlay(Border screen, System.Windows.Controls.Image picture)
    {
        var grid = new Grid();
        _monitorGrid = grid;
        grid.Children.Add(picture);
        _overlay = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
        grid.Children.Add(_overlay);
        screen.Child = grid;
        _overlay.MouseLeftButtonDown += MonitorDown;
        _overlay.MouseMove += MonitorMove;
        _overlay.MouseLeftButtonUp += MonitorUp;
        _overlay.SizeChanged += (_, _) => { ApplyMonitorZoom(); RefreshOverlay(); };
        _overlay.MouseLeave += (_, _) => { if (_monDrag == MonitorDrag.None) _overlay.Cursor = Cursors.Arrow; };
    }

    private void MonitorDown(object sender, MouseButtonEventArgs e)
    {
        if (_pickKeyEffect != null) { PickFromPicture(e); return; }
        _sourceActive = false;
        if (_inplace != null) { CommitInplace(); }
        if (_player.IsPlaying) { _player.Pause(); UpdatePlayGlyph(); }
        var p = e.GetPosition(_overlay);
        _monDown = p;
        _monDrag = MonitorDrag.None;

        // a handle of a chosen clip?
        foreach (var c in _s.SelectedClips.Where(c => _s.Project.TrackOf(c)?.Kind == TrackKind.Video))
        {
            var f = FrameOf(c);
            if (f == null) continue;
            foreach (var hnd in Handles(c, f.Value))
                if (Math.Abs(hnd.At.X - p.X) <= HandleSize && Math.Abs(hnd.At.Y - p.Y) <= HandleSize)
                {
                    BeginMonitorDrag(hnd.Kind, p);
                    e.Handled = true;
                    return;
                }
        }
        var hit = HitClip(p);
        if (hit == null) { _s.ClearSelection(); return; }
        if (!_s.Selected.Contains(hit.Id)) _s.Select(_s.WithLinked(new[] { hit }));
        if (e.ClickCount == 2 && hit.Kind == ClipKind.Text) { StartInplaceEdit(hit); e.Handled = true; return; }
        if (_s.Project.TrackOf(hit)?.Locked == true) return;
        BeginMonitorDrag(MonitorDrag.Move, p);
        e.Handled = true;
    }

    private void BeginMonitorDrag(MonitorDrag kind, Point p)
    {
        _monClips = _s.WithLinked(_s.SelectedClips).Where(c => _s.Project.TrackOf(c)?.Kind == TrackKind.Video && !(_s.Project.TrackOf(c)?.Locked ?? false)).ToList();
        if (_monClips.Count == 0) return;
        _monDrag = kind;
        _monBefore = _s.BeginEdit();
        _monStart.Clear();
        double t = _s.Playhead;
        foreach (var c in _monClips)
            _monStart[c] = (c.ValueAt("X", t, c.X), c.ValueAt("Y", t, c.Y), c.ValueAt("Scale", t, c.Scale), c.ValueAt("Rotation", t, c.Rotation), c.TextSize);
        var first = _monClips[0];
        var f = FrameOf(first);
        _monCentre = f?.Centre ?? p;
        _monStartDistance = Math.Max(4, (p - _monCentre).Length);
        _monStartAngle = Math.Atan2(p.Y - _monCentre.Y, p.X - _monCentre.X) * 180 / Math.PI;
        _overlay.CaptureMouse();
        _overlay.Cursor = kind == MonitorDrag.Move ? Cursors.SizeAll : kind == MonitorDrag.Rotate ? Cursors.Hand : Cursors.SizeNWSE;
    }

    private void SetKeyOrPlain(Clip c, string prop, double v)
    {
        if (c.IsAnimated(prop)) _s.PutKey(c, prop, _s.Playhead, v);
        else switch (prop)
            {
                case "X": c.X = v; break;
                case "Y": c.Y = v; break;
                case "Scale": c.Scale = v; break;
                case "Rotation": c.Rotation = v; break;
            }
    }

    private void MonitorMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_overlay);
        if (_monDrag == MonitorDrag.None)
        {
            var hover = HitClip(p);
            _overlay.Cursor = hover != null ? Cursors.SizeAll : Cursors.Arrow;
            if (_pickKeyEffect != null) _overlay.Cursor = Cursors.Cross;
            return;
        }
        var proj = _s.Project;
        var (ox, oy, k) = CanvasMap();
        _monSnapX = _monSnapY = false;
        switch (_monDrag)
        {
            case MonitorDrag.Move:
            {
                double dx = (p.X - _monDown.X) / k / proj.Width, dy = (p.Y - _monDown.Y) / k / proj.Height;
                bool free = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
                foreach (var c in _monClips)
                {
                    var s0 = _monStart[c];
                    double nx = s0.X + dx, ny = s0.Y + dy;
                    if (!free && _monClips.Count == 1)
                    {
                        if (Math.Abs(nx - 0.5) * proj.Width * k < 7) { nx = 0.5; _monSnapX = true; }
                        if (Math.Abs(ny - 0.5) * proj.Height * k < 7) { ny = 0.5; _monSnapY = true; }
                    }
                    SetKeyOrPlain(c, "X", Math.Round(nx, 4));
                    SetKeyOrPlain(c, "Y", Math.Round(ny, 4));
                }
                break;
            }
            case MonitorDrag.Scale:
            {
                double ratio = Math.Max(0.02, (p - _monCentre).Length / _monStartDistance);
                foreach (var c in _monClips)
                {
                    var s0 = _monStart[c];
                    if (c.Kind == ClipKind.Text) c.TextSize = Math.Round(Math.Clamp(s0.TextSize * ratio, 0.02, 0.4), 3);
                    else SetKeyOrPlain(c, "Scale", Math.Round(Math.Clamp(s0.Scale * ratio, 0.05, 6), 3));
                }
                break;
            }
            case MonitorDrag.Rotate:
            {
                double now = Math.Atan2(p.Y - _monCentre.Y, p.X - _monCentre.X) * 180 / Math.PI;
                double delta = now - _monStartAngle;
                if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) { }
                foreach (var c in _monClips.Where(c => c.Kind == ClipKind.Media))
                {
                    double v = _monStart[c].Rotation + delta;
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) v = Math.Round(v / 15) * 15;          // Shift: in steps of 15 degrees
                    SetKeyOrPlain(c, "Rotation", Math.Round(((v + 540) % 360) - 180, 1));
                }
                break;
            }
        }
        RefreshOverlay();
        _timeline.InvalidateVisual();
        _refresh.Stop(); _refresh.Start();
    }

    private void MonitorUp(object sender, MouseButtonEventArgs e)
    {
        if (_monDrag == MonitorDrag.None) return;
        var kind = _monDrag;
        _monDrag = MonitorDrag.None;
        _overlay.ReleaseMouseCapture();
        _overlay.Cursor = Cursors.Arrow;
        _monSnapX = _monSnapY = false;
        if (_monBefore != null) _s.CommitEdit(_monBefore, kind == MonitorDrag.Move ? "Move" : kind == MonitorDrag.Scale ? "Scale" : "Rotate");
        _monBefore = null;
        RefreshOverlay();
    }

    // ---------- typing in a title right in the picture ----------
    private void StartInplaceEdit(Clip c)
    {
        var f = FrameOf(c);
        if (f == null) return;
        var (centre, w, h, _) = f.Value;
        var (_, _, k) = CanvasMap();
        double size = Math.Max(8, _s.Project.Height * Math.Clamp(c.TextSize, 0.01, 0.5)) * k;
        Color col;
        try { col = (Color)ColorConverter.ConvertFromString(c.TextColor); } catch (FormatException) { col = Colors.White; }
        var box = new TextBox
        {
            Text = c.Text, AcceptsReturn = true, FontFamily = new FontFamily(c.TextFont), FontSize = Math.Max(10, size),
            FontWeight = c.TextBold ? FontWeights.Bold : FontWeights.Normal, Foreground = new SolidColorBrush(col),
            Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), BorderBrush = (Brush)FindResource("AccentBrush"), BorderThickness = new Thickness(2),
            Padding = new Thickness(6, 2, 6, 2), MinWidth = Math.Max(160, w + 24), TextAlignment = TextAlignment.Center, CaretBrush = Brushes.White,
        };
        Canvas.SetLeft(box, Math.Max(0, centre.X - Math.Max(160, w + 24) / 2));
        Canvas.SetTop(box, Math.Max(0, centre.Y - Math.Max(size * 1.4, h) / 2));
        _overlay.Children.Clear();
        _inplace = box;
        _overlay.Children.Add(box);
        box.PreviewKeyDown += (_, ev) =>
        {
            if (ev.Key == Key.Escape) { CancelInplace(); ev.Handled = true; }
            else if (ev.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { CommitInplace(); ev.Handled = true; }
        };
        box.LostKeyboardFocus += (_, _) => { if (_inplace == box) CommitInplace(); };
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        SetStatus("Typing in the title: click outside or press Ctrl+Enter to finish, Esc to cancel.");
        _inplaceClip = c;
    }

    private Clip? _inplaceClip;

    private void CommitInplace()
    {
        var box = _inplace; var c = _inplaceClip;
        _inplace = null; _inplaceClip = null;
        if (box == null || c == null) return;
        string text = box.Text;
        _overlay.Children.Clear();
        if (text != c.Text) _s.SetProperty(new[] { c }, "Title text", x => { x.Text = text; x.Label = text.Split('\n')[0]; });
        else RefreshOverlay();
        SetStatus("");
        _timeline.Focus();
    }

    private void CancelInplace()
    {
        _inplace = null; _inplaceClip = null;
        _overlay.Children.Clear();
        RefreshOverlay();
        _timeline.Focus();
    }
}
