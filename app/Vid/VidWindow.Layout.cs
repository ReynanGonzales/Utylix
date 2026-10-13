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
        var head = new DockPanel { LastChildFill = true };
        titleBlock = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var tab = new Border { Child = titleBlock, BorderThickness = new Thickness(0, 0, 0, 2) };
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
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 240 });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.05, GridUnitType.Star), MinHeight = 210 });
        Content = root;

        // ----- menu bar -----
        var bar = new DockPanel { Margin = new Thickness(10, 0, 10, 0) };
        var logo = new System.Windows.Controls.Image { Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        try { logo.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/vid.png")); } catch (Exception e) when (e is System.IO.IOException or NotSupportedException or UriFormatException) { }
        bar.Children.Add(logo);
        var menus = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        menus.Children.Add(FlatButton("File", b => ShowMenu(b,
            ("New project", NewProject, "Ctrl+N"), ("Open project…", OpenProject, "Ctrl+O"), ("-", null, null),
            ("Save", () => SaveProject(false), "Ctrl+S"), ("Save as…", () => SaveProject(true), "Ctrl+Shift+S"), ("-", null, null),
            ("Import media…", ImportDialog, "Ctrl+I"), ("Export media…", Export, "Ctrl+E"), ("Export frame…", ExportFrame, "Ctrl+Shift+E"), ("-", null, null),
            ("Exit", Close, null)), "VidMenuFile"));
        menus.Children.Add(FlatButton("Edit", b => ShowMenu(b,
            ("Undo", () => _s.Undo(), "Ctrl+Z"), ("Redo", () => _s.Redo(), "Ctrl+Y"), ("-", null, null),
            ("Cut", () => _s.Cut(), "Ctrl+X"), ("Copy", () => _s.Copy(), "Ctrl+C"), ("Paste", () => PasteClips(), "Ctrl+V"),
            ("Duplicate", () => _s.Duplicate(), "Ctrl+D"), ("Delete", () => _s.Delete(false), "Del"), ("Ripple delete", () => _s.Delete(true), "Shift+Del"), ("-", null, null),
            ("Select all", () => _s.Select(_s.Project.AllClips), "Ctrl+A"), ("-", null, null),
            ("Settings…", OpenSettings, null)), "VidMenuEdit"));
        menus.Children.Add(FlatButton("Clip", b => ShowMenu(b,
            ("Split at the playhead", DoSplit, "S"), ("Add a title", AddTitle, "T"), ("-", null, null),
            ("Lift (remove In to Out, leave a gap)", () => DoLift(), ";"), ("Extract (remove In to Out, close the gap)", () => DoExtract(), "'"), ("-", null, null),
            ("Remove the background (AI)…", RemoveBackground, null), ("-", null, null),
            ("Add the default transition at the playhead", AddTransitionAtPlayhead, "Ctrl+Shift+D"),
            ("Add the cross dissolve", () => ApplyTransition(TransitionKind.Dissolve), null), ("Remove the transition", () => ApplyTransition(TransitionKind.None), null), ("-", null, null),
            ("Speed 0.5×", () => SetSelectedSpeed(0.5), null), ("Speed 1× (normal)", () => SetSelectedSpeed(1), null), ("Speed 2×", () => SetSelectedSpeed(2), null)), "VidMenuClip"));
        menus.Children.Add(FlatButton("Sequence", b => ShowMenu(b,
            ("Mark In", MarkIn, "I"), ("Mark Out", MarkOut, "O"), ("Clear In and Out", () => _s.ClearWorkArea(), "Ctrl+Shift+X"), ("-", null, null),
            ("Add a video track", () => _s.AddTrack(TrackKind.Video), null), ("Add a sound track", () => _s.AddTrack(TrackKind.Audio), null), ("-", null, null),
            ("Previous edit point", () => GoToEdit(-1), "Up"), ("Next edit point", () => GoToEdit(1), "Down"), ("-", null, null),
            ("Zoom in", () => ZoomBy(1.25), "+"), ("Zoom out", () => ZoomBy(0.8), "-"), ("Fit the whole sequence", () => _timeline.ZoomToFit(), "\\")), "VidMenuSequence"));
        menus.Children.Add(FlatButton("Markers", b => ShowMenu(b,
            ("Add a marker", AddMarkerHere, "M"), ("Previous marker", () => GoToMarker(-1), null), ("Next marker", () => GoToMarker(1), null), ("-", null, null),
            ("Clear all markers", () => _s.ClearMarkers(), null)), "VidMenuMarkers"));
        menus.Children.Add(FlatButton("Help", b => ShowMenu(b, ("Keyboard shortcuts…", ShowShortcuts, null)), "VidMenuHelp"));
        bar.Children.Add(menus);

        var rightBar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(rightBar, Dock.Right);
        _undoBtn = MakeButton("", "", "Undo (Ctrl+Z)", () => _s.Undo(), id: "VidUndo");
        _redoBtn = MakeButton("", "", "Redo (Ctrl+Y)", () => _s.Redo(), id: "VidRedo");
        _undoBtn.Margin = new Thickness(0, 0, 4, 0); _redoBtn.Margin = new Thickness(0, 0, 10, 0);
        var export = MakeButton("", "Export", "Make the finished video (Ctrl+E)", Export, primary: true, id: "VidExport");
        export.Margin = new Thickness(0);
        rightBar.Children.Add(_undoBtn); rightBar.Children.Add(_redoBtn); rightBar.Children.Add(export);
        bar.Children.Add(rightBar);
        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 14, 0), TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        bar.Children.Add(_status);
        var barBorder = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
        barBorder.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        barBorder.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        Grid.SetRow(barBorder, 0);
        root.Children.Add(barBorder);

        // ----- top: Source / Effect Controls | Program monitor -----
        var top = new Grid { Margin = new Thickness(8, 8, 8, 0) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400), MinWidth = 300 });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 360 });
        Grid.SetRow(top, 1);
        root.Children.Add(top);

        var inspScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _inspector = new StackPanel { Margin = new Thickness(12, 8, 12, 12) };
        inspScroll.Content = _inspector;
        var topLeft = TabbedPanel(new[] { "Source", "Effect Controls" }, new UIElement[] { BuildSourceMonitor(), inspScroll }, out _, out _selectTopLeft);
        _selectTopLeft(1);
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
        screen.Child = _picture;
        Grid.SetRow(screen, 0);
        monitor.Children.Add(screen);

        var times = new Grid { Margin = new Thickness(12, 8, 12, 0) };
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        times.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _timeLabel = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = TimecodeBrush, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_timeLabel, "VidTime");
        _canvasLabel = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _canvasLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _durationLabel = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 15, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _durationLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_durationLabel, "VidDuration");
        Grid.SetColumn(_canvasLabel, 1); Grid.SetColumn(_durationLabel, 2);
        times.Children.Add(_timeLabel); times.Children.Add(_canvasLabel); times.Children.Add(_durationLabel);
        Grid.SetRow(times, 1);
        monitor.Children.Add(times);

        var controls = new Grid { Margin = new Thickness(10, 6, 10, 8) };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var marks = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        marks.Children.Add(MakeButton("", "In", "Mark In (I)", MarkIn, id: "VidMarkIn"));
        marks.Children.Add(MakeButton("", "Out", "Mark Out (O)", MarkOut, id: "VidMarkOut"));
        marks.Children.Add(MakeButton("", "", "Add a marker (M)", AddMarkerHere, id: "VidAddMarker"));
        controls.Children.Add(marks);
        var transport = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        transport.Children.Add(MakeButton("", "", "Previous edit point (Up)", () => GoToEdit(-1), id: "VidToStart"));
        transport.Children.Add(MakeButton("", "", "Back one frame (Left)", () => SeekTo(_s.Playhead - 1.0 / Fps), id: "VidBack"));
        var play = MakeButton("", "", "Play / pause (Space)", () => { _timeline.Focus(); _sourceActive = false; if (_srcPlayer.IsPlaying) _srcPlayer.Pause(); _player.Toggle(); UpdatePlayGlyph(); }, id: "VidPlay");
        _playGlyph = (TextBlock)((StackPanel)play.Content).Children[0];
        transport.Children.Add(play);
        transport.Children.Add(MakeButton("", "", "Forward one frame (Right)", () => SeekTo(_s.Playhead + 1.0 / Fps), id: "VidForward"));
        transport.Children.Add(MakeButton("", "", "Next edit point (Down)", () => GoToEdit(1), id: "VidToEnd"));
        Grid.SetColumn(transport, 1);
        controls.Children.Add(transport);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(MakeButton("", "", "Lift: remove what is between In and Out and leave a gap (;)", () => DoLift(), id: "VidLift"));
        right.Children.Add(MakeButton("", "", "Extract: remove what is between In and Out and close the gap (')", () => DoExtract(), id: "VidExtract"));
        right.Children.Add(MakeButton("", "", "Export this frame as a picture (Ctrl+Shift+E)", ExportFrame, id: "VidFrame"));
        var quality = MakeButton("", "Full", "Preview quality: Full, 1/2, 1/4 (a lower one plays smoother on a slow PC)", CyclePreviewQuality, id: "VidQuality");
        _qualityText = (TextBlock)((StackPanel)quality.Content).Children[1];
        quality.Margin = new Thickness(0);
        right.Children.Add(quality);
        Grid.SetColumn(right, 2);
        controls.Children.Add(right);
        Grid.SetRow(controls, 2);
        monitor.Children.Add(controls);
        monitor.PreviewMouseDown += (_, _) => { _sourceActive = false; };
        var programPanel = Panel("Program: Sequence", monitor, out _programTitle);
        Grid.SetColumn(programPanel, 2);
        top.Children.Add(programPanel);

        // ----- splitter between the rows -----
        var rowSplit = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetRow(rowSplit, 2);
        root.Children.Add(rowSplit);

        // ----- bottom: Project / Effects | tools | Timeline -----
        var bottom = new Grid { Margin = new Thickness(8, 0, 8, 8) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340), MinWidth = 240 });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 400 });
        Grid.SetRow(bottom, 3);
        root.Children.Add(bottom);

        var binScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _bin = new StackPanel { Margin = new Thickness(8, 8, 8, 8) };
        binScroll.Content = _bin;
        var binGrid = new Grid();
        binGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        binGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(binScroll, 0);
        binGrid.Children.Add(binScroll);
        var binTools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 6) };
        binTools.Children.Add(MakeButton("", "Import", "Bring in videos, sounds and pictures (Ctrl+I)", () => ImportDialog(), id: "VidImport"));
        Grid.SetRow(binTools, 1);
        binGrid.Children.Add(binTools);
        var bottomLeft = TabbedPanel(new[] { "Project", "Effects" }, new UIElement[] { binGrid, BuildEffectsPanel() }, out var bl, out _selectBottomLeft);
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
        var fit = MakeButton("", "", "Fit: show the whole sequence (\\)", () => _timeline.ZoomToFit(), id: "VidFit");
        fit.Margin = new Thickness(0);
        zoom.Children.Add(fit);
        DockPanel.SetDock(zoom, Dock.Right);
        tlHead.Children.Add(zoom);
        tlHead.Children.Add(iconBar);
        Grid.SetRow(tlHead, 0);
        tlGrid.Children.Add(tlHead);

        _timeline = new VidTimeline(_s, _cache);
        var tlScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _timeline };
        _meters = new VidMeters { Width = 34, Margin = new Thickness(6, 4, 6, 4) };
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
        double bar = Math.Floor((w - 6) / 2);
        double top = 4, usable = h - 22;
        for (int i = 0; i < 2; i++)
        {
            double x = 1 + i * (bar + 4);
            dc.DrawRectangle(track, null, new Rect(x, top, bar, usable));
            double level = i == 0 ? _l : _r;
            double hold = i == 0 ? _holdL : _holdR;
            // level 0..1 is the sample peak; the scale is in dB from -48 to 0
            double Frac(double v) => v <= 0.0001 ? 0 : Math.Clamp((20 * Math.Log10(v) + 48) / 48, 0, 1);
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
        dc.DrawText(ft, new Point((w - ft.Width) / 2, h - 15));
    }
}
