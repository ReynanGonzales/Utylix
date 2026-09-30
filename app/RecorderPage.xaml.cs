using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>Screen recorder: the whole screen, an area or a window, with sound, pause, a hotkey and MP4 or GIF output.</summary>
public partial class RecorderPage : UserControl
{
    private enum Mode { Full, Region, Window }

    /// <summary>What was chosen last time (kept in recorder.json, so the tab looks the same tomorrow).</summary>
    private sealed class Prefs
    {
        public string Mode { get; set; } = "full";
        public int Monitor { get; set; } = -1;                  // -1 = all screens
        public int Fps { get; set; } = 30;
        public string Quality { get; set; } = "normal";
        public bool System { get; set; } = true;
        public bool Mic { get; set; }
        public string MicId { get; set; } = "";                 // "" = the default microphone
        public bool Cursor { get; set; } = true;
        public bool Clicks { get; set; }
        public string Format { get; set; } = "mp4";
        public int Countdown { get; set; } = 3;
        public bool Frame { get; set; } = true;                 // red frame + REC tag on screen (kept out of the video)
    }

    private readonly Manager _manager;
    private readonly Recorder _recorder = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Prefs _p = new();
    private bool _loading = true;
    private bool _busy;
    private bool _shellWasVisible;
    private RecorderBar? _bar;
    private ClickEffects? _clicks;
    private RecordingFrame? _frame;
    private OpenWindow? _chosen;                                // Window mode: the window picked in the list
    private bool _updatingList;
    private Int32Rect _area;
    private string? _lastFile;
    private List<(string Id, string Name)> _mics = new();
    private readonly System.Windows.Forms.Screen[] _screens = System.Windows.Forms.Screen.AllScreens;

    private static string PrefsPath => Path.Combine(App.DataDir, "recorder.json");

