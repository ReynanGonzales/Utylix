using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IdmClone.Engine;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace IdmClone;

/// <summary>One song in the playlist. The facts (title, artist, album, length) are read in the background after it is added.</summary>
public sealed class Track : INotifyPropertyChanged
{
    public string Path { get; init; } = "";
    private string _title = "", _artist = "", _album = "";
    private long _length;
    private bool _current;
    public string Title { get => _title.Length > 0 ? _title : System.IO.Path.GetFileNameWithoutExtension(Path); set { _title = value; Changed(nameof(Title)); Changed(nameof(Line2)); Changed(nameof(Primary)); Changed(nameof(Secondary)); } }
    public string Artist { get => _artist; set { _artist = value; Changed(nameof(Artist)); Changed(nameof(Line2)); Changed(nameof(Primary)); Changed(nameof(Secondary)); } }
    public string Album { get => _album; set { _album = value; Changed(nameof(Album)); } }
    public long Length { get => _length; set { _length = value; Changed(nameof(LengthText)); } }
    private bool _favourite;
    /// <summary>A song marked with the heart (kept in music.json by its path).</summary>
    public bool IsFavourite { get => _favourite; set { _favourite = value; Changed(nameof(IsFavourite)); Changed(nameof(HeartMark)); } }
    public string HeartMark => _favourite ? "\uEB51" : "";
    public string LengthText => _length <= 0 ? "" : TimeSpan.FromMilliseconds(_length).ToString(_length >= 3600000 ? @"h\:mm\:ss" : @"m\:ss");
    /// <summary>What the lists and the player show: 0 = song and artist, 1 = artist only, 2 = song only.</summary>
    public static int DisplayMode;
    public string Primary => DisplayMode == 1 ? (_artist.Length > 0 ? _artist : Title) : Title;
    public string Secondary => DisplayMode == 0 ? Line2 : "";
    public void RefreshDisplay() { Changed(nameof(Primary)); Changed(nameof(Secondary)); }
    public string Line2 => _artist.Length > 0 ? _artist : System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path) ?? "");
    public bool IsCurrent { get => _current; set { _current = value; Changed(nameof(Marker)); Changed(nameof(Weight)); } }
    public string Marker => _current ? "" : "";
    public FontWeight Weight => _current ? FontWeights.SemiBold : FontWeights.Normal;
    public bool InfoRead { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The Utylix music player: a playlist with cover art, shuffle and repeat, the keyboard's media keys, and a small mode. libvlc plays;
/// like the video player it is only ever called from one separate thread.
/// </summary>
public sealed partial class MusicWindow : Window
{
    private enum Repeat { Off, All, One }

    private sealed record Snap(VLCState State, bool Playing, long Time, long Length)
    {
        public static readonly Snap Empty = new(VLCState.NothingSpecial, false, 0, 0);
    }

    private sealed class Saved
    {
        public int Volume { get; set; } = 80;
        public bool Shuffle { get; set; }
        public int Repeat { get; set; }
        public int Display { get; set; }
        public double Width { get; set; } = 900;
        public double Height { get; set; } = 560;
        public SoundSettings Sound { get; set; } = new();
        public List<string> Queue { get; set; } = new();          // the playlist as it was left (shown again when the player is opened empty)
        public int LastIndex { get; set; } = -1;
        public string Look { get; set; } = "classic";              // how the player looks (see MusicWindow.Looks.cs)
        public List<string> Favourites { get; set; } = new();       // songs marked with the heart
        public bool ShowList { get; set; } = true;                 // the wide look: the playlist under the card
        public bool Floating { get; set; }                         // the small player (just the card) was left open
        public double? FloatLeft { get; set; }
        public double? FloatTop { get; set; }
        public bool FloatOnTop { get; set; }
    }

    // ---------- one music window ----------
    private static MusicWindow? _main;
    public static int Count => _main == null ? 0 : 1;
    public static event Action? AnyClosed;
    public static bool IsPlaying => _main?._snap.Playing == true;

    /// <summary>Plays these songs (a folder adds its songs). Songs that arrive while it is open are added to the list and the first one plays.</summary>
    private static DateTime _lastOpen = DateTime.MinValue;

    public static void Open(IEnumerable<string> paths, bool enqueue = false)
    {
        var given = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var songs = Expand(given);
        string? startWith = null;
        // one song opened from Explorer: the rest of its folder follows it, starting at that song
        if (given.Count == 1 && File.Exists(given[0]) && songs.Count == 1)
        {
            string dir = System.IO.Path.GetDirectoryName(songs[0]) ?? "";
            try
            {
                var siblings = Directory.EnumerateFiles(dir).Where(PlayerMedia.IsAudio).ToList();
                siblings.Sort((a, b) => StrCmpLogicalW(System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b)));
                if (siblings.Count > 1 && siblings.Count <= 2000) { startWith = songs[0]; songs = siblings; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (songs.Count == 0) { AnyClosed?.Invoke(); return; }
        if (!PlayerWindow.EnsureEngine()) { AnyClosed?.Invoke(); return; }
        bool join = _main != null && (enqueue || (DateTime.UtcNow - _lastOpen).TotalSeconds < 3);     // files that arrive together (a multi-selection) make one list
        _lastOpen = DateTime.UtcNow;
        if (_main == null) { _main = new MusicWindow(); _main.Show(); }
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        var target = _main;
        target.Dispatcher.BeginInvoke(new Action(() => { if (!join) target.Clear(); target.AddAndPlay(songs, join, startWith); }), DispatcherPriority.ApplicationIdle);
    }

    public static void OpenEmpty()
    {
        if (!PlayerWindow.EnsureEngine()) return;
        if (_main == null) { _main = new MusicWindow(); _main.Show(); _main.RestoreQueue(); }
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public static void TogglePlayFromOutside() => _main?.Dispatcher.BeginInvoke(new Action(() => _main?.TogglePause()));
    public static void NextFromOutside() => _main?.Dispatcher.BeginInvoke(new Action(() => _main?.Next(manual: true)));

    public static void Browse(bool add = false)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open music", Multiselect = true,
            Filter = "Music|" + string.Join(";", PlayerMedia.AudioExtensions.Select(e => "*." + e)) + "|All files|*.*",
        };
        if (dlg.ShowDialog() == true) Open(dlg.FileNames, enqueue: add);
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int StrCmpLogicalW(string a, string b);

    /// <summary>Files stay; a folder becomes the songs in it (and in its folders), in Explorer's order.</summary>
    private static List<string> Expand(IEnumerable<string> paths)
    {
        var result = new List<string>();
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try
            {
                if (Directory.Exists(p))
                {
                    var inside = Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Where(PlayerMedia.IsAudio).Take(5000).ToList();
                    inside.Sort((a, b) => StrCmpLogicalW(a, b));
                    result.AddRange(inside);
                }
                else if (File.Exists(p) && PlayerMedia.IsAudio(p)) result.Add(p);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    // ---------- the player thread ----------
    private readonly BlockingCollection<Action> _commands = new();
    private Thread? _thread;
    private volatile bool _threadStop;
    private volatile Snap _snap = Snap.Empty;
    private MediaPlayer? _mp;                       // (touched only by the player thread)
    private int _volume = 80;

    private void Post(Action<MediaPlayer> command) => _commands.Add(() => { if (_mp != null) command(_mp); });

    private void PlayerThread()
    {
        try
        {
            var vlc = VlcEngine.Instance;
            var mp = new MediaPlayer(vlc) { EnableHardwareDecoding = false };
            mp.EnableKeyInput = false; mp.EnableMouseInput = false;
            mp.EndReached += (_, _) => Dispatcher.BeginInvoke(new Action(() => Next(manual: false)));
            mp.EncounteredError += (_, _) => Dispatcher.BeginInvoke(new Action(OnError));
            mp.Volume = _volume;
            _mp = mp;
            Dispatcher.BeginInvoke(new Action(ApplySound));              // (the equalizer and speed chosen last time)
        }
        catch (Exception ex)
        {
            Dispatcher.BeginInvoke(new Action(() => _nowTitle.Text = "The playback engine could not start: " + ex.Message));
            return;
        }
        var lastSnap = DateTime.MinValue;
        while (!_threadStop)
        {
            if (_commands.TryTake(out var command, 100)) { try { command(); } catch (Exception) { } }
            if ((DateTime.UtcNow - lastSnap).TotalMilliseconds >= 200)
            {
                lastSnap = DateTime.UtcNow;
                try
                {
                    var st = _mp!.State;
                    bool active = st is VLCState.Playing or VLCState.Paused or VLCState.Buffering or VLCState.Opening;
                    _snap = new Snap(st, _mp.IsPlaying, active ? _mp.Time : 0, active ? _mp.Length : 0);
                }
                catch (Exception) { }
            }
        }
        try { _mp!.Stop(); } catch (Exception) { }
        try { _mp!.Dispose(); } catch (Exception) { }
        _mp = null;
    }

    // ---------- the window ----------
    private readonly ObservableCollection<Track> _list = new();
    private List<int> _order = new();                // the order songs are played in (shuffled or not)
    private int _pos = -1;                           // position in _order
    private Repeat _repeat = Repeat.Off;
    private bool _shuffle, _seeking, _compact;
    private readonly Random _random = new();
    private readonly DispatcherTimer _ui = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Saved _saved = LoadSaved();
    private static string SavedPath => System.IO.Path.Combine(App.DataDir, "music.json");

    private readonly Image _cover = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock _nowTitle = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _nowArtist = new() { FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(0xA7, 0xAE, 0xBF)), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Slider _seek = new() { Minimum = 0, Maximum = 1, IsMoveToPointEnabled = true, Margin = new Thickness(0, 14, 0, 0) };
    private readonly TextBlock _timeNow = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xA7, 0xAE, 0xBF)), FontSize = 12 };
    private readonly TextBlock _timeLen = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xA7, 0xAE, 0xBF)), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Slider _vol = new() { Minimum = 0, Maximum = 100, Width = 110, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true };
    private readonly ListBox _playlist = new();
    private Button _play = null!, _shuffleBtn = null!, _repeatBtn = null!, _displayBtn = null!, _prevBtn = null!, _nextBtn = null!, _heartBtn = null!, _moreBtn = null!, _closeBtn = null!, _soundBtn = null!;
    private Border _coverHost = null!;
    private TextBlock _coverFallback = null!, _coverWords = null!, _volIcon = null!;
    private Border _right = null!;
    private Grid _root = null!;
    private int _coverToken;

    private static Saved LoadSaved()
    {
        try
        {
            if (File.Exists(SavedPath))
            {
                var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(SavedPath)) ?? new();
                saved.Sound ??= new SoundSettings();
                if (saved.Sound.Bands == null || saved.Sound.Bands.Length != 10) saved.Sound.Bands = new double[10];
                saved.Queue ??= new List<string>();
                saved.Favourites ??= new List<string>();
                return saved;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    private SoundSettings _sound => _saved.Sound;

    private void Save()
    {
        try
        {
            _saved.Volume = (int)_vol.Value; _saved.Shuffle = _shuffle; _saved.Repeat = (int)_repeat; _saved.Display = Track.DisplayMode;
            _saved.Queue = _list.Select(t => t.Path).Take(5000).ToList();
            _saved.LastIndex = _list.ToList().FindIndex(t => t.IsCurrent);
            if (!_compact && WindowState == WindowState.Normal) { _saved.Width = ActualWidth; _saved.Height = ActualHeight; }
            File.WriteAllText(SavedPath, JsonSerializer.Serialize(_saved));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private MusicWindow()
    {
        Title = "Utylix Music";
        Width = Math.Clamp(_saved.Width, 640, SystemParameters.WorkArea.Width); Height = Math.Clamp(_saved.Height, 420, SystemParameters.WorkArea.Height);
        MinWidth = 380; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x18));
        AllowDrop = true; FontFamily = new FontFamily("Segoe UI");
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/music.ico")); } catch (Exception e) when (e is IOException or UriFormatException) { }
        WindowTheme.DarkTitleBar(this);
        WindowTheme.OwnTaskbarButton(this, "Utylix.Music");
        Track.DisplayMode = Math.Clamp(_saved.Display, 0, 2);
        _volume = Math.Clamp(_saved.Volume, 0, 100); _shuffle = _saved.Shuffle; _repeat = (Repeat)Math.Clamp(_saved.Repeat, 0, 2);
        _vol.Value = _volume;
        _seek.Template = SliderTemplate(); _vol.Template = SliderTemplate();
        _seek.Foreground = _vol.Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF));

        // ---- the controls every look is made of (each look lays them out and dresses them its own way) ----
        _coverHost = new Border { ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Center };
        _coverFallback = new TextBlock { Text = "\uE8D6", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 96, Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x44, 0x5E)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _coverWords = new TextBlock { Text = "MUSIC\nPLAYER", FontSize = 17, Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x6C, 0x75)), TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _coverHost.Child = new Grid { Children = { _coverFallback, _coverWords, _cover } };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_nowTitle, "MusicTitle");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_timeNow, "MusicTime");

        Button T(string glyph, string tip, Action a, string id)
        {
            var b = new Button { Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets") }, ToolTip = tip, Template = ButtonTemplate(false), Foreground = Brushes.White, Margin = new Thickness(3, 0, 3, 0), Focusable = false };
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, id);
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            b.Click += (_, _) => a();
            return b;
        }
        _shuffleBtn = T("\uE8B1", "Shuffle (S)", () => { _shuffle = !_shuffle; RebuildOrder(keepCurrent: true); RefreshModes(); }, "MusicShuffle");
        _prevBtn = T("\uE892", "Previous (P)", () => Previous(), "MusicPrev");
        _play = T("\uE768", "Play / pause (Space)", TogglePause, "MusicPlay");
        _nextBtn = T("\uE893", "Next (N)", () => Next(manual: true), "MusicNext");
        _repeatBtn = T("\uE8EE", "Repeat (R)", () => { _repeat = (Repeat)(((int)_repeat + 1) % 3); RefreshModes(); }, "MusicRepeat");
        _heartBtn = T("\uE006", "Favourite (F): the heart marks songs you love", ToggleFavourite, "MusicHeart");
        _moreBtn = T("\uE10C", "More: song info, look, sound, playlist ...", () => ShowMoreMenu(), "MusicMore");
        _closeBtn = T("\uE711", "Close the player", () => Close(), "MusicClose");
        _volIcon = new TextBlock { Text = "\uE767", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16, Foreground = new SolidColorBrush(Color.FromRgb(0xA7, 0xAE, 0xBF)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        _soundBtn = new Button { Content = "Sound…", Style = (Style)FindResource("SmallButton"), Margin = new Thickness(16, 0, 0, 0), ToolTip = "Equalizer, speed, even out the volume, sleep timer" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_soundBtn, "MusicSound");
        _soundBtn.Click += (_, _) => ShowSound();

        // ---- right: the playlist ----
        var head = new StackPanel { Margin = new Thickness(16, 14, 16, 4) };
        var buttons = new WrapPanel { Margin = new Thickness(-6, 8, 0, 0) };          // (the buttons wrap onto a second line in a narrow window)
        Button Small(string text, Action a, string id) { var b = new Button { Content = text, Style = (Style)FindResource("SmallButton"), Margin = new Thickness(6, 0, 0, 6) }; System.Windows.Automation.AutomationProperties.SetAutomationId(b, id); b.Click += (_, _) => a(); buttons.Children.Add(b); return b; }
        _displayBtn = Small(DisplayLabel(), CycleDisplay, "MusicDisplay");
        Small("Add songs…", () => Browse(add: true), "MusicAdd");
        Small("Add folder…", AddFolder, "MusicFolder");
        Small("Clear", Clear, "MusicClear");
        Small("Small player", ToggleFloating, "MusicCompact").ToolTip = "Just the card, in a window you can drag anywhere";
        Button? lookBtn = null; lookBtn = Small("Look…", () => ShowLookMenu(lookBtn), "MusicLook"); lookBtn.ToolTip = "Change how the player looks (key L cycles through the looks)";
        Small("\u2665", PlayFavourites, "MusicFavourites").ToolTip = "Play only my favourites (the songs with a heart)";
        head.Children.Add(new TextBlock { Text = "Playlist", Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(buttons);

        _playlist.ItemsSource = _list;
        _playlist.Background = Brushes.Transparent; _playlist.BorderThickness = new Thickness(0);
        _playlist.ItemTemplate = (DataTemplate)XamlReader.Parse(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid Margin='6,5'><Grid.ColumnDefinitions><ColumnDefinition Width='22'/><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='20'/></Grid.ColumnDefinitions>" +
            "<TextBlock Text='{Binding Marker}' FontFamily='Segoe MDL2 Assets' Foreground='#5B8DEF' VerticalAlignment='Center' FontSize='12'/>" +
            "<StackPanel Grid.Column='1'><TextBlock Text='{Binding Primary}' Foreground='White' FontWeight='{Binding Weight}' TextTrimming='CharacterEllipsis'/><TextBlock Text='{Binding Secondary}' Foreground='#8B93A5' FontSize='12' TextTrimming='CharacterEllipsis'><TextBlock.Style><Style TargetType='TextBlock'><Style.Triggers><DataTrigger Binding='{Binding Secondary}' Value=''><Setter Property='Visibility' Value='Collapsed'/></DataTrigger></Style.Triggers></Style></TextBlock.Style></TextBlock></StackPanel>" +
            "<TextBlock Grid.Column='2' Text='{Binding LengthText}' Foreground='#8B93A5' FontSize='12' VerticalAlignment='Center' Margin='12,0,4,0'/>" +
            "<TextBlock Grid.Column='3' Text='{Binding HeartMark}' FontFamily='Segoe MDL2 Assets' Foreground='#E8506A' FontSize='12' VerticalAlignment='Center' Margin='4,0,2,0'/></Grid></DataTemplate>");
        _playlist.ItemContainerStyle = (Style)XamlReader.Parse(
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListBoxItem'><Setter Property='Template'><Setter.Value>" +
            "<ControlTemplate TargetType='ListBoxItem'><Border x:Name='bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='Transparent' CornerRadius='6' Margin='8,1'><ContentPresenter/></Border>" +
            "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='#1F2533'/></Trigger>" +
            "<Trigger Property='IsSelected' Value='True'><Setter TargetName='bd' Property='Background' Value='#262E42'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>");
        _playlist.SelectionMode = SelectionMode.Extended;
        _playlist.MouseDoubleClick += (_, _) => { if (_playlist.SelectedIndex >= 0) PlayTrack(_playlist.SelectedIndex); };
        _playlist.ContextMenuOpening += (_, e) => { if (_playlist.SelectedItems.Count == 0) e.Handled = true; else _playlist.ContextMenu = PlaylistMenu(); };
        _playlist.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && _playlist.SelectedIndex >= 0) { RemoveAt(_playlist.SelectedIndex); e.Handled = true; }
            else if (e.Key == Key.Enter && _playlist.SelectedIndex >= 0) { PlayTrack(_playlist.SelectedIndex); e.Handled = true; }
            else if (e.Key == Key.F2 && _playlist.SelectedItems.Count > 0) { EditInfo(_playlist.SelectedItems.Cast<Track>().ToList()); e.Handled = true; }
        };
        var emptyHint = new TextBlock { Text = "Drop songs or a folder here,\nor press Add songs…", Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA5)), FontSize = 14, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        _list.CollectionChanged += (_, _) => emptyHint.Visibility = _list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var rightGrid = new Grid();
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        // a box to find a song in a long playlist: typing jumps to the first match, Enter goes to the next, Enter on a chosen song plays it
        var find = new TextBox { Margin = new Thickness(16, 0, 16, 8), Padding = new Thickness(8, 5, 8, 5), Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1F, 0x2B)), Foreground = Brushes.White, CaretBrush = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x34, 0x46)), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Find a song in the playlist (title, artist, album or file name). Enter: the next match" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(find, "MusicFind");
        var findHint = new TextBlock { Text = "Find a song…", Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x93, 0xA5)), Margin = new Thickness(26, 0, 0, 8), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        find.TextChanged += (_, _) => { findHint.Visibility = find.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; FindSong(find.Text, next: false); };
        find.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { FindSong(find.Text, next: true); e.Handled = true; }
            else if (e.Key == Key.Escape) { find.Text = ""; _playlist.Focus(); e.Handled = true; }
            else if (e.Key == Key.Down && _playlist.Items.Count > 0) { _playlist.Focus(); e.Handled = true; }
        };
        var findBox = new Grid(); findBox.Children.Add(find); findBox.Children.Add(findHint);
        Grid.SetRow(head, 0); Grid.SetRow(findBox, 1); Grid.SetRow(_playlist, 2);
        rightGrid.Children.Add(head); rightGrid.Children.Add(findBox); rightGrid.Children.Add(_playlist);
        var listArea = new Grid(); listArea.Children.Add(rightGrid); listArea.Children.Add(emptyHint);
        _right = new Border { Child = listArea, Background = new SolidColorBrush(Color.FromRgb(0x15, 0x18, 0x21)) };

        _root = new Grid();
        Content = _root;
        ApplyLook(_saved.Look);
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image)).AddValueChanged(_cover, (_, _) => UpdateCoverVisibility());
        UpdateCoverVisibility();

        // ---- input ----
        _seek.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, _) => _seeking = true), true);
        _seek.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) =>
        {
            var snap = _snap;
            long target = (long)(_seek.Value / Math.Max(1, _seek.Maximum) * snap.Length);
            Post(mp => mp.Time = target);
            _ = Task.Delay(300).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => _seeking = false)));
        }), true);
        _vol.ValueChanged += (_, _) => { int v = (int)_vol.Value; Post(mp => mp.Volume = v); };
        PreviewKeyDown += OnKey;
        Drop += (_, e) => DropFiles(e);
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Loaded += (_, _) => { if (_saved.Floating) Dispatcher.BeginInvoke(new Action(EnterFloating), DispatcherPriority.ApplicationIdle); _thread = new Thread(PlayerThread) { IsBackground = true, Name = "Utylix music" }; _thread.SetApartmentState(ApartmentState.MTA); _thread.Start(); _ui.Start(); RegisterMediaKeys(); };
        _ui.Tick += (_, _) => UpdateUi();
        Closing += (_, _) => { if (_floating != null) { _saved.FloatLeft = _floating.Left; _saved.FloatTop = _floating.Top; _saved.FloatOnTop = _floating.Topmost; } Save(); _ui.Stop(); UnregisterMediaKeys(); _threadStop = true; _floating?.CloseForReal(); };
        Closed += (_, _) => { _main = null; AnyClosed?.Invoke(); };
        RefreshModes();
        emptyHint.Visibility = Visibility.Visible;
    }

    // ---------- sound: equalizer, speed, sleep timer ----------
    private Equalizer? _eq;                         // (touched only by the player thread)
    private MusicSoundWindow? _soundWindow;
    private DispatcherTimer? _sleepTimer;
    private DateTime _sleepAt;
    private bool _sleepAfterSong;

    private void ShowSound()
    {
        if (_soundWindow != null) { _soundWindow.Activate(); return; }
        _soundWindow = new MusicSoundWindow(this, _sound, ApplySound, SetSleep, SleepText);
        _soundWindow.Closed += (_, _) => { _soundWindow = null; Save(); };
        _soundWindow.Show();
    }

    /// <summary>Puts the equalizer and speed chosen in the Sound window on the playing song (and the ones after it).</summary>
    private void ApplySound()
    {
        var s = _sound;
        bool on = s.EqOn; float preamp = (float)s.Preamp; float[] bands = s.Bands.Select(b => (float)b).ToArray(); float speed = (float)s.Speed;
        Post(mp =>
        {
            if (on)
            {
                var eq = new Equalizer();
                eq.SetPreamp(preamp);
                for (uint i = 0; i < bands.Length && i < eq.BandCount; i++) eq.SetAmp(bands[i], i);
                mp.SetEqualizer(eq);
                _eq?.Dispose(); _eq = eq;
            }
            else
            {
                var flat = new Equalizer();                                    // (all bands at 0 dB = the sound as it is)
                mp.SetEqualizer(flat);
                _eq?.Dispose(); _eq = flat;
            }
            mp.SetRate(speed);
        });
    }

    /// <summary>0 = off, minutes = stop after that long, -1 = stop when this song ends.</summary>
    private void SetSleep(int minutes)
    {
        _sleepTimer?.Stop();
        _sleepAfterSong = minutes < 0;
        if (minutes <= 0) return;
        _sleepAt = DateTime.UtcNow.AddMinutes(minutes);
        _sleepTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _sleepTimer.Tick -= SleepTick; _sleepTimer.Tick += SleepTick;
        _sleepTimer.Start();
    }

    private void SleepTick(object? sender, EventArgs e)
    {
        if (DateTime.UtcNow < _sleepAt) return;
        _sleepTimer?.Stop();
        Post(mp => mp.SetPause(true));                                         // (paused, not closed: press play to go on)
        _nowArtist.Text = "Sleep timer: paused";
    }

    private string SleepText()
    {
        if (_sleepAfterSong) return "The music stops when this song ends.";
        if (_sleepTimer is { IsEnabled: true }) { var left = _sleepAt - DateTime.UtcNow; if (left < TimeSpan.Zero) left = TimeSpan.Zero; return $"The music pauses in {(int)left.TotalMinutes}:{left.Seconds:00}."; }
        return "No sleep timer.";
    }

    // ---------- the playlist as it was left ----------
    private void RestoreQueue()
    {
        if (_list.Count > 0 || _saved.Queue.Count == 0) return;
        foreach (string path in _saved.Queue) if (File.Exists(path)) _list.Add(NewTrack(path));
        if (_list.Count == 0) return;
        RebuildOrder(keepCurrent: false);
        if (_saved.LastIndex >= 0 && _saved.LastIndex < _list.Count) { _playlist.SelectedIndex = _saved.LastIndex; _playlist.ScrollIntoView(_list[_saved.LastIndex]); _pos = Math.Max(0, _order.IndexOf(_saved.LastIndex)); }
        _ = ReadInfoAsync();
    }

    // ---------- finding a song in the playlist ----------
    private void FindSong(string text, bool next)
    {
        text = text.Trim();
        if (text.Length == 0 || _list.Count == 0) return;
        int start = next ? Math.Max(0, _playlist.SelectedIndex) + 1 : 0;
        for (int k = 0; k < _list.Count; k++)
        {
            int i = (start + k) % _list.Count;
            var t = _list[i];
            if (t.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase) || t.Artist.Contains(text, StringComparison.CurrentCultureIgnoreCase) || t.Album.Contains(text, StringComparison.CurrentCultureIgnoreCase) || System.IO.Path.GetFileNameWithoutExtension(t.Path).Contains(text, StringComparison.CurrentCultureIgnoreCase))
            {
                _playlist.SelectedIndex = i; _playlist.ScrollIntoView(t);
                return;
            }
        }
    }

    // ---------- templates ----------
    /// <summary>A round flat button; its size comes from Width / Height. light: for the light looks (a dark hover instead of a white one).</summary>
    private static ControlTemplate ButtonTemplate(bool light) => (ControlTemplate)XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'>" +
        "<Border x:Name='bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='Transparent' CornerRadius='100' MinWidth='{TemplateBinding Width}' MinHeight='{TemplateBinding Height}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' /></Border>" +
        "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='Background' Value='" + (light ? "#22000000" : "#26FFFFFF") + "' /></Trigger>" +
        "<Trigger Property='IsPressed' Value='True'><Setter TargetName='bd' Property='Background' Value='" + (light ? "#44000000" : "#44FFFFFF") + "' /></Trigger></ControlTemplate.Triggers></ControlTemplate>");

    private static ControlTemplate SliderTemplate(string track = "#2D3446", string thumb = "White", int thumbSize = 14, int thickness = 4) => (ControlTemplate)XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Slider'>" +
        "<Grid Height='22' Background='Transparent'><Border Height='" + thickness + "' CornerRadius='2' Background='" + track + "' VerticalAlignment='Center'/>" +
        "<Track x:Name='PART_Track' VerticalAlignment='Center'>" +
        "<Track.DecreaseRepeatButton><RepeatButton Command='Slider.DecreaseLarge' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Height='" + thickness + "' CornerRadius='2' Background='{Binding Foreground, RelativeSource={RelativeSource AncestorType=Slider}}'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>" +
        "<Track.IncreaseRepeatButton><RepeatButton Command='Slider.IncreaseLarge' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Height='22' Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>" +
        "<Track.Thumb><Thumb Width='" + thumbSize + "' Height='" + thumbSize + "' Focusable='False'><Thumb.Template><ControlTemplate TargetType='Thumb'><Ellipse Fill='" + thumb + "' Width='" + thumbSize + "' Height='" + thumbSize + "'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>" +
        "</Track></Grid></ControlTemplate>");

    // ---------- the playlist ----------
    private void AddAndPlay(List<string> songs, bool enqueue, string? startWith = null)
    {
        int first = _list.Count;
        foreach (var s in songs) _list.Add(NewTrack(s));
        if (startWith != null) { int at = songs.FindIndex(x => string.Equals(x, startWith, StringComparison.OrdinalIgnoreCase)); if (at > 0) first += at; }
        RebuildOrder(keepCurrent: true);
        if (!enqueue || _pos < 0 || !_snap.Playing && _snap.State is VLCState.Stopped or VLCState.Ended or VLCState.NothingSpecial)
            PlayTrack(first);
        _ = ReadInfoAsync();
    }

    private void RebuildOrder(bool keepCurrent)
    {
        int current = _pos >= 0 && _pos < _order.Count ? _order[_pos] : -1;
        _order = Enumerable.Range(0, _list.Count).ToList();
        if (_shuffle)
            for (int i = _order.Count - 1; i > 0; i--) { int j = _random.Next(i + 1); (_order[i], _order[j]) = (_order[j], _order[i]); }
        if (keepCurrent && current >= 0 && current < _list.Count)
        {
            int at = _order.IndexOf(current);
            if (_shuffle && at > 0) { _order.RemoveAt(at); _order.Insert(0, current); at = 0; }     // the song that plays stays first; the rest are shuffled after it
            _pos = Math.Max(0, at);
        }
        else _pos = -1;
    }

    private void PlayTrack(int listIndex)
    {
        if (listIndex < 0 || listIndex >= _list.Count) return;
        int at = _order.IndexOf(listIndex);
        _pos = at >= 0 ? at : 0;
        var track = _list[listIndex];
        foreach (var t in _list) t.IsCurrent = t == track;
        _playlist.SelectedIndex = listIndex; _playlist.ScrollIntoView(track);
        UpdateNow(track);
        _cover.Source = null;
        UpdateHeart();
        if (_wave != null) _ = LoadWaveAsync(track);
        _ = LoadCoverAsync(track);
        string path = track.Path;
        bool even = _sound.Normalize;
        float speed = (float)_sound.Speed;
        Post(mp =>
        {
            using var media = new Media(VlcEngine.Instance, path, FromType.FromPath);
            if (even) media.AddOption(":audio-filter=normvol");           // evens out loud and quiet songs
            mp.Play(media);
            mp.SetRate(speed);
        });
        track.PropertyChanged += OnTrackChanged;
        _ = ReadInfoAsync();
    }

    private void OnTrackChanged(object? s, PropertyChangedEventArgs e)
    {
        if (s is Track t && t.IsCurrent) UpdateNow(t);
    }

    /// <summary>The big text of the player follows the "Show" choice: song and artist, only the artist, or only the song.</summary>
    private void UpdateNow(Track t)
    {
        try { UpdateNowCore(t); if (_floating != null) _floating.Title = Title; } catch (InvalidOperationException) { }
    }

    private void UpdateNowCore(Track t)
    {
        string artist = t.Artist.Length > 0 ? t.Artist : "";
        switch (Track.DisplayMode)
        {
            case 1:
                _nowTitle.Text = artist.Length > 0 ? artist : t.Title; _nowArtist.Text = "";
                Title = _nowTitle.Text + " - Utylix Music"; break;
            case 2:
                _nowTitle.Text = t.Title; _nowArtist.Text = "";
                Title = t.Title + " - Utylix Music"; break;
            default:
                _nowTitle.Text = t.Title; _nowArtist.Text = artist.Length > 0 ? artist + (t.Album.Length > 0 ? "  ·  " + t.Album : "") : t.Line2;
                Title = t.Title + " - Utylix Music"; break;
        }
    }

    private static string DisplayLabel() => Track.DisplayMode switch { 1 => "Show: artist only", 2 => "Show: song only", _ => "Show: song + artist" };

    private void CycleDisplay()
    {
        Track.DisplayMode = (Track.DisplayMode + 1) % 3;
        _displayBtn.Content = DisplayLabel();
        foreach (var t in _list) t.RefreshDisplay();
        var cur = _list.FirstOrDefault(t => t.IsCurrent);
        if (cur != null) UpdateNow(cur);
    }

    private void Next(bool manual)
    {
        if (_list.Count == 0) return;
        if (!manual && _sleepAfterSong) { _sleepAfterSong = false; Post(mp => mp.Stop()); _nowArtist.Text = "Sleep timer: stopped"; return; }
        if (!manual && _repeat == Repeat.One) { PlayTrack(_order[Math.Max(0, _pos)]); return; }
        int next = _pos + 1;
        if (next >= _order.Count)
        {
            if (_repeat == Repeat.All || manual) { if (_shuffle) RebuildOrder(keepCurrent: false); next = 0; }
            else { Post(mp => mp.Stop()); return; }          // the list is over
        }
        PlayTrack(_order[next]);
    }

    private void Previous()
    {
        if (_list.Count == 0) return;
        if (_snap.Time > 3000 || _pos <= 0) { Post(mp => mp.Time = 0); return; }        // more than 3 seconds in: back to the start of this song first
        PlayTrack(_order[_pos - 1]);
    }

    private void TogglePause()
    {
        if (_list.Count == 0) { Browse(add: true); return; }
        var snap = _snap;
        if (snap.State is VLCState.Stopped or VLCState.Ended or VLCState.NothingSpecial or VLCState.Error) { PlayTrack(_pos >= 0 && _pos < _order.Count ? _order[_pos] : 0); return; }
        Post(mp => mp.Pause());
    }

    private void RemoveAt(int i)
    {
        bool wasCurrent = _list[i].IsCurrent;
        _list.RemoveAt(i);
        RebuildOrder(keepCurrent: false);
        if (wasCurrent) { Post(mp => mp.Stop()); _nowTitle.Text = ""; _nowArtist.Text = ""; _cover.Source = null; UpdateHeart(); }
        else { int cur = _list.ToList().FindIndex(t => t.IsCurrent); _pos = cur >= 0 ? _order.IndexOf(cur) : -1; }
    }

    private void Clear() { _list.Clear(); _order.Clear(); _pos = -1; Post(mp => mp.Stop()); _nowTitle.Text = ""; _nowArtist.Text = ""; _cover.Source = null; Title = "Utylix Music"; }

    private void AddFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Add a music folder" };
        if (dlg.ShowDialog() == true) { var songs = Expand(new[] { dlg.FolderName }); if (songs.Count > 0) AddAndPlay(songs, enqueue: _list.Count > 0 && _snap.Playing); }
    }

    private void ToggleCompact()
    {
        _compact = !_compact;
        _right.Visibility = _compact || (_vertical && !_saved.ShowList) ? Visibility.Collapsed : Visibility.Visible;
        if (!_compact) { SizeToContent = SizeToContent.Manual; Width = Math.Clamp(_saved.Width, 640, SystemParameters.WorkArea.Width); Height = Math.Clamp(_saved.Height, 420, SystemParameters.WorkArea.Height); }
        FitWindowToLook();
    }

    /// <summary>Songs or a folder dropped on the player (or on the small player).</summary>
    private void DropFiles(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped)
        {
            var songs = Expand(dropped);
            if (songs.Count > 0) AddAndPlay(songs, enqueue: _list.Count > 0 && _snap.Playing);
        }
    }

    private void OnError()
    {
        if (_pos < 0 || _pos >= _order.Count) return;
        _nowArtist.Text = "This song can't be played - skipping";
        _ = Task.Delay(900).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => Next(manual: false))));
    }

    // ---------- song facts and cover art ----------
    private bool _reading;
    private async Task ReadInfoAsync()
    {
        if (_reading) return;
        _reading = true;
        try
        {
            while (true)
            {
                var track = _list.FirstOrDefault(t => !t.InfoRead);
                if (track == null) break;
                track.InfoRead = true;
                var (title, artist, album, length) = await Task.Run(() => ReadFacts(track.Path));
                if (!_list.Contains(track)) continue;
                string fileName = System.IO.Path.GetFileName(track.Path), bare = System.IO.Path.GetFileNameWithoutExtension(track.Path);
                if (!string.IsNullOrWhiteSpace(title) && !title.Equals(fileName, StringComparison.OrdinalIgnoreCase) && !title.Equals(bare, StringComparison.OrdinalIgnoreCase)) track.Title = title;
                if (!string.IsNullOrWhiteSpace(artist)) track.Artist = artist;
                if (!string.IsNullOrWhiteSpace(album)) track.Album = album;
                if (length > 0) track.Length = length;
            }
        }
        finally { _reading = false; }
    }

    private static (string? Title, string? Artist, string? Album, long Length) ReadFacts(string path)
    {
        try
        {
            using var media = new Media(VlcEngine.Instance, path, FromType.FromPath);
            media.Parse(MediaParseOptions.ParseLocal, 4000).GetAwaiter().GetResult();
            return (media.Meta(MetadataType.Title), media.Meta(MetadataType.Artist), media.Meta(MetadataType.Album), media.Duration);
        }
        catch (Exception) { return (null, null, null, 0); }
    }

    private async Task LoadCoverAsync(Track track)
    {
        int token = ++_coverToken;
        var picture = await Task.Run(() =>
        {
            try
            {
                string? file = null;
                using (var media = new Media(VlcEngine.Instance, track.Path, FromType.FromPath))
                {
                    media.Parse(MediaParseOptions.ParseLocal, 4000).GetAwaiter().GetResult();
                    string? art = media.Meta(MetadataType.ArtworkURL);
                    if (!string.IsNullOrEmpty(art) && Uri.TryCreate(art, UriKind.Absolute, out var u) && u.IsFile && File.Exists(u.LocalPath)) file = u.LocalPath;
                }
                if (file == null)                                               // a picture next to the songs: cover.jpg, folder.jpg, front.png ...
                {
                    string dir = System.IO.Path.GetDirectoryName(track.Path) ?? "";
                    foreach (string name in new[] { "cover", "folder", "front", "album", "albumart", "artwork" })
                        foreach (string ext in new[] { "jpg", "jpeg", "png" })
                        {
                            string candidate = System.IO.Path.Combine(dir, name + "." + ext);
                            if (File.Exists(candidate)) { file = candidate; break; }
                        }
                }
                if (file == null) return null;
                var b = new BitmapImage();
                b.BeginInit(); b.UriSource = new Uri(file); b.DecodePixelWidth = 600; b.CacheOption = BitmapCacheOption.OnLoad; b.EndInit(); b.Freeze();
                return (ImageSource)b;
            }
            catch (Exception e) when (e is IOException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException or COMException or FileFormatException) { return null; }
        });
        if (token == _coverToken) _cover.Source = picture;
    }

    // ---------- screen ----------
    private void UpdateUi()
    {
        var s = _snap;
        if (_wave != null) _wave.Progress = s.Length > 0 ? (double)s.Time / s.Length : 0;
        ((TextBlock)_play.Content).Text = s.Playing ? "" : "";
        if (_seeking) return;
        _seek.Maximum = Math.Max(1, s.Length); _seek.Value = Math.Min(s.Time, _seek.Maximum);
        _timeNow.Text = Fmt(s.Time); _timeLen.Text = s.Length > 0 ? Fmt(s.Length) : "";
        // the song's length: shown in the list too
        if (s.Length > 0 && _pos >= 0 && _pos < _order.Count) { var t = _list[_order[_pos]]; if (t.Length <= 0) t.Length = s.Length; }
    }

    private static string Fmt(long ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms)).ToString(ms >= 3600000 ? @"h\:mm\:ss" : @"m\:ss");

    private void RefreshModes()
    {
        _shuffleBtn.Foreground = _shuffle ? _accentBrush : _fgBrush;
        _repeatBtn.Foreground = _repeat == Repeat.Off ? _fgBrush : _accentBrush;
        ((TextBlock)_repeatBtn.Content).Text = _repeat == Repeat.One ? "" : "";
        _repeatBtn.ToolTip = _repeat switch { Repeat.Off => "Repeat: off (R)", Repeat.All => "Repeat: all songs (R)", _ => "Repeat: this song (R)" };
    }

    // ---------- keys ----------
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        bool handled = true;
        switch (e.Key)
        {
            case Key.Space: TogglePause(); break;
            case Key.N: Next(manual: true); break;
            case Key.P: Previous(); break;
            case Key.S: _shuffle = !_shuffle; RebuildOrder(keepCurrent: true); RefreshModes(); break;
            case Key.R: _repeat = (Repeat)(((int)_repeat + 1) % 3); RefreshModes(); break;
            case Key.Left: { long t = Math.Max(0, _snap.Time - 5000); Post(mp => mp.Time = t); break; }
            case Key.Right: { long t = _snap.Time + 5000; Post(mp => mp.Time = t); break; }
            case Key.Up: _vol.Value = Math.Min(100, _vol.Value + 5); break;
            case Key.Down: _vol.Value = Math.Max(0, _vol.Value - 5); break;
            case Key.O when (Keyboard.Modifiers & ModifierKeys.Control) != 0: Browse(add: true); break;
            case Key.F: ToggleFavourite(); break;
            case Key.F2: { var picked = _playlist.SelectedItems.Cast<Track>().ToList(); var cur = CurrentTrack; if (picked.Count == 0 && cur != null) picked.Add(cur); if (picked.Count > 0) EditInfo(picked); break; }
            case Key.L: { int at = Array.FindIndex(LookList, l => l.Key == _look); ApplyLook(LookList[(at + 1) % LookList.Length].Key); Save(); break; }
            case Key.MediaPlayPause: TogglePause(); break;
            case Key.MediaNextTrack: Next(manual: true); break;
            case Key.MediaPreviousTrack: Previous(); break;
            case Key.Escape: if (_floating != null) ExitFloating(); else Close(); break;
            default: handled = false; break;
        }
        e.Handled = handled;
    }

    // ---------- the keyboard's media keys (they work while this window is open, whatever program has the focus) ----------
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    private HwndSource? _source;
    private readonly List<int> _keys = new();

    private void RegisterMediaKeys()
    {
        _source = (HwndSource?)PresentationSource.FromVisual(this);
        if (_source == null) return;
        _source.AddHook(WndProc);
        foreach (var (id, vk) in new[] { (9101, 0xB3u), (9102, 0xB0u), (9103, 0xB1u) })
            if (RegisterHotKey(_source.Handle, id, 0, vk)) _keys.Add(id);
    }

    private void UnregisterMediaKeys()
    {
        if (_source == null) return;
        foreach (int id in _keys) UnregisterHotKey(_source.Handle, id);
        _source.RemoveHook(WndProc);
        _keys.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312)                      // WM_HOTKEY
        {
            switch (wParam.ToInt32())
            {
                case 9101: TogglePause(); handled = true; break;
                case 9102: Next(manual: true); handled = true; break;
                case 9103: Previous(); handled = true; break;
            }
        }
        return IntPtr.Zero;
    }
}
