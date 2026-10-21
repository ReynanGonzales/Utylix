using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Measure tool for drawings (floor plans, lot plans): a scale is set once (the printed scale 1 : 100, or by drawing a length you know), then the Measure tool
/// gives the length of a line of clicked points and the Area tool the area of a clicked shape. The answer is written next to it as normal text, saved with the page.
/// </summary>
public sealed partial class PdfWindow
{
    private double _ptPerUnit;              // PDF points that are 1 unit of real length (0 = no scale set yet)
    private string _unit = "m";
    private double _paperScale;             // N of 1 : N when the scale came from the printed scale (0 = from a known length)
    private MeasureItem? _measure;          // the line / shape being clicked (also _drawing, so it is drawn)
    private bool _calibrating;              // the next line drawn with Measure sets the scale

    private static readonly (string Name, double Mm)[] LengthUnits = { ("m", 1000), ("cm", 10), ("mm", 1), ("ft", 304.8), ("in", 25.4) };

    private static string? UnitName(string typed) => typed.Trim().ToLowerInvariant() switch
    {
        "m" or "meter" or "meters" or "metre" or "metres" => "m",
        "cm" => "cm",
        "mm" => "mm",
        "ft" or "'" or "feet" or "foot" => "ft",
        "in" or "\"" or "inch" or "inches" => "in",
        _ => null,
    };

    private sealed class MeasureItem : EditItem
    {
        public List<Point> Points = new();
        public bool Area, Draft = true, Calibrate;
        public double Width = 1.6, PtPerUnit;
        public string Unit = "m";
        public Point? Cursor;                       // where the mouse is while clicking (the line to it is only a preview)
        public TextItem? Label;                     // the answer, a normal text of the page

        List<Point> Live() { var l = Points.ToList(); if (Draft && Cursor is Point c) l.Add(c); return l; }

        public static double LengthOf(IList<Point> pts, bool closed)
        {
            double sum = 0;
            for (int i = 1; i < pts.Count; i++) sum += (pts[i] - pts[i - 1]).Length;
            if (closed && pts.Count > 2) sum += (pts[0] - pts[^1]).Length;
            return sum;
        }

        public static double AreaOf(IList<Point> pts)
        {
            double s = 0;
            for (int i = 0; i < pts.Count; i++) { var a = pts[i]; var b = pts[(i + 1) % pts.Count]; s += a.X * b.Y - b.X * a.Y; }
            return Math.Abs(s) / 2;
        }

        static string Fmt(double v) => v.ToString(v >= 1000 ? "N0" : v >= 100 ? "N1" : "N2", CultureInfo.InvariantCulture);

        public string Answer(IList<Point> pts)
        {
            if (PtPerUnit <= 0 || pts.Count < 2) return "";
            return Area ? pts.Count < 3 ? "" : Fmt(AreaOf(pts) / (PtPerUnit * PtPerUnit)) + " " + Unit + "²"
                        : Fmt(LengthOf(pts, false) / PtPerUnit) + " " + Unit;
        }

        public void MakeLabel()
        {
            Point at;
            if (Area) at = new Point(Points.Average(p => p.X), Points.Average(p => p.Y));
            else
            {
                int best = 1; double longest = -1;
                for (int i = 1; i < Points.Count; i++) { double d = (Points[i] - Points[i - 1]).Length; if (d > longest) { longest = d; best = i; } }
                at = new Point((Points[best].X + Points[best - 1].X) / 2, (Points[best].Y + Points[best - 1].Y) / 2);
            }
            var t = new TextItem { Page = Page, Text = Answer(Points), FontSize = 11, Bold = true, Color = Color, Fill = Colors.White, BorderWidth = 0.6 };
            var b = t.Bounds;
            t.TopLeft = new Point(at.X - b.Width / 2, at.Y - b.Height / 2);
            Label = t;
        }

        public override Rect Bounds
        {
            get
            {
                if (Points.Count == 0) return Rect.Empty;
                var r = new Rect(Points[0], Points[0]);
                foreach (var p in Points) r.Union(p);
                r.Inflate(Width / 2, Width / 2);
                if (Label != null) r.Union(Label.Bounds);
                return r;
            }
        }

        public override bool Hit(Point p)
        {
            if (Label != null && Label.Hit(p)) return true;
            double reach = Math.Max(4, Width);
            for (int i = 1; i <= Points.Count; i++)
            {
                if (i == Points.Count && !(Area && Points.Count > 2)) break;
                if (DistanceToSegment(p, Points[i - 1], Points[i % Points.Count]) <= reach) return true;
            }
            return false;
        }