    public RecorderPage(Manager manager)
    {
        InitializeComponent();
        _manager = manager;
        try { if (File.Exists(PrefsPath)) _p = JsonSerializer.Deserialize<Prefs>(File.ReadAllText(PrefsPath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { _p = new(); }

        AddChip(ModeChips, "Full screen", "mode", () => Set(() => _p.Mode = "full"), _p.Mode == "full");
        AddChip(ModeChips, "Area", "mode", () => Set(() => _p.Mode = "region"), _p.Mode == "region");
        AddChip(ModeChips, "Window", "mode", () => { Set(() => _p.Mode = "window"); RefreshWindows(); }, _p.Mode == "window");

        if (_screens.Length > 1)
        {
            AddChip(MonitorChips, "All screens", "monitor", () => Set(() => _p.Monitor = -1), _p.Monitor < 0 || _p.Monitor >= _screens.Length);
            for (int i = 0; i < _screens.Length; i++)
            {
                int index = i;
                var b = _screens[i].Bounds;
                AddChip(MonitorChips, $"Screen {i + 1}{(_screens[i].Primary ? " (main)" : "")}  {b.Width}×{b.Height}", "monitor", () => Set(() => _p.Monitor = index), _p.Monitor == i);
            }
        }

        _mics = AudioMixer.Microphones();
        AddChip(MicChips, "Default microphone", "mic", () => Set(() => _p.MicId = ""), _p.MicId.Length == 0 || _mics.All(m => m.Id != _p.MicId));
        foreach (var (id, name) in _mics)
        {
            string micId = id;
            AddChip(MicChips, name, "mic", () => Set(() => _p.MicId = micId), _p.MicId == id);
        }

        AddChip(FormatChips, "MP4 video", "format", () => Set(() => _p.Format = "mp4"), _p.Format == "mp4");
        AddChip(FormatChips, "GIF animation", "format", () => Set(() => _p.Format = "gif"), _p.Format == "gif");
        foreach (var (label, key) in new[] { ("Low", "low"), ("Normal", "normal"), ("High", "high") })
            AddChip(QualityChips, label, "quality", () => Set(() => _p.Quality = key), _p.Quality == key);
        foreach (int fps in new[] { 15, 30, 60 })
            AddChip(FpsChips, fps + " fps", "fps", () => Set(() => _p.Fps = fps), _p.Fps == fps);
        foreach (var (label, seconds) in new[] { ("None", 0), ("3 s", 3), ("5 s", 5), ("10 s", 10) })
            AddChip(CountdownChips, label, "countdown", () => Set(() => _p.Countdown = seconds), _p.Countdown == seconds);

        SystemBox.IsChecked = _p.System;
        MicBox.IsChecked = _p.Mic && _mics.Count > 0;
        CursorBox.IsChecked = _p.Cursor;
        ClicksBox.IsChecked = _p.Clicks;
        FrameBox.IsChecked = _p.Frame;
        _loading = false;
        Refresh();

        _timer.Tick += (_, _) => _bar?.Update(_recorder.Elapsed, _recorder.State == Recorder.RecState.Paused, _recorder.State == Recorder.RecState.Finishing);
        _recorder.Faulted += message => Dispatcher.BeginInvoke(new Action(async () =>
        {
            App.Notify("Recording stopped", message, null);
            await StopAsync();
        }));
        IsVisibleChanged += (_, _) => { if (IsVisible) { RefreshHint(); RefreshBanner(); if (CurrentMode == Mode.Window) RefreshWindows(); } };
    }

    private void AddChip(WrapPanel host, string text, string group, Action onChecked, bool isChecked)
    {
        var chip = new RadioButton { Content = text, GroupName = group, Style = (Style)FindResource("ChipButton"), IsChecked = isChecked };
        chip.Checked += (_, _) => onChecked();
        host.Children.Add(chip);
    }

    private void Set(Action change)
    {
        if (_loading) return;
        change();
        Refresh();
        Save();
    }

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _p.System = SystemBox.IsChecked == true;
        _p.Mic = MicBox.IsChecked == true;
        _p.Cursor = CursorBox.IsChecked == true;
        _p.Clicks = ClicksBox.IsChecked == true;
        _p.Frame = FrameBox.IsChecked == true;
        Refresh();
        Save();
    }

    private void Save()
    {
        try { File.WriteAllText(PrefsPath, JsonSerializer.Serialize(_p)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a forgotten choice is not worth an error */ }
    }

    // ---------- what the tab shows ----------
    private Mode CurrentMode => _p.Mode switch { "region" => Mode.Region, "window" => Mode.Window, _ => Mode.Full };

    private void Refresh()
    {
        if (ModeNote == null) return;
        bool gif = _p.Format == "gif";
        MonitorRow.Visibility = CurrentMode == Mode.Full && _screens.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        ModeNote.Text = CurrentMode switch
        {
            Mode.Full => _screens.Length > 1 ? "Everything on the screen you choose." : "Everything on your screen.",
            Mode.Region => "When you start, drag over the part of the screen to record.",
            _ => "Choose the program window to record. Utylix brings it to the front and records the area it covers (the recording stays there if you move the window).",
        };
        WindowRow.Visibility = CurrentMode == Mode.Window ? Visibility.Visible : Visibility.Collapsed;
        SystemBox.IsEnabled = MicBox.IsEnabled = !gif;
        MicRow.Visibility = MicBox.IsChecked == true && !gif ? Visibility.Visible : Visibility.Collapsed;
        MicBox.ToolTip = _mics.Count == 0 ? "No microphone was found" : null;
        MicBox.IsEnabled = !gif && _mics.Count > 0;
        QualityRow.Visibility = gif ? Visibility.Collapsed : Visibility.Visible;
        FormatNote.Text = gif
            ? "An animated picture: no sound, at most 15 pictures a second and up to 800 pixels wide. Best for short clips."
            : _p.Fps == 60 ? "60 fps is smooth for games but makes big files and needs a fast PC." : "";
        FormatNote.Visibility = FormatNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshHint();
        RefreshBanner();
    }

    public void RefreshHint()
    {
        bool on = _manager.Config.RecHotkey;
        ShortcutHint.Text = on ? "Shortcuts:  Ctrl + Alt + R  start / stop   ·   Ctrl + Alt + P  pause" : "Shortcuts are off (Settings → Screen Recorder)";
        EmptyText.Text = "Click Start recording" + (on ? ", or press Ctrl + Alt + R anywhere." : ".") + "\nPress it again to stop (Ctrl + Alt + P pauses). Recordings go to:\n" + OutDir();
    }

    private void RefreshBanner()
    {
        if (FfmpegBanner == null) return;
        FfmpegBanner.Visibility = Tools.HasFfmpeg ? Visibility.Collapsed : Visibility.Visible;
        InstallBtn.IsEnabled = true;
    }

    private async void InstallFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        InstallBtn.IsEnabled = false;
        try
        {
            await Tools.InstallFfmpegAsync(s => Dispatcher.Invoke(() => FfmpegText.Text = s), CancellationToken.None);
            RefreshBanner();
        }
        catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or InvalidOperationException or UnauthorizedAccessException)
        {
            FfmpegText.Text = "Couldn't install ffmpeg: " + ex.Message;
            InstallBtn.IsEnabled = true;
        }
    }

    // ---------- choosing a window ----------
    private void RefreshWindows_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    /// <summary>List the open windows (keeping the choice if that window is still open).</summary>
    private void RefreshWindows()
    {
        if (WindowsBox == null || _recorder.State != Recorder.RecState.Idle) return;
        var windows = WindowList.Enumerate();
        _updatingList = true;
        WindowsBox.ItemsSource = windows;
        var keep = _chosen == null ? null : windows.FirstOrDefault(w => w.Handle == _chosen.Handle);
        WindowsBox.SelectedItem = keep;
        _updatingList = false;
        _chosen = keep;
        WindowNote.Text = windows.Count == 0 ? "No open windows found." : keep == null ? "Click the window you want to record." : "";
    }

    private void Window_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingList) return;
        _chosen = WindowsBox.SelectedItem as OpenWindow;
        WindowNote.Text = "";
    }

    private string OutDir()
    {
        string dir = _manager.Config.RecDir;
        try { if (!string.IsNullOrWhiteSpace(dir)) return Path.GetFullPath(Environment.ExpandEnvironmentVariables(dir)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { /* fall back to the default */ }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Utylix");
    }

    // ---------- recording ----------
    private void Start_Click(object sender, RoutedEventArgs e) => _ = StartAsync();

    /// <summary>The shortcut and the tray menu: start when idle, stop when recording.</summary>
    public Task ToggleAsync() => _recorder.State is Recorder.RecState.Recording or Recorder.RecState.Paused ? StopAsync() : StartAsync();

    public bool IsRecording => _recorder.State is Recorder.RecState.Recording or Recorder.RecState.Paused or Recorder.RecState.Finishing;

    /// <summary>Choose what to record (if needed), count down, and start. Utylix hides itself so it is not in the video.</summary>
    public async Task StartAsync()
    {
        if (_busy || _recorder.State != Recorder.RecState.Idle) return;
        if (!Tools.HasFfmpeg) { App.Show("recorder"); RefreshBanner(); return; }
        if (CurrentMode == Mode.Window && (_chosen == null || !WindowList.Exists(_chosen.Handle) && !_chosen.Minimized))
        {
            App.Show("recorder");                                           // nothing (valid) chosen: ask, don't guess
            RefreshWindows();
            if (WindowNote != null) WindowNote.Text = "Click the window you want to record first, then Start recording.";
            return;
        }
        _busy = true;
        App.Capturing = true;
        var shell = Window.GetWindow(this);
        _shellWasVisible = shell?.IsVisible == true;
        try
        {
            shell?.Hide();
            await Task.Delay(280);
            var area = CurrentMode == Mode.Window ? new Int32Rect(0, 0, 0, 0) : ChooseArea();
            if (area == null) return;                                       // cancelled
            if (!await CountdownWindow.RunAsync(_p.Countdown)) return;
            if (CurrentMode == Mode.Window)
            {
                var target = _chosen!.Handle;
                WindowList.Show(target);                                    // un-minimize it and bring it to the front: what is on screen there is the window
                await Task.Delay(700);
                area = WindowList.BoundsOf(target) ?? throw new IOException("That window has closed.");
            }

            _area = area.Value;
            string? mic = _p.Format == "mp4" && _p.Mic && _mics.Count > 0 ? _p.MicId : null;
            await _recorder.StartAsync(new RecordOptions(_area, _p.Format == "gif" ? Math.Min(_p.Fps, 15) : _p.Fps, _p.Quality,
                                                         _p.Format == "mp4" && _p.System, mic, _p.Cursor, _p.Format, OutDir()));
            ShowBar();
            if (_recorder.Warning != null) App.Notify("Recording without some sound", _recorder.Warning, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            LogError(ex);
            App.Notify("Couldn't start recording", ex.Message, null);
        }
        catch (Exception ex)
        {
            LogError(ex);
            App.Notify("Couldn't start recording", ex.Message, null);
        }
        finally
        {
            _busy = false;
            if (_recorder.State == Recorder.RecState.Idle) Restore();      // cancelled or failed: back to how it was
        }
    }

    private Int32Rect? ChooseArea()
    {
        if (CurrentMode == Mode.Full)
        {
            if (_p.Monitor < 0 || _p.Monitor >= _screens.Length) return ScreenGrab.VirtualScreen;
            var b = _screens[_p.Monitor].Bounds;
            return new Int32Rect(b.X, b.Y, b.Width, b.Height);
        }
        var shot = ScreenGrab.CaptureVirtualScreen(out var all);
        var overlay = new CaptureOverlay(shot, all, CurrentMode == Mode.Window ? CaptureOverlay.Kind.Window : CaptureOverlay.Kind.Rectangle, "record");
        overlay.ShowDialog();
        return overlay.SelectedArea;
    }

    private void ShowBar()
    {
        _bar = new RecorderBar();
        _bar.PauseClicked += () => _ = TogglePauseAsync();
        _bar.StopClicked += () => _ = StopAsync();
        _bar.SetArea(_area);
        _bar.Show();
        if (_p.Clicks) _clicks = new ClickEffects(_area);
        if (_p.Frame) { _frame = new RecordingFrame(_area); _frame.Show(); }
        _timer.Start();
        App.SetRecordingState(true, this);
    }

    /// <summary>Pause when recording, continue when paused (the bar's button and Ctrl + Alt + P).</summary>
    public async Task TogglePauseAsync()
    {
        try
        {
            if (_recorder.State == Recorder.RecState.Recording)
            {
                _clicks?.Dispose(); _clicks = null;
                _frame?.SetPaused(true);
                await _recorder.PauseAsync();
            }
            else if (_recorder.State == Recorder.RecState.Paused)
            {
                await _recorder.ResumeAsync();
                _frame?.SetPaused(false);
                if (_p.Clicks) _clicks = new ClickEffects(_area);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            App.Notify("Couldn't continue recording", ex.Message, null);
            await StopAsync();
        }
        _bar?.Update(_recorder.Elapsed, _recorder.State == Recorder.RecState.Paused, false);
    }

    public async Task StopAsync()
    {
        if (_recorder.State is Recorder.RecState.Idle or Recorder.RecState.Finishing) return;
        _timer.Stop();
        _clicks?.Dispose(); _clicks = null;
        var length = _recorder.Elapsed;
        _bar?.Update(length, false, busy: true);
        try
        {
            string path = await _recorder.StopAsync();
            ShowLast(path, length);
            App.Notify("Recording saved", Path.GetFileName(path), path);
        }
        catch (Exception ex)
        {
            LogError(ex);
            App.Notify("Recording failed", ex.Message, null);
        }
        finally
        {
            _bar?.Close(); _bar = null;
            _frame?.Close(); _frame = null;
            App.SetRecordingState(false, this);
            Restore();
        }
    }

    /// <summary>Utylix comes back only if it was open when the recording began (started from the tray or a shortcut, it stays away).</summary>
    private void Restore()
    {
        App.Capturing = false;
        if (_shellWasVisible) App.Show("recorder");
    }

    /// <summary>Utylix is closing: finish what is being recorded so nothing is lost.</summary>
    public void StopForExit() => _recorder.StopForExit();

    private static void LogError(Exception ex)
    {
        try { File.AppendAllText(Path.Combine(App.DataDir, "error.log"), $"{DateTime.Now:s} recorder: {ex}{Environment.NewLine}"); } catch (Exception) { }
    }

    // ---------- the last recording ----------
    private void ShowLast(string path, TimeSpan length)
    {
        _lastFile = path;
        long size = 0;
        try { size = new FileInfo(path).Length; } catch (IOException) { }
        LastName.Text = Path.GetFileName(path);
        LastInfo.Text = (length.TotalHours >= 1 ? length.ToString(@"h\:mm\:ss") : length.ToString(@"m\:ss")) + "   ·   " + Format.Bytes(size);
        LastPanel.Visibility = Visibility.Visible;
        EmptyHint.Visibility = Visibility.Collapsed;
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFile == null || !File.Exists(_lastFile)) { App.Notify("File not found", "That recording was moved or deleted.", null); return; }
        try { Process.Start(new ProcessStartInfo(_lastFile) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { App.Notify("Couldn't open it", ex.Message, null); }
    }

    /// <summary>Move the last recording to the Recycle Bin (it can be put back from there).</summary>
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFile == null) return;
        try
        {
            if (File.Exists(_lastFile))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(_lastFile, Microsoft.VisualBasic.FileIO.UIOption.AllDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            App.Notify("Couldn't delete it", ex.Message, null);
            return;
        }
        if (File.Exists(_lastFile)) return;                                 // Windows asked and the answer was no
        _lastFile = null;
        LastPanel.Visibility = Visibility.Collapsed;
        EmptyHint.Visibility = Visibility.Visible;
        RefreshHint();
        EmptyText.Text = "Recording moved to the Recycle Bin.\n" + EmptyText.Text;
    }

    private void Show_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFile == null) return;
        try
        {
            if (File.Exists(_lastFile)) Process.Start("explorer.exe", $"/select,\"{_lastFile}\"");
            else if (Directory.Exists(Path.GetDirectoryName(_lastFile))) Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(_lastFile)}\"");
        }
        catch (Exception) { /* nothing more to do */ }
    }
}
