using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IdmClone.Engine;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace IdmClone;

/// <summary>One entry of the playlist.</summary>
public sealed class PlaylistItem : INotifyPropertyChanged
{
    public string Path { get; init; } = "";
    public string Title { get; init; } = "";
    public bool IsUrl { get; init; }
    private bool _current;
    public bool IsCurrent { get => _current; set { _current = value; Changed(nameof(Marker)); Changed(nameof(Weight)); } }
    public string Marker => _current ? "" : "";
    public FontWeight Weight => _current ? FontWeights.SemiBold : FontWeights.Normal;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The Utylix Player: a VLC-style player in its own window. libvlc (the engine inside VLC) plays and decodes; everything around it
/// (menus, seek bar, playlist, resume, full screen) is Utylix.
///
/// Every call into libvlc is made on one separate "player thread", never on the window's thread: libvlc's video output calls back
/// into the window, so a libvlc call made on the window's own thread can wait for the very thread that is waiting for it (the
/// player freezes). The window only reads a snapshot of the player's state, which that thread refreshes several times a second.
/// </summary>
public partial class PlayerWindow : Window
{
    private enum Repeat { Off, All, One }

    /// <summary>What the player thread last saw.</summary>
    private sealed record Snap(
        VLCState State, bool Playing, long Time, long Length, bool Seekable, float Rate, bool Mute, int AudioTrack, int Spu,
        TrackDescription[] Audio, TrackDescription[] Subs, long AudioDelay, long SpuDelay)
    {
        public static readonly Snap Empty = new(VLCState.NothingSpecial, false, 0, 0, false, 1f, false, -1, -1, Array.Empty<TrackDescription>(), Array.Empty<TrackDescription>(), 0, 0);
    }

    // ---------- one player window; files that arrive later join it ----------
    private static PlayerWindow? _main;
    private static DateTime _lastOpen = DateTime.MinValue;
    public static int Count => _main == null ? 0 : 1;
    public static event Action? AnyClosed;

