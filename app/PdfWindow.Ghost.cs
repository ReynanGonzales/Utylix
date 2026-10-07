using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// The placing guide: while a tool that puts something on the page is chosen, a dashed outline follows the pointer and shows where it will land and how big it will be (for text: the caret, "Abc" and the
/// line the letters stand on). It lines up with the other things on the page like a moved thing does (the pink lines show), and the click lands exactly where the guide is. Tools that are dragged
/// (pen, shapes, highlight, white-out, redact, link, table) show a small cross with the same lines. Alt = no lining up.
/// </summary>
public sealed partial class PdfWindow
{
    private Canvas? _ghost;
    private int _ghostPage = -1;
    private Vector _ghostSnap;                                       // how far the guide was moved to line up (the click is moved the same way)
    private (int Page, int Count) _ghostKey = (-1, -1);              // what _snapTargets was read for

    private static bool PlacesOnClick(EditTool t) =>
        t is EditTool.Text or EditTool.Date or EditTool.Check or EditTool.Cross or EditTool.Note or EditTool.Stamp
            or EditTool.TextField or EditTool.CheckField or EditTool.RadioField or EditTool.SignField or EditTool.DropField;

    private static bool IsDragTool(EditTool t) =>
        t is EditTool.Pen or EditTool.Highlight or EditTool.Underline or EditTool.Strike or EditTool.Shapes or EditTool.WhiteOut or EditTool.Redact or EditTool.Link or EditTool.Table;

    private void HideGhost()
    {
        if (_ghost?.Parent is Canvas c) c.Children.Remove(_ghost);
        _ghost = null; _ghostPage = -1; _ghostSnap = default;
        _guides.Clear();
    }

    /// <summary>Where the thing the tool puts would be, with the pointer at <paramref name="p"/> (points on the page), and how it is drawn.</summary>
    private (Rect Box, string Kind, string Text)? GhostSpec(Point p)
    {
        switch (_tool)
        {
            case EditTool.Text:
            case EditTool.Date:
            {
                string sample = _tool == EditTool.Date ? PdfStampSettings.Today(PdfStampSettings.Current.DateFormat) : "Abc";
                double h = _textSize * 1.35, w = _tool == EditTool.Date ? Math.Max(60, _textSize * 6.5) : Math.Max(60, _textSize * 5);
                return (new Rect(p.X - 1, p.Y - _textSize * 0.6, w, h), "text", sample);
            }
            case EditTool.Check or EditTool.Cross: return (new Rect(p.X - 7, p.Y - 7, 14, 14), "box", _tool == EditTool.Check ? "✓" : "✕");
            case EditTool.Note: return (new Rect(p.X - 9, p.Y - 9, 18, 18), "box", "");
            case EditTool.Stamp:
            {
                if (_stampPicture != null) return (new Rect(p.X - 56, p.Y - 28, 113, 56), "box", "");
                var s = PdfStampSettings.Current;
                var size = StampItem.NaturalSize(_stampLabel, s.StampWithDate);
                return (new Rect(p.X - size.Width / 2, p.Y - size.Height / 2, size.Width, size.Height), "box", _stampLabel);
            }
            case EditTool.TextField or EditTool.DropField: return (new Rect(p.X - 2, p.Y - 11, 150, 22), "box", "");
            case EditTool.SignField: return (new Rect(p.X - 2, p.Y - 20, 180, 40), "box", "");
            case EditTool.CheckField or EditTool.RadioField: return (new Rect(p.X - 8, p.Y - 8, 16, 16), "box", "");
        }
        return null;
    }

