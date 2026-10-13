using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace IdmClone.Vid;

public sealed partial class VidWindow
{
    private bool _inspectorBuilding;

    private TextBlock Heading(string text, double top = 0)
    {
        var t = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, top, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis };
        return t;
    }

    private TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), FontSize = 12.5 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return t;
    }

    /// <summary>A label with a box for a number: Enter or leaving the box applies it.</summary>
    private void NumberRow(string label, string unit, double value, string format, Action<double> apply, string? id = null)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var box = new TextBox { Text = value.ToString(format, CultureInfo.CurrentCulture), Padding = new Thickness(6, 3, 6, 3), HorizontalContentAlignment = HorizontalAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center };
        if (id != null) System.Windows.Automation.AutomationProperties.SetAutomationId(box, id);
        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        var u = new TextBlock { Text = unit, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        u.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Grid.SetColumn(u, 2);
        row.Children.Add(u);
        bool done = false;
        void Commit()
        {
            if (done) return;
            if (!double.TryParse(box.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) &&
                !double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out v)) { box.Text = value.ToString(format, CultureInfo.CurrentCulture); return; }
            if (Math.Abs(v - value) < 1e-9) return;
            done = true;
            apply(v);
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; _timeline.Focus(); } else if (e.Key == Key.Escape) { box.Text = value.ToString(format, CultureInfo.CurrentCulture); _timeline.Focus(); e.Handled = true; } };
        box.LostKeyboardFocus += (_, _) => Commit();
        _inspector.Children.Add(row);
    }

    /// <summary>A labelled slider that changes the clips live while it is dragged and is ONE undo step.</summary>
    private void SliderRow(string label, double value, double min, double max, Func<double, string> show, Action<double> apply, string undoLabel, string? id = null)
    {
        var head = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        var val = new TextBlock { Text = show(value) };
        val.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        DockPanel.SetDock(val, Dock.Right);
        head.Children.Add(val);
        head.Children.Add(new TextBlock { Text = label });
        _inspector.Children.Add(head);
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Margin = new Thickness(0, 2, 0, 8), SmallChange = (max - min) / 100, LargeChange = (max - min) / 10 };
        if (id != null) System.Windows.Automation.AutomationProperties.SetAutomationId(slider, id);
        string? before = null;
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => before = _s.BeginEdit()));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            if (before != null) { string b = before; before = null; _s.CommitEdit(b, undoLabel); }
        }));
        slider.ValueChanged += (_, e) =>
        {
            if (_inspectorBuilding) return;
            val.Text = show(e.NewValue);
            if (before != null)
            {
                apply(e.NewValue);                                  // live: no history entry until the drag ends
                _timeline.InvalidateVisual();
                _refresh.Stop(); _refresh.Start();
            }
            else _s.Do(undoLabel, () => apply(e.NewValue));
        };
        _inspector.Children.Add(slider);
    }

    private void RefreshInspector()
    {
        if (_inspector == null) return;
        _inspectorBuilding = true;
        try
        {
            _inspector.Children.Clear();
            var sel = _s.WithLinked(_s.SelectedClips).ToList();
            if (sel.Count == 0) BuildProjectPanel(); else BuildClipPanel(sel);
        }
        finally { _inspectorBuilding = false; }
    }

    private void BuildProjectPanel()
    {
        var p = _s.Project;
        _inspector.Children.Add(Heading("Project"));
        _inspector.Children.Add(Hint("Choose a clip on the timeline to change its speed, sound, fades and position."));
        _inspector.Children.Add(Heading("Picture size", 6));
        var sizes = new (string Label, int W, int H)[]
        {
            ("1920 × 1080  wide", 1920, 1080), ("1280 × 720  wide", 1280, 720), ("3840 × 2160  4K", 3840, 2160),
            ("1080 × 1920  phone", 1080, 1920), ("1080 × 1080  square", 1080, 1080), ("1440 × 1080  4:3", 1440, 1080),
        };
        var panel = new WrapPanel();
        var chipStyle = (Style)FindResource("ChipButton");
        foreach (var s in sizes)
        {
            var chip = new RadioButton { Content = s.Label, Style = chipStyle, GroupName = "vidSize", IsChecked = p.Width == s.W && p.Height == s.H };
            chip.Checked += (_, _) =>
            {
                if (_inspectorBuilding || (p.Width == s.W && p.Height == s.H)) return;
                _s.Do("Picture size", () => { _s.Project.Width = s.W; _s.Project.Height = s.H; });
            };
            panel.Children.Add(chip);
        }
        _inspector.Children.Add(panel);
        _inspector.Children.Add(Heading("Frames per second", 6));
        var fpsPanel = new WrapPanel();
        foreach (double f in new double[] { 24, 25, 30, 50, 60 })
        {
            var chip = new RadioButton { Content = f.ToString("0"), Style = chipStyle, GroupName = "vidFps", IsChecked = Math.Abs(p.Fps - f) < 0.01 };
            chip.Checked += (_, _) =>
            {
                if (_inspectorBuilding || Math.Abs(p.Fps - f) < 0.01) return;
                _s.Do("Frame rate", () => _s.Project.Fps = f);
            };
            fpsPanel.Children.Add(chip);
        }
        _inspector.Children.Add(fpsPanel);
        int clips = p.AllClips.Count();
        _inspector.Children.Add(Heading("About this project", 10));
        _inspector.Children.Add(Hint($"{clips} clip{(clips == 1 ? "" : "s")}  ·  {VidTimeline.TimeText(p.Duration)} long  ·  {p.Media.Count} file{(p.Media.Count == 1 ? "" : "s")}"));
    }

    private void BuildClipPanel(List<Clip> sel)
    {
        var project = _s.Project;
        var tracks = sel.ToDictionary(c => c, c => project.TrackOf(c));
        var videos = sel.Where(c => tracks[c]?.Kind == TrackKind.Video).ToList();
        var sounds = sel.Where(c => tracks[c]?.Kind == TrackKind.Audio).ToList();
        var primary = videos.OrderBy(c => c.Start).FirstOrDefault() ?? sounds.OrderBy(c => c.Start).First();
        var media = project.MediaOf(primary);
        bool image = media?.Kind == MediaKind.Image;

        _inspector.Children.Add(Heading(primary.Label ?? media?.Name ?? "Clip"));
        string kind = videos.Count > 0 && sounds.Count > 0 ? "picture and sound" : videos.Count > 0 ? (image ? "picture" : "picture") : "sound";
        string many = sel.Count > 2 || (videos.Count > 1 || sounds.Count > 1) ? $"{sel.Count} clips chosen  ·  " : "";
        _inspector.Children.Add(Hint(many + kind));

        _inspector.Children.Add(Heading("Time", 4));
        NumberRow("Starts at", "s", primary.Start, "0.00", v =>
        {
            var applied = _s.Move(sel, Math.Max(0, v) - primary.Start, 0, true);
            if (Math.Abs(applied.Time - (Math.Max(0, v) - primary.Start)) > 0.001) SetStatus("There is another clip in the way, so it was put as close as it fits.");
        }, "VidInspStart");
        NumberRow("Length", "s", primary.Length, "0.00", v =>
        {
            double want = Math.Max(VidSession.MinLength, v);
            _s.Do("Length", () => _s.TrimEnd(new[] { primary }, primary.Start + want, commit: false));
        }, "VidInspLength");
        if (!image)
            NumberRow("Speed", "×", primary.Speed, "0.##", v =>
            {
                if (!_s.SetSpeed(primary, v)) SetStatus("There is another clip in the way: make room first.");
            }, "VidInspSpeed");

        if (sounds.Count > 0)
        {
            _inspector.Children.Add(Heading("Sound", 8));
            SliderRow("Volume", sounds[0].Volume, 0, 2, v => $"{v * 100:0} %", v => { foreach (var c in sounds) c.Volume = Math.Round(v, 2); }, "Volume", "VidInspVolume");
        }
        if (videos.Count > 0)
        {
            _inspector.Children.Add(Heading("Picture", 8));
            SliderRow("Opacity", videos[0].Opacity, 0, 1, v => $"{v * 100:0} %", v => { foreach (var c in videos) c.Opacity = Math.Round(v, 2); }, "Opacity", "VidInspOpacity");
            SliderRow("Size", videos[0].Scale, 0.1, 3, v => $"{v * 100:0} %", v => { foreach (var c in videos) c.Scale = Math.Round(v, 2); }, "Size", "VidInspScale");
            SliderRow("Position across", videos[0].X, 0, 1, v => $"{v * 100:0} %", v => { foreach (var c in videos) c.X = Math.Round(v, 3); }, "Position", "VidInspX");
            SliderRow("Position down", videos[0].Y, 0, 1, v => $"{v * 100:0} %", v => { foreach (var c in videos) c.Y = Math.Round(v, 3); }, "Position", "VidInspY");
            var centre = MakeButton("", "Centre and fit", "Put the picture back in the middle at its natural size", () =>
                _s.SetProperty(videos, "Centre", c => { c.X = 0.5; c.Y = 0.5; c.Scale = 1; }));
            centre.HorizontalAlignment = HorizontalAlignment.Left;
            _inspector.Children.Add(centre);
        }
        _inspector.Children.Add(Heading("Fades", 10));
        NumberRow("Fade in", "s", primary.FadeIn, "0.0#", v => _s.SetProperty(sel, "Fade in", c => c.FadeIn = Math.Clamp(v, 0, Math.Max(0, c.Length - c.FadeOut))), "VidInspFadeIn");
        NumberRow("Fade out", "s", primary.FadeOut, "0.0#", v => _s.SetProperty(sel, "Fade out", c => c.FadeOut = Math.Clamp(v, 0, Math.Max(0, c.Length - c.FadeIn))), "VidInspFadeOut");
    }
}
