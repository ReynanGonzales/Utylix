using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace IdmClone.Vid;

/// <summary>The editor's workspace, laid out like Premiere Pro's "Editing" workspace: menu bar; Source monitor / Effect Controls and the Program monitor
/// on top; Project / Effects panel, tools and the Timeline (with audio meters) below. Panels are separated by splitters you can drag.</summary>
public sealed partial class VidWindow
{
    private TextBlock _durationLabel = null!, _tlTimeLabel = null!, _programTitle = null!, _timelineTitle = null!, _projectTitle = null!, _qualityText = null!;
    private VidMeters _meters = null!;
    private ColumnDefinition _topLeftColumn = null!, _bottomLeftColumn = null!;
    private readonly Dictionary<string, (Border Tab, TextBlock Label)> _workspaceTabs = new();
    private string _workspace = "Editing";
    private TextBlock _zoomText = null!;
    private VidScrub _programScrub = null!;
    private double _monitorZoomPct;
    private bool _safeMargins;
    private ToggleButton _snapToggle = null!;
    private readonly Dictionary<VidTool, ToggleButton> _toolButtons = new();

    private const string FlatButtonStyle = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>
  <Setter Property='Cursor' Value='Hand'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Border x:Name='Bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='Transparent' CornerRadius='5' Padding='10,5'>
          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='{DynamicResource LineBrush}'/></Trigger>
          <Trigger Property='IsPressed' Value='True'><Setter TargetName='Bd' Property='Background' Value='{DynamicResource TrackBrush}'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

    private const string ToolToggleStyle = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ToggleButton'>
  <Setter Property='Cursor' Value='Hand'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='ToggleButton'>
        <Border x:Name='Bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='Transparent' CornerRadius='6' Width='36' Height='34'>
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

    private static readonly Brush TimecodeBrush = new SolidColorBrush(Color.FromRgb(0x5C, 0xC8, 0xFF));

