using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>What the person chose for the lights (rgb.json in the data folder).</summary>
internal sealed class RgbSettings
{
    [JsonPropertyName("mode")] public int Mode { get; set; } = 1;
    [JsonPropertyName("r")] public int R { get; set; } = 0x4C;
    [JsonPropertyName("g")] public int G { get; set; } = 0x8D;
    [JsonPropertyName("b")] public int B { get; set; } = 0xFF;
    [JsonPropertyName("brightness")] public int Brightness { get; set; } = 100;
    [JsonPropertyName("speed")] public int Speed { get; set; } = 2;         // 0 slowest .. 4 fastest (the chip counts the other way round)
    [JsonPropertyName("reverse")] public bool Reverse { get; set; }

    private static string PathOnDisk => System.IO.Path.Combine(App.DataDir, "rgb.json");

    public static RgbSettings Load()
    {
        try { if (File.Exists(PathOnDisk)) return JsonSerializer.Deserialize<RgbSettings>(File.ReadAllText(PathOnDisk)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// The dashboard's lighting section: colour and effect of the RAM's lights, set through the fan helper's administrator process.
/// Changes show at once; "Save in the RAM" stores them inside the sticks so they stay when Utylix is closed and after a restart.
/// </summary>
internal sealed class RgbPanel : UserControl
{
    private readonly FansPage _fans;
    private readonly RgbSettings _s = RgbSettings.Load();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
    private readonly Button _find = new();
    private readonly TextBlock _intro = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _controls = new() { Visibility = Visibility.Collapsed };
    private readonly WrapPanel _modes = new() { Margin = new Thickness(0, 6, 0, 6) };
    private readonly StackPanel _colourRow = new();
    private readonly StackPanel _speedRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Slider _red = Make(0, 255), _green = Make(0, 255), _blue = Make(0, 255), _bright = Make(10, 100), _speed = Make(0, 4);
    private readonly Border _preview = new() { Width = 46, Height = 46, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
    private readonly CheckBox _reverse = new() { Content = "Run the other way round", Margin = new Thickness(18, 8, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _armoury = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(8) };
    private readonly TextBlock _armouryText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _armouryButton = new() { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _saveButton = new();
    private readonly TextBlock _saveNote = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private bool _building = true, _busy, _found;
    private string _armouryState = "none";

    private static Slider Make(double min, double max) => new() { Minimum = min, Maximum = max, Width = 220, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };

    private static readonly (string Name, int R, int G, int B)[] Palette =
    {
        ("White", 255, 255, 255), ("Red", 255, 40, 40), ("Orange", 255, 130, 0), ("Yellow", 255, 220, 0), ("Green", 40, 220, 80),
        ("Cyan", 0, 220, 230), ("Blue", 40, 100, 255), ("Purple", 150, 60, 255), ("Pink", 255, 70, 170),
    };

    public RgbPanel(FansPage fans)
    {
        _fans = fans;
        var R = (string key) => Application.Current.FindResource(key);
        _intro.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");   // (by name: they follow a change of theme)
        _intro.Text = "Set the colour and effect of the lights on your RAM, like Armoury Crate does, without Armoury Crate. It needs the same administrator helper as the fan control (Windows asks once) and the free PawnIO driver. What you change shows at once; \"Save in the RAM\" keeps it inside the sticks, so it stays even when Utylix is closed.";
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _saveNote.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _saveNote.Text = "Saving writes into the RAM's own memory, which can only be written a limited number of times: use it when you are happy with the look, not for every try.";

        _find.Content = "Find my RAM lights";
        _find.Style = (Style)R("PrimaryButton");
        _find.MinWidth = 150; _find.Height = 38;
        AutomationProperties.SetAutomationId(_find, "RgbFind");
        _find.Click += async (_, _) => await FindAsync(interactive: true);

        var head = new Grid();
        head.Children.Add(new TextBlock { Text = "Lighting", FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetAutomationId(_status, "RgbStatus");
        right.Children.Add(_status); right.Children.Add(_find);
        head.Children.Add(right);

        // effects
        _controls.Children.Add(new TextBlock { Text = "Effect", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
        for (int i = 0; i < EneModule.ModeNames.Length; i++)
        {
            int mode = i;
            var chip = new RadioButton { Content = EneModule.ModeNames[i], GroupName = "rgbmode", Style = (Style)R("ChipButton"), IsChecked = _s.Mode == i };
            AutomationProperties.SetAutomationId(chip, "RgbMode" + i);
            chip.Checked += (_, _) => { if (_building) return; _s.Mode = mode; UpdateRows(); Changed(); };
            _modes.Children.Add(chip);
        }
        _controls.Children.Add(_modes);

        // colour: a few ready colours + three sliders
        var swatches = new WrapPanel { Margin = new Thickness(0, 6, 0, 4) };
        foreach (var p in Palette)
        {
            var b = new Button { Width = 30, Height = 30, Margin = new Thickness(0, 0, 8, 6), ToolTip = p.Name, Cursor = Cursors.Hand, Background = new SolidColorBrush(Color.FromRgb((byte)p.R, (byte)p.G, (byte)p.B)), BorderBrush = (Brush)R("LineBrush"), BorderThickness = new Thickness(1) };
            b.Template = Swatch();
            AutomationProperties.SetName(b, p.Name);
            AutomationProperties.SetAutomationId(b, "RgbColour" + p.Name);
            var pick = p;
            b.Click += (_, _) => { _building = true; _red.Value = pick.R; _green.Value = pick.G; _blue.Value = pick.B; _building = false; ReadSliders(); Changed(); };
            swatches.Children.Add(b);
        }
        _colourRow.Children.Add(new TextBlock { Text = "Colour", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
        _colourRow.Children.Add(swatches);
        var mix = new Grid();
        mix.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mix.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var sliders = new StackPanel();
        void Row(string label, Slider s, string id)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
            row.Children.Add(new TextBlock { Text = label, Width = 52, VerticalAlignment = VerticalAlignment.Center }.Live(TextBlock.ForegroundProperty, "MutedBrush"));
            AutomationProperties.SetAutomationId(s, id);
            row.Children.Add(s);
            sliders.Children.Add(row);
            s.ValueChanged += (_, _) => { if (!_building) { ReadSliders(); Changed(); } };
        }
        Row("Red", _red, "RgbRed"); Row("Green", _green, "RgbGreen"); Row("Blue", _blue, "RgbBlue");
        mix.Children.Add(sliders);
        _preview.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        _preview.VerticalAlignment = VerticalAlignment.Top; _preview.Margin = new Thickness(18, 4, 0, 0);
        Grid.SetColumn(_preview, 1);
        mix.Children.Add(_preview);
        _colourRow.Children.Add(mix);
        _controls.Children.Add(_colourRow);

        // brightness, speed, direction
        var br = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        br.Children.Add(new TextBlock { Text = "Brightness", Width = 76, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
        AutomationProperties.SetAutomationId(_bright, "RgbBrightness");
        br.Children.Add(_bright);
        _bright.ValueChanged += (_, _) => { if (!_building) { _s.Brightness = (int)_bright.Value; Changed(); } };
        _controls.Children.Add(br);
        _speedRow.Children.Add(new TextBlock { Text = "Speed", Width = 76, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
        AutomationProperties.SetAutomationId(_speed, "RgbSpeed");
        _speedRow.Children.Add(_speed);
        _speed.ValueChanged += (_, _) => { if (!_building) { _s.Speed = (int)Math.Round(_speed.Value); Changed(); } };
        AutomationProperties.SetAutomationId(_reverse, "RgbReverse");
        _reverse.Click += (_, _) => { _s.Reverse = _reverse.IsChecked == true; Changed(); };
        _speedRow.Children.Add(_reverse);
        _controls.Children.Add(_speedRow);

        // save into the sticks
        _saveButton.Content = "Save in the RAM";
        _saveButton.Style = (Style)R("DialogButton");
        _saveButton.HorizontalAlignment = HorizontalAlignment.Left;
        _saveButton.Margin = new Thickness(0, 14, 0, 0);
        _saveButton.Padding = new Thickness(16, 6, 16, 6);
        AutomationProperties.SetAutomationId(_saveButton, "RgbSave");
        _saveButton.Click += async (_, _) => await SaveAsync();
        _controls.Children.Add(_saveButton);
        _controls.Children.Add(_saveNote);

        // Armoury Crate
        _armoury.SetResourceReference(Border.BackgroundProperty, "BgBrush");
        _armoury.BorderThickness = new Thickness(1);
        _armoury.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        var astack = new StackPanel();
        astack.Children.Add(_armouryText);
        _armouryButton.Style = (Style)R("DialogButton");
        _armouryButton.Padding = new Thickness(14, 5, 14, 5);
        AutomationProperties.SetAutomationId(_armouryButton, "RgbArmoury");
        _armouryButton.Click += async (_, _) => await ArmouryAsync();
        astack.Children.Add(_armouryButton);
        _armoury.Child = astack;
        _controls.Children.Add(_armoury);

        var stack = new StackPanel();
        stack.Children.Add(head);
        _intro.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(_intro);
        stack.Children.Add(_controls);
        var card = new Border { Style = (Style)R("Section"), Padding = new Thickness(16, 12, 16, 14), Child = stack };
        Content = card;

        _building = true;
        _red.Value = _s.R; _green.Value = _s.G; _blue.Value = _s.B; _bright.Value = _s.Brightness; _speed.Value = _s.Speed; _reverse.IsChecked = _s.Reverse;
        _building = false;
        UpdateRows();
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await PushAsync(); };
        SetStatus("Not looked for yet");
        Loaded += async (_, _) => await FindAsync(interactive: false);          // quietly, only if the helper is already running
    }

    /// <summary>A flat round button that shows its own colour (the default button template would paint over it).</summary>
    private static ControlTemplate Swatch()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private void SetStatus(string text) => _status.Text = text;

    private void ReadSliders()
    {
        _s.R = (int)_red.Value; _s.G = (int)_green.Value; _s.B = (int)_blue.Value;
        _preview.Background = new SolidColorBrush(Color.FromRgb((byte)_s.R, (byte)_s.G, (byte)_s.B));
    }

    private void UpdateRows()
    {
        _colourRow.Visibility = EneModule.UsesColour(_s.Mode) ? Visibility.Visible : Visibility.Collapsed;
        _speedRow.Visibility = _s.Mode > 1 ? Visibility.Visible : Visibility.Collapsed;
        _preview.Background = new SolidColorBrush(Color.FromRgb((byte)_s.R, (byte)_s.G, (byte)_s.B));
    }

    private void Changed()
    {
        _s.Save();
        if (_found) { _debounce.Stop(); _debounce.Start(); }
    }

    private LightLook Look()
    {
        double k = Math.Clamp(_s.Brightness, 10, 100) / 100.0;
        return new LightLook { Mode = _s.Mode, R = (int)Math.Round(_s.R * k), G = (int)Math.Round(_s.G * k), B = (int)Math.Round(_s.B * k), Speed = 4 - Math.Clamp(_s.Speed, 0, 4), Reverse = _s.Reverse };
    }

    // ---------- talking to the helper ----------
    private async Task FindAsync(bool interactive)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            bool canTask = FanTask.AccountCanElevate;
            if (!FanSettings.Client.Connected || FanSettings.Client.HelperIsLimited)
            {
                // a quiet look only uses a helper that already runs with administrator rights
                if (!interactive)
                {
                    if (!canTask && !_intro.Text.Contains(FansPage.NeedsAdminPassword)) _intro.Text += "\n\n" + FansPage.NeedsAdminPassword;
                    return;
                }
                _find.IsEnabled = false;
                SetStatus(canTask ? "Starting the helper…" : "Type an administrator's password in Windows' prompt…");
                if (!await _fans.EnsureHelperAsync()) { SetStatus(canTask ? "The helper did not start" : "Not started: Windows needs an administrator's password"); return; }
            }
            _find.IsEnabled = false;
            SetStatus("Looking…");
            var reply = await FanSettings.Client.AskAsync(new FanRequest { Cmd = "light-scan" }, 15);
            if (reply == null) { SetStatus("No answer from the helper"); return; }
            if (!reply.Ok) { SetStatus(reply.Error ?? "Could not look"); ShowArmoury(reply.Armoury); return; }
            var sticks = reply.Sticks ?? new();
            _armouryState = reply.Armoury ?? "none";
            if (sticks.Count == 0)
            {
                _found = false;
                _controls.Visibility = Visibility.Collapsed;
                _intro.Visibility = Visibility.Visible;
                SetStatus("No lit RAM found");
                _intro.Text = "No RAM with lights that Utylix can drive was found. Utylix drives the Aura-compatible kind (the lighting chip used by ASUS Aura Sync and by many T-Force and other RGB RAM). If Armoury Crate is running, it can hide the sticks: switch it off and press Find again.";
                return;
            }
            _found = true;
            _intro.Visibility = Visibility.Collapsed;
            _controls.Visibility = Visibility.Visible;
            SetStatus(sticks.Count == 1 ? "1 stick found" : sticks.Count + " sticks found");
            ShowArmoury(_armouryState);
            await PushAsync();
        }
        finally { _find.IsEnabled = true; _find.Content = _found ? "Look again" : "Find my RAM lights"; _busy = false; }
    }

    private async Task PushAsync()
    {
        if (!_found || !FanSettings.Client.Connected) return;
        var reply = await FanSettings.Client.AskAsync(new FanRequest { Cmd = "light-set", Look = Look() }, 10);
        SetStatus(reply?.Ok == true ? "Applied" : reply?.Error ?? "No answer from the helper");
    }

    private async Task SaveAsync()
    {
        if (!_found || !FanSettings.Client.Connected) return;
        await PushAsync();
        _saveButton.IsEnabled = false;
        var reply = await FanSettings.Client.AskAsync(new FanRequest { Cmd = "light-save" }, 10);
        SetStatus(reply?.Ok == true ? "Saved in the RAM" : reply?.Error ?? "No answer from the helper");
        _saveButton.IsEnabled = true;
    }

    private void ShowArmoury(string? state)
    {
        state ??= "none";
        _armouryState = state;
        if (state == "none") { _armoury.Visibility = Visibility.Collapsed; return; }
        _armoury.Visibility = Visibility.Visible;
        if (state == "running")
        {
            _armouryText.Text = "Armoury Crate's lighting service is running. It writes to the same lights and puts its own colours back (for example at every start), so what you choose here can be overwritten.";
            _armouryButton.Content = "Turn Armoury Crate's lighting service off";
        }
        else
        {
            _armouryText.Text = "Armoury Crate's lighting service is switched off, so it will not change the lights. Armoury Crate itself stays installed.";
            _armouryButton.Content = "Turn it back on";
        }
    }

    private async Task ArmouryAsync()
    {
        if (!FanSettings.Client.Connected) return;
        bool back = _armouryState != "running";
        _armouryButton.IsEnabled = false;
        SetStatus(back ? "Turning it back on…" : "Turning it off…");
        var reply = await FanSettings.Client.AskAsync(new FanRequest { Cmd = "armoury", Flag = back }, 20);
        ShowArmoury(reply?.Armoury);
        SetStatus(reply?.Ok == true ? (back ? "Armoury Crate's lighting is back on" : "Armoury Crate's lighting is off") : reply?.Error ?? "No answer from the helper");
        _armouryButton.IsEnabled = true;
        if (!back) await PushAsync();
    }
}
