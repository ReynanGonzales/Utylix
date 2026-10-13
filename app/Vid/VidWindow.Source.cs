using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace IdmClone.Vid;

/// <summary>A thin bar under a monitor: click or drag to move through the clip; shows the In / Out range.</summary>
public sealed class VidScrub : FrameworkElement
{
    public double Duration { get; set; }
    public double Position { get; set; }
    public double? In { get; set; }
    public double? Out { get; set; }
    public event Action<double>? Seek;

    public VidScrub() { Height = 26; Cursor = Cursors.Hand; Focusable = false; }

    private double X(double t) => 8 + Math.Clamp(t / Math.Max(0.001, Duration), 0, 1) * Math.Max(1, ActualWidth - 16);

    protected override void OnRender(DrawingContext dc)
    {
        var track = (Brush)Application.Current.FindResource("TrackBrush");
        var accentColor = ((SolidColorBrush)Application.Current.FindResource("AccentBrush")).Color;
        double y = ActualHeight / 2;
        dc.DrawRoundedRectangle(track, null, new Rect(8, y - 3, Math.Max(1, ActualWidth - 16), 6), 3, 3);
        if (In != null || Out != null)
        {
            double a = X(In ?? 0), b = X(Out ?? Duration);
            dc.DrawRoundedRectangle(new SolidColorBrush(accentColor), null, new Rect(a, y - 3, Math.Max(2, b - a), 6), 3, 3);
            var bracket = new Pen(Brushes.White, 2);
            if (In != null) { dc.DrawLine(bracket, new Point(a, y - 9), new Point(a, y + 9)); dc.DrawLine(bracket, new Point(a, y - 9), new Point(a + 5, y - 9)); dc.DrawLine(bracket, new Point(a, y + 9), new Point(a + 5, y + 9)); }
            if (Out != null) { dc.DrawLine(bracket, new Point(b, y - 9), new Point(b, y + 9)); dc.DrawLine(bracket, new Point(b, y - 9), new Point(b - 5, y - 9)); dc.DrawLine(bracket, new Point(b, y + 9), new Point(b - 5, y + 9)); }
        }
        double px = X(Position);
        var red = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        dc.DrawLine(new Pen(red, 2), new Point(px, 2), new Point(px, ActualHeight - 2));
        dc.DrawEllipse(red, null, new Point(px, 4), 4, 4);
    }

