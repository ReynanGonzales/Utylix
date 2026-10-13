using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace IdmClone.Vid;

/// <summary>Effect Controls: everything about the chosen clip(s), laid out in foldable sections like Premiere's panel: Motion, Opacity, Time,
/// Audio, Transition, Effects, Fades (and the words of a title). Animatable rows have a stopwatch and keyframe buttons.</summary>
public sealed partial class VidWindow
{
    private bool _inspectorBuilding;
    private readonly HashSet<string> _collapsed = new();

    // ---------- small building blocks ----------
    private TextBlock Heading(string text, double top = 0) =>
        new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, top, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis };

    private TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), FontSize = 12.5 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return t;
    }

    /// <summary>A section title: click it to fold the rows below it (like Premiere's twirl-down arrows).</summary>
    private UIElement SectionHeader(string title)
    {
        var row = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(0, 10, 0, 6) };
        var chevron = new TextBlock { FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Width = 18, VerticalAlignment = VerticalAlignment.Center, Text = _collapsed.Contains(title) ? "" : "" };
        row.Children.Add(chevron);
        row.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
        row.Tag = "section:" + title;
        row.MouseLeftButtonUp += (_, _) =>
        {
            if (!_collapsed.Remove(title)) _collapsed.Add(title);
            RefreshInspector();
        };
        return row;
    }

    /// <summary>Hides the rows under the sections that are folded.</summary>
    private void ApplySections()
    {
        bool hide = false;
        foreach (UIElement child in _inspector.Children)
        {
            if (child is FrameworkElement fe && fe.Tag is string tag && tag.StartsWith("section:", StringComparison.Ordinal))
            {
                hide = _collapsed.Contains(tag.Substring(8));
                continue;
            }
            child.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>A label with a box for a number: Enter or leaving the box applies it.</summary>
    private void NumberRow(string label, string unit, double value, string format, Action<double> apply, string? id = null, Panel? into = null)
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
        (into ?? _inspector).Children.Add(row);
    }

    /// <summary>A slider that changes things live while it is dragged and is ONE undo step. <paramref name="apply"/> must not record history itself.</summary>
    private Slider MakeSlider(double value, double min, double max, Action<double> apply, string undoLabel, Action<double>? shown = null, string? id = null)
    {
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
            shown?.Invoke(e.NewValue);
            if (before != null)
            {
                apply(e.NewValue);
                _timeline.InvalidateVisual();
                _refresh.Stop(); _refresh.Start();
            }
            else _s.Do(undoLabel, () => apply(e.NewValue));
        };
        return slider;
    }

    private void SliderRow(string label, double value, double min, double max, Func<double, string> show, Action<double> apply, string undoLabel, string? id = null)
    {
        var head = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
        var val = new TextBlock { Text = show(value) };
        val.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        DockPanel.SetDock(val, Dock.Right);
        head.Children.Add(val);
        head.Children.Add(new TextBlock { Text = label });
        _inspector.Children.Add(head);
        _inspector.Children.Add(MakeSlider(value, min, max, apply, undoLabel, v => val.Text = show(v), id));
    }

    private static readonly Brush KeyGold = new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15));

    private ToggleButton Stopwatch(bool on)
    {
        var t = new ToggleButton
        {
            Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            IsChecked = on, Width = 26, Height = 24, ToolTip = "Animate: turn on to set keyframes over time",
            Style = (Style)XamlReader.Parse(ToolToggleStyleSmall),
        };
        return t;
    }

    private const string ToolToggleStyleSmall = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ToggleButton'>
  <Setter Property='Cursor' Value='Hand'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='ToggleButton'>
        <Border x:Name='Bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='Transparent' CornerRadius='5'>
          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='{DynamicResource LineBrush}'/></Trigger>
          <Trigger Property='IsChecked' Value='True'><Setter TargetName='Bd' Property='Background' Value='{DynamicResource AccentBrush}'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

    /// <summary>
    /// A property that can be animated: stopwatch, previous / add-remove / next keyframe, a slider and the value at the playhead.
    /// With the stopwatch off the value is one number for the whole clip; on, every change sets a keyframe at the playhead.
    /// </summary>
    private void KeyRow(IReadOnlyList<Clip> clips, string label, string prop, Func<Clip, double> plain, Action<Clip, double> setPlain,
                        double min, double max, Func<double, string> show, string id)
    {
        var first = clips[0];
        double now = _s.Playhead;
        double value = first.ValueAt(prop, now, plain(first));
        bool animated = clips.Any(c => c.IsAnimated(prop));
        bool keyHere = first.Keys.TryGetValue(prop, out var list) && list.Any(k => Math.Abs(first.Start + k.T - now) < VidSession.KeyEps);

        var head = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var watch = Stopwatch(animated);
        System.Windows.Automation.AutomationProperties.SetAutomationId(watch, id + "Watch");
        head.Children.Add(watch);
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        Grid.SetColumn(name, 1);
        head.Children.Add(name);
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        Button Small(string glyph, string tip, bool enabled, Action click, string? bid = null)
        {
            var b = new Button
            {
                Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center },
                Width = 24, Height = 22, ToolTip = tip, IsEnabled = enabled, Margin = new Thickness(0, 0, 2, 0), Padding = new Thickness(0),
                Style = (Style)XamlReader.Parse(
                    "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Setter Property='Cursor' Value='Hand'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>" +
                    "<Border x:Name='Bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='Transparent' CornerRadius='5'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
                    "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='{DynamicResource LineBrush}'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.35'/></Trigger></ControlTemplate.Triggers>" +
                    "</ControlTemplate></Setter.Value></Setter></Style>"),
            };
            if (bid != null) System.Windows.Automation.AutomationProperties.SetAutomationId(b, bid);
            b.Click += (_, _) => click();
            return b;
        }
        double? prev = animated ? VidSession.PreviousKeyTime(first, prop, now) : null;
        double? next = animated ? VidSession.NextKeyTime(first, prop, now) : null;
        tools.Children.Add(Small("", "Previous keyframe", prev != null, () => { SeekTo(prev!.Value); }));
        var diamond = Small(keyHere ? "" : "", keyHere ? "Delete the keyframe here" : "Add a keyframe here", animated, () =>
        {
            if (keyHere) _s.Do("Delete a keyframe", () => { foreach (var c in clips) RemoveKeyNow(c, prop, now); });
            else _s.Do("Add a keyframe", () => { foreach (var c in clips) _s.PutKey(c, prop, now, c.ValueAt(prop, now, plain(c))); });
        }, id + "Key");
        tools.Children.Add(diamond);
        tools.Children.Add(Small("", "Next keyframe", next != null, () => { SeekTo(next!.Value); }));
        var valueText = new TextBlock { Text = show(value), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        valueText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Grid.SetColumn(valueText, 2);
        head.Children.Add(valueText);
        Grid.SetColumn(tools, 3);
        head.Children.Add(tools);
        _inspector.Children.Add(head);

        watch.Click += (_, _) =>
        {
            if (watch.IsChecked == true)
                _s.Do("Animate " + label, () => { foreach (var c in clips) _s.PutKey(c, prop, now, c.ValueAt(prop, now, plain(c))); });
            else
                _s.Do("Stop animating " + label, () =>
                {
                    foreach (var c in clips)
                    {
                        double v = c.ValueAt(prop, now, plain(c));
                        c.Keys.Remove(prop);
                        setPlain(c, v);
                    }
                });
        };

        var slider = MakeSlider(value, min, max, v =>
        {
            foreach (var c in clips)
            {
                if (c.IsAnimated(prop)) _s.PutKey(c, prop, now, v);
                else setPlain(c, v);
            }
        }, label, v => valueText.Text = show(v), id);
        _inspector.Children.Add(slider);
    }

    private static void RemoveKeyNow(Clip c, string prop, double now)
    {
        if (!c.Keys.TryGetValue(prop, out var list)) return;
        double t = now - c.Start;
        list.RemoveAll(k => Math.Abs(k.T - t) < VidSession.KeyEps);
        if (list.Count == 0) c.Keys.Remove(prop);
    }

    // ---------- the panel ----------
    private void RefreshInspector()
    {
        if (_inspector == null) return;
        _inspectorBuilding = true;
        try
        {
            _inspector.Children.Clear();
            var sel = _s.WithLinked(_s.SelectedClips).ToList();
            if (sel.Count == 0) BuildProjectPanel(); else BuildClipPanel(sel);
            ApplySections();
        }
        finally { _inspectorBuilding = false; }
    }

    private void BuildProjectPanel()
    {
        var p = _s.Project;
        _inspector.Children.Add(Heading("Sequence settings"));
        _inspector.Children.Add(Hint("Choose a clip on the timeline to see its Motion, Opacity, Audio and Effects here."));
        _inspector.Children.Add(SectionHeader("Picture size"));
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
        _inspector.Children.Add(SectionHeader("Frames per second"));
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
        _inspector.Children.Add(SectionHeader("About this sequence"));
        _inspector.Children.Add(Hint($"{clips} clip{(clips == 1 ? "" : "s")}  ·  {VidTimeline.TimeText(p.Duration)} long  ·  {p.Media.Count} file{(p.Media.Count == 1 ? "" : "s")}  ·  {p.Markers.Count} marker{(p.Markers.Count == 1 ? "" : "s")}"));
    }

    private static readonly string[] TitleColors = { "#FFFFFF", "#000000", "#FFD43B", "#FF4D5E", "#4ADE80", "#38BDF8", "#8B5CF6", "#FB923C" };

    private void BuildClipPanel(List<Clip> sel)
    {
        var project = _s.Project;
        var tracks = sel.ToDictionary(c => c, c => project.TrackOf(c));
        var videos = sel.Where(c => tracks[c]?.Kind == TrackKind.Video).ToList();
        var sounds = sel.Where(c => tracks[c]?.Kind == TrackKind.Audio).ToList();
        var primary = videos.OrderBy(c => c.Start).FirstOrDefault() ?? sounds.OrderBy(c => c.Start).First();
        var media = project.MediaOf(primary);
        bool image = media?.Kind == MediaKind.Image;
        bool title = primary.Kind == ClipKind.Text;
        var texts = videos.Where(c => c.Kind == ClipKind.Text).ToList();
        var pictures = videos.Where(c => c.Kind == ClipKind.Media).ToList();
        var motion = title ? texts : pictures.Count > 0 ? pictures : videos;

        _inspector.Children.Add(Heading(title ? "Title" : (primary.Label ?? media?.Name ?? "Clip")));
        string kind = title ? "title" : videos.Count > 0 && sounds.Count > 0 ? "picture and sound" : videos.Count > 0 ? "picture" : "sound";
        string many = sel.Count > 2 || (videos.Count > 1 || sounds.Count > 1) ? $"{sel.Count} clips chosen  ·  " : "";
        _inspector.Children.Add(Hint(many + kind));

        if (title) BuildTitleLook(texts, primary);

        if (motion.Count > 0)
        {
            _inspector.Children.Add(SectionHeader("Motion"));
            KeyRow(motion, "Position across", "X", c => c.X, (c, v) => c.X = Math.Round(v, 3), 0, 1, v => $"{v * 100:0.#} %", "VidInspX");
            KeyRow(motion, "Position down", "Y", c => c.Y, (c, v) => c.Y = Math.Round(v, 3), 0, 1, v => $"{v * 100:0.#} %", "VidInspY");
            if (!title)
            {
                KeyRow(motion, "Scale", "Scale", c => c.Scale, (c, v) => c.Scale = Math.Round(v, 3), 0.05, 3, v => $"{v * 100:0} %", "VidInspScale");
                KeyRow(motion, "Rotation", "Rotation", c => c.Rotation, (c, v) => c.Rotation = Math.Round(v, 1), -360, 360, v => $"{v:0.#}°", "VidInspRotation");
            }
            var centre = MakeButton("", "Reset motion", "Put the picture back in the middle at its natural size", () =>
                _s.SetProperty(motion, "Reset motion", c => { c.X = 0.5; c.Y = 0.5; c.Scale = 1; c.Rotation = 0; c.Keys.Remove("X"); c.Keys.Remove("Y"); c.Keys.Remove("Scale"); c.Keys.Remove("Rotation"); }));
            centre.HorizontalAlignment = HorizontalAlignment.Left;
            _inspector.Children.Add(centre);
            _inspector.Children.Add(SectionHeader("Opacity"));
            KeyRow(motion, "Opacity", "Opacity", c => c.Opacity, (c, v) => c.Opacity = Math.Round(v, 2), 0, 1, v => $"{v * 100:0} %", "VidInspOpacity");
        }

        _inspector.Children.Add(SectionHeader("Time"));
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
        if (!image && !title)
            NumberRow("Speed", "×", primary.Speed, "0.##", v =>
            {
                if (!_s.SetSpeed(primary, v)) SetStatus("There is another clip in the way: make room first.");
            }, "VidInspSpeed");

        if (sounds.Count > 0)
        {
            _inspector.Children.Add(SectionHeader("Audio"));
            KeyRow(sounds, "Volume", "Volume", c => c.Volume, (c, v) => c.Volume = Math.Round(v, 2), 0, 2, v => $"{v * 100:0} %", "VidInspVolume");
            var track = project.TrackOf(sounds[0]);
            if (track != null)
            {
                _inspector.Children.Add(SectionHeader("Track: " + track.Name));
                SliderRow("Track volume", track.Volume, 0, 2, v => $"{v * 100:0} %", v => track.Volume = Math.Round(v, 2), "Track volume", "VidTrackVolume");
                SliderRow("Balance (left to right)", track.Pan, -1, 1, v => v < -0.01 ? $"{-v * 100:0} % left" : v > 0.01 ? $"{v * 100:0} % right" : "centre", v => track.Pan = Math.Round(v, 2), "Balance", "VidTrackPan");
                var flags = new WrapPanel();
                var mute = new CheckBox { Content = "Mute", IsChecked = track.Muted, Margin = new Thickness(0, 0, 14, 8) };
                mute.Checked += (_, _) => { if (!_inspectorBuilding) _s.Do("Mute track", () => track.Muted = true); };
                mute.Unchecked += (_, _) => { if (!_inspectorBuilding) _s.Do("Unmute track", () => track.Muted = false); };
                var solo = new CheckBox { Content = "Solo", IsChecked = track.Solo, Margin = new Thickness(0, 0, 0, 8) };
                solo.Checked += (_, _) => { if (!_inspectorBuilding) _s.Do("Solo track", () => track.Solo = true); };
                solo.Unchecked += (_, _) => { if (!_inspectorBuilding) _s.Do("Unsolo track", () => track.Solo = false); };
                flags.Children.Add(mute); flags.Children.Add(solo);
                _inspector.Children.Add(flags);
            }
        }

        if (videos.Count > 0)
        {
            _inspector.Children.Add(SectionHeader("Transition in"));
            _inspector.Children.Add(Hint("How this clip comes in over the clip before it on the same track."));
            var chips = new WrapPanel();
            var chipStyle = (Style)FindResource("ChipButton");
            var kinds = new List<(TransitionKind Kind, string Name)> { (TransitionKind.None, "None") };
            kinds.AddRange(VidEffects.Transitions);
            foreach (var (k, n) in kinds)
            {
                var chip = new RadioButton { Content = n, Style = chipStyle, GroupName = "vidTrans", IsChecked = primary.Transition == k };
                System.Windows.Automation.AutomationProperties.SetAutomationId(chip, "VidTrans" + k);
                chip.Checked += (_, _) =>
                {
                    if (_inspectorBuilding || primary.Transition == k) return;
                    _s.SetTransition(videos, k, primary.TransitionLength);
                };
                chips.Children.Add(chip);
            }
            _inspector.Children.Add(chips);
            if (primary.Transition != TransitionKind.None)
                NumberRow("Duration", "s", primary.TransitionLength, "0.0#", v => _s.SetTransition(videos, primary.Transition, v), "VidInspTransLength");
        }

        // effects: each is a card with its numbers
        if (!title)
        {
            _inspector.Children.Add(SectionHeader("Effects"));
            foreach (var c in new[] { primary }.Concat(sel.Where(x => x != primary && tracks[x]?.Kind != tracks[primary]?.Kind).Take(1)))
                foreach (var e in c.Effects.ToList()) EffectCard(c, e);
            var add = MakeButton("", "Add an effect", "Pick an effect for the chosen clip (or double-click one in the Effects panel)", () => ShowEffectMenu(sel), id: "VidAddEffect");
            add.HorizontalAlignment = HorizontalAlignment.Left;
            _inspector.Children.Add(add);
        }

        _inspector.Children.Add(SectionHeader("Fades"));
        NumberRow("Fade in", "s", primary.FadeIn, "0.0#", v => _s.SetProperty(sel, "Fade in", c => c.FadeIn = Math.Clamp(v, 0, Math.Max(0, c.Length - c.FadeOut))), "VidInspFadeIn");
        NumberRow("Fade out", "s", primary.FadeOut, "0.0#", v => _s.SetProperty(sel, "Fade out", c => c.FadeOut = Math.Clamp(v, 0, Math.Max(0, c.Length - c.FadeIn))), "VidInspFadeOut");
    }

    private void ShowEffectMenu(List<Clip> sel)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("ThemedMenu"), Placement = PlacementMode.MousePoint };
        string? group = null;
        foreach (var info in VidEffects.All)
        {
            if (info.Group != group)
            {
                if (group != null) menu.Items.Add(new Separator { Style = (Style)FindResource("ThemedSeparator") });
                group = info.Group;
                var head = new MenuItem { Header = group, IsEnabled = false, Style = (Style)FindResource("ThemedMenuItem"), FontWeight = FontWeights.SemiBold };
                menu.Items.Add(head);
            }
            string type = info.Type;
            menu.Items.Add(Item(info.Name, () => ApplyEffect(type)));
        }
        menu.IsOpen = true;
    }

    /// <summary>Applies an effect to the chosen clips (used by the Effect Controls menu and by a double-click in the Effects panel).</summary>
    private void ApplyEffect(string type)
    {
        int n = _s.AddEffect(_s.SelectedClips, type);
        if (n == 0) SetStatus("Choose a clip on the timeline first (a picture effect needs a picture clip, a sound effect a sound clip).");
    }

    private void EffectCard(Clip clip, ClipEffect e)
    {
        var info = VidEffects.Info(e.Type);
        var card = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(10, 8, 10, 4), Margin = new Thickness(0, 0, 0, 8) };
        card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        card.SetResourceReference(Border.BackgroundProperty, "BgBrush");
        var stack = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var remove = new Button { Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11 }, Width = 24, Height = 22, ToolTip = "Remove this effect", Padding = new Thickness(0), Style = (Style)FindResource("DialogButton") };
        remove.Click += (_, _) => _s.RemoveEffect(clip, e);
        DockPanel.SetDock(remove, Dock.Right);
        head.Children.Add(remove);
        var on = new CheckBox { IsChecked = e.On, Content = info?.Name ?? e.Type, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        on.Checked += (_, _) => { if (!_inspectorBuilding) _s.Do("Effect on", () => e.On = true); };
        on.Unchecked += (_, _) => { if (!_inspectorBuilding) _s.Do("Effect off", () => e.On = false); };
        head.Children.Add(on);
        stack.Children.Add(head);
        if (info != null)
            for (int i = 0; i < info.Params.Length; i++)
            {
                int index = i;
                var prm = info.Params[i];
                var row = new DockPanel();
                var val = new TextBlock { Text = FormatParam(prm, VidEffects.Get(e, index)) };
                val.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
                DockPanel.SetDock(val, Dock.Right);
                row.Children.Add(val);
                row.Children.Add(new TextBlock { Text = prm.Label, TextTrimming = TextTrimming.CharacterEllipsis });
                stack.Children.Add(row);
                var slider = MakeSlider(VidEffects.Get(e, index), prm.Min, prm.Max, v => VidEffects.Set(e, index, v), info.Name, v => val.Text = FormatParam(prm, v));
                stack.Children.Add(slider);
            }
        card.Child = stack;
        _inspector.Children.Add(card);
    }

    private static string FormatParam(EffectParam p, double v) => (p.Max - p.Min > 20 ? v.ToString("0") : v.ToString("0.##")) + (p.Unit.Length > 0 ? " " + p.Unit : "");

    /// <summary>The look of a title: letters, colour, font, bold, outline, background box.</summary>
    private void BuildTitleLook(List<Clip> texts, Clip primary)
    {
        var box = new TextBox
        {
            Text = primary.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 76, MaxHeight = 160, Padding = new Thickness(8, 6, 8, 6),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 6),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(box, "VidInspText");
        bool committed = false;
        void Commit()
        {
            if (committed || box.Text == primary.Text) return;
            committed = true;
            string t = box.Text;
            _s.SetProperty(texts, "Title text", c => { c.Text = t; c.Label = t.Split('\n')[0]; });
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Commit(); _timeline.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { box.Text = primary.Text; _timeline.Focus(); e.Handled = true; }
        };
        _inspector.Children.Add(box);
        _inspector.Children.Add(Hint("Click outside the box (or Ctrl+Enter) to apply."));
        _inspector.Children.Add(SectionHeader("Look"));
        SliderRow("Letter size", primary.TextSize, 0.02, 0.3, v => $"{v * 100:0} %", v => { foreach (var c in texts) c.TextSize = Math.Round(v, 3); }, "Letter size", "VidInspTextSize");
        var colours = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (string hex in TitleColors)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            bool on = string.Equals(primary.TextColor, hex, StringComparison.OrdinalIgnoreCase);
            var sw = new Button
            {
                Width = 26, Height = 26, Margin = new Thickness(0, 0, 6, 6), Background = new SolidColorBrush(color), Cursor = Cursors.Hand, ToolTip = hex,
                BorderThickness = new Thickness(on ? 3 : 1),
                Template = (ControlTemplate)XamlReader.Parse(
                    "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border CornerRadius='13' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'/></ControlTemplate>"),
            };
            sw.SetResourceReference(Control.BorderBrushProperty, on ? "AccentBrush" : "MutedBrush");
            System.Windows.Automation.AutomationProperties.SetAutomationId(sw, "VidTitleColor" + hex.Substring(1));
            sw.Click += (_, _) => _s.SetProperty(texts, "Title colour", c => c.TextColor = hex);
            colours.Children.Add(sw);
        }
        _inspector.Children.Add(colours);
        var fonts = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        var chipStyle = (Style)FindResource("ChipButton");
        foreach (string f in VidFonts.Available())
        {
            var chip = new RadioButton { Content = f, Style = chipStyle, GroupName = "vidFont", IsChecked = primary.TextFont == f, FontFamily = new FontFamily(f) };
            chip.Checked += (_, _) => { if (!_inspectorBuilding && primary.TextFont != f) _s.SetProperty(texts, "Font", c => c.TextFont = f); };
            fonts.Children.Add(chip);
        }
        _inspector.Children.Add(fonts);
        var flags = new WrapPanel();
        var bold = new CheckBox { Content = "Bold", IsChecked = primary.TextBold, Margin = new Thickness(0, 0, 14, 8) };
        bold.Checked += (_, _) => { if (!_inspectorBuilding) _s.SetProperty(texts, "Bold", c => c.TextBold = true); };
        bold.Unchecked += (_, _) => { if (!_inspectorBuilding) _s.SetProperty(texts, "Bold", c => c.TextBold = false); };
        var outline = new CheckBox { Content = "Outline", IsChecked = primary.TextOutline, Margin = new Thickness(0, 0, 0, 8) };
        outline.Checked += (_, _) => { if (!_inspectorBuilding) _s.SetProperty(texts, "Outline", c => c.TextOutline = true); };
        outline.Unchecked += (_, _) => { if (!_inspectorBuilding) _s.SetProperty(texts, "Outline", c => c.TextOutline = false); };
        flags.Children.Add(bold); flags.Children.Add(outline);
        _inspector.Children.Add(flags);
        SliderRow("Dark box behind the words", primary.TextBoxOpacity, 0, 1, v => v < 0.02 ? "none" : $"{v * 100:0} %", v => { foreach (var c in texts) c.TextBoxOpacity = Math.Round(v, 2); }, "Box", "VidInspTextBox");
    }
}
