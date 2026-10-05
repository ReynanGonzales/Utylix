using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IdmClone;

/// <summary>One picture in the strip of small pictures at the bottom (its small picture is made when it first scrolls into view).</summary>
public sealed class ThumbItem : INotifyPropertyChanged
{
    private static readonly SemaphoreSlim Gate = new(3, 3);
    private ImageSource? _source;
    private bool _started;
    public string Path { get; init; } = "";
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
            var picture = await Task.Run(() =>
            {
                try
                {
                    var b = new BitmapImage();
                    b.BeginInit();
                    b.UriSource = new Uri(Path);
                    b.DecodePixelHeight = 96;
                    b.CacheOption = BitmapCacheOption.OnLoad;
                    b.EndInit();
                    b.Freeze();
                    return (ImageSource)b;
                }
                catch (Exception e) when (e is IOException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException or COMException or FileFormatException) { return null; }
            });
            _source = picture;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Source)));
        }
        finally { Gate.Release(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// The Utylix photo viewer: a quick, dark window for pictures. Arrow keys go through the folder, the wheel zooms, drag moves, double-click
/// toggles fit / 100 %. R rotates, Space plays a slideshow, F11 is full screen, Delete sends the picture to the Recycle Bin.
/// </summary>
public sealed class ViewerWindow : Window
{
    public static readonly string[] Extensions =
    {
        "jpg", "jpeg", "jpe", "jfif", "png", "gif", "bmp", "dib", "tif", "tiff", "ico", "webp", "heic", "heif", "avif", "jxr", "wdp", "hdp",
    };

    public static bool IsViewable(string path) =>
        Extensions.Contains(System.IO.Path.GetExtension(path).TrimStart('.'), StringComparer.OrdinalIgnoreCase);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    // ---------- one viewer window; pictures that arrive later are shown in it ----------
    private static ViewerWindow? _main;
    public static int Count => _main == null ? 0 : 1;
    public static event Action? AnyClosed;

    public static void Open(IEnumerable<string> files)
    {
        var list = files.Where(f => !string.IsNullOrWhiteSpace(f) && File.Exists(f) && IsViewable(f)).ToList();
        if (list.Count == 0) { AnyClosed?.Invoke(); return; }
        if (_main == null) { _main = new ViewerWindow(); _main.Show(); }
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        WindowTheme.BringToFront(_main);
        _main.Load(list);
    }

    /// <summary>Asks for a picture (from the Dashboard / tray) and opens it.</summary>
    public static void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a picture",
            Filter = "Pictures|" + string.Join(";", Extensions.Select(e => "*." + e)) + "|All files|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() == true) Open(dlg.FileNames);
    }

    // ---------- state ----------
    private List<string> _files = new();
    private int _index = -1;
    private BitmapSource? _bmp;
    private int _rotation;                    // 0, 90, 180, 270 (degrees, clockwise)
    private double _zoom = 1, _tx, _ty;
    private bool _fit = true;
    private int _token;
    private readonly Dictionary<string, (BitmapSource Picture, int Rotation)> _cache = new();
    private readonly List<string> _cacheOrder = new();
    private readonly DispatcherTimer _slide = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private bool _full, _slideshow;
    private Point _dragFrom;
    private bool _dragging;
    private double _dragTx, _dragTy;
    private WindowState _beforeFull;
    private WindowStyle _styleBeforeFull;

    // ---------- parts ----------
    private readonly Grid _stage = new() { ClipToBounds = true, Background = Brushes.Transparent };
    private readonly Canvas _canvas = new();
    private readonly Image _image = new() { Stretch = Stretch.Fill, SnapsToDevicePixels = false, RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly RotateTransform _rotate = new();
    private readonly ScaleTransform _scale = new();
    private readonly TextBlock _title = new() { Foreground = Brushes.White, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _info = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xA7, 0xAE, 0xBF)), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
    private readonly TextBlock _message = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xD5, 0xE2)), FontSize = 15, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock _toast = new() { Foreground = Brushes.White, FontSize = 13 };
    private readonly Border _toastBox = new() { Background = new SolidColorBrush(Color.FromArgb(220, 20, 24, 34)), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 7, 14, 7), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 24), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ListBox _strip = new();
    private readonly ObservableCollection<ThumbItem> _thumbs = new();
    private readonly Border _top = new();
    private readonly Border _bottom = new();
    private readonly Border _stripBox = new() { Visibility = Visibility.Collapsed };
    private readonly Button _slideBtn;
    private readonly Button _prevEdge, _nextEdge;
    private bool _syncingStrip;

    private static readonly Brush Panel = new SolidColorBrush(Color.FromArgb(235, 22, 26, 36));

    private ViewerWindow()
    {
        Title = "Utylix Photos";
        Width = 1100; Height = 720; MinWidth = 520; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0F, 0x14));
        AllowDrop = true;
        FontFamily = new FontFamily("Segoe UI");
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/photos.ico")); } catch (Exception e) when (e is IOException or UriFormatException) { }
        WindowTheme.DarkTitleBar(this);
        WindowTheme.OwnTaskbarButton(this, "Utylix.Photos");
        SourceInitialized += (_, _) => { if (!App.IsDarkTheme) ForceDarkTitle(); };

        _image.RenderTransform = new TransformGroup { Children = { _rotate, _scale } };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _canvas.Margin = new Thickness(0, 38, 0, 0);                  // (the picture sits under the name bar, not behind it)
        _canvas.Children.Add(_image);
        _stage.Children.Add(_canvas);
        _stage.Children.Add(_message);
        _stage.Children.Add(_toastBox);
        _toastBox.Child = _toast;

        // ---- top bar: the name and facts ----
        var topRow = new DockPanel { Margin = new Thickness(16, 0, 16, 0) };
        DockPanel.SetDock(_info, Dock.Right);
        topRow.Children.Add(_info);
        topRow.Children.Add(_title);
        _top.Child = topRow;
        _top.Background = Panel; _top.Height = 38; _top.VerticalAlignment = VerticalAlignment.Top;

        // ---- edge arrows ----
        _prevEdge = EdgeButton("", HorizontalAlignment.Left, () => Step(-1), "Previous picture (Left arrow)");
        _nextEdge = EdgeButton("", HorizontalAlignment.Right, () => Step(1), "Next picture (Right arrow)");
        _stage.Children.Add(_prevEdge);
        _stage.Children.Add(_nextEdge);

        // ---- bottom tool bar ----
        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        Button Tool(string glyph, string tip, Action action, string id)
        {
            var b = new Button { Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 17 }, ToolTip = tip, Template = ToolTemplate(), Margin = new Thickness(2, 0, 2, 0), Foreground = Brushes.White, Focusable = false };
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, id);
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            b.Click += (_, _) => action();
            tools.Children.Add(b);
            return b;
        }
        Tool("", "Previous (Left arrow)", () => Step(-1), "ViewPrev");
        Tool("", "Next (Right arrow)", () => Step(1), "ViewNext");
        Gap(tools);
        Tool("", "Zoom out (-)", () => ZoomBy(1 / 1.25), "ViewZoomOut");
        Tool("", "Zoom in (+)", () => ZoomBy(1.25), "ViewZoomIn");
        Tool("", "Fit to the window (0)", FitNow, "ViewFit");
        Tool("", "Actual size, 100 % (1)", () => SetZoom(1, null), "ViewActual");
        Gap(tools);
        Tool("", "Rotate left (Shift+R)", () => Rotate(-90), "ViewRotateLeft");
        Tool("", "Rotate right (R)", () => Rotate(90), "ViewRotateRight");
        _slideBtn = Tool("", "Slideshow (Space)", ToggleSlideshow, "ViewSlideshow");
        Tool("", "Small pictures strip (T)", ToggleStrip, "ViewStrip");
        Tool("", "Full screen (F11)", ToggleFull, "ViewFull");
        Gap(tools);
        Tool("", "Copy the picture (Ctrl+C)", CopyPicture, "ViewCopy");
        Tool("", "Show in its folder", ShowInFolder, "ViewFolder");
        Tool("", "Move to the Recycle Bin (Delete)", DeleteCurrent, "ViewDelete");
        _bottom.Child = tools;
        _bottom.Background = Panel; _bottom.Padding = new Thickness(10, 7, 10, 7);

        // ---- strip of small pictures ----
        _strip.ItemsSource = _thumbs;
        _strip.Background = Brushes.Transparent;
        _strip.BorderThickness = new Thickness(0);
        _strip.Height = 80;
        ScrollViewer.SetHorizontalScrollBarVisibility(_strip, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_strip, ScrollBarVisibility.Disabled);
        var panel = new FrameworkElementFactory(typeof(VirtualizingStackPanel));
        panel.SetValue(VirtualizingStackPanel.OrientationProperty, Orientation.Horizontal);
        _strip.ItemsPanel = new ItemsPanelTemplate(panel);
        _strip.ItemTemplate = (DataTemplate)XamlReader.Parse(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Border Width='84' Height='64' Background='#1A1F2B'><Image Source='{Binding Source}' Stretch='UniformToFill' /></Border></DataTemplate>");
        _strip.ItemContainerStyle = (Style)XamlReader.Parse(
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListBoxItem'><Setter Property='Margin' Value='3,4' /><Setter Property='Template'><Setter.Value>" +
            "<ControlTemplate TargetType='ListBoxItem'><Border x:Name='bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' BorderThickness='2' BorderBrush='Transparent' CornerRadius='4' Padding='0'><ContentPresenter /></Border>" +
            "<ControlTemplate.Triggers><Trigger Property='IsSelected' Value='True'><Setter TargetName='bd' Property='BorderBrush' Value='#5B8DEF' /></Trigger>" +
            "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='BorderBrush' Value='#8FA9D9' /></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>");
        _strip.SelectionChanged += (_, _) =>
        {
            if (_syncingStrip || _strip.SelectedIndex < 0 || _strip.SelectedIndex == _index) return;
            _ = Show(_strip.SelectedIndex);
        };
        _stripBox.Child = _strip;
        _stripBox.Background = Panel;

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_stage, 0); Grid.SetRow(_stripBox, 1); Grid.SetRow(_bottom, 2);
        layout.Children.Add(_stage); layout.Children.Add(_stripBox); layout.Children.Add(_bottom);
        _stage.Children.Add(_top);
        Content = layout;

        // ---- context menu ----
        var menu = new ContextMenu();
        void Item(string text, Action a) { var m = new MenuItem { Header = text }; m.Click += (_, _) => a(); menu.Items.Add(m); }
        Item("Copy", CopyPicture); Item("Show in its folder", ShowInFolder); Item("Rotate right", () => Rotate(90)); Item("Rotate left", () => Rotate(-90));
        Item("Move to the Recycle Bin", DeleteCurrent);
        _stage.ContextMenu = menu;

        // ---- input ----
        _canvas.SizeChanged += (_, _) => { if (_fit) FitNow(); else Apply(); };
        _stage.MouseWheel += OnWheel;
        _stage.MouseLeftButtonDown += OnDown;
        _stage.MouseMove += OnMove;
        _stage.MouseLeftButtonUp += (_, _) => { _dragging = false; _stage.ReleaseMouseCapture(); };
        _stage.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.XButton1) Step(-1); else if (e.ChangedButton == MouseButton.XButton2) Step(1); };
        PreviewKeyDown += OnKey;
        MouseMove += (_, _) => Reveal();
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped) Load(dropped.ToList()); };
        _slide.Tick += (_, _) => { if (_files.Count > 1) Step(1, wrap: true); };
        _hide.Tick += (_, _) => { _hide.Stop(); if (_full) SetBars(false); };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toastBox.Visibility = Visibility.Collapsed; };
        Closed += (_, _) => { _main = null; _slide.Stop(); AnyClosed?.Invoke(); };
    }

    private static void ForceDarkTitle()
    {
        // (the picture area is always dark, so is the title bar - done in WindowTheme only for dark Windows themes)
    }

    private static void Gap(StackPanel p) => p.Children.Add(new Border { Width = 1, Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), Margin = new Thickness(8, 4, 8, 4) });

    private static ControlTemplate ToolTemplate() => (ControlTemplate)XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
        "<Border x:Name='bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='Transparent' CornerRadius='6' Width='38' Height='34'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' /></Border>" +
        "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#33FFFFFF' /></Trigger>" +
        "<Trigger Property='IsPressed' Value='True'><Setter TargetName='bd' Property='Background' Value='#55FFFFFF' /></Trigger></ControlTemplate.Triggers></ControlTemplate>");

    private Button EdgeButton(string glyph, HorizontalAlignment side, Action action, string tip)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 22 }, ToolTip = tip, Foreground = Brushes.White, HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0), Opacity = 0, Focusable = false,
            Template = (ControlTemplate)XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
                "<Border x:Name='bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='#88000000' CornerRadius='24' Width='48' Height='48'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' /></Border>" +
                "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#CC3A4258' /></Trigger></ControlTemplate.Triggers></ControlTemplate>"),
        };
        b.Click += (_, _) => action();
        return b;
    }

    // ---------- loading ----------
    private void Load(List<string> files)
    {
        // one picture: show the others of its folder, in Explorer's order; several: just those
        if (files.Count == 1)
        {
            string path = System.IO.Path.GetFullPath(files[0]);
            string dir = System.IO.Path.GetDirectoryName(path) ?? "";
            var siblings = new List<string>();
            try { siblings = Directory.EnumerateFiles(dir).Where(IsViewable).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (!siblings.Contains(path, StringComparer.OrdinalIgnoreCase)) siblings.Add(path);
            siblings.Sort((a, b) => StrCmpLogicalW(System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b)));
            _files = siblings.Count <= 5000 ? siblings : new List<string> { path };
            _index = _files.FindIndex(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
        }
        else { _files = files.Select(System.IO.Path.GetFullPath).ToList(); _index = 0; }
        RebuildStrip();
        _ = Show(Math.Max(0, _index));
    }

    private void RebuildStrip()
    {
        _thumbs.Clear();
        foreach (var f in _files) _thumbs.Add(new ThumbItem { Path = f });
    }

    private void SyncStrip()
    {
        _syncingStrip = true;
        try { _strip.SelectedIndex = _index; if (_index >= 0 && _index < _thumbs.Count) _strip.ScrollIntoView(_thumbs[_index]); }
        finally { _syncingStrip = false; }
    }

    private async Task Show(int index)
    {
        if (_files.Count == 0) return;
        index = ((index % _files.Count) + _files.Count) % _files.Count;
        _index = index;
        string path = _files[index];
        int token = ++_token;
        _title.Text = System.IO.Path.GetFileName(path);
        Title = _title.Text + " - Utylix Photos";
        _info.Text = $"{index + 1} / {_files.Count}";
        SyncStrip();

        (BitmapSource Picture, int Rotation)? loaded;
        string? error = null;
        if (_cache.TryGetValue(path, out var cached)) loaded = cached;
        else
        {
            (loaded, error) = await Task.Run(() => Decode(path));
            if (token != _token) return;                      // another picture was asked for meanwhile
            if (loaded != null) Remember(path, loaded.Value);
        }
        if (loaded == null)
        {
            _bmp = null; _image.Source = null;
            _message.Text = error ?? "This picture can't be opened.";
            _message.Visibility = Visibility.Visible;
            return;
        }
        _message.Visibility = Visibility.Collapsed;
        _bmp = loaded.Value.Picture;
        _rotation = loaded.Value.Rotation;
        _image.Source = _bmp;
        _fit = true;
        FitNow();
        UpdateInfo();
        Prefetch(index + 1); Prefetch(index - 1);
    }

    private void Remember(string path, (BitmapSource, int) value)
    {
        _cache[path] = value; _cacheOrder.Remove(path); _cacheOrder.Add(path);
        while (_cacheOrder.Count > 4) { _cache.Remove(_cacheOrder[0]); _cacheOrder.RemoveAt(0); }
    }

    private void Prefetch(int index)
    {
        if (_files.Count < 2) return;
        index = ((index % _files.Count) + _files.Count) % _files.Count;
        string path = _files[index];
        if (_cache.ContainsKey(path)) return;
        _ = Task.Run(() => Decode(path)).ContinueWith(t =>
        {
            if (t.Result.Item1 is { } v) Dispatcher.BeginInvoke(() => Remember(path, v));
        }, TaskScheduler.Default);
    }

    /// <summary>Reads a picture (off the window's thread). Gives the picture and how it must be turned (from the photo's own orientation tag), or a message.</summary>
    private static ((BitmapSource, int)?, string?) Decode(string path)
    {
        try
        {
            int rotation = 0;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                if (frame.Metadata is BitmapMetadata meta && meta.GetQuery("/app1/ifd/{ushort=274}") is ushort o)
                    rotation = o switch { 3 or 4 => 180, 5 or 6 => 90, 7 or 8 => 270, _ => 0 };
            }
            catch (Exception e) when (e is NotSupportedException or IOException or InvalidOperationException or COMException or ArgumentException or FileFormatException) { }

            var b = new BitmapImage();
            b.BeginInit();
            b.UriSource = new Uri(path);
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            b.EndInit();
            if ((long)b.PixelWidth * b.PixelHeight > 120_000_000)           // a gigantic panorama: show a smaller copy rather than run out of memory
            {
                var small = new BitmapImage();
                small.BeginInit();
                small.UriSource = new Uri(path);
                small.DecodePixelWidth = 9000;
                small.CacheOption = BitmapCacheOption.OnLoad;
                small.EndInit();
                small.Freeze();
                return ((small, rotation), null);
            }
            b.Freeze();
            return ((b, rotation), null);
        }
        catch (Exception e) when (e is NotSupportedException or IOException or InvalidOperationException or COMException or FileFormatException or UnauthorizedAccessException or ArgumentException)
        {
            string ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            string hint = ext is "webp" ? "Windows needs its free \"WebP Image Extensions\" (Microsoft Store) for this type."
                : ext is "heic" or "heif" ? "Windows needs its \"HEIF Image Extensions\" (Microsoft Store) for this type."
                : ext is "avif" ? "Windows needs its free \"AV1 Video Extension\" and \"AVIF Image Extensions\" (Microsoft Store) for this type."
                : "The file may be damaged or not really a picture.";
            return (null, "Couldn't open \"" + System.IO.Path.GetFileName(path) + "\".\n" + hint);
        }
    }

    // ---------- showing ----------
    private Size ImageSize()
    {
        if (_bmp == null) return new Size(1, 1);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return new Size(_bmp.PixelWidth / dpi, _bmp.PixelHeight / dpi);
    }

    private Size Turned()
    {
        var s = ImageSize();
        return _rotation % 180 == 0 ? s : new Size(s.Height, s.Width);
    }

    private double FitZoom()
    {
        var t = Turned();
        double aw = Math.Max(50, _canvas.ActualWidth), ah = Math.Max(50, _canvas.ActualHeight);
        return Math.Min(1.0, Math.Min(aw / t.Width, ah / t.Height));                // big pictures shrink to fit; small ones stay as they are
    }

    private void FitNow() { _fit = true; _zoom = FitZoom(); _tx = _ty = 0; Apply(); }

    private void Apply()
    {
        if (_bmp == null) return;
        var s = ImageSize(); var t = Turned();
        double aw = _canvas.ActualWidth, ah = _canvas.ActualHeight;
        double sw = t.Width * _zoom, sh = t.Height * _zoom;
        double maxX = Math.Max(0, (sw - aw) / 2), maxY = Math.Max(0, (sh - ah) / 2);
        _tx = Math.Clamp(_tx, -maxX, maxX); _ty = Math.Clamp(_ty, -maxY, maxY);
        _image.Width = s.Width; _image.Height = s.Height;
        _rotate.Angle = _rotation;
        _scale.ScaleX = _scale.ScaleY = _zoom;
        Canvas.SetLeft(_image, aw / 2 - s.Width / 2 + _tx);
        Canvas.SetTop(_image, ah / 2 - s.Height / 2 + _ty);
        UpdateInfo();
    }

    private void UpdateInfo()
    {
        if (_bmp == null || _index < 0) return;
        string size = "";
        try { size = FormatBytes(new FileInfo(_files[_index]).Length); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        _info.Text = $"{_index + 1} / {_files.Count}   ·   {_bmp.PixelWidth} × {_bmp.PixelHeight}   ·   {size}   ·   {Math.Round(_zoom * 100)} %";
    }

    private static string FormatBytes(long n) => n >= 1048576 ? (n / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB" : (n / 1024.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " KB";

    // ---------- zoom and move ----------
    private void SetZoom(double zoom, Point? around)
    {
        if (_bmp == null) return;
        zoom = Math.Clamp(zoom, Math.Min(0.05, FitZoom()), 40);
        double k = zoom / _zoom;
        var c = new Point(_canvas.ActualWidth / 2, _canvas.ActualHeight / 2);
        var p = around ?? c;
        _tx = (p.X - c.X) - k * ((p.X - c.X) - _tx);
        _ty = (p.Y - c.Y) - k * ((p.Y - c.Y) - _ty);
        _zoom = zoom;
        _fit = Math.Abs(zoom - FitZoom()) < 0.001;
        Apply();
    }

    private void ZoomBy(double factor) => SetZoom(_zoom * factor, null);

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) { Step(e.Delta > 0 ? -1 : 1); return; }       // Shift + wheel: the next / previous picture
        SetZoom(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), e.GetPosition(_canvas));
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (_fit && FitZoom() < 1) SetZoom(1, e.GetPosition(_canvas)); else FitNow();
            return;
        }
        _dragging = true; _dragFrom = e.GetPosition(_stage); _dragTx = _tx; _dragTy = _ty;
        _stage.CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(_stage);
        _tx = _dragTx + (p.X - _dragFrom.X); _ty = _dragTy + (p.Y - _dragFrom.Y);
        Apply();
    }

    private void Rotate(int degrees)
    {
        if (_bmp == null) return;
        _rotation = ((_rotation + degrees) % 360 + 360) % 360;
        if (_fit) FitNow(); else Apply();
    }

    // ---------- going through the folder ----------
    private void Step(int by, bool wrap = false)
    {
        if (_files.Count == 0) return;
        int next = _index + by;
        if (!wrap && (next < 0 || next >= _files.Count)) { Toast(next < 0 ? "This is the first picture" : "This is the last picture"); return; }
        _ = Show(next);
    }

    // ---------- tools ----------
    private void ToggleSlideshow()
    {
        _slideshow = !_slideshow;
        if (_slideshow) _slide.Start(); else _slide.Stop();
        ((TextBlock)_slideBtn.Content).Text = _slideshow ? "" : "";
        Toast(_slideshow ? "Slideshow: every 3 seconds (Space stops it)" : "Slideshow stopped");
    }

    private void ToggleStrip()
    {
        _stripBox.Visibility = _stripBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (_stripBox.Visibility == Visibility.Visible) SyncStrip();
    }

    private void ToggleFull()
    {
        _full = !_full;
        if (_full)
        {
            _beforeFull = WindowState; _styleBeforeFull = WindowStyle;
            WindowStyle = WindowStyle.None; WindowState = WindowState.Normal; WindowState = WindowState.Maximized;
            _canvas.Margin = new Thickness(0);                           // full screen: the whole screen is the picture, the bars float over it
            Reveal();
        }
        else
        {
            WindowStyle = _styleBeforeFull; WindowState = _beforeFull;
            SetBars(true); _hide.Stop();
            _canvas.Margin = new Thickness(0, 38, 0, 0);
        }
    }

    private void SetBars(bool visible)
    {
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        _top.Visibility = _bottom.Visibility = v;
        _canvas.Margin = new Thickness(0, visible && !_full ? 38 : 0, 0, 0);
        if (!visible) _stripBox.Visibility = Visibility.Collapsed;
        Cursor = visible ? Cursors.Arrow : Cursors.None;
    }

    /// <summary>The mouse moved: show the arrows (and, in full screen, the bars again for a moment).</summary>
    private void Reveal()
    {
        if (_full) { SetBars(true); _hide.Stop(); _hide.Start(); }
        _prevEdge.Opacity = _nextEdge.Opacity = 0.9;
        _edgeFade ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _edgeFade.Tick -= EdgeFadeTick; _edgeFade.Tick += EdgeFadeTick;
        _edgeFade.Stop(); _edgeFade.Start();
    }

    private DispatcherTimer? _edgeFade;
    private void EdgeFadeTick(object? s, EventArgs e) { _edgeFade?.Stop(); _prevEdge.Opacity = _nextEdge.Opacity = 0; }

    private void CopyPicture()
    {
        if (_bmp == null) return;
        try { Clipboard.SetImage(_bmp); Toast("Copied"); }
        catch (Exception e) when (e is COMException or ExternalException) { Toast("The clipboard is busy, try again"); }
    }

    private void ShowInFolder()
    {
        if (_index < 0) return;
        try { Process.Start("explorer.exe", $"/select,\"{_files[_index]}\""); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private void DeleteCurrent()
    {
        if (_index < 0 || _index >= _files.Count) return;
        string path = _files[_index];
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException) { Toast("Couldn't delete it: " + e.Message); return; }
        _cache.Remove(path); _cacheOrder.Remove(path);
        _files.RemoveAt(_index);
        _thumbs.RemoveAt(_index);
        if (_files.Count == 0) { Close(); return; }
        Toast("Moved to the Recycle Bin");
        _ = Show(Math.Min(_index, _files.Count - 1));
    }

    private void Toast(string text)
    {
        _toast.Text = text; _toastBox.Visibility = Visibility.Visible;
        _toastTimer.Stop(); _toastTimer.Start();
    }

    // ---------- keyboard ----------
    private void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Right or Key.PageDown or Key.Down when !ctrl: Step(1); break;
            case Key.Left or Key.PageUp or Key.Up when !ctrl: Step(-1); break;
            case Key.Home: _ = Show(0); break;
            case Key.End: _ = Show(_files.Count - 1); break;
            case Key.Add or Key.OemPlus: ZoomBy(1.25); break;
            case Key.Subtract or Key.OemMinus: ZoomBy(1 / 1.25); break;
            case Key.D0 or Key.NumPad0: FitNow(); break;
            case Key.D1 or Key.NumPad1: SetZoom(1, null); break;
            case Key.R: Rotate(shift ? -90 : 90); break;
            case Key.Space: ToggleSlideshow(); break;
            case Key.T: ToggleStrip(); break;
            case Key.F11 or Key.F: ToggleFull(); break;
            case Key.Delete: DeleteCurrent(); break;
            case Key.C when ctrl: CopyPicture(); break;
            case Key.O when ctrl: Browse(); break;
            case Key.Escape: if (_full) ToggleFull(); else Close(); break;
            default: e.Handled = false; break;
        }
    }
}