    private bool _down;
    private void Move(MouseEventArgs e)
    {
        double w = Math.Max(1, ActualWidth - 16);
        double t = Math.Clamp((e.GetPosition(this).X - 8) / w, 0, 1) * Duration;
        Seek?.Invoke(t);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { _down = true; CaptureMouse(); Move(e); }
    protected override void OnMouseMove(MouseEventArgs e) { if (_down) Move(e); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { _down = false; ReleaseMouseCapture(); }
}

public sealed partial class VidWindow
{
    // ----- the Source monitor -----
    private readonly VidSession _src = new();
    private VidPlayer _srcPlayer = null!;
    private System.Windows.Controls.Image _srcPicture = null!;
    private TextBlock _srcTime = null!, _srcDuration = null!, _srcName = null!, _srcMarks = null!;
    private VidScrub _srcScrub = null!;
    private MediaItem? _srcMedia;
    private double? _srcIn, _srcOut;
    private bool _sourceActive;
    private Action<int> _selectTopLeft = _ => { };
    private Action<int> _selectBottomLeft = _ => { };
    private StackPanel _effectsList = null!;

    private Border TabbedPanel(string[] titles, UIElement[] bodies, out TextBlock[] titleBlocks, out Action<int> select)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        var blocks = new TextBlock[titles.Length];
        var tabs = new Border[titles.Length];
        var content = new Grid();
        for (int i = 0; i < titles.Length; i++)
        {
            var tb = new TextBlock { Text = titles[i], FontWeight = FontWeights.SemiBold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
            var tab = new Border { Child = tb, BorderThickness = new Thickness(0, 0, 0, 2), BorderBrush = Brushes.Transparent, Cursor = Cursors.Hand, Background = Brushes.Transparent };
            System.Windows.Automation.AutomationProperties.SetAutomationId(tab, "VidTab" + titles[i].Replace(" ", ""));
            blocks[i] = tb; tabs[i] = tab;
            head.Children.Add(tab);
            content.Children.Add(bodies[i]);
        }
        void Select(int index)
        {
            for (int i = 0; i < titles.Length; i++)
            {
                bool on = i == index;
                bodies[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (on) tabs[i].SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); else tabs[i].BorderBrush = Brushes.Transparent;
                blocks[i].Opacity = on ? 1 : 0.6;
            }
        }
        for (int i = 0; i < titles.Length; i++) { int k = i; tabs[i].MouseLeftButtonUp += (_, _) => Select(k); }
        Select(0);
        var headBorder = new Border { Child = head, BorderThickness = new Thickness(0, 0, 0, 1) };
        headBorder.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        headBorder.SetResourceReference(Border.BackgroundProperty, "BgBrush");
        Grid.SetRow(headBorder, 0);
        Grid.SetRow(content, 1);
        grid.Children.Add(headBorder);
        grid.Children.Add(content);
        titleBlocks = blocks;
        select = Select;
        return Card(grid);
    }

    private UIElement BuildSourceMonitor()
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var screen = new Border { Background = Brushes.Black, ClipToBounds = true };
        _srcPicture = new System.Windows.Controls.Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(_srcPicture, BitmapScalingMode.HighQuality);
        _srcName = new TextBlock { Text = "Double-click a file in the Project panel to open it here.", Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(20) };
        var stack = new Grid();
        stack.Children.Add(_srcPicture);
        stack.Children.Add(_srcName);
        screen.Child = stack;
        Grid.SetRow(screen, 0);
        g.Children.Add(screen);

        _srcScrub = new VidScrub { Margin = new Thickness(8, 6, 8, 0) };
        _srcScrub.Seek += t => SrcSeek(t);
        Grid.SetRow(_srcScrub, 1);
        g.Children.Add(_srcScrub);

        var times = new Grid { Margin = new Thickness(12, 4, 12, 0) };
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _srcTime = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = TimecodeBrush, Text = "00:00:00:00" };
        _srcMarks = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        _srcMarks.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _srcDuration = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Right, Text = "00:00:00:00" };
        _srcDuration.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Grid.SetColumn(_srcMarks, 1); Grid.SetColumn(_srcDuration, 2);
        times.Children.Add(_srcTime); times.Children.Add(_srcMarks); times.Children.Add(_srcDuration);
        Grid.SetRow(times, 2);
        g.Children.Add(times);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 8) };
        bar.Children.Add(MakeButton("", "", "Back one frame", () => SrcSeek(_src.Playhead - 1.0 / SrcFps), id: "VidSrcBack"));
        var play = MakeButton("", "", "Play / pause the source", () => SrcToggle(), id: "VidSrcPlay");
        bar.Children.Add(play);
        bar.Children.Add(MakeButton("", "", "Forward one frame", () => SrcSeek(_src.Playhead + 1.0 / SrcFps), id: "VidSrcForward"));
        bar.Children.Add(new Border { Width = 1, Margin = new Thickness(4, 4, 10, 4), Background = (Brush)FindResource("LineBrush") });
        bar.Children.Add(MakeButton("", "In", "Mark In (I)", () => SrcMarkIn(), id: "VidSrcMarkIn"));
        bar.Children.Add(MakeButton("", "Out", "Mark Out (O)", () => SrcMarkOut(), id: "VidSrcMarkOut"));
        bar.Children.Add(new Border { Width = 1, Margin = new Thickness(4, 4, 10, 4), Background = (Brush)FindResource("LineBrush") });
        bar.Children.Add(MakeButton("", "Insert", "Put it into the sequence at the playhead and push everything after it along (,)", () => SrcPlace(false), id: "VidSrcInsert"));
        bar.Children.Add(MakeButton("", "Overwrite", "Put it into the sequence at the playhead, replacing what is there (.)", () => SrcPlace(true), id: "VidSrcOverwrite"));
        Grid.SetRow(bar, 3);
        g.Children.Add(bar);

        _srcPlayer = new VidPlayer(_src);
        _srcPicture.Source = _srcPlayer.Bitmap;
        _srcPlayer.BitmapChanged += () => _srcPicture.Source = _srcPlayer.Bitmap;
        _srcPlayer.Position += t => SrcUpdate();
        _srcPlayer.Stopped += () => SrcUpdate();
        g.PreviewMouseDown += (_, _) => { _sourceActive = true; };
        return g;
    }

    private double SrcFps => _src.Project.Fps > 0 ? _src.Project.Fps : 30;

    private void SrcUpdate()
    {
        _srcTime.Text = VidTimeline.Timecode(_src.Playhead, SrcFps);
        _srcDuration.Text = VidTimeline.Timecode(_src.Project.Duration, SrcFps);
        _srcScrub.Duration = _src.Project.Duration;
        _srcScrub.Position = _src.Playhead;
        _srcScrub.In = _srcIn; _srcScrub.Out = _srcOut;
        _srcScrub.InvalidateVisual();
        _srcMarks.Text = _srcIn == null && _srcOut == null ? "" :
            $"In {(_srcIn != null ? VidTimeline.Timecode(_srcIn.Value, SrcFps) : "start")}   Out {(_srcOut != null ? VidTimeline.Timecode(_srcOut.Value, SrcFps) : "end")}";
    }

    /// <summary>Opens a file in the Source monitor.</summary>
    private void LoadSource(MediaItem m)
    {
        if (!File.Exists(m.Path)) { UMessage.Show(this, "This file is not where it was:\n" + m.Path, "Utylix Vid Editor", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _srcPlayer.Pause();
        var p = VidProject.NewEmpty();
        p.Media.Add(m);
        if (m.HasVideo && m.Width > 0)
        {
            double scale = Math.Min(1, 1280.0 / m.Width);
            p.Width = Math.Max(2, (int)Math.Round(m.Width * scale) / 2 * 2);
            p.Height = Math.Max(2, (int)Math.Round(m.Height * scale) / 2 * 2);
            double[] standard = { 24, 25, 30, 48, 50, 60 };
            if (m.Fps > 0) p.Fps = standard.OrderBy(f => Math.Abs(f - m.Fps)).First();
        }
        double len = m.Kind == MediaKind.Image ? 5 : m.Duration;
        if (m.HasVideo) p.Tracks.First(t => t.Kind == TrackKind.Video).Clips.Add(new Clip { MediaId = m.Id, Length = len });
        if (m.HasAudio) p.Tracks.First(t => t.Kind == TrackKind.Audio).Clips.Add(new Clip { MediaId = m.Id, Length = len });
        _src.Load(p);
        _srcMedia = m;
        _srcIn = _srcOut = null;
        _srcName.Visibility = Visibility.Collapsed;
        _selectTopLeft(0);
        _sourceActive = true;
        SrcUpdate();
        _srcPlayer.Refresh();
        _srcPlayer.ShowFrame(0);
    }

    private void SrcSeek(double t)
    {
        if (_srcMedia == null) return;
        if (_srcPlayer.IsPlaying) _srcPlayer.Pause();
        _src.Playhead = Math.Clamp(t, 0, _src.Project.Duration);
        SrcUpdate();
        _srcPlayer.ShowFrame(_src.Playhead);
    }

    private void SrcToggle()
    {
        if (_srcMedia == null) return;
        if (_player.IsPlaying) { _player.Pause(); UpdatePlayGlyph(); }
        _srcPlayer.Toggle();
    }

    private void SrcMarkIn() { if (_srcMedia == null) return; _srcIn = _src.Playhead; if (_srcOut != null && _srcOut <= _srcIn) _srcOut = null; SrcUpdate(); }
    private void SrcMarkOut() { if (_srcMedia == null) return; _srcOut = _src.Playhead; if (_srcIn != null && _srcIn >= _srcOut) _srcIn = null; SrcUpdate(); }

    private void SrcPlace(bool overwrite)
    {
        if (_srcMedia == null) { SetStatus("Open a file in the Source monitor first (double-click it in the Project panel)."); return; }
        var m = _srcMedia;
        double a = _srcIn ?? 0, b = _srcOut ?? _src.Project.Duration;
        if (b - a < VidSession.MinLength) { SetStatus("The In and Out points leave nothing to put in."); return; }
        double at = _s.Playhead;
        var clips = _s.Place(m, a, b, at, overwrite);
        if (clips.Count > 0)
        {
            _s.Select(clips);
            SeekTo(clips.Max(c => c.End));
            _timeline.EnsureVisible(at);
        }
    }

    // ----- the Effects panel -----
    private UIElement BuildEffectsPanel()
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var search = new TextBox { Margin = new Thickness(8, 8, 8, 4), Padding = new Thickness(8, 5, 8, 5), ToolTip = "Search effects" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(search, "VidEffectsSearch");
        search.TextChanged += (_, _) => RebuildEffects(search.Text);
        Grid.SetRow(search, 0);
        g.Children.Add(search);
        _effectsList = new StackPanel { Margin = new Thickness(8, 4, 8, 8) };
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _effectsList };
        Grid.SetRow(scroll, 1);
        g.Children.Add(scroll);
        RebuildEffects("");
        return g;
    }

    private void RebuildEffects(string filter)
    {
        _effectsList.Children.Clear();
        string f = filter.Trim();
        bool Match(string s) => f.Length == 0 || s.IndexOf(f, StringComparison.CurrentCultureIgnoreCase) >= 0;
        void Group(string name)
        {
            var t = new TextBlock { Text = name.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 10, 0, 4) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            _effectsList.Children.Add(t);
        }
        UIElement Row(string title, string glyph, Action apply, string id, string? drag = null)
        {
            var d = new DockPanel();
            d.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, Width = 24, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 });
            d.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
            var b = new Border { Child = d, Padding = new Thickness(6, 6, 6, 6), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Double-click to apply to the chosen clip" };
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, id);
            b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            Point down = default; bool pressed = false;
            b.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { apply(); return; } down = e.GetPosition(b); pressed = true; };
            b.MouseLeftButtonUp += (_, _) => pressed = false;
            b.MouseMove += (_, e) =>
            {
                if (!pressed || drag == null || e.LeftButton != MouseButtonState.Pressed) { pressed = false; return; }
                var pos = e.GetPosition(b);
                if (Math.Abs(pos.X - down.X) + Math.Abs(pos.Y - down.Y) < 8) return;
                pressed = false;
                var data = new DataObject();
                data.SetData("UtylixVidEffect", drag);
                DragDrop.DoDragDrop(b, data, DragDropEffects.Copy);
            };
            if (drag != null) b.ToolTip = "Drag onto a clip on the timeline, or double-click to apply to the chosen clip";
            return b;
        }
        string? last = null;
        foreach (var info in VidEffects.All.Where(i => Match(i.Name) || Match(i.Group)))
        {
            if (info.Group != last) { Group(info.Group); last = info.Group; }
            string type = info.Type;
            _effectsList.Children.Add(Row(info.Name, info.Audio ? "" : "", () => ApplyEffect(type), "VidFx_" + type, "fx:" + type));
        }
        if (Match("Remove the background AI cut out person"))
        {
            Group("AI tools");
            _effectsList.Children.Add(Row("Remove the background (AI)…", "\uE7F8", RemoveBackground, "VidAiMatte"));
        }
        var trans = VidEffects.Transitions.Where(t => Match(t.Name) || Match("Transitions")).ToList();
        if (trans.Count > 0)
        {
            Group("Video transitions");
            foreach (var (kind, name) in trans)
            {
                var k = kind;
                _effectsList.Children.Add(Row(name, "", () =>
                {
                    var targets = _s.SelectedClips.Where(c => _s.Project.TrackOf(c)?.Kind == TrackKind.Video).ToList();
                    if (targets.Count == 0) { SetStatus("Choose a picture clip first: the transition goes at its start."); return; }
                    _s.SetTransition(targets, k, 1.0);
                }, "VidTr_" + k, "tr:" + k));
            }
        }
        if (_effectsList.Children.Count == 0)
        {
            var none = new TextBlock { Text = "No effect has that name.", Margin = new Thickness(6) };
            none.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            _effectsList.Children.Add(none);
        }
    }
}