    /// <summary>Called when the pointer moves over a page with nothing being dragged: draws (or removes) the placing guide.</summary>
    private void UpdateGhost(PageView pv, Point p)
    {
        bool wants = _editing && _drag == DragMode.None && _runBox == null && _fieldEditor == null && _typing == null && (PlacesOnClick(_tool) || IsDragTool(_tool))
                     && p.X >= 0 && p.Y >= 0 && p.X <= pv.Overlay.Width && p.Y <= pv.Overlay.Height;
        if (wants && PlacesOnClick(_tool) && ItemAt(pv.Index, p) != null) wants = false;            // (a click on something already there works on that thing)
        if (!wants) { if (_ghost != null) { var old = _ghostPage; HideGhost(); } return; }

        if (_ghost?.Parent is Canvas oldParent) oldParent.Children.Remove(_ghost);
        double k = 1 / Math.Max(0.01, pv.OverlayScale.ScaleX);
        var canvas = new Canvas { Width = pv.Overlay.Width, Height = pv.Overlay.Height, IsHitTestVisible = false };
        var accent = new SolidColorBrush(Color.FromRgb(0x2F, 0x6B, 0xEA));
        _guides.Clear(); _ghostSnap = default;

        if (PlacesOnClick(_tool) && GhostSpec(p) is { } spec)
        {
            var box = spec.Box;
            if (_ghostKey != (pv.Index, _items.Count)) { _snapTargets = null; _ghostKey = (pv.Index, _items.Count); }
            var snap = SnapOffset(pv.Index, box, Array.Empty<EditItem>(), null);
            _ghostSnap = snap;
            box = new Rect(box.X + snap.X, box.Y + snap.Y, box.Width, box.Height);
            DrawGuides(pv.Index, canvas);
            var tint = _toolColors.TryGetValue(_tool, out var c) ? c : Color.FromRgb(0x2F, 0x6B, 0xEA);
            var frame = new Rectangle
            {
                Width = box.Width, Height = box.Height, RadiusX = 3 * k, RadiusY = 3 * k, Stroke = accent, StrokeThickness = 1 * k, StrokeDashArray = new DoubleCollection { 3, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(16, 0x2F, 0x6B, 0xEA)), IsHitTestVisible = false,
            };
            Canvas.SetLeft(frame, box.X); Canvas.SetTop(frame, box.Y);
            canvas.Children.Add(frame);
            if (spec.Kind == "text")
            {
                var family = TextItem.Family(_font, _fontName);
                double baseline = box.Y + _textSize * 1.35 / 2 + (family.Baseline - family.LineSpacing / 2) * _textSize;      // (the line the letters stand on)
                var ink = new SolidColorBrush(tint);
                var label = new TextBlock { Text = spec.Text, FontFamily = family, FontSize = _textSize, FontWeight = FontWeights.Bold, Foreground = ink, IsHitTestVisible = false };
                Canvas.SetLeft(label, box.X + 4 * k + 2); Canvas.SetTop(label, baseline - family.Baseline * _textSize);
                canvas.Children.Add(label);
                canvas.Children.Add(new Line { X1 = box.X + 2 * k + 1, X2 = box.Right - 2 * k, Y1 = baseline, Y2 = baseline, Stroke = ink, StrokeThickness = 1 * k, Opacity = 0.75, IsHitTestVisible = false });
                canvas.Children.Add(new Line { X1 = box.X + 2 * k + 2, X2 = box.X + 2 * k + 2, Y1 = box.Y + 2 * k, Y2 = box.Bottom - 2 * k, Stroke = ink, StrokeThickness = 1.2 * k, IsHitTestVisible = false });   // (the caret)
            }
            else if (spec.Text.Length > 0)
            {
                var label = new TextBlock { Text = spec.Text, FontSize = Math.Clamp(box.Height * 0.55, 6, 22), Foreground = accent, Opacity = 0.8, IsHitTestVisible = false };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(label, box.X + Math.Max(2 * k, (box.Width - label.DesiredSize.Width) / 2)); Canvas.SetTop(label, box.Y + Math.Max(0, (box.Height - label.DesiredSize.Height) / 2));
                canvas.Children.Add(label);
            }
        }
        else
        {
            // a dragged tool: a small cross at the pointer, and the thin lines through it
            double arm = 9 * k;
            foreach (var (x1, y1, x2, y2) in new[] { (p.X - arm, p.Y, p.X + arm, p.Y), (p.X, p.Y - arm, p.X, p.Y + arm) })
                canvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = accent, StrokeThickness = 1 * k, StrokeDashArray = new DoubleCollection { 3, 2 }, IsHitTestVisible = false });
        }
        pv.Overlay.Children.Add(canvas);
        _ghost = canvas; _ghostPage = pv.Index;
    }
}