    /// <summary>Play files (Explorer's "Play with Utylix", double-click, drag). Files that arrive within 3 seconds of each other join one playlist.</summary>
    public static void Open(IEnumerable<string> files, bool enqueue = false)
    {
        var list = files.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        if (list.Count == 0) return;
        if (!EnsureEngine()) { AnyClosed?.Invoke(); return; }
        bool join = _main != null && (enqueue || (DateTime.UtcNow - _lastOpen).TotalSeconds < 3);
        _lastOpen = DateTime.UtcNow;
        if (_main == null) { _main = new PlayerWindow(); _main.Show(); }
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        var target = _main;                                                      // (a window that was just created starts its engine when it is shown: wait for that)
        target.Dispatcher.BeginInvoke(new Action(() => { if (join) target.Enqueue(list); else target.PlayFiles(list); }), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Opens an empty player (from the Video Player tab).</summary>
    public static PlayerWindow? OpenEmpty()
    {
        if (!EnsureEngine()) return null;
        if (_main == null) { _main = new PlayerWindow(); _main.Show(); }
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        return _main;
    }

    /// <summary>true when the playback engine is there; otherwise asks before downloading it.</summary>
    public static bool EnsureEngine()
    {
        if (VlcEngine.Available) return true;
        var window = new ToolDownloadWindow("Video Player",
            "The Video Player needs its playback engine",
            "It is the same engine VLC is made of, so the player opens practically every video and music format, with subtitles and audio tracks. " +
            "It is downloaded once (about 80 MB) from videolan.org, checked against the checksum VideoLAN publishes, and kept on this PC.",
            "Download (80 MB)", VlcEngine.InstallAsync);
        return window.ShowDialog() == true && VlcEngine.Available;
    }

    // ---------- the player thread ----------
    private readonly BlockingCollection<Action> _commands = new();
    private Thread? _thread;
    private volatile bool _threadStop;
    private volatile Snap _snap = Snap.Empty;
    private LibVLC? _vlc;
    private MediaPlayer? _mp;                        // (touched only by the player thread)
    private bool _engineFailed;

    private void Post(Action<MediaPlayer> command) => _commands.Add(() => { if (_mp != null) command(_mp); });

    private void StartThread()
    {
        _thread = new Thread(PlayerThread) { IsBackground = true, Name = "Utylix player" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void PlayerThread()
    {
        try
        {
            _vlc = VlcEngine.Instance;
            var mp = new MediaPlayer(_vlc) { EnableHardwareDecoding = true };
            mp.EnableKeyInput = false;                                              // keys and mouse belong to this window, not to VLC's own picture window
            mp.EnableMouseInput = false;
            mp.SetMarqueeInt(VideoMarqueeOption.Enable, 1);
            mp.SetMarqueeInt(VideoMarqueeOption.Position, 5);                      // top left
            mp.SetMarqueeInt(VideoMarqueeOption.Size, 26);
            mp.SetMarqueeInt(VideoMarqueeOption.Timeout, 1800);
            mp.SetMarqueeInt(VideoMarqueeOption.Opacity, 230);
            mp.EndReached += (_, _) => Dispatcher.BeginInvoke(new Action(OnEnded));
            mp.EncounteredError += (_, _) => Dispatcher.BeginInvoke(new Action(OnError));
            mp.Hwnd = Dispatcher.Invoke(() => Video.Surface);
            mp.Volume = Dispatcher.Invoke(() => (int)VolumeSlider.Value);
            _mp = mp;
        }
        catch (Exception ex)
        {
            _engineFailed = true;
            Dispatcher.BeginInvoke(new Action(() => IdleText.Text = "The playback engine could not start: " + ex.Message));
            return;
        }

        var lastSnap = DateTime.MinValue;
        while (!_threadStop)
        {
            if (_commands.TryTake(out var command, 100))
            {
                try { command(); } catch (Exception) { /* one failed command must not stop the player */ }
            }
            if ((DateTime.UtcNow - lastSnap).TotalMilliseconds >= 150)
            {
                lastSnap = DateTime.UtcNow;
                try { _snap = Take(_mp!); } catch (Exception) { }
            }
        }
        try { _mp!.Stop(); } catch (Exception) { }
        try { _mp!.Dispose(); } catch (Exception) { }
        _mp = null;
    }

    private static Snap Take(MediaPlayer mp)
    {
        var state = mp.State;
        bool active = state is VLCState.Playing or VLCState.Paused or VLCState.Buffering or VLCState.Opening;
        return new Snap(state, mp.IsPlaying, active ? mp.Time : 0, active ? mp.Length : 0, active && mp.IsSeekable, mp.Rate, mp.Mute,
                        active ? mp.AudioTrack : -1, active ? mp.Spu : -1,
                        active ? mp.AudioTrackDescription ?? Array.Empty<TrackDescription>() : Array.Empty<TrackDescription>(),
                        active ? mp.SpuDescription ?? Array.Empty<TrackDescription>() : Array.Empty<TrackDescription>(),
                        active ? mp.AudioDelay : 0, active ? mp.SpuDelay : 0);
    }

    // ---------- the window ----------
    private readonly PlayerState _state = PlayerState.Current;
    private readonly ObservableCollection<PlaylistItem> _list = new();
    private int _index = -1;
    private Repeat _repeat = Repeat.Off;
    private bool _shuffle, _muted;
    private float _rate = 1f;
    private readonly Random _random = new();
    private readonly DispatcherTimer _ui = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer _click = new() { Interval = TimeSpan.FromMilliseconds(260) };
    private readonly DispatcherTimer _osdClear = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _settingSlider, _pointerOnSlider;
    private double _pendingResume;
    private DateTime _lastSaved = DateTime.UtcNow;
    private bool _fullscreen, _minimal;
    private FullscreenBar? _bar;
    private Rect _restoreBounds;
    private WindowStyle _restoreStyle;
    private ResizeMode _restoreResize;
    private bool _restoreTopmost, _restoreMaximized;
    private string _aspect = "";
    private float _zoom;
    private static readonly float[] Speeds = { 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f, 3f, 4f };
    private static readonly (string Name, string? Value)[] Aspects =
    {
        ("Default", null), ("16:9", "16:9"), ("4:3", "4:3"), ("1:1", "1:1"), ("16:10", "16:10"), ("2.21:1", "221:100"), ("2.35:1", "235:100"), ("2.39:1", "239:100"), ("5:4", "5:4"),
    };
    private static readonly (string Name, float Value)[] Zooms =
    {
        ("Fit to window", 0f), ("Quarter (1:4)", 0.25f), ("Half (1:2)", 0.5f), ("Original (1:1)", 1f), ("Double (2:1)", 2f),
    };

    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int StrCmpLogicalW(string a, string b);

    public PlayerWindow()
    {
        InitializeComponent();
        WindowTheme.DarkTitleBar(this);
        WindowTheme.OwnTaskbarButton(this, "Utylix.Player");
        Width = Math.Clamp(_state.Width, 640, SystemParameters.WorkArea.Width);
        Height = Math.Clamp(_state.Height, 420, SystemParameters.WorkArea.Height);
        PlaylistBox.ItemsSource = _list;
        VolumeSlider.Value = Math.Clamp(_state.Volume, 0, 150);
        BuildStaticMenus();
        RefreshRepeatUi();

        PreviewKeyDown += OnKeyDown;
        Drop += OnDrop;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) => { _main = null; AnyClosed?.Invoke(); };
        StateChanged += (_, _) => { if (_fullscreen && WindowState == WindowState.Minimized) ExitFullscreen(); };

        _ui.Tick += (_, _) => UpdateUi();
        _hide.Tick += (_, _) => HideBar();
        _click.Tick += (_, _) => { _click.Stop(); TogglePause(); };                // a single click pauses, but not when it was the first half of a double click
        _osdClear.Tick += (_, _) => { _osdClear.Stop(); StatusText.Text = ""; };
        Video.Clicked += () => { _click.Stop(); _click.Start(); };
        Video.DoubleClicked += () => { _click.Stop(); ToggleFullscreen(); };
        Video.RightClicked += ShowContextMenu;
        Video.Moved += () => ShowBar(fromMouse: true);
        Video.Wheel += dir => ChangeVolume(dir * 5);
        MouseMove += (_, _) => ShowBar(fromMouse: true);
        SeekSlider.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, _) => _pointerOnSlider = true), true);
        SeekSlider.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(async (_, _) => { await System.Threading.Tasks.Task.Delay(350); _pointerOnSlider = false; }), true);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartThread();
        _ui.Start();
        UpdateUi();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveProgress();
        if (_fullscreen) ExitFullscreen();
        if (WindowState == WindowState.Normal) { _state.Width = ActualWidth; _state.Height = ActualHeight; }
        _state.Volume = (int)VolumeSlider.Value;
        _state.Save();
        _ui.Stop(); _hide.Stop();
        SetThreadExecutionState(0x80000000);
        _threadStop = true;                                                          // the player thread stops and disposes the player by itself
        _bar?.Close();
    }

    // ---------- opening ----------
    private static IEnumerable<string> PlayableIn(string folder) =>
        Directory.EnumerateFiles(folder).Where(PlayerMedia.IsPlayable).OrderBy(f => f, Comparer<string>.Create(StrCmpLogicalW));

