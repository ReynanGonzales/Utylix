using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IdmClone.Engine;
using Microsoft.Win32;

namespace IdmClone.Vid;

/// <summary>Utylix Vid Editor: media bin, preview, inspector and the timeline. The project lives in <see cref="VidSession"/>.</summary>
public sealed partial class VidWindow : Window
{
    private readonly VidSession _s = new();
    private readonly VidMediaCache _cache = new();
    private VidPlayer _player = null!;
    private VidTimeline _timeline = null!;
    private Image _picture = null!;
    private TextBlock _timeLabel = null!, _canvasLabel = null!, _status = null!, _playGlyph = null!;
    private StackPanel _bin = null!, _inspector = null!;
    private Button _undoBtn = null!, _redoBtn = null!;
    private ScrollBar _hScroll = null!;
    private readonly DispatcherTimer _refresh, _autosave, _binTimer;
    private bool _syncingScroll;
    private string? _binSelected;
    private int _binSignature;
    private static string AutosavePath => Path.Combine(App.DataDir, "autosave.utvid");
    private static readonly string[] MediaFilter =
    {
        "Video, sound and pictures|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.wmv;*.m4v;*.flv;*.ts;*.mts;*.m2ts;*.3gp;*.mpg;*.mpeg;*.gif;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.wma;*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.tif;*.tiff",
        "All files|*.*",
    };

