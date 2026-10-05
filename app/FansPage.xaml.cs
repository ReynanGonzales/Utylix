using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace IdmClone;

/// <summary>Temperatures and fan speeds of this PC, and control of the fans (through the administrator helper).</summary>
public partial class FansPage : UserControl
{
    private sealed class Card
    {
        public required FanSensor Control;
        public FanSensor? Fan;
        public required TextBlock Rpm, Now;
        public required Slider Slider;
        public required TextBlock SliderValue;
        public required StackPanel FixedPanel, CurvePanel;
        public required TextBox Points;
        public required RadioButton Auto, Fixed, Curve, Cpu, Gpu;
        public RotateTransform? Spin;
        public double LastRpm;
    }

    private readonly FanSettings _settings = FanSettings.Load();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, Card> _cards = new();
    private readonly Dictionary<string, TempTile> _tempTiles = new();
    private readonly Dictionary<string, Queue<double>> _history = new();       // (the last 90 readings of each temperature, one a second)
    private const int HistoryLength = 90;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private List<FanSensor> _sensors = new();
    private string _layout = "";
    private bool _building, _busy;

    /// <summary>A place between the fan cards and the safety settings, for the dashboard to put something (the downloads).</summary>
    public Panel ExtraSlot => Extra;

    public FansPage()
    {
        InitializeComponent();
        FloorBox.Text = _settings.Floor.ToString("0", CultureInfo.InvariantCulture);
        CpuLimitBox.Text = _settings.Config.EmergencyCpu.ToString("0", CultureInfo.InvariantCulture);
        GpuLimitBox.Text = _settings.Config.EmergencyGpu.ToString("0", CultureInfo.InvariantCulture);
        UnusedBox.IsChecked = _settings.ShowUnused;
        AutoBox.IsChecked = _settings.AutoStart;
        _timer.Tick += async (_, _) => await TickAsync();
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await PushAsync(); };
        SetOff();
        Cards.SizeChanged += (_, _) => Relayout();
        _timer.Start();                                           // (the helper wants to hear from us every second, whether or not this tab is showing)
        _ = BringUpAsync(interactive: false);                     // fan control starts by itself when it has been set up before
    }

    // ---------- on / off ----------
    private void SetOff(string? problem = null)
    {
        StartBtn.Visibility = Visibility.Visible; StartBtn.IsEnabled = true;
        StopBtn.Visibility = Visibility.Collapsed;
        StatusText.Text = "Fan control is off: the PC controls its fans";
        Intro.Visibility = Visibility.Visible;
        SafetyCard.Visibility = Visibility.Collapsed;
        Temps.Children.Clear(); _tempTiles.Clear();                               // (only the memory tile stays: it does not need fan control)
        TempsTitle.Text = "Memory"; TempsCard.Visibility = Visibility.Visible;
        UpdateMemory();
        Cards.Children.Clear(); _cards.Clear(); _layout = "";
        ProblemText.Text = problem ?? "";
        ProblemText.Visibility = string.IsNullOrEmpty(problem) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await BringUpAsync(interactive: true);

    /// <summary>Starts the administrator helper if it is not running (Windows may ask), for the lighting section that shares it.</summary>
    public async Task<bool> EnsureHelperAsync()
    {
        await BringUpAsync(interactive: true);
        return FanSettings.Client.Connected;
    }

    /// <summary>
    /// Gets the helper running and connected. "interactive" = the person pressed Start: Windows may be asked for permission. Otherwise
    /// (Utylix just started) it only starts the helper if that can be done without a prompt.
    /// </summary>
    private async Task BringUpAsync(bool interactive)
    {
        if (FanSettings.Client.Connected) return;
        if (!interactive && !_settings.AutoStart) return;
        if (await FanSettings.Client.ConnectAsync(400)) { await PushAsync(); await TickAsync(); return; }        // already running from before
        if (interactive) { StartBtn.IsEnabled = false; StatusText.Text = "Starting…"; }

        bool started = false;
        if (_settings.AutoStart)
        {
            bool ready = await FanTask.IsReadyAsync();
            if (!ready && interactive)
            {
                if (FanTask.WillLockFolder)
                {
                    var ok = UMessage.Ask(Window.GetWindow(this), $"To run fan control safely, Utylix's own folder ({System.IO.Path.GetDirectoryName(Environment.ProcessPath)}) will be locked: afterwards only administrators can change what is in it, like the Program Files folder. Other users can still run Utylix.\n\nThat is what lets the fan helper run as administrator from here without a second copy. Updates of Utylix will ask for Windows' permission.\n\nWindows will ask once now.",
                        "Fan control", MessageBoxImage.Question, MessageBoxResult.Yes, MessageBoxResult.Cancel, ("Lock the folder and continue", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.Cancel));
                    if (ok != MessageBoxResult.Yes) { SetOff("Fan control was not started."); return; }
                }
                StatusText.Text = "Waiting for Windows' permission (needed once)…";
                if (!await FanTask.InstallWithPromptAsync())
                {
                    SetOff("Windows' permission was not given (or the setup failed), so the automatic start was not set up. You can untick \"Start fan control by itself\" below to start it with a prompt each time instead.");
                    return;
                }
                ready = await FanTask.IsReadyAsync();
            }
            if (ready) started = FanTask.RunNow();
            else if (!interactive) return;                            // not set up (or out of date after an update): press Start once
        }
        if (!started)
        {
            if (!interactive) return;
            StatusText.Text = "Waiting for Windows' permission…";
            if (!FanClient.StartHelper()) { SetOff("Windows' permission was not given, so fan control did not start."); return; }
        }
        if (interactive) StatusText.Text = "Starting… (reading the hardware takes a few seconds)";
        bool connected = await FanSettings.Client.ConnectAsync(started ? 15000 : 25000);
        if (!connected && started)
        {
            // started through the task, yet nobody answers: a helper left over from before an update may still hold the one connection
            // (and "run only one copy" makes the new start do nothing). End it once and start a fresh one.
            if (interactive) StatusText.Text = "Restarting the fan helper…";
            await Task.Run(FanTask.EndStale);
            await Task.Delay(1500);
            if (await Task.Run(FanTask.RunNow)) connected = await FanSettings.Client.ConnectAsync(25000);
        }
        if (!connected)
        {
            if (interactive) SetOff("The fan helper did not start. If an antivirus blocked it, allow Utylix; details are in fan-helper.log in Utylix's data folder.");
            return;
        }
        await PushAsync();
        await TickAsync();
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        await FanSettings.Client.StopAsync();
        SetOff();
    }

    // ---------- reading ----------
    private async Task TickAsync()
    {
        UpdateMemory();
        if (_busy || !FanSettings.Client.Connected) { if (!FanSettings.Client.Connected && _cards.Count > 0) SetOff("Lost contact with the fan helper. Every fan is back under the PC's own control."); return; }
        _busy = true;
        try
        {
            var reply = await FanSettings.Client.AskAsync(new FanRequest { Cmd = "snapshot" });
            if (reply?.Ok != true || reply.Sensors == null) { SetOff("Lost contact with the fan helper. Every fan is back under the PC's own control."); return; }
            _sensors = reply.Sensors;
            StartBtn.Visibility = Visibility.Collapsed; StopBtn.Visibility = Visibility.Visible;
            bool readable = _sensors.Any(s => s.Kind is "cpu" or "board");
            ProblemText.Text = readable ? "" : "Utylix could not read the processor or the motherboard. Those sensors need the free PawnIO driver (pawnio.eu, or in a terminal: winget install namazso.PawnIO). Install it, press Stop, then Start again.";
            ProblemText.Visibility = readable ? Visibility.Collapsed : Visibility.Visible;
            Intro.Visibility = readable ? Visibility.Collapsed : Visibility.Visible;
            TempsCard.Visibility = SafetyCard.Visibility = Visibility.Visible;
            TempsTitle.Text = "Temperatures and memory";
            StatusText.Text = reply.Emergency ? "Too hot: every controlled fan is at 100 %" : "Fan control is on";
            if (reply.Emergency) StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)); else StatusText.Live(TextBlock.ForegroundProperty, "MutedBrush");
            string layout = string.Join("|", _sensors.Where(s => s.Type == "control").Select(s => s.Id)) + "#" + _settings.ShowUnused + "#" + string.Join(",", _sensors.Where(s => s.Type == "fan" && (s.Value ?? 0) > 0).Select(s => s.Id));
            if (layout != _layout) { _layout = layout; Rebuild(); }
            Refresh();
        }
        finally { _busy = false; }
    }

    private static string Label(FanSensor s) => s.Kind switch { "cpu" => "Processor", "gpu" => "Graphics card", "board" => "Motherboard", _ => s.Hardware } + " " + s.Name.Replace("Temperature ", "");

    private FanSensor? FanOf(FanSensor control) => _sensors.FirstOrDefault(s => s.Type == "fan" && s.HardwareId == control.HardwareId && s.Name == control.Name);

    private string NameOf(FanSensor control) =>
        _settings.Names.TryGetValue(control.Id, out var n) && n.Length > 0 ? n
        : control.Kind == "gpu" ? "Graphics card fan"
        : "Fan header " + new string(control.Name.Where(char.IsDigit).ToArray());

    private FanSensor? SourceId(string which) =>
        which == "gpu" ? _sensors.FirstOrDefault(s => s.Type == "temp" && s.Kind == "gpu" && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                       : _sensors.FirstOrDefault(s => s.Type == "temp" && s.Kind == "cpu" && (s.Name.Contains("Tctl") || s.Name.Contains("Package") || s.Name.Contains("Tdie")))
                         ?? _sensors.FirstOrDefault(s => s.Type == "temp" && s.Kind == "cpu");

    // ---------- the screen ----------
    private void Rebuild()
    {
        _building = true;
        Temps.Children.Clear(); Cards.Children.Clear(); _cards.Clear();
        _tempTiles.Clear();
        foreach (var t in ShownTemps())
        {
            var tile = BuildTempTile(t);
            _tempTiles[t.Id] = tile;
            Temps.Children.Add(tile.Frame);
        }
        int index = 0;
        foreach (var control in _sensors.Where(s => s.Type == "control"))
        {
            var fan = FanOf(control);
            if (!_settings.ShowUnused && (fan?.Value ?? 0) <= 0 && control.Kind != "gpu") continue;
            var card = BuildCard(control, fan, index++);
            _cards[control.Id] = card;
        }
        Relayout();
        if (_cards.Count == 0)
            Cards.Children.Add(new TextBlock { Text = "No fan that can be set was found. Reading the temperatures still works.", Margin = new Thickness(0, 10, 0, 0) }.Live(TextBlock.ForegroundProperty, "MutedBrush"));
        UpdateMemory();                                                              // (Clear() above took the memory tile out: it goes back after the temperatures)
        _building = false;
    }

    // ---------- the temperature tiles ----------
    private sealed class TempTile
    {
        public Border Frame = null!, Bar = null!;
        public TextBlock Value = null!, Hint = null!;
        public ColumnDefinition Fill = null!, Rest = null!;
        public System.Windows.Shapes.Polyline Line = null!;
        public System.Windows.Shapes.Path Icon = null!;
        public TextBlock Name = null!;
    }

    /// <summary>A small line drawing of what is measured (24 x 24 units): a processor with its pins, a graphics card with two fans, a motherboard.</summary>
    private static System.Windows.Shapes.Path SensorIcon(string kind)
    {
        string d = kind switch
        {
            "cpu" => "M6,6 H18 V18 H6 Z M9.5,9.5 H14.5 V14.5 H9.5 Z M9,3 V6 M12,3 V6 M15,3 V6 M9,18 V21 M12,18 V21 M15,18 V21 M3,9 H6 M3,12 H6 M3,15 H6 M18,9 H21 M18,12 H21 M18,15 H21",
            "gpu" => "M2,6.5 H22 V16.5 H2 Z M5.3,11.5 A3.2,3.2 0 1 0 11.7,11.5 A3.2,3.2 0 1 0 5.3,11.5 M12.3,11.5 A3.2,3.2 0 1 0 18.7,11.5 A3.2,3.2 0 1 0 12.3,11.5 M5,16.5 V19 M8,16.5 V19 M11,16.5 V19 M14,16.5 V19",
            "board" => "M3,3 H21 V21 H3 Z M6.5,6.5 H12 V12 H6.5 Z M15,6 V13 M18,6 V13 M6.5,16 H17.5 M6.5,18.5 H12",
            "ram" => "M2,7 H22 V16 H2 Z M5,10 H8 V13 H5 Z M10.5,10 H13.5 V13 H10.5 Z M16,10 H19 V13 H16 Z M4,16 V18.5 M7,16 V18.5 M10,16 V18.5 M14,16 V18.5 M17,16 V18.5 M20,16 V18.5",
            _ => "M12,3 V12 L17,15 M12,21 A9,9 0 1 1 12,3 A9,9 0 0 1 12,21",
        };
        return new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(d), Width = 28, Height = 28, Stretch = Stretch.Uniform, StrokeThickness = 1.5, Fill = Brushes.Transparent,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
        };
    }

    private const double SparkWidth = 180, SparkHeight = 28;

    // ---------- memory (RAM): read in this program, no administrator permission needed ----------
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    private TempTile? _memTile;
    private string _ramSpec = "";                 // "DDR4 · 3600 MHz", read once from Windows (WMI) in the background
    private bool _ramSpecAsked;

    /// <summary>The kind and speed of the installed memory sticks, like "DDR4 · 3600 MHz" (empty when Windows doesn't say).</summary>
    private static string ReadRamSpec()
    {
        try
        {
            var speeds = new List<uint>();
            uint type = 0;
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT Speed, ConfiguredClockSpeed, SMBIOSMemoryType FROM Win32_PhysicalMemory");
            foreach (System.Management.ManagementObject m in searcher.Get())
            {
                uint speed = m["ConfiguredClockSpeed"] is { } c ? Convert.ToUInt32(c, CultureInfo.InvariantCulture) : 0;     // (what the sticks really run at)
                if (speed == 0 && m["Speed"] is { } rated) speed = Convert.ToUInt32(rated, CultureInfo.InvariantCulture);
                if (speed > 0) speeds.Add(speed);
                if (m["SMBIOSMemoryType"] is { } t) type = Convert.ToUInt32(t, CultureInfo.InvariantCulture);
            }
            string kind = type switch { 20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5", _ => "" };
            string mhz = speeds.Count > 0 ? speeds.Max().ToString(CultureInfo.InvariantCulture) + " MHz" : "";
            return string.Join(" · ", new[] { kind, mhz }.Where(x => x.Length > 0));
        }
        catch (Exception e) when (e is System.Management.ManagementException or InvalidCastException or FormatException or OverflowException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return "";
        }
    }

    private static (Color Color, string Word) MemoryHeat(double percent) =>
        percent < 60 ? (Color.FromRgb(0x2E, 0xB8, 0x7A), "Plenty free")
        : percent < 80 ? (Color.FromRgb(0xE0, 0xA3, 0x2B), "Busy")
        : percent < 90 ? (Color.FromRgb(0xE8, 0x74, 0x2F), "Low free")
        : (Color.FromRgb(0xE5, 0x48, 0x4D), "Almost full");

    /// <summary>The memory tile: how much of the RAM is in use. It is shown with or without fan control (it needs nothing from the helper).</summary>
    private void UpdateMemory()
    {
        var status = new MemoryStatus { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) return;
        double total = status.TotalPhys / 1073741824.0, used = total - status.AvailPhys / 1073741824.0, percent = Math.Clamp(used / total * 100, 0, 100);
        _memTile ??= BuildTile("MEMORY", "RAM in use", "ram", "%");
        if (_memTile.Frame.Parent == null) Temps.Children.Add(_memTile.Frame);
        if (!_ramSpecAsked)
        {
            _ramSpecAsked = true;
            _ = Task.Run(ReadRamSpec).ContinueWith(t => Dispatcher.BeginInvoke(() => { _ramSpec = t.Result; if (_memTile != null && _ramSpec.Length > 0) _memTile.Name.Text = _ramSpec; }), TaskContinuationOptions.OnlyOnRanToCompletion);
        }
        _memTile.Name.Text = _ramSpec.Length > 0 ? _ramSpec : "RAM in use";
        string gigabytes = $"{used.ToString("0.0", CultureInfo.InvariantCulture)} / {total.ToString("0.0", CultureInfo.InvariantCulture)} GB";
        _memTile.Frame.ToolTip = $"{gigabytes} in use ({MemoryHeat(percent).Word})";
        UpdateTile("ram", _memTile, percent, p => (MemoryHeat(p).Color, gigabytes));        // (beside the number: how much, in GB)
    }

    /// <summary>How hot a reading is: the colour and the word that go with it (below 55 cool, 70 warm, 82 hot, then very hot).</summary>
    private static (Color Color, string Word) Heat(double c) =>
        c < 55 ? (Color.FromRgb(0x2E, 0xB8, 0x7A), "Cool")
        : c < 70 ? (Color.FromRgb(0xE0, 0xA3, 0x2B), "Warm")
        : c < 82 ? (Color.FromRgb(0xE8, 0x74, 0x2F), "Hot")
        : (Color.FromRgb(0xE5, 0x48, 0x4D), "Very hot");

    /// <summary>One temperature as a tile: what it is, the big number in the colour of its heat, a gauge and the last minute and a half as a small line.</summary>
    private TempTile BuildTempTile(FanSensor t)
    {
        string kind = t.Kind switch { "cpu" => "PROCESSOR", "gpu" => "GRAPHICS CARD", "board" => "MOTHERBOARD", _ => (t.Hardware ?? "").ToUpperInvariant() };
        string name = t.Name.Replace("Temperature ", "").Trim();
        if (name.StartsWith('#')) name = "Sensor " + name;
        var tile = BuildTile(kind, name, t.Kind, "°C");
        UpdateTile(t.Id, tile, t.Value, Heat);
        return tile;
    }

    /// <summary>The tile itself (also used for the memory): small heading, big number with its unit, the heat word, a gauge and the line of the last readings.</summary>
    private TempTile BuildTile(string kind, string name, string iconKind, string unit)
    {
        var tile = new TempTile();
        var stack = new StackPanel();
        var heading = new StackPanel { Margin = new Thickness(0, 0, 36, 0) };
        heading.Children.Add(new TextBlock { Text = kind, FontSize = 10.5, FontWeight = FontWeights.SemiBold }.Live(TextBlock.ForegroundProperty, "MutedBrush"));
        tile.Name = new TextBlock { Text = name, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) }.Live(TextBlock.ForegroundProperty, "MutedBrush");
        heading.Children.Add(tile.Name);
        tile.Icon = SensorIcon(iconKind);
        tile.Icon.Live(System.Windows.Shapes.Shape.StrokeProperty, "MutedBrush");
        var top = new Grid();
        top.Children.Add(heading); top.Children.Add(tile.Icon);
        stack.Children.Add(top);

        var number = new StackPanel { Orientation = Orientation.Horizontal };
        tile.Value = new TextBlock { Text = "–", FontSize = 32, FontWeight = FontWeights.SemiBold, LineHeight = 38 };
        number.Children.Add(tile.Value);
        number.Children.Add(new TextBlock { Text = unit, FontSize = 14, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3, 0, 0, 6) }.Live(TextBlock.ForegroundProperty, "MutedBrush"));
        tile.Hint = new TextBlock { FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 7) };
        var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(number); row.Children.Add(tile.Hint);
        stack.Children.Add(row);

        // the gauge: 0 to 100 degrees
        var gauge = new Grid { Height = 5, Margin = new Thickness(0, 6, 0, 0) };
        gauge.Children.Add(new Border { CornerRadius = new CornerRadius(2.5) }.Live(Border.BackgroundProperty, "LineBrush"));
        var fill = new Grid();
        tile.Fill = new ColumnDefinition { Width = new GridLength(0.001, GridUnitType.Star) };
        tile.Rest = new ColumnDefinition { Width = new GridLength(100, GridUnitType.Star) };
        fill.ColumnDefinitions.Add(tile.Fill); fill.ColumnDefinitions.Add(tile.Rest);
        tile.Bar = new Border { CornerRadius = new CornerRadius(2.5) };
        fill.Children.Add(tile.Bar);
        gauge.Children.Add(fill);
        stack.Children.Add(gauge);

        // the last readings
        tile.Line = new System.Windows.Shapes.Polyline { StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round, Opacity = 0.95 };
        var spark = new Canvas { Width = SparkWidth, Height = SparkHeight, Margin = new Thickness(0, 10, 0, 0), ClipToBounds = true, ToolTip = "The last minute and a half" };
        spark.Children.Add(tile.Line);
        stack.Children.Add(spark);

        tile.Frame = new Border
        {
            Child = stack, Width = SparkWidth + 30, Padding = new Thickness(14, 11, 14, 11), Margin = new Thickness(0, 0, 10, 10),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
        };
        tile.Frame.Live(Border.BackgroundProperty, "BgBrush").Live(Border.BorderBrushProperty, "LineBrush");
        return tile;
    }

    private void UpdateTile(string id, TempTile tile, double? value, Func<double, (Color Color, string Word)> heat)
    {
        if (value is not double v) { tile.Value.Text = "–"; tile.Hint.Text = ""; return; }
        var (color, word) = heat(v);
        var brush = new SolidColorBrush(color);
        tile.Value.Text = v.ToString("0", CultureInfo.InvariantCulture);
        tile.Value.Foreground = brush; tile.Bar.Background = brush; tile.Line.Stroke = brush; tile.Icon.Stroke = brush;
        tile.Hint.Text = word; tile.Hint.Foreground = brush;
        double share = Math.Clamp(v, 1, 100);
        tile.Fill.Width = new GridLength(share, GridUnitType.Star); tile.Rest.Width = new GridLength(100 - share + 0.001, GridUnitType.Star);

        if (!_history.TryGetValue(id, out var history)) _history[id] = history = new Queue<double>();
        history.Enqueue(v);
        while (history.Count > HistoryLength) history.Dequeue();
        if (history.Count < 2) { tile.Line.Points = new PointCollection(); return; }
        double lo = Math.Min(history.Min(), v - 4), hi = Math.Max(history.Max(), lo + 14);         // (a flat line stays flat: the scale is never narrower than 14 degrees)
        double step = SparkWidth / (HistoryLength - 1);
        var points = new PointCollection();
        int i = 0, n = history.Count;
        foreach (double h in history)
        {
            points.Add(new Point(SparkWidth - (n - 1 - i) * step, SparkHeight - 2 - (h - lo) / (hi - lo) * (SparkHeight - 4)));
            i++;
        }
        tile.Line.Points = points;
    }

    /// <summary>A little fan (three blades) that the card turns at the speed of the real one.</summary>
    private FrameworkElement FanIcon(out RotateTransform spin)
    {
        spin = new RotateTransform(0, 18, 18);
        var canvas = new Canvas { Width = 36, Height = 36, RenderTransform = spin, ToolTip = "Turns as fast as the fan" };
        var blade = Geometry.Parse("M18,18 C14,8 20,1 27,3 C31,10 26,16 18,18 Z");
        for (int i = 0; i < 3; i++)
            canvas.Children.Add(new System.Windows.Shapes.Path { Data = blade, RenderTransform = new RotateTransform(i * 120, 18, 18) }.Live(System.Windows.Shapes.Shape.FillProperty, "AccentBrush"));
        var hub = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, StrokeThickness = 1.5 }.Live(System.Windows.Shapes.Shape.FillProperty, "CardBrush").Live(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
        Canvas.SetLeft(hub, 13.5); Canvas.SetTop(hub, 13.5);
        canvas.Children.Add(hub);
        return new Border { Width = 36, Height = 36, Child = canvas, Background = Brushes.Transparent };
    }

    /// <summary>Two cards per row (one when the page is narrow).</summary>
    private void Relayout()
    {
        double w = Cards.ActualWidth;
        if (w <= 0) return;
        double each = w >= 700 ? Math.Floor(w / 2) - 12 : w - 12;
        foreach (FrameworkElement card in Cards.Children.OfType<Border>()) card.Width = Math.Max(250, each);
    }

    /// <summary>The few temperatures worth looking at: the processor, the graphics card, and one board reading that looks believable.</summary>
    private IEnumerable<FanSensor> ShownTemps()
    {
        var temps = _sensors.Where(s => s.Type == "temp").ToList();
        if (_settings.ShowUnused) return temps;
        var list = new List<FanSensor>();
        if (SourceId("cpu") is { } cpu) list.Add(cpu);
        if (SourceId("gpu") is { } gpu) list.Add(gpu);
        list.AddRange(temps.Where(s => s.Kind == "board" && (s.Value ?? 0) is > 15 and < 90).Take(1));
        return list;
    }

    private Card BuildCard(FanSensor control, FanSensor? fan, int index)
    {
        var rule = _settings.Config.Rules.Find(r => r.Control == control.Id);
        string mode = rule?.Mode ?? "auto";
        if (rule != null && rule.Points.Count == 0) rule.Points = FanSettings.Presets[1].Points.Select(p => (double[])p.Clone()).ToList();

        var border = new Border { Style = (Style)FindResource("Section"), Margin = new Thickness(0, 12, 12, 0), Padding = new Thickness(16, 12, 16, 14) };
        var root = new StackPanel();
        border.Child = root;
        AutomationProperties.SetAutomationId(border, "FanCard" + index);

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = FanIcon(out var spin);
        icon.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(icon, 0);
        var name = new TextBlock { Text = NameOf(control), FontWeight = FontWeights.SemiBold, FontSize = 15, Cursor = Cursors.Hand, ToolTip = "Double-click to rename" };
        AutomationProperties.SetAutomationId(name, "FanName" + index);
        name.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;
            string? n = TextPrompt.Ask(Window.GetWindow(this)!, "Name this fan", "What is this fan? (for example: CPU fan, front fan)", NameOf(control));
            if (n == null) return;
            _settings.Names[control.Id] = n.Trim(); _settings.Save(); name.Text = NameOf(control);
        };
        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(right, 2);
        var rpm = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
        var now = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right }.Live(TextBlock.ForegroundProperty, "MutedBrush");
        AutomationProperties.SetAutomationId(rpm, "FanRpm" + index);
        AutomationProperties.SetAutomationId(now, "FanNow" + index);
        right.Children.Add(rpm); right.Children.Add(now);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(name);
        titles.Children.Add(new TextBlock { Text = control.Hardware, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis }.Live(TextBlock.ForegroundProperty, "MutedBrush"));
        Grid.SetColumn(titles, 1);
        head.Children.Add(icon); head.Children.Add(titles); head.Children.Add(right);
        root.Children.Add(head);

        var chips = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        RadioButton Chip(string text, string id, bool on, string group)
        {
            var c = new RadioButton { Content = text, GroupName = group + index, Style = (Style)FindResource("ChipButton"), IsChecked = on };
            AutomationProperties.SetAutomationId(c, id + index);
            chips.Children.Add(c);
            return c;
        }
        var auto = Chip("Automatic (the PC decides)", "FanModeAuto", mode == "auto", "mode");
        var fixedChip = Chip("Fixed speed", "FanModeFixed", mode == "manual", "mode");
        var curveChip = Chip("Curve", "FanModeCurve", mode == "curve", "mode");
        root.Children.Add(chips);

        var fixedPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Visibility = mode == "manual" ? Visibility.Visible : Visibility.Collapsed };
        double floor = Math.Max(control.Min, _settings.Floor);
        var slider = new Slider { Minimum = floor, Maximum = Math.Max(floor + 1, control.Max), Width = 250, Value = Math.Clamp(rule?.Percent ?? 50, floor, 100), TickFrequency = 5, IsSnapToTickEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(slider, "FanSlider" + index);
        var sliderValue = new TextBlock { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 44 };
        fixedPanel.Children.Add(slider); fixedPanel.Children.Add(sliderValue);
        root.Children.Add(fixedPanel);

        var curvePanel = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Visibility = mode == "curve" ? Visibility.Visible : Visibility.Collapsed };
        var srcRow = new WrapPanel();
        srcRow.Children.Add(new TextBlock { Text = "Follow the temperature of", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        string currentSource = rule != null && _sensors.FirstOrDefault(s => s.Id == rule.Source) is { } cs ? cs.Kind : (control.Kind == "gpu" ? "gpu" : "cpu");
        var cpu = new RadioButton { Content = "the processor", GroupName = "src" + index, Style = (Style)FindResource("ChipButton"), IsChecked = currentSource != "gpu" };
        var gpu = new RadioButton { Content = "the graphics card", GroupName = "src" + index, Style = (Style)FindResource("ChipButton"), IsChecked = currentSource == "gpu" };
        AutomationProperties.SetAutomationId(cpu, "FanSrcCpu" + index); AutomationProperties.SetAutomationId(gpu, "FanSrcGpu" + index);
        srcRow.Children.Add(cpu); srcRow.Children.Add(gpu);
        curvePanel.Children.Add(srcRow);
        var presetRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        presetRow.Children.Add(new TextBlock { Text = "Start from", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        var points = new TextBox { Style = (Style)FindResource("Field"), Margin = new Thickness(0, 8, 0, 0), MaxWidth = 520 };
        AutomationProperties.SetAutomationId(points, "FanPoints" + index);
        curvePanel.Children.Add(presetRow);
        curvePanel.Children.Add(new TextBlock { Text = "Curve: temperature in °C : fan speed in %, separated by commas", FontSize = 12, Margin = new Thickness(0, 10, 0, 0) }.Live(TextBlock.ForegroundProperty, "MutedBrush"));
        curvePanel.Children.Add(points);
        root.Children.Add(curvePanel);

        var card = new Card { Control = control, Fan = fan, Rpm = rpm, Now = now, Slider = slider, SliderValue = sliderValue, FixedPanel = fixedPanel, CurvePanel = curvePanel, Spin = spin, Points = points, Auto = auto, Fixed = fixedChip, Curve = curveChip, Cpu = cpu, Gpu = gpu };
        points.Text = FormatPoints((rule?.Points.Count > 0 ? rule.Points : FanSettings.Presets[1].Points.Select(p => (double[])p.Clone()).ToList()));
        foreach (var (pname, ppoints) in FanSettings.Presets)
        {
            var b = new Button { Content = pname, Style = (Style)FindResource("DialogButton"), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
            var pp = ppoints;
            b.Click += (_, _) => { points.Text = FormatPoints(pp.Select(p => (double[])p.Clone()).ToList()); ApplyCard(card, index); };
            presetRow.Children.Add(b);
        }

        void Changed() { if (!_building) ApplyCard(card, index); }
        auto.Checked += (_, _) => Changed(); fixedChip.Checked += (_, _) => Changed(); curveChip.Checked += (_, _) => Changed();
        cpu.Checked += (_, _) => Changed(); gpu.Checked += (_, _) => Changed();
        slider.ValueChanged += (_, _) => { sliderValue.Text = slider.Value.ToString("0", CultureInfo.InvariantCulture) + " %"; Changed(); };
        points.LostFocus += (_, _) => Changed();
        points.KeyDown += (_, e) => { if (e.Key == Key.Enter) Changed(); };
        sliderValue.Text = slider.Value.ToString("0", CultureInfo.InvariantCulture) + " %";
        Cards.Children.Add(border);
        return card;
    }

    private static string FormatPoints(List<double[]> points) => string.Join(", ", points.Select(p => p[0].ToString("0", CultureInfo.InvariantCulture) + ":" + p[1].ToString("0", CultureInfo.InvariantCulture)));

    private static List<double[]>? ParsePoints(string text)
    {
        var list = new List<double[]>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var xy = part.Split(':');
            if (xy.Length != 2 || !double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) || !double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return null;
            list.Add(new[] { t, p });
        }
        return list.Count >= 2 ? list : null;
    }

    /// <summary>The person changed something on a card: store it and tell the helper.</summary>
    private void ApplyCard(Card card, int index)
    {
        var rule = _settings.RuleFor(card.Control.Id);
        rule.Floor = _settings.Floor;
        rule.Mode = card.Curve.IsChecked == true ? "curve" : card.Fixed.IsChecked == true ? "manual" : "auto";
        rule.Percent = card.Slider.Value;
        var source = SourceId(card.Gpu.IsChecked == true ? "gpu" : "cpu");
        rule.Source = source?.Id ?? "";
        var parsed = ParsePoints(card.Points.Text);
        if (parsed != null) rule.Points = parsed; else card.Points.Text = FormatPoints(rule.Points);
        card.FixedPanel.Visibility = rule.Mode == "manual" ? Visibility.Visible : Visibility.Collapsed;
        card.CurvePanel.Visibility = rule.Mode == "curve" ? Visibility.Visible : Visibility.Collapsed;
        _settings.Save();
        _debounce.Stop(); _debounce.Start();                                   // (a slider sends many changes: one message after it settles)
    }

    private async Task PushAsync()
    {
        if (!FanSettings.Client.Connected) return;
        await FanSettings.Client.AskAsync(new FanRequest { Cmd = "config", Config = _settings.Config });
    }

    /// <summary>Updates the numbers on the screen without rebuilding it.</summary>
    private void Refresh()
    {
        foreach (var (id, tile) in _tempTiles)
            if (_sensors.FirstOrDefault(s => s.Id == id) is { } t) UpdateTile(id, tile, t.Value, Heat);
        foreach (var (id, card) in _cards)
        {
            var control = _sensors.FirstOrDefault(s => s.Id == id);
            var fan = control == null ? null : FanOf(control);
            card.Rpm.Text = fan?.Value is double rpm ? rpm.ToString("0", CultureInfo.InvariantCulture) + " RPM" : "– RPM";
            // the icon only turns while it can be seen (a hidden or minimized window would still use the graphics card for nothing)
            bool shown = IsVisible && Window.GetWindow(this)?.WindowState != WindowState.Minimized;
            double speed = shown ? fan?.Value ?? 0 : 0;
            if (card.Spin != null && Math.Abs(speed - card.LastRpm) > Math.Max(60, card.LastRpm * 0.15))        // the icon turns as fast as the fan (roughly)
            {
                card.LastRpm = speed;
                card.Spin.BeginAnimation(RotateTransform.AngleProperty, speed <= 0 ? null
                    : SlowFrames(new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(Math.Clamp(1200.0 / speed, 0.5, 6))) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever }));
            }
            card.Now.Text = control?.Value is double pct ? (control.Auto ? "PC decides, now " : "set, now ") + pct.ToString("0", CultureInfo.InvariantCulture) + " %" : "";
        }
    }

    /// <summary>A small icon does not need 60 pictures a second: 20 is smooth enough and costs the graphics card a third.</summary>
    private static System.Windows.Media.Animation.DoubleAnimation SlowFrames(System.Windows.Media.Animation.DoubleAnimation a)
    {
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(a, 20);
        return a;
    }

    // ---------- safety ----------
    private async void Safety_Changed(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(FloorBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var floor)) _settings.Floor = Math.Clamp(floor, 15, 100);
        if (double.TryParse(CpuLimitBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var cpu)) _settings.Config.EmergencyCpu = Math.Clamp(cpu, 60, 95);
        if (double.TryParse(GpuLimitBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gpu)) _settings.Config.EmergencyGpu = Math.Clamp(gpu, 60, 95);
        FloorBox.Text = _settings.Floor.ToString("0", CultureInfo.InvariantCulture);
        CpuLimitBox.Text = _settings.Config.EmergencyCpu.ToString("0", CultureInfo.InvariantCulture);
        GpuLimitBox.Text = _settings.Config.EmergencyGpu.ToString("0", CultureInfo.InvariantCulture);
        foreach (var r in _settings.Config.Rules) r.Floor = _settings.Floor;
        _settings.Save();
        await PushAsync();
    }

    private void Auto_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoStart = AutoBox.IsChecked == true;
        _settings.Save();
    }

    private async void RemoveTask_Click(object sender, RoutedEventArgs e)
    {
        if (UMessage.Show("Remove the automatic start of fan control from this PC?" + Environment.NewLine + Environment.NewLine + "The protected copy and the Windows task are deleted (Windows asks for permission). Fan control then works only when you press Start.", "Utylix", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await FanSettings.Client.StopAsync();
        SetOff();
        bool ok = await FanTask.RemoveWithPromptAsync();
        _settings.AutoStart = false; AutoBox.IsChecked = false; _settings.Save();
        UMessage.Show(ok ? "Removed. The fans are under the PC's own control." : "It could not be removed (permission refused?). The fans are under the PC's own control.", "Utylix");
    }

    private void Unused_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowUnused = UnusedBox.IsChecked == true;
        _settings.Save();
        _layout = "";                                                          // rebuilt on the next reading
    }
}
