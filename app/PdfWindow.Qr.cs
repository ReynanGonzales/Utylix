using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using IdmClone.Engine;
using QRCoder;

namespace IdmClone;

/// <summary>
/// QR maker (Add tab > QR): a QR code for a web link, some text, an e-mail, a phone number or a Wi-Fi network, made on this PC and put on the page as sharp squares (not a picture), so it stays crisp
/// at any size and in any PDF reader. Move it, resize it (it stays square), or double-click it to change what it says.
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
        private bool[,]? _modules; private string _key = "";

        public override bool KeepAspect => true;
        public override Rect Bounds => Box;
        public override EditItem Clone() { var c = (QrItem)MemberwiseClone(); c.Fields = new Dictionary<string, string>(Fields); return c; }
        public override void MoveBy(Vector d) => Box.Offset(d);
        public override void ResizeTo(Rect r) { double s = Math.Max(16, Math.Min(r.Width, r.Height)); Box = new Rect(r.X, r.Y, s, s); }

        public bool[,]? Modules
        {
            get
            {
                string key = Payload + "|" + Level;
                if (_modules == null || key != _key) { _modules = QrModules(Payload, (QRCodeGenerator.ECCLevel)Level, out _); _key = key; }
                return _modules;
            }
        }

        /// <summary>The dark squares as rectangles (a row's neighbours joined), in points of the page as shown.</summary>
        public List<Rect> Squares()
        {
            var list = new List<Rect>();
            var m = Modules;
            if (m == null) return list;
            int n = m.GetLength(0);
            double unit = Box.Width / (n + 8);                                // (the border is 4 squares on every side)
            for (int y = 0; y < n; y++)
            {
                int x = 0;
                while (x < n)
                {
                    if (!m[y, x]) { x++; continue; }
                    int start = x;
                    while (x < n && m[y, x]) x++;
                    list.Add(new Rect(Box.X + (start + 4) * unit, Box.Y + (y + 4) * unit, (x - start) * unit, unit));
                }
            }
            return list;
        }

        public override FrameworkElement Build()
        {
            var canvas = new Canvas();
            if (Back is Color back)
            {
                var bg = new Rectangle { Width = Box.Width, Height = Box.Height, Fill = new SolidColorBrush(back) };
                Canvas.SetLeft(bg, Box.X); Canvas.SetTop(bg, Box.Y);
                canvas.Children.Add(bg);
            }
            var geometry = new StreamGeometry();
            using (var c = geometry.Open())
                foreach (var r in Squares())
                {
                    // (a hair wider than the square, so no light lines show between the squares at any zoom)
                    c.BeginFigure(new Point(r.Left, r.Top), true, true);
                    c.LineTo(new Point(r.Right + 0.02, r.Top), false, false); c.LineTo(new Point(r.Right + 0.02, r.Bottom + 0.02), false, false); c.LineTo(new Point(r.Left, r.Bottom + 0.02), false, false);
                }
            geometry.Freeze();
            canvas.Children.Add(new System.Windows.Shapes.Path { Data = geometry, Fill = new SolidColorBrush(Color) });
            return canvas;
        }

        public override IEnumerable<PdfMark> Marks()
        {
            if (Back is Color back)
                yield return new PdfPathMark(Page, new[] { Rectangle(Box) }, null, 0, back, false);
            var squares = Squares();
            if (squares.Count > 0)
                yield return new PdfPathMark(Page, squares.Select(Rectangle).ToList(), null, 0, Color, false);
        }

        private static PdfFigure Rectangle(Rect r) =>
            new(r.TopLeft, new[] { PdfSegment.Line(r.TopRight), PdfSegment.Line(r.BottomRight), PdfSegment.Line(r.BottomLeft) }, true);
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
        Toast("Drag it into place; the corner resizes it; double-click it to change what it says");
    }

    private void EditQr(QrItem item)
    {
        var copy = (QrItem)item.Clone();
        if (!QrDialog(copy, isNew: false)) return;
        Snapshot();
        item.Payload = copy.Payload; item.Kind = copy.Kind; item.Fields = copy.Fields; item.Level = copy.Level; item.Back = copy.Back; item.Color = copy.Color;
        RenderItems(item.Page);
        UpdateEditButtons();
    }

    private static string WifiEscape(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace(":", "\\:").Replace("\"", "\\\"");

    /// <summary>The QR maker's window. True when OK was pressed (the item then holds what was made).</summary>
    private bool QrDialog(QrItem item, bool isNew)
    {
        var dlg = MakeDialog(isNew ? "QR maker" : "Change the QR code", 560);
        var chip = DialogStyle("ChipButton");
        TextBlock Label(string t, double top = 10) => new() { Text = t, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 0) };
        string Get(string key, string fallback = "") => item.Fields.TryGetValue(key, out var v) ? v : fallback;

        // what the code is for
        var kinds = new[] { ("link", "Web link"), ("text", "Text"), ("email", "E-mail"), ("phone", "Phone"), ("wifi", "Wi-Fi") };
        var kindChips = new List<(string Key, RadioButton Chip)>();
        var kindRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (key, name) in kinds)
        {
            var c = new RadioButton { Content = name, Style = chip, GroupName = "qrkind", IsChecked = item.Kind == key };
            Id("PdfQrKind" + name.Replace("-", "").Replace(" ", ""), c);
            kindChips.Add((key, c)); kindRow.Children.Add(c);
        }
        if (!kindChips.Any(k => k.Chip.IsChecked == true)) kindChips[0].Chip.IsChecked = true;

        // the fields (only the ones of the chosen kind are shown)
        var a = Field(Get("a"), "PdfQrA"); var b = Field(Get("b"), "PdfQrB"); var c2 = Field(Get("c"), "PdfQrC");
        c2.AcceptsReturn = true; c2.TextWrapping = TextWrapping.Wrap; c2.MinHeight = 60; c2.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var la = Label("", 10); var lb = Label("", 10); var lc = Label("", 10);
        var wifiSecurity = new ComboBox { Margin = new Thickness(0, 4, 0, 0), ItemsSource = new[] { "WPA / WPA2 (most Wi-Fi)", "WEP", "No password" }, SelectedIndex = Math.Clamp(int.TryParse(Get("sec", "0"), out int sv) ? sv : 0, 0, 2) };
        Id("PdfQrWifiSecurity", wifiSecurity);

        // the look
        var safety = new ComboBox { Margin = new Thickness(0, 4, 0, 0), ItemsSource = new[] { "Low (smallest code)", "Medium (usual)", "Quartile", "High (still reads when dirty or torn)" }, SelectedIndex = Math.Clamp(item.Level, 0, 3) };
        Id("PdfQrSafety", safety);
        var whiteBack = new CheckBox { Content = "White square behind it (turn off for a see-through code)", IsChecked = item.Back != null, Margin = new Thickness(0, 10, 0, 0) };
        Id("PdfQrWhite", whiteBack);
        var colour = new Button { Content = "Colour of the squares…", Style = DialogStyle("DialogButton"), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4) };
        var swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(item.Color), Margin = new Thickness(8, 8, 0, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        Id("PdfQrColour", colour);
        Color dark = item.Color;

        var preview = new Border { Width = 150, Height = 150, Background = Brushes.White, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(16, 4, 0, 0), VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true };
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
            if (k == "wifi") b.IsEnabled = wifiSecurity.SelectedIndex != 2; else b.IsEnabled = true;
        }
        void Redraw()
        {
            string payload = Payload();
            bool[,]? modules = payload.Length == 0 ? null : QrModules(payload, (QRCodeGenerator.ECCLevel)safety.SelectedIndex, out string? why);
            problem.Text = payload.Length == 0 ? "" : modules == null ? "That is too much for one QR code. Make it shorter." : "";
            ok.IsEnabled = modules != null;
            var canvas = new Canvas { Width = 150, Height = 150 };
            if (modules != null)
            {
                int n = modules.GetLength(0);
                double unit = 150.0 / (n + 8);
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                    for (int yy = 0; yy < n; yy++) for (int xx = 0; xx < n; xx++)
                        if (modules[yy, xx])
                        {
                            ctx.BeginFigure(new Point((xx + 4) * unit, (yy + 4) * unit), true, true);
                            ctx.LineTo(new Point((xx + 5) * unit, (yy + 4) * unit), false, false); ctx.LineTo(new Point((xx + 5) * unit, (yy + 5) * unit), false, false); ctx.LineTo(new Point((xx + 4) * unit, (yy + 5) * unit), false, false);
                        }
                g.Freeze();
                canvas.Children.Add(new System.Windows.Shapes.Path { Data = g, Fill = new SolidColorBrush(dark) });
            }
            preview.Background = whiteBack.IsChecked == true ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xD8, 0xDB, 0xE2));
            preview.Child = canvas;
        }
        colour.Click += (_, _) => { if (PickCustomColour(dark) is Color picked) { dark = picked; swatch.Background = new SolidColorBrush(picked); Redraw(); } };
        foreach (var (_, ch) in kindChips) ch.Checked += (_, _) => { Layout(); Redraw(); };
        foreach (var box in new[] { a, b, c2 }) box.TextChanged += (_, _) => Redraw();
        wifiSecurity.SelectionChanged += (_, _) => { Layout(); Redraw(); };
        safety.SelectionChanged += (_, _) => Redraw();
        whiteBack.Checked += (_, _) => Redraw(); whiteBack.Unchecked += (_, _) => Redraw();

        bool accepted = false;
        ok.Click += (_, _) =>
        {
            string payload = Payload();
            if (payload.Length == 0) { problem.Text = "Type what the code should say first."; return; }
            item.Kind = KindNow(); item.Payload = payload; item.Level = safety.SelectedIndex; item.Color = dark; item.Back = whiteBack.IsChecked == true ? Colors.White : null;
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
