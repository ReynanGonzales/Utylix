using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Editing in Utylix PDF: add text, signatures, pictures, check marks, highlights, pen strokes, shapes and white-out. Everything stays
/// movable (Select) until it is saved; saving writes it into the pages (it then looks the same in every PDF reader).
/// </summary>
public sealed partial class PdfWindow
{
    private enum EditTool { Select, EditText, Text, Signature, Image, Check, Cross, Stamp, Date, Highlight, Underline, Strike, Note, Pen, Shapes, WhiteOut, Redact, TextField, CheckField, Table, RadioField, SignField, DropField, Link }

    // ---------- what can be on a page ----------
    private abstract class EditItem
    {
        public int Page;
        public Color Color;
        public int GroupId;                            // 0 = on its own; things with the same number are grouped (they are chosen, moved, copied and deleted together)
        public abstract Rect Bounds { get; }
        public abstract EditItem Clone();
        public abstract FrameworkElement Build();
        public abstract IEnumerable<PdfMark> Marks();
        public abstract void MoveBy(Vector d);
        public abstract void ResizeTo(Rect r);
        public virtual bool KeepAspect => false;
        /// <summary>Items that can be turned (stamps): the angle in degrees, clockwise, around the middle of <see cref="Bounds"/>.</summary>
        public virtual bool CanRotate => false;
        public virtual double Angle => 0;
        public virtual void SetAngle(double degrees) { }
        public virtual bool Hit(Point p) => Inflate(Bounds, 3).Contains(p);
        protected static Rect Inflate(Rect r, double by) { r.Inflate(by, by); return r; }

        /// <summary>The top-left to give a box of this size so that its TURNED top-left corner stays at <paramref name="corner"/> (turned around its middle).</summary>
        protected static Point TopLeftFor(Point corner, Size size, double angle)
        {
            var half = new Vector(size.Width / 2, size.Height / 2);
            var turned = Rot(new Point(half.X, half.Y), new Point(0, 0), angle) - new Point(0, 0);
            return corner - half + turned;
        }
    }

    private sealed class TextItem : EditItem
    {
        public Point TopLeft;
        public string Text = "";
        public PdfFontKind Font;
        public bool Bold;
        public double FontSize = 12;

        public string? FontName;                       // a font chosen by name (any installed one); null = the kind above
        public double AngleDeg;                        // turned clockwise around the middle of the text

        public bool Italic, Underline;
        public int Align;                              // 0 left, 1 middle, 2 right (inside the box width, or inside the longest line)
        public double BoxWidth;                        // 0 = the box fits the longest line; otherwise the text wraps at this width (points)
        public Color? Fill;                            // background of the box (null = none)
        public double BorderWidth;                     // frame of the box in the text's colour (0 = none)

        public const double Pad = 3;                   // room between the frame and the words
        public bool HasBox => Fill != null || BorderWidth > 0;
        public double Inset => HasBox ? Pad : 0;

        public override bool CanRotate => true;
        public override double Angle => AngleDeg;
        public override void SetAngle(double degrees) => AngleDeg = degrees;
        Point Centre { get { var b = Bounds; return new Point(b.X + b.Width / 2, b.Y + b.Height / 2); } }
        public override bool Hit(Point p) => Inflate(Bounds, 3).Contains(AngleDeg == 0 ? p : Rot(p, Centre, -AngleDeg));

        public static FontFamily Family(PdfFontKind k, string? name = null) => new(name ?? k switch { PdfFontKind.Serif => "Times New Roman", PdfFontKind.Mono => "Courier New", _ => "Arial" });

        Typeface Face => new(Family(Font, FontName), Italic ? FontStyles.Italic : FontStyles.Normal, Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        double Measure(string s) => new FormattedText(s.Length == 0 ? " " : s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, FontSize, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;
        public double LineHeight => Family(Font, FontName).LineSpacing * FontSize;

        /// <summary>The lines as they are shown and written: the text's own lines, broken at the box width when there is one (words are never cut, except one longer than the whole line).</summary>
        public List<string> Lines()
        {
            var raw = (Text.Length == 0 ? " " : Text).Replace("\r\n", "\n").Split('\n');
            if (BoxWidth <= 0) return raw.ToList();
            double room = Math.Max(10, BoxWidth - 2 * Inset);
            var result = new List<string>();
            foreach (string paragraph in raw)
            {
                if (paragraph.Length == 0 || Measure(paragraph) <= room) { result.Add(paragraph); continue; }
                string line = "";
                foreach (string word in paragraph.Split(' '))
                {
                    string tryLine = line.Length == 0 ? word : line + " " + word;
                    if (line.Length > 0 && Measure(tryLine) > room) { result.Add(line); line = word; }
                    else line = tryLine;
                    while (line.Length > 1 && Measure(line) > room)                            // (one long word: cut where it no longer fits)
                    {
                        int cut = line.Length - 1;
                        while (cut > 1 && Measure(line[..cut]) > room) cut--;
                        result.Add(line[..cut]); line = line[cut..];
                    }
                }
                result.Add(line);
            }
            return result;
        }

        public override Rect Bounds
        {
            get
            {
                var lines = Lines();
                double w = BoxWidth > 0 ? BoxWidth : lines.Max(Measure) + 2 * Inset;
                return new Rect(TopLeft, new Size(Math.Max(4, w), lines.Count * LineHeight + 2 * Inset));
            }
        }

        /// <summary>Where a line starts (x, relative to the box's left edge).</summary>
        double LineX(string line, double boxWidth) => Inset + Align switch { 1 => (boxWidth - 2 * Inset - Measure(line)) / 2, 2 => boxWidth - 2 * Inset - Measure(line), _ => 0 };

        public override EditItem Clone() => (TextItem)MemberwiseClone();
        public override FrameworkElement Build()
        {
            var b = Bounds; var lines = Lines();
            var canvas = new Canvas { Width = b.Width, Height = b.Height };
            if (HasBox)
            {
                var frame = new Rectangle { Width = b.Width, Height = b.Height, Fill = Fill is Color f ? new SolidColorBrush(f) : null };
                if (BorderWidth > 0) { frame.Stroke = new SolidColorBrush(Color); frame.StrokeThickness = BorderWidth; }
                canvas.Children.Add(frame);
            }
            double lineH = LineHeight;
            for (int i = 0; i < lines.Count; i++)
            {
                var t = new TextBlock { Text = lines[i].Length == 0 ? " " : lines[i], FontFamily = Family(Font, FontName), FontSize = FontSize, FontStyle = Italic ? FontStyles.Italic : FontStyles.Normal, FontWeight = Bold ? FontWeights.Bold : FontWeights.Normal, Foreground = new SolidColorBrush(Color), TextWrapping = TextWrapping.NoWrap };
                if (Underline) t.TextDecorations = System.Windows.TextDecorations.Underline;
                Canvas.SetLeft(t, LineX(lines[i], b.Width)); Canvas.SetTop(t, Inset + i * lineH);
                canvas.Children.Add(t);
            }
            Canvas.SetLeft(canvas, b.X); Canvas.SetTop(canvas, b.Y);
            if (AngleDeg != 0) canvas.RenderTransform = new RotateTransform(AngleDeg, b.Width / 2, b.Height / 2);
            return canvas;
        }

        public override IEnumerable<PdfMark> Marks()
        {
            if (Text.Trim().Length == 0) yield break;
            var f = Family(Font, FontName);
            var b = Bounds; var pivot = AngleDeg != 0 ? (Point?)Centre : null;
            Point Turn(Point p) => AngleDeg == 0 ? p : Rot(p, Centre, AngleDeg);
            if (HasBox)
            {
                var corners = new[] { b.TopLeft, new Point(b.Right, b.Top), b.BottomRight, new Point(b.Left, b.Bottom) };
                var fig = new PdfFigure(Turn(corners[0]), corners.Skip(1).Select(c => PdfSegment.Line(Turn(c))).ToList(), true);
                yield return new PdfPathMark(Page, new[] { fig }, BorderWidth > 0 ? Color : null, BorderWidth, Fill, false);
            }
            var lines = Lines(); double lineH = LineHeight;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Trim().Length == 0) continue;
                double x = b.X + LineX(lines[i], b.Width), y = b.Y + Inset + i * lineH;
                yield return new PdfTextMark(Page, new Point(x, y), lines[i], Font, Bold, FontSize, Color, f.LineSpacing, f.Baseline, FontName, AngleDeg, pivot, Italic: Italic);
                if (Underline)
                {
                    double ly = y + (f.Baseline + 0.1) * FontSize, w = Measure(lines[i]);
                    var line = new PdfFigure(Turn(new Point(x, ly)), new[] { PdfSegment.Line(Turn(new Point(x + w, ly))) }, false);
                    yield return new PdfPathMark(Page, new[] { line }, Color, Math.Max(0.5, FontSize / 15), null, false);
                }
            }
        }
        public override void MoveBy(Vector d) => TopLeft += d;
        public override void ResizeTo(Rect r)
        {
            var corner = AngleDeg == 0 ? r.TopLeft : Rot(Bounds.TopLeft, Centre, AngleDeg);        // (a turned text keeps its turned top-left corner where it is)
            if (BoxWidth > 0) BoxWidth = Math.Max(24, r.Width);                                       // (a box with a width: the corner changes the width, the words wrap again)
            else
            {
                double h = Bounds.Height;
                if (h > 0) FontSize = Math.Clamp(FontSize * r.Height / h, 4, 200);
            }
            TopLeft = AngleDeg == 0 ? r.TopLeft : TopLeftFor(corner, Bounds.Size, AngleDeg);
        }
        public override bool KeepAspect => BoxWidth <= 0;
    }
    public enum ShapeKind { Rectangle, Ellipse, Line, Arrow, Highlight, WhiteOut, Check, Cross, Redact }

    private sealed class ShapeItem : EditItem
    {
        public ShapeKind Kind;
        public Point A, B;                       // corners, or the two ends of a line
        public double Width = 2;
        public Color? Fill;                      // the inside of a box or circle (null = see-through)
        public double AngleDeg;                  // box, circle, check and cross can be turned (clockwise, around the middle)

        public override bool CanRotate => Kind is ShapeKind.Rectangle or ShapeKind.Ellipse or ShapeKind.Check or ShapeKind.Cross;
        public override double Angle => AngleDeg;
        public override void SetAngle(double degrees) => AngleDeg = degrees;
        Point Centre => new(Box.X + Box.Width / 2, Box.Y + Box.Height / 2);

        bool IsLine => Kind is ShapeKind.Line or ShapeKind.Arrow;
        Rect Box => new(A, B);
        double StampWidth => Math.Max(1.2, Math.Min(Box.Width, Box.Height) * 0.13);

        public override Rect Bounds => IsLine ? Inflate(Box, Width / 2 + (Kind == ShapeKind.Arrow ? ArrowHead : 0)) : Box;
        double ArrowHead => Math.Max(7, Width * 4);
        public override EditItem Clone() => (ShapeItem)MemberwiseClone();
        public override bool KeepAspect => Kind is ShapeKind.Check or ShapeKind.Cross;

        public override bool Hit(Point p)
        {
            if (!IsLine) return Inflate(Box, 3).Contains(AngleDeg == 0 ? p : Rot(p, Centre, -AngleDeg));
            // near the line
            Vector ab = B - A, ap = p - A;
            double t = ab.LengthSquared < 0.01 ? 0 : Math.Clamp((ap * ab) / ab.LengthSquared, 0, 1);
            return (p - (A + ab * t)).Length <= Width / 2 + 4;
        }

        public List<PdfFigure> Figures()
        {
            var figures = FlatFigures();
            return AngleDeg == 0 ? figures : TurnFigures(figures, Centre, AngleDeg);
        }

        List<PdfFigure> FlatFigures()
        {
            var r = Box;
            switch (Kind)
            {
                case ShapeKind.Ellipse: return new() { Ellipse(r) };
                case ShapeKind.Line: return new() { new PdfFigure(A, new[] { PdfSegment.Line(B) }, false) };
                case ShapeKind.Arrow:
                {
                    var figs = new List<PdfFigure> { new(A, new[] { PdfSegment.Line(B) }, false) };
                    Vector back = A - B;
                    if (back.Length > 0.1)
                    {
                        back.Normalize(); back *= ArrowHead;
                        var left = B + Rotate(back, 28); var right = B + Rotate(back, -28);
                        figs.Add(new PdfFigure(left, new[] { PdfSegment.Line(B), PdfSegment.Line(right) }, false));
                    }
                    return figs;
                }
                case ShapeKind.Check:
                    return new() { new PdfFigure(At(r, 0.08, 0.55), new[] { PdfSegment.Line(At(r, 0.38, 0.86)), PdfSegment.Line(At(r, 0.93, 0.12)) }, false) };
                case ShapeKind.Cross:
                    return new() { new PdfFigure(At(r, 0.12, 0.12), new[] { PdfSegment.Line(At(r, 0.88, 0.88)) }, false), new PdfFigure(At(r, 0.88, 0.12), new[] { PdfSegment.Line(At(r, 0.12, 0.88)) }, false) };
                default: return new() { new PdfFigure(r.TopLeft, new[] { PdfSegment.Line(r.TopRight), PdfSegment.Line(r.BottomRight), PdfSegment.Line(r.BottomLeft) }, true) };
            }
        }

        static Point At(Rect r, double x, double y) => new(r.X + r.Width * x, r.Y + r.Height * y);
        static Vector Rotate(Vector v, double degrees) { double a = degrees * Math.PI / 180; return new Vector(v.X * Math.Cos(a) - v.Y * Math.Sin(a), v.X * Math.Sin(a) + v.Y * Math.Cos(a)); }

        (Color? Stroke, double Width, Color? Fill, bool Multiply, double ShownOpacity) Paint() => Kind switch
        {
            ShapeKind.Highlight => (null, 0, Color.FromArgb(140, Color.R, Color.G, Color.B), true, 1),
            ShapeKind.WhiteOut => (null, 0, Color, false, 1),
            ShapeKind.Redact => (null, 0, Colors.Black, false, 1),
            ShapeKind.Check or ShapeKind.Cross => (Color, StampWidth, null, false, 1),
            ShapeKind.Rectangle or ShapeKind.Ellipse => (Color, Width, Fill, false, 1),
            _ => (Color, Width, null, false, 1),
        };