    public VidWindow(string? file)
    {
        Title = "Utylix Vid Editor";
        Width = 1340; Height = 840; MinWidth = 1000; MinHeight = 660;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        WindowTheme.DarkTitleBar(this);
        WindowTheme.OwnTaskbarButton(this, "Utylix.VidEditor");
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/vid.ico")); }
        catch (Exception e) when (e is IOException or NotSupportedException or UriFormatException) { /* the toolbox's icon is used */ }
        Resources.Add(typeof(Slider), (Style)XamlReader.Parse(SliderStyle));

        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _refresh.Tick += (_, _) => { _refresh.Stop(); _player.Refresh(); };
        _autosave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _autosave.Tick += (_, _) => AutoSave();
        _binTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _binTimer.Tick += (_, _) => { _binTimer.Stop(); RebuildBin(); };

        BuildUi();

        _player = new VidPlayer(_s);
        _picture.Source = _player.Bitmap;
        _player.BitmapChanged += () => _picture.Source = _player.Bitmap;
        _player.Position += t => { _timeline.EnsureVisible(t); _timeline.InvalidateVisual(); UpdateTime(); };
        _player.Stopped += () => { UpdatePlayGlyph(); UpdateTime(); };
        _player.Problem += msg => SetStatus(msg);

        _s.Changed += OnProjectChanged;
        _s.SelectionChanged += RefreshInspector;
        _cache.Updated += () => { _binTimer.Stop(); _binTimer.Start(); };
        _timeline.ViewChanged += SyncScroll;
        _timeline.SizeChanged += (_, _) => SyncScroll();
        _timeline.PlayheadDragged += (t, final) => SeekTo(t);
        _timeline.ContextMenuRequested += ShowTimelineMenu;
        _timeline.MediaDropped += (id, t, track) =>
        {
            var m = _s.Project.Media.FirstOrDefault(x => x.Id == id);
            if (m != null) AddToTimeline(m, t, track >= 0 ? track : null);
        };
        _timeline.FilesDropped += (files, t) => _ = ImportAsync(files, addToTimeline: true, at: t);
        _timeline.ClipActivated += c => _timeline.Focus();

        AllowDrop = true;
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) _ = ImportAsync(files, addToTimeline: !_s.Project.AllClips.Any()); };
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _autosave.Stop(); _refresh.Stop();
            _player.Dispose(); _cache.Dispose();
            try { if (File.Exists(AutosavePath)) File.Delete(AutosavePath); } catch (IOException) { }
        };
        Loaded += async (_, _) => await StartAsync(file);
        ApplyAutosaveSetting();
        OnProjectChanged();
    }

    // ---------- start ----------
    private async Task StartAsync(string? file)
    {
        if (!Tools.HasFfmpeg || !VidProbe.Available)
        {
            var answer = UMessage.Ask(this,
                "Utylix Vid Editor needs ffmpeg (the video engine Utylix also uses for converting), which is not on this PC yet.\n\nGet it now? It is a one-time download of about 100 MB.",
                "Utylix Vid Editor - one more part", MessageBoxImage.Question, MessageBoxResult.Yes, MessageBoxResult.No, ("Get it", MessageBoxResult.Yes), ("Not now", MessageBoxResult.No));
            if (answer == MessageBoxResult.Yes)
            {
                SetStatus("Getting ffmpeg…");
                try { await Tools.InstallFfmpegAsync(s => Dispatcher.Invoke(() => SetStatus(s)), default); SetStatus("ffmpeg is ready."); }
                catch (Exception e) when (e is IOException or System.Net.Http.HttpRequestException or InvalidOperationException)
                { UMessage.Show(this, "ffmpeg could not be downloaded:\n\n" + e.Message, "Utylix Vid Editor", MessageBoxButton.OK, MessageBoxImage.Warning); }
            }
        }
        // a project the last session did not get to save
        try
        {
            if (File.Exists(AutosavePath) && file == null)
            {
                var ask = UMessage.Ask(this, "Utylix Vid Editor did not close normally last time. Open the project it kept for you?", "Utylix Vid Editor",
                    MessageBoxImage.Question, MessageBoxResult.Yes, MessageBoxResult.No, ("Open it", MessageBoxResult.Yes), ("Start fresh", MessageBoxResult.No));
                if (ask == MessageBoxResult.Yes) { LoadProject(AutosavePath, keepPath: false); _s.MarkDirty(); UpdateTitle(); }
            }
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { }
        if (file != null)
        {
            if (file.EndsWith(".utvid", StringComparison.OrdinalIgnoreCase)) LoadProject(file);
            else await ImportAsync(new[] { file }, addToTimeline: true);
        }
    }

    // ---------- building the window ----------
    private const string SliderStyle = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Slider'>
  <Setter Property='Height' Value='22'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='Slider'>
        <Grid Background='Transparent'>
          <Border Height='4' CornerRadius='2' Background='{DynamicResource TrackBrush}' VerticalAlignment='Center'/>
          <Track x:Name='PART_Track' VerticalAlignment='Center'>
            <Track.DecreaseRepeatButton>
              <RepeatButton Command='Slider.DecreaseLarge' Focusable='False'>
                <RepeatButton.Template><ControlTemplate TargetType='RepeatButton'>
                  <Border Height='22' Background='Transparent'><Border Height='4' CornerRadius='2' Background='{DynamicResource AccentBrush}' VerticalAlignment='Center'/></Border>
                </ControlTemplate></RepeatButton.Template>
              </RepeatButton>
            </Track.DecreaseRepeatButton>
            <Track.Thumb>
              <Thumb Width='14' Height='14' Focusable='False'>
                <Thumb.Template><ControlTemplate TargetType='Thumb'>
                  <Ellipse Width='14' Height='14' Fill='{DynamicResource TextBrush}' Stroke='{DynamicResource AccentBrush}' StrokeThickness='2'/>
                </ControlTemplate></Thumb.Template>
              </Thumb>
            </Track.Thumb>
            <Track.IncreaseRepeatButton>
              <RepeatButton Command='Slider.IncreaseLarge' Focusable='False'>
                <RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Height='22' Background='Transparent'/></ControlTemplate></RepeatButton.Template>
              </RepeatButton>
            </Track.IncreaseRepeatButton>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

    private Button MakeButton(string glyph, string label, string tip, Action click, bool primary = false, string? id = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        Brush fg = primary ? Brushes.White : (Brush)FindResource("TextBrush");
        if (glyph.Length > 0)
            content.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14, Foreground = fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, label.Length > 0 ? 7 : 0, 0) });
        if (label.Length > 0) content.Children.Add(new TextBlock { Text = label, Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = content, ToolTip = tip, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(label.Length > 0 ? 12 : 9, 6, label.Length > 0 ? 12 : 9, 6) };
        b.Style = (Style)FindResource(primary ? "DialogPrimary" : "DialogButton");
        b.Click += (_, _) => click();
        if (id != null) System.Windows.Automation.AutomationProperties.SetAutomationId(b, id);
        return b;
    }

    private Border Card(UIElement child, Thickness? margin = null)
    {
        var b = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(0), Child = child, Margin = margin ?? new Thickness(0), ClipToBounds = true };
        b.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        return b;
    }

    private void BuildUi()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 250 });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(7) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(326), MinHeight = 190 });
        Content = root;

        // ----- top bar -----
        var top = new DockPanel { Margin = new Thickness(12, 10, 12, 8), LastChildFill = false };
        var title = new TextBlock { Text = "Utylix Vid Editor", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 18, 0) };
        top.Children.Add(title);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(MakeButton("", "Import", "Bring in videos, sounds and pictures (Ctrl+I)", () => ImportDialog(), id: "VidImport"));
        left.Children.Add(MakeButton("", "Open", "Open a project (Ctrl+O)", OpenProject, id: "VidOpen"));
        left.Children.Add(MakeButton("", "Save", "Save the project (Ctrl+S)", () => SaveProject(false), id: "VidSave"));
        _undoBtn = MakeButton("", "", "Undo (Ctrl+Z)", () => _s.Undo(), id: "VidUndo");
        _redoBtn = MakeButton("", "", "Redo (Ctrl+Y)", () => _s.Redo(), id: "VidRedo");
        left.Children.Add(_undoBtn); left.Children.Add(_redoBtn);
        left.Children.Add(MakeButton("", "Settings", "The editor's settings: colours, export and recovery copies", OpenSettings, id: "VidSettings"));
        DockPanel.SetDock(left, Dock.Left);
        top.Children.Add(left);
        var export = MakeButton("", "Export", "Make the finished video (Ctrl+E)", Export, primary: true, id: "VidExport");
        export.Margin = new Thickness(0);
        DockPanel.SetDock(export, Dock.Right);
        top.Children.Add(export);
        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 14, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        DockPanel.SetDock(_status, Dock.Right);
        top.Children.Add(_status);
        Grid.SetRow(top, 0);
        root.Children.Add(top);

        // ----- body: bin | preview | inspector -----
        var body = new Grid { Margin = new Thickness(12, 0, 12, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(272) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 320 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(296) });
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        // media bin
        var binHead = new DockPanel { Margin = new Thickness(14, 10, 10, 6) };
        binHead.Children.Add(new TextBlock { Text = "Media", FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        var binScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _bin = new StackPanel { Margin = new Thickness(8, 0, 8, 8) };
        binScroll.Content = _bin;
        var binGrid = new Grid();
        binGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        binGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(binHead, 0); Grid.SetRow(binScroll, 1);
        binGrid.Children.Add(binHead); binGrid.Children.Add(binScroll);
        var binCard = Card(binGrid);
        Grid.SetColumn(binCard, 0);
        body.Children.Add(binCard);

        // preview + transport
        var previewGrid = new Grid();
        previewGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        previewGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var screen = new Border { Background = Brushes.Black, Margin = new Thickness(0), ClipToBounds = true };
        _picture = new Image { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(_picture, BitmapScalingMode.HighQuality);
        screen.Child = _picture;
        Grid.SetRow(screen, 0);
        previewGrid.Children.Add(screen);
        var transport = new DockPanel { Margin = new Thickness(12, 8, 12, 8) };
        var tl = new StackPanel { Orientation = Orientation.Horizontal };
        tl.Children.Add(MakeButton("", "", "To the start (Home)", () => SeekTo(0), id: "VidToStart"));
        var play = MakeButton("", "", "Play / pause (Space)", () => { _timeline.Focus(); _player.Toggle(); UpdatePlayGlyph(); }, id: "VidPlay");
        _playGlyph = (TextBlock)((StackPanel)play.Content).Children[0];
        tl.Children.Add(play);
        tl.Children.Add(MakeButton("", "", "To the end (End)", () => SeekTo(_s.Project.Duration), id: "VidToEnd"));
        _timeLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontFamily = new FontFamily("Consolas"), FontSize = 14 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_timeLabel, "VidTime");
        tl.Children.Add(_timeLabel);
        DockPanel.SetDock(tl, Dock.Left);
        transport.Children.Add(tl);
        _canvasLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        _canvasLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        transport.Children.Add(_canvasLabel);
        Grid.SetRow(transport, 1);
        previewGrid.Children.Add(transport);
        var previewCard = Card(previewGrid, new Thickness(8, 0, 8, 0));
        Grid.SetColumn(previewCard, 1);
        body.Children.Add(previewCard);

        // inspector
        var inspScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _inspector = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        inspScroll.Content = _inspector;
        var inspCard = Card(inspScroll);
        Grid.SetColumn(inspCard, 2);
        body.Children.Add(inspCard);

        // ----- splitter + timeline -----
        var splitter = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetRow(splitter, 2);
        root.Children.Add(splitter);

        var tlGrid = new Grid { Margin = new Thickness(12, 0, 12, 12) };
        tlGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        tlGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        tlGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        tools.Children.Add(MakeButton("", "Split", "Cut at the playhead (S)", () => DoSplit(), id: "VidSplit"));
        tools.Children.Add(MakeButton("", "Delete", "Delete the chosen clips (Delete)", () => _s.Delete(false), id: "VidDelete"));
        tools.Children.Add(MakeButton("", "Delete and close the gap", "Delete and pull what follows left (Shift+Delete)", () => _s.Delete(true), id: "VidRipple"));
        tools.Children.Add(MakeButton("", "Duplicate", "Copy after itself (Ctrl+D)", () => _s.Duplicate(), id: "VidDuplicate"));
        tools.Children.Add(MakeButton("", "Title", "Add a text title at the playhead (T)", AddTitle, id: "VidAddTitle"));
        tools.Children.Add(new Border { Width = 1, Margin = new Thickness(6, 4, 12, 4), Background = (Brush)FindResource("LineBrush") });
        tools.Children.Add(MakeButton("", "Video track", "Add a picture track", () => _s.AddTrack(TrackKind.Video), id: "VidAddVideoTrack"));
        tools.Children.Add(MakeButton("", "Sound track", "Add a sound track", () => _s.AddTrack(TrackKind.Audio), id: "VidAddAudioTrack"));
        var zoom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var toolRow = new DockPanel();
        toolRow.Children.Add(tools);
        zoom.Children.Add(MakeButton("", "", "Zoom out (-)", () => ZoomBy(0.8)));
        zoom.Children.Add(MakeButton("", "", "Zoom in (+)", () => ZoomBy(1.25)));
        zoom.Children.Add(MakeButton("", "Fit", "Show the whole project (\\)", () => _timeline.ZoomToFit(), id: "VidFit"));
        ((FrameworkElement)zoom.Children[zoom.Children.Count - 1]).Margin = new Thickness(0);
        toolRow.Children.Add(zoom);
        Grid.SetRow(toolRow, 0);
        tlGrid.Children.Add(toolRow);

        _timeline = new VidTimeline(_s, _cache);
        var tlScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _timeline };
        var tlCard = Card(tlScroll);
        Grid.SetRow(tlCard, 1);
        tlGrid.Children.Add(tlCard);
        _hScroll = new ScrollBar { Orientation = Orientation.Horizontal, Margin = new Thickness(VidTimeline.HeaderWidth, 4, 0, 0) };
        _hScroll.ValueChanged += (_, e) => { if (!_syncingScroll) _timeline.Offset = e.NewValue; };
        Grid.SetRow(_hScroll, 2);
        tlGrid.Children.Add(_hScroll);
        Grid.SetRow(tlGrid, 3);
        root.Children.Add(tlGrid);
    }

    // ---------- little things ----------
    private void SetStatus(string text) => _status.Text = text;

    private void UpdateTitle()
    {
        string name = _s.Project.Path != null ? Path.GetFileNameWithoutExtension(_s.Project.Path) : "Untitled";
        Title = (_s.Dirty ? "● " : "") + name + " - Utylix Vid Editor";
    }

    private void UpdateTime()
    {
        _timeLabel.Text = VidTimeline.TimeText(_s.Playhead, true) + "  /  " + VidTimeline.TimeText(_s.Project.Duration, true);
    }

    private void UpdatePlayGlyph() => _playGlyph.Text = _player.IsPlaying ? "" : "";

    private void SyncScroll()
    {
        if (_timeline == null || _hScroll == null) return;
        _syncingScroll = true;
        double vis = _timeline.VisibleSeconds;
        double max = Math.Max(_s.Project.Duration + vis * 0.5, vis);
        _hScroll.Minimum = 0;
        _hScroll.Maximum = Math.Max(0, max - vis);
        _hScroll.ViewportSize = vis;
        _hScroll.SmallChange = vis * 0.1;
        _hScroll.LargeChange = vis * 0.8;
        _hScroll.Value = Math.Min(_timeline.Offset, _hScroll.Maximum);
        _syncingScroll = false;
    }

    private void ZoomBy(double factor)
    {
        double anchor = _s.Playhead;
        double x = VidTimeline.HeaderWidth + (anchor - _timeline.Offset) * _timeline.PixelsPerSecond;
        if (x < VidTimeline.HeaderWidth || x > _timeline.ActualWidth) { anchor = _timeline.Offset + _timeline.VisibleSeconds / 2; x = VidTimeline.HeaderWidth + _timeline.VisibleSeconds / 2 * _timeline.PixelsPerSecond; }
        _timeline.ZoomAround(factor, anchor, x);
    }

    private void SeekTo(double t)
    {
        if (_player.IsPlaying) { _player.Pause(); UpdatePlayGlyph(); }
        _s.Playhead = Math.Clamp(t, 0, Math.Max(_s.Project.Duration, t));
        _timeline.InvalidateVisual();
        UpdateTime();
        _player.ShowFrame(_s.Playhead);
    }

    private void OnProjectChanged()
    {
        UpdateTitle();
        UpdateTime();
        _undoBtn.IsEnabled = _s.CanUndo;
        _redoBtn.IsEnabled = _s.CanRedo;
        _undoBtn.ToolTip = _s.CanUndo ? "Undo " + _s.NextUndoLabel + " (Ctrl+Z)" : "Nothing to undo";
        _canvasLabel.Text = $"{_s.Project.Width} × {_s.Project.Height}   ·   {_s.Project.Fps:0.##} fps";
        int sig = _s.Project.Media.Count;
        foreach (var m in _s.Project.Media) sig = sig * 31 + m.Id.GetHashCode();
        if (sig != _binSignature) { _binSignature = sig; RebuildBin(); }
        SyncScroll();
        RefreshInspector();
        _refresh.Stop(); _refresh.Start();
    }

    private void AutoSave()
    {
        if (!_s.Dirty) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AutosavePath)!);
            File.WriteAllText(AutosavePath, _s.Project.ToJson());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* try again next time */ }
    }

    // ---------- the media bin ----------
    private void RebuildBin()
    {
        _bin.Children.Clear();
        if (_s.Project.Media.Count == 0)
        {
            var hint = new TextBlock { Text = "Bring in videos, sounds and pictures with Import, or drop files here. Drag them onto the timeline, or double-click to add them.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 4, 6, 0) };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            _bin.Children.Add(hint);
            return;
        }
        foreach (var m in _s.Project.Media) _bin.Children.Add(BinRow(m));
    }

    private UIElement BinRow(MediaItem m)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var thumbBox = new Border { Width = 64, Height = 38, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        ImageSource? img = m.HasVideo ? _cache.Thumbnail(m, Math.Min(1.0, m.Duration / 2)) : null;
        if (img != null) thumbBox.Child = new Image { Source = img, Stretch = Stretch.UniformToFill };
        else thumbBox.Child = new TextBlock { Text = m.Kind == MediaKind.Audio ? "" : "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
        grid.Children.Add(thumbBox);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = m.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        string info = m.Kind == MediaKind.Image ? "picture" : VidTimeline.TimeText(m.Duration) ;
        if (m.HasVideo && m.Width > 0) info += $"  ·  {m.Width}×{m.Height}";
        if (!File.Exists(m.Path)) info = "file not found";
        var infoText = new TextBlock { Text = info, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        infoText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        texts.Children.Add(infoText);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var row = new Border { Child = grid, Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 2), CornerRadius = new CornerRadius(7), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = m.Path };
        bool selected = _binSelected == m.Id;
        if (selected) row.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        row.MouseEnter += (_, _) => { if (_binSelected != m.Id) row.SetResourceReference(Border.BackgroundProperty, "TrackBrush"); };
        row.MouseLeave += (_, _) => { if (_binSelected != m.Id) row.Background = Brushes.Transparent; };
        Point down = default; bool pressed = false;
        row.MouseLeftButtonDown += (_, e) =>
        {
            _binSelected = m.Id;
            if (e.ClickCount == 2) { AddToTimeline(m, EndOfTimeline(), null); e.Handled = true; return; }
            down = e.GetPosition(row); pressed = true;
            foreach (Border b in _bin.Children.OfType<Border>()) b.Background = Brushes.Transparent;
            row.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        };
        row.MouseMove += (_, e) =>
        {
            if (!pressed || e.LeftButton != MouseButtonState.Pressed) { pressed = false; return; }
            var p = e.GetPosition(row);
            if (Math.Abs(p.X - down.X) + Math.Abs(p.Y - down.Y) < 8) return;
            pressed = false;
            var data = new DataObject();
            data.SetData("UtylixVidMedia", m.Id);
            DragDrop.DoDragDrop(row, data, DragDropEffects.Copy);
        };
        row.MouseLeftButtonUp += (_, _) => pressed = false;
        var menu = new ContextMenu { Style = (Style)FindResource("ThemedMenu") };
        menu.Items.Add(Item("Add to the end of the timeline", () => AddToTimeline(m, EndOfTimeline(), null)));
        menu.Items.Add(Item("Add at the playhead", () => AddToTimeline(m, _s.Playhead, null)));
        menu.Items.Add(new Separator { Style = (Style)FindResource("ThemedSeparator") });
        menu.Items.Add(Item("Show in Explorer", () => { try { Process.Start("explorer.exe", "/select,\"" + m.Path + "\""); } catch (Win32Exception) { } }));
        menu.Items.Add(Item("Take out of the project", () => RemoveMedia(m)));
        row.ContextMenu = menu;
        return row;
    }

    private MenuItem Item(string header, Action click, string? gesture = null)
    {
        var mi = new MenuItem { Header = header, Style = (Style)FindResource("ThemedMenuItem") };
        if (gesture != null) mi.InputGestureText = gesture;
        mi.Click += (_, _) => click();
        return mi;
    }

    private double EndOfTimeline()
    {
        var t = _s.FirstTrack(TrackKind.Video);
        return t.Clips.Count == 0 ? 0 : t.Clips.Max(c => c.End);
    }

    private void RemoveMedia(MediaItem m)
    {
        if (_s.Project.AllClips.Any(c => c.MediaId == m.Id))
        {
            UMessage.Show(this, "This file is used on the timeline. Delete its clips first, then take it out of the project.", "Utylix Vid Editor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _s.Project.Media.Remove(m);
        _s.MarkDirty();
        _binSignature = -1;
        OnProjectChanged();
    }

    // ---------- importing ----------
    private void ImportDialog()
    {
        var dlg = new OpenFileDialog { Title = "Bring in videos, sounds and pictures", Multiselect = true, Filter = string.Join("|", MediaFilter) };
        if (dlg.ShowDialog(this) == true) _ = ImportAsync(dlg.FileNames, addToTimeline: !_s.Project.AllClips.Any());
    }

    public async Task ImportAsync(IEnumerable<string> paths, bool addToTimeline, double? at = null)
    {
        if (!VidProbe.Available) { UMessage.Show(this, "ffmpeg is not installed yet, so files can't be read. Close the editor and open it again to get it.", "Utylix Vid Editor"); return; }
        var errors = new List<string>();
        var added = new List<MediaItem>();
        foreach (string path in paths.Where(p => File.Exists(p)).ToList())
        {
            var existing = _s.Project.Media.FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { added.Add(existing); continue; }
            SetStatus("Reading " + Path.GetFileName(path) + "…");
            try
            {
                var item = await VidProbe.ReadAsync(path);
                _s.Project.Media.Add(item);
                added.Add(item);
                _s.MarkDirty();
            }
            catch (Exception e) when (e is IOException or Win32Exception or System.Text.Json.JsonException)
            {
                errors.Add(Path.GetFileName(path) + ": " + e.Message.Split('\n')[0]);
            }
        }
        SetStatus("");
        _binSignature = -1;
        OnProjectChanged();
        if (addToTimeline)
        {
            double t = at ?? EndOfTimeline();
            foreach (var m in added)
            {
                var clips = _s.AddToTimeline(m, t);
                t = clips.Count > 0 ? clips.Max(c => c.End) : t;
            }
            _timeline.ZoomToFit();
            SeekTo(0);
        }
        if (errors.Count > 0)
            UMessage.Show(this, "Some files could not be used:\n\n" + string.Join("\n", errors), "Utylix Vid Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void AddToTimeline(MediaItem m, double at, int? track)
    {
        if (!File.Exists(m.Path)) { UMessage.Show(this, "This file is not where it was:\n" + m.Path, "Utylix Vid Editor", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var clips = _s.AddToTimeline(m, at, track);
        _s.Select(clips);
        if (_s.Project.AllClips.Count() == clips.Count) _timeline.ZoomToFit();
    }

    // ---------- editing commands ----------
    private void AddTitle()
    {
        var c = _s.AddText(_s.Playhead);
        _s.Select(new[] { c });
        _timeline.EnsureVisible(c.Start);
    }

    private void DoSplit()
    {
        if (!_s.Split(_s.Playhead)) SetStatus("Put the playhead on a clip to cut it there.");
    }

    private void ShowTimelineMenu(Clip? clip, double time)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("ThemedMenu") };
        if (clip != null)
        {
            menu.Items.Add(Item("Split at the playhead", DoSplit, "S"));
            menu.Items.Add(Item("Split here", () => { _s.Playhead = time; DoSplit(); _timeline.InvalidateVisual(); UpdateTime(); }));
            menu.Items.Add(new Separator { Style = (Style)FindResource("ThemedSeparator") });
            menu.Items.Add(Item("Duplicate", () => _s.Duplicate(), "Ctrl+D"));
            menu.Items.Add(Item("Delete", () => _s.Delete(false), "Del"));
            menu.Items.Add(Item("Delete and close the gap", () => _s.Delete(true), "Shift+Del"));
        }
        else
        {
            menu.Items.Add(Item("Put the playhead here", () => SeekTo(time)));
            menu.Items.Add(Item("Add a picture track", () => _s.AddTrack(TrackKind.Video)));
            menu.Items.Add(Item("Add a sound track", () => _s.AddTrack(TrackKind.Audio)));
        }
        menu.PlacementTarget = _timeline;
        menu.IsOpen = true;
    }

    // ---------- keyboard ----------
    private void OnKey(object sender, KeyEventArgs e)
    {
        bool typing = Keyboard.FocusedElement is TextBox;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        Key k = e.Key == Key.System ? e.SystemKey : e.Key;
        if (ctrl)
        {
            switch (k)
            {
                case Key.Z: if (!typing) { _s.Undo(); e.Handled = true; } return;
                case Key.Y: if (!typing) { _s.Redo(); e.Handled = true; } return;
                case Key.S: SaveProject(shift); e.Handled = true; return;
                case Key.O: OpenProject(); e.Handled = true; return;
                case Key.I: ImportDialog(); e.Handled = true; return;
                case Key.E: Export(); e.Handled = true; return;
                case Key.D: if (!typing) { _s.Duplicate(); e.Handled = true; } return;
                case Key.A: if (!typing) { _s.Select(_s.Project.AllClips); e.Handled = true; } return;
            }
            return;
        }
        if (typing) { if (k == Key.Escape) { Keyboard.ClearFocus(); _timeline.Focus(); e.Handled = true; } return; }
        double frame = 1.0 / (_s.Project.Fps > 0 ? _s.Project.Fps : 30);
        switch (k)
        {
            case Key.Space: _player.Toggle(); UpdatePlayGlyph(); e.Handled = true; break;
            case Key.S: DoSplit(); e.Handled = true; break;
            case Key.T: AddTitle(); e.Handled = true; break;
            case Key.Delete: case Key.Back: _s.Delete(shift); e.Handled = true; break;
            case Key.Left: SeekTo(_s.Playhead - (shift ? 1 : frame)); e.Handled = true; break;
            case Key.Right: SeekTo(_s.Playhead + (shift ? 1 : frame)); e.Handled = true; break;
            case Key.Home: SeekTo(0); _timeline.Offset = 0; e.Handled = true; break;
            case Key.End: SeekTo(_s.Project.Duration); _timeline.EnsureVisible(_s.Project.Duration); e.Handled = true; break;
            case Key.OemPlus: case Key.Add: ZoomBy(1.25); e.Handled = true; break;
            case Key.OemMinus: case Key.Subtract: ZoomBy(0.8); e.Handled = true; break;
            case Key.OemBackslash: case Key.OemPipe: _timeline.ZoomToFit(); e.Handled = true; break;
            case Key.Escape: _s.ClearSelection(); e.Handled = true; break;
        }
    }

    // ---------- project files ----------
    private bool ConfirmDiscard()
    {
        if (!_s.Dirty) return true;
        var r = UMessage.Ask(this, "Save the changes to this project first?", "Utylix Vid Editor", MessageBoxImage.Question, MessageBoxResult.Yes, MessageBoxResult.Cancel,
            ("Save", MessageBoxResult.Yes), ("Don't save", MessageBoxResult.No), ("Cancel", MessageBoxResult.Cancel));
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return SaveProject(false);
        return true;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exporting) { e.Cancel = true; SetStatus("An export is running: cancel it first."); return; }
        if (!ConfirmDiscard()) e.Cancel = true;
    }

    private bool SaveProject(bool saveAs)
    {
        string? path = _s.Project.Path;
        if (path == null || saveAs)
        {
            var dlg = new SaveFileDialog { Title = "Save the project", Filter = "Utylix Vid project (*.utvid)|*.utvid", DefaultExt = ".utvid", FileName = path != null ? Path.GetFileName(path) : "My video.utvid" };
            if (dlg.ShowDialog(this) != true) return false;
            path = dlg.FileName;
        }
        try { _s.Project.Save(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            UMessage.Show(this, "The project could not be saved:\n\n" + e.Message, "Utylix Vid Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _s.ClearDirty();
        UpdateTitle();
        SetStatus("Saved.");
        return true;
    }

    private void OpenProject()
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog { Title = "Open a project", Filter = "Utylix Vid project (*.utvid)|*.utvid|All files|*.*" };
        if (dlg.ShowDialog(this) == true) LoadProject(dlg.FileName);
    }

    private void LoadProject(string path, bool keepPath = true)
    {
        try
        {
            var p = VidProject.Load(path);
            if (!keepPath) p.Path = null;
            _player.Pause();
            _s.Load(p);
            _binSignature = -1;
            OnProjectChanged();
            _timeline.ZoomToFit();
            UpdatePlayGlyph();
            var missing = p.Media.Where(m => !File.Exists(m.Path)).Select(m => m.Name).ToList();
            if (missing.Count > 0)
                UMessage.Show(this, "These files are not where the project expects them:\n\n" + string.Join("\n", missing.Take(10)) + (missing.Count > 10 ? "\n…" : ""),
                    "Utylix Vid Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            UMessage.Show(this, "This project could not be opened:\n\n" + e.Message, "Utylix Vid Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- settings ----------
    private void OpenSettings()
    {
        var w = new VidSettingsWindow(this);
        w.ShowDialog();
        ApplyAutosaveSetting();
        _timeline.InvalidateVisual();
        RefreshInspector();
        UpdatePlayGlyph();
    }

    private void ApplyAutosaveSetting()
    {
        int secs = VidSettings.Current.AutosaveSeconds;
        _autosave.Stop();
        if (secs > 0) { _autosave.Interval = TimeSpan.FromSeconds(secs); _autosave.Start(); }
    }

    // ---------- export ----------
    private bool _exporting;

    private void Export()
    {
        if (_s.Project.Duration <= 0.05) { UMessage.Show(this, "The timeline is empty. Add something to it first.", "Utylix Vid Editor"); return; }
        if (_player.IsPlaying) { _player.Pause(); UpdatePlayGlyph(); }
        var w = new VidExportWindow(_s.Project, Path.GetFileNameWithoutExtension(_s.Project.Path ?? "My video")) { Owner = this };
        _exporting = true;
        try { w.ShowDialog(); }
        finally { _exporting = false; }
    }
}
