using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace IdmClone.Vid;

/// <summary>
/// Premiere's timeline navigator: a bar under the timeline whose lighter part is what the timeline shows. Drag the middle to scroll, drag either
/// end to zoom (the other end stays where it is), double-click to fit the whole sequence.
/// </summary>
public sealed class VidNavigator : FrameworkElement
{
    private enum Mode { None, Body, Left, Right }
    private const double Grip = 9;

    private readonly VidTimeline _tl;
    private readonly Func<double> _duration;
    private Mode _mode;
    private double _downX, _dStart, _dEnd, _dTotal;
    private bool _hover;

    public VidNavigator(VidTimeline timeline, Func<double> sequenceSeconds)
    {
        _tl = timeline;
        _duration = sequenceSeconds;
        Height = 16;
        Cursor = Cursors.Arrow;
        Focusable = false;
        ToolTip = "Drag the ends to zoom, drag the middle to scroll, double-click to fit the whole sequence";
        System.Windows.Automation.AutomationProperties.SetAutomationId(this, "VidNavigator");
    }

    private (double Total, double Start, double Vis) View()
    {
        double vis = _tl.VisibleSeconds, start = _tl.Offset;
        double total = Math.Max(Math.Max(_duration() * 1.1, 5), start + vis);
        return (total, start, vis);
    }

    private Rect ThumbRect()
    {
        var (total, start, vis) = View();
        double w = Math.Max(1, ActualWidth);
        double x0 = start / total * w, x1 = (start + vis) / total * w;
        if (x1 - x0 < Grip * 2 + 4) x1 = Math.Min(w, x0 + Grip * 2 + 4);
        if (x1 - x0 < Grip * 2 + 4) x0 = Math.Max(0, x1 - Grip * 2 - 4);
        return new Rect(x0, 1, Math.Max(2, x1 - x0), Math.Max(2, ActualHeight - 2));
    }

    private Mode HitTest(Point p)
    {
        var t = ThumbRect();
        if (p.X < t.X - 3 || p.X > t.Right + 3) return Mode.None;
        if (p.X <= t.X + Grip) return Mode.Left;
        if (p.X >= t.Right - Grip) return Mode.Right;
        return Mode.Body;
    }

    private static Color TextColour(FrameworkElement e)
        => (e.TryFindResource("TextBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0xD4, 0xD4, 0xD4);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var trackBrush = TryFindResource("TrackBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        dc.DrawRoundedRectangle(trackBrush, null, new Rect(0, 1, w, h - 2), 7, 7);
        var tc = TextColour(this);
        var t = ThumbRect();
        byte alpha = (byte)(_mode != Mode.None ? 110 : _hover ? 90 : 70);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(alpha, tc.R, tc.G, tc.B)), null, t, 7, 7);
        var gripPen = new Pen(new SolidColorBrush(Color.FromArgb(210, tc.R, tc.G, tc.B)), 1.5);
        foreach (double gx in new[] { t.X + 5, t.Right - 5 })
            dc.DrawLine(gripPen, new Point(gx, t.Y + 4), new Point(gx, t.Bottom - 4));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_mode == Mode.None)
        {
            var hit = HitTest(p);
            Cursor = hit is Mode.Left or Mode.Right ? Cursors.SizeWE : hit == Mode.Body ? Cursors.Hand : Cursors.Arrow;
            bool over = hit != Mode.None;
            if (over != _hover) { _hover = over; InvalidateVisual(); }
            return;
        }
        double dsec = (p.X - _downX) / Math.Max(1, ActualWidth) * _dTotal;
        double paneW = Math.Max(10, _tl.ActualWidth - VidTimeline.HeaderWidth);
        double minVis = paneW / 1200.0, maxVis = paneW / 2.0;
        double s = _dStart, vis = _dEnd - _dStart;
        switch (_mode)
        {
            case Mode.Body:
                s = Math.Clamp(_dStart + dsec, 0, Math.Max(0, _dTotal - vis));
                break;
            case Mode.Left:
                s = Math.Clamp(_dStart + dsec, 0, _dEnd - minVis);
                vis = Math.Clamp(_dEnd - s, minVis, maxVis);
                s = Math.Max(0, _dEnd - vis);
                break;
            case Mode.Right:
                vis = Math.Clamp(_dEnd + dsec - _dStart, minVis, maxVis);
                break;
        }
        if (_mode != Mode.Body) _tl.PixelsPerSecond = paneW / vis;
        _tl.Offset = s;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_mode == Mode.None && _hover) { _hover = false; InvalidateVisual(); }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var p = e.GetPosition(this);
        if (e.ClickCount >= 2) { _tl.ZoomToFit(); InvalidateVisual(); e.Handled = true; return; }
        var hit = HitTest(p);
        if (hit == Mode.None)
        {
            // a click on the bar outside the lighter part: jump there, then drag from the middle
            var (total, _, vis0) = View();
            double centre = p.X / Math.Max(1, ActualWidth) * total;
            _tl.Offset = Math.Clamp(centre - vis0 / 2, 0, Math.Max(0, total - vis0));
            hit = Mode.Body;
        }
        var (tot, start, vis) = View();
        _mode = hit;
        _downX = p.X;
        _dStart = start; _dEnd = start + vis; _dTotal = tot;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_mode == Mode.None) return;
        _mode = Mode.None;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var (total, start, vis) = View();
        _tl.Offset = Math.Clamp(start - Math.Sign(e.Delta) * vis * 0.15, 0, Math.Max(0, total - vis));
        e.Handled = true;
    }
}
