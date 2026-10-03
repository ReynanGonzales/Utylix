using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>What the person chose for stamps and dates in the PDF editor (kept in pdf-stamps.json in the data folder).</summary>
internal sealed class PdfStampSettings
{
    public List<string> Recent { get; set; } = new();              // custom stamps typed before, newest first
    public string DateFormat { get; set; } = "yyyy-MM-dd";
    public bool StampWithDate { get; set; }

    private static string PathOnDisk => System.IO.Path.Combine(App.DataDir, "pdf-stamps.json");
    private static PdfStampSettings? _current;
    public static PdfStampSettings Current => _current ??= Load();

    private static PdfStampSettings Load()
    {
        try { if (File.Exists(PathOnDisk)) return JsonSerializer.Deserialize<PdfStampSettings>(File.ReadAllText(PathOnDisk)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(this)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* forgetting is not worth an error */ }
    }

    public static string Today(string format)
    {
        try { return DateTime.Today.ToString(format, CultureInfo.CurrentCulture); }
        catch (FormatException) { return DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
    }
}

/// <summary>Stamps (Approved, Paid, Confidential ...) and today's date: placed like text, movable and resizable until saved.</summary>
public sealed partial class PdfWindow
{
    /// <summary>The ready-made stamps and their colours.</summary>
    private static readonly (string Label, Color Color)[] StampPresets =
    {
        ("APPROVED", Color.FromRgb(0x2E, 0x7D, 0x32)), ("RECEIVED", Color.FromRgb(0x15, 0x65, 0xC0)), ("PAID", Color.FromRgb(0x2E, 0x7D, 0x32)),
        ("FINAL", Color.FromRgb(0x2E, 0x7D, 0x32)), ("REJECTED", Color.FromRgb(0xC6, 0x28, 0x28)), ("CONFIDENTIAL", Color.FromRgb(0xC6, 0x28, 0x28)),
        ("URGENT", Color.FromRgb(0xC6, 0x28, 0x28)), ("DRAFT", Color.FromRgb(0xEF, 0x6C, 0x00)), ("COPY", Color.FromRgb(0x15, 0x65, 0xC0)),
    };

    private static readonly string[] DateFormats = { "yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy", "d MMM yyyy", "MMMM d, yyyy", "dddd, MMMM d, yyyy" };

    /// <summary>
    /// A flat button for the dark tool bars: the Windows light theme gives the normal dialog button a pale face, which left its white text
    /// unreadable on the dark bar (Edit, Save as, Done, Page size).
    /// </summary>
    private static ControlTemplate BarButtonTemplate() => (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
        "<Border x:Name='bd' Background='#26FFFFFF' BorderBrush='#40FFFFFF' BorderThickness='1' CornerRadius='6' Padding='{TemplateBinding Padding}'>" +
        "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' TextElement.Foreground='White' /></Border>" +
        "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#45FFFFFF' /></Trigger>" +
        "<Trigger Property='IsPressed' Value='True'><Setter TargetName='bd' Property='Background' Value='#60FFFFFF' /></Trigger>" +
        "<Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45' /></Trigger></ControlTemplate.Triggers></ControlTemplate>");

    /// <summary>Text for a button on a dark bar: the colour is set on the text itself (the app's own text style would otherwise make it dark in Windows' light theme).</summary>
    private static TextBlock BarLabel(string text) => new() { Text = text, Foreground = Brushes.White };

    private string _stampLabel = "APPROVED";
    private bool _stampMenuBusy;                                    // (choosing from the menu selects the tool, which must not open the menu again)

    private void PickStampTool()
    {
        _stampMenuBusy = true;
        try { _toolButtons[EditTool.Stamp].IsChecked = true; } finally { _stampMenuBusy = false; }
    }

    /// <summary>A rounded box with a heavy border and a bold word (and, if asked, a date under it), like a rubber stamp.</summary>
    private sealed class StampItem : EditItem
    {
        public Rect Box;
        public string Label = "";
        public string? DateText;

        public double AngleDeg;                          // turned clockwise around the middle of the box

        public override Rect Bounds => Box;
        public override bool KeepAspect => true;
        public override bool CanRotate => true;
        public override double Angle => AngleDeg;
        public override void SetAngle(double degrees) => AngleDeg = degrees;
        public override bool Hit(Point p) => Inflate(Box, 3).Contains(AngleDeg == 0 ? p : Rot(p, Centre, -AngleDeg));
        Point Centre => new(Box.X + Box.Width / 2, Box.Y + Box.Height / 2);
        public override EditItem Clone() => (StampItem)MemberwiseClone();
        public override void MoveBy(Vector d) => Box.Offset(d);
        public override void ResizeTo(Rect r) => Box = r;

        static readonly FontFamily Arial = new("Arial");
        static Typeface Face => new(Arial, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        static double Width100(string text) => new FormattedText(text.Length == 0 ? " " : text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 100, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;

        /// <summary>The stamp that fits its text, for placing: a size that suits the word (and the date line).</summary>
        public static Size NaturalSize(string label, bool withDate)
        {
            double w = Math.Clamp(Width100(label) * 0.26 + 38, 120, 340);
            return new Size(w, withDate ? 64 : 44);
        }

        double BorderWidth => Math.Max(1.4, Box.Height * 0.06);

        /// <summary>Where the two lines of text go: size and top-left of each.</summary>
        (double MainSize, Point MainAt, double SubSize, Point SubAt) Layout()
        {
            bool sub = !string.IsNullOrEmpty(DateText);
            double inner = Box.Width - 2 * BorderWidth * 3.2;
            double mainSize = Math.Min(inner / Width100(Label) * 100, Box.Height * (sub ? 0.42 : 0.56));
            double subSize = sub ? Math.Min(mainSize * 0.52, inner / Width100(DateText!) * 100) : 0;
            double lineMain = Arial.LineSpacing * mainSize, lineSub = Arial.LineSpacing * subSize, gap = sub ? Box.Height * 0.03 : 0;
            double total = lineMain + gap + lineSub, top = Box.Y + (Box.Height - total) / 2;
            var mainAt = new Point(Box.X + (Box.Width - Width100(Label) * mainSize / 100) / 2, top);
            var subAt = sub ? new Point(Box.X + (Box.Width - Width100(DateText!) * subSize / 100) / 2, top + lineMain + gap) : default;
            return (mainSize, mainAt, subSize, subAt);
        }

        List<PdfFigure> Outlines(out double outerWidth, out double innerWidth)
        {
            outerWidth = BorderWidth; innerWidth = Math.Max(0.6, BorderWidth * 0.35);
            double inset = BorderWidth * 1.9;
            return new() { Rounded(Box, Math.Min(Box.Width, Box.Height) * 0.2), Rounded(new Rect(Box.X + inset, Box.Y + inset, Math.Max(2, Box.Width - 2 * inset), Math.Max(2, Box.Height - 2 * inset)), Math.Min(Box.Width, Box.Height) * 0.12) };
        }

        static PdfFigure Rounded(Rect r, double rad)
        {
            rad = Math.Min(rad, Math.Min(r.Width, r.Height) / 2);
            double k = 0.5523 * rad;
            double x0 = r.X, y0 = r.Y, x1 = r.Right, y1 = r.Bottom;
            return new PdfFigure(new Point(x0 + rad, y0), new[]
            {
                PdfSegment.Line(new Point(x1 - rad, y0)),
                PdfSegment.Bezier(new Point(x1 - rad + k, y0), new Point(x1, y0 + rad - k), new Point(x1, y0 + rad)),
                PdfSegment.Line(new Point(x1, y1 - rad)),
                PdfSegment.Bezier(new Point(x1, y1 - rad + k), new Point(x1 - rad + k, y1), new Point(x1 - rad, y1)),
                PdfSegment.Line(new Point(x0 + rad, y1)),
                PdfSegment.Bezier(new Point(x0 + rad - k, y1), new Point(x0, y1 - rad + k), new Point(x0, y1 - rad)),
                PdfSegment.Line(new Point(x0, y0 + rad)),
                PdfSegment.Bezier(new Point(x0, y0 + rad - k), new Point(x0 + rad - k, y0), new Point(x0 + rad, y0)),
            }, true);
        }

        public override FrameworkElement Build()
        {
            var figures = Outlines(out double outer, out double inner);
            var brush = new SolidColorBrush(Color);
            var canvas = new Canvas();
            canvas.Children.Add(new System.Windows.Shapes.Path { Data = Geometry(new[] { figures[0] }), Stroke = brush, StrokeThickness = outer, StrokeLineJoin = PenLineJoin.Round });
            canvas.Children.Add(new System.Windows.Shapes.Path { Data = Geometry(new[] { figures[1] }), Stroke = brush, StrokeThickness = inner, StrokeLineJoin = PenLineJoin.Round });
            var (ms, mp, ss, sp) = Layout();
            TextBlock Line(string text, double size, Point at)
            {
                var t = new TextBlock { Text = text, FontFamily = Arial, FontWeight = FontWeights.Bold, FontSize = size, Foreground = brush };
                Canvas.SetLeft(t, at.X); Canvas.SetTop(t, at.Y);
                return t;
            }
            canvas.Children.Add(Line(Label, ms, mp));
            if (!string.IsNullOrEmpty(DateText)) canvas.Children.Add(Line(DateText!, ss, sp));
            if (AngleDeg != 0) canvas.RenderTransform = new RotateTransform(AngleDeg, Centre.X, Centre.Y);
            return canvas;
        }

        static PdfFigure Turned(PdfFigure f, Point c, double deg) => new(Rot(f.Start, c, deg),
            f.Segments.Select(s => new PdfSegment(s.Curve ? Rot(s.C1, c, deg) : default, s.Curve ? Rot(s.C2, c, deg) : default, Rot(s.To, c, deg), s.Curve)).ToList(), f.Closed);

        public override IEnumerable<PdfMark> Marks()
        {
            var figures = Outlines(out double outer, out double inner);
            var c = Centre;
            if (AngleDeg != 0) figures = figures.Select(f => Turned(f, c, AngleDeg)).ToList();
            yield return new PdfPathMark(Page, new[] { figures[0] }, Color, outer, null, false);
            yield return new PdfPathMark(Page, new[] { figures[1] }, Color, inner, null, false);
            var (ms, mp, ss, sp) = Layout();
            foreach (var m in new TextItem { Page = Page, TopLeft = mp, Text = Label, Font = PdfFontKind.Sans, Bold = true, FontSize = ms, Color = Color }.Marks()) yield return TurnedText(m, c);
            if (!string.IsNullOrEmpty(DateText))
                foreach (var m in new TextItem { Page = Page, TopLeft = sp, Text = DateText!, Font = PdfFontKind.Sans, Bold = true, FontSize = ss, Color = Color }.Marks()) yield return TurnedText(m, c);
        }

        PdfMark TurnedText(PdfMark m, Point centre) => AngleDeg != 0 && m is PdfTextMark t ? t with { Angle = AngleDeg, Pivot = centre } : m;
    }

    /// <summary>A point turned clockwise (as seen on the page, y pointing down) by degrees around a centre.</summary>
    private static Point Rot(Point p, Point c, double degrees)
    {
        double a = degrees * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a), dx = p.X - c.X, dy = p.Y - c.Y;
        return new Point(c.X + dx * cos - dy * sin, c.Y + dx * sin + dy * cos);
    }

    private double _stampAngle;                      // the tilt of the last stamp the person turned: the next stamp starts with it

    // ---------- placing ----------
    private void PlaceStamp(PageView pv, Point at)
    {
        var settings = PdfStampSettings.Current;
        string? date = settings.StampWithDate ? PdfStampSettings.Today(settings.DateFormat) : null;
        var size = StampItem.NaturalSize(_stampLabel, date != null);
        var box = new Rect(at.X - size.Width / 2, at.Y - size.Height / 2, size.Width, size.Height);
        // inside the page
        box.X = Math.Clamp(box.X, 0, Math.Max(0, pv.Overlay.Width - box.Width)); box.Y = Math.Clamp(box.Y, 0, Math.Max(0, pv.Overlay.Height - box.Height));
        Add(new StampItem { Page = pv.Index, Box = box, Label = _stampLabel, DateText = date, Color = _toolColors[EditTool.Stamp], AngleDeg = _stampAngle }, select: true);
    }

    private void PlaceDate(PageView pv, Point at)
    {
        string text = PdfStampSettings.Today(PdfStampSettings.Current.DateFormat);
        Add(new TextItem { Page = pv.Index, TopLeft = new Point(at.X - 1, at.Y - _textSize * 0.6), Text = text, Font = _font, FontName = _fontName, Bold = _bold, FontSize = _textSize, Color = _toolColors[EditTool.Date] }, select: true);
    }

    // ---------- the menus (click the tool again, or right-click it) ----------
    private void StampMenus()
    {
        var stamp = _toolButtons[EditTool.Stamp];
        var date = _toolButtons[EditTool.Date];
        stamp.PreviewMouseLeftButtonDown += (_, e) => { if (stamp.IsChecked == true) { ShowStampMenu(stamp); e.Handled = true; } };
        stamp.PreviewMouseRightButtonUp += (_, e) => { ShowStampMenu(stamp); e.Handled = true; };
        date.PreviewMouseLeftButtonDown += (_, e) => { if (date.IsChecked == true) { ShowDateMenu(date); e.Handled = true; } };
        date.PreviewMouseRightButtonUp += (_, e) => { ShowDateMenu(date); e.Handled = true; };
    }

    private void ChooseStamp(string label, Color? color)
    {
        _stampLabel = label;
        if (color is Color c) _toolColors[EditTool.Stamp] = c;
        _toolButtons[EditTool.Stamp].ToolTip = "Click on the page to put the stamp \"" + label + "\" (click this button again to choose another)";
    }

    internal void ShowStampMenu(UIElement anchor)
    {
        var settings = PdfStampSettings.Current;
        var menu = new ContextMenu();
        foreach (var (label, color) in StampPresets)
        {
            var item = new MenuItem { Header = label, IsChecked = _stampLabel == label };
            item.Icon = new TextBlock { Text = "■", Foreground = new SolidColorBrush(color), FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            item.Click += (_, _) => { ChooseStamp(label, color); PickStampTool(); };
            menu.Items.Add(item);
        }
        if (settings.Recent.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (string recent in settings.Recent)
            {
                var item = new MenuItem { Header = recent, IsChecked = _stampLabel == recent };
                item.Click += (_, _) => { ChooseStamp(recent, null); PickStampTool(); };
                menu.Items.Add(item);
            }
        }
        menu.Items.Add(new Separator());
        var custom = new MenuItem { Header = "Your own words…" };
        custom.Click += (_, _) =>
        {
            string? typed = TextPrompt.Ask(this, "Your own stamp", "What should the stamp say? (Keep it short, like PAID IN FULL.) Pick its colour with the colour dots.", "");
            string text = (typed ?? "").Trim().ToUpperInvariant();
            if (text.Length == 0) return;
            if (text.Length > 40) text = text[..40];
            settings.Recent.RemoveAll(r => string.Equals(r, text, StringComparison.OrdinalIgnoreCase));
            settings.Recent.Insert(0, text);
            if (settings.Recent.Count > 6) settings.Recent.RemoveRange(6, settings.Recent.Count - 6);
            settings.Save();
            ChooseStamp(text, null);
            PickStampTool();
        };
        menu.Items.Add(custom);
        var withDate = new MenuItem { Header = "Add today's date under the stamp", IsCheckable = true, IsChecked = settings.StampWithDate };
        withDate.Click += (_, _) => { settings.StampWithDate = withDate.IsChecked; settings.Save(); };
        menu.Items.Add(withDate);
        Themed(menu);
        menu.PlacementTarget = anchor; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
    }

    internal void ShowDateMenu(UIElement anchor)
    {
        var settings = PdfStampSettings.Current;
        var menu = new ContextMenu();
        foreach (string format in DateFormats)
        {
            var item = new MenuItem { Header = PdfStampSettings.Today(format), IsChecked = settings.DateFormat == format };
            item.Click += (_, _) =>
            {
                settings.DateFormat = format; settings.Save();
                _toolButtons[EditTool.Date].ToolTip = "Click on the page to put today's date (" + PdfStampSettings.Today(format) + "); click this button again to choose another look";
                _toolButtons[EditTool.Date].IsChecked = true;
            };
            menu.Items.Add(item);
        }
        Themed(menu);
        menu.PlacementTarget = anchor; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
    }
}
