using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>Screen capture like the Snipping Tool (rectangle, window, full screen, delay, pen/highlighter) plus a Text Detector.</summary>
public partial class CapturePage : UserControl
{
    private enum Mode { Rectangle, Window, Full, Text }
    private enum Tool { Pen, Highlighter, Eraser }

    private static readonly (string Name, string Hex)[] Colors =
    {
        ("Red", "#E5484D"), ("Yellow", "#FFD60A"), ("Green", "#30A46C"), ("Blue", "#3E63DD"), ("Black", "#111111"), ("White", "#FFFFFF"),
    };

    private readonly Manager _manager;
    private Mode _mode = Mode.Rectangle;
    private int _delaySeconds;
    private Tool _tool = Tool.Pen;
    private Color _color = (Color)ColorConverter.ConvertFromString("#E5484D");
    private double _size = 4;
    private BitmapSource? _image;
    private bool _busy;
    private string? _language;                 // null = the languages of the Windows profile
    private bool _languagesBuilt;

    public CapturePage(Manager manager)
    {
        InitializeComponent();
        _manager = manager;

        // clicking a mode starts that capture at once, like pressing the capture button
        AddChip(ModeChips, "Rectangle", "mode", () => SetMode(Mode.Rectangle), true, () => _ = CaptureAsync());
        AddChip(ModeChips, "Window", "mode", () => SetMode(Mode.Window), onClicked: () => _ = CaptureAsync());
        AddChip(ModeChips, "Full screen", "mode", () => SetMode(Mode.Full), onClicked: () => _ = CaptureAsync());
        AddChip(ModeChips, "Text Detector", "mode", () => SetMode(Mode.Text), onClicked: () => _ = CaptureAsync());
        foreach (var (label, seconds) in new[] { ("No delay", 0), ("3 s", 3), ("5 s", 5), ("10 s", 10) })
            AddChip(DelayChips, label, "delay", () => _delaySeconds = seconds, seconds == 0);

        AddChip(ToolChips, "Pen", "tool", () => SetTool(Tool.Pen), true);
        AddChip(ToolChips, "Highlighter", "tool", () => SetTool(Tool.Highlighter));
        AddChip(ToolChips, "Eraser", "tool", () => SetTool(Tool.Eraser));
        foreach (var (name, hex) in Colors)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var swatch = new RadioButton { Style = (Style)FindResource("Swatch"), GroupName = "color", Background = new SolidColorBrush(color), ToolTip = name, IsChecked = name == "Red" };
            swatch.Checked += (_, _) => { _color = color; ApplyTool(); };
            Swatches.Children.Add(swatch);
        }
        foreach (var (label, size) in new[] { ("S", 2.5), ("M", 4.0), ("L", 8.0) })
            AddChip(SizeChips, label, "size", () => { _size = size; ApplyTool(); }, size == 4.0);