    /// <summary>Replace the playlist and play the first item. One file: the other media files of its folder follow it in the playlist.</summary>
    public void PlayFiles(IEnumerable<string> paths)
    {
        var files = ExpandPaths(paths).ToList();
        if (files.Count == 0) return;
        int start = 0;
        _list.Clear();
        if (files.Count == 1 && !IsUrl(files[0]))
        {
            string dir = Path.GetDirectoryName(files[0]) ?? "";
            var siblings = Directory.Exists(dir) ? PlayableIn(dir).ToList() : new List<string>();
            if (siblings.Count > 1 && siblings.Count <= 2000)
            {
                start = Math.Max(0, siblings.FindIndex(f => string.Equals(f, files[0], StringComparison.OrdinalIgnoreCase)));
                files = siblings;
            }
        }
        foreach (var f in files) _list.Add(MakeItem(f));
        PlayIndex(start);
    }

    private void Enqueue(IEnumerable<string> paths)
    {
        int before = _list.Count;
        foreach (var f in ExpandPaths(paths)) _list.Add(MakeItem(f));
        if (_list.Count > before && (_index < 0 || _snap.State is VLCState.Stopped or VLCState.Ended or VLCState.NothingSpecial or VLCState.Error)) PlayIndex(before);
    }

    private static bool IsUrl(string s) => s.Contains("://", StringComparison.Ordinal);

    private static PlaylistItem MakeItem(string path) => new()
    {
        Path = path, IsUrl = IsUrl(path),
        Title = IsUrl(path) ? path : System.IO.Path.GetFileNameWithoutExtension(path),
    };

