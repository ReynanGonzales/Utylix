using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using IdmClone.Engine;
using QRCoder;

namespace IdmClone;

/// <summary>
/// QR maker (Add tab > QR): a QR code for a web link, some text, an e-mail, a phone number or a Wi-Fi network, made on this PC and put on the page as sharp shapes (not a picture), so it stays crisp
/// at any size and in any PDF reader. Look: square or round (round dots, round eyes and corners), a frame, and a picture (logo) in the middle. Move it, resize it (it stays square), or double-click it
/// to change it.
/// </summary>
public sealed partial class PdfWindow
{
    /// <summary>The dark squares of a QR code (without its empty border), or null when the text is too long for a QR code.</summary>
    private static bool[,]? QrModules(string payload, QRCodeGenerator.ECCLevel level, out string? problem)
    {
        problem = null;
        try
        {
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(payload, level);
            var matrix = data.ModuleMatrix;
            int total = matrix.Count, quiet = 4, n = total - 2 * quiet;
            var modules = new bool[n, n];
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) modules[y, x] = matrix[y + quiet][x + quiet];
            return modules;
        }
        catch (Exception e) when (e is QRCoder.Exceptions.DataTooLongException or ArgumentException)
        {
            problem = "That is too much for one QR code. Make it shorter (a QR code holds about 2,000 characters, fewer with a higher safety level).";
            return null;
        }
    }

    private sealed class QrItem : EditItem
    {
        public Rect Box;
        public string Payload = "";
        public string Kind = "link";                        // what the dialog showed: link, text, email, phone, wifi (and its fields, to change it again)
        public Dictionary<string, string> Fields = new();
        public int Level = 1;                               // 0 low, 1 medium, 2 quartile, 3 high
        public Color? Back = Colors.White;                  // the colour behind the squares (and their border); null = see-through
        public bool Round;                                  // round dots, round eyes and round corners
        public int BorderLevel;                             // 0 none, 1 thin, 2 medium, 3 thick (a frame round the code, in the colour of the squares)
        public PdfPicture? Logo;                            // a picture in the middle
        public int LogoSize = 1;                            // 0 small, 1 medium, 2 large
        public string LogoName = "";
        private bool[,]? _modules; private string _key = "";

        public override bool KeepAspect => true;
        public override Rect Bounds => Box;
        public override EditItem Clone() { var c = (QrItem)MemberwiseClone(); c.Fields = new Dictionary<string, string>(Fields); return c; }
        public override void MoveBy(Vector d) => Box.Offset(d);
        public override void ResizeTo(Rect r) { double s = Math.Max(16, Math.Min(r.Width, r.Height)); Box = new Rect(r.X, r.Y, s, s); }

        /// <summary>A picture in the middle hides some squares, so the code is made with a higher safety level (it still reads).</summary>
        public int EffectiveLevel => Logo == null ? Level : Math.Max(Level, LogoSize == 2 ? 3 : 2);

        public bool[,]? Modules
        {
            get
            {
                string key = Payload + "|" + EffectiveLevel;
                if (_modules == null || key != _key) { _modules = QrModules(Payload, (QRCodeGenerator.ECCLevel)EffectiveLevel, out _); _key = key; }
                return _modules;
            }
        }

        double Unit => Box.Width / ((Modules?.GetLength(0) ?? 21) + 8);                    // (one square; the empty border is 4 squares on every side)
        double FrameRadius => Round ? 2.4 * Unit : 0;
        double BorderWidth => Unit * BorderLevel switch { 1 => 0.45, 2 => 0.9, 3 => 1.5, _ => 0 };

        /// <summary>The picture's place (a square the picture fits in, in the middle of the code).</summary>
        Rect LogoSquare()
        {
            int n = Modules?.GetLength(0) ?? 21;
            double side = n * Unit * (LogoSize == 0 ? 0.18 : LogoSize == 1 ? 0.24 : 0.30);
            return new Rect(Box.X + Box.Width / 2 - side / 2, Box.Y + Box.Height / 2 - side / 2, side, side);
        }

        Rect LogoRect()
        {
            var sq = LogoSquare();
            if (Logo == null) return sq;
            double aspect = Logo.Pixels.PixelWidth / (double)Math.Max(1, Logo.Pixels.PixelHeight);
            double w = aspect >= 1 ? sq.Width : sq.Width * aspect, h = aspect >= 1 ? sq.Height / aspect : sq.Height;
            return new Rect(sq.X + (sq.Width - w) / 2, sq.Y + (sq.Height - h) / 2, w, h);
        }

        /// <summary>The white (or see-through) pad round the picture: no squares are drawn under it.</summary>
        Rect LogoPad() { var r = LogoSquare(); r.Inflate(Unit * 0.7, Unit * 0.7); return r; }

        /// <summary>The dark parts as figures (squares of a row joined, or round dots and round eyes), in points of the page as shown.</summary>
        public List<PdfFigure> DarkFigures()
        {
            var figures = new List<PdfFigure>();
            var source = Modules;
            if (source == null) return figures;
            int n = source.GetLength(0);
            double u = Unit, left = Box.X + 4 * u, top = Box.Y + 4 * u;
            var m = (bool[,])source.Clone();
            if (Logo != null)
            {
                var pad = LogoPad();
                for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
                    if (m[y, x] && new Rect(left + x * u, top + y * u, u, u).IntersectsWith(pad)) m[y, x] = false;
            }
            bool Eye(int x, int y) => (x < 7 && y < 7) || (x >= n - 7 && y < 7) || (x < 7 && y >= n - 7);
            if (!Round)
            {
                for (int y = 0; y < n; y++)
                {
                    int x = 0;
                    while (x < n)
                    {
                        if (!m[y, x]) { x++; continue; }
                        int start = x;
                        while (x < n && m[y, x]) x++;
                        figures.Add(RectFigure(new Rect(left + start * u, top + y * u, (x - start) * u, u)));
                    }
                }
                return figures;
            }
            // round: dots for the data, round blocks in the middle of the eyes (the eyes' rings are strokes: see EyeRings)
            foreach (var (ex, ey) in new[] { (0, 0), (n - 7, 0), (0, n - 7) })
                figures.Add(RoundRect(new Rect(left + (ex + 2) * u, top + (ey + 2) * u, 3 * u, 3 * u), 1.0 * u, false));
            double gap = u * 0.04;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    if (m[y, x] && !Eye(x, y)) figures.Add(Ellipse(new Rect(left + x * u + gap, top + y * u + gap, u - 2 * gap, u - 2 * gap)));
            return figures;
        }

        /// <summary>The rings of the three eyes of a round code (drawn as lines one square thick: a shape with a hole can not be one figure).</summary>
        List<PdfFigure> EyeRings()
        {
            var list = new List<PdfFigure>();
            int n = Modules?.GetLength(0) ?? 21;
            double u = Unit, left = Box.X + 4 * u, top = Box.Y + 4 * u;
            if (!Round) return list;
            foreach (var (ex, ey) in new[] { (0, 0), (n - 7, 0), (0, n - 7) })
                list.Add(RoundRect(new Rect(left + ex * u + 0.5 * u, top + ey * u + 0.5 * u, 6 * u, 6 * u), 1.8 * u, false));
            return list;
        }

        static PdfFigure RectFigure(Rect r) => new(r.TopLeft, new[] { PdfSegment.Line(r.TopRight), PdfSegment.Line(r.BottomRight), PdfSegment.Line(r.BottomLeft) }, true);

        /// <summary>A rectangle with round corners; <paramref name="reverse"/> draws it the other way round (a hole in a shape).</summary>
        static PdfFigure RoundRect(Rect r, double radius, bool reverse)
        {
            radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
            if (radius < 0.01) return RectFigure(r);
            double k = 0.5523 * radius;
            Point P(double x, double y) => reverse ? new Point(2 * (r.X + r.Width / 2) - x, y) : new Point(x, y);          // (mirrored: the same shape, the other way round)
            var segments = new List<PdfSegment>
            {
                PdfSegment.Line(P(r.Right - radius, r.Top)),
                PdfSegment.Bezier(P(r.Right - radius + k, r.Top), P(r.Right, r.Top + radius - k), P(r.Right, r.Top + radius)),
                PdfSegment.Line(P(r.Right, r.Bottom - radius)),
                PdfSegment.Bezier(P(r.Right, r.Bottom - radius + k), P(r.Right - radius + k, r.Bottom), P(r.Right - radius, r.Bottom)),
                PdfSegment.Line(P(r.Left + radius, r.Bottom)),
                PdfSegment.Bezier(P(r.Left + radius - k, r.Bottom), P(r.Left, r.Bottom - radius + k), P(r.Left, r.Bottom - radius)),
                PdfSegment.Line(P(r.Left, r.Top + radius)),
                PdfSegment.Bezier(P(r.Left, r.Top + radius - k), P(r.Left + radius - k, r.Top), P(r.Left + radius, r.Top)),
            };
            return new PdfFigure(P(r.Left + radius, r.Top), segments, true);
        }

        PdfFigure FrameFigure() => RoundRect(Box, FrameRadius, false);
        PdfFigure BorderFigure() { var r = Box; double inset = BorderWidth / 2 + Unit * 0.3; r.Inflate(-inset, -inset); return RoundRect(r, Math.Max(0, FrameRadius - inset), false); }

        public override FrameworkElement Build()
        {
            var canvas = new Canvas();
            if (Back is Color back)
                canvas.Children.Add(new System.Windows.Shapes.Path { Data = Geometry(new[] { FrameFigure() }), Fill = new SolidColorBrush(back) });
            canvas.Children.Add(new System.Windows.Shapes.Path { Data = Geometry(DarkFigures()), Fill = new SolidColorBrush(Color) });
            if (Round) canvas.Children.Add(new System.Windows.Shapes.Path { Data = Geometry(EyeRings()), Stroke = new SolidColorBrush(Color), StrokeThickness = Unit, StrokeLineJoin = PenLineJoin.Round });
            if (BorderLevel > 0)
                canvas.Children.Add(new System.Windows.Shapes.Path { Data = Geometry(new[] { BorderFigure() }), Stroke = new SolidColorBrush(Color), StrokeThickness = BorderWidth });
            if (Logo != null)
            {
                if (Back is Color padColour)
                    canvas.Children.Add(new System.Windows.Shapes.Path { Data = Geometry(new[] { RoundRect(LogoPad(), Round ? Unit * 1.4 : 0, false) }), Fill = new SolidColorBrush(padColour) });
                var r = LogoRect();
                var image = new Image { Source = Logo.Pixels, Width = r.Width, Height = r.Height, Stretch = Stretch.Fill };
                System.Windows.Media.RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                Canvas.SetLeft(image, r.X); Canvas.SetTop(image, r.Y);
                canvas.Children.Add(image);
            }
            return canvas;
        }

        public override IEnumerable<PdfMark> Marks()
        {
            if (Back is Color back)
                yield return new PdfPathMark(Page, new[] { FrameFigure() }, null, 0, back, false);
            var dark = DarkFigures();
            if (dark.Count > 0) yield return new PdfPathMark(Page, dark, null, 0, Color, false);
            if (Round) yield return new PdfPathMark(Page, EyeRings(), Color, Unit, null, false);
            if (BorderLevel > 0) yield return new PdfPathMark(Page, new[] { BorderFigure() }, Color, BorderWidth, null, false);
            if (Logo != null)
            {
                if (Back is Color padColour)
                    yield return new PdfPathMark(Page, new[] { RoundRect(LogoPad(), Round ? Unit * 1.4 : 0, false) }, null, 0, padColour, false);
                yield return new PdfImageMark(Page, LogoRect(), Logo.Jpeg, Logo.Jpeg == null ? Logo.Pixels : null);
            }
        }
    }

    private void AddQr()
    {
        if (_pdf == null || _pages.Count == 0) return;
        if (!_editing) { EnterEditing(); if (!_editing) return; }
        var (pv, at) = VisibleSpot();
        var item = new QrItem { Page = pv.Index, Color = Colors.Black };
        if (!QrDialog(item, isNew: true)) return;
        double size = 110;
        item.Box = new Rect(Math.Clamp(at.X - size / 2, 0, Math.Max(0, pv.Overlay.Width - size)), Math.Clamp(at.Y - size / 2, 0, Math.Max(0, pv.Overlay.Height - size)), size, size);
        Add(item, select: true);
        Toast("Drag it into place; the corner resizes it; double-click it to change it");
    }

    private void EditQr(QrItem item)
    {
        var copy = (QrItem)item.Clone();
        if (!QrDialog(copy, isNew: false)) return;
        Snapshot();
        item.Payload = copy.Payload; item.Kind = copy.Kind; item.Fields = copy.Fields; item.Level = copy.Level; item.Back = copy.Back; item.Color = copy.Color;
        item.Round = copy.Round; item.BorderLevel = copy.BorderLevel; item.Logo = copy.Logo; item.LogoSize = copy.LogoSize; item.LogoName = copy.LogoName;
        RenderItems(item.Page);
        UpdateEditButtons();
    }

    private static string WifiEscape(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace(":", "\\:").Replace("\"", "\\\"");

    /// <summary>The QR maker's window. True when OK was pressed (the item then holds what was made).</summary>
    private bool QrDialog(QrItem item, bool isNew)
    {
        var dlg = MakeDialog(isNew ? "QR maker" : "Change the QR code", 600);
        var chip = DialogStyle("ChipButton");
        TextBlock Label(string t, double top = 10) => new() { Text = t, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 0) };
        string Get(string key, string fallback = "") => item.Fields.TryGetValue(key, out var v) ? v : fallback;
        RadioButton Chip(string text, string group, bool on, string id) { var r = new RadioButton { Content = text, Style = chip, GroupName = group, IsChecked = on }; Id(id, r); return r; }

        // what the code is for
        var kinds = new[] { ("link", "Web link"), ("text", "Text"), ("email", "E-mail"), ("phone", "Phone"), ("wifi", "Wi-Fi") };
        var kindChips = new List<(string Key, RadioButton Chip)>();
        var kindRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (key, name) in kinds)
        {
            var c = Chip(name, "qrkind", item.Kind == key, "PdfQrKind" + name.Replace("-", "").Replace(" ", ""));
            kindChips.Add((key, c)); kindRow.Children.Add(c);
        }
        if (!kindChips.Any(k => k.Chip.IsChecked == true)) kindChips[0].Chip.IsChecked = true;

        // the fields (only the ones of the chosen kind are shown)
        var a = Field(Get("a"), "PdfQrA"); var b = Field(Get("b"), "PdfQrB"); var c2 = Field(Get("c"), "PdfQrC");
        c2.AcceptsReturn = true; c2.TextWrapping = TextWrapping.Wrap; c2.MinHeight = 60; c2.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var la = Label("", 10); var lb = Label("", 10); var lc = Label("", 10);
        var wifiSecurity = new ComboBox { Margin = new Thickness(0, 4, 0, 0), ItemsSource = new[] { "WPA / WPA2 (most Wi-Fi)", "WEP", "No password" }, SelectedIndex = Math.Clamp(int.TryParse(Get("sec", "0"), out int sv) ? sv : 0, 0, 2) };
        Id("PdfQrWifiSecurity", wifiSecurity);

        // the look: shape, frame, picture in the middle
        var shapeSquare = Chip("Square", "qrshape", !item.Round, "PdfQrShapeSquare"); var shapeRound = Chip("Round", "qrshape", item.Round, "PdfQrShapeRound");
        var shapeRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) }; shapeRow.Children.Add(shapeSquare); shapeRow.Children.Add(shapeRound);
        var borderChips = new List<RadioButton>();
        var borderRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (name, i) in new[] { ("None", 0), ("Thin", 1), ("Medium", 2), ("Thick", 3) }) { var c = Chip(name, "qrborder", item.BorderLevel == i, "PdfQrBorder" + name); borderChips.Add(c); borderRow.Children.Add(c); }
        PdfPicture? logo = item.Logo; string logoName = item.LogoName;
        var logoLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 };
        var logoPick = new Button { Content = "Choose a picture…", Style = DialogStyle("DialogButton"), Padding = new Thickness(10, 4, 10, 4) }; Id("PdfQrLogoPick", logoPick);
        var logoClear = new Button { Content = "Take it out", Style = DialogStyle("DialogButton"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) }; Id("PdfQrLogoClear", logoClear);
        var logoRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        logoRow.Children.Add(logoPick); logoRow.Children.Add(logoClear); logoRow.Children.Add(logoLabel);
        var logoSizes = new List<RadioButton>();
        var logoSizeRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (name, i) in new[] { ("Small", 0), ("Medium", 1), ("Large", 2) }) { var c = Chip(name, "qrlogosize", item.LogoSize == i, "PdfQrLogoSize" + name); logoSizes.Add(c); logoSizeRow.Children.Add(c); }
        var logoNote = new TextBlock { Text = "A picture hides some of the squares, so the code is made with a higher safety level. Test it with your phone camera.", Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

        var safety = new ComboBox { Margin = new Thickness(0, 4, 0, 0), ItemsSource = new[] { "Low (smallest code)", "Medium (usual)", "Quartile", "High (still reads when dirty or torn)" }, SelectedIndex = Math.Clamp(item.Level, 0, 3) };
        Id("PdfQrSafety", safety);
        var whiteBack = new CheckBox { Content = "White square behind it (turn off for a see-through code)", IsChecked = item.Back != null, Margin = new Thickness(0, 10, 0, 0) };
        Id("PdfQrWhite", whiteBack);
        var colour = new Button { Content = "Colour of the squares…", Style = DialogStyle("DialogButton"), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4) };
        var swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(item.Color), Margin = new Thickness(8, 8, 0, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        Id("PdfQrColour", colour);
        Color dark = item.Color;

        var preview = new Border { Width = 170, Height = 170, Background = Brushes.White, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(16, 4, 0, 0), VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true };
        var problem = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), FontSize = 12 };
        var ok = new Button { Content = isNew ? "Add to the page" : "OK", Style = DialogStyle("DialogPrimary"), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) }; Id("PdfQrOk", ok);
        var cancel = new Button { Content = "Cancel", Style = DialogStyle("DialogButton"), IsCancel = true };

        string KindNow() => kindChips.First(k => k.Chip.IsChecked == true).Key;
        string Payload()
        {
            string x = a.Text.Trim(), y = b.Text.Trim(), z = c2.Text.Trim();
            switch (KindNow())
            {
                case "link":
                    if (x.Length == 0) return "";
                    return x.Contains("://") || x.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? x : "https://" + x;
                case "text": return z;
                case "email":
                    if (x.Length == 0) return "";
                    string q = "";
                    if (y.Length > 0) q += "subject=" + Uri.EscapeDataString(y);
                    if (z.Length > 0) q += (q.Length > 0 ? "&" : "") + "body=" + Uri.EscapeDataString(z);
                    return "mailto:" + x + (q.Length > 0 ? "?" + q : "");
                case "phone": return x.Length == 0 ? "" : "tel:" + x.Replace(" ", "");
                default:
                    if (x.Length == 0) return "";
                    int sec = wifiSecurity.SelectedIndex;
                    return $"WIFI:T:{(sec == 0 ? "WPA" : sec == 1 ? "WEP" : "nopass")};S:{WifiEscape(x)};{(sec == 2 ? "" : "P:" + WifiEscape(y) + ";")};";
            }
        }
        QrItem Made(Rect box) => new()
        {
            Page = item.Page, Box = box, Payload = Payload(), Level = safety.SelectedIndex, Color = dark, Back = whiteBack.IsChecked == true ? Colors.White : null,
            Round = shapeRound.IsChecked == true, BorderLevel = Math.Max(0, borderChips.FindIndex(c => c.IsChecked == true)), Logo = logo, LogoName = logoName, LogoSize = Math.Max(0, logoSizes.FindIndex(c => c.IsChecked == true)),
        };
        void Layout()
        {
            string k = KindNow();
            la.Text = k switch { "link" => "Web address", "text" => "", "email" => "E-mail address", "phone" => "Phone number (with the country code, like +63…)", _ => "Network name (Wi-Fi name)" };
            lb.Text = k switch { "email" => "Subject (optional)", "wifi" => "Password", _ => "" };
            lc.Text = k switch { "text" => "Text", "email" => "Message (optional)", _ => "" };
            la.Visibility = a.Visibility = k == "text" ? Visibility.Collapsed : Visibility.Visible;
            lb.Visibility = b.Visibility = k is "email" or "wifi" ? Visibility.Visible : Visibility.Collapsed;
            lc.Visibility = c2.Visibility = k is "text" or "email" ? Visibility.Visible : Visibility.Collapsed;
            wifiSecurity.Visibility = k == "wifi" ? Visibility.Visible : Visibility.Collapsed;
            b.IsEnabled = k != "wifi" || wifiSecurity.SelectedIndex != 2;
            logoLabel.Text = logo == null ? "none" : logoName;
            logoSizeRow.IsEnabled = logo != null; logoClear.IsEnabled = logo != null;
        }
        void Redraw()
        {
            string payload = Payload();
            var sample = Made(new Rect(0, 0, 170, 170));
            bool fine = payload.Length > 0 && sample.Modules != null;
            problem.Text = payload.Length == 0 ? "" : fine ? "" : "That is too much for one QR code. Make it shorter.";
            ok.IsEnabled = fine;
            preview.Background = whiteBack.IsChecked == true ? new SolidColorBrush(Color.FromRgb(0xE9, 0xEC, 0xF2)) : new SolidColorBrush(Color.FromRgb(0xD8, 0xDB, 0xE2));
            preview.Child = fine ? sample.Build() : null;
            logoNote.Visibility = logo != null ? Visibility.Visible : Visibility.Collapsed;
        }
        colour.Click += (_, _) => { if (PickCustomColour(dark) is Color picked) { dark = picked; swatch.Background = new SolidColorBrush(picked); Redraw(); } };
        logoPick.Click += (_, _) =>
        {
            var open = new Microsoft.Win32.OpenFileDialog { Title = "A picture for the middle of the code", Filter = "Pictures|" + string.Join(";", PdfCombiner.PictureExtensions.Select(e => "*." + e)) + "|All files|*.*", CheckFileExists = true };
            if (open.ShowDialog(dlg) != true) return;
            try { logo = PdfCombiner.LoadPicture(File.ReadAllBytes(open.FileName), 600); logoName = System.IO.Path.GetFileName(open.FileName); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or System.Runtime.InteropServices.COMException or InvalidOperationException)
            { problem.Text = "Couldn't read that picture: " + e.Message; return; }
            Layout(); Redraw();
        };
        logoClear.Click += (_, _) => { logo = null; logoName = ""; Layout(); Redraw(); };
        foreach (var (_, ch) in kindChips) ch.Checked += (_, _) => { Layout(); Redraw(); };
        foreach (var box in new[] { a, b, c2 }) box.TextChanged += (_, _) => Redraw();
        wifiSecurity.SelectionChanged += (_, _) => { Layout(); Redraw(); };
        safety.SelectionChanged += (_, _) => Redraw();
        whiteBack.Checked += (_, _) => Redraw(); whiteBack.Unchecked += (_, _) => Redraw();
        foreach (var ch in new[] { shapeSquare, shapeRound }.Concat(borderChips).Concat(logoSizes)) ch.Checked += (_, _) => Redraw();

        bool accepted = false;
        ok.Click += (_, _) =>
        {
            var made = Made(new Rect(0, 0, 110, 110));
            if (made.Payload.Length == 0) { problem.Text = "Type what the code should say first."; return; }
            item.Kind = KindNow(); item.Payload = made.Payload; item.Level = made.Level; item.Color = dark; item.Back = made.Back;
            item.Round = made.Round; item.BorderLevel = made.BorderLevel; item.Logo = logo; item.LogoName = logoName; item.LogoSize = made.LogoSize;
            item.Fields = new Dictionary<string, string> { ["a"] = a.Text, ["b"] = b.Text, ["c"] = c2.Text, ["sec"] = wifiSecurity.SelectedIndex.ToString() };
            accepted = true; dlg.Close();
        };

        var left = new StackPanel();
        left.Children.Add(Label("What is it for?", 0)); left.Children.Add(kindRow);
        left.Children.Add(la); left.Children.Add(a);
        left.Children.Add(lb); left.Children.Add(b); left.Children.Add(wifiSecurity);
        left.Children.Add(lc); left.Children.Add(c2);
        var top = new DockPanel();
        DockPanel.SetDock(preview, Dock.Right);
        top.Children.Add(preview); top.Children.Add(left);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(top);
        var look = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        look.ColumnDefinitions.Add(new ColumnDefinition()); look.ColumnDefinitions.Add(new ColumnDefinition());
        var lookLeft = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; lookLeft.Children.Add(Label("Shape (dots, eyes and corners)")); lookLeft.Children.Add(shapeRow);
        var lookRight = new StackPanel(); lookRight.Children.Add(Label("Border")); lookRight.Children.Add(borderRow);
        Grid.SetColumn(lookRight, 1); look.Children.Add(lookLeft); look.Children.Add(lookRight);
        root.Children.Add(look);
        root.Children.Add(Label("Picture in the middle (a logo)")); root.Children.Add(logoRow); root.Children.Add(logoSizeRow); root.Children.Add(logoNote);
        root.Children.Add(Label("Safety (how much damage it can take)")); root.Children.Add(safety);
        root.Children.Add(whiteBack);
        var colourRow = new StackPanel { Orientation = Orientation.Horizontal }; colourRow.Children.Add(colour); colourRow.Children.Add(swatch);
        root.Children.Add(colourRow);
        root.Children.Add(problem);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        dlg.Content = root;
        Layout(); Redraw();
        dlg.Loaded += (_, _) => { (KindNow() == "text" ? c2 : a).Focus(); };
        dlg.ShowDialog();
        return accepted;
    }
}