    /// <summary>A panel with a title tab (like Premiere's) above its content.</summary>
    private Border Panel(string title, UIElement body, out TextBlock titleBlock, UIElement? headerRight = null)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = new DockPanel { LastChildFill = false };
        titleBlock = new TextBlock { Text = title, FontWeight = FontWeights.Normal, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var tabContent = new StackPanel { Orientation = Orientation.Horizontal };
        tabContent.Children.Add(titleBlock);
        tabContent.Children.Add(new TextBlock { Text = "\uE700", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        var tab = new Border { Child = tabContent, BorderThickness = new Thickness(0, 0, 0, 2) };
        tab.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        DockPanel.SetDock(tab, Dock.Left);
        head.Children.Add(tab);
        if (headerRight != null) { DockPanel.SetDock(headerRight, Dock.Right); head.Children.Add(headerRight); }
        var headBorder = new Border { Child = head, BorderThickness = new Thickness(0, 0, 0, 1) };
        headBorder.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        headBorder.SetResourceReference(Border.BackgroundProperty, "BgBrush");
        Grid.SetRow(headBorder, 0);
        Grid.SetRow(body, 1);
        grid.Children.Add(headBorder);
        grid.Children.Add(body);
        return Card(grid);
    }

    /// <summary>A small drop-down like Premiere's "Fit" and "Full" boxes in the Program monitor: its text and a chevron, a menu on click.</summary>
    private Button Dropdown(string text, out TextBlock label, string id, params (string Label, Action Click)[] items)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        label = new TextBlock { Text = text, FontSize = 13 };
        content.Children.Add(label);
        content.Children.Add(new TextBlock { Text = "\uE70D", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 9, Margin = new Thickness(6, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = content, Style = (Style)XamlReader.Parse(FlatButtonStyle), Margin = new Thickness(0, 0, 4, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(b, id);
        b.Click += (_, _) => ShowMenu(b, items.Select(i => (i.Label, (Action?)i.Click, (string?)null)).ToArray());
        return b;
    }

    /// <summary>The picture at a fixed size in the Program monitor (percent of the sequence's own size), or fitted (0).</summary>
    private void SetMonitorZoom(double pct)
    {
        _monitorZoomPct = pct;
        _zoomText.Text = pct <= 0 ? "Fit" : pct.ToString("0") + " %";
        ApplyMonitorZoom();
    }

    private void SetPreviewQuality(double scale, string name)
    {
        _player.PreviewScale = scale;
        _qualityText.Text = name;
        _player.Refresh();
    }

    private void ToggleSafeMargins()
    {
        _safeMargins = !_safeMargins;
        RefreshOverlay();
    }

    private Button FlatButton(string text, Action<Button> click, string? id = null)
    {
        var b = new Button { Content = new TextBlock { Text = text, FontSize = 13 }, Style = (Style)XamlReader.Parse(FlatButtonStyle), Margin = new Thickness(0, 0, 2, 0) };
        if (id != null) System.Windows.Automation.AutomationProperties.SetAutomationId(b, id);
        b.Click += (_, _) => click(b);
        return b;
    }

    private void ShowMenu(Button anchor, params (string Header, Action? Click, string? Gesture)[] items)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("ThemedMenu"), PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var (header, click, gesture) in items)
        {
            if (header == "-") { menu.Items.Add(new Separator { Style = (Style)FindResource("ThemedSeparator") }); continue; }
            var mi = Item(header, () => click?.Invoke(), gesture);
            if (click == null) mi.IsEnabled = false;
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    private ToggleButton ToolButton(VidTool tool, UIElement icon, string tip, string id)
    {
        var t = new ToggleButton { Content = icon, ToolTip = tip, Style = (Style)XamlReader.Parse(ToolToggleStyle), Margin = new Thickness(0, 0, 0, 3), IsChecked = tool == VidTool.Select };
        System.Windows.Automation.AutomationProperties.SetAutomationId(t, id);
        t.Click += (_, _) => SetTool(tool);
        _toolButtons[tool] = t;
        return t;
    }

    private void SetTool(VidTool tool)
    {
        _timeline.Tool = tool;
        foreach (var kv in _toolButtons) kv.Value.IsChecked = kv.Key == tool;
        SetStatus(tool switch
        {
            VidTool.Razor => "Razor: click a clip to cut it there (Shift cuts every track).",
            VidTool.Hand => "Hand: drag the timeline sideways.",
            VidTool.Ripple => "Ripple edit: drag a clip's edge; everything after it moves with it.",
            VidTool.Rolling => "Rolling edit: drag the cut between two clips; their total length stays.",
            VidTool.Slip => "Slip: drag inside a clip to show another part of it in the same place.",
            VidTool.TrackForward => "Track select forward: click a clip to choose it and everything after it (Shift: all tracks).",
            _ => "",
        });
    }

    private static TextBlock Glyph(string text, double size = 17, string font = "Segoe MDL2 Assets") =>
        new() { Text = text, FontFamily = new FontFamily(font), FontSize = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    private UIElement SelectionArrowIcon()
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M4,2 L4,17 L8,13.5 L10.6,19 L13,18 L10.4,12.6 L15.5,12.6 Z"), Width = 20, Height = 21, Stretch = Stretch.Uniform,
            StrokeThickness = 0, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        path.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextBrush");
        return path;
    }

    private void BuildPremiereUi()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });                   // menu bar
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });                   // workspace bar
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 240 });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.95, GridUnitType.Star), MinHeight = 210 });
        Content = root;

        // ----- menu bar: File Edit Clip Sequence Markers Graphics View Window Help (Premiere's order) -----
        var bar = new DockPanel { Margin = new Thickness(8, 0, 10, 0) };
        var menus = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        menus.Children.Add(FlatButton("File", b => ShowMenu(b,
            ("New project", NewProject, "Ctrl+N"), ("Open project…", OpenProject, "Ctrl+O"), ("-", null, null),
            ("Save", () => SaveProject(false), "Ctrl+S"), ("Save as…", () => SaveProject(true), "Ctrl+Shift+S"), ("-", null, null),
            ("Import media…", ImportDialog, "Ctrl+I"), ("Export media…", Export, "Ctrl+M"), ("Export frame…", ExportFrame, "Ctrl+Shift+E"), ("-", null, null),
            ("Exit", Close, null)), "VidMenuFile"));
        menus.Children.Add(FlatButton("Edit", b => ShowMenu(b,
            ("Undo", () => _s.Undo(), "Ctrl+Z"), ("Redo", () => _s.Redo(), "Ctrl+Shift+Z"), ("-", null, null),
            ("Cut", () => _s.Cut(), "Ctrl+X"), ("Copy", () => _s.Copy(), "Ctrl+C"), ("Paste", () => PasteClips(), "Ctrl+V"),
            ("Duplicate", () => _s.Duplicate(), "Ctrl+Alt+D"), ("Clear", () => _s.Delete(false), "Del"), ("Ripple delete", () => _s.Delete(true), "Shift+Del"), ("-", null, null),
            ("Select all", () => _s.Select(_s.Project.AllClips), "Ctrl+A"), ("-", null, null),
            ("Settings…", OpenSettings, null)), "VidMenuEdit"));
        menus.Children.Add(FlatButton("Clip", b => ShowMenu(b,
            ("Remove the background (AI)…", RemoveBackground, null), ("-", null, null),
            ("Speed 0.5×", () => SetSelectedSpeed(0.5), null), ("Speed 1× (normal)", () => SetSelectedSpeed(1), null), ("Speed 2×", () => SetSelectedSpeed(2), null), ("-", null, null),
            ("Apply the default video transition", AddTransitionAtPlayhead, "Ctrl+D"),
            ("Add the cross dissolve", () => ApplyTransition(TransitionKind.Dissolve), null), ("Remove the transition", () => ApplyTransition(TransitionKind.None), null)), "VidMenuClip"));
        menus.Children.Add(FlatButton("Sequence", b => ShowMenu(b,
            ("Add edit (split at the playhead)", DoSplit, "Ctrl+K"),
            ("Lift", () => DoLift(), ";"), ("Extract", () => DoExtract(), "'"), ("-", null, null),
            ("Mark In", MarkIn, "I"), ("Mark Out", MarkOut, "O"), ("Clear In and Out", () => _s.ClearWorkArea(), "Ctrl+Shift+X"), ("-", null, null),
            ("Add a video track", () => _s.AddTrack(TrackKind.Video), null), ("Add a sound track", () => _s.AddTrack(TrackKind.Audio), null), ("-", null, null),
            ("Go to the previous edit point", () => GoToEdit(-1), "Up"), ("Go to the next edit point", () => GoToEdit(1), "Down"), ("-", null, null),
            ("Zoom in", () => ZoomBy(1.25), "+"), ("Zoom out", () => ZoomBy(0.8), "-"), ("Zoom to sequence", () => _timeline.ZoomToFit(), "\\")), "VidMenuSequence"));
        menus.Children.Add(FlatButton("Markers", b => ShowMenu(b,
            ("Add marker", AddMarkerHere, "M"), ("Go to the previous marker", () => GoToMarker(-1), "Ctrl+Shift+M"), ("Go to the next marker", () => GoToMarker(1), "Shift+M"), ("-", null, null),
            ("Clear all markers", () => _s.ClearMarkers(), null)), "VidMenuMarkers"));
        menus.Children.Add(FlatButton("Graphics", b => ShowMenu(b,
            ("New title (text)", AddTitle, "Ctrl+T"), ("-", null, null),
            ("Edit the chosen title", () => { var c = _s.SelectedClips.FirstOrDefault(x => x.Kind == ClipKind.Text); if (c != null) StartInplaceEdit(c); else SetStatus("Choose a title first."); }, null)), "VidMenuGraphics"));
        menus.Children.Add(FlatButton("View", b => ShowMenu(b,
            ("Show safe margins", ToggleSafeMargins, null), ("-", null, null),
            ("Playback quality: Full", () => SetPreviewQuality(1, "Full"), null), ("Playback quality: 1/2", () => SetPreviewQuality(0.5, "1/2"), null), ("Playback quality: 1/4", () => SetPreviewQuality(0.25, "1/4"), null), ("-", null, null),
            ("Zoom: Fit", () => SetMonitorZoom(0), null), ("Zoom: 100 %", () => SetMonitorZoom(100), null)), "VidMenuView"));
        menus.Children.Add(FlatButton("Window", b => ShowMenu(b,
            ("Workspaces: Assembly", () => SetWorkspace("Assembly"), null), ("Workspaces: Editing", () => SetWorkspace("Editing"), null), ("Workspaces: Color", () => SetWorkspace("Color"), null),
            ("Workspaces: Effects", () => SetWorkspace("Effects"), null), ("Workspaces: Audio", () => SetWorkspace("Audio"), null), ("Workspaces: Graphics", () => SetWorkspace("Graphics"), null), ("-", null, null),
            ("Source monitor", () => { _selectTopLeft(0); }, null), ("Effect Controls", () => { _selectTopLeft(1); }, null), ("-", null, null),
            ("Project", () => { _selectBottomLeft(0); }, null), ("Effects", () => { _selectBottomLeft(1); }, null)), "VidMenuWindow"));
        menus.Children.Add(FlatButton("Help", b => ShowMenu(b, ("Keyboard shortcuts…", ShowShortcuts, null)), "VidMenuHelp"));
        bar.Children.Add(menus);
        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        bar.Children.Add(_status);
        var barBorder = new Border { Child = bar };
        barBorder.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        Grid.SetRow(barBorder, 0);
        root.Children.Add(barBorder);

        // ----- workspace bar: Home, Learning ... Libraries, and the Export button on the right -----
        var wsGrid = new Grid { Margin = new Thickness(10, 0, 10, 0) };
        wsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        wsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        wsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var homeGlyph = Glyph("\uE80F", 17);
        homeGlyph.Margin = new Thickness(0, 0, 14, 0);
        homeGlyph.ToolTip = "Utylix Vid Editor";
        wsGrid.Children.Add(homeGlyph);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch };
        foreach (var name in new[] { "Learning", "Assembly", "Editing", "Color", "Effects", "Audio", "Graphics", "Captions", "Libraries" })
        {
            string ws = name;
            bool available = ws is "Assembly" or "Editing" or "Color" or "Effects" or "Audio" or "Graphics";
            var label = new TextBlock { Text = ws, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 14, 0), Opacity = available ? 0.85 : 0.45 };
            var tab = new Border { Child = label, Background = Brushes.Transparent, Cursor = available ? Cursors.Hand : Cursors.Arrow, BorderThickness = new Thickness(0, 0, 0, 2), BorderBrush = Brushes.Transparent };
            System.Windows.Automation.AutomationProperties.SetAutomationId(tab, "VidWs" + ws);
            tab.ToolTip = available ? null : ws + " is not part of Utylix Vid Editor.";
            tab.MouseLeftButtonUp += (_, _) => { if (available) SetWorkspace(ws); };
            _workspaceTabs[ws] = (tab, label);
            tabs.Children.Add(tab);
        }
        Grid.SetColumn(tabs, 1);
        wsGrid.Children.Add(tabs);
        var rightBar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _undoBtn = MakeButton("\uE7A7", "", "Undo (Ctrl+Z)", () => _s.Undo(), id: "VidUndo");
        _redoBtn = MakeButton("\uE7A6", "", "Redo (Ctrl+Shift+Z)", () => _s.Redo(), id: "VidRedo");
        _undoBtn.Margin = new Thickness(0, 0, 4, 0); _redoBtn.Margin = new Thickness(0, 0, 10, 0);
        var export = MakeButton("\uE896", "Export", "Export media (Ctrl+M)", Export, primary: true, id: "VidExport");
        export.Margin = new Thickness(0);
        rightBar.Children.Add(_undoBtn); rightBar.Children.Add(_redoBtn); rightBar.Children.Add(export);
        Grid.SetColumn(rightBar, 2);
        wsGrid.Children.Add(rightBar);
        var wsBorder = new Border { Child = wsGrid, BorderThickness = new Thickness(0, 0, 0, 1) };
        wsBorder.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        wsBorder.SetResourceReference(Border.BackgroundProperty, "BgBrush");
        Grid.SetRow(wsBorder, 1);
        root.Children.Add(wsBorder);

        // ----- top: Source / Effect Controls | Program monitor -----
        var top = new Grid { Margin = new Thickness(8, 8, 8, 0) };
        _topLeftColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 };
        top.ColumnDefinitions.Add(_topLeftColumn);
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 360 });
        Grid.SetRow(top, 2);
        root.Children.Add(top);

        var inspScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _inspector = new StackPanel { Margin = new Thickness(12, 8, 12, 12) };
        inspScroll.Content = _inspector;
        var topLeft = TabbedPanel(new[] { "Source", "Effect Controls" }, new UIElement[] { BuildSourceMonitor(), inspScroll }, out _, out _selectTopLeft);
        _selectTopLeft(0);
        Grid.SetColumn(topLeft, 0);
        top.Children.Add(topLeft);
        top.Children.Add(Splitter(1));

        // program monitor
        var monitor = new Grid();
        monitor.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        monitor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        monitor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var screen = new Border { Background = Brushes.Black, ClipToBounds = true };
        _picture = new System.Windows.Controls.Image { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(_picture, BitmapScalingMode.HighQuality);
        BuildMonitorOverlay(screen, _picture);
        Grid.SetRow(screen, 0);
        monitor.Children.Add(screen);

        // under the picture, like Premiere's Program monitor: timecode, Fit, ..., Full, wrench, duration; a scrub bar; then the transport row
        var times = new Grid { Margin = new Thickness(10, 7, 10, 0) };
        for (int i = 0; i < 6; i++) times.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        _timeLabel = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = TimecodeBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 14, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_timeLabel, "VidTime");
        _canvasLabel = new TextBlock();
        var fit = Dropdown("Fit", out _zoomText, "VidZoom",
            ("Fit", () => SetMonitorZoom(0)), ("10 %", () => SetMonitorZoom(10)), ("25 %", () => SetMonitorZoom(25)), ("50 %", () => SetMonitorZoom(50)),
            ("75 %", () => SetMonitorZoom(75)), ("100 %", () => SetMonitorZoom(100)), ("150 %", () => SetMonitorZoom(150)), ("200 %", () => SetMonitorZoom(200)));
        var full = Dropdown("Full", out _qualityText, "VidQuality",
            ("Full", () => SetPreviewQuality(1, "Full")), ("1/2", () => SetPreviewQuality(0.5, "1/2")), ("1/4", () => SetPreviewQuality(0.25, "1/4")));
        var wrench = FlatButton("", b => ShowMenu(b,
            (_safeMargins ? "Hide safe margins" : "Show safe margins", ToggleSafeMargins, null), ("-", null, null), ("Settings…", OpenSettings, null)), "VidMonitorWrench");
        ((TextBlock)wrench.Content).FontFamily = new FontFamily("Segoe MDL2 Assets");
        _durationLabel = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 15, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        _durationLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_durationLabel, "VidDuration");
        Grid.SetColumn(fit, 1); Grid.SetColumn(full, 3); Grid.SetColumn(wrench, 4); Grid.SetColumn(_durationLabel, 5);
        times.Children.Add(_timeLabel); times.Children.Add(fit); times.Children.Add(full); times.Children.Add(wrench); times.Children.Add(_durationLabel);
        Grid.SetRow(times, 1);
        monitor.Children.Add(times);

        var lower = new StackPanel();
        _programScrub = new VidScrub { Margin = new Thickness(10, 2, 10, 0) };
        _programScrub.Seek += t => SeekTo(t);
        lower.Children.Add(_programScrub);
        var transport = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 8) };
        transport.Children.Add(MakeButton("", "", "Add a marker (M)", AddMarkerHere, id: "VidAddMarker"));
        transport.Children.Add(MakeButton("", "{", "Mark In (I)", MarkIn, id: "VidMarkIn"));
        transport.Children.Add(MakeButton("", "}", "Mark Out (O)", MarkOut, id: "VidMarkOut"));
        transport.Children.Add(new Border { Width = 1, Margin = new Thickness(4, 6, 10, 6), Background = (Brush)FindResource("LineBrush") });
        transport.Children.Add(MakeButton("", "", "Go to In (Shift+I)", () => SeekTo(_s.Project.WorkIn ?? 0), id: "VidToStart"));
        transport.Children.Add(MakeButton("", "", "Back one frame (Left)", () => SeekTo(_s.Playhead - 1.0 / Fps), id: "VidBack"));
        var play = MakeButton("", "", "Play / pause (Space)", () => { _timeline.Focus(); _sourceActive = false; if (_srcPlayer.IsPlaying) _srcPlayer.Pause(); _player.Toggle(); UpdatePlayGlyph(); }, id: "VidPlay");
        _playGlyph = (TextBlock)((StackPanel)play.Content).Children[0];
        transport.Children.Add(play);
        transport.Children.Add(MakeButton("", "", "Forward one frame (Right)", () => SeekTo(_s.Playhead + 1.0 / Fps), id: "VidForward"));
        transport.Children.Add(MakeButton("", "", "Go to Out (Shift+O)", () => SeekTo(_s.Project.WorkOut ?? _s.Project.Duration), id: "VidToEnd"));
        transport.Children.Add(new Border { Width = 1, Margin = new Thickness(10, 6, 10, 6), Background = (Brush)FindResource("LineBrush") });
        transport.Children.Add(MakeButton("", "", "Lift: remove what is between In and Out and leave a gap (;)", () => DoLift(), id: "VidLift"));
        transport.Children.Add(MakeButton("", "", "Extract: remove what is between In and Out and close the gap (')", () => DoExtract(), id: "VidExtract"));
        transport.Children.Add(MakeButton("", "", "Export this frame as a picture (Ctrl+Shift+E)", ExportFrame, id: "VidFrame"));
        lower.Children.Add(transport);
        Grid.SetRow(lower, 2);
        monitor.Children.Add(lower);
        monitor.PreviewMouseDown += (_, _) => { _sourceActive = false; };
        var programPanel = Panel("Program: Sequence", monitor, out _programTitle);
        Grid.SetColumn(programPanel, 2);
        top.Children.Add(programPanel);

        // ----- splitter between the rows -----
        var rowSplit = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetRow(rowSplit, 3);
        root.Children.Add(rowSplit);

        // ----- bottom: Project / Effects | tools | Timeline -----
        var bottom = new Grid { Margin = new Thickness(8, 0, 8, 8) };
        _bottomLeftColumn = new ColumnDefinition { Width = new GridLength(470), MinWidth = 240 };
        bottom.ColumnDefinitions.Add(_bottomLeftColumn);
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 400 });
        Grid.SetRow(bottom, 4);
        root.Children.Add(bottom);

        var binGrid = BuildProjectPanelBody();
        var bottomLeft = TabbedPanel(new[] { "Project", "Effects", "Info", "History", "Markers" },
            new UIElement[] { binGrid, BuildEffectsPanel(), BuildInfoPanel(), BuildHistoryPanel(), BuildMarkersPanel() }, out var bl, out _selectBottomLeft);
        _projectTitle = bl[0];
        Grid.SetColumn(bottomLeft, 0);
        bottom.Children.Add(bottomLeft);
        bottom.Children.Add(Splitter(1));

        // tools column
        var tools = new StackPanel { Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        tools.Children.Add(ToolButton(VidTool.Select, SelectionArrowIcon(), "Selection tool (V)", "VidToolSelect"));
        tools.Children.Add(ToolButton(VidTool.TrackForward, Glyph("⇉", 17, "Segoe UI Symbol"), "Track select forward (A): choose a clip and everything after it", "VidToolForward"));
        tools.Children.Add(ToolButton(VidTool.Ripple, Glyph("⇥", 17, "Segoe UI Symbol"), "Ripple edit (B): trim an edge and close the gap", "VidToolRipple"));
        tools.Children.Add(ToolButton(VidTool.Rolling, Glyph("⇆", 17, "Segoe UI Symbol"), "Rolling edit (N): move the cut between two clips", "VidToolRolling"));
        tools.Children.Add(ToolButton(VidTool.Razor, Glyph("✂", 17, "Segoe UI Symbol"), "Razor tool (C): click a clip to cut it", "VidToolRazor"));
        tools.Children.Add(ToolButton(VidTool.Slip, Glyph("↔", 17, "Segoe UI Symbol"), "Slip tool (Y): show another part of a clip in the same place", "VidToolSlip"));
        tools.Children.Add(ToolButton(VidTool.Hand, Glyph("✋", 16, "Segoe UI Symbol"), "Hand tool (H): drag the timeline", "VidToolHand"));
        var titleTool = new Button { Content = Glyph("T", 17, "Segoe UI"), ToolTip = "Add a title at the playhead (T)", Style = (Style)XamlReader.Parse(FlatButtonStyle), Width = 36, Height = 34, Margin = new Thickness(0, 0, 0, 3) };
        ((TextBlock)titleTool.Content).FontWeight = FontWeights.Bold;
        System.Windows.Automation.AutomationProperties.SetAutomationId(titleTool, "VidAddTitle");
        titleTool.Click += (_, _) => AddTitle();
        tools.Children.Add(titleTool);
        var toolsBorder = new Border { Child = tools, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 0) };
        toolsBorder.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        toolsBorder.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        Grid.SetColumn(toolsBorder, 2);
        bottom.Children.Add(toolsBorder);

        // timeline panel
        var tlGrid = new Grid();
        tlGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        tlGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        tlGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var tlHead = new DockPanel { Margin = new Thickness(10, 6, 10, 4) };
        _tlTimeLabel = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = TimecodeBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 18, 0) };
        tlHead.Children.Add(_tlTimeLabel);
        var iconBar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _snapToggle = new ToggleButton { Content = Glyph("", 15), ToolTip = "Snap to edges, markers and the playhead", IsChecked = true, Style = (Style)XamlReader.Parse(ToolToggleStyle), Width = 30, Height = 30, Margin = new Thickness(0, 0, 4, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_snapToggle, "VidSnap");
        _snapToggle.Click += (_, _) => { _timeline.SnapEnabled = _snapToggle.IsChecked == true; };
        iconBar.Children.Add(_snapToggle);
        iconBar.Children.Add(MakeButton("", "", "Split: cut at the playhead (S)", () => DoSplit(), id: "VidSplit"));
        iconBar.Children.Add(MakeButton("", "", "Delete the chosen clips (Delete)", () => _s.Delete(false), id: "VidDelete"));
        iconBar.Children.Add(MakeButton("", "", "Ripple delete: delete and pull what follows left (Shift+Delete)", () => _s.Delete(true), id: "VidRipple"));
        iconBar.Children.Add(MakeButton("", "", "Duplicate: copy after itself (Ctrl+D)", () => _s.Duplicate(), id: "VidDuplicate"));
        iconBar.Children.Add(MakeButton("", "V", "Add a picture track", () => _s.AddTrack(TrackKind.Video), id: "VidAddVideoTrack"));
        iconBar.Children.Add(MakeButton("", "A", "Add a sound track", () => _s.AddTrack(TrackKind.Audio), id: "VidAddAudioTrack"));
        var zoom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        zoom.Children.Add(MakeButton("", "", "Zoom out (-)", () => ZoomBy(0.8)));
        zoom.Children.Add(MakeButton("", "", "Zoom in (+)", () => ZoomBy(1.25)));
        var fitAll = MakeButton("", "", "Fit: show the whole sequence (\\)", () => _timeline.ZoomToFit(), id: "VidFit");
        fitAll.Margin = new Thickness(0);
        zoom.Children.Add(fitAll);
        DockPanel.SetDock(zoom, Dock.Right);
        tlHead.Children.Add(zoom);
        tlHead.Children.Add(iconBar);
        Grid.SetRow(tlHead, 0);
        tlGrid.Children.Add(tlHead);

        _timeline = new VidTimeline(_s, _cache);
        var tlScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _timeline };
        _meters = new VidMeters { Width = 58, Margin = new Thickness(6, 4, 6, 4) };
        var tlBody = new DockPanel();
        DockPanel.SetDock(_meters, Dock.Right);
        tlBody.Children.Add(_meters);
        tlBody.Children.Add(tlScroll);
        Grid.SetRow(tlBody, 1);
        tlGrid.Children.Add(tlBody);
        _hScroll = new ScrollBar { Orientation = Orientation.Horizontal, Margin = new Thickness(VidTimeline.HeaderWidth + 10, 2, 46, 6) };
        _hScroll.ValueChanged += (_, e) => { if (!_syncingScroll) _timeline.Offset = e.NewValue; };
        Grid.SetRow(_hScroll, 2);
        tlGrid.Children.Add(_hScroll);
        var timelinePanel = Panel("Timeline: Sequence", tlGrid, out _timelineTitle);
        Grid.SetColumn(timelinePanel, 3);
        timelinePanel.PreviewMouseDown += (_, _) => { _sourceActive = false; };
        bottom.Children.Add(timelinePanel);
        SetWorkspace("Editing");
    }

    private GridSplitter Splitter(int column)
    {
        var s = new GridSplitter
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.Transparent,
            ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext, Cursor = Cursors.SizeWE,
        };
        Grid.SetColumn(s, column);
        return s;
    }

    /// <summary>Premiere-style workspaces: the same panels, arranged for the job (which tabs are in front, how wide the panels are).</summary>
    private void SetWorkspace(string name)
    {
        _workspace = name;
        foreach (var kv in _workspaceTabs)
        {
            bool on = kv.Key == name;
            kv.Value.Tab.SetResourceReference(Border.BorderBrushProperty, on ? "AccentBrush" : "BgBrush");
            if (on) kv.Value.Tab.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); else kv.Value.Tab.BorderBrush = Brushes.Transparent;
            kv.Value.Label.Opacity = on ? 1 : (kv.Key is "Learning" or "Captions" or "Libraries" ? 0.45 : 0.85);
            if (on) kv.Value.Label.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); else kv.Value.Label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        }
        switch (name)
        {
            case "Assembly":
                _selectTopLeft(0); _selectBottomLeft(0);
                _topLeftColumn.Width = new GridLength(1, GridUnitType.Star);
                _bottomLeftColumn.Width = new GridLength(760);
                break;
            case "Color":
                _selectTopLeft(1); _selectBottomLeft(1);
                _topLeftColumn.Width = new GridLength(0.8, GridUnitType.Star);
                _bottomLeftColumn.Width = new GridLength(330);
                break;
            case "Effects":
                _selectTopLeft(1); _selectBottomLeft(1);
                _topLeftColumn.Width = new GridLength(0.8, GridUnitType.Star);
                _bottomLeftColumn.Width = new GridLength(560);
                break;
            case "Audio":
                _selectTopLeft(1); _selectBottomLeft(0);
                _topLeftColumn.Width = new GridLength(0.8, GridUnitType.Star);
                _bottomLeftColumn.Width = new GridLength(330);
                break;
            case "Graphics":
                _selectTopLeft(1); _selectBottomLeft(0);
                _topLeftColumn.Width = new GridLength(0.9, GridUnitType.Star);
                _bottomLeftColumn.Width = new GridLength(330);
                break;
            default:                                                  // Editing
                _selectTopLeft(0); _selectBottomLeft(0);
                _topLeftColumn.Width = new GridLength(1, GridUnitType.Star);
                _bottomLeftColumn.Width = new GridLength(470);
                break;
        }
    }

    private double Fps => _s.Project.Fps > 0 ? _s.Project.Fps : 30;

    /// <summary>Jumps the playhead to the previous (-1) or next (+1) edge of any clip.</summary>
    private void GoToEdit(int direction)
    {
        var edges = _s.Project.AllClips.SelectMany(c => new[] { c.Start, c.End }).Distinct().OrderBy(x => x).ToList();
        double t = _s.Playhead;
        double target = direction < 0
            ? edges.Where(e => e < t - 0.001).DefaultIfEmpty(0).Max()
            : edges.Where(e => e > t + 0.001).DefaultIfEmpty(_s.Project.Duration).Min();
        SeekTo(target);
        _timeline.EnsureVisible(target);
    }

    private void SetSelectedSpeed(double speed)
    {
        var c = _s.SelectedClips.FirstOrDefault(x => x.Kind == ClipKind.Media);
        if (c == null) { SetStatus("Choose a clip first."); return; }
        if (!_s.SetSpeed(c, speed)) SetStatus("There is another clip in the way: make room first.");
    }

    private void NewProject()
    {
        if (!ConfirmDiscard()) return;
        _player.Pause();
        _s.Load(VidProject.NewEmpty());
        _binSignature = -1;
        OnProjectChanged();
        UpdatePlayGlyph();
        _timeline.ZoomToFit();
    }

    private void ShowShortcuts()
    {
        UMessage.Show(this,
            "Space  play / pause (the monitor you last used)\nV selection   A track select forward   B ripple   N rolling   C razor   Y slip   H hand\n" +
            "S split at the playhead     T add a title\nDelete  delete     Shift+Delete  delete and close the gap\n" +
            "I / O  mark In / Out     ;  lift     '  extract     M  add a marker\n" +
            ", / .  insert / overwrite from the Source monitor\n" +
            "Left / Right  one frame (Shift: one second)     Up / Down  previous / next edit     Home / End  start / end\n" +
            "Ctrl+Z / Ctrl+Y  undo / redo     Ctrl+C / X / V  copy / cut / paste     Ctrl+D  duplicate     Ctrl+A  select all\n" +
            "Ctrl+S save   Ctrl+O open   Ctrl+N new   Ctrl+I import   Ctrl+E export   Ctrl+Shift+E export a frame\n" +
            "+ / -  zoom     \\  fit     Ctrl + mouse wheel  zoom the timeline",
            "Utylix Vid Editor - keyboard shortcuts");
    }
}