        public override FrameworkElement Build()
        {
            var (stroke, width, fill, multiply, _) = Paint();
            var path = new System.Windows.Shapes.Path
            {
                Data = Geometry(Figures()),
                Stroke = stroke is Color s ? new SolidColorBrush(s) : null,
                StrokeThickness = width,
                Fill = fill is Color f ? new SolidColorBrush(multiply ? Color.FromArgb(105, f.R, f.G, f.B) : f) : null,
                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            };
            if (Kind == ShapeKind.WhiteOut) path.Effect = null;
            if (Kind == ShapeKind.Redact)
            {
                // black like it will be, with a red dashed edge while it can still be changed (the edge is not saved)
                path.Stroke = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)); path.StrokeThickness = 1.6; path.StrokeDashArray = new DoubleCollection { 4, 3 };
            }
            return path;
        }

        public override IEnumerable<PdfMark> Marks()
        {
            var (stroke, width, fill, multiply, _) = Paint();
            if (Kind == ShapeKind.Redact) yield return new PdfRedactMark(Page, Box);       // (what is under it goes for good when saved; the black box is drawn after)
            yield return new PdfPathMark(Page, Figures(), stroke, width, fill, multiply);
        }
        public override void MoveBy(Vector d) { A += d; B += d; }
        public override void ResizeTo(Rect r)
        {
            var old = IsLine ? Box : Bounds;
            if (AngleDeg != 0 && !IsLine) r = new Rect(TopLeftFor(Rot(old.TopLeft, new Point(old.X + old.Width / 2, old.Y + old.Height / 2), AngleDeg), r.Size, AngleDeg), r.Size);       // (a turned shape keeps its turned top-left corner)
            Point Map(Point p) => new(r.X + (old.Width < 0.01 ? 0 : (p.X - old.X) / old.Width * r.Width), r.Y + (old.Height < 0.01 ? 0 : (p.Y - old.Y) / old.Height * r.Height));
            A = Map(A); B = Map(B);
        }
    }

    private sealed class InkItem : EditItem
    {
        public List<List<Point>> Strokes = new();
        public double Width = 2;
        public bool Signature;
        public double AngleDeg;                          // turned clockwise around the middle of the drawing

        public override bool CanRotate => true;
        public override double Angle => AngleDeg;
        public override void SetAngle(double degrees) => AngleDeg = degrees;
        Point Centre { get { var b = Bounds; return b.IsEmpty ? default : new Point(b.X + b.Width / 2, b.Y + b.Height / 2); } }
        public override bool Hit(Point p) => Inflate(Bounds, 3).Contains(AngleDeg == 0 ? p : Rot(p, Centre, -AngleDeg));
        public override Rect Bounds
        {
            get
            {
                var all = Strokes.SelectMany(s => s).ToList();
                if (all.Count == 0) return Rect.Empty;
                var r = new Rect(all[0], all[0]);
                foreach (var p in all) r.Union(p);
                return Inflate(r, Width / 2);
            }
        }
        public override bool KeepAspect => Signature;
        public override EditItem Clone() { var c = (InkItem)MemberwiseClone(); c.Strokes = Strokes.Select(s => s.ToList()).ToList(); return c; }
        public List<PdfFigure> Figures()
        {
            var figures = Strokes.Where(s => s.Count > 0).Select(Smooth).ToList();
            return AngleDeg == 0 || figures.Count == 0 ? figures : TurnFigures(figures, Centre, AngleDeg);
        }

        /// <summary>A hand-drawn stroke as soft curves through the middles of its points.</summary>
        static PdfFigure Smooth(List<Point> s)
        {
            if (s.Count < 3) return new PdfFigure(s[0], new[] { PdfSegment.Line(s.Count > 1 ? s[1] : s[0] + new Vector(0.01, 0)) }, false);
            var segs = new List<PdfSegment>();
            Point last = s[0];
            for (int i = 1; i < s.Count - 1; i++)
            {
                Point mid = new((s[i].X + s[i + 1].X) / 2, (s[i].Y + s[i + 1].Y) / 2);
                // quadratic (last, s[i], mid) as a cubic
                segs.Add(PdfSegment.Bezier(last + (s[i] - last) * 2.0 / 3, mid + (s[i] - mid) * 2.0 / 3, mid));
                last = mid;
            }
            segs.Add(PdfSegment.Line(s[^1]));
            return new PdfFigure(s[0], segs, false);
        }
        public override FrameworkElement Build() => new System.Windows.Shapes.Path
        {
            Data = Geometry(Figures()), Stroke = new SolidColorBrush(Color), StrokeThickness = Width,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        public override IEnumerable<PdfMark> Marks() { yield return new PdfPathMark(Page, Figures(), Color, Width, null, false); }
        public override void MoveBy(Vector d) { foreach (var s in Strokes) for (int i = 0; i < s.Count; i++) s[i] += d; }
        public override void ResizeTo(Rect r)
        {
            var old = Bounds;
            if (old.Width < 0.5 || old.Height < 0.5) return;
            if (AngleDeg != 0) r = new Rect(TopLeftFor(Rot(old.TopLeft, Centre, AngleDeg), r.Size, AngleDeg), r.Size);          // (a turned drawing keeps its turned top-left corner)
            double sx = r.Width / old.Width, sy = r.Height / old.Height;
            foreach (var s in Strokes) for (int i = 0; i < s.Count; i++) s[i] = new Point(r.X + (s[i].X - old.X) * sx, r.Y + (s[i].Y - old.Y) * sy);
            if (Signature) Width *= Math.Sqrt(sx * sy);
        }
    }

    private sealed class ImageItem : EditItem
    {
        public Rect Box;
        public byte[]? Jpeg;
        public BitmapSource Pixels = null!;
        public double AngleDeg;                          // turned clockwise around the middle of the picture
        public override Rect Bounds => Box;
        public override bool KeepAspect => true;
        public override bool CanRotate => true;
        public override double Angle => AngleDeg;
        public override void SetAngle(double degrees) => AngleDeg = degrees;
        public override bool Hit(Point p) => Inflate(Box, 3).Contains(AngleDeg == 0 ? p : Rot(p, new Point(Box.X + Box.Width / 2, Box.Y + Box.Height / 2), -AngleDeg));
        public override EditItem Clone() => (ImageItem)MemberwiseClone();
        public override FrameworkElement Build()
        {
            var img = new Image { Source = Pixels, Width = Box.Width, Height = Box.Height, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            Canvas.SetLeft(img, Box.X); Canvas.SetTop(img, Box.Y);
            if (AngleDeg != 0) img.RenderTransform = new RotateTransform(AngleDeg, Box.Width / 2, Box.Height / 2);
            return img;
        }
        public override IEnumerable<PdfMark> Marks()
        {
            yield return new PdfImageMark(Page, Box, Jpeg, Jpeg == null ? Pixels : null, Angle: AngleDeg, Pivot: AngleDeg != 0 ? new Point(Box.X + Box.Width / 2, Box.Y + Box.Height / 2) : null);
        }
        public override void MoveBy(Vector d) => Box.Offset(d);
        public override void ResizeTo(Rect r) => Box = r;
    }

    private static PdfFigure Ellipse(Rect r)
    {
        double k = 0.5523, cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, rx = r.Width / 2, ry = r.Height / 2;
        return new PdfFigure(new Point(cx + rx, cy), new[]
        {
            PdfSegment.Bezier(new Point(cx + rx, cy + k * ry), new Point(cx + k * rx, cy + ry), new Point(cx, cy + ry)),
            PdfSegment.Bezier(new Point(cx - k * rx, cy + ry), new Point(cx - rx, cy + k * ry), new Point(cx - rx, cy)),
            PdfSegment.Bezier(new Point(cx - rx, cy - k * ry), new Point(cx - k * rx, cy - ry), new Point(cx, cy - ry)),
            PdfSegment.Bezier(new Point(cx + k * rx, cy - ry), new Point(cx + rx, cy - k * ry), new Point(cx + rx, cy)),
        }, true);
    }

    /// <summary>The same figures as WPF draws them on screen (so the screen and the saved file agree).</summary>
    private static Geometry Geometry(IEnumerable<PdfFigure> figures)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
            foreach (var f in figures)
            {
                c.BeginFigure(f.Start, true, f.Closed);
                foreach (var s in f.Segments)
                    if (s.Curve) c.BezierTo(s.C1, s.C2, s.To, true, true); else c.LineTo(s.To, true, true);
            }
        g.Freeze();
        return g;
    }

    // ---------- editing state ----------
    private bool _editing, _dirty;
    private EditTool _tool = EditTool.Text;
    private readonly List<EditItem> _items = new();
    private readonly Stack<List<EditItem>> _undo = new(), _redo = new();
    private EditItem? _selected;
    private readonly Dictionary<EditTool, Color> _toolColors = new()
    {
        [EditTool.TextField] = Colors.Black, [EditTool.CheckField] = Colors.Black, [EditTool.Table] = Colors.Black, [EditTool.RadioField] = Colors.Black, [EditTool.SignField] = Colors.Black, [EditTool.DropField] = Colors.Black,
        [EditTool.Text] = Colors.Black, [EditTool.Stamp] = Color.FromRgb(0x2E, 0x7D, 0x32), [EditTool.Date] = Colors.Black, [EditTool.Signature] = Color.FromRgb(0x10, 0x2A, 0x8C), [EditTool.Check] = Colors.Black, [EditTool.Cross] = Colors.Black,
        [EditTool.Highlight] = Color.FromRgb(0xFF, 0xE0, 0x30), [EditTool.Underline] = Color.FromRgb(0x1E, 0x63, 0xE9), [EditTool.Strike] = Color.FromRgb(0xD3, 0x2F, 0x2F),
        [EditTool.Note] = Color.FromRgb(0xFF, 0xD5, 0x4F), [EditTool.Pen] = Color.FromRgb(0x10, 0x2A, 0x8C), [EditTool.Shapes] = Color.FromRgb(0xD3, 0x2F, 0x2F), [EditTool.WhiteOut] = Colors.White, [EditTool.Redact] = Colors.Black,
    };
    private ShapeKind _shapeKind = ShapeKind.Rectangle;                   // (what the Shapes tool draws)
    private double _textSize = 12, _lineWidth = 2;
    private PdfFontKind _font = PdfFontKind.Sans;
    private bool _bold;

    // dragging
    private enum DragMode { None, Move, Resize, Draw, Ink, Rotate, RunMove, Marquee, GroupMove }
    private readonly List<EditItem> _group = new();                     // several items chosen with a box (Select tool): they move / delete together
    private Rect? _marquee;                                             // the box being dragged to choose them
    private int _marqueePage;
    private Point _groupLast;
    private RunEditItem? _runDragItem;                                 // Edit text: a line pressed on, to move (or, without moving, to change)
    private PdfTextRun? _runDragRun;
    private Vector _runDragStartOffset;
    private bool _runDragMoved;
    private double _rotateFrom, _rotateStart;                         // (the angle of the mouse and of the item when turning began)
    private DragMode _drag;
    private PageView? _dragPage;
    private Point _dragStart;
    private Rect _dragBox;
    private bool _dragSnapshotTaken;
    private EditItem? _drawing;

    // typing
    private TextBox? _textBox;
    private TextItem? _typing;

    // parts
    private Border _editBar = null!;
    private Button _editButton = null!;
    private readonly Dictionary<EditTool, RadioButton> _toolButtons = new();
    private readonly StackPanel _colorRow = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _sizeText = new() { Width = 34, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White };
    private readonly TextBlock _sizeLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0), Foreground = Soft, FontSize = 12 };
    private readonly StackPanel _fontRow = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<PdfFontKind, RadioButton> _fontButtons = new();
    private ToggleButton _boldButton = null!;
    private Button _undoButton = null!, _redoButton = null!, _deleteButton = null!, _saveButton = null!;

    private readonly List<(Button Button, Color Color)> _swatchButtons = new();
    private Button _moreColor = null!;

    /// <summary>Rings the colour dot that is in use (the selected item's colour, else the tool's); a colour that isn't a dot shows on the "…" button.</summary>
    private void MarkColor()
    {
        if (_moreColor == null) return;
        Color now = _runTyping != null ? _runTyping.EffColor : _selected != null && _selected is not ImageItem ? _selected.Color : _toolColors.TryGetValue(_tool, out var tc) ? tc : Colors.Black;
        bool known = false;
        foreach (var (b, c) in _swatchButtons)
        {
            bool same = c == now;
            known |= same;
            b.Tag = same ? "picked" : null;
        }
        if (known) { _moreColor.Content = "…"; _moreColor.ToolTip = "More colours"; }
        else
        {
            _moreColor.Content = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(now), BorderBrush = Brushes.White, BorderThickness = new Thickness(2) };
            _moreColor.ToolTip = "Your colour (click for more colours)";
        }
    }

    private static readonly Color[] Swatches =
    {
        Colors.Black, Color.FromRgb(0x10, 0x2A, 0x8C), Color.FromRgb(0x1E, 0x63, 0xE9), Color.FromRgb(0xD3, 0x2F, 0x2F),
        Color.FromRgb(0x2E, 0x7D, 0x32), Color.FromRgb(0xFF, 0xE0, 0x30), Colors.White,
    };

    private Button EditButton()
    {
        _editButton = new Button
        {
            Content = BarLabel("Edit"), Template = BarButtonTemplate(),  Height = 30, Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(8, 0, 0, 0), Focusable = false, Background = Brushes.Transparent, Foreground = Brushes.White,
            ToolTip = "Add text, a signature, pictures, check marks, highlights, drawings and white-out",
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_editButton, "PdfEdit");
        _editButton.Click += (_, _) => { if (_editing) ExitEditing(); else EnterEditing(); };
        return _editButton;
    }

    /// <summary>Alt + a letter picks each tool while editing (a plain letter would be typed into a text by accident). Shown in the tool's tip and in Settings &gt; Hotkeys.</summary>
    private static readonly (EditTool Tool, Key Key, string Letter)[] ToolKeys =
    {
        (EditTool.Select, Key.V, "V"), (EditTool.EditText, Key.E, "E"), (EditTool.Text, Key.T, "T"), (EditTool.Signature, Key.G, "G"), (EditTool.Image, Key.I, "I"),
        (EditTool.Check, Key.C, "C"), (EditTool.Cross, Key.X, "X"), (EditTool.Stamp, Key.M, "M"), (EditTool.Date, Key.D, "D"), (EditTool.Highlight, Key.H, "H"),
        (EditTool.Underline, Key.U, "U"), (EditTool.Strike, Key.K, "K"), (EditTool.Note, Key.N, "N"), (EditTool.Pen, Key.P, "P"), (EditTool.Shapes, Key.S, "S"),
        (EditTool.WhiteOut, Key.W, "W"), (EditTool.Redact, Key.R, "R"), (EditTool.TextField, Key.F, "F"), (EditTool.CheckField, Key.B, "B"), (EditTool.Table, Key.L, "L"), (EditTool.RadioField, Key.O, "O"), (EditTool.SignField, Key.Q, "Q"), (EditTool.DropField, Key.Y, "Y"), (EditTool.Link, Key.J, "J"),
    };

    // the tabs of the tool bar (see BuildEditBar)
    private readonly Dictionary<string, RadioButton> _tabChips = new();
    private readonly Dictionary<string, StackPanel> _tabPanels = new();
    private string _editTab = LoadEditTab();

    // the tab you were on is remembered (a one-word file in the data folder)
    private static string TabFile => System.IO.Path.Combine(App.DataDir, "pdf-editor-tab.txt");
    private static string LoadEditTab()
    {
        try { string t = System.IO.File.Exists(TabFile) ? System.IO.File.ReadAllText(TabFile).Trim() : ""; return t is "Add" or "Mark up" or "Forms" or "Page" or "Convert" ? t : "Add"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "Add"; }
    }

    private void ShowTab(string name)
    {
        if (!_tabPanels.ContainsKey(name)) return;
        if (_editTab != name) { try { System.IO.File.WriteAllText(TabFile, name); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* not kept this time */ } }
        _editTab = name;
        foreach (var (n, panel) in _tabPanels) panel.Visibility = n == name ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Choosing a tool by its key (or from anywhere else) shows the tab it lives on.</summary>
    private static string? TabOf(EditTool tool) => tool switch
    {
        EditTool.Text or EditTool.EditText or EditTool.Date or EditTool.Image or EditTool.Signature or EditTool.Stamp or EditTool.Link => "Add",
        EditTool.Highlight or EditTool.Underline or EditTool.Strike or EditTool.Note or EditTool.Pen or EditTool.Shapes or EditTool.Table or EditTool.Check or EditTool.Cross => "Mark up",
        EditTool.TextField or EditTool.CheckField or EditTool.RadioField or EditTool.SignField or EditTool.DropField => "Forms",
        EditTool.WhiteOut or EditTool.Redact => "Page",
        _ => null,                                                          // (Select is on every tab)
    };

    private UIElement BuildEditBar()
    {
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var actions = new Dictionary<string, Button>();                       // (the buttons that open a dialog instead of being a chosen tool)
        void ToolButton(EditTool tool, string glyph, string label, string tip, string font = "Segoe MDL2 Assets", UIElement? icon = null)
        {
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(icon ?? new TextBlock { Text = glyph, FontFamily = new FontFamily(font), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.White });
            content.Children.Add(new TextBlock { Text = label, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Soft, Margin = new Thickness(0, 2, 0, 0) });
            string letter = Array.Find(ToolKeys, k => k.Tool == tool).Letter;
            var b = new RadioButton { Content = content, GroupName = "pdftool", ToolTip = letter == null ? tip : tip + "  [Alt+" + letter + "]", Template = ToolChoiceTemplate(), Focusable = false, Margin = new Thickness(1, 0, 1, 0) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, "PdfTool" + tool);
            System.Windows.Automation.AutomationProperties.SetName(b, label);
            b.Checked += (_, _) => SetTool(tool);
            _toolButtons[tool] = b;
            tools.Children.Add(b);
        }
        void ActionButton(string label, string tip, Action action, string id, UIElement icon)
        {
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(icon);
            content.Children.Add(new TextBlock { Text = label, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Soft, Margin = new Thickness(0, 2, 0, 0) });
            var b = new Button { Content = content, ToolTip = tip, Template = ToolActionTemplate(), Focusable = false, Margin = new Thickness(1, 0, 1, 0) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, id);
            System.Windows.Automation.AutomationProperties.SetName(b, label);
            b.Click += (_, _) => action();
            tools.Children.Add(b);
            actions[id] = b;
        }
        ToolButton(EditTool.Select, "↖", "Select", "Select, move and resize what you added (double-click a text to change it)", "Segoe UI Symbol");
        ToolButton(EditTool.Text, "", "Text", "Click anywhere to type (also for filling in forms)");
        ToolButton(EditTool.EditText, "", "Edit text", "Click on text already in the PDF to change it (Enter or click elsewhere when done)");
        ActionButton("Columns", "Text in columns, like Word: choose how many columns and type the words", AddColumns, "PdfActionColumns",
                     new StackPanel
                     {
                         Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0),
                         Children = { ColumnBar(), ColumnBar(), ColumnBar() },
                     });
        ToolButton(EditTool.Signature,"", "Sign", "Add your signature (draw it once, use it again)");
        ToolButton(EditTool.Image, "", "Picture", "Add a picture");
        ToolButton(EditTool.Check, "", "Check", "Click to put a check mark");
        ToolButton(EditTool.Cross, "", "Cross", "Click to put a cross");
        ToolButton(EditTool.Stamp, "", "Stamp", "Put a stamp (Approved, Paid, Confidential ...) on the page: click this button again to choose which",
                   icon: new Border { BorderBrush = Brushes.White, BorderThickness = new Thickness(1.6), CornerRadius = new CornerRadius(4), Padding = new Thickness(3, 0, 3, 0), Height = 19, HorizontalAlignment = HorizontalAlignment.Center,
                                      Child = new TextBlock { Text = "OK", FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center } });
        ToolButton(EditTool.Date, "", "Date", "Put today's date on the page: click this button again to choose how it looks");
        ToolButton(EditTool.Highlight, "", "Highlight", "Drag over text to highlight it (or drag a box anywhere)");
        ToolButton(EditTool.Underline, "U̲", "Underline", "Drag over text to underline it", "Segoe UI");
        ToolButton(EditTool.Strike, "S̶", "Strike", "Drag over text to strike it out", "Segoe UI");
        ToolButton(EditTool.Note, "", "Note", "Click to add a sticky note (a comment other PDF readers show too)");
        ToolButton(EditTool.Pen, "", "Pen", "Draw freely");
        ToolButton(EditTool.TextField, "", "Text box", "A fillable text box (a real form field): drag its size on the page. Anyone can type in it, here or in any PDF reader once saved",
                   icon: new Border { BorderBrush = Brushes.White, BorderThickness = new Thickness(1.4), CornerRadius = new CornerRadius(2), Width = 26, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0),
                                      Child = new TextBlock { Text = "I", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, -1, 0, 0) } });
        ToolButton(EditTool.CheckField, "", "Check box", "A fillable check box (a real form field): click on the page. Click it to tick or untick",
                   icon: new Border { BorderBrush = Brushes.White, BorderThickness = new Thickness(1.4), CornerRadius = new CornerRadius(2), Width = 17, Height = 17, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0),
                                      Child = new TextBlock { Text = "✓", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) } });
        ToolButton(EditTool.RadioField, "", "Option", "A fillable round button (a real form field): click for each choice. Buttons placed one after the other are one group, where only one can be chosen; choose the tool again to start another group",
                   icon: new Border { BorderBrush = Brushes.White, BorderThickness = new Thickness(1.4), CornerRadius = new CornerRadius(9), Width = 17, Height = 17, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0),
                                      Child = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(4), Width = 7, Height = 7, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        ToolButton(EditTool.Link, "", "Link", "Drag a box and say where it goes: a web address or a page of this PDF. (Or select words and right-click > Make the selected words a link.) While this or Select is chosen, the links are outlined in blue: click one to change or delete it");
        ToolButton(EditTool.DropField, "", "Dropdown", "A fillable drop-down list (a real form field): drag its size, then type the choices. People pick one from the list, here or in any PDF reader",
                   icon: new Border { BorderBrush = Brushes.White, BorderThickness = new Thickness(1.4), CornerRadius = new CornerRadius(2), Width = 26, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0),
                                      Child = new TextBlock { Text = "▾", FontSize = 11, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1, 4, 0) } });
        ToolButton(EditTool.SignField, "", "Sign box", "A box for a signature (a real form field): drag its size on the page. Click it later to sign here, or sign it with a certificate (Tools menu)",
                   icon: new Border { BorderBrush = Brushes.White, BorderThickness = new Thickness(1.4), CornerRadius = new CornerRadius(2), Width = 26, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0),
                                      Child = new TextBlock { Text = "✕", FontSize = 9, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 0, 0) } });
        ToolButton(EditTool.Table, "", "Table", "Drag the size of a table, then choose its rows and columns: a grid of lines (type in the cells with the Text tool)",
                   icon: new Border { BorderBrush = Brushes.White, BorderThickness = new Thickness(1.4), Width = 22, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0),
                                      Child = new Grid { Children = { new Border { BorderBrush = Brushes.White, BorderThickness = new Thickness(0, 0, 1, 0), Width = 10, HorizontalAlignment = HorizontalAlignment.Left }, new Border { BorderBrush = Brushes.White, BorderThickness = new Thickness(0, 1, 0, 0), Height = 7, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0) } } } });
        ToolButton(EditTool.Shapes, "▭", "Shapes", "Box, circle, line or arrow: click again to choose (Shift: straight / square)", "Segoe UI Symbol");
        ShapesMenu();
        StampMenus();
        ActionButton("Border", "A frame around the pages (colour, thickness, dashed, rounded ...)", AddBorder, "PdfActionBorder",
                     new Border { BorderBrush = Brushes.White, BorderThickness = new Thickness(1.6), CornerRadius = new CornerRadius(5), Width = 22, Height = 17, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) });
        ToolButton(EditTool.WhiteOut,"⬜", "White-out", "Drag to cover something with white (it hides it on the page; the words underneath are not erased from the file)", "Segoe UI Symbol");
        ToolButton(EditTool.Redact, "", "Redact", "Drag a box over what must disappear for good: when you save, the words, pictures and comments under it are really removed from the file (not just covered)",
                   icon: new Border { Width = 24, Height = 15, Background = Brushes.Black, BorderBrush = Brushes.White, BorderThickness = new Thickness(1.4), CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) });

        ActionButton("Watermark", "A big pale text or picture across the pages (CONFIDENTIAL, DRAFT, a logo). To take one out: Tools > Remove a watermark", () => AddPageMarks("mark"), "PdfActionWatermark",
                     new TextBlock { Text = "ABC", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 1), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(-24) });
        ActionButton("Page no.", "Page numbers, header and footer on the pages", () => AddPageMarks("line"), "PdfActionPageNumbers",
                     new TextBlock { Text = "#", FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -2, 0, -1) });

        ActionButton("To Word", "Make a Word document (.docx) from this PDF", () => ExportToOffice(excel: false), "PdfActionToWord", ConvertIcon("W", Color.FromRgb(0x2B, 0x57, 0x9A)));
        ActionButton("To Excel", "Make an Excel workbook (.xlsx) from this PDF", () => ExportToOffice(excel: true), "PdfActionToExcel", ConvertIcon("X", Color.FromRgb(0x1D, 0x6F, 0x42)));
        ActionButton("To Pictures", "Save the pages as PNG or JPG pictures", () => OpenPagesDialog(split: false), "PdfActionToPictures", ConvertIcon("", Color.FromRgb(0xC2, 0x6A, 0x1B), "Segoe MDL2 Assets"));

        // The tools are sorted into tabs, so the bar never gets longer than the window: Select is always there, then the tabs, then the tools of the chosen tab.
        // Add (put something on the page) / Mark up (comment and draw) / Forms (fields to fill in) / Page (hide things, frames, numbers) / Convert (into other files).
        tools.Children.Clear();
        UIElement Sep() => new Border { Width = 1, Height = 30, Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        void Tab(string name, string tip, params UIElement[][] groups)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            foreach (var group in groups)
            {
                if (panel.Children.Count > 0) panel.Children.Add(Sep());
                foreach (var item in group) panel.Children.Add(item);
            }
            _tabPanels[name] = panel;
            var chip = new RadioButton { Content = new TextBlock { Text = name, Foreground = Brushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold }, GroupName = "pdftab", Template = ToolChoiceTemplate(small: true), Focusable = false, Margin = new Thickness(1, 0, 1, 0), ToolTip = tip };
            System.Windows.Automation.AutomationProperties.SetAutomationId(chip, "PdfTab" + name.Replace(" ", ""));
            System.Windows.Automation.AutomationProperties.SetName(chip, name);
            chip.Checked += (_, _) => ShowTab(name);
            _tabChips[name] = chip;
        }
        UIElement[] T(params EditTool[] list) => list.Select(t => (UIElement)_toolButtons[t]).ToArray();
        UIElement[] A(params string[] list) => list.Select(id => (UIElement)actions[id]).ToArray();
        Tab("Add", "Put something on the page: text, the date, columns, pictures, your signature, stamps", T(EditTool.Text, EditTool.EditText, EditTool.Date).Concat(A("PdfActionColumns")).ToArray(), T(EditTool.Image, EditTool.Signature, EditTool.Stamp, EditTool.Link));
        Tab("Mark up", "Comment and draw: highlight, underline, notes, pen, shapes, tables, check marks", T(EditTool.Highlight, EditTool.Underline, EditTool.Strike, EditTool.Note), T(EditTool.Pen, EditTool.Shapes, EditTool.Table, EditTool.Check, EditTool.Cross));
        Tab("Forms", "Fields people can fill in: text box, check box, option buttons, signature box", T(EditTool.TextField, EditTool.CheckField, EditTool.RadioField, EditTool.DropField, EditTool.SignField));
        Tab("Page", "Hide or remove things, a frame, a watermark, page numbers", T(EditTool.WhiteOut, EditTool.Redact), A("PdfActionBorder", "PdfActionWatermark", "PdfActionPageNumbers"));
        Tab("Convert", "Make a Word or Excel file or pictures from this PDF", A("PdfActionToWord", "PdfActionToExcel", "PdfActionToPictures"));
        tools.Children.Add(_toolButtons[EditTool.Select]);
        tools.Children.Add(Sep());
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var chip in _tabChips.Values) chipRow.Children.Add(chip);
        tools.Children.Add(chipRow);
        tools.Children.Add(Sep());
        foreach (var panel in _tabPanels.Values) tools.Children.Add(panel);
        _tabChips[_editTab].IsChecked = true;

        // colours, size, font
        foreach (var c in Swatches)
        {
            var b = new Button
            {
                Width = 24, Height = 24, Margin = new Thickness(1, 0, 1, 0), Focusable = false, ToolTip = ColorName(c), Cursor = Cursors.Hand,
                // (the chosen colour gets a white ring with a gap; the ring is there even for the white dot)
                Template = (ControlTemplate)XamlReader.Parse(
                    "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
                    "<Grid><Border x:Name='ring' CornerRadius='12' BorderBrush='Transparent' BorderThickness='2' />" +
                    "<Border x:Name='bd' Margin='3' Background='{TemplateBinding Background}' CornerRadius='9' BorderBrush='#55FFFFFF' BorderThickness='1' /></Grid>" +
                    "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='BorderBrush' Value='White' /></Trigger>" +
                    "<Trigger Property='Tag' Value='picked'><Setter TargetName='ring' Property='BorderBrush' Value='White' /></Trigger></ControlTemplate.Triggers></ControlTemplate>"),
                Background = new SolidColorBrush(c),
            };
            b.Click += (_, _) => SetColor(c);
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, "PdfColor" + _swatchButtons.Count);
            System.Windows.Automation.AutomationProperties.SetName(b, ColorName(c));
            _swatchButtons.Add((b, c));
            _colorRow.Children.Add(b);
        }
        var more = new Button { Content = "…", Width = 24, Height = 22, Padding = new Thickness(0), Margin = new Thickness(3, 0, 0, 0), Focusable = false, ToolTip = "More colours", Background = Brushes.Transparent, Foreground = Brushes.White };
        more.Click += (_, _) =>
        {
            using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) SetColor(Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
        };
        _colorRow.Children.Add(more);
        _moreColor = more;

        var minus = SmallBar("", "Smaller", () => ChangeSize(-1));
        var plus = SmallBar("", "Bigger", () => ChangeSize(+1));
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        sizeRow.Children.Add(_sizeLabel); sizeRow.Children.Add(minus); sizeRow.Children.Add(_sizeText); sizeRow.Children.Add(plus);
        sizeRow.Children.Add(FillButton());
        System.Windows.Automation.AutomationProperties.SetAutomationId(_sizeText, "PdfEditSize");

        foreach (var (kind, label, family) in new[] { (PdfFontKind.Sans, "Sans", "Arial"), (PdfFontKind.Serif, "Serif", "Times New Roman"), (PdfFontKind.Mono, "Mono", "Courier New") })
        {
            var r = new RadioButton { Content = new TextBlock { Text = label, FontFamily = new FontFamily(family), Foreground = Brushes.White }, GroupName = "pdffont", Template = ToolChoiceTemplate(small: true), Focusable = false, ToolTip = label + " font" };
            r.Checked += (_, _) => SetFont(kind, null);
            _fontButtons[kind] = r;
            _fontRow.Children.Add(r);
        }
        _boldButton = new ToggleButton { Content = new TextBlock { Text = "B", FontWeight = FontWeights.Bold, Foreground = Brushes.White }, Template = ToggleTemplate(), Focusable = false, ToolTip = "Bold" };
        _boldButton.Click += (_, _) => SetFont(null, _boldButton.IsChecked == true);
        _fontRow.Children.Add(_boldButton);
        _fontRow.Children.Add(FontPickerButton());
        _fontRow.Children.Add(StyleButton());
        _fontButtons[PdfFontKind.Sans].IsChecked = true;

        // undo, delete, save
        _undoButton = SmallBar("", "Undo (Ctrl+Z)", Undo);
        _redoButton = SmallBar("", "Redo (Ctrl+Y)", Redo);
        _deleteButton = SmallBar("", "Delete what is selected (Delete)", DeleteSelected);
        _saveButton = new Button { Content = "Save", Style = (Style)Application.Current.FindResource("DialogPrimary"), Height = 30, MinWidth = 76, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(8, 0, 0, 0), Focusable = false, ToolTip = "Save the changes into this PDF (Ctrl+S)" };
        _saveButton.Click += async (_, _) => await SaveEditsAsync(saveAs: false);
        var saveAs = new Button { Content = BarLabel("Save as…"), Template = BarButtonTemplate(),  Height = 30, MinWidth = 80, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(6, 0, 0, 0), Focusable = false, Background = Brushes.Transparent, Foreground = Brushes.White, ToolTip = "Save the changes as a new PDF (this one stays as it is)" };
        saveAs.Click += async (_, _) => await SaveEditsAsync(saveAs: true);
        var done = new Button { Content = BarLabel("Done"), Template = BarButtonTemplate(),  Height = 30, MinWidth = 70, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(6, 0, 4, 0), Focusable = false, Background = Brushes.Transparent, Foreground = Brushes.White, ToolTip = "Stop editing" };
        done.Click += (_, _) => ExitEditing();
        foreach (var (b, id) in new[] { (_saveButton, "PdfEditSave"), (saveAs, "PdfEditSaveAs"), (done, "PdfEditDone"), (_undoButton, "PdfEditUndo"), (_redoButton, "PdfEditRedo"), (_deleteButton, "PdfEditDelete") })
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, id);

        // the two rows: the tools (grouped) on top, and below them the look of the tool or of what is selected (colour, size, font) on the left,
        // with undo / redo / delete and Save / Save as / Done on the right, so the tools have the whole width
        var actionsRight = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
        actionsRight.Children.Add(_undoButton); actionsRight.Children.Add(_redoButton); actionsRight.Children.Add(_deleteButton);
        actionsRight.Children.Add(new Border { Width = 1, Height = 22, Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
        actionsRight.Children.Add(_saveButton); actionsRight.Children.Add(saveAs); actionsRight.Children.Add(done);
        var row1 = new ScrollViewer { Content = tools, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };

        var look = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
        // ("Colour" sits inside the colour row, so it goes away with it)
        _colorRow.Children.Insert(0, new TextBlock { Text = "Colour", Foreground = Soft, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        _toolHint.Foreground = Soft; _toolHint.FontSize = 12; _toolHint.VerticalAlignment = VerticalAlignment.Center; _toolHint.TextTrimming = TextTrimming.CharacterEllipsis;
        _fontRow.Margin = new Thickness(14, 0, 0, 0);
        _toolHint.Margin = new Thickness(14, 0, 0, 0);
        sizeRow.Margin = new Thickness(14, 0, 0, 0);
        foreach (UIElement part in new UIElement[] { _colorRow, sizeRow, _fontRow }) { DockPanel.SetDock(part, Dock.Left); look.Children.Add(part); }
        look.Children.Add(_toolHint);                                   // (last: it takes what is left, and is cut with "..." when it is long)

        var row2 = new DockPanel { Height = 36, Margin = new Thickness(6, 2, 0, 0), LastChildFill = true };
        DockPanel.SetDock(actionsRight, Dock.Right);
        row2.Children.Add(actionsRight);
        row2.Children.Add(look);

        var rows = new StackPanel();
        rows.Children.Add(row1);
        rows.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), Margin = new Thickness(0, 4, 0, 0) });
        rows.Children.Add(row2);

        _editBar = new Border { Child = rows, Background = new SolidColorBrush(Color.FromRgb(0x23, 0x28, 0x34)), Padding = new Thickness(8, 4, 8, 2), Visibility = Visibility.Collapsed, BorderBrush = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x1A)), BorderThickness = new Thickness(0, 1, 0, 1) };
        return _editBar;
    }

    private Button SmallBar(string glyph, string tip, Action action)
    {
        var b = new Button { Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, Foreground = Brushes.White }, ToolTip = tip, Template = ToolTemplate(), Foreground = Brushes.White, Focusable = false };
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => action();
        return b;
    }

    private static ControlTemplate ToolChoiceTemplate(bool small = false) => (ControlTemplate)XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='RadioButton'>" +
        $"<Border x:Name='bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='Transparent' CornerRadius='6' MinWidth='{(small ? 40 : 42)}' Height='{(small ? 26 : 44)}' Padding='{(small ? "6,0" : "3,2")}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' /></Border>" +
        "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#22FFFFFF' /></Trigger>" +
        "<Trigger Property='IsChecked' Value='True'><Setter TargetName='bd' Property='Background' Value='#5B8DEF' /></Trigger></ControlTemplate.Triggers></ControlTemplate>");

    private static Border ColumnBar() => new() { Width = 5, Height = 17, Margin = new Thickness(1.5, 0, 1.5, 0), Background = Brushes.White, CornerRadius = new CornerRadius(1) };

    /// <summary>A tool-bar button that is not "chosen" (it opens a dialog): the same hover as a tool.</summary>
    private static ControlTemplate ToolActionTemplate() => (ControlTemplate)XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
        "<Border x:Name='bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='Transparent' CornerRadius='6' MinWidth='46' Height='44' Padding='3,2'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' /></Border>" +
        "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#22FFFFFF' /></Trigger>" +
        "<Trigger Property='IsPressed' Value='True'><Setter TargetName='bd' Property='Background' Value='#44FFFFFF' /></Trigger></ControlTemplate.Triggers></ControlTemplate>");

    private static ControlTemplate ToggleTemplate() => (ControlTemplate)XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ToggleButton'>" +
        "<Border x:Name='bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='Transparent' CornerRadius='6' Width='30' Height='28'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' /></Border>" +
        "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#22FFFFFF' /></Trigger>" +
        "<Trigger Property='IsChecked' Value='True'><Setter TargetName='bd' Property='Background' Value='#5B8DEF' /></Trigger></ControlTemplate.Triggers></ControlTemplate>");

    private static string ColorName(Color c) =>
        c == Colors.Black ? "Black" : c == Colors.White ? "White" : c == Swatches[1] ? "Dark blue (like a ballpen)" : c == Swatches[2] ? "Blue" : c == Swatches[3] ? "Red" : c == Swatches[4] ? "Green" : c == Swatches[5] ? "Yellow" : "Colour";

    // ---------- going in and out of editing ----------
    private void EnterEditing()
    {
        if (_pdf == null || _path == null || _editing) return;
        if (!_pdf.CanEdit && !AskOwnerPassword()) return;
        if (_rotation != 0) { _rotation = 3; Rotate(); }              // (back to upright: editing works on the pages as they are)
        _editing = true;
        OutlinesChanged();
        _editBar.Visibility = Visibility.Visible;
        _editButton.Background = new SolidColorBrush(Color.FromArgb(60, 91, 141, 239));
        ClearTextSelection();
        _tool = EditTool.Select;                                       // (Edit always starts on Select: no tool is armed, so a click or the placing guide never surprises; the tab you were last on stays)
        _toolButtons[_tool].IsChecked = true;
        SetTool(_tool);
        UpdateEditButtons();
        SyncDark();                                                    // (dark reading is off while editing)
        Toast("Editing: pick a tool, then click or drag on the page");
    }

    /// <summary>The PDF is protected against changes: its owner (permissions) password opens it for editing.</summary>
    private bool AskOwnerPassword()
    {
        bool wrong = false;
        while (true)
        {
            var ask = new PasswordWindow(System.IO.Path.GetFileName(_path!), wrong, "This PDF is protected against changes. Enter its owner (permissions) password to edit it") { Owner = this };
            if (ask.ShowDialog() != true) return false;
            try
            {
                var opened = PdfFile.Open(_path!, ask.Password);
                if (!opened.CanEdit) { opened.Dispose(); wrong = true; continue; }
                _pdf?.Dispose();
                _pdf = opened;
                RedrawPages();
                return true;
            }
            catch (PdfPasswordException) { wrong = true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Toast("Couldn't open it: " + e.Message); return false; }
        }
    }

    private void ExitEditing()
    {
        if (!_editing) return;
        if (!ConfirmLeaveEdits()) return;
        ResetEdits(keepEditing: false);
    }

    /// <summary>Forgets what was added (after loading or saving); keepEditing leaves the tools open.</summary>
    private void ResetEdits(bool keepEditing)
    {
        CloseTextBox(commit: false);
        _items.Clear(); _undo.Clear(); _redo.Clear(); _pageUndo.Clear(); _pageRedo.Clear();
        _group.Clear(); _marquee = null;
        _selected = null; _dirty = false; _drag = DragMode.None;
        _editing = keepEditing && _editing;
        OutlinesChanged();
        if (_editBar != null) _editBar.Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
        if (_editButton != null) _editButton.Background = _editing ? new SolidColorBrush(Color.FromArgb(60, 91, 141, 239)) : Brushes.Transparent;
        foreach (var p in _pages) { p.Overlay.Children.Clear(); p.Overlay.Cursor = _editing ? CursorFor(_tool) : null; }
        UpdateTitle();
        if (_undoButton != null) UpdateEditButtons();
        SyncDark();
    }

    /// <summary>Unsaved changes: asks to save them first. False = stay.</summary>
    private bool ConfirmLeaveEdits()
    {
        CloseTextBox(commit: true);
        if (!_dirty || _path == null) return true;
        var answer = UMessage.Ask(this, $"Save the changes to {System.IO.Path.GetFileName(_path)}?", "Utylix Editor", MessageBoxImage.Question, MessageBoxResult.Yes, MessageBoxResult.Cancel,
                                  ("Save", MessageBoxResult.Yes), ("Don't save", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel));
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No) { _dirty = false; return true; }
        return SaveEdits(_path);
    }

    private void UpdateTitle()
    {
        if (_path == null) return;
        Title = (_dirty ? "• " : "") + System.IO.Path.GetFileName(_path) + " - Utylix Editor";
    }

    private void OnZoomChangedForEditing()
    {
        if (_editing && _selected != null) RenderItems(_selected.Page);
    }

    // ---------- tools and their settings ----------
    private void SetTool(EditTool tool)
    {
        HideGhost();
        CloseTextBox(commit: true);
        _tool = tool;
        OutlinesChanged();
        if (TabOf(tool) is string tab && tab != _editTab && _tabChips.TryGetValue(tab, out var tabChip)) tabChip.IsChecked = true;      // (the tab with that tool is shown)
        if (tool == EditTool.RadioField) _radioGroup = null;                // (choosing the tool starts a new group of round buttons)
        if (tool is EditTool.Signature) { _toolButtons[EditTool.Select].IsChecked = true; AddSignature(); return; }
        if (tool is EditTool.Image) { _toolButtons[EditTool.Select].IsChecked = true; AddPicture(); return; }
        if (tool != EditTool.Select) Select(null);
        if (tool == EditTool.Stamp && !_stampMenuBusy) Dispatcher.BeginInvoke(new Action(() => ShowStampMenu(_toolButtons[EditTool.Stamp])));     // (a stamp has to be chosen first)
        foreach (var p in _pages) p.Overlay.Cursor = CursorFor(tool);
        UpdateProperties();
    }

    private static Cursor CursorFor(EditTool t) => t switch { EditTool.Select => Cursors.Arrow, EditTool.Text or EditTool.Underline or EditTool.Strike => Cursors.IBeam, EditTool.Note => Cursors.Hand, _ => Cursors.Cross };

    /// <summary>The Shapes tool: clicking it again (or right-clicking) offers box, circle, line, arrow.</summary>
    private void ShapesMenu()
    {
        var button = _toolButtons[EditTool.Shapes];
        var menu = new ContextMenu();
        foreach (var (kind, glyph, text) in new[] { (ShapeKind.Rectangle, "▭", "Box"), (ShapeKind.Ellipse, "◯", "Circle"), (ShapeKind.Line, "╱", "Line"), (ShapeKind.Arrow, "→", "Arrow") })
        {
            var item = new MenuItem { Header = text, Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe UI Symbol") } };
            item.Click += (_, _) =>
            {
                _shapeKind = kind;
                var content = (StackPanel)button.Content;
                ((TextBlock)content.Children[0]).Text = glyph;
                ((TextBlock)content.Children[1]).Text = text;
                button.IsChecked = true;
                SetTool(EditTool.Shapes);
            };
            menu.Items.Add(item);
        }
        button.ContextMenu = Themed(menu);
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (button.IsChecked != true) return;
            menu.PlacementTarget = button; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
            e.Handled = true;
        };
    }

    /// <summary>Shows the settings that fit the tool (or the selected item): colour always, size (text size or line width), font for text.</summary>
    private void UpdateProperties()
    {
        var item = _selected;
        var typingRun = _runTyping;                                // Edit text with a line open: the font, size and colour are that line's
        bool textField = item is FieldItem { Kind: PdfNewFieldKind.Text or PdfNewFieldKind.Dropdown } || (item == null && _tool is EditTool.TextField or EditTool.DropField);
        bool text = item is TextItem || (item == null && _tool is EditTool.Text or EditTool.Date) || typingRun != null || textField;
        bool line = item is ShapeItem { Kind: ShapeKind.Rectangle or ShapeKind.Ellipse or ShapeKind.Line or ShapeKind.Arrow } || item is InkItem { Signature: false } || item is TableItem
                    || (item == null && _tool is EditTool.Pen or EditTool.Shapes or EditTool.Table);
        _fontRow.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        if (_styleButton != null) _styleButton.Visibility = item is TextItem || _typing != null || (item == null && _tool is EditTool.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (_fillButton != null) _fillButton.Visibility = item is ShapeItem { Kind: ShapeKind.Rectangle or ShapeKind.Ellipse } || (item == null && _tool is EditTool.Shapes && _shapeKind is ShapeKind.Rectangle or ShapeKind.Ellipse) ? Visibility.Visible : Visibility.Collapsed;
        ((FrameworkElement)_sizeLabel.Parent).Visibility = text || line ? Visibility.Visible : Visibility.Collapsed;
        _sizeLabel.Text = text ? "Size" : "Line";
        double size = typingRun != null ? typingRun.EffSize : item switch { TextItem t => t.FontSize, FieldItem f => f.FontSize, ShapeItem s => s.Width, InkItem i => i.Width, TableItem tb => tb.Width, _ => text ? _textSize : _lineWidth };
        if (_fontPickerButton != null) _fontPickerButton.Visibility = textField ? Visibility.Collapsed : Visibility.Visible;
        _sizeText.Text = size.ToString(size < 10 ? "0.#" : "0", CultureInfo.InvariantCulture);
        _syncingFont = true;                                       // (showing the font must not change it)
        try
        {
            if (typingRun != null)
            {
                foreach (var chip in _fontButtons.Values) chip.IsChecked = false;
                _fontLabel.Text = typingRun.FontOverride ?? typingRun.Run.Family;       // (the PDF's own font, until another one is chosen)
                _boldButton.IsChecked = typingRun.EffBold;
            }
            else if (item is TextItem ti) { ShowFont(ti.Font, ti.FontName); _boldButton.IsChecked = ti.Bold; }
            else if (item is FieldItem fi && fi.Kind is PdfNewFieldKind.Text or PdfNewFieldKind.Dropdown) { ShowFont(fi.Font, null); _boldButton.IsChecked = fi.Bold; }
            else if (textField) { ShowFont(_font, null); _boldButton.IsChecked = _bold; }
            else if (text) { ShowFont(_font, _fontName); _boldButton.IsChecked = _bold; }
        }
        finally { _syncingFont = false; }
        bool run = item is RunEditItem || (item == null && _tool == EditTool.EditText);
        _toolHint.Text = typingRun != null ? "Shift+Enter: new line · Enter: done"
                       : run ? "Click a line of text to change it, or drag it to move it. Shift+Enter makes a new line; Enter or a click beside it finishes; Save puts it into the PDF." : "";
        _toolHint.Visibility = run ? Visibility.Visible : Visibility.Collapsed;
        bool redact = item is ShapeItem { Kind: ShapeKind.Redact } || (item == null && _tool == EditTool.Redact);
        bool fieldTool = item is FieldItem || (item == null && _tool is EditTool.TextField or EditTool.CheckField or EditTool.RadioField or EditTool.SignField or EditTool.DropField);
        // nothing to set: no colour dots, size or font for a tool or a choice that has none (the bar only shows what the tool uses)
        bool nothingToSet = (item == null && _tool is EditTool.Select or EditTool.EditText or EditTool.Image or EditTool.Signature or EditTool.Link) && typingRun == null;
        _colorRow.Visibility = item is ImageItem or PageObjectItem or LinkPick || (run && typingRun == null) || redact || nothingToSet ? Visibility.Collapsed : Visibility.Visible;
        if (nothingToSet && !run)
        {
            _toolHint.Text = _tool == EditTool.Link
                ? "Drag a box where the link should be, then say where it goes. Or select words and right-click > Make the selected words a link."
                : "Click something to change it, or drag a box on empty paper to choose several things. Right-click for more (copy, arrange, field options ...).";
            _toolHint.Visibility = Visibility.Visible;
        }
        if (item is LinkPick lp) { _toolHint.Text = (lp.Link.Uri != null ? "A link to " + lp.Link.Uri : "A link to page " + (lp.Link.Page + 1)) + ". Enter changes where it goes, Delete removes it."; _toolHint.Visibility = Visibility.Visible; }
        if (item is PageObjectItem) { _toolHint.Text = "Already in the PDF: drag it to move it, the corner to resize it, Delete to remove it. Undo takes it back."; _toolHint.Visibility = Visibility.Visible; }
        if (fieldTool)
        {
            _toolHint.Text = "Text box and sign box: drag the size (or click). Check box and option: click. The colour is the edge of the field. They become real fillable fields when you Save.";
            _toolHint.Visibility = Visibility.Visible;
        }
        if (redact) { _toolHint.Text = "Drag over what must go. Saving removes it from the file for good (Save replaces the file: use Save as… to keep the original)."; _toolHint.Visibility = Visibility.Visible; }
        if (run && typingRun == null) ((FrameworkElement)_sizeLabel.Parent).Visibility = Visibility.Collapsed;
        if (item is ColumnsItem) { _toolHint.Text = "Double-click (or Enter) to change the words or the number of columns. Drag a side handle to make it wider or narrower; the height follows the words."; _toolHint.Visibility = Visibility.Visible; }
        if (item is StampItem) { _toolHint.Text = "Drag the round handle above the stamp to turn it (Shift: steps of 15°), the square corner to resize, the stamp itself to move."; _toolHint.Visibility = Visibility.Visible; }
        MarkColor();
    }

    private void SetColor(Color c)
    {
        if (_runTyping != null) { _runTyping.ColorOverride = c; StyleRunBox(); RenderItems(_runTyping.Page); MarkColor(); return; }
        if (_selected != null && _selected is not (ImageItem or PageObjectItem or LinkPick))
        {
            Snapshot();
            _selected.Color = c;
            RenderItems(_selected.Page);
            if (_tool != EditTool.Select) _toolColors[_tool] = c;             // (the next one made with this tool has the colour just chosen, not an older one)
        }
        else _toolColors[_tool] = c;
        if (_tool is EditTool.TextField or EditTool.CheckField or EditTool.RadioField or EditTool.SignField or EditTool.DropField || _selected is FieldItem)
            _toolColors[EditTool.TextField] = _toolColors[EditTool.CheckField] = _toolColors[EditTool.RadioField] = _toolColors[EditTool.SignField] = _toolColors[EditTool.DropField] = c;     // (all the fields share their colour)
        MarkColor();
    }

    private static readonly double[] TextSizes = { 6, 7, 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 60, 72 };
    private static readonly double[] LineWidths = { 0.5, 1, 1.5, 2, 3, 4, 6, 8, 12 };

    private void ChangeSize(int step)
    {
        static double Next(double[] steps, double now, int dir) =>
            dir > 0 ? steps.FirstOrDefault(s => s > now + 0.01, steps[^1]) : steps.LastOrDefault(s => s < now - 0.01, steps[0]);
        if (_runTyping != null) { _runTyping.SizeOverride = Next(TextSizes, _runTyping.EffSize, step); StyleRunBox(); RenderItems(_runTyping.Page); return; }
        switch (_selected)
        {
            case TextItem t: Snapshot(); t.FontSize = Next(TextSizes, t.FontSize, step); _textSize = t.FontSize; RenderItems(t.Page); break;
            case FieldItem { Kind: PdfNewFieldKind.Text or PdfNewFieldKind.Dropdown } fld: Snapshot(); fld.FontSize = Next(TextSizes, fld.FontSize, step); _textSize = fld.FontSize; RenderItems(fld.Page); break;
            case ShapeItem s: Snapshot(); s.Width = Next(LineWidths, s.Width, step); _lineWidth = s.Width; RenderItems(s.Page); break;
            case InkItem i: Snapshot(); i.Width = Next(LineWidths, i.Width, step); _lineWidth = i.Width; RenderItems(i.Page); break;
            case TableItem tb: Snapshot(); tb.Width = Next(LineWidths, tb.Width, step); _lineWidth = tb.Width; RenderItems(tb.Page); break;
            default:
                if (_tool is EditTool.Text or EditTool.Date or EditTool.TextField or EditTool.DropField) _textSize = Next(TextSizes, _textSize, step); else _lineWidth = Next(LineWidths, _lineWidth, step);
                break;
        }
        if (_typing != null && _textBox != null) { _typing.FontSize = _textSize; _textBox.FontSize = _textSize; }
        UpdateProperties();
    }

    private void SetFont(PdfFontKind? kind, bool? bold, string? name = null)
    {
        if (_syncingFont) return;
        if (_runTyping != null)                                    // Edit text: the font of the line being changed (not the default for new text)
        {
            if (kind is PdfFontKind rk) _runTyping.FontOverride = rk switch { PdfFontKind.Serif => "Times New Roman", PdfFontKind.Mono => "Courier New", _ => "Arial" };
            if (name != null) _runTyping.FontOverride = name;
            if (bold is bool rb) _runTyping.BoldOverride = rb;
            StyleRunBox(); RenderItems(_runTyping.Page);
            return;
        }
        if (kind is PdfFontKind k) { _font = k; _fontName = null; if (_fontLabel != null) _fontLabel.Text = "More fonts"; }
        if (name != null) _fontName = name;
        if (bold is bool b) _bold = b;
        if (_selected is FieldItem { Kind: PdfNewFieldKind.Text or PdfNewFieldKind.Dropdown } field)             // a text box or list of a form: Sans / Serif / Mono and bold (the standard PDF fonts)
        {
            Snapshot();
            if (kind is PdfFontKind fk) field.Font = fk;
            if (bold is bool fb) field.Bold = fb;
            RenderItems(field.Page);
            return;
        }
        var target = _typing ?? _selected as TextItem;
        if (target != null)
        {
            if (_typing == null) Snapshot();
            if (kind is PdfFontKind k2) { target.Font = k2; target.FontName = null; }
            if (name != null) target.FontName = name;
            if (bold is bool b2) target.Bold = b2;
            if (_textBox != null && _typing != null) StyleTextBox(_textBox, _typing);
            else RenderItems(target.Page);
        }
    }

    // ---------- the items on the pages ----------
    private void Snapshot()
    {
        _undo.Push(_items.Select(i => i.Clone()).ToList());
        _redo.Clear(); _pageRedo.Clear();
        _dirty = true;
        UpdateTitle();
        UpdateEditButtons();
    }

    private void Undo()
    {
        CloseTextBox(commit: true);
        if (_undo.Count == 0) { PageHistory(redo: false); return; }
        _redo.Push(_items.Select(i => i.Clone()).ToList());
        Restore(_undo.Pop());
    }

    private void Redo()
    {
        CloseTextBox(commit: true);
        if (_redo.Count == 0) { PageHistory(redo: true); return; }
        _undo.Push(_items.Select(i => i.Clone()).ToList());
        Restore(_redo.Pop());
    }

    private void Restore(List<EditItem> state)
    {
        var pages = _items.Select(i => i.Page).Concat(state.Select(i => i.Page)).Distinct().ToList();
        _group.Clear();
        _items.Clear(); _items.AddRange(state);
        _selected = null;
        _dirty = true;
        foreach (int p in pages) RenderItems(p);
        UpdateTitle(); UpdateEditButtons(); UpdateProperties();
    }

    private void UpdateEditButtons()
    {
        _undoButton.IsEnabled = _undo.Count > 0 || _pageUndo.Count > 0;
        _redoButton.IsEnabled = _redo.Count > 0 || _pageRedo.Count > 0;
        _deleteButton.IsEnabled = _selected != null || _group.Count > 0;
        _saveButton.IsEnabled = _dirty;
    }

    private void Add(EditItem item, bool select = false)
    {
        Snapshot();
        _items.Add(item);
        if (select) Select(item); else RenderItems(item.Page);
    }

    /// <summary>Lets go of the group chosen with a box (and redraws the pages that showed it).</summary>
    private void ClearGroup()
    {
        if (_group.Count == 0) return;
        var pages = _group.Select(i => i.Page).Distinct().ToList();
        _group.Clear();
        foreach (int p in pages) RenderItems(p);
        UpdateEditButtons();
    }

    private void DeleteSelected()
    {
        if (_selected is PageObjectItem picked) { DeletePageObjects(picked); return; }
        if (_selected is LinkPick pickedLink) { DeleteLink(pickedLink); return; }
        if (_group.Count > 0)
        {
            Snapshot();
            var pages = _group.Select(i => i.Page).Distinct().ToList();
            foreach (var g in _group) _items.Remove(g);
            _group.Clear();
            foreach (int p in pages) RenderItems(p);
            UpdateEditButtons(); UpdateProperties();
            return;
        }
        if (_selected == null) return;
        Snapshot();
        int page = _selected.Page;
        _items.Remove(_selected);
        _selected = null;
        RenderItems(page);
        UpdateEditButtons(); UpdateProperties();
    }

    private void Select(EditItem? item)
    {
        ClearGroup();
        var old = _selected;
        _selected = item;
        if (old != null) RenderItems(old.Page);
        if (item != null && item.Page != old?.Page) RenderItems(item.Page);
        UpdateEditButtons();
        UpdateProperties();
    }

    private EditItem? ItemAt(int page, Point p) => _items.LastOrDefault(i => i.Page == page && i.Hit(p));

    /// <summary>Draws the items of one page on its editing layer (and the selection frame with its corner handle).</summary>
    private void RenderItems(int page)
    {
        if (page < 0 || page >= _pages.Count) return;
        var overlay = _pages[page].Overlay;
        overlay.Children.Clear();
        foreach (var item in _items.Where(i => i.Page == page))
        {
            if (item == _typing || item == _runTyping) continue;
            var e = item.Build();
            e.IsHitTestVisible = false;
            overlay.Children.Add(e);
        }
        if (_drawing != null && _drawing.Page == page) { var e = _drawing.Build(); e.IsHitTestVisible = false; overlay.Children.Add(e); }
        if (_textBox != null && _typing?.Page == page) overlay.Children.Add(_textBox);
        RenderRunExtras(page, overlay);
        DrawLinkOutlines(page, overlay);
        DrawGuides(page, overlay);
        if (_group.Count > 0)
        {
            double gk = 1 / Math.Max(0.01, _pages[page].OverlayScale.ScaleX);
            var gblue = new SolidColorBrush(Color.FromRgb(0x2F, 0x6B, 0xEA));
            foreach (var g in _group.Where(i => i.Page == page))
            {
                var gb = g.Bounds;
                var gf = new Rectangle { Width = gb.Width + 6 * gk, Height = gb.Height + 6 * gk, Stroke = gblue, StrokeThickness = 1.2 * gk, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false };
                if (g.Angle != 0) gf.RenderTransform = new RotateTransform(g.Angle, gf.Width / 2, gf.Height / 2);
                Canvas.SetLeft(gf, gb.X - 3 * gk); Canvas.SetTop(gf, gb.Y - 3 * gk);
                overlay.Children.Add(gf);
            }
        }
        if (_marquee is Rect mq && _marqueePage == page)
        {
            double mk = 1 / Math.Max(0.01, _pages[page].OverlayScale.ScaleX);
            var box = new Rectangle { Width = mq.Width, Height = mq.Height, Stroke = new SolidColorBrush(Color.FromRgb(0x2F, 0x6B, 0xEA)), StrokeThickness = 1.2 * mk, StrokeDashArray = new DoubleCollection { 4, 3 }, Fill = new SolidColorBrush(Color.FromArgb(40, 0x2F, 0x6B, 0xEA)), IsHitTestVisible = false };
            Canvas.SetLeft(box, mq.X); Canvas.SetTop(box, mq.Y);
            overlay.Children.Add(box);
        }
        if (_selected != null && _selected.Page == page && _selected != _typing)
        {
            double k = 1 / Math.Max(0.01, _pages[page].OverlayScale.ScaleX);      // (screen pixels -> points)
            var r = _selected.Bounds;
            var blue = new SolidColorBrush(Color.FromRgb(0x2F, 0x6B, 0xEA));
            // (the frame and its handles turn with the item)
            var group = new Canvas { IsHitTestVisible = false };
            if (_selected.Angle != 0) group.RenderTransform = new RotateTransform(_selected.Angle, r.X + r.Width / 2, r.Y + r.Height / 2);
            var frame = new Rectangle { Width = r.Width + 6 * k, Height = r.Height + 6 * k, Stroke = blue, StrokeThickness = 1.2 * k, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false };
            Canvas.SetLeft(frame, r.X - 3 * k); Canvas.SetTop(frame, r.Y - 3 * k);
            group.Children.Add(frame);
            var handle = new Rectangle { Width = 9 * k, Height = 9 * k, Fill = Brushes.White, Stroke = blue, StrokeThickness = 1.5 * k, IsHitTestVisible = false };
            Canvas.SetLeft(handle, r.Right + 3 * k - 4.5 * k); Canvas.SetTop(handle, r.Bottom + 3 * k - 4.5 * k);
            group.Children.Add(handle);
            if (_selected.CanRotate)
            {
                double cx = r.X + r.Width / 2;
                var stem = new Line { X1 = cx, Y1 = r.Y - 3 * k, X2 = cx, Y2 = r.Y - (3 + RotateStem) * k, Stroke = blue, StrokeThickness = 1.2 * k, IsHitTestVisible = false };
                var knob = new Ellipse { Width = 11 * k, Height = 11 * k, Fill = Brushes.White, Stroke = blue, StrokeThickness = 1.5 * k, IsHitTestVisible = false };
                Canvas.SetLeft(knob, cx - 5.5 * k); Canvas.SetTop(knob, r.Y - (3 + RotateStem) * k - 5.5 * k);
                group.Children.Add(stem); group.Children.Add(knob);
            }
            overlay.Children.Add(group);
        }
    }

    private const double RotateStem = 22;            // (screen pixels from the frame to the round turning handle)

    private Point CentreOf(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    private bool OnHandle(PageView pv, Point p)
    {
        if (_selected == null || _selected.Page != pv.Index) return false;
        double k = 1 / Math.Max(0.01, pv.OverlayScale.ScaleX);
        var r = _selected.Bounds;
        if (_selected.Angle != 0) p = Rot(p, CentreOf(r), -_selected.Angle);
        return (p - new Point(r.Right + 3 * k, r.Bottom + 3 * k)).Length <= 9 * k;
    }

    /// <summary>The round handle above a stamp that turns it.</summary>
    private bool OnRotateHandle(PageView pv, Point p)
    {
        if (_selected == null || !_selected.CanRotate || _selected.Page != pv.Index) return false;
        double k = 1 / Math.Max(0.01, pv.OverlayScale.ScaleX);
        var r = _selected.Bounds;
        if (_selected.Angle != 0) p = Rot(p, CentreOf(r), -_selected.Angle);
        return (p - new Point(r.X + r.Width / 2, r.Y - (3 + RotateStem) * k)).Length <= 9 * k;
    }

    /// <summary>The overlays start listening (called for every page when the pages are made).</summary>
    private void HookOverlay(PageView pv)
    {
        // not editing (or marking text while editing): selecting text, links, notes; editing: the tools
        pv.Overlay.MouseLeftButtonDown += (_, e) => { if (!_editing) TextDown(pv, e, null); else OverlayDown(pv, e); };
        pv.Overlay.MouseMove += (_, e) => { if (!_editing || _textDrag) TextMove(pv, e); else OverlayMove(pv, e); };
        pv.Overlay.MouseLeftButtonUp += (_, e) => { if (!_editing || _textDrag) TextUp(pv, e); else OverlayUp(pv, e); };
        pv.Overlay.MouseRightButtonUp += (_, e) => PageMenu(pv, e);
        pv.Overlay.MouseLeave += (_, _) => { _hoverTip = null; ShowHoverTip(pv, null); if (_ghostPage == pv.Index) { HideGhost(); } };
        pv.Overlay.PreviewMouseLeftButtonDown += (_, _) => { _hoverTip = null; ShowHoverTip(pv, null); };
        if (_editing) pv.Overlay.Cursor = CursorFor(_tool);
    }

    // ---------- the mouse on a page ----------
    private void OverlayDown(PageView pv, MouseButtonEventArgs e)
    {
        if (!_editing || e.OriginalSource is DependencyObject d && IsInside(d, _textBox)) return;
        var p = e.GetPosition(pv.Overlay);
        if (_ghost != null && _ghostPage == pv.Index && PlacesOnClick(_tool) && _ghostSnap != default && (Keyboard.Modifiers & ModifierKeys.Alt) == 0) p += _ghostSnap;      // (the click lands where the guide showed)
        HideGhost();
        bool wasTyping = _typing != null || _runTyping != null;
        CloseTextBox(commit: true);
        e.Handled = true;
        _dragPage = pv; _dragStart = p; _dragSnapshotTaken = false;
        ResetSnap();
        // the handles of the selected item (it can be turned and resized while the tool that placed it is still on)
        if (_tool is EditTool.Select or EditTool.Stamp or EditTool.Text or EditTool.Date or EditTool.Signature or EditTool.Image or EditTool.TextField or EditTool.CheckField && _selected != null)
        {
            if (OnRotateHandle(pv, p))
            {
                var c = CentreOf(_selected.Bounds);
                _drag = DragMode.Rotate; _dragBox = _selected.Bounds; _rotateStart = _selected.Angle; _rotateFrom = Math.Atan2(p.Y - c.Y, p.X - c.X) * 180 / Math.PI;
                pv.Overlay.CaptureMouse();
                return;
            }
            if (_tool != EditTool.Select && OnHandle(pv, p)) { _drag = DragMode.Resize; _dragBox = _selected.Bounds; pv.Overlay.CaptureMouse(); return; }
        }
        switch (_tool)
        {
            case EditTool.EditText:
            {
                // a line: pressed and let go = change it; pressed and dragged = move it
                var existingRun = _items.OfType<RunEditItem>().LastOrDefault(i => i.Page == pv.Index && i.Hit(p));
                var runUnder = existingRun == null ? RunAt(pv.Index, p) : null;
                if (runUnder != null && (Keyboard.Modifiers & ModifierKeys.Alt) != 0) { _dragPage = null; EditParagraphAt(pv, p); return; }       // (Alt + click: the whole paragraph)
                if (existingRun == null && runUnder == null)
                {
                    _dragPage = null;
                    EditTextAt(pv, p, quiet: wasTyping);                              // (a click beside the text just finishes it; on a page without text it explains)
                    return;
                }
                _runDragItem = existingRun; _runDragRun = runUnder; _runDragStartOffset = existingRun?.Offset ?? default; _runDragMoved = false;
                _drag = DragMode.RunMove;
                break;
            }
            case EditTool.Select:
                if ((Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0 && ExtendChoice(pv, p)) { _dragPage = null; return; }       // (Shift / Ctrl + click: add to what is chosen)
                if (_group.Count > 0 && (_group.Any(g => g.Page == pv.Index && g.Hit(p)) || (ItemAt(pv.Index, p) == null && InGroupFrame(pv.Index, p)))) { _drag = DragMode.GroupMove; _groupLast = p; break; }      // (several chosen: they all follow the mouse, also when the pointer is between them)
                if (OnHandle(pv, p)) { _drag = DragMode.Resize; _dragBox = _selected!.Bounds; break; }
                if (_selected is PageObjectItem chosenSaved && ItemAt(pv.Index, p) == null && SavedChoiceUnder(pv.Index, p, chosenSaved)) { _drag = DragMode.Move; _dragBox = chosenSaved.Bounds; break; }      // (things already in the PDF chosen: a drag on any of them, or between them, moves them all)
                var item = ItemAt(pv.Index, p);
                if (item != null && item.GroupId != 0 && e.ClickCount < 2) { ChooseItem(item); _drag = DragMode.GroupMove; _groupLast = p; break; }      // (a grouped thing: the whole group is chosen and follows the mouse)
                Select(item);
                if (item == null)
                {
                    // not one of this session's things: a saved text box / check box of ours, or anything drawn on the page, can be picked up too
                    if (OwnFieldAt(pv.Index, p) is { } own) { _dragPage = null; LiftOwnField(pv.Index, own); return; }
                    if (AnnotLinkAt(pv.Index, p) is { } link)                        // (a link of the PDF: chosen; a second click on it changes where it goes)
                    {
                        bool again = e.ClickCount >= 2 && _selected is LinkPick prev && prev.Link.Box == link.Box;
                        _dragPage = null;
                        var pick = new LinkPick { Page = pv.Index, Link = link };
                        if (again) { EditLink(pick); return; }
                        Select(pick);
                        return;
                    }
                    if (PickPageObject(pv.Index, p) is { } picked) { Select(picked); _drag = DragMode.Move; _dragBox = picked.Bounds; break; }
                    _drag = DragMode.Marquee; _marquee = new Rect(p, p); _marqueePage = pv.Index; break;      // (an empty spot: drag a box round what you want)
                }
                if (item is TextItem t && e.ClickCount == 2) { EditText(t, isNew: false); return; }
                if (item is NoteItem n && e.ClickCount == 2) { OpenNote(n, isNew: false); return; }
                if (item is ColumnsItem columns && e.ClickCount == 2) { EditColumns(columns); return; }
                if (item is TableItem tableHere && e.ClickCount == 2) { EditTable(tableHere); return; }
                if (item is FieldItem fieldHere && e.ClickCount == 2) { EditFieldOptions(fieldHere); return; }
                if (item != null) { _drag = DragMode.Move; _dragBox = item.Bounds; }
                break;
            case EditTool.Text:
                if (ItemAt(pv.Index, p) is TextItem existing) { EditText(existing, isNew: false); return; }
                if (wasTyping) return;                                                 // (a click outside just finishes the text)
                var text = new TextItem { Page = pv.Index, TopLeft = new Point(p.X - 1, p.Y - _textSize * 0.6), Font = _font, FontName = _fontName, Bold = _bold, Italic = _italic, Underline = _underline, Align = _align, FontSize = _textSize, Color = _toolColors[EditTool.Text] };
                EditText(text, isNew: true);
                return;
            case EditTool.Check or EditTool.Cross:
            {
                double s = 14;
                Add(new ShapeItem { Page = pv.Index, Kind = _tool == EditTool.Check ? ShapeKind.Check : ShapeKind.Cross, A = new Point(p.X - s / 2, p.Y - s / 2), B = new Point(p.X + s / 2, p.Y + s / 2), Color = _toolColors[_tool] });
                return;
            }
            case EditTool.Link:
                if (AnnotLinkAt(pv.Index, p) is { } existingLink) { _dragPage = null; Select(new LinkPick { Page = pv.Index, Link = existingLink }); return; }
                _drawing = new LinkDraft { Page = pv.Index, Box = new Rect(p, p) };
                _drag = DragMode.Draw;
                break;
            case EditTool.Table:
                if (ItemAt(pv.Index, p) is TableItem existingTable) { Select(existingTable); _drag = DragMode.Move; _dragBox = existingTable.Bounds; break; }
                _drawing = new TableItem { Page = pv.Index, Box = new Rect(p, p), Rows = _tableRows, Cols = _tableCols, Color = _toolColors[EditTool.Table], Width = _lineWidth };
                _drag = DragMode.Draw;
                break;
            case EditTool.TextField or EditTool.CheckField or EditTool.RadioField or EditTool.SignField or EditTool.DropField:
                if (ItemAt(pv.Index, p) is FieldItem placed) { Select(placed); _drag = DragMode.Move; _dragBox = placed.Bounds; break; }       // (a click on a field already there moves it)
                if (OwnFieldAt(pv.Index, p) is { } saved) { _dragPage = null; LiftOwnField(pv.Index, saved); return; }                        // (a saved one: picked up again)
                StartField(pv, p);
                break;
            case EditTool.Stamp:
                _dragPage = null;
                PlaceStamp(pv, p);
                return;
            case EditTool.Date:
                _dragPage = null;
                PlaceDate(pv, p);
                return;
            case EditTool.Pen:
                _drawing = new InkItem { Page = pv.Index, Color = _toolColors[EditTool.Pen], Width = _lineWidth, Strokes = { new List<Point> { p } } };
                _drag = DragMode.Ink;
                break;
            case EditTool.Underline or EditTool.Strike:
                _dragPage = null;
                TextDown(pv, e, _tool == EditTool.Underline ? Pdfium.AnnotUnderline : Pdfium.AnnotStrikeOut);
                return;
            case EditTool.Highlight when TextUnder(pv, p):
                _dragPage = null;
                TextDown(pv, e, Pdfium.AnnotHighlight);                              // (starting on text: highlight the words; elsewhere: a box)
                return;
            case EditTool.Note:
                if (ItemAt(pv.Index, p) is NoteItem existingNote) { OpenNote(existingNote, isNew: false); return; }
                var note = new NoteItem { Page = pv.Index, At = new Point(p.X - 9, p.Y - 9), Color = _toolColors[EditTool.Note] };
                OpenNote(note, isNew: true);
                return;
            default:
                var kind = _tool switch { EditTool.Highlight => ShapeKind.Highlight, EditTool.WhiteOut => ShapeKind.WhiteOut, EditTool.Redact => ShapeKind.Redact, _ => _shapeKind };
                _drawing = new ShapeItem { Page = pv.Index, Kind = kind, A = p, B = p, Color = _toolColors[_tool], Width = _lineWidth, Fill = kind is ShapeKind.Rectangle or ShapeKind.Ellipse ? _shapeFill : null };
                _drag = DragMode.Draw;
                break;
        }
        pv.Overlay.CaptureMouse();
    }

    private void OverlayMove(PageView pv, MouseEventArgs e)
    {
        if (_drag == DragMode.None && _editing) UpdateGhost(pv, e.GetPosition(pv.Overlay));               // (the placing guide)
        if (_drag == DragMode.None && _editing && _tool == EditTool.EditText) { HoverRun(pv, e.GetPosition(pv.Overlay)); return; }
        if (_drag == DragMode.None || _dragPage != pv) return;
        var p = e.GetPosition(pv.Overlay);
        p = new Point(Math.Clamp(p.X, 0, pv.Overlay.Width), Math.Clamp(p.Y, 0, pv.Overlay.Height));
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (_drag)
        {
            case DragMode.Move when _selected != null:
            {
                var target = _dragBox.TopLeft + (p - _dragStart);
                target += SnapOffset(pv.Index, new Rect(target, _dragBox.Size), new[] { _selected }, _selected is PageObjectItem movingSaved ? movingSaved.Indices : null);       // (lines up with the other things on the page)
                var delta = target - _selected.Bounds.TopLeft;
                if (delta.Length < 0.01) { if (_guides.Count > 0) RenderItems(pv.Index); return; }
                if (!_dragSnapshotTaken) { if ((p - _dragStart).Length < 1.5) return; if (_selected is not PageObjectItem) Snapshot(); _dragSnapshotTaken = true; }
                _selected.MoveBy(delta);
                RenderItems(pv.Index);
                break;
            }
            case DragMode.Resize when _selected != null:
            {
                if (!_dragSnapshotTaken) { if (_selected is not PageObjectItem) Snapshot(); _dragSnapshotTaken = true; }
                // (a turned item keeps its top-left corner where it is on the page, and the mouse is measured along the item's own directions)
                double angle = _selected.Angle;
                var c0 = CentreOf(_dragBox);
                var corner = angle == 0 ? _dragBox.TopLeft : Rot(_dragBox.TopLeft, c0, angle);
                var along = angle == 0 ? p - corner : Rot(p, corner, -angle) - corner;
                double w = Math.Max(4, along.X), h = Math.Max(4, along.Y);
                if (_selected.KeepAspect && _dragBox.Width > 0) h = w * _dragBox.Height / _dragBox.Width;
                if (angle == 0)
                {
                    var snapped = SnapSize(pv.Index, new Rect(corner.X, corner.Y, w, h), _selected.KeepAspect && _dragBox.Width > 0, new[] { _selected }, _selected is PageObjectItem sizingSaved ? sizingSaved.Indices : null);
                    w = snapped.Width; h = snapped.Height;
                }
                var half = angle == 0 ? new Vector(w / 2, h / 2) : Rot(new Point(w / 2, h / 2), new Point(0, 0), angle) - new Point(0, 0);
                var centre = corner + half;
                _selected.ResizeTo(new Rect(centre.X - w / 2, centre.Y - h / 2, w, h));
                RenderItems(pv.Index);
                break;
            }
            case DragMode.RunMove:
            {
                if (!_runDragMoved && (p - _dragStart).Length < 4) return;              // (a click that wobbles a little is still a click)
                if (!_runDragMoved)
                {
                    Snapshot();
                    if (_runDragItem == null && _runDragRun is { } run)
                    {
                        _runDragItem = new RunEditItem { Page = pv.Index, Run = run, NewText = run.Text, Cover = CoverColour(pv, run.Box) };
                        _items.Add(_runDragItem);
                    }
                    _runDragMoved = true;
                    pv.Overlay.Cursor = Cursors.SizeAll;
                }
                if (_runDragItem == null) return;
                _runDragItem.Offset = _runDragStartOffset + (p - _dragStart);
                RenderItems(pv.Index);
                break;
            }
            case DragMode.Draw when _drawing is LinkDraft draft:
                draft.Box = new Rect(_dragStart, p);
                RenderItems(pv.Index);
                break;
            case DragMode.Draw when _drawing is TableItem table:
                table.Box = new Rect(_dragStart, p);
                RenderItems(pv.Index);
                break;
            case DragMode.Draw when _drawing is FieldItem field:
            {
                var a = _dragStart;
                field.Box = new Rect(a, field.Square ? new Point(a.X + Math.Max(Math.Abs(p.X - a.X), Math.Abs(p.Y - a.Y)) * (p.X >= a.X ? 1 : -1), a.Y + Math.Max(Math.Abs(p.X - a.X), Math.Abs(p.Y - a.Y)) * (p.Y >= a.Y ? 1 : -1)) : p);
                RenderItems(pv.Index);
                break;
            }
            case DragMode.Marquee when _marquee != null:
                _marquee = new Rect(_dragStart, p);
                RenderItems(pv.Index);
                break;
            case DragMode.GroupMove:
            {
                if (!_dragSnapshotTaken) { if ((p - _dragStart).Length < 1.5) return; Snapshot(); _dragSnapshotTaken = true; }
                if (_groupStartBox == null)
                {
                    var all = Rect.Empty;
                    foreach (var g in _group.Where(i => i.Page == pv.Index && !i.Bounds.IsEmpty)) all.Union(g.Bounds);
                    _groupStartBox = all;
                }
                var want = p - _dragStart;
                if (!_groupStartBox.Value.IsEmpty) want += SnapOffset(pv.Index, new Rect(_groupStartBox.Value.TopLeft + want, _groupStartBox.Value.Size), _group, null);
                var step = want - _groupApplied;
                if (step.Length < 0.01) { if (_guides.Count > 0) RenderItems(pv.Index); return; }
                _groupApplied = want;
                foreach (var g in _group) g.MoveBy(step);
                RenderItems(pv.Index);
                break;
            }
            case DragMode.Rotate when _selected != null:
            {
                var raw = e.GetPosition(pv.Overlay);                                   // (not held inside the page: turning goes on beyond its edge)
                var c = CentreOf(_dragBox);
                double deg = _rotateStart + (Math.Atan2(raw.Y - c.Y, raw.X - c.X) * 180 / Math.PI - _rotateFrom);
                deg = ((deg % 360) + 540) % 360 - 180;                                 // -180 .. 180
                double step = shift ? 15 : 45, snapped = Math.Round(deg / step) * step;
                if (shift || Math.Abs(deg - snapped) < 2.5) deg = snapped;             // (it clicks into place near 0, 45, 90...)
                if (Math.Abs(deg - _selected.Angle) < 0.01) return;
                if (!_dragSnapshotTaken) { Snapshot(); _dragSnapshotTaken = true; }
                _selected.SetAngle(deg);
                if (_selected is StampItem) _stampAngle = deg;
                RenderItems(pv.Index);
                break;
            }
            case DragMode.Ink when _drawing is InkItem ink:
            {
                var stroke = ink.Strokes[0];
                if ((p - stroke[^1]).Length < 0.8) return;
                stroke.Add(p);
                RenderItems(pv.Index);
                break;
            }
            case DragMode.Draw when _drawing is ShapeItem shape:
            {
                if (shift && shape.Kind is ShapeKind.Line or ShapeKind.Arrow)
                {
                    // straight: snap to every 45 degrees
                    Vector v = p - shape.A;
                    double angle = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
                    p = shape.A + new Vector(Math.Cos(angle), Math.Sin(angle)) * v.Length;
                }
                else if (shift && shape.Kind is ShapeKind.Rectangle or ShapeKind.Ellipse)
                {
                    double s = Math.Max(Math.Abs(p.X - shape.A.X), Math.Abs(p.Y - shape.A.Y));
                    p = new Point(shape.A.X + Math.Sign(p.X - shape.A.X) * s, shape.A.Y + Math.Sign(p.Y - shape.A.Y) * s);
                }
                shape.B = p;
                RenderItems(pv.Index);
                break;
            }
        }
    }

    private void OverlayUp(PageView pv, MouseButtonEventArgs e)
    {
        if (_dragPage != pv) return;
        pv.Overlay.ReleaseMouseCapture();
        if (_guides.Count > 0) { ResetSnap(); RenderItems(pv.Index); } else ResetSnap();          // (the alignment lines go when the mouse is let go)
        var drawn = _drawing;
        _drawing = null;
        var mode = _drag;
        _drag = DragMode.None;
        if (mode == DragMode.RunMove)
        {
            bool moved = _runDragMoved; _runDragMoved = false;
            pv.Overlay.Cursor = CursorFor(_tool);
            if (!moved) EditTextAt(pv, _dragStart);                                    // pressed and let go: change the words
            else { _hoverRun = null; UpdateEditButtons(); RenderItems(pv.Index); }               // (the dashed frame was around the old place)
            _runDragItem = null; _runDragRun = null;
            return;
        }
        if (mode is DragMode.Move or DragMode.Resize && _selected is PageObjectItem pickedUp) { CommitPageObject(pickedUp); return; }
        if (mode == DragMode.GroupMove) { UpdateEditButtons(); return; }
        if (drawn is FieldItem newField && mode == DragMode.Draw) { FinishField(newField, _dragStart); return; }
        if (drawn is TableItem newTable && mode == DragMode.Draw) { FinishTable(newTable); return; }
        if (drawn is LinkDraft newLink && mode == DragMode.Draw) { FinishLink(newLink); return; }
        if (mode == DragMode.Marquee)
        {
            var box = _marquee ?? Rect.Empty;
            _marquee = null;
            var hit = box.Width < 3 && box.Height < 3 ? new List<EditItem>() : WithGroupMates(_items.Where(i => i.Page == pv.Index && !i.Bounds.IsEmpty && box.IntersectsWith(i.Bounds)));
            if (hit.Count == 1) Select(hit[0]);
            else if (hit.Count > 1) { _selected = null; _group.AddRange(hit); UpdateProperties(); }
            else if (box.Width >= 3 || box.Height >= 3) { if (PickPageObjectsIn(pv.Index, box) is { } many) Select(many); }       // (nothing of this session's: what is already drawn inside the box)
            RenderItems(pv.Index);
            UpdateEditButtons();
            return;
        }
        if (drawn is ShapeItem shape && mode == DragMode.Draw)
        {
            var r = new Rect(shape.A, shape.B);
            bool line = shape.Kind is ShapeKind.Line or ShapeKind.Arrow;
            if ((line && (shape.B - shape.A).Length >= 3) || (!line && r.Width >= 3 && r.Height >= 3))
            {
                if (!line) { shape.A = r.TopLeft; shape.B = r.BottomRight; }
                Add(shape);
                return;
            }
        }
        else if (drawn is InkItem ink && mode == DragMode.Ink && ink.Strokes[0].Count > 0) { Add(ink); return; }
        RenderItems(pv.Index);
    }

    private static bool IsInside(DependencyObject d, DependencyObject? container)
    {
        for (var x = d; x != null && container != null; x = x is Visual || x is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x == container) return true;
        return false;
    }

    // ---------- typing text on a page ----------
    private void EditText(TextItem item, bool isNew)
    {
        CloseTextBox(commit: true);
        if (!isNew) Snapshot();
        else if (!_items.Contains(item)) { Snapshot(); _items.Add(item); }
        _typing = item;
        _selected = null;
        var box = new TextBox
        {
            Text = item.Text, AcceptsReturn = true, AcceptsTab = false, Padding = new Thickness(0), BorderThickness = new Thickness(0), MinWidth = 6,
            Template = (ControlTemplate)XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='TextBox'>" +
                "<Border Background='#185B8DEF' BorderBrush='#AA5B8DEF' BorderThickness='0.6'><ScrollViewer x:Name='PART_ContentHost' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Margin='0' Padding='0' Focusable='False' HorizontalScrollBarVisibility='Hidden' VerticalScrollBarVisibility='Hidden' /></Border></ControlTemplate>"),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(box, "PdfTextBox");
        StyleTextBox(box, item);
        box.TextChanged += (_, _) => { if (_typing != null) { _typing.Text = box.Text; _dirty = true; UpdateTitle(); } };
        box.LostKeyboardFocus += (_, ev) =>
        {
            // focus moved to the tool bar (colour, size, font): keep typing; anywhere else: finished
            if (ev.NewFocus is DependencyObject nf && IsInside(nf, _editBar)) return;
            Dispatcher.BeginInvoke(() => { if (_textBox == box && !box.IsKeyboardFocusWithin) CloseTextBox(commit: true); });
        };
        _textBox = box;
        RenderItems(item.Page);
        UpdateProperties();
        Dispatcher.BeginInvoke(() => { box.Focus(); box.CaretIndex = box.Text.Length; }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void StyleTextBox(TextBox box, TextItem item)
    {
        box.FontFamily = TextItem.Family(item.Font, item.FontName);
        box.FontSize = item.FontSize;
        box.FontWeight = item.Bold ? FontWeights.Bold : FontWeights.Normal;
        box.FontStyle = item.Italic ? FontStyles.Italic : FontStyles.Normal;
        box.TextDecorations = item.Underline ? System.Windows.TextDecorations.Underline : null;
        box.TextAlignment = item.Align switch { 1 => TextAlignment.Center, 2 => TextAlignment.Right, _ => TextAlignment.Left };
        if (item.BoxWidth > 0) { box.TextWrapping = TextWrapping.Wrap; box.Width = Math.Max(10, item.BoxWidth - 2 * item.Inset); }
        else { box.TextWrapping = TextWrapping.NoWrap; box.Width = double.NaN; }
        box.Foreground = new SolidColorBrush(item.Color);
        box.CaretBrush = new SolidColorBrush(item.Color.R + item.Color.G + item.Color.B > 600 ? Colors.Black : item.Color);
        // (a WPF text box draws its text 2 units in from its left edge)
        Canvas.SetLeft(box, item.TopLeft.X + item.Inset - 2); Canvas.SetTop(box, item.TopLeft.Y + item.Inset);
        box.RenderTransformOrigin = new Point(0.5, 0.5);
        box.RenderTransform = item.AngleDeg != 0 ? new RotateTransform(item.AngleDeg) : Transform.Identity;
    }

    private void CloseTextBox(bool commit)
    {
        CloseRunBox();
        if (_textBox == null || _typing == null) { _textBox = null; _typing = null; return; }
        var item = _typing;
        item.Text = _textBox.Text.TrimEnd();
        _textBox = null; _typing = null;
        if (item.Text.Length == 0)
        {
            _items.Remove(item);                                    // (an empty text is no text)
            if (_undo.Count > 0 && _undo.Peek().Count == _items.Count && !_undo.Peek().OfType<TextItem>().Any(t => t.TopLeft == item.TopLeft && t.Text.Length > 0)) { _undo.Pop(); _dirty = _undo.Count > 0; UpdateTitle(); }
        }
        RenderItems(item.Page);
        UpdateEditButtons(); UpdateProperties();
        _scroll.Focus();
    }

    // ---------- signature and pictures ----------
    /// <summary>Where the person is looking: the middle of the visible part of the current page, in its points.</summary>
    private (PageView Page, Point At) VisibleSpot()
    {
        var pv = _pages[Math.Clamp(_current, 0, _pages.Count - 1)];
        var mid = _scroll.TranslatePoint(new Point(_scroll.ViewportWidth / 2, _scroll.ViewportHeight / 2), pv.Overlay);
        mid = new Point(Math.Clamp(mid.X, 0, pv.Overlay.Width), Math.Clamp(mid.Y, 0, pv.Overlay.Height));
        return (pv, mid);
    }

    /// <param name="page">with <paramref name="into"/>: a signature field to fill (the signature is fitted inside it)</param>
    private void AddSignature(int? page = null, Rect? into = null)
    {
        if (_pages.Count == 0) return;
        var dlg = new PdfSignatureWindow { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Chosen == null) return;
        var (pv, at) = VisibleSpot();
        var sig = dlg.Chosen;
        double w = Math.Min(170, pv.Overlay.Width * 0.4), h = w / Math.Max(0.1, sig.Aspect);
        if (page is int pg && into is Rect field && pg >= 0 && pg < _pages.Count)
        {
            pv = _pages[pg];
            at = new Point(field.X + field.Width / 2, field.Y + field.Height / 2);
            w = Math.Min(field.Width * 0.95, field.Height * 0.95 * sig.Aspect); h = w / Math.Max(0.1, sig.Aspect);
        }
        var box = new Rect(at.X - w / 2, at.Y - h / 2, w, h);
        if (sig.Strokes != null)
        {
            var ink = new InkItem { Page = pv.Index, Signature = true, Color = sig.Color, Width = Math.Max(0.8, w * sig.WidthRatio) };
            foreach (var s in sig.Strokes) ink.Strokes.Add(s.Select(p => new Point(box.X + p.X * w, box.Y + p.Y * h)).ToList());
            Add(ink, select: true);
        }
        else if (sig.Picture != null) Add(new ImageItem { Page = pv.Index, Box = box, Pixels = sig.Picture }, select: true);
        Toast("Drag it into place; pull its corner to resize, the round handle turns it");
    }

    private void AddPicture()
    {
        if (_pages.Count == 0) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Add a picture", Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        BitmapSource picture; byte[]? jpeg = null;
        try
        {
            // big photos are made a sensible size for a page (they'd make the PDF huge); phone photos are turned the right way up
            var loaded = PdfCombiner.LoadPicture(File.ReadAllBytes(dlg.FileName), 2400);
            jpeg = loaded.Jpeg;
            picture = new FormatConvertedBitmap(loaded.Pixels, PixelFormats.Bgra32, null, 0);
            picture.Freeze();
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Toast("Couldn't read that picture: " + e.Message);
            return;
        }
        var (pv, at) = VisibleSpot();
        double w = Math.Min(picture.PixelWidth * 0.75, pv.Overlay.Width * 0.5), h = w * picture.PixelHeight / picture.PixelWidth;
        if (h > pv.Overlay.Height * 0.6) { h = pv.Overlay.Height * 0.6; w = h * picture.PixelWidth / picture.PixelHeight; }
        Add(new ImageItem { Page = pv.Index, Box = new Rect(at.X - w / 2, at.Y - h / 2, w, h), Pixels = picture, Jpeg = jpeg }, select: true);
        Toast("Drag it into place; pull its corner to resize");
    }

    // ---------- keyboard while editing ----------
    private bool EditorKey(KeyEventArgs e)
    {
        if (!_editing) return false;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (_runBox != null && _runBox.IsKeyboardFocusWithin)
        {
            if (e.Key == Key.Enter && shift) return false;                           // (Shift+Enter: a new line, the box handles it)
            if (e.Key is Key.Escape or Key.Enter) { CloseRunBox(); return true; }
            return false;
        }
        if (_textBox != null && _textBox.IsKeyboardFocusWithin)
        {
            if (e.Key == Key.Escape) { CloseTextBox(commit: true); return true; }
            return false;                                                      // (the text box gets every other key)
        }
        if (e.Key == Key.Delete && _strip.IsKeyboardFocusWithin) { DeleteSelectedPages(); return true; }          // (pages chosen at the side)
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        if (alt && !ctrl && !shift && Keyboard.FocusedElement is not TextBox)
        {
            Key letterKey = e.Key == Key.System ? e.SystemKey : e.Key;            // (with Alt held, WPF reports the letter as SystemKey)
            var hit = Array.Find(ToolKeys, k => k.Key == letterKey);
            if (hit.Letter != null)
            {
                if (_toolButtons[hit.Tool].IsChecked == true) SetTool(hit.Tool); else _toolButtons[hit.Tool].IsChecked = true;
                return true;
            }
        }
        switch (e.Key)
        {
            case Key.C when ctrl && !shift && (_selected != null || _group.Count > 0) && !HasSelection: return CopyItems();
            case Key.X when ctrl && !shift && (_selected != null || _group.Count > 0): CutItems(); return true;
            case Key.V when ctrl && !shift && HasCopy: return PasteItems();
            case Key.D when ctrl && !shift && (_selected != null || _group.Count > 0): if (CopyItems()) PasteItems(); return true;
            case Key.G when ctrl && shift && CanGroup: GroupChosen(); return true;
            case Key.U when ctrl && shift && CanUngroup: UngroupChosen(); return true;
            case Key.OemCloseBrackets when ctrl && CanArrange: Arrange(shift ? ZMove.ToFront : ZMove.Forward); return true;
            case Key.OemOpenBrackets when ctrl && CanArrange: Arrange(shift ? ZMove.ToBack : ZMove.Backward); return true;
            case Key.Z when ctrl && !shift: Undo(); return true;
            case Key.Y when ctrl: Redo(); return true;
            case Key.Z when ctrl && shift: Redo(); return true;
            case Key.Delete or Key.Back when _selected != null || _group.Count > 0: DeleteSelected(); return true;
            case Key.Escape when _group.Count > 0: ClearGroup(); return true;
            case Key.Escape when _selected != null: Select(null); return true;
            case Key.Left or Key.Right or Key.Up or Key.Down when _group.Count > 0:
            {
                double gstep = shift ? 10 : 1;
                var gd = e.Key switch { Key.Left => new Vector(-gstep, 0), Key.Right => new Vector(gstep, 0), Key.Up => new Vector(0, -gstep), _ => new Vector(0, gstep) };
                Snapshot();
                foreach (var g in _group) g.MoveBy(gd);
                foreach (int pg in _group.Select(i => i.Page).Distinct().ToList()) RenderItems(pg);
                return true;
            }
            case Key.Enter when _selected is TextItem t: EditText(t, isNew: false); return true;
            case Key.Enter when _selected is ColumnsItem columns: EditColumns(columns); return true;
            case Key.Enter when _selected is TableItem tableSel: EditTable(tableSel); return true;
            case Key.Enter when _selected is FieldItem fieldSel: EditFieldOptions(fieldSel); return true;
            case Key.Enter when _selected is LinkPick linkSel: EditLink(linkSel); return true;
            case Key.Left or Key.Right or Key.Up or Key.Down when _selected != null:
            {
                double step = shift ? 10 : 1;
                var d = e.Key switch { Key.Left => new Vector(-step, 0), Key.Right => new Vector(step, 0), Key.Up => new Vector(0, -step), _ => new Vector(0, step) };
                if (_selected is PageObjectItem nudged) { nudged.MoveBy(d); CommitPageObject(nudged); return true; }
                Snapshot();
                _selected.MoveBy(d);
                RenderItems(_selected.Page);
                return true;
            }
        }
        return false;
    }

    // ---------- saving ----------
    private async Task SaveEditsAsync(bool saveAs)
    {
        CloseTextBox(commit: true);
        if (_pdf == null || _path == null) return;
        string target = _path;
        if (saveAs)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save the edited PDF as", Filter = "PDF|*.pdf",
                FileName = System.IO.Path.GetFileNameWithoutExtension(_path) + " (edited).pdf", InitialDirectory = System.IO.Path.GetDirectoryName(_path),
            };
            if (dlg.ShowDialog(this) != true) return;
            target = dlg.FileName;
        }
        else if (!_dirty) { Toast("Nothing changed yet"); return; }
        await Task.Yield();
        SaveEdits(target);
    }

    private List<DocState>? _historyAfterSave;       // the undo history to keep through the save: what it was, plus a "before this save" step (LoadAsync puts it back after the reload)

    /// <summary>Writes the items into the pages and saves to <paramref name="target"/>, then shows the saved file. False when it failed.</summary>
    private bool SaveEdits(string target)
    {
        if (_pdf == null || _path == null) return false;
        var pdf = _pdf;
        string? password = pdf.Password;
        var marks = _items.SelectMany(i => i.Marks()).ToList();
        var redactions = marks.OfType<PdfRedactMark>().ToList();
        var newFields = marks.OfType<PdfFieldMark>().ToList();
        if (newFields.Count > 0 && pdf.IsProtected) { UMessage.Show(this, "A password-protected PDF can't get new form fields here (the file would lose its protection). Open a copy without the password protection.", "Form fields", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        var flattened = new List<int>();
        if (redactions.Count > 0)
        {
            if (pdf.IsProtected) { UMessage.Show(this, "A password-protected PDF can't be redacted here (the file would lose its protection). Open a copy without the password protection.", "Redact", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            bool replaces = string.Equals(System.IO.Path.GetFullPath(target), System.IO.Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase);
            int pages = redactions.Select(r => r.Page).Distinct().Count();
            var answer = UMessage.Ask(this,
                $"{redactions.Count} black box{(redactions.Count == 1 ? "" : "es")} on {pages} page{(pages == 1 ? "" : "s")}.\n\nSaving removes what is under {(redactions.Count == 1 ? "it" : "them")} from the file for good: the words, the parts of pictures, links and comments. It can't be undone in the saved file.\n\nThe document's properties (title, author…) and the pages' preview pictures are cleared too."
                + (replaces ? "\n\nThis replaces the file. To keep the original, choose Save as… instead." : ""),
                "Redact", MessageBoxImage.Warning, MessageBoxResult.Cancel, MessageBoxResult.Cancel, ("Redact and save", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.Cancel));
            if (answer != MessageBoxResult.Yes) return false;
        }
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            // what the document looked like before this save: Undo after saving goes back to it (not for redaction: that must stay gone)
            // the history goes on through the save: a step "before this save" (the document, and what was added on the pages with its own undo steps) is put after the older ones.
            // Not after a redaction: that must stay gone, so nothing before it can be gone back to.
            _historyAfterSave = new List<DocState>();
            if (redactions.Count == 0 && pdf.Length < 150_000_000)
            {
                try
                {
                    _historyAfterSave.AddRange(_pageUndo);
                    _historyAfterSave.Add(new DocState(pdf.SaveToBytes(), _items.Select(i => i.Clone()).ToList(), UndoSteps(), SaveMarker: true));
                }
                catch (Exception e) when (e is IOException or InvalidOperationException or OutOfMemoryException) { _historyAfterSave = new List<DocState>(); }
            }
            PdfMarkWriter.Apply(pdf, marks);
            flattened = PdfMarkWriter.FlattenedPages.Select(p => p + 1).OrderBy(p => p).ToList();
            byte[] bytes = pdf.SaveToBytes();
            if (redactions.Count > 0) bytes = PdfRedactor.Finish(bytes, redactions);       // (cleaned and checked: if anything is left under a box, nothing is saved)
            if (newFields.Count > 0 || _fieldsLifted) bytes = PdfFormFields.Add(pdf, bytes, newFields);     // (PDFium can't make form fields: they are written into the saved bytes; also tidies the form after a saved field was picked up)
            // written next to it first, so a failure can't leave half a file
            string temp = target + ".utylix-tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, target, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException or OutOfMemoryException or InvalidOperationException or PdfSharp.PdfSharpException)
        {
            Mouse.OverrideCursor = null;
            _historyAfterSave = null;
            // the open copy may hold half the changes now: read the file again, keeping what was added (still editable)
            try { var fresh = PdfFile.Open(_path, password); _pdf.Dispose(); _pdf = fresh; RedrawPages(); } catch (Exception) { }
            UMessage.Show(this, "Couldn't save: " + e.Message + (e is UnauthorizedAccessException or IOException ? "\n\nIs the file open in another program, or in a folder you can't write to? Try \"Save as…\"." : ""), "Utylix Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { Mouse.OverrideCursor = null; }
        _dirty = false; _fieldsLifted = false;
        bool other = !string.Equals(System.IO.Path.GetFullPath(target), System.IO.Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase);
        _ = LoadAsync(target, password, keepEditing: true);
        string saved = other ? "Saved as " + System.IO.Path.GetFileName(target) : "Saved";
        if (redactions.Count > 0) saved += flattened.Count == 0 ? ": what was under the black boxes is gone from the file" : $": what was under the black boxes is gone. Page{(flattened.Count == 1 ? "" : "s")} {string.Join(", ", flattened)} had to become a picture (its text can't be selected any more)";
        Toast(saved);
        return true;
    }

    /// <summary>The page pictures are drawn again (the document behind them changed).</summary>
    private void RedrawPages()
    {
        _generation++;
        foreach (var p in _pages) { p.Picture.Source = null; p.RenderedWidth = 0; p.WantedWidth = 0; }
        RebuildThumbs();
        _renderTimer.Stop(); _renderTimer.Start();
    }
}