    /// <summary>Files stay files, folders become the media files inside them.</summary>
    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (IsUrl(p)) yield return p;
            else if (Directory.Exists(p)) { foreach (var f in PlayableIn(p)) yield return f; }
            else if (File.Exists(p)) yield return p;
        }
    }

    private void PlayIndex(int index)
    {
        if (_engineFailed || index < 0 || index >= _list.Count) return;
        SaveProgress();
        foreach (var i in _list) i.IsCurrent = false;
        _index = index;
        var item = _list[index];
        item.IsCurrent = true;
        PlaylistBox.ScrollIntoView(item);

        // like VLC: a subtitle file with the same name next to the video is switched on by itself
        var subs = !item.IsUrl && PlayerMedia.IsVideo(item.Path) ? PlayerMedia.FindSubtitles(item.Path) : new List<(string File, uint Priority)>();
        string path = item.Path;
        bool isUrl = item.IsUrl;
        var aspect = _aspect.Length == 0 ? null : _aspect;
        float zoom = _zoom;
        Post(mp =>
        {
            try
            {
                using var media = isUrl ? new Media(_vlc!, new Uri(path)) : new Media(_vlc!, path, FromType.FromPath);
                foreach (var (file, priority) in subs) media.AddSlave(MediaSlaveType.Subtitle, priority, new Uri(file).AbsoluteUri);
                mp.Play(media);
                mp.SetRate(1f);
                mp.AspectRatio = aspect;
                mp.Scale = zoom;
            }
            catch (Exception ex) { Dispatcher.BeginInvoke(new Action(() => Say("Can't open this: " + ex.Message))); }
        });
        _rate = 1f;
        SpeedBtn.Content = "1.00×";

        bool audioOnly = !item.IsUrl && PlayerMedia.IsAudio(item.Path);
        Video.Visibility = audioOnly ? Visibility.Hidden : Visibility.Visible;
        IdlePanel.Visibility = audioOnly ? Visibility.Visible : Visibility.Collapsed;
        if (audioOnly) IdleText.Text = item.Title;
        Title = item.Title + " - Utylix Player";
        _pendingResume = item.IsUrl ? 0 : _state.ResumeAt(item.Path);
        if (!item.IsUrl) _state.Played(item.Path);
        if (subs.Count > 0) Say("Subtitles: " + Path.GetFileName(subs[0].File), 8);
    }

    // ---------- playback ----------
    private bool Playing => _snap.Playing;
    private bool HasMedia => _index >= 0 && _snap.State is VLCState.Playing or VLCState.Paused or VLCState.Buffering or VLCState.Opening;

    private void TogglePause()
    {
        if (_snap.State is VLCState.Stopped or VLCState.Ended or VLCState.NothingSpecial or VLCState.Error)
        {
            if (_list.Count > 0) PlayIndex(_index >= 0 ? _index : 0);
            else OpenFile_Click(this, new RoutedEventArgs());
            return;
        }
        bool wasPlaying = Playing;
        Post(mp => mp.Pause());
        _snap = _snap with { Playing = !wasPlaying };
        Osd(wasPlaying ? "Pause" : "Play");
    }

    private void StopPlayback()
    {
        SaveProgress();
        Post(mp => mp.Stop());
        Video.Visibility = Visibility.Hidden;
        IdlePanel.Visibility = Visibility.Visible;
        IdleText.Text = "Drop a video or music file here, or press Ctrl+O";
        Title = "Utylix Player";
        foreach (var i in _list) i.IsCurrent = false;
        _snap = Snap.Empty;
        UpdateUi();
    }

    private void OnEnded()
    {
        if (_index >= 0 && _index < _list.Count && !_list[_index].IsUrl) { _state.Positions.Remove(_list[_index].Path); _state.Save(); }
        if (_repeat == Repeat.One) { PlayIndex(_index); return; }
        int next = NextIndex();
        if (next >= 0) PlayIndex(next);
        else StopPlayback();
    }

    private void OnError()
    {
        string name = _index >= 0 && _index < _list.Count ? _list[_index].Title : "this file";
        Say($"Can't play \"{name}\".");
        int next = NextIndex();
        if (next >= 0 && next != _index && _list.Count > 1) PlayIndex(next);
    }

    /// <summary>The item after the current one (random, or in order, or from the top when the playlist repeats); -1 at the end.</summary>
    private int NextIndex()
    {
        if (_list.Count == 0) return -1;
        if (_shuffle && _list.Count > 1)
        {
            int n;
            do n = _random.Next(_list.Count); while (n == _index);
            return n;
        }
        if (_index + 1 < _list.Count) return _index + 1;
        return _repeat == Repeat.All ? 0 : -1;
    }

    private void Next() { int n = NextIndex(); if (n >= 0) PlayIndex(n); else Say("That was the last one."); }

    private DateTime _lastPrevious = DateTime.MinValue;

    /// <summary>Like a CD player: a first press restarts the file, a second press (or one within the first 3 s) goes to the one before.</summary>
    private void Previous()
    {
        bool again = (DateTime.UtcNow - _lastPrevious).TotalSeconds < 2.5;
        _lastPrevious = DateTime.UtcNow;
        if (_index > 0 && (again || _snap.Time <= 3000)) { _lastPrevious = DateTime.MinValue; PlayIndex(_index - 1); return; }
        Post(mp => mp.Time = 0);
    }

    private void Jump(int seconds)
    {
        var snap = _snap;
        if (!snap.Seekable) return;
        long target = Math.Clamp(snap.Time + seconds * 1000L, 0, Math.Max(0, snap.Length - 500));
        Post(mp => mp.Time = target);
        _snap = snap with { Time = target };                                          // (shows at once, the player thread confirms it a moment later)
        Osd($"{(seconds > 0 ? "+" : "−")}{Math.Abs(seconds)} s   {Fmt(target)}");
    }

    private void ChangeVolume(int delta)
    {
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 150);
        Osd($"Volume {(int)VolumeSlider.Value}%");
    }

    private void SetSpeed(float rate)
    {
        rate = Math.Clamp(rate, 0.25f, 4f);
        _rate = rate;
        Post(mp => mp.SetRate(rate));
        SpeedBtn.Content = rate.ToString("0.00") + "×";
        Osd($"Speed {rate:0.##}×");
    }

    private void StepSpeed(int direction)
    {
        int index = Array.FindIndex(Speeds, s => Math.Abs(s - _rate) < 0.01f);
        if (index < 0) index = direction > 0 ? Array.FindLastIndex(Speeds, s => s < _rate) : Array.FindIndex(Speeds, s => s > _rate) + 1;
        SetSpeed(Speeds[Math.Clamp(index + direction, 0, Speeds.Length - 1)]);
    }

    private void SaveProgress()
    {
        try
        {
            if (_index < 0 || _index >= _list.Count || _list[_index].IsUrl) return;
            var snap = _snap;
            if (snap.Length <= 0 || snap.Time < 0) return;
            _state.Remember(_list[_index].Path, snap.Time / 1000.0, snap.Length / 1000.0);
            _state.Save();
            _lastSaved = DateTime.UtcNow;
        }
        catch (Exception) { /* the player is going away */ }
    }

    // ---------- keeping the window in step ----------
    private static string Fmt(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private void UpdateUi()
    {
        var snap = _snap;
        bool has = HasMedia;
        PlayBtn.Content = snap.Playing ? "" : "";
        StopBtn.IsEnabled = has;
        SeekSlider.IsEnabled = has && snap.Seekable;

        long length = has ? snap.Length : 0, time = has ? snap.Time : 0;
        if (_pendingResume > 0 && has && length > 0 && snap.Seekable && snap.State == VLCState.Playing)
        {
            long at = (long)(_pendingResume * 1000);
            Post(mp => mp.Time = at);
            Osd("Continuing from " + Fmt(at));
            _pendingResume = 0;
        }
        TimeText.Text = Fmt(time);
        LengthText.Text = length > 0 ? Fmt(length) : has ? "live" : "0:00";
        if (!_pointerOnSlider)
        {
            _settingSlider = true;
            SeekSlider.Value = length > 0 ? Math.Clamp(time * 1000.0 / length, 0, 1000) : 0;
            _settingSlider = false;
        }
        bool video = has && Video.Visibility == Visibility.Visible;
        SetThreadExecutionState(snap.Playing && video ? 0x80000003u : 0x80000000u);                // no sleep and no screen saver while a video plays
        if ((DateTime.UtcNow - _lastSaved).TotalSeconds > 15 && has) SaveProgress();
    }

    private void Osd(string text)
    {
        Post(mp => mp.SetMarqueeString(VideoMarqueeOption.Text, text));
        Say(text);
    }

    private void Say(string text, double seconds = 3)
    {
        StatusText.Text = text;
        _osdClear.Stop();
        _osdClear.Interval = TimeSpan.FromSeconds(seconds);
        _osdClear.Start();
    }

    // ---------- seek bar, volume ----------
    private void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var snap = _snap;
        if (_settingSlider || !snap.Seekable || snap.Length <= 0) return;
        long target = (long)(SeekSlider.Value / 1000.0 * snap.Length);
        Post(mp => mp.Time = target);
        TimeText.Text = Fmt(target);
    }

    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeText == null) return;
        int v = (int)Math.Round(VolumeSlider.Value);
        VolumeText.Text = v + "%";
        Post(mp => mp.Volume = v);
        if (v > 0 && _muted) SetMute(false, announce: false);
        MuteBtn.Content = v == 0 || _muted ? "" : "";
    }

    private void SetMute(bool mute, bool announce = true)
    {
        _muted = mute;
        Post(mp => mp.Mute = mute);
        MuteItem.IsChecked = mute;
        MuteBtn.Content = mute ? "" : "";
        if (announce) Osd(mute ? "Muted" : $"Volume {(int)VolumeSlider.Value}%");
    }

    // ---------- buttons and menu items ----------
    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePause();
    private void Stop_Click(object sender, RoutedEventArgs e) => StopPlayback();
    private void Prev_Click(object sender, RoutedEventArgs e) => Previous();
    private void Next_Click(object sender, RoutedEventArgs e) => Next();
    private void Forward_Click(object sender, RoutedEventArgs e) => Jump(10);
    private void Back_Click(object sender, RoutedEventArgs e) => Jump(-10);
    private void Forward1_Click(object sender, RoutedEventArgs e) => Jump(60);
    private void Back1_Click(object sender, RoutedEventArgs e) => Jump(-60);
    private void Frame_Click(object sender, RoutedEventArgs e) => Post(mp => { if (mp.IsPlaying) mp.Pause(); mp.NextFrame(); });
    private void VolUp_Click(object sender, RoutedEventArgs e) => ChangeVolume(5);
    private void VolDown_Click(object sender, RoutedEventArgs e) => ChangeVolume(-5);
    private void Mute_Click(object sender, RoutedEventArgs e) => SetMute(!_muted);
    private void SpeedReset_Click(object sender, RoutedEventArgs e) => SetSpeed(1f);
    private void Quit_Click(object sender, RoutedEventArgs e) => Close();
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void Playlist_Click(object sender, RoutedEventArgs e) => TogglePlaylist();
    private void Minimal_Click(object sender, RoutedEventArgs e) => ToggleMinimal();
    private void OnTop_Click(object sender, RoutedEventArgs e) { Topmost = OnTopItem.IsChecked; Osd(Topmost ? "Always on top" : "Not on top"); }

    private void Repeat_Click(object sender, RoutedEventArgs e) { _repeat = _repeat switch { Repeat.Off => Repeat.All, Repeat.All => Repeat.One, _ => Repeat.Off }; RefreshRepeatUi(); Osd("Repeat: " + RepeatName()); }
    private void RepeatOff_Click(object sender, RoutedEventArgs e) { _repeat = Repeat.Off; RefreshRepeatUi(); }
    private void RepeatAll_Click(object sender, RoutedEventArgs e) { _repeat = Repeat.All; RefreshRepeatUi(); }
    private void RepeatOne_Click(object sender, RoutedEventArgs e) { _repeat = Repeat.One; RefreshRepeatUi(); }
    private string RepeatName() => _repeat switch { Repeat.All => "playlist", Repeat.One => "current file", _ => "off" };

    private void RefreshRepeatUi()
    {
        RepeatBtn.Content = _repeat == Repeat.One ? "" : "";
        RepeatBtn.ToolTip = "Repeat: " + RepeatName();
        RepeatBtn.Foreground = (Brush)FindResource(_repeat == Repeat.Off ? "MutedBrush" : "AccentBrush");
        RepeatOff.IsChecked = _repeat == Repeat.Off;
        RepeatAll.IsChecked = _repeat == Repeat.All;
        RepeatOne.IsChecked = _repeat == Repeat.One;
    }

    private void Shuffle_Click(object sender, RoutedEventArgs e) { _shuffle = ShuffleBtn.IsChecked == true; ShuffleItem.IsChecked = _shuffle; Osd("Random: " + (_shuffle ? "on" : "off")); }
    private void ShuffleItem_Click(object sender, RoutedEventArgs e) { _shuffle = ShuffleItem.IsChecked; ShuffleBtn.IsChecked = _shuffle; Osd("Random: " + (_shuffle ? "on" : "off")); }

    // ---------- open dialogs ----------
    private static string MediaFilter =>
        "Video and music|" + string.Join(";", PlayerMedia.VideoExtensions.Concat(PlayerMedia.AudioExtensions).Select(e => "*." + e)) + "|All files|*.*";

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Open a video or music file", Filter = MediaFilter };
        if (dlg.ShowDialog(this) == true) PlayFiles(new[] { dlg.FileName });
    }

    private void OpenMany_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Open files", Filter = MediaFilter, Multiselect = true };
        if (dlg.ShowDialog(this) == true) PlayFiles(dlg.FileNames);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Play a folder" };
        if (dlg.ShowDialog(this) != true) return;
        var files = PlayableIn(dlg.FolderName).ToList();
        if (files.Count == 0) { Say("There are no video or music files in that folder."); return; }
        _list.Clear();
        foreach (var f in files) _list.Add(MakeItem(f));
        PlayIndex(0);
    }

    private void OpenNetwork_Click(object sender, RoutedEventArgs e)
    {
        var url = TextPrompt.Ask(this, "Open network stream", "Address of a video or radio stream (http, https, rtsp, mms …):", "");
        if (string.IsNullOrWhiteSpace(url)) return;
        url = url.Trim();
        if (!IsUrl(url)) url = "http://" + url;
        PlayFiles(new[] { url });
    }

    private void AddToList_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Add to the playlist", Filter = MediaFilter, Multiselect = true };
        if (dlg.ShowDialog(this) == true) Enqueue(dlg.FileNames);
    }

    private void RemoveFromList_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in PlaylistBox.SelectedItems.Cast<PlaylistItem>().ToList())
        {
            int i = _list.IndexOf(item);
            if (i < 0) continue;
            _list.RemoveAt(i);
            if (i < _index) _index--;
            else if (i == _index) { _index = -1; StopPlayback(); }
        }
    }

    private void ClearList_Click(object sender, RoutedEventArgs e) { _list.Clear(); _index = -1; StopPlayback(); }

    private void PlaylistBox_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PlaylistBox.SelectedItem is PlaylistItem item) PlayIndex(_list.IndexOf(item));
    }

    private void PlaylistBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && PlaylistBox.SelectedItem is PlaylistItem item) { PlayIndex(_list.IndexOf(item)); e.Handled = true; }
        else if (e.Key == Key.Delete) { RemoveFromList_Click(sender, e); e.Handled = true; }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) Enqueue(paths); else PlayFiles(paths);
        e.Handled = true;
    }

    // ---------- menus that depend on what is playing ----------
    private void BuildStaticMenus()
    {
        foreach (var s in Speeds)
        {
            float speed = s;
            var item = new MenuItem { Header = speed == 1f ? "Normal (1.00×)" : $"{speed:0.##}×", Tag = speed };
            item.Click += (_, _) => SetSpeed(speed);
            SpeedMenu.Items.Add(item);
        }
        SpeedMenu.Items.Add(new Separator());
        var faster = new MenuItem { Header = "Faster", InputGestureText = "]" }; faster.Click += (_, _) => StepSpeed(1);
        var slower = new MenuItem { Header = "Slower", InputGestureText = "[" }; slower.Click += (_, _) => StepSpeed(-1);
        SpeedMenu.Items.Add(faster); SpeedMenu.Items.Add(slower);

        foreach (var (name, value) in Aspects)
        {
            var item = new MenuItem { Header = name, Tag = value ?? "" };
            item.Click += (_, _) => SetAspect(value);
            AspectMenu.Items.Add(item);
        }
        foreach (var (name, value) in Zooms)
        {
            var item = new MenuItem { Header = name, Tag = value };
            item.Click += (_, _) => SetZoom(value);
            ZoomMenu.Items.Add(item);
        }
    }

    private void Menu_Opened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not MenuItem source || source.Role != MenuItemRole.TopLevelHeader) return;
        var snap = _snap;
        PlayPauseItem.Header = snap.Playing ? "_Pause" : "_Play";
        foreach (MenuItem m in SpeedMenu.Items.OfType<MenuItem>().Where(i => i.Tag is float)) m.IsChecked = Math.Abs((float)m.Tag - _rate) < 0.01f;
        foreach (MenuItem m in AspectMenu.Items.OfType<MenuItem>()) m.IsChecked = (string)m.Tag == _aspect;
        foreach (MenuItem m in ZoomMenu.Items.OfType<MenuItem>()) m.IsChecked = Math.Abs((float)m.Tag - _zoom) < 0.001f;
        FullscreenItem.Header = _fullscreen ? "Leave _Fullscreen" : "_Fullscreen";
        MuteItem.IsChecked = _muted;
        BuildTrackMenu(AudioTrackMenu, snap.Audio, snap.AudioTrack, id => Post(mp => mp.SetAudioTrack(id)), "There are no audio tracks");
        BuildTrackMenu(SubTrackMenu, snap.Subs, snap.Spu, id => Post(mp => mp.SetSpu(id)), "There are no subtitles (use Add Subtitle File…)");
        BuildRecentMenu();
    }

    private void BuildTrackMenu(MenuItem menu, TrackDescription[] tracks, int current, Action<int> set, string emptyText)
    {
        menu.Items.Clear();
        if (tracks.Length == 0) { menu.Items.Add(new MenuItem { Header = emptyText, IsEnabled = false }); return; }
        foreach (var t in tracks)
        {
            int id = t.Id;
            string name = id == -1 ? "Disable" : string.IsNullOrWhiteSpace(t.Name) ? "Track " + id : t.Name;
            var item = new MenuItem { Header = name.Replace("_", "__"), IsChecked = id == current };
            item.Click += (_, _) => { set(id); Osd(name); };
            menu.Items.Add(item);
        }
    }

    private void BuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        var existing = _state.Recent.Where(File.Exists).Take(12).ToList();
        if (existing.Count == 0) { RecentMenu.Items.Add(new MenuItem { Header = "(nothing yet)", IsEnabled = false }); return; }
        foreach (var path in existing)
        {
            string p = path;
            var item = new MenuItem { Header = Path.GetFileName(p).Replace("_", "__") };
            item.Click += (_, _) => PlayFiles(new[] { p });
            RecentMenu.Items.Add(item);
        }
        RecentMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "Clear the list" };
        clear.Click += (_, _) => { _state.Recent.Clear(); _state.Save(); };
        RecentMenu.Items.Add(clear);
    }

    private void SetAspect(string? value)
    {
        _aspect = value ?? "";
        Post(mp => mp.AspectRatio = value);
        Osd("Aspect ratio: " + Aspects.First(a => (a.Value ?? "") == _aspect).Name);
    }

    private void SetZoom(float value)
    {
        _zoom = value;
        Post(mp => mp.Scale = value);
        Osd("Zoom: " + Zooms.First(z => Math.Abs(z.Value - value) < 0.001f).Name);
    }

    private void CycleTrack(bool audio)
    {
        var snap = _snap;
        var tracks = audio ? snap.Audio : snap.Subs;
        if (tracks.Length == 0) return;
        int current = audio ? snap.AudioTrack : snap.Spu;
        int i = Array.FindIndex(tracks, t => t.Id == current);
        var next = tracks[(i + 1) % tracks.Length];
        int id = next.Id;
        if (audio) Post(mp => mp.SetAudioTrack(id)); else Post(mp => mp.SetSpu(id));
        Osd((audio ? "Audio: " : "Subtitle: ") + (id == -1 ? "off" : next.Name));
    }

    private void AddSubtitle_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Add a subtitle file", Filter = "Subtitles|*.srt;*.ass;*.ssa;*.sub;*.vtt;*.idx;*.smi;*.txt|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        string uri = new Uri(dlg.FileName).AbsoluteUri;
        Post(mp => mp.AddSlave(MediaSlaveType.Subtitle, uri, true));
        Osd("Subtitles added");
    }

    private void SubDelayUp_Click(object sender, RoutedEventArgs e) => ShiftSubtitles(50);
    private void SubDelayDown_Click(object sender, RoutedEventArgs e) => ShiftSubtitles(-50);
    private void AudioDelayUp_Click(object sender, RoutedEventArgs e) => ShiftAudio(50);
    private void AudioDelayDown_Click(object sender, RoutedEventArgs e) => ShiftAudio(-50);
    private void NextSub_Click(object sender, RoutedEventArgs e) => CycleTrack(false);

    private void ShiftSubtitles(int ms)
    {
        long value = _snap.SpuDelay + ms * 1000L;
        Post(mp => mp.SetSpuDelay(value));
        _snap = _snap with { SpuDelay = value };
        Osd($"Subtitle delay {value / 1000} ms");
    }

    private void ShiftAudio(int ms)
    {
        long value = _snap.AudioDelay + ms * 1000L;
        Post(mp => mp.SetAudioDelay(value));
        _snap = _snap with { AudioDelay = value };
        Osd($"Audio delay {value / 1000} ms");
    }

    private void Snapshot_Click(object sender, RoutedEventArgs e)
    {
        if (!HasMedia || Video.Visibility != Visibility.Visible) { Say("Nothing to take a picture of."); return; }
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Utylix Snapshots");
            Directory.CreateDirectory(dir);
            string name = (_index >= 0 ? Util.Sanitize(_list[_index].Title) : "Snapshot") + " " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".png";
            string path = Path.Combine(dir, name);
            Post(mp =>
            {
                bool ok = mp.TakeSnapshot(0, path, 0, 0);
                Dispatcher.BeginInvoke(new Action(() => { if (ok) Osd("Snapshot saved: " + name); else Say("Couldn't take the snapshot."); }));
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Say("Couldn't save the snapshot: " + ex.Message); }
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            "Space  play / pause\nF or double-click  full screen (Esc leaves)\nLeft / Right  back / forward 10 s  (Shift 3 s, Ctrl 1 min)\nUp / Down or mouse wheel  volume\nM  mute\n" +
            "[  ]  slower / faster    =  normal speed\nN / P  next / previous    S  stop\nE  next frame\nV  next subtitle track    B  next audio track\nG / H  subtitle delay −/+ 50 ms    J / K  audio delay −/+ 50 ms\n" +
            "A  aspect ratio    Shift+S  snapshot\nCtrl+O  open file    Ctrl+F  open folder    Ctrl+N  network stream\nCtrl+L  playlist    Ctrl+H  minimal view    Ctrl+T  always on top    Ctrl+Q  quit",
            "Keyboard shortcuts", MessageBoxButton.OK, MessageBoxImage.Information);

    private void About_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this, "Utylix Player\n\nPlays video and music with libvlc, the engine inside VLC (videolan.org).\nEverything around it is part of Utylix.", "About", MessageBoxButton.OK, MessageBoxImage.Information);

    // ---------- playlist panel, minimal view ----------
    private void TogglePlaylist()
    {
        bool show = PlaylistPanel.Visibility != Visibility.Visible;
        PlaylistPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PlaylistColumn.Width = show ? new GridLength(300) : new GridLength(0);
        PlaylistMenuItem.IsChecked = show;
    }

    private void ToggleMinimal()
    {
        _minimal = !_minimal;
        MinimalItem.IsChecked = _minimal;
        MenuBorder.Visibility = _minimal ? Visibility.Collapsed : Visibility.Visible;
        ControlsBar.Visibility = _minimal ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowContextMenu()
    {
        var menu = new ContextMenu { PlacementTarget = Video };
        MenuItem Add(string header, RoutedEventHandler click, string gesture = "")
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture };
            item.Click += click;
            menu.Items.Add(item);
            return item;
        }
        Add(Playing ? "Pause" : "Play", PlayPause_Click, "Space");
        Add("Stop", Stop_Click, "S");
        menu.Items.Add(new Separator());
        Add(_fullscreen ? "Leave fullscreen" : "Fullscreen", Fullscreen_Click, "F");
        Add("Take snapshot", Snapshot_Click, "Shift+S");
        Add("Playlist", Playlist_Click, "Ctrl+L");
        menu.Items.Add(new Separator());
        Add("Open file…", OpenFile_Click, "Ctrl+O");
        menu.IsOpen = true;
    }

    // ---------- full screen ----------
    private void ToggleFullscreen()
    {
        if (_fullscreen) ExitFullscreen(); else EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        if (_fullscreen) return;
        _restoreMaximized = WindowState == WindowState.Maximized;
        WindowState = WindowState.Normal;
        _restoreBounds = new Rect(Left, Top, ActualWidth, ActualHeight);
        _restoreStyle = WindowStyle; _restoreResize = ResizeMode; _restoreTopmost = Topmost;
        double scale = ScreenGrab.Scale;
        var screen = System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle((int)(Left * scale), (int)(Top * scale), (int)(ActualWidth * scale), (int)(ActualHeight * scale)));
        MenuBorder.Visibility = Visibility.Collapsed;
        PlaylistPanel.Visibility = Visibility.Collapsed;
        PlaylistColumn.Width = new GridLength(0);
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; Topmost = true;
        Left = screen.Bounds.X / scale; Top = screen.Bounds.Y / scale; Width = screen.Bounds.Width / scale; Height = screen.Bounds.Height / scale;
        _fullscreen = true;

        // the controls move into a small window on top of the picture (a window can't draw over the video window), shown while the mouse moves
        ((Panel)ControlsBar.Parent).Children.Remove(ControlsBar);
        ControlsBar.Visibility = Visibility.Visible;
        _bar = new FullscreenBar(this, ControlsBar, new Rect(screen.Bounds.X / scale, screen.Bounds.Y / scale, screen.Bounds.Width / scale, screen.Bounds.Height / scale));
        FullscreenBtn.Content = "";
        ShowBar();
    }

    private void ExitFullscreen()
    {
        if (!_fullscreen) return;
        _fullscreen = false;
        _hide.Stop();
        Video.CursorHidden = false;
        _bar?.Detach();
        _bar?.Close();
        _bar = null;
        ((Grid)Content).Children.Add(ControlsBar);
        Grid.SetRow(ControlsBar, 2);
        ControlsBar.Visibility = _minimal ? Visibility.Collapsed : Visibility.Visible;
        MenuBorder.Visibility = _minimal ? Visibility.Collapsed : Visibility.Visible;
        WindowStyle = _restoreStyle; ResizeMode = _restoreResize; Topmost = _restoreTopmost || OnTopItem.IsChecked;
        Left = _restoreBounds.X; Top = _restoreBounds.Y; Width = _restoreBounds.Width; Height = _restoreBounds.Height;
        if (_restoreMaximized) WindowState = WindowState.Maximized;
        FullscreenBtn.Content = "";
        if (PlaylistMenuItem.IsChecked) { PlaylistPanel.Visibility = Visibility.Visible; PlaylistColumn.Width = new GridLength(300); }
    }

    private System.Drawing.Point _cursorAt;

    /// <summary>
    /// Shows the controls in full screen and restarts the time they stay. "fromMouse": only when the pointer really moved. Windows also
    /// sends a "mouse moved" message when a window appears or goes away under a pointer that is standing still (which the controls' own
    /// window does), and counting that as movement kept the controls on screen for ever.
    /// </summary>
    private void ShowBar(bool fromMouse = false)
    {
        if (!_fullscreen) return;
        if (fromMouse)
        {
            var at = System.Windows.Forms.Cursor.Position;
            if (at == _cursorAt) return;
            _cursorAt = at;
        }
        Video.CursorHidden = false;
        _bar?.Show();
        _hide.Stop();
        _hide.Start();
    }

    private void HideBar()
    {
        _hide.Stop();
        if (!_fullscreen) return;
        if (_bar != null && System.Windows.Input.Mouse.LeftButton == MouseButtonState.Pressed) { _hide.Start(); return; }       // dragging the seek bar or the volume: keep them
        _cursorAt = System.Windows.Forms.Cursor.Position;                              // (the controls go even when the pointer rests on them)
        _bar?.Hide();
        Video.CursorHidden = true;
    }

    // ---------- keyboard ----------
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0, alt = (mods & ModifierKeys.Alt) != 0;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool inList = PlaylistBox.IsKeyboardFocusWithin;
        bool handled = true;
        ShowBar();
        switch (key)
        {
            case Key.Space: TogglePause(); break;
            case Key.F when !ctrl: ToggleFullscreen(); break;
            case Key.Enter when alt: ToggleFullscreen(); break;
            case Key.Escape:
                if (_fullscreen) ExitFullscreen(); else if (_minimal) ToggleMinimal(); else handled = false;
                break;
            case Key.Left: Jump(ctrl ? -60 : shift ? -3 : -10); break;
            case Key.Right: Jump(ctrl ? 60 : shift ? 3 : 10); break;
            case Key.Up when !inList: ChangeVolume(5); break;
            case Key.Down when !inList: ChangeVolume(-5); break;
            case Key.M: SetMute(!_muted); break;
            case Key.S when shift: Snapshot_Click(this, e); break;
            case Key.S: StopPlayback(); break;
            case Key.N when ctrl: OpenNetwork_Click(this, e); break;
            case Key.N: Next(); break;
            case Key.P: Previous(); break;
            case Key.OemCloseBrackets: StepSpeed(1); break;
            case Key.OemOpenBrackets: StepSpeed(-1); break;
            case Key.OemPlus: SetSpeed(1f); break;
            case Key.E: Frame_Click(this, e); break;
            case Key.V: CycleTrack(false); break;
            case Key.B: CycleTrack(true); break;
            case Key.A: { int i = Array.FindIndex(Aspects, a => (a.Value ?? "") == _aspect); SetAspect(Aspects[(i + 1) % Aspects.Length].Value); break; }
            case Key.G: ShiftSubtitles(-50); break;
            case Key.H when ctrl: ToggleMinimal(); break;
            case Key.H: ShiftSubtitles(50); break;
            case Key.J: ShiftAudio(-50); break;
            case Key.K: ShiftAudio(50); break;
            case Key.O when ctrl && shift: OpenMany_Click(this, e); break;
            case Key.O when ctrl: OpenFile_Click(this, e); break;
            case Key.F when ctrl: OpenFolder_Click(this, e); break;
            case Key.L when ctrl: TogglePlaylist(); break;
            case Key.T when ctrl: OnTopItem.IsChecked = !OnTopItem.IsChecked; OnTop_Click(this, e); break;
            case Key.Q when ctrl: Close(); break;
            default: handled = false; break;
        }
        if (handled) e.Handled = true;
    }

    // ---------- the controls' window in full screen ----------
    private sealed class FullscreenBar : Window
    {
        public FullscreenBar(PlayerWindow owner, Border controls, Rect screen)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Owner = owner;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = Math.Min(screen.Width, 1100);
            Left = screen.X + (screen.Width - Width) / 2;
            Background = (Brush)Application.Current.FindResource("CardBrush");
            Content = controls;
            Loaded += (_, _) => Top = screen.Bottom - ActualHeight - 30;
            PreviewKeyDown += (s, e) => owner.OnKeyDown(s, e);
            MouseMove += (_, _) => owner.ShowBar(fromMouse: true);
        }

        /// <summary>Take the controls back out before the window closes (they go back into the player).</summary>
        public void Detach() => Content = null;
    }
}
