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
    }

    private readonly FanSettings _settings = FanSettings.Load();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, Card> _cards = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private List<FanSensor> _sensors = new();
    private string _layout = "";
    private bool _building, _busy;

    public FansPage()
    {
        InitializeComponent();
        FloorBox.Text = _settings.Floor.ToString("0", CultureInfo.InvariantCulture);
        CpuLimitBox.Text = _settings.Config.EmergencyCpu.ToString("0", CultureInfo.InvariantCulture);
        GpuLimitBox.Text = _settings.Config.EmergencyGpu.ToString("0", CultureInfo.InvariantCulture);
        UnusedBox.IsChecked = _settings.ShowUnused;
        _timer.Tick += async (_, _) => await TickAsync();
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await PushAsync(); };
        Loaded += async (_, _) =>
        {
            if (!FanSettings.Client.Connected && await FanSettings.Client.ConnectAsync(400)) { await PushAsync(); }     // a helper that is still running from before
            _timer.Start();
        };
        SetOff();
    }

    // ---------- on / off ----------
    private void SetOff(string? problem = null)
    {
        StartBtn.Visibility = Visibility.Visible; StartBtn.IsEnabled = true;
        StopBtn.Visibility = Visibility.Collapsed;
        StatusText.Text = "Fan control is off: the PC controls its fans";
        Intro.Visibility = Visibility.Visible;
        TempsCard.Visibility = SafetyCard.Visibility = Visibility.Collapsed;
        Cards.Children.Clear(); _cards.Clear(); _layout = "";
        ProblemText.Text = problem ?? "";
        ProblemText.Visibility = string.IsNullOrEmpty(problem) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        StartBtn.IsEnabled = false;
        StatusText.Text = "Waiting for Windows' permission…";
        if (!FanClient.StartHelper()) { SetOff("Windows' permission was not given, so fan control did not start."); return; }
        StatusText.Text = "Starting… (reading the hardware takes a few seconds)";
        if (!await FanSettings.Client.ConnectAsync(25000))
        {
            SetOff("The fan helper did not start. If an antivirus blocked it, allow Utylix; details are in fan-helper.log in Utylix's data folder.");
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
            StatusText.Text = reply.Emergency ? "Too hot: every controlled fan is at 100 %" : "Fan control is on";
            StatusText.Foreground = reply.Emergency ? new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)) : (Brush)FindResource("MutedBrush");
            string layout = string.Join("|", _sensors.Where(s => s.Type == "control").Select(s => s.Id)) + "#" + _settings.ShowUnused + "#" + string.Join(",", _sensors.Where(s => s.Type == "fan" && (s.Value ?? 0) > 0).Select(s => s.Id));
            if (layout != _layout) { _layout = layout; Rebuild(); }
            Refresh();
        }
        finally { _busy = false; }
    }

    private static string Label(FanSensor s) => s.Kind switch { "cpu" => "Processor", "gpu" => "Graphics card", "board" => "Board", _ => s.Hardware } + " " + s.Name;

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
        foreach (var t in _sensors.Where(s => s.Type == "temp" && (s.Kind is "cpu" or "gpu" || _settings.ShowUnused)))
        {
            var chip = new Border { Background = (Brush)FindResource("BgBrush"), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 8) };
            var text = new TextBlock { Text = Label(t) + "   " + (t.Value?.ToString("0", CultureInfo.InvariantCulture) ?? "–") + " °C", Tag = t.Id };
            chip.Child = text;
            Temps.Children.Add(chip);
        }
        int index = 0;
        foreach (var control in _sensors.Where(s => s.Type == "control"))
        {
            var fan = FanOf(control);
            if (!_settings.ShowUnused && (fan?.Value ?? 0) <= 0 && control.Kind != "gpu") continue;
            var card = BuildCard(control, fan, index++);
            _cards[control.Id] = card;
        }
        if (_cards.Count == 0)
            Cards.Children.Add(new TextBlock { Text = "No fan that can be set was found. Reading the temperatures still works.", Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 10, 0, 0) });
        _building = false;
    }

    private Card BuildCard(FanSensor control, FanSensor? fan, int index)
    {
        var rule = _settings.Config.Rules.Find(r => r.Control == control.Id);
        string mode = rule?.Mode ?? "auto";
        if (rule != null && rule.Points.Count == 0) rule.Points = FanSettings.Presets[1].Points.Select(p => (double[])p.Clone()).ToList();

        var border = new Border { Style = (Style)FindResource("Section"), Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(16, 12, 16, 14) };
        var root = new StackPanel();
        border.Child = root;
        AutomationProperties.SetAutomationId(border, "FanCard" + index);

        var head = new Grid();
        var name = new TextBlock { Text = NameOf(control), FontWeight = FontWeights.SemiBold, FontSize = 15, Cursor = Cursors.Hand, ToolTip = "Double-click to rename" };
        AutomationProperties.SetAutomationId(name, "FanName" + index);
        name.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;
            string? n = TextPrompt.Ask(Window.GetWindow(this)!, "Name this fan", "What is this fan? (for example: CPU fan, front fan)", NameOf(control));
            if (n == null) return;
            _settings.Names[control.Id] = n.Trim(); _settings.Save(); name.Text = NameOf(control);
        };
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var rpm = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold };
        var now = new TextBlock { Margin = new Thickness(12, 0, 0, 0), Foreground = (Brush)FindResource("MutedBrush"), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(rpm, "FanRpm" + index);
        AutomationProperties.SetAutomationId(now, "FanNow" + index);
        right.Children.Add(rpm); right.Children.Add(now);
        head.Children.Add(name); head.Children.Add(right);
        root.Children.Add(head);
        root.Children.Add(new TextBlock { Text = control.Hardware, FontSize = 12, Foreground = (Brush)FindResource("MutedBrush") });

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
        var slider = new Slider { Minimum = floor, Maximum = Math.Max(floor + 1, control.Max), Width = 320, Value = Math.Clamp(rule?.Percent ?? 50, floor, 100), TickFrequency = 5, IsSnapToTickEnabled = false, VerticalAlignment = VerticalAlignment.Center };
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
        var points = new TextBox { Style = (Style)FindResource("Field"), Margin = new Thickness(0, 8, 0, 0), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 420 };
        AutomationProperties.SetAutomationId(points, "FanPoints" + index);
        curvePanel.Children.Add(presetRow);
        curvePanel.Children.Add(new TextBlock { Text = "Curve: temperature in °C : fan speed in %, separated by commas", FontSize = 12, Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 10, 0, 0) });
        curvePanel.Children.Add(points);
        root.Children.Add(curvePanel);

        var card = new Card { Control = control, Fan = fan, Rpm = rpm, Now = now, Slider = slider, SliderValue = sliderValue, FixedPanel = fixedPanel, CurvePanel = curvePanel, Points = points, Auto = auto, Fixed = fixedChip, Curve = curveChip, Cpu = cpu, Gpu = gpu };
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
        foreach (var chip in Temps.Children.OfType<Border>())
            if (chip.Child is TextBlock tb && tb.Tag is string id && _sensors.FirstOrDefault(s => s.Id == id) is { } t)
                tb.Text = Label(t) + "   " + (t.Value?.ToString("0", CultureInfo.InvariantCulture) ?? "–") + " °C";
        foreach (var (id, card) in _cards)
        {
            var control = _sensors.FirstOrDefault(s => s.Id == id);
            var fan = control == null ? null : FanOf(control);
            card.Rpm.Text = fan?.Value is double rpm ? rpm.ToString("0", CultureInfo.InvariantCulture) + " RPM" : "– RPM";
            card.Now.Text = control?.Value is double pct ? (control.Auto ? "PC decides, now " : "set, now ") + pct.ToString("0", CultureInfo.InvariantCulture) + " %" : "";
        }
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

    private void Unused_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowUnused = UnusedBox.IsChecked == true;
        _settings.Save();
        _layout = "";                                                          // rebuilt on the next reading
    }
}
