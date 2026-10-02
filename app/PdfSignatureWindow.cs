using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IdmClone;

/// <summary>A signature to put on a page: pen strokes (0..1 inside its box) or a picture with a see-through background.</summary>
public sealed class SignatureChoice
{
    public List<List<Point>>? Strokes;
    public double Aspect = 3;             // width / height
    public double WidthRatio = 0.012;     // pen width / box width
    public Color Color = Color.FromRgb(0x10, 0x2A, 0x8C);
    public BitmapSource? Picture;
}

/// <summary>
/// "Your signature": draw it (mouse, touch or pen), type it in a handwriting font, or take a photo / scan of it on paper (the paper is
/// made see-through). Signatures are remembered on this PC (Utylix's data folder) to use again with one click.
/// </summary>
public sealed class PdfSignatureWindow : Window
{
    public SignatureChoice? Chosen { get; private set; }

    private static string Folder => Path.Combine(App.DataDir, "signatures");

    private readonly InkCanvas _ink = new() { Background = Brushes.White, Height = 190, Cursor = Cursors.Pen };
    private readonly TextBox _typed = new();
    private readonly ComboBox _typeFont = new() { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _typedPreview = new() { FontSize = 54, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Image _picturePreview = new() { Stretch = Stretch.Uniform, MaxHeight = 170 };
    private BitmapSource? _picture;
    private readonly CheckBox _remember = new() { Content = "Remember it on this PC", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly WrapPanel _saved = new();
    private readonly RadioButton _draw = new() { Content = "Draw", GroupName = "sigmode", IsChecked = true };
    private readonly RadioButton _type = new() { Content = "Type", GroupName = "sigmode" };
    private readonly RadioButton _photo = new() { Content = "From a picture", GroupName = "sigmode" };
    private readonly Grid _pages = new();
    private Color _color = Color.FromRgb(0x10, 0x2A, 0x8C);

    public PdfSignatureWindow()
    {
        Title = "Your signature - Utylix Editor";
        Width = 640; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13.5;
        WindowTheme.DarkTitleBar(this);
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/pdf.ico")); } catch (Exception e) when (e is IOException or UriFormatException) { }

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        root.Children.Add(new TextBlock { Text = "Your signature", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });

        // ---- remembered ones ----
        var savedTitle = new TextBlock { Text = "Use a saved one", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
        root.Children.Add(savedTitle);
        root.Children.Add(_saved);
        LoadSaved();
        savedTitle.Visibility = _saved.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // ---- a new one ----
        root.Children.Add(new TextBlock { Text = "Or make a new one", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, _saved.Children.Count > 0 ? 12 : 0, 0, 6) });
        var modes = new WrapPanel();
        foreach (var r in new[] { _draw, _type, _photo }) { r.Style = (Style)Application.Current.FindResource("ChipButton"); modes.Children.Add(r); }
        var colors = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (c, name) in new[] { (Color.FromRgb(0x10, 0x2A, 0x8C), "Blue ink"), (Colors.Black, "Black ink") })
        {
            var b = new RadioButton { Content = name, GroupName = "sigcolor", Style = (Style)Application.Current.FindResource("ChipButton"), IsChecked = c == _color };
            b.Checked += (_, _) => { _color = c; _ink.DefaultDrawingAttributes.Color = c; foreach (var s in _ink.Strokes) s.DrawingAttributes.Color = c; _typedPreview.Foreground = new SolidColorBrush(c); };
            colors.Children.Add(b);
        }
        var modeRow = new DockPanel();
        DockPanel.SetDock(colors, Dock.Right);
        modeRow.Children.Add(colors);
        modeRow.Children.Add(modes);
        root.Children.Add(modeRow);

        // draw
        _ink.DefaultDrawingAttributes = new DrawingAttributes { Color = _color, Width = 2.6, Height = 2.6, FitToCurve = true, IgnorePressure = false };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_ink, "SigInk");
        var clear = new Button { Content = "Clear", Style = (Style)Application.Current.FindResource("SmallButton"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        clear.Click += (_, _) => _ink.Strokes.Clear();
        var drawPage = new StackPanel();
        drawPage.Children.Add(new Border { Child = _ink, CornerRadius = new CornerRadius(8), BorderBrush = (Brush)Application.Current.FindResource("LineBrush"), BorderThickness = new Thickness(1), ClipToBounds = true });
        var hint = new DockPanel();
        DockPanel.SetDock(clear, Dock.Right);
        hint.Children.Add(clear);
        hint.Children.Add(new TextBlock { Text = "Sign above with the mouse, a pen or your finger.", Opacity = 0.65, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        drawPage.Children.Add(hint);

        // type
        _typed.Style = (Style)Application.Current.FindResource("Field");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_typed, "SigTyped");
        var installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var handwriting = new[] { "Segoe Script", "Ink Free", "Lucida Handwriting", "Brush Script MT", "Segoe Print", "Mistral" }.Where(installed.Contains).ToList();
        if (handwriting.Count == 0) handwriting.Add("Segoe UI");
        _typeFont.ItemsSource = handwriting;
        _typeFont.SelectedIndex = 0;
        _typeFont.SelectionChanged += (_, _) => UpdateTyped();
        _typed.TextChanged += (_, _) => UpdateTyped();
        _typedPreview.Foreground = new SolidColorBrush(_color);
        var typePage = new StackPanel { Visibility = Visibility.Collapsed };
        var typeRow = new DockPanel();
        DockPanel.SetDock(_typeFont, Dock.Right);
        _typeFont.Margin = new Thickness(8, 0, 0, 0);
        typeRow.Children.Add(_typeFont);
        typeRow.Children.Add(_typed);
        typePage.Children.Add(typeRow);
        typePage.Children.Add(new Border { Height = 150, Background = Brushes.White, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 8, 0, 0), Child = _typedPreview, Padding = new Thickness(12, 0, 12, 0) });

        // picture
        var choose = new Button { Content = "Choose a photo or scan of your signature…", Style = (Style)Application.Current.FindResource("DialogButton"), HorizontalAlignment = HorizontalAlignment.Left };
        choose.Click += (_, _) => ChoosePicture();
        var photoPage = new StackPanel { Visibility = Visibility.Collapsed };
        photoPage.Children.Add(choose);
        photoPage.Children.Add(new TextBlock { Text = "Sign on white paper; the paper is made see-through.", Opacity = 0.65, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
        photoPage.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 8, 0, 0), MinHeight = 120, Child = _picturePreview, Padding = new Thickness(8) });

        _pages.Margin = new Thickness(0, 8, 0, 0);
        foreach (var p in new UIElement[] { drawPage, typePage, photoPage }) _pages.Children.Add(p);
        _draw.Checked += (_, _) => Show(drawPage); _type.Checked += (_, _) => Show(typePage); _photo.Checked += (_, _) => Show(photoPage);
        void Show(UIElement page) { foreach (UIElement p in _pages.Children) p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed; }
        root.Children.Add(_pages);

        // ---- buttons ----
        var use = new Button { Content = "Use this signature", Style = (Style)Application.Current.FindResource("DialogPrimary"), Margin = new Thickness(0, 0, 10, 0) };
        var cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.FindResource("DialogButton") };
        System.Windows.Automation.AutomationProperties.SetAutomationId(use, "SigUse");
        use.Click += (_, _) => UseNew();
        cancel.Click += (_, _) => Close();
        var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(use); right.Children.Add(cancel);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        _remember.Foreground = Foreground;
        buttons.Children.Add(_remember);
        root.Children.Add(buttons);
        Content = root;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void UpdateTyped()
    {
        _typedPreview.Text = _typed.Text;
        if (_typeFont.SelectedItem is string f) _typedPreview.FontFamily = new FontFamily(f);
    }

    // ---------- making the choice ----------
    private void UseNew()
    {
        SignatureChoice? choice = null;
        if (_draw.IsChecked == true)
        {
            if (_ink.Strokes.Count == 0) { UMessage.Show(this, "Sign in the white box first.", Title); return; }
            choice = FromInk();
        }
        else if (_type.IsChecked == true)
        {
            if (_typed.Text.Trim().Length == 0) { UMessage.Show(this, "Type your name first.", Title); return; }
            choice = new SignatureChoice { Picture = Crop(RenderTyped()), Color = _color };
        }
        else
        {
            if (_picture == null) { UMessage.Show(this, "Choose a picture of your signature first.", Title); return; }
            choice = new SignatureChoice { Picture = _picture, Color = _color };
        }
        if (choice.Picture != null) choice.Aspect = (double)choice.Picture.PixelWidth / Math.Max(1, choice.Picture.PixelHeight);
        if (_remember.IsChecked == true) Save(choice);
        Chosen = choice;
        DialogResult = true;
    }

    private SignatureChoice FromInk()
    {
        double pen = _ink.DefaultDrawingAttributes.Width;
        var strokes = _ink.Strokes.Select(s => s.StylusPoints.Select(p => new Point(p.X, p.Y)).ToList()).Where(s => s.Count > 0).ToList();
        var all = strokes.SelectMany(s => s).ToList();
        var box = new Rect(all[0], all[0]);
        foreach (var p in all) box.Union(p);
        box.Inflate(pen / 2, pen / 2);
        return new SignatureChoice
        {
            Strokes = strokes.Select(s => s.Select(p => new Point((p.X - box.X) / box.Width, (p.Y - box.Y) / box.Height)).ToList()).ToList(),
            Aspect = box.Width / box.Height, WidthRatio = pen / box.Width, Color = _color,
        };
    }

    private BitmapSource RenderTyped()
    {
        var face = new Typeface(_typedPreview.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var text = new FormattedText(_typed.Text.Trim(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 160, new SolidColorBrush(_color), 1.0);
        var dv = new DrawingVisual();
        using (var g = dv.RenderOpen()) g.DrawText(text, new Point(20, 20));
        var bmp = new RenderTargetBitmap((int)text.WidthIncludingTrailingWhitespace + 60, (int)text.Height + 60, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        return new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
    }

    private void ChoosePicture()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "A photo or scan of your signature", Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var src = BitmapFrame.Create(new Uri(dlg.FileName), BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
            BitmapSource s = src;
            double big = Math.Max(s.PixelWidth, s.PixelHeight) / 1600.0;
            if (big > 1) s = new TransformedBitmap(s, new ScaleTransform(1 / big, 1 / big));
            _picture = Crop(PaperToClear(s));
            _picturePreview.Source = _picture;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            UMessage.Show(this, "Couldn't read that picture: " + e.Message, Title);
        }
    }

    /// <summary>Light paper becomes see-through, the ink stays (a little darker, so a grey photo still looks like ink).</summary>
    private static BitmapSource PaperToClear(BitmapSource s)
    {
        var b = new FormatConvertedBitmap(s, PixelFormats.Bgra32, null, 0);
        int w = b.PixelWidth, h = b.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        b.CopyPixels(px, stride, 0);
        // the paper's brightness: the brightest tenth of the picture
        var lum = new int[256];
        for (int i = 0; i < px.Length; i += 4) lum[(px[i] * 114 + px[i + 1] * 587 + px[i + 2] * 299) / 1000]++;
        int paper = 255, seen = 0;
        for (int l = 255; l >= 0; l--) { seen += lum[l]; if (seen > px.Length / 4 / 10) { paper = l; break; } }
        int ink = Math.Max(0, paper - 110);
        for (int i = 0; i < px.Length; i += 4)
        {
            int l = (px[i] * 114 + px[i + 1] * 587 + px[i + 2] * 299) / 1000;
            int a = Math.Clamp((paper - 12 - l) * 255 / Math.Max(1, paper - 12 - ink), 0, 255);
            px[i + 3] = (byte)a;
            px[i] = (byte)(px[i] * 0.6); px[i + 1] = (byte)(px[i + 1] * 0.6); px[i + 2] = (byte)(px[i + 2] * 0.6);
        }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        result.Freeze();
        return result;
    }

    /// <summary>Cuts away the empty (see-through) border.</summary>
    private static BitmapSource Crop(BitmapSource s)
    {
        var b = s.Format == PixelFormats.Bgra32 ? s : new FormatConvertedBitmap(s, PixelFormats.Bgra32, null, 0);
        int w = b.PixelWidth, h = b.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        b.CopyPixels(px, stride, 0);
        int l = w, t = h, r = -1, bo = -1;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (px[y * stride + x * 4 + 3] > 24) { l = Math.Min(l, x); r = Math.Max(r, x); t = Math.Min(t, y); bo = Math.Max(bo, y); }
        if (r < 0) return b;
        int pad = 4;
        l = Math.Max(0, l - pad); t = Math.Max(0, t - pad); r = Math.Min(w - 1, r + pad); bo = Math.Min(h - 1, bo + pad);
        var c = new CroppedBitmap(b, new Int32Rect(l, t, r - l + 1, bo - t + 1));
        var frozen = new FormatConvertedBitmap(c, PixelFormats.Bgra32, null, 0);
        frozen.Freeze();
        return frozen;
    }

    // ---------- remembered signatures ----------
    private sealed class Stored { public double Aspect { get; set; } public double WidthRatio { get; set; } public string Color { get; set; } = "#102A8C"; public List<double[]> Strokes { get; set; } = new(); }

    private static void Save(SignatureChoice c)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string name = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            if (c.Strokes != null)
            {
                var s = new Stored { Aspect = c.Aspect, WidthRatio = c.WidthRatio, Color = c.Color.ToString(CultureInfo.InvariantCulture), Strokes = c.Strokes.Select(st => st.SelectMany(p => new[] { Math.Round(p.X, 4), Math.Round(p.Y, 4) }).ToArray()).ToList() };
                File.WriteAllText(Path.Combine(Folder, name + ".json"), JsonSerializer.Serialize(s));
            }
            else if (c.Picture != null)
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(c.Picture));
                using var f = File.Create(Path.Combine(Folder, name + ".png"));
                enc.Save(f);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* not remembered, still used */ }
    }

    private static SignatureChoice? Load(string file)
    {
        try
        {
            if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(file));
                if (s == null || s.Strokes.Count == 0) return null;
                return new SignatureChoice
                {
                    Aspect = s.Aspect, WidthRatio = s.WidthRatio, Color = (Color)ColorConverter.ConvertFromString(s.Color),
                    Strokes = s.Strokes.Select(a => Enumerable.Range(0, a.Length / 2).Select(i => new Point(a[i * 2], a[i * 2 + 1])).ToList()).ToList(),
                };
            }
            var img = BitmapFrame.Create(new Uri(file), BitmapCreateOptions.IgnoreImageCache, BitmapCacheOption.OnLoad);
            var pic = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
            pic.Freeze();
            return new SignatureChoice { Picture = pic, Aspect = (double)pic.PixelWidth / Math.Max(1, pic.PixelHeight) };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or NotSupportedException or FormatException or FileFormatException or ArgumentException) { return null; }
    }

    private void LoadSaved()
    {
        _saved.Children.Clear();
        if (!Directory.Exists(Folder)) return;
        foreach (string file in Directory.GetFiles(Folder).Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => f).Take(8))
        {
            var sig = Load(file);
            if (sig == null) continue;
            var preview = Preview(sig, 150, 56);
            var use = new Button { Content = preview, Width = 170, Height = 70, Background = Brushes.White, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(6), ToolTip = "Use this one" };
            use.Click += (_, _) => { Chosen = sig; DialogResult = true; };
            var remove = new Button { Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10 }, Width = 26, Height = 26, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Top, ToolTip = "Forget this signature" };
            string path = file;
            remove.Click += (_, _) => { try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } LoadSaved(); };
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 8) };
            item.Children.Add(use); item.Children.Add(remove);
            _saved.Children.Add(item);
        }
    }

    private static UIElement Preview(SignatureChoice s, double w, double h)
    {
        if (s.Picture != null) return new Image { Source = s.Picture, Stretch = Stretch.Uniform, Width = w, Height = h };
        double k = Math.Min(w / s.Aspect, h);                       // height of the box that fits
        double bw = k * s.Aspect, bh = k;
        var g = new StreamGeometry();
        using (var c = g.Open())
            foreach (var st in s.Strokes!)
            {
                c.BeginFigure(new Point(st[0].X * bw, st[0].Y * bh), false, false);
                foreach (var p in st.Skip(1)) c.LineTo(new Point(p.X * bw, p.Y * bh), true, true);
            }
        return new System.Windows.Shapes.Path { Data = g, Stroke = new SolidColorBrush(s.Color), StrokeThickness = Math.Max(1, s.WidthRatio * bw), StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = bw, Height = bh };
    }
}