        SetMode(Mode.Rectangle);
        // Ctrl + S while this tab is in use and there is a picture: "Save as…"
        PreviewKeyDown += (_, e) => { if (e.Key == Key.S && TrySaveAs()) e.Handled = true; };
        IsVisibleChanged += (_, _) => { if (IsVisible) RefreshShortcutHint(); };
    }

    private void AddChip(WrapPanel host, string text, string group, Action onChecked, bool isChecked = false, Action? onClicked = null)
    {
        var chip = new RadioButton { Content = text, GroupName = group, Style = (Style)FindResource("ChipButton"), IsChecked = isChecked };
        chip.Checked += (_, _) => onChecked();
        if (onClicked != null) chip.Click += (_, _) => onClicked();
        host.Children.Add(chip);
    }

    // ---------- mode / shortcuts ----------
    private void SetMode(Mode mode)
    {
        _mode = mode;
        ModeNote.Text = mode switch
        {
            Mode.Rectangle => "Drag over the part of the screen you want.",
            Mode.Window => "Click the window you want.",
            Mode.Full => "Everything on all your screens.",
            _ => "Drag over some text on the screen: Utylix reads it and copies it for you. You can also open or paste a picture.",
        };
        if (NewBtn != null) NewBtn.Content = mode == Mode.Text ? "🔍   Detect text" : "📷   New capture";
        RefreshEmptyText();
    }

    public void RefreshShortcutHint()
    {
        var c = _manager.Config;
        var keys = new List<string>();
        if (c.ShotWinS) keys.Add("Win + S");
        if (c.ShotCtrlAltS) keys.Add("Ctrl + Alt + S");
        ShortcutHint.Text = keys.Count > 0 ? "Shortcut:  " + string.Join("   ·   ", keys) : "Shortcuts are off (Settings → Screen Capture)";
        RefreshEmptyText();
    }

    private void RefreshEmptyText()
    {
        if (EmptyText == null) return;
        var c = _manager.Config;
        string key = c.ShotWinS ? "Win + S" : c.ShotCtrlAltS ? "Ctrl + Alt + S" : "";
        EmptyText.Text = (_mode == Mode.Text ? "Click Detect text" : "Click New capture") + (key.Length > 0 ? $", or press {key} anywhere." : ".") +
                         "\nYou can also drop a picture here.";
    }

    // ---------- capturing ----------
    private void New_Click(object sender, RoutedEventArgs e) => _ = CaptureAsync();

    /// <summary>Starts a capture with the chosen mode and delay (also called by the keyboard shortcuts).</summary>
    public async Task CaptureAsync()
    {
        if (_busy) return;
        _busy = true;
        App.Capturing = true;
        var shell = Window.GetWindow(this);
        try
        {
            shell?.Hide();                                                      // never capture ourselves
            await Task.Delay(260 + _delaySeconds * 1000);

            var shot = ScreenGrab.CaptureVirtualScreen(out var area);
            BitmapSource? result = null;
            if (_mode == Mode.Full) result = shot;
            else
            {
                var overlay = new CaptureOverlay(shot, area, _mode == Mode.Window ? CaptureOverlay.Kind.Window : CaptureOverlay.Kind.Rectangle);
                overlay.ShowDialog();
                result = overlay.Result;
            }
            if (result == null) return;                                         // cancelled

            SetImage(result);
            if (_mode == Mode.Text) await DetectAsync(copyResult: true);
            else Finished(result);
        }
        catch (Exception ex)
        {
            // never fail silently: tell the person, and leave the details where they can be found
            try { File.AppendAllText(Path.Combine(App.DataDir, "capture-error.log"), $"{DateTime.Now:s} {ex}" + Environment.NewLine); } catch (Exception) { }
            App.Notify("Screen capture failed", ex.Message, null);
        }
        finally
        {
            _busy = false;
            App.Capturing = false;
            App.Show("capture");                                                // back to the result
        }
    }

    /// <summary>What the settings say should happen to every new capture.</summary>
    private void Finished(BitmapSource picture)
    {
        var c = _manager.Config;
        if (c.ShotCopy) TryCopyImage(picture);
        if (c.ShotAutoSave) SaveTo(picture, announce: false);
    }

    // ---------- the picture ----------
    private void SetImage(BitmapSource image)
    {
        _image = image;
        Preview.Source = image;
        Surface.Width = image.PixelWidth;                                       // 1 unit = 1 picture pixel, so marks line up exactly
        Surface.Height = image.PixelHeight;
        Ink.Strokes.Clear();
        EmptyHint.Visibility = Visibility.Collapsed;
        PreviewBox.Visibility = Visibility.Visible;
        ToolBar.Visibility = Visibility.Visible;
        ApplyTool();
    }

    private void SetTool(Tool tool) { _tool = tool; ApplyTool(); }

    private void ApplyTool()
    {
        if (Ink == null) return;
        if (_tool == Tool.Eraser) { Ink.EditingMode = InkCanvasEditingMode.EraseByStroke; return; }
        Ink.EditingMode = InkCanvasEditingMode.Ink;
        bool hl = _tool == Tool.Highlighter;
        Ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = _color, FitToCurve = true, IsHighlighter = hl,
            Width = hl ? _size * 3 : _size, Height = hl ? _size * 3 : _size,
            StylusTip = hl ? StylusTip.Rectangle : StylusTip.Ellipse,
        };
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (Ink.Strokes.Count > 0) Ink.Strokes.RemoveAt(Ink.Strokes.Count - 1);
    }

    private void ClearMarks_Click(object sender, RoutedEventArgs e) => Ink.Strokes.Clear();

    /// <summary>The picture with the pen marks on it.</summary>
    private BitmapSource Composite()
    {
        var image = _image ?? throw new InvalidOperationException("There is no picture yet.");
        if (Ink.Strokes.Count == 0) return image;
        int w = image.PixelWidth, h = image.PixelHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(image, new Rect(0, 0, w, h));
            foreach (var stroke in Ink.Strokes) stroke.Draw(dc);
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    // ---------- copy / save ----------
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_image == null) return;
        if (TryCopyImage(Composite())) App.Notify("Copied", "The picture is on the clipboard.", null);
    }

    private static bool TryCopyImage(BitmapSource picture)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetImage(picture); return true; }
            catch (System.Runtime.InteropServices.COMException) { System.Threading.Thread.Sleep(80); }    // another program has the clipboard for a moment
        }
        App.Notify("Couldn't copy", "Another program is using the clipboard. Try again.", null);
        return false;
    }

    private string SaveFolder()
    {
        string dir = _manager.Config.ShotDir;
        return string.IsNullOrWhiteSpace(dir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots") : dir;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_image != null) SaveTo(Composite(), announce: true);
    }

    private void SaveTo(BitmapSource picture, bool announce)
    {
        try
        {
            string dir = SaveFolder();
            Directory.CreateDirectory(dir);
            string name = Util.UniqueName(dir, $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}.png");
            string path = Path.Combine(dir, name);
            Encode(picture, path);
            if (announce) App.Notify("Screenshot saved", name, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Notify("Couldn't save the screenshot", ex.Message, null);
        }
    }

    /// <summary>Ctrl + S: opens "Save as…" when the keys are exactly Ctrl + S and there is a picture. Returns true when it did.</summary>
    public bool TrySaveAs()
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || (Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Shift)) != 0 || _image == null) return false;
        SaveAs_Click(this, new RoutedEventArgs());
        return true;
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_image == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save picture", InitialDirectory = SaveFolder(), FileName = $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}",
            Filter = "PNG picture|*.png|JPG picture|*.jpg", DefaultExt = ".png",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try { Encode(Composite(), dlg.FileName); App.Notify("Screenshot saved", Path.GetFileName(dlg.FileName), dlg.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.Notify("Couldn't save the screenshot", ex.Message, null); }
    }

    private static void Encode(BitmapSource picture, string path)
    {
        BitmapEncoder encoder = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? new JpegBitmapEncoder { QualityLevel = 93 } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    // ---------- opening pictures ----------
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a picture", Filter = "Pictures|" + string.Join(";", ImageConverter.InputExtensions.Select(x => "*." + x)) + "|All files|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) _ = LoadFileAsync(dlg.FileName);
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        BitmapSource? image = null;
        try { if (Clipboard.ContainsImage()) image = Clipboard.GetImage(); }
        catch (System.Runtime.InteropServices.COMException) { }
        if (image == null) { App.Notify("Nothing to paste", "There is no picture on the clipboard.", null); return; }
        _ = ShowLoadedAsync(image);
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Page_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) _ = LoadFileAsync(files[0]);
    }

    private async Task LoadFileAsync(string path)
    {
        try
        {
            BitmapDecoder decoder;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            await ShowLoadedAsync(frame);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            App.Notify("Couldn't open that picture", ex.Message, null);
        }
    }

    private async Task ShowLoadedAsync(BitmapSource image)
    {
        var flat = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        flat.Freeze();
        SetImage(flat);
        if (_mode == Mode.Text) await DetectAsync(copyResult: true);
    }

    // ---------- text detector ----------
    private void Detect_Click(object sender, RoutedEventArgs e) => _ = DetectAsync(copyResult: false);
    private void HideText_Click(object sender, RoutedEventArgs e) => TextPanel.Visibility = Visibility.Collapsed;

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        string text = Detected.SelectionLength > 0 ? Detected.SelectedText : Detected.Text;
        if (text.Length > 0 && CopyText(text)) TextStatus.Text = "Copied";
    }

    private static bool CopyText(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                App.ClipboardWatch?.IgnoreNext(text.Trim());      // our own copy must not trigger the "download this link?" popup
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException) { System.Threading.Thread.Sleep(80); }
        }
        return false;
    }

    private async Task DetectAsync(bool copyResult)
    {
        if (_image == null) return;
        var picture = _image;
        TextPanel.Visibility = Visibility.Visible;
        TextStatus.Text = "Reading the text…";
        Detected.Text = "";
        BuildLanguageChips();
        try
        {
            string text = await TextDetector.ReadAsync(picture, _language);
            Detected.Text = text;
            if (text.Length == 0) { TextStatus.Text = "No text found in this picture."; return; }
            int lines = text.Split('\n').Length;
            TextStatus.Text = $"{text.Length} characters, {lines} line{(lines == 1 ? "" : "s")}";
            if (copyResult && CopyText(text)) { TextStatus.Text += "  ·  copied to the clipboard"; App.Notify("Text copied", Snippet(text), null); }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException or IOException)
        {
            TextStatus.Text = "";
            Detected.Text = ex.Message;
        }
    }

    private static string Snippet(string text) => text.Length <= 90 ? text.Replace('\n', ' ') : text[..90].Replace('\n', ' ') + "…";

    /// <summary>If Windows has several text-recognition languages, let the person pick one.</summary>
    private void BuildLanguageChips()
    {
        if (_languagesBuilt) return;
        _languagesBuilt = true;
        List<(string Name, string Tag)> languages;
        try { languages = TextDetector.Languages(); }
        catch (Exception) { return; }
        if (languages.Count < 2) return;
        LangChips.Visibility = Visibility.Visible;
        void Add(string label, string? tag, bool on)
        {
            var chip = new RadioButton { Content = label, GroupName = "lang", Style = (Style)FindResource("ChipButton"), IsChecked = on };
            chip.Checked += (_, _) => { _language = tag; if (_image != null) _ = DetectAsync(copyResult: false); };
            LangChips.Children.Add(chip);
        }
        Add("Automatic", null, true);
        foreach (var (name, tag) in languages) Add(name, tag, false);
    }
}
