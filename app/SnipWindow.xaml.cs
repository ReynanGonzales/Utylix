using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// The screen capture tool, laid out like the classic Windows Snipping Tool: New, Mode, Delay, Cancel, Options. Win + S (or
/// Ctrl + Alt + S) takes a snip from anywhere; the window comes up afterwards with the picture, where you can mark it up
/// (pen, highlighter, eraser), copy, save, or read the text in it.
/// </summary>
public partial class SnipWindow : Window
{
    private enum Mode { FreeForm, Rectangle, Window, Full, Text }
    private enum Tool { Pen, Highlighter, Eraser }

    private static readonly (string Name, string Hex)[] Colors =
    {
        ("Red", "#E5484D"), ("Yellow", "#FFD60A"), ("Green", "#30A46C"), ("Blue", "#3E63DD"), ("Black", "#111111"), ("White", "#FFFFFF"),
    };
    private static readonly (string Label, string Menu, double Size)[] Sizes = { ("S", "Thin", 2.5), ("M", "Medium", 4.0), ("L", "Thick", 8.0) };
    private const string DefaultMessage = "Select the snip mode using the Mode button or click the New button.";

    private static SnipWindow? _instance;
    private static Mode _mode = Mode.Rectangle;              // remembered while Utylix runs
    private static int _delaySeconds;

    private readonly Manager _manager;
    private Tool _tool = Tool.Pen;
    private Color _color = (Color)ColorConverter.ConvertFromString("#E5484D");
    private int _sizeIndex = 1;
    private BitmapSource? _image;
    private bool _busy;
    private CancellationTokenSource? _cts;
    private CaptureOverlay? _overlay;

