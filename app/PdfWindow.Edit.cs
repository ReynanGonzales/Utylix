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
    private enum EditTool { Select, EditText, Text, Signature, Image, Check, Cross, Stamp, Date, Highlight, Underline, Strike, Note, Pen, Shapes, WhiteOut, Redact }

    // ---------- what can be on a page ----------
    private abstract class EditItem
    {
        public int Page;
        public Color Color;
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
    }

    private sealed class TextItem : EditItem
    {
        public Point TopLeft;
        public string Text = "";
        public PdfFontKind Font;
        public bool Bold;
        public double FontSize = 12;

        public string? FontName;                       // a font chosen by name (any installed one); null = the kind above

        public static FontFamily Family(PdfFontKind k, string? name = null) => new(name ?? k switch { PdfFontKind.Serif => "Times New Roman", PdfFontKind.Mono => "Courier New", _ => "Arial" });

        public override Rect Bounds
        {
            get
            {
                var family = Family(Font, FontName);
                var face = new Typeface(family, FontStyles.Normal, Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
                var lines = (Text.Length == 0 ? " " : Text).Replace("\r\n", "\n").Split('\n');
                double w = lines.Max(l => new FormattedText(l.Length == 0 ? " " : l, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, FontSize, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace);
                return new Rect(TopLeft, new Size(Math.Max(4, w), lines.Length * family.LineSpacing * FontSize));
            }
        }
        public override EditItem Clone() => (TextItem)MemberwiseClone();
        public override FrameworkElement Build()
        {
            var t = new TextBlock { Text = Text, FontFamily = Family(Font, FontName), FontSize = FontSize, FontWeight = Bold ? FontWeights.Bold : FontWeights.Normal, Foreground = new SolidColorBrush(Color) };
            Canvas.SetLeft(t, TopLeft.X); Canvas.SetTop(t, TopLeft.Y);
            return t;
        }
        public override IEnumerable<PdfMark> Marks()
        {
            if (Text.Trim().Length == 0) yield break;
            var f = Family(Font, FontName);
            yield return new PdfTextMark(Page, TopLeft, Text, Font, Bold, FontSize, Color, f.LineSpacing, f.Baseline, FontName);
        }
        public override void MoveBy(Vector d) => TopLeft += d;
        public override void ResizeTo(Rect r)
        {
            double h = Bounds.Height;
            if (h > 0) FontSize = Math.Clamp(FontSize * r.Height / h, 4, 200);
            TopLeft = r.TopLeft;
        }
        public override bool KeepAspect => true;
    }

    public enum ShapeKind { Rectangle, Ellipse, Line, Arrow, Highlight, WhiteOut, Check, Cross, Redact }

    private sealed class ShapeItem : EditItem
    {
        public ShapeKind Kind;
        public Point A, B;                       // corners, or the two ends of a line
        public double Width = 2;

        bool IsLine => Kind is ShapeKind.Line or ShapeKind.Arrow;
        Rect Box => new(A, B);
        double StampWidth => Math.Max(1.2, Math.Min(Box.Width, Box.Height) * 0.13);

        public override Rect Bounds => IsLine ? Inflate(Box, Width / 2 + (Kind == ShapeKind.Arrow ? ArrowHead : 0)) : Box;
        double ArrowHead => Math.Max(7, Width * 4);
        public override EditItem Clone() => (ShapeItem)MemberwiseClone();
        public override bool KeepAspect => Kind is ShapeKind.Check or ShapeKind.Cross;

        public override bool Hit(Point p)
        {
            if (!IsLine) return Inflate(Box, 3).Contains(p);
            // near the line
            Vector ab = B - A, ap = p - A;
            double t = ab.LengthSquared < 0.01 ? 0 : Math.Clamp((ap * ab) / ab.LengthSquared, 0, 1);
            return (p - (A + ab * t)).Length <= Width / 2 + 4;
        }

        public List<PdfFigure> Figures()
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
            Point Map(Point p) => new(r.X + (old.Width < 0.01 ? 0 : (p.X - old.X) / old.Width * r.Width), r.Y + (old.Height < 0.01 ? 0 : (p.Y - old.Y) / old.Height * r.Height));
            A = Map(A); B = Map(B);
        }
    }

    private sealed class InkItem : EditItem
    {
        public List<List<Point>> Strokes = new();
        public double Width = 2;
        public bool Signature;
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
        public List<PdfFigure> Figures() => Strokes.Where(s => s.Count > 0).Select(Smooth).ToList();

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
        public override Rect Bounds => Box;
        public override bool KeepAspect => true;
        public override EditItem Clone() => (ImageItem)MemberwiseClone();
        public override FrameworkElement Build()
        {
            var img = new Image { Source = Pixels, Width = Box.Width, Height = Box.Height, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            Canvas.SetLeft(img, Box.X); Canvas.SetTop(img, Box.Y);
            return img;
        }
        public override IEnumerable<PdfMark> Marks() { yield return new PdfImageMark(Page, Box, Jpeg, Jpeg == null ? Pixels : null); }
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
        [EditTool.Text] = Colors.Black, [EditTool.Stamp] = Color.FromRgb(0x2E, 0x7D, 0x32), [EditTool.Date] = Colors.Black, [EditTool.Signature] = Color.FromRgb(0x10, 0x2A, 0x8C), [EditTool.Check] = Colors.Black, [EditTool.Cross] = Colors.Black,
        [EditTool.Highlight] = Color.FromRgb(0xFF, 0xE0, 0x30), [EditTool.Underline] = Color.FromRgb(0x1E, 0x63, 0xE9), [EditTool.Strike] = Color.FromRgb(0xD3, 0x2F, 0x2F),
        [EditTool.Note] = Color.FromRgb(0xFF, 0xD5, 0x4F), [EditTool.Pen] = Color.FromRgb(0x10, 0x2A, 0x8C), [EditTool.Shapes] = Color.FromRgb(0xD3, 0x2F, 0x2F), [EditTool.WhiteOut] = Colors.White, [EditTool.Redact] = Colors.Black,
    };
    private ShapeKind _shapeKind = ShapeKind.Rectangle;                   // (what the Shapes tool draws)
    private double _textSize = 12, _lineWidth = 2;
    private PdfFontKind _font = PdfFontKind.Sans;
    private bool _bold;

    // dragging
    private enum DragMode { None, Move, Resize, Draw, Ink, Rotate }
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
        Color now = _selected != null && _selected is not ImageItem ? _selected.Color : _toolColors.TryGetValue(_tool, out var tc) ? tc : Colors.Black;
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

    private UIElement BuildEditBar()
    {
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        void ToolButton(EditTool tool, string glyph, string label, string tip, string font = "Segoe MDL2 Assets", UIElement? icon = null)
        {
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(icon ?? new TextBlock { Text = glyph, FontFamily = new FontFamily(font), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.White });
            content.Children.Add(new TextBlock { Text = label, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Soft, Margin = new Thickness(0, 2, 0, 0) });
            var b = new RadioButton { Content = content, GroupName = "pdftool", ToolTip = tip, Template = ToolChoiceTemplate(), Focusable = false, Margin = new Thickness(1, 0, 1, 0) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, "PdfTool" + tool);
            System.Windows.Automation.AutomationProperties.SetName(b, label);
            b.Checked += (_, _) => SetTool(tool);
            _toolButtons[tool] = b;
            tools.Children.Add(b);
        }
        ToolButton(EditTool.Select, "↖", "Select", "Select, move and resize what you added (double-click a text to change it)", "Segoe UI Symbol");
        ToolButton(EditTool.Text, "", "Text", "Click anywhere to type (also for filling in forms)");
        ToolButton(EditTool.EditText, "", "Edit text", "Click on text already in the PDF to change it (Enter or click elsewhere when done)");
        ToolButton(EditTool.Signature, "", "Sign", "Add your signature (draw it once, use it again)");
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
        ToolButton(EditTool.Shapes, "▭", "Shapes", "Box, circle, line or arrow: click again to choose (Shift: straight / square)", "Segoe UI Symbol");
        ShapesMenu();
        StampMenus();
        ToolButton(EditTool.WhiteOut, "⬜", "White-out", "Drag to cover something with white (it hides it on the page; the words underneath are not erased from the file)", "Segoe UI Symbol");
        ToolButton(EditTool.Redact, "", "Redact", "Drag a box over what must disappear for good: when you save, the words, pictures and comments under it are really removed from the file (not just covered)",
                   icon: new Border { Width = 24, Height = 15, Background = Brushes.Black, BorderBrush = Brushes.White, BorderThickness = new Thickness(1.4), CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) });

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

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(_undoButton); right.Children.Add(_redoButton); right.Children.Add(_deleteButton);
        right.Children.Add(_saveButton); right.Children.Add(saveAs); right.Children.Add(done);

        // first row: the tools, and undo / save / done; second row: colour, size and font of the tool or of what is selected
        var row1 = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(right, Dock.Right);
        row1.Children.Add(right);
        row1.Children.Add(new ScrollViewer { Content = tools, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });

        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Height = 32, Margin = new Thickness(6, 2, 0, 0) };
        // ("Colour" sits inside the colour row, so it goes away with it)
        _colorRow.Children.Insert(0, new TextBlock { Text = "Colour", Foreground = Soft, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        row2.Children.Add(_colorRow); row2.Children.Add(sizeRow);
        _toolHint.Foreground = Soft; _toolHint.FontSize = 12; _toolHint.VerticalAlignment = VerticalAlignment.Center;
        row2.Children.Add(_toolHint);
        _fontRow.Margin = new Thickness(14, 0, 0, 0);
        row2.Children.Add(_fontRow);

        var rows = new StackPanel();
        rows.Children.Add(row1);
        rows.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), Margin = new Thickness(0, 4, 0, 0) });
        rows.Children.Add(row2);

        _editBar = new Border { Child = rows, Background = new SolidColorBrush(Color.FromRgb(0x23, 0x28, 0x34)), Padding = new Thickness(8, 4, 8, 2), Visibility = Visibility.Collapsed, BorderBrush = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x1A)), BorderThickness = new Thickness(0, 1, 0, 1) };
        return _editBar;
    }

    private Button SmallBar(string glyph, string tip, Action action)
    {
        var b = new Button { Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13 }, ToolTip = tip, Template = ToolTemplate(), Foreground = Brushes.White, Focusable = false };
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => action();
        return b;
    }

    private static ControlTemplate ToolChoiceTemplate(bool small = false) => (ControlTemplate)XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='RadioButton'>" +
        $"<Border x:Name='bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='Transparent' CornerRadius='6' MinWidth='{(small ? 40 : 46)}' Height='{(small ? 26 : 44)}' Padding='{(small ? "6,0" : "3,2")}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' /></Border>" +
        "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#22FFFFFF' /></Trigger>" +
        "<Trigger Property='IsChecked' Value='True'><Setter TargetName='bd' Property='Background' Value='#5B8DEF' /></Trigger></ControlTemplate.Triggers></ControlTemplate>");

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
        _editBar.Visibility = Visibility.Visible;
        _editButton.Background = new SolidColorBrush(Color.FromArgb(60, 91, 141, 239));
        ClearTextSelection();
        _toolButtons[_tool].IsChecked = true;
        SetTool(_tool);
        UpdateEditButtons();
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
        _selected = null; _dirty = false; _drag = DragMode.None;
        _editing = keepEditing && _editing;
        if (_editBar != null) _editBar.Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
        if (_editButton != null) _editButton.Background = _editing ? new SolidColorBrush(Color.FromArgb(60, 91, 141, 239)) : Brushes.Transparent;
        foreach (var p in _pages) { p.Overlay.Children.Clear(); p.Overlay.Cursor = _editing ? CursorFor(_tool) : null; }
        UpdateTitle();
        if (_undoButton != null) UpdateEditButtons();
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
        CloseTextBox(commit: true);
        _tool = tool;
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
        bool text = item is TextItem || (item == null && _tool is EditTool.Text or EditTool.Date);
        bool line = item is ShapeItem { Kind: ShapeKind.Rectangle or ShapeKind.Ellipse or ShapeKind.Line or ShapeKind.Arrow } || item is InkItem { Signature: false }
                    || (item == null && _tool is EditTool.Pen or EditTool.Shapes);
        _fontRow.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        ((FrameworkElement)_sizeLabel.Parent).Visibility = text || line ? Visibility.Visible : Visibility.Collapsed;
        _sizeLabel.Text = text ? "Size" : "Line";
        double size = item switch { TextItem t => t.FontSize, ShapeItem s => s.Width, InkItem i => i.Width, _ => text ? _textSize : _lineWidth };
        _sizeText.Text = size.ToString(size < 10 ? "0.#" : "0", CultureInfo.InvariantCulture);
        _syncingFont = true;                                       // (showing the font must not change it)
        try
        {
            if (item is TextItem ti) { ShowFont(ti.Font, ti.FontName); _boldButton.IsChecked = ti.Bold; }
            else if (text) { ShowFont(_font, _fontName); _boldButton.IsChecked = _bold; }
        }
        finally { _syncingFont = false; }
        bool run = item is RunEditItem || (item == null && _tool == EditTool.EditText);
        _toolHint.Text = run ? "Click on a line of text to change it. Enter or a click beside it finishes; Save puts it into the PDF." : "";
        _toolHint.Visibility = run ? Visibility.Visible : Visibility.Collapsed;
        bool redact = item is ShapeItem { Kind: ShapeKind.Redact } || (item == null && _tool == EditTool.Redact);
        _colorRow.Visibility = item is ImageItem || run || redact ? Visibility.Collapsed : Visibility.Visible;
        if (redact) { _toolHint.Text = "Drag over what must go. Saving removes it from the file for good (Save replaces the file: use Save as… to keep the original)."; _toolHint.Visibility = Visibility.Visible; }
        if (run) ((FrameworkElement)_sizeLabel.Parent).Visibility = Visibility.Collapsed;
        if (item is StampItem) { _toolHint.Text = "Drag the round handle above the stamp to turn it (Shift: steps of 15°), the square corner to resize, the stamp itself to move."; _toolHint.Visibility = Visibility.Visible; }
        MarkColor();
    }

    private void SetColor(Color c)
    {
        if (_selected != null && _selected is not ImageItem)
        {
            Snapshot();
            _selected.Color = c;
            RenderItems(_selected.Page);
        }
        else _toolColors[_tool] = c;
        MarkColor();
    }

    private static readonly double[] TextSizes = { 6, 7, 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 60, 72 };
    private static readonly double[] LineWidths = { 0.5, 1, 1.5, 2, 3, 4, 6, 8, 12 };

    private void ChangeSize(int step)
    {
        static double Next(double[] steps, double now, int dir) =>
            dir > 0 ? steps.FirstOrDefault(s => s > now + 0.01, steps[^1]) : steps.LastOrDefault(s => s < now - 0.01, steps[0]);
        switch (_selected)
        {
            case TextItem t: Snapshot(); t.FontSize = Next(TextSizes, t.FontSize, step); _textSize = t.FontSize; RenderItems(t.Page); break;
            case ShapeItem s: Snapshot(); s.Width = Next(LineWidths, s.Width, step); _lineWidth = s.Width; RenderItems(s.Page); break;
            case InkItem i: Snapshot(); i.Width = Next(LineWidths, i.Width, step); _lineWidth = i.Width; RenderItems(i.Page); break;
            default:
                if (_tool is EditTool.Text or EditTool.Date) _textSize = Next(TextSizes, _textSize, step); else _lineWidth = Next(LineWidths, _lineWidth, step);
                break;
        }
        if (_typing != null && _textBox != null) { _typing.FontSize = _textSize; _textBox.FontSize = _textSize; }
        UpdateProperties();
    }

    private void SetFont(PdfFontKind? kind, bool? bold, string? name = null)
    {
        if (_syncingFont) return;
        if (kind is PdfFontKind k) { _font = k; _fontName = null; if (_fontLabel != null) _fontLabel.Text = "More fonts"; }
        if (name != null) _fontName = name;
        if (bold is bool b) _bold = b;
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
        _deleteButton.IsEnabled = _selected != null;
        _saveButton.IsEnabled = _dirty;
    }

    private void Add(EditItem item, bool select = false)
    {
        Snapshot();
        _items.Add(item);
        if (select) Select(item); else RenderItems(item.Page);
    }

    private void DeleteSelected()
    {
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
        if (_editing) pv.Overlay.Cursor = CursorFor(_tool);
    }

    // ---------- the mouse on a page ----------
    private void OverlayDown(PageView pv, MouseButtonEventArgs e)
    {
        if (!_editing || e.OriginalSource is DependencyObject d && IsInside(d, _textBox)) return;
        var p = e.GetPosition(pv.Overlay);
        bool wasTyping = _typing != null || _runTyping != null;
        CloseTextBox(commit: true);
        e.Handled = true;
        _dragPage = pv; _dragStart = p; _dragSnapshotTaken = false;
        // the handles of the selected item (a stamp can be turned and resized while the Stamp tool is still on)
        if (_tool is EditTool.Select or EditTool.Stamp && _selected != null)
        {
            if (OnRotateHandle(pv, p))
            {
                var c = CentreOf(_selected.Bounds);
                _drag = DragMode.Rotate; _dragBox = _selected.Bounds; _rotateStart = _selected.Angle; _rotateFrom = Math.Atan2(p.Y - c.Y, p.X - c.X) * 180 / Math.PI;
                pv.Overlay.CaptureMouse();
                return;
            }
            if (_tool == EditTool.Stamp && OnHandle(pv, p)) { _drag = DragMode.Resize; _dragBox = _selected.Bounds; pv.Overlay.CaptureMouse(); return; }
        }
        switch (_tool)
        {
            case EditTool.EditText:
                _dragPage = null;
                EditTextAt(pv, p, quiet: wasTyping);                                  // (a click beside the text just finishes it)
                return;
            case EditTool.Select:
                if (OnHandle(pv, p)) { _drag = DragMode.Resize; _dragBox = _selected!.Bounds; break; }
                var item = ItemAt(pv.Index, p);
                Select(item);
                if (item is TextItem t && e.ClickCount == 2) { EditText(t, isNew: false); return; }
                if (item is NoteItem n && e.ClickCount == 2) { OpenNote(n, isNew: false); return; }
                if (item != null) { _drag = DragMode.Move; _dragBox = item.Bounds; }
                break;
            case EditTool.Text:
                if (ItemAt(pv.Index, p) is TextItem existing) { EditText(existing, isNew: false); return; }
                if (wasTyping) return;                                                 // (a click outside just finishes the text)
                var text = new TextItem { Page = pv.Index, TopLeft = new Point(p.X - 1, p.Y - _textSize * 0.6), Font = _font, FontName = _fontName, Bold = _bold, FontSize = _textSize, Color = _toolColors[EditTool.Text] };
                EditText(text, isNew: true);
                return;
            case EditTool.Check or EditTool.Cross:
            {
                double s = 14;
                Add(new ShapeItem { Page = pv.Index, Kind = _tool == EditTool.Check ? ShapeKind.Check : ShapeKind.Cross, A = new Point(p.X - s / 2, p.Y - s / 2), B = new Point(p.X + s / 2, p.Y + s / 2), Color = _toolColors[_tool] });
                return;
            }
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
                _drawing = new ShapeItem { Page = pv.Index, Kind = kind, A = p, B = p, Color = _toolColors[_tool], Width = _lineWidth };
                _drag = DragMode.Draw;
                break;
        }
        pv.Overlay.CaptureMouse();
    }

    private void OverlayMove(PageView pv, MouseEventArgs e)
    {
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
                var delta = target - _selected.Bounds.TopLeft;
                if (delta.Length < 0.01) return;
                if (!_dragSnapshotTaken) { if ((p - _dragStart).Length < 1.5) return; Snapshot(); _dragSnapshotTaken = true; }
                _selected.MoveBy(delta);
                RenderItems(pv.Index);
                break;
            }
            case DragMode.Resize when _selected != null:
            {
                if (!_dragSnapshotTaken) { Snapshot(); _dragSnapshotTaken = true; }
                // (a turned item keeps its top-left corner where it is on the page, and the mouse is measured along the item's own directions)
                double angle = _selected.Angle;
                var c0 = CentreOf(_dragBox);
                var corner = angle == 0 ? _dragBox.TopLeft : Rot(_dragBox.TopLeft, c0, angle);
                var along = angle == 0 ? p - corner : Rot(p, corner, -angle) - corner;
                double w = Math.Max(4, along.X), h = Math.Max(4, along.Y);
                if (_selected.KeepAspect && _dragBox.Width > 0) h = w * _dragBox.Height / _dragBox.Width;
                var half = angle == 0 ? new Vector(w / 2, h / 2) : Rot(new Point(w / 2, h / 2), new Point(0, 0), angle) - new Point(0, 0);
                var centre = corner + half;
                _selected.ResizeTo(new Rect(centre.X - w / 2, centre.Y - h / 2, w, h));
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
        var drawn = _drawing;
        _drawing = null;
        var mode = _drag;
        _drag = DragMode.None;
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
        box.Foreground = new SolidColorBrush(item.Color);
        box.CaretBrush = new SolidColorBrush(item.Color.R + item.Color.G + item.Color.B > 600 ? Colors.Black : item.Color);
        // (a WPF text box draws its text 2 units in from its left edge)
        Canvas.SetLeft(box, item.TopLeft.X - 2); Canvas.SetTop(box, item.TopLeft.Y);
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
        Toast("Drag it into place; pull its corner to resize");
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
            if (e.Key is Key.Escape or Key.Enter) { CloseRunBox(); return true; }
            return false;
        }
        if (_textBox != null && _textBox.IsKeyboardFocusWithin)
        {
            if (e.Key == Key.Escape) { CloseTextBox(commit: true); return true; }
            return false;                                                      // (the text box gets every other key)
        }
        if (e.Key == Key.Delete && _strip.IsKeyboardFocusWithin) { DeleteSelectedPages(); return true; }          // (pages chosen at the side)
        switch (e.Key)
        {
            case Key.Z when ctrl && !shift: Undo(); return true;
            case Key.Y when ctrl: Redo(); return true;
            case Key.Z when ctrl && shift: Redo(); return true;
            case Key.Delete or Key.Back when _selected != null: DeleteSelected(); return true;
            case Key.Escape when _selected != null: Select(null); return true;
            case Key.Enter when _selected is TextItem t: EditText(t, isNew: false); return true;
            case Key.Left or Key.Right or Key.Up or Key.Down when _selected != null:
            {
                double step = shift ? 10 : 1;
                var d = e.Key switch { Key.Left => new Vector(-step, 0), Key.Right => new Vector(step, 0), Key.Up => new Vector(0, -step), _ => new Vector(0, step) };
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

    /// <summary>Writes the items into the pages and saves to <paramref name="target"/>, then shows the saved file. False when it failed.</summary>
    private bool SaveEdits(string target)
    {
        if (_pdf == null || _path == null) return false;
        var pdf = _pdf;
        string? password = pdf.Password;
        var marks = _items.SelectMany(i => i.Marks()).ToList();
        var redactions = marks.OfType<PdfRedactMark>().ToList();
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
            PdfMarkWriter.Apply(pdf, marks);
            flattened = PdfMarkWriter.FlattenedPages.Select(p => p + 1).OrderBy(p => p).ToList();
            byte[] bytes = pdf.SaveToBytes();
            if (redactions.Count > 0) bytes = PdfRedactor.Finish(bytes, redactions);       // (cleaned and checked: if anything is left under a box, nothing is saved)
            // written next to it first, so a failure can't leave half a file
            string temp = target + ".utylix-tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, target, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException or OutOfMemoryException)
        {
            Mouse.OverrideCursor = null;
            // the open copy may hold half the changes now: read the file again, keeping what was added (still editable)
            try { var fresh = PdfFile.Open(_path, password); _pdf.Dispose(); _pdf = fresh; RedrawPages(); } catch (Exception) { }
            UMessage.Show(this, "Couldn't save: " + e.Message + (e is UnauthorizedAccessException or IOException ? "\n\nIs the file open in another program, or in a folder you can't write to? Try \"Save as…\"." : ""), "Utylix Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { Mouse.OverrideCursor = null; }
        _dirty = false;
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