        static double DistanceToSegment(Point p, Point a, Point b)
        {
            var ab = b - a; double len2 = ab.LengthSquared;
            double t = len2 < 1e-9 ? 0 : Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2, 0, 1);
            return (p - new Point(a.X + ab.X * t, a.Y + ab.Y * t)).Length;
        }

        public override EditItem Clone() { var c = (MeasureItem)MemberwiseClone(); c.Points = Points.ToList(); c.Label = (TextItem?)Label?.Clone(); return c; }
        public override void MoveBy(Vector d) { for (int i = 0; i < Points.Count; i++) Points[i] += d; if (Label != null) Label.TopLeft += d; }
        public override void ResizeTo(Rect r) { }            // (its size is what was measured: it is not stretched)

        public override FrameworkElement Build()
        {
            var canvas = new Canvas { IsHitTestVisible = false };
            var pts = Live();
            if (pts.Count >= 1)
            {
                var fig = new PathFigure { StartPoint = pts[0], IsClosed = Area && pts.Count >= 3, IsFilled = false };
                foreach (var q in pts.Skip(1)) fig.Segments.Add(new LineSegment(q, true));
                var geo = new PathGeometry(); geo.Figures.Add(fig);
                canvas.Children.Add(new System.Windows.Shapes.Path
                {
                    Data = geo, Stroke = new SolidColorBrush(Color), StrokeThickness = Width, StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeDashArray = Draft ? new DoubleCollection { 4, 3 } : null,
                });
            }
            if (Draft)
            {
                foreach (var p in Points)
                {
                    var dot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Fill = Brushes.White, Stroke = new SolidColorBrush(Color), StrokeThickness = 1.2 };
                    Canvas.SetLeft(dot, p.X - 2.5); Canvas.SetTop(dot, p.Y - 2.5);
                    canvas.Children.Add(dot);
                }
                string live = Calibrate ? "" : Answer(pts);
                if (live.Length > 0 && Cursor is Point c)
                {
                    var tip = new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color), BorderThickness = new Thickness(0.6), Padding = new Thickness(2, 0, 2, 0),
                                           Child = new TextBlock { Text = live, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color) } };
                    Canvas.SetLeft(tip, c.X + 10); Canvas.SetTop(tip, c.Y - 16);
                    canvas.Children.Add(tip);
                }
            }
            else if (Label != null) canvas.Children.Add(Label.Build());
            return canvas;
        }

        public override IEnumerable<PdfMark> Marks()
        {
            if (Points.Count < 2) yield break;
            var figure = new PdfFigure(Points[0], Points.Skip(1).Select(q => PdfSegment.Line(q)).ToList(), Area && Points.Count > 2);
            yield return new PdfPathMark(Page, new[] { figure }, Color, Width, null, false);
            if (Label != null) foreach (var m in Label.Marks()) yield return m;
        }
    }

    // ---------- clicking the points ----------
    private string ScaleText => _ptPerUnit <= 0 ? "no scale set yet" : _paperScale > 0 ? $"1 : {_paperScale:0.##}, in {_unit}" : $"from a known length, in {_unit}";

    private void MeasureToolChosen(EditTool tool)
    {
        MeasureCancel();
        if (_ptPerUnit <= 0) { Dispatcher.BeginInvoke(new Action(AskScale)); return; }
        Toast((tool == EditTool.Area ? "Click the corners of the shape" : "Click each point of the line") + $" (scale {ScaleText}). Double-click or Enter finishes, Esc cancels, Shift keeps it straight");
    }

    private void MeasureClick(PageView pv, Point p, bool doubleClick)
    {
        if (_ptPerUnit <= 0 && !_calibrating) { AskScale(); return; }
        if (_measure != null && _measure.Page != pv.Index) { Toast("Finish on the same page (Enter), or press Esc to start again"); return; }
        if (_measure == null)
        {
            if (doubleClick) return;
            _measure = new MeasureItem { Page = pv.Index, Area = _tool == EditTool.Area && !_calibrating, Calibrate = _calibrating, Color = _toolColors[_calibrating ? EditTool.Measure : _tool], Width = _lineWidth > 0 ? Math.Max(1.2, _lineWidth) : 1.6, PtPerUnit = _ptPerUnit, Unit = _unit };
            _measure.Points.Add(p);
            _drawing = _measure;
            RenderItems(pv.Index);
            return;
        }
        if (doubleClick) { MeasureFinish(); return; }
        p = MeasureSnap(_measure, p);
        if (_measure.Area && _measure.Points.Count >= 3 && (p - _measure.Points[0]).Length < 6) { MeasureFinish(); return; }       // (back on the first corner: the shape is closed)
        _measure.Points.Add(p);
        if (_calibrating) { MeasureFinish(); return; }                                                                            // (a known length is two points)
        RenderItems(pv.Index);
    }

    private void MeasureMove(PageView pv, Point p)
    {
        if (_measure == null || _measure.Page != pv.Index) return;
        p = new Point(Math.Clamp(p.X, 0, pv.Overlay.Width), Math.Clamp(p.Y, 0, pv.Overlay.Height));
        _measure.Cursor = MeasureSnap(_measure, p);
        RenderItems(pv.Index);
    }

    private static Point MeasureSnap(MeasureItem m, Point p)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0 || m.Points.Count == 0) return p;
        var from = m.Points[^1]; var v = p - from;
        double angle = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
        return from + new Vector(Math.Cos(angle), Math.Sin(angle)) * v.Length;
    }

    private void MeasureCancel()
    {
        if (_measure == null) return;
        int page = _measure.Page;
        _measure = null; _drawing = null; _calibrating = false;
        RenderItems(page);
    }

    private void MeasureFinish()
    {
        var m = _measure;
        if (m == null) return;
        _measure = null; _drawing = null;
        m.Cursor = null; m.Draft = false;
        while (m.Points.Count > 1 && (m.Points[^1] - m.Points[^2]).Length < 1) m.Points.RemoveAt(m.Points.Count - 1);       // (a double-click puts the same point twice)
        if (m.Points.Count < (m.Area ? 3 : 2)) { RenderItems(m.Page); Toast(m.Area ? "An area needs at least three corners" : "A line needs at least two points"); return; }
        if (m.Calibrate) { FinishCalibration(m); return; }
        m.MakeLabel();
        Add(m, select: true);
    }

    // ---------- the scale ----------
    private void AskScale()
    {
        var answer = UMessage.Ask(this,
            "The drawing needs a scale before it can be measured.\n\n" +
            "Printed scale: type what is written on the drawing (1 : 100). Right when the PDF is printed at 100% on the paper size it was made for.\n\n" +
            "Known length: draw a line along something whose real length you know (a wall, a lot side), then type that length.",
            "Set the scale", MessageBoxImage.Question, MessageBoxResult.Yes, MessageBoxResult.Cancel,
            ("Printed scale…", MessageBoxResult.Yes), ("Known length…", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel));
        if (answer == MessageBoxResult.Yes) AskPrintedScale();
        else if (answer == MessageBoxResult.No)
        {
            _calibrating = true;
            _toolButtons[EditTool.Measure].IsChecked = true;
            Toast("Click both ends of a length you know");
        }
    }

    private void AskPrintedScale()
    {
        string? typed = TextPrompt.Ask(this, "Printed scale", "Type the scale and the unit to measure in, for example  1:100 m   (or 1:50 cm,  1:200 ft,  1:500 m)", _paperScale > 0 ? $"1:{_paperScale:0.##} {_unit}" : "1:100 m");
        if (typed == null) return;
        var m = Regex.Match(typed, @"^\s*1\s*[:/]\s*([0-9]+(?:[.,][0-9]+)?)\s*([a-zA-Z""']*)\s*$");
        string? unit = m.Success ? (m.Groups[2].Value.Length == 0 ? _unit : UnitName(m.Groups[2].Value)) : null;
        if (!m.Success || unit == null || !double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || n <= 0)
        {
            Toast("Type it like 1:100 m (the scale, then m, cm, mm, ft or in)");
            return;
        }
        double mm = LengthUnits.First(u => u.Name == unit).Mm;
        _ptPerUnit = mm / (n * 25.4 / 72);                          // (1 pt = 25.4 / 72 mm on the paper; the drawing is n times bigger in real life)
        _unit = unit; _paperScale = n;
        Toast($"Scale set: 1 : {n:0.##}, measuring in {unit}");
    }

    private void FinishCalibration(MeasureItem m)
    {
        _calibrating = false;
        RenderItems(m.Page);
        double pts = MeasureItem.LengthOf(m.Points, false);
        string? typed = TextPrompt.Ask(this, "Known length", "How long is that line in real life? Type the length and its unit, for example  12.5 m   or  40 ft", "");
        if (typed == null) return;
        var r = Regex.Match(typed, @"^\s*([0-9]+(?:[.,][0-9]+)?)\s*([a-zA-Z""']*)\s*$");
        string? unit = r.Success ? (r.Groups[2].Value.Length == 0 ? _unit : UnitName(r.Groups[2].Value)) : null;
        if (!r.Success || unit == null || !double.TryParse(r.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value <= 0 || pts < 2)
        {
            Toast("Type the length like 12.5 m (a number, then m, cm, mm, ft or in)");
            return;
        }
        _ptPerUnit = pts / value; _unit = unit; _paperScale = 0;
        Toast($"Scale set: that line is {value:0.##} {unit}");
    }
}