    private SnipWindow(Manager manager)
    {
        InitializeComponent();
        WindowTheme.DarkTitleBar(this);
        _manager = manager;
        foreach (var (name, hex) in Colors)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var swatch = new RadioButton { Style = (Style)FindResource("Swatch"), GroupName = "color", Background = new SolidColorBrush(color), ToolTip = name, IsChecked = name == "Red" };
            swatch.Checked += (_, _) => { _color = color; ApplyTool(); };
            Swatches.Children.Add(swatch);
        }
        ApplyTool();
        PreviewKeyDown += OnKey;
        Closed += (_, _) => { if (_instance == this) _instance = null; };
    }

    /// <summary>The one snip window (made when first needed, not shown until there is something to show).</summary>
    public static SnipWindow Get(Manager manager) => _instance ??= new SnipWindow(manager);

    /// <summary>Shows the window (with the picture of the last snip, or the "select the snip mode" message).</summary>
    public void Open()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        ScreenGrab.ForceForeground(new System.Windows.Interop.WindowInteropHelper(this).Handle);     // in front of whatever is open
        Activate();
    }

    /// <summary>Take a snip now with the mode and delay that are set (Win + S, the tray icon).</summary>
    public static void StartCapture(Manager manager) => _ = Get(manager).CaptureAsync();

    // ---------- the buttons ----------
    private void New_Click(object sender, RoutedEventArgs e) => _ = CaptureAsync();

    private static string ModeName(Mode m) => m switch
    {
        Mode.FreeForm => "Free-form Snip", Mode.Rectangle => "Rectangular Snip", Mode.Window => "Window Snip", Mode.Full => "Full-screen Snip", _ => "Text Detector",
    };

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ModeBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var m in new[] { Mode.FreeForm, Mode.Rectangle, Mode.Window, Mode.Full, Mode.Text })
        {
            if (m == Mode.Text) menu.Items.Add(new Separator());
            var item = new MenuItem { Header = ModeName(m), IsCheckable = true, IsChecked = m == _mode };
            var chosen = m;
            item.Click += (_, _) => { _mode = chosen; _ = CaptureAsync(); };            // choosing a mode starts the snip, like the Snipping Tool
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void Delay_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = DelayBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        for (int s = 0; s <= 5; s++)
        {
            int seconds = s;
            var item = new MenuItem { Header = s == 0 ? "No delay" : s == 1 ? "1 second" : $"{s} seconds", IsCheckable = true, IsChecked = s == _delaySeconds };
            item.Click += (_, _) => { _delaySeconds = seconds; };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _overlay?.Close();
    }

    private void Options_Click(object sender, RoutedEventArgs e) => new SettingsWindow(_manager, "capture") { Owner = this }.ShowDialog();

    // ---------- capturing ----------
    /// <summary>Starts a snip with the chosen mode and delay.</summary>
    public async Task CaptureAsync()
    {
        if (_busy) return;
        _busy = true;
        App.Capturing = true;
        bool wasVisible = IsVisible;
        _cts = new CancellationTokenSource();
        Action? restore = null;
        bool got = false;
        try
        {
            // a delay: the window stays in view (with a count-down and a working Cancel) until the last moment
            for (int left = _delaySeconds; left > 0; left--)
            {
                if (IsVisible) { CancelBtn.IsEnabled = true; if (_image == null) MessageText.Text = $"Snipping in {left}…  (Cancel stops it)"; }
                await Task.Delay(1000, _cts.Token);
            }
            CancelBtn.IsEnabled = false;
            restore = App.HideForCapture();                                      // never capture ourselves
            await Task.Delay(260);

            var shot = ScreenGrab.CaptureVirtualScreen(out var area);
            BitmapSource? result;
            if (_mode == Mode.Full) result = shot;
            else
            {
                var kind = _mode == Mode.Window ? CaptureOverlay.Kind.Window : _mode == Mode.FreeForm ? CaptureOverlay.Kind.FreeForm : CaptureOverlay.Kind.Rectangle;
                _overlay = new CaptureOverlay(shot, area, kind);
                _overlay.ShowDialog();
                result = _overlay.Result;
                _overlay = null;
            }
            if (result == null) return;                                         // cancelled

            SetImage(result);
            got = true;
            if (_mode == Mode.Text) await DetectAsync(copyResult: true);
            else Finished(result);
        }
        catch (OperationCanceledException) { /* Cancel during the delay */ }
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
            CancelBtn.IsEnabled = false;
            if (_image == null) MessageText.Text = DefaultMessage;
            restore?.Invoke();
            if (got || wasVisible) Open();
        }
    }

    /// <summary>What the settings say should happen to every new snip.</summary>
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
        Message.Visibility = Visibility.Collapsed;
        PictureHost.Visibility = Visibility.Visible;
        EditBar.Visibility = Visibility.Visible;
        ApplyTool();

        // like the Snipping Tool, the window grows to fit the snip (up to most of the screen)
        var work = SystemParameters.WorkArea;
        double scale = ScreenGrab.Scale;
        double w = Math.Clamp(image.PixelWidth / scale + 40, 620, work.Width * 0.9);
        double h = Math.Clamp(image.PixelHeight / scale + 200, 340, work.Height * 0.9);
        Width = w; Height = h;
        if (IsVisible) { Left = Math.Max(work.Left, Math.Min(Left, work.Right - w)); Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - h)); }
    }

    private void SetTool(Tool tool)
    {
        _tool = tool;
        PenBtn.IsChecked = tool == Tool.Pen; HighlightBtn.IsChecked = tool == Tool.Highlighter; EraserBtn.IsChecked = tool == Tool.Eraser;
        ApplyTool();
    }

    private void Pen_Click(object sender, RoutedEventArgs e) => SetTool(Tool.Pen);
    private void Highlight_Click(object sender, RoutedEventArgs e) => SetTool(Tool.Highlighter);
    private void Eraser_Click(object sender, RoutedEventArgs e) => SetTool(Tool.Eraser);

    private void Size_Click(object sender, RoutedEventArgs e)
    {
        _sizeIndex = (_sizeIndex + 1) % Sizes.Length;
        SizeText.Text = Sizes[_sizeIndex].Label;
        SizeBtn.ToolTip = "Thickness: " + Sizes[_sizeIndex].Menu;
        ApplyTool();
    }

    private void ApplyTool()
    {
        if (Ink == null) return;
        if (_tool == Tool.Eraser) { Ink.EditingMode = InkCanvasEditingMode.EraseByStroke; return; }
        bool hl = _tool == Tool.Highlighter;
        double size = Sizes[_sizeIndex].Size;
        Ink.EditingMode = InkCanvasEditingMode.Ink;
        Ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = _color, FitToCurve = true, IsHighlighter = hl,
            Width = hl ? size * 3 : size, Height = hl ? size * 3 : size,
            StylusTip = hl ? StylusTip.Rectangle : StylusTip.Ellipse,
        };
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (Ink.Strokes.Count > 0) Ink.Strokes.RemoveAt(Ink.Strokes.Count - 1);
    }

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

    // ---------- keys ----------
    private void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0 && (Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Shift)) == 0;
        bool inText = Keyboard.FocusedElement is TextBox;
        if (!ctrl) return;
        switch (e.Key)
        {
            case Key.N: _ = CaptureAsync(); e.Handled = true; break;
            case Key.S when _image != null: SaveAs_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.C when _image != null && !inText: Copy_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.O: Open_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case Key.V when !inText: Paste_Click(this, new RoutedEventArgs()); e.Handled = true; break;
        }
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
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(80); }    // another program has the clipboard for a moment
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

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_image == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save picture", InitialDirectory = SaveFolder(), FileName = $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}",
            Filter = "PNG picture|*.png|JPG picture|*.jpg", DefaultExt = ".png",
        };
        if (dlg.ShowDialog(this) != true) return;
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
        if (dlg.ShowDialog(this) == true) _ = LoadFileAsync(dlg.FileName);
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        BitmapSource? image = null;
        try { if (Clipboard.ContainsImage()) image = Clipboard.GetImage(); }
        catch (System.Runtime.InteropServices.COMException) { }
        if (image == null) { App.Notify("Nothing to paste", "There is no picture on the clipboard.", null); return; }
        ShowLoaded(image);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
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
            ShowLoaded(frame);
            await Task.CompletedTask;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            App.Notify("Couldn't open that picture", ex.Message, null);
        }
    }

    private void ShowLoaded(BitmapSource image)
    {
        var flat = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        flat.Freeze();
        SetImage(flat);
        if (_mode == Mode.Text) _ = DetectAsync(copyResult: true);
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
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(80); }
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
        try
        {
            string text = await TextDetector.ReadAsync(picture, null);
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
}
