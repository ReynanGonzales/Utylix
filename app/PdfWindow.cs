using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>One small page in the side strip (drawn when it first scrolls into view).</summary>
public sealed class PdfThumb : INotifyPropertyChanged
{
    private static readonly SemaphoreSlim Gate = new(2, 2);
    private ImageSource? _source;
    private bool _started;
    public PdfFile? Pdf { get; init; }
    public int Index { get; init; }
    public int Rotation { get; init; }
    public string Label => (Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    public double Width { get; init; }
    public double Height { get; init; }

    public ImageSource? Source
    {
        get
        {
            if (!_started) { _started = true; _ = LoadAsync(); }
            return _source;
        }
    }

    private async Task LoadAsync()
    {
        await Gate.WaitAsync();
        try
        {
            var pdf = Pdf;
            if (pdf == null) return;
            _source = await Task.Run(() =>
            {
                try { return (ImageSource)pdf.Render(Index, (int)(Width * 1.5), (int)(Height * 1.5), Rotation); }
                catch (Exception e) when (e is ObjectDisposedException or IOException or OutOfMemoryException) { return null; }
            });
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Source)));
        }
        finally { Gate.Release(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Utylix PDF: reads PDFs (PDFium, the engine inside Chrome) and makes them smaller ("Reduce file size", like Acrobat), all offline.
/// The pages sit in one column; only the pages on screen are drawn, sharp at any zoom. Ctrl + wheel zooms, Ctrl+P prints.
/// </summary>
public sealed partial class PdfWindow : Window
{
    public static readonly string[] Extensions = { "pdf" };
    public static bool IsPdf(string path) => string.Equals(System.IO.Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    // ---------- one window per PDF ----------
    private static readonly List<PdfWindow> Windows = new();
    public static int Count => Windows.Count;
    public static event Action? AnyClosed;

    public static void Open(IEnumerable<string> files)
    {
        var list = files.Where(f => !string.IsNullOrWhiteSpace(f) && File.Exists(f) && IsPdf(f)).Select(System.IO.Path.GetFullPath).ToList();
        if (list.Count == 0) { AnyClosed?.Invoke(); return; }
        foreach (string file in list)
        {
            var w = Windows.FirstOrDefault(x => string.Equals(x._path, file, StringComparison.OrdinalIgnoreCase))
                    ?? Windows.FirstOrDefault(x => x._path == null);
            if (w == null) { w = new PdfWindow(); w.Show(); }
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
            if (!string.Equals(w._path, file, StringComparison.OrdinalIgnoreCase)) _ = w.LoadAsync(file);
        }
    }

    /// <summary>Asks for PDFs (from the Dashboard / tray) and opens them.</summary>
    public static void Browse() => Browse(null);

    private static void Browse(Window? owner)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Open a PDF", Filter = "PDF|*.pdf|All files|*.*", Multiselect = true };
        if (owner != null ? dlg.ShowDialog(owner) == true : dlg.ShowDialog() == true) Open(dlg.FileNames);
    }

    /// <summary>An empty Utylix PDF window (to open or drop a PDF in).</summary>
    public static void OpenEmpty()
    {
        var w = Windows.FirstOrDefault() ?? new PdfWindow();
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    // ---------- one page in the column ----------
    private sealed class PageView : Border
    {
        public readonly Image Picture = new() { Stretch = Stretch.Fill };
        /// <summary>The editing layer: its units are points of the page as shown, scaled to the zoom.</summary>
        public readonly Canvas Overlay = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true };
        public readonly ScaleTransform OverlayScale = new();
        /// <summary>Under the editing layer: the text selection and the search results (same units).</summary>
        public readonly Canvas TextLayer = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        public int Index;
        public int RenderedWidth;           // pixels of the picture shown now (0: none)
        public int WantedWidth;             // pixels asked for (to not ask twice)
        public PageView()
        {
            Background = Brushes.White;
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x1C));
            BorderThickness = new Thickness(1);
            Margin = new Thickness(0, 0, 0, 12);
            HorizontalAlignment = HorizontalAlignment.Center;
            RenderOptions.SetBitmapScalingMode(Picture, BitmapScalingMode.HighQuality);
            Overlay.RenderTransform = OverlayScale;
            TextLayer.RenderTransform = OverlayScale;
            Overlay.Background = Brushes.Transparent;          // (always listening: selecting text, links, editing)
            var both = new Grid();
            both.Children.Add(Picture);
            both.Children.Add(TextLayer);
            both.Children.Add(Overlay);
            Child = both;
        }
    }

    private enum Fit { None, Width, Page }

    // ---------- state ----------
    private PdfFile? _pdf;
    private string? _path;
    private Size[] _sizes = Array.Empty<Size>();
    private double _zoom = 1;                // 1 = 100 % (a page shown at its printed size)
    private Fit _fit = Fit.Width;
    private int _rotation;                   // quarter turns clockwise, for looking only (the file isn't changed)
    private int _generation;                 // goes up when zoom / rotation / document change: older drawings are dropped
    private int _current;
    private readonly SemaphoreSlim _renderGate = new(1, 1);

    // ---------- parts ----------
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = true, PanningMode = PanningMode.Both };
    private readonly StackPanel _column = new() { Margin = new Thickness(16, 16, 16, 4) };
    private readonly List<PageView> _pages = new();
    private readonly ListBox _strip = new();
    private readonly ObservableCollection<PdfThumb> _thumbs = new();
    private readonly Border _stripBox = new() { Width = 168 };
    private readonly TextBox _pageBox = new() { Width = 46, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Height = 28, Padding = new Thickness(2, 0, 2, 0) };   // (the app's text box padding is too tall for the tool bar)
    private readonly TextBlock _pageCount = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
    private readonly TextBlock _zoomText = new() { VerticalAlignment = VerticalAlignment.Center, Width = 48, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _info = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0), Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _message = new() { FontSize = 15, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _toast = new() { Foreground = Brushes.White, FontSize = 13 };
    private readonly Border _toastBox = new() { Background = new SolidColorBrush(Color.FromArgb(225, 20, 24, 34)), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 7, 14, 7), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 24), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly List<UIElement> _needsDocument = new();
    private bool _syncingStrip;

    private static readonly Brush Bar = new SolidColorBrush(Color.FromRgb(0x1B, 0x1F, 0x29));
    private static readonly Brush Desk = new SolidColorBrush(Color.FromRgb(0x2B, 0x2F, 0x3A));
    private static readonly Brush Soft = new SolidColorBrush(Color.FromRgb(0xD0, 0xD5, 0xE2));

    private PdfWindow()
    {
        Title = "Utylix Editor";
        Width = 1100; Height = 800; MinWidth = 560; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Desk;
        Foreground = Brushes.White;
        AllowDrop = true;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/pdf.ico")); } catch (Exception e) when (e is IOException or UriFormatException) { }
        WindowTheme.DarkTitleBar(this);
        WindowTheme.OwnTaskbarButton(this, "Utylix.Pdf");

        // ---- tool bar ----
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button Tool(string glyph, string tip, Action action, string id, bool needsDoc = true, string font = "Segoe MDL2 Assets")
        {
            var b = new Button { Content = new TextBlock { Text = glyph, FontFamily = new FontFamily(font), FontSize = 16, Foreground = Brushes.White }, ToolTip = tip, Template = ToolTemplate(), Margin = new Thickness(1, 0, 1, 0), Foreground = Brushes.White, Focusable = false };
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, id);
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            b.Click += (_, _) => action();
            tools.Children.Add(b);
            if (needsDoc) _needsDocument.Add(b);
            return b;
        }
        Tool("", "Open a PDF (Ctrl+O)", () => Browse(this), "PdfOpen", needsDoc: false);
        Tool("", "Save a copy (Ctrl+S)", SaveCopy, "PdfSaveCopy");
        Tool("", "Print (Ctrl+P)", Print, "PdfPrint");
        Tool("", "Pages at the side (F4)", ToggleStrip, "PdfStrip");
        Tool("", "Search (Ctrl+F)", OpenSearch, "PdfSearch");
        Gap(tools);
        Tool("", "Previous page", () => GoTo(_current - 1), "PdfPrev");
        tools.Children.Add(_pageBox);
        tools.Children.Add(_pageCount);
        _needsDocument.Add(_pageBox);
        Tool("", "Next page", () => GoTo(_current + 1), "PdfNext");
        Gap(tools);
        Tool("", "Zoom out (Ctrl+-)", () => ZoomBy(1 / 1.2), "PdfZoomOut");
        tools.Children.Add(_zoomText);
        Tool("", "Zoom in (Ctrl+=)", () => ZoomBy(1.2), "PdfZoomIn");
        Tool("↔", "Fit the width (Ctrl+2)", () => SetFit(Fit.Width), "PdfFitWidth", font: "Segoe UI Symbol");
        Tool("", "Whole page (Ctrl+0)", () => SetFit(Fit.Page), "PdfFitPage");
        Tool("", "Turn the pages (Ctrl+R) - for viewing only", Rotate, "PdfRotate");
        Gap(tools);
        Tool("", "Show in its folder", ShowInFolder, "PdfFolder");

        var reduce = new Button { Content = "Reduce file size", Style = (Style)Application.Current.FindResource("DialogPrimary"), Height = 30, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(8, 0, 4, 0), Focusable = false, ToolTip = "Make a smaller copy of this PDF, like Acrobat's \"Reduce file size\"" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(reduce, "PdfReduce");
        reduce.Click += (_, _) => Reduce();
        _needsDocument.Add(reduce);
        var pageSize = new Button { Content = BarLabel("Page size"), Template = BarButtonTemplate(),  Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(8, 0, 0, 0), Focusable = false, Background = Brushes.Transparent, Foreground = Brushes.White, ToolTip = "Make a copy with every page on another size of paper (A4, Letter, Long ...)" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(pageSize, "PdfPageSize");
        pageSize.Click += (_, _) => PageSize();
        _needsDocument.Add(pageSize);

        var top = new DockPanel { Background = Bar, Height = 46, LastChildFill = true };
        DockPanel.SetDock(reduce, Dock.Right);
        top.Children.Add(reduce);
        DockPanel.SetDock(pageSize, Dock.Right);
        top.Children.Add(pageSize);
        var edit = EditButton();
        DockPanel.SetDock(edit, Dock.Right);
        top.Children.Add(edit);
        _needsDocument.Add(edit);
        var toolsHolder = new Border { Child = tools, Padding = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(toolsHolder, Dock.Left);
        top.Children.Add(toolsHolder);
        _info.Foreground = Soft;
        top.Children.Add(_info);

        _pageBox.Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x30, 0x3D));
        _pageBox.Foreground = Brushes.White; _pageBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x4B, 0x5C)); _pageBox.CaretBrush = Brushes.White;
        System.Windows.Automation.AutomationProperties.SetAutomationId(_pageBox, "PdfPageBox");
        _pageBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (int.TryParse(_pageBox.Text.Trim(), out int n)) GoTo(n - 1);
            _scroll.Focus();
            e.Handled = true;
        };
        _pageBox.GotKeyboardFocus += (_, _) => _pageBox.SelectAll();
        _pageCount.Foreground = Soft; _zoomText.Foreground = Soft;

        // ---- pages at the side ----
        _strip.ItemsSource = _thumbs;
        _strip.Background = Bar;
        _strip.BorderThickness = new Thickness(0);
        _strip.Padding = new Thickness(0, 8, 0, 8);
        ScrollViewer.SetHorizontalScrollBarVisibility(_strip, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(_strip, true);
        VirtualizingPanel.SetVirtualizationMode(_strip, VirtualizationMode.Recycling);
        _strip.ItemTemplate = (DataTemplate)XamlReader.Parse(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel HorizontalAlignment='Center'>" +
            "<Border Background='White' Width='{Binding Width}' Height='{Binding Height}'><Image Source='{Binding Source}' Stretch='Fill' /></Border>" +
            "<TextBlock Text='{Binding Label}' Foreground='#A7AEBF' FontSize='11' HorizontalAlignment='Center' Margin='0,4,0,0' /></StackPanel></DataTemplate>");
        _strip.ItemContainerStyle = (Style)XamlReader.Parse(
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListBoxItem'><Setter Property='Margin' Value='0,4' /><Setter Property='HorizontalContentAlignment' Value='Center' /><Setter Property='Template'><Setter.Value>" +
            "<ControlTemplate TargetType='ListBoxItem'><Border x:Name='bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' BorderThickness='2' BorderBrush='Transparent' CornerRadius='3' Padding='6' Margin='10,0'><ContentPresenter HorizontalAlignment='Center' /></Border>" +
            "<ControlTemplate.Triggers><Trigger Property='IsSelected' Value='True'><Setter TargetName='bd' Property='BorderBrush' Value='#5B8DEF' /><Setter TargetName='bd' Property='Background' Value='#223B5998' /></Trigger>" +
            "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='BorderBrush' Value='#8FA9D9' /></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>");
        _strip.SelectionChanged += (_, _) => { if (!_syncingStrip && _strip.SelectedIndex >= 0) GoTo(_strip.SelectedIndex); };
        _stripBox.Child = SidePanel(_strip);
        _stripBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x1A));
        _stripBox.BorderThickness = new Thickness(0, 0, 1, 0);

        // ---- the pages ----
        _scroll.Content = _column;
        _scroll.Background = Desk;
        var stage = new Grid();
        stage.Children.Add(_scroll);
        _message.Foreground = Soft;
        stage.Children.Add(_message);
        _toastBox.Child = _toast;
        stage.Children.Add(_toastBox);
        stage.Children.Add(SearchBar());

        var body = new DockPanel();
        DockPanel.SetDock(_stripBox, Dock.Left);
        body.Children.Add(_stripBox);
        body.Children.Add(stage);

        var layout = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        layout.Children.Add(top);
        var editBar = BuildEditBar();
        DockPanel.SetDock(editBar, Dock.Top);
        layout.Children.Add(editBar);
        var formBar = FormBar();
        DockPanel.SetDock(formBar, Dock.Top);
        layout.Children.Add(formBar);
        layout.Children.Add(body);
        Content = layout;

        // ---- input ----
        _scroll.ScrollChanged += (_, e) =>
        {
            if (e.ViewportWidthChange != 0 && _fit != Fit.None) ApplyZoom(FitZoom(_fit), keepPlace: true);
            UpdateCurrent();
            _renderTimer.Stop(); _renderTimer.Start();
        };
        _renderTimer.Tick += (_, _) => { _renderTimer.Stop(); RenderVisible(); };
        _scroll.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            e.Handled = true;
            ZoomBy(e.Delta > 0 ? 1.1 : 1 / 1.1);
        };
        PreviewKeyDown += OnKey;
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;
            var pdfs = dropped.Where(IsPdf).ToList();
            if (pdfs.Count == 0) { Toast("Only PDF files open here"); return; }
            if (_path == null) { _ = LoadAsync(pdfs[0]); pdfs.RemoveAt(0); }
            if (pdfs.Count > 0) Open(pdfs);
        };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toastBox.Visibility = Visibility.Collapsed; };
        Closing += (_, e) => { if (!ConfirmLeaveEdits()) e.Cancel = true; };
        Closed += (_, _) =>
        {
            Windows.Remove(this);
            _generation++;
            _thumbs.Clear();
            _pdf?.Dispose(); _pdf = null;
            AnyClosed?.Invoke();
        };

        Windows.Add(this);
        ShowEmpty();
    }

    private static void Gap(StackPanel p) => p.Children.Add(new Border { Width = 1, Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), Margin = new Thickness(8, 10, 8, 10) });

    private static ControlTemplate ToolTemplate() => (ControlTemplate)XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
        "<Border x:Name='bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='Transparent' CornerRadius='6' Width='36' Height='34'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' /></Border>" +
        "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#33FFFFFF' /></Trigger>" +
        "<Trigger Property='IsPressed' Value='True'><Setter TargetName='bd' Property='Background' Value='#55FFFFFF' /></Trigger>" +
        "<Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.35' /></Trigger></ControlTemplate.Triggers></ControlTemplate>");

    private void ShowEmpty()
    {
        _message.Text = "Open a PDF (Ctrl+O) or drop one here.";
        _message.Visibility = Visibility.Visible;
        foreach (var e in _needsDocument) e.IsEnabled = false;
        _info.Text = ""; _pageCount.Text = ""; _pageBox.Text = ""; _zoomText.Text = "";
    }

    // ---------- loading ----------
    private async Task LoadAsync(string path, string? knownPassword = null, bool keepEditing = false)
    {
        if (!keepEditing && !ConfirmLeaveEdits()) return;
        int generation = ++_generation;
        _message.Text = "Opening " + System.IO.Path.GetFileName(path) + "…";
        _message.Visibility = Visibility.Visible;
        PdfFile? pdf = null;
        string? password = knownPassword;
        bool wrong = false;
        while (pdf == null)
        {
            try { pdf = await Task.Run(() => PdfFile.Open(path, password)); }
            catch (PdfPasswordException)
            {
                var ask = new PasswordWindow(System.IO.Path.GetFileName(path), wrong, "This PDF needs a password") { Owner = this };
                if (ask.ShowDialog() != true) { if (_pdf == null) ShowEmpty(); else _message.Visibility = Visibility.Collapsed; return; }
                password = ask.Password; wrong = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OutOfMemoryException or DllNotFoundException or BadImageFormatException)
            {
                if (generation != _generation) return;
                if (_pdf == null) { ShowEmpty(); _message.Text = "Couldn't open \"" + System.IO.Path.GetFileName(path) + "\".\n" + e.Message; }
                else { _message.Visibility = Visibility.Collapsed; Toast("Couldn't open it: " + e.Message); }
                return;
            }
        }
        if (generation != _generation || !Windows.Contains(this)) { pdf.Dispose(); return; }

        var sizes = await Task.Run(() => Enumerable.Range(0, pdf.PageCount).Select(pdf.PageSize).ToArray());
        if (generation != _generation) { pdf.Dispose(); return; }

        int keepPage = keepEditing ? _current : 0;
        _pdf?.Dispose();
        _pdf = pdf; _path = path; _sizes = sizes; _rotation = 0; _current = 0;
        ResetEdits(keepEditing);
        Title = System.IO.Path.GetFileName(path) + " - Utylix Editor";
        _info.Text = $"{System.IO.Path.GetFileName(path)}   ·   {pdf.PageCount} page{(pdf.PageCount == 1 ? "" : "s")}   ·   {PdfReduceWindow.Bytes(pdf.Length)}";
        _pageCount.Text = "/ " + pdf.PageCount;
        _pageBox.Text = pdf.PageCount == 0 ? "" : "1";
        foreach (var e in _needsDocument) e.IsEnabled = true;
        _message.Visibility = pdf.PageCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (pdf.PageCount == 0) _message.Text = "This PDF has no pages.";
        BuildPages();
        OnDocumentLoaded();
        _fit = Fit.Width;
        UpdateLayout();
        ApplyZoom(FitZoom(Fit.Width), keepPlace: false);
        _scroll.ScrollToTop();
        if (keepPage > 0) { UpdateLayout(); GoTo(keepPage); }
        _scroll.Focus();
    }

    private void BuildPages()
    {
        _column.Children.Clear(); _pages.Clear();
        for (int i = 0; i < _sizes.Length; i++)
        {
            var p = new PageView { Index = i };
            HookOverlay(p);
            _pages.Add(p); _column.Children.Add(p);
        }
        RebuildThumbs();
    }

    private void RebuildThumbs()
    {
        _thumbs.Clear();
        if (_pdf == null) return;
        for (int i = 0; i < _sizes.Length; i++)
        {
            var s = Turned(i);
            double k = Math.Min(118 / s.Width, 150 / s.Height);
            _thumbs.Add(new PdfThumb { Pdf = _pdf, Index = i, Rotation = _rotation, Width = Math.Round(s.Width * k), Height = Math.Round(s.Height * k) });
        }
        SyncStrip();
    }

    // ---------- size and zoom ----------
    /// <summary>The page's size in points as shown (turned).</summary>
    private Size Turned(int i) => _rotation % 2 == 0 ? _sizes[i] : new Size(_sizes[i].Height, _sizes[i].Width);

    private const double PointToDip = 96.0 / 72.0;

    private double FitZoom(Fit fit)
    {
        if (_sizes.Length == 0) return 1;
        double aw = Math.Max(100, _scroll.ViewportWidth - 34), ah = Math.Max(100, _scroll.ViewportHeight - 30);
        double widest = Enumerable.Range(0, _sizes.Length).Max(i => Turned(i).Width) * PointToDip;
        var cur = Turned(Math.Clamp(_current, 0, _sizes.Length - 1));
        return fit == Fit.Page ? Math.Min(aw / (cur.Width * PointToDip), ah / (cur.Height * PointToDip)) : aw / widest;
    }

    private void SetFit(Fit fit) { _fit = fit; ApplyZoom(FitZoom(fit), keepPlace: true, pageTop: fit == Fit.Page); }

    private void ZoomBy(double factor) { _fit = Fit.None; ApplyZoom(_zoom * factor, keepPlace: true); }

    private void ApplyZoom(double zoom, bool keepPlace, bool pageTop = false)
    {
        if (_pages.Count == 0) return;
        zoom = Math.Clamp(zoom, 0.1, 8);
        // stay on the same spot of the same page
        int anchor = Math.Clamp(_current, 0, _pages.Count - 1);
        double within = 0;
        if (keepPlace && !pageTop)
        {
            double top = _pages[anchor].TranslatePoint(new Point(0, 0), _column).Y;
            within = _pages[anchor].ActualHeight > 0 ? (_scroll.VerticalOffset - top) / _pages[anchor].ActualHeight : 0;
        }
        bool changed = Math.Abs(zoom - _zoom) > 0.0005 || _pages[0].Width is double.NaN or 0;
        _zoom = zoom;
        if (changed)
        {
            _generation++;
            for (int i = 0; i < _pages.Count; i++)
            {
                var s = Turned(i);
                _pages[i].Width = Math.Round(s.Width * PointToDip * _zoom);
                _pages[i].Height = Math.Round(s.Height * PointToDip * _zoom);
                _pages[i].WantedWidth = 0;
                // the editing layer: one unit = one point, laid exactly over the page picture (inside the 1 px frame)
                _pages[i].Overlay.Width = s.Width; _pages[i].Overlay.Height = s.Height;
                _pages[i].TextLayer.Width = s.Width; _pages[i].TextLayer.Height = s.Height;
                _pages[i].OverlayScale.ScaleX = (_pages[i].Width - 2) / s.Width;
                _pages[i].OverlayScale.ScaleY = (_pages[i].Height - 2) / s.Height;
            }
            OnZoomChangedForEditing();
        }
        _zoomText.Text = Math.Round(_zoom * 100) + " %";
        if (keepPlace)
        {
            _column.UpdateLayout();
            double top = _pages[anchor].TranslatePoint(new Point(0, 0), _column).Y;
            _scroll.ScrollToVerticalOffset(pageTop ? top - 8 : top + within * _pages[anchor].ActualHeight);
        }
        _renderTimer.Stop(); _renderTimer.Start();
    }

    // ---------- drawing the pages on screen ----------
    private void RenderVisible()
    {
        if (_pdf == null || _pages.Count == 0) return;
        double vh = _scroll.ViewportHeight;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int generation = _generation;
        var near = new List<PageView>();
        foreach (var p in _pages)
        {
            double y = p.TranslatePoint(new Point(0, 0), _scroll).Y;
            bool visible = y + p.Height > -200 && y < vh + 200;
            bool keep = y + p.Height > -vh * 2 && y < vh * 3;
            if (visible) near.Add(p);
            else if (!keep && p.RenderedWidth != 0) { p.Picture.Source = null; p.RenderedWidth = 0; p.WantedWidth = 0; }   // far away: give the memory back
        }
        // the page in the middle first
        near.Sort((a, b) => Math.Abs(a.Index - _current).CompareTo(Math.Abs(b.Index - _current)));
        foreach (var p in near)
        {
            int w = (int)Math.Round(p.Width * dpi), h = (int)Math.Round(p.Height * dpi);
            double big = (double)w * h / 36_000_000;                    // very deep zoom: keep one page under ~36 megapixels
            if (big > 1) { w = (int)(w / Math.Sqrt(big)); h = (int)(h / Math.Sqrt(big)); }
            if (p.RenderedWidth == w || p.WantedWidth == w) continue;
            p.WantedWidth = w;
            _ = RenderPage(p, w, h, generation);
        }
    }

    private async Task RenderPage(PageView p, int w, int h, int generation)
    {
        await _renderGate.WaitAsync();
        try
        {
            var pdf = _pdf;
            if (pdf == null || generation != _generation || p.WantedWidth != w) return;
            int index = p.Index, rotation = _rotation;
            var picture = await Task.Run(() =>
            {
                try { return pdf.Render(index, w, h, rotation); }
                catch (Exception e) when (e is ObjectDisposedException or IOException or OutOfMemoryException) { return null; }
            });
            if (picture == null || generation != _generation) { if (p.WantedWidth == w) p.WantedWidth = 0; return; }
            p.Picture.Source = picture;
            p.RenderedWidth = w;
        }
        finally { _renderGate.Release(); }
    }

    // ---------- pages ----------
    private void UpdateCurrent()
    {
        if (_pages.Count == 0) return;
        double middle = _scroll.ViewportHeight / 3;
        int best = _current;
        for (int i = 0; i < _pages.Count; i++)
        {
            double y = _pages[i].TranslatePoint(new Point(0, 0), _scroll).Y;
            if (y <= middle) best = i; else break;
        }
        if (best != _current || _pageBox.Text.Length == 0)
        {
            _current = best;
            if (!_pageBox.IsKeyboardFocused) _pageBox.Text = (_current + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            SyncStrip();
        }
    }

    private void SyncStrip()
    {
        if (_current < 0 || _current >= _thumbs.Count) return;
        _syncingStrip = true;
        try { _strip.SelectedIndex = _current; _strip.ScrollIntoView(_thumbs[_current]); }
        finally { _syncingStrip = false; }
    }

    private void GoTo(int index)
    {
        if (_pages.Count == 0) return;
        index = Math.Clamp(index, 0, _pages.Count - 1);
        double top = _pages[index].TranslatePoint(new Point(0, 0), _column).Y;
        _scroll.ScrollToVerticalOffset(top - 8);
        _current = index;
        _pageBox.Text = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        SyncStrip();
    }

    private void Rotate()
    {
        if (_pdf == null) return;
        if (_editing) { Toast("Turning the view is off while editing"); return; }
        _rotation = (_rotation + 1) % 4;
        int keep = _current;
        foreach (var p in _pages) { p.Picture.Source = null; p.RenderedWidth = 0; }
        RebuildThumbs();
        _pages[0].Width = 0;                        // (forces the new sizes)
        ApplyZoom(_fit == Fit.None ? _zoom : FitZoom(_fit), keepPlace: false);
        GoTo(keep);
    }

    private void ToggleStrip() => _stripBox.Visibility = _stripBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    // ---------- tools ----------
    private void Reduce()
    {
        if (_path == null || _pdf == null) return;
        new PdfReduceWindow(_path, _pdf.Password) { Owner = this }.ShowDialog();
    }

    private void PageSize()
    {
        if (_path == null || _pdf == null) return;
        if (_dirty) { UMessage.Show(this, "You have changes that are not saved yet. Save them first (Ctrl+S): the copy with the new page size is made from the saved file.", "Page size"); return; }
        new PdfResizeWindow(_path, _pdf.Password) { Owner = this }.ShowDialog();
    }

    private void SaveCopy()
    {
        if (_path == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save a copy of the PDF", Filter = "PDF|*.pdf",
            FileName = System.IO.Path.GetFileNameWithoutExtension(_path) + " (copy).pdf",
            InitialDirectory = System.IO.Path.GetDirectoryName(_path),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            if (string.Equals(System.IO.Path.GetFullPath(dlg.FileName), _path, StringComparison.OrdinalIgnoreCase)) return;
            File.Copy(_path, dlg.FileName, overwrite: true);
            Toast("Saved " + System.IO.Path.GetFileName(dlg.FileName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Toast("Couldn't save it: " + e.Message); }
    }

    private void ShowInFolder()
    {
        if (_path == null) return;
        try { Process.Start("explorer.exe", $"/select,\"{_path}\""); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
    }

    private void Print()
    {
        if (_pdf == null || _path == null) return;
        if (new PdfPrintWindow(_pdf, _current, System.IO.Path.GetFileName(_path)) { Owner = this }.ShowDialog() == true) Toast("Sent to the printer");
    }

    private void Toast(string text)
    {
        _toast.Text = text; _toastBox.Visibility = Visibility.Visible;
        _toastTimer.Stop(); _toastTimer.Start();
    }

    // ---------- keyboard ----------
    private void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (EditorKey(e)) { e.Handled = true; return; }
        if (TextKey(e)) { e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox && !ctrl) return;            // (typing in the page box or in a text on the page)
        e.Handled = true;
        switch (e.Key)
        {
            case Key.O when ctrl: Browse(this); break;
            case Key.S when ctrl: if (_editing || _dirty) _ = SaveEditsAsync(saveAs: false); else SaveCopy(); break;      // (a filled-in form saves too)
            case Key.P when ctrl: Print(); break;
            case Key.R when ctrl: Rotate(); break;
            case Key.OemPlus or Key.Add when ctrl: ZoomBy(1.2); break;
            case Key.OemMinus or Key.Subtract when ctrl: ZoomBy(1 / 1.2); break;
            case Key.D0 or Key.NumPad0 when ctrl: SetFit(Fit.Page); break;
            case Key.D1 or Key.NumPad1 when ctrl: _fit = Fit.None; ApplyZoom(1, keepPlace: true); break;
            case Key.D2 or Key.NumPad2 when ctrl: SetFit(Fit.Width); break;
            case Key.F4: ToggleStrip(); break;
            case Key.Home when ctrl || !_scroll.IsKeyboardFocusWithin: GoTo(0); break;
            case Key.End when ctrl || !_scroll.IsKeyboardFocusWithin: GoTo(_pages.Count - 1); break;
            case Key.Right when _fit == Fit.Page: GoTo(_current + 1); break;
            case Key.Left when _fit == Fit.Page: GoTo(_current - 1); break;
            case Key.W when ctrl: Close(); break;
            default: e.Handled = false; break;
        }
    }
}