/// <summary>Two level bars (left and right) with a dB scale, like the audio meters at the edge of Premiere's timeline.</summary>
public sealed class VidMeters : FrameworkElement
{
    private double _l, _r, _holdL, _holdR;

    public void SetLevels(double l, double r)
    {
        _l = l; _r = r;
        _holdL = Math.Max(_holdL * 0.97, l); _holdR = Math.Max(_holdR * 0.97, r);
        InvalidateVisual();
    }

    public void Reset() { _l = _r = _holdL = _holdR = 0; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 8 || h < 20) return;
        var track = (Brush)Application.Current.FindResource("TrackBrush");
        var muted = (Brush)Application.Current.FindResource("MutedBrush");
        double scaleW = 24;                                         // room for the dB numbers on the left, like Premiere's
        double bar = Math.Floor((w - scaleW - 6) / 2);
        double top = 4, usable = h - 22;
        double Frac(double v) => v <= 0.0001 ? 0 : Math.Clamp((20 * Math.Log10(v) + 48) / 48, 0, 1);
        for (int db = 0; db >= -48; db -= 6)
        {
            double y = top + usable - (db + 48) / 48.0 * usable;
            var t = new FormattedText(db.ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 9, muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(t, new Point(Math.Max(0, scaleW - 4 - t.Width), Math.Min(h - 24, Math.Max(0, y - t.Height / 2))));
        }
        for (int i = 0; i < 2; i++)
        {
            double x = scaleW + i * (bar + 4);
            dc.DrawRectangle(track, null, new Rect(x, top, bar, usable));
            double level = i == 0 ? _l : _r;
            double hold = i == 0 ? _holdL : _holdR;
            double fh = Frac(level) * usable;
            var lg = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            lg.GradientStops.Add(new GradientStop(Color.FromRgb(0xEF, 0x44, 0x44), 0));
            lg.GradientStops.Add(new GradientStop(Color.FromRgb(0xFA, 0xCC, 0x15), 0.2));
            lg.GradientStops.Add(new GradientStop(Color.FromRgb(0x22, 0xC5, 0x5E), 0.5));
            dc.PushClip(new RectangleGeometry(new Rect(x, top + usable - fh, bar, fh)));
            dc.DrawRectangle(lg, null, new Rect(x, top, bar, usable));
            dc.Pop();
            double hy = top + usable - Frac(hold) * usable;
            if (hold > 0.001) dc.DrawRectangle(muted, null, new Rect(x, hy, bar, 1.5));
        }
        var ft = new FormattedText("L  R", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(scaleW + (w - scaleW - ft.Width) / 2, h - 15));
    }
}
