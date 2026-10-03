using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace IdmClone;

/// <summary>How the music sounds: the equalizer, the speed, and whether loud and quiet songs are evened out. Kept in music.json.</summary>
public sealed class SoundSettings
{
    public bool EqOn { get; set; }
    public double Preamp { get; set; }
    public double[] Bands { get; set; } = new double[10];
    public string Preset { get; set; } = "Flat";
    public double Speed { get; set; } = 1;
    public bool Normalize { get; set; }
}

/// <summary>The "Sound" window of the music player: equalizer with presets, speed, even out volume, and the sleep timer.</summary>
public sealed class MusicSoundWindow : Window
{
    /// <summary>Centre frequencies of libvlc's 10 equalizer bands.</summary>
    public static readonly string[] BandNames = { "60 Hz", "170 Hz", "310 Hz", "600 Hz", "1 kHz", "3 kHz", "6 kHz", "12 kHz", "14 kHz", "16 kHz" };

    /// <summary>Starting points for the sliders (decibels per band).</summary>
    public static readonly (string Name, double[] Bands)[] Presets =
    {
        ("Flat", new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
        ("Bass boost", new double[] { 7, 6, 4, 2, 0, 0, 0, 0, 0, 0 }),
        ("Treble boost", new double[] { 0, 0, 0, 0, 0, 1, 3, 5, 6, 7 }),
        ("Vocal", new double[] { -3, -3, -2, 1, 4, 4, 3, 1, 0, -1 }),
        ("Rock", new double[] { 5, 4, 2, -2, -3, 1, 4, 5, 5, 5 }),
        ("Pop", new double[] { -1, 2, 4, 5, 3, 0, -1, -1, -1, -1 }),
        ("Jazz", new double[] { 4, 3, 1, 2, -2, -2, 0, 2, 3, 4 }),
        ("Classical", new double[] { 0, 0, 0, 0, 0, 0, -4, -4, -4, -6 }),
        ("Dance", new double[] { 8, 6, 2, 0, 0, -4, -6, -6, 0, 0 }),
    };

    private readonly SoundSettings _s;
    private readonly Action _changed;
    private readonly Action<int> _setSleep;
    private readonly Func<string> _sleepText;
    private readonly List<Slider> _sliders = new();
    private readonly List<TextBlock> _values = new();
    private readonly Slider _pre = new() { Minimum = -12, Maximum = 12, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _preValue = new() { Width = 46, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _eqOn = new() { Content = "Use the equalizer", FontWeight = FontWeights.SemiBold };
    private readonly List<(RadioButton Chip, string Name)> _presetChips = new();
    private readonly TextBlock _sleepLabel = new() { Opacity = 0.75, Margin = new Thickness(0, 6, 0, 0), FontSize = 12.5 };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _loading;

    public MusicSoundWindow(Window owner, SoundSettings settings, Action changed, Action<int> setSleep, Func<string> sleepText)
    {
        _s = settings; _changed = changed; _setSleep = setSleep; _sleepText = sleepText;
        Owner = owner;
        Title = "Sound - Utylix Music";
        Width = 520; SizeToContent = SizeToContent.Height; MaxHeight = 820; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13.5;
        WindowTheme.DarkTitleBar(this);
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/music.ico")); } catch (Exception e) when (e is IOException or UriFormatException) { }
        var chip = (Style)Application.Current.FindResource("ChipButton");
        var muted = (Brush)Application.Current.FindResource("MutedBrush");

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };
        root.Children.Add(new TextBlock { Text = "Sound", FontSize = 20, FontWeight = FontWeights.SemiBold });

        // ---- equalizer ----
        System.Windows.Automation.AutomationProperties.SetAutomationId(_eqOn, "MusicEqOn");
        _eqOn.Margin = new Thickness(0, 14, 0, 6);
        root.Children.Add(_eqOn);
        var presets = new WrapPanel();
        foreach (var (name, bands) in Presets)
        {
            var r = new RadioButton { Content = name, Style = chip, GroupName = "eqpreset", Margin = new Thickness(0, 0, 6, 6) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(r, "MusicPreset" + name.Replace(" ", ""));
            string n = name; var b = bands;
            r.Checked += (_, _) => { if (_loading) return; _s.Preset = n; for (int i = 0; i < 10; i++) _s.Bands[i] = b[i]; _s.EqOn = true; Load(); _changed(); };
            _presetChips.Add((r, name)); presets.Children.Add(r);
        }
        root.Children.Add(presets);

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Row(int index, string label, Slider slider, TextBlock value)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Foreground = muted, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
            slider.Margin = new Thickness(0, 3, 0, 3);
            Grid.SetRow(l, index); Grid.SetRow(slider, index); Grid.SetColumn(slider, 1); Grid.SetRow(value, index); Grid.SetColumn(value, 2);
            grid.Children.Add(l); grid.Children.Add(slider); grid.Children.Add(value);
        }
        Row(0, "Preamp", _pre, _preValue);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_pre, "MusicPreamp");
        _pre.ValueChanged += (_, _) => { if (_loading) return; _s.Preamp = _pre.Value; _s.Preset = ""; _s.EqOn = true; Load(); _changed(); };
        for (int i = 0; i < 10; i++)
        {
            var slider = new Slider { Minimum = -12, Maximum = 12, TickFrequency = 1, IsSnapToTickEnabled = true };
            var value = new TextBlock { Width = 46, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetAutomationId(slider, "MusicBand" + i);
            int band = i;
            slider.ValueChanged += (_, _) => { if (_loading) return; _s.Bands[band] = slider.Value; _s.Preset = ""; _s.EqOn = true; Load(); _changed(); };
            _sliders.Add(slider); _values.Add(value);
            Row(i + 1, BandNames[i], slider, value);
        }
        root.Children.Add(grid);
        var reset = new Button { Content = "Reset to flat", Style = (Style)Application.Current.FindResource("DialogButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        reset.Click += (_, _) => { _s.Preset = "Flat"; _s.Preamp = 0; for (int i = 0; i < 10; i++) _s.Bands[i] = 0; _s.EqOn = false; Load(); _changed(); };
        root.Children.Add(reset);
        _eqOn.Checked += (_, _) => { if (_loading) return; _s.EqOn = true; _changed(); };
        _eqOn.Unchecked += (_, _) => { if (_loading) return; _s.EqOn = false; _changed(); };

        // ---- speed, volume, sleep ----
        root.Children.Add(Heading("Speed"));
        var speeds = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (double speed in new[] { 0.75, 1.0, 1.25, 1.5, 2.0 })
        {
            var r = new RadioButton { Content = speed == 1 ? "Normal" : speed.ToString("0.##") + "×", Style = chip, GroupName = "musicspeed", IsChecked = Math.Abs(_s.Speed - speed) < 0.01 };
            System.Windows.Automation.AutomationProperties.SetAutomationId(r, "MusicSpeed" + speed.ToString("0.##").Replace(".", "_"));
            double sp = speed;
            r.Checked += (_, _) => { if (_loading) return; _s.Speed = sp; _changed(); };
            speeds.Children.Add(r);
        }
        root.Children.Add(speeds);

        var normalize = new CheckBox { Content = "Even out the volume (loud and quiet songs sound about the same)", IsChecked = _s.Normalize, Margin = new Thickness(0, 16, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(normalize, "MusicNormalize");
        normalize.Checked += (_, _) => { _s.Normalize = true; _changed(); };
        normalize.Unchecked += (_, _) => { _s.Normalize = false; _changed(); };
        root.Children.Add(normalize);
        root.Children.Add(new TextBlock { Text = "Takes effect from the next song.", Opacity = 0.6, FontSize = 12, Margin = new Thickness(26, 2, 0, 0) });

        root.Children.Add(Heading("Sleep timer"));
        var sleep = new WrapPanel();
        foreach (var (label, minutes) in new[] { ("Off", 0), ("15 minutes", 15), ("30 minutes", 30), ("60 minutes", 60), ("After this song", -1) })
        {
            var r = new RadioButton { Content = label, Style = chip, GroupName = "musicsleep", Margin = new Thickness(0, 0, 6, 6) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(r, "MusicSleep" + (minutes < 0 ? "Song" : minutes.ToString()));
            int m = minutes;
            r.Checked += (_, _) => { if (_loading) return; _setSleep(m); UpdateSleepLabel(); };
            if (minutes == 0) r.IsChecked = true;
            sleep.Children.Add(r);
        }
        root.Children.Add(sleep);
        root.Children.Add(_sleepLabel);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_sleepLabel, "MusicSleepText");

        var close = new Button { Content = "Close", Style = (Style)Application.Current.FindResource("DialogButton"), HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 96, Margin = new Thickness(0, 14, 0, 0), IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();
        root.Children.Add(close);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _tick.Tick += (_, _) => UpdateSleepLabel();
        Loaded += (_, _) => { Load(); UpdateSleepLabel(); _tick.Start(); };
        Closed += (_, _) => _tick.Stop();
    }

    private TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) };

    private void UpdateSleepLabel() => _sleepLabel.Text = _sleepText();

    /// <summary>Shows the settings in the controls (without changing them).</summary>
    private void Load()
    {
        _loading = true;
        try
        {
            _eqOn.IsChecked = _s.EqOn;
            _pre.Value = _s.Preamp; _preValue.Text = Db(_s.Preamp);
            for (int i = 0; i < 10; i++) { _sliders[i].Value = _s.Bands[i]; _values[i].Text = Db(_s.Bands[i]); }
            foreach (var (chip, name) in _presetChips) chip.IsChecked = string.Equals(name, _s.Preset, StringComparison.Ordinal);
        }
        finally { _loading = false; }
    }

    private static string Db(double v) => (v > 0 ? "+" : "") + v.ToString("0") + " dB";
}
