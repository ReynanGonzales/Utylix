using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace IdmClone;

/// <summary>
/// The brightness panel that opens from the tray icon: one slider for every screen (external monitors over the video cable,
/// a laptop's own screen through Windows), like the free "Monitorian" program. It closes when you click somewhere else.
/// </summary>
public partial class BrightnessWindow : Window
{
    private sealed class Prefs { public bool Unison { get; set; } }

    private sealed class Row
    {
        public required DisplayMonitor Monitor;
        public required Slider Slider;
        public required TextBlock Value;
    }

    private static BrightnessWindow? _open;
    private static string PrefsPath => Path.Combine(App.DataDir, "brightness.json");

    private readonly List<Row> _rows = new();
    private List<DisplayMonitor> _monitors = new();
    private readonly System.Drawing.Point _cursor = System.Windows.Forms.Cursor.Position;
    private bool _activated, _updating, _closing;

    public BrightnessWindow()
    {
        InitializeComponent();
        WindowTheme.DarkTitleBar(this);
        try { if (File.Exists(PrefsPath)) Unison.IsChecked = JsonSerializer.Deserialize<Prefs>(File.ReadAllText(PrefsPath))?.Unison; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { /* default */ }
        Unison.Click += (_, _) =>
        {
            try { File.WriteAllText(PrefsPath, JsonSerializer.Serialize(new Prefs { Unison = Unison.IsChecked == true })); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* not fatal */ }
        };
        Activated += (_, _) => _activated = true;
        Deactivated += (_, _) => { if (_activated && !_closing) Close(); };
        Closing += (_, _) => _closing = true;
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !_closing) Close(); };
        SizeChanged += (_, _) => Place();
        Loaded += async (_, _) =>
        {
            Activate();
            Place();
            try { _monitors = await Task.Run(DisplayMonitor.Enumerate); }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or DllNotFoundException or InvalidOperationException)
            {
                StatusText.Text = "Windows would not list the screens: " + e.Message;
                return;
            }
            Build();
        };
        Closed += (_, _) =>
        {
            if (_open == this) _open = null;
            var monitors = _monitors;
            _ = Task.Delay(1500).ContinueWith(_ => DisplayMonitor.Release(monitors));       // after a last slider change has been sent
        };
    }

    /// <summary>Opens the panel (or brings it to the front when it is already open).</summary>
    public static void ShowPanel()
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new BrightnessWindow();
        _open.Show();
    }

    /// <summary>Next to the tray icon: the corner of the screen the pointer is nearest to, above or beside the taskbar.</summary>
    private void Place()
    {
        var screen = System.Windows.Forms.Screen.FromPoint(_cursor);
        var work = screen.WorkingArea;
        double scale = ScreenGrab.Scale;
        double left = work.Left / scale, right = work.Right / scale, top = work.Top / scale, bottom = work.Bottom / scale;
        const double margin = 12;
        Left = _cursor.X > (screen.Bounds.Left + screen.Bounds.Right) / 2 ? right - ActualWidth - margin : left + margin;
        Top = _cursor.Y > (screen.Bounds.Top + screen.Bounds.Bottom) / 2 ? bottom - ActualHeight - margin : top + margin;
    }

    private void Build()
    {
        Monitors.Children.Clear();
        _rows.Clear();
        if (_monitors.Count == 0) { StatusText.Text = "No screens found."; return; }

        int controllable = 0;
        for (int i = 0; i < _monitors.Count; i++)
        {
            var m = _monitors[i];
            bool works = m.Mode != BrightnessMode.None;
            if (works) controllable++;

            var name = new TextBlock { Text = m.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var value = new TextBlock { Text = works ? m.Percent.ToString() : "–", Foreground = RecWin.B("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, MinWidth = 28, TextAlignment = TextAlignment.Right };
            var head = new Grid { Margin = new Thickness(0, i == 0 ? 0 : 14, 0, 4) };
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(name);
            Grid.SetColumn(value, 1);
            head.Children.Add(value);
            Monitors.Children.Add(head);

            if (!works)
            {
                Monitors.Children.Add(new TextBlock
                {
                    Text = "This screen does not answer brightness commands. Switch on “DDC/CI” in the monitor's own menu and open this panel again.",
                    Foreground = RecWin.B("MutedBrush"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
                });
                continue;
            }

            var slider = new Slider { Style = (Style)FindResource("BrightSlider"), Value = m.Percent };
            AutomationProperties.SetAutomationId(slider, "BrightSlider" + i);
            AutomationProperties.SetName(slider, m.Name + " brightness");
            var row = new Row { Monitor = m, Slider = slider, Value = value };
            _rows.Add(row);
            slider.ValueChanged += (_, e) => { if (!_updating) Changed(row, (int)Math.Round(e.NewValue)); };
            slider.PreviewMouseWheel += (_, e) => { slider.Value = Math.Clamp(slider.Value + (e.Delta > 0 ? 5 : -5), 0, 100); e.Handled = true; };
            m.Failed += message => Dispatcher.BeginInvoke(() => StatusText.Text = m.Name + ": " + message);
            Monitors.Children.Add(slider);
        }

        StatusText.Visibility = Visibility.Collapsed;
        Unison.Visibility = controllable > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (controllable == 0)
        {
            StatusText.Text = "None of the screens can be adjusted from here.";
            StatusText.Visibility = Visibility.Visible;
        }
    }

    private void Changed(Row row, int percent)
    {
        row.Value.Text = percent.ToString();
        row.Monitor.SetPercent(percent);
        if (Unison.IsChecked != true) return;
        _updating = true;
        try
        {
            foreach (var other in _rows.Where(r => r != row))
            {
                other.Slider.Value = percent;
                other.Value.Text = percent.ToString();
                other.Monitor.SetPercent(percent);
            }
        }
        finally { _updating = false; }
    }
}
