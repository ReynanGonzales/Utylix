using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using IdmClone.Engine;
using Microsoft.Win32;

namespace IdmClone;

public partial class App : Application
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Utylix";
    private const string LegacyRunValue = "IDMClone";      // what the app was called before

    private Mutex? _mutex;
    private Manager? _manager;
    private ApiServer? _api;
    private ShellWindow? _shell;       // the one window: tab bar + the selected tool
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _hintShown;
    private bool _exiting;

    public static bool IsDarkTheme { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // an unexpected error must never close Utylix (it may be doing your downloads): show a notice and keep a log
        DispatcherUnhandledException += (_, ev) =>
        {
            LogError(ev.Exception);
            Balloon(6000, "Something went wrong", ev.Exception.Message, System.Windows.Forms.ToolTipIcon.Warning, null);
            ev.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => LogError(ev.ExceptionObject as Exception);

        int port = 6800;
        bool explicitPort = false;                    // --port given (tests): use exactly that one
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dataDir = Path.Combine(appData, "Utylix");
        bool customData = false;
        string? toolsDir = null;
        for (int i = 0; i < e.Args.Length - 1; i++)
        {
            if (e.Args[i] == "--port" && int.TryParse(e.Args[i + 1], out int p)) { port = p; explicitPort = true; }
            if (e.Args[i] == "--data") { dataDir = e.Args[i + 1]; customData = true; }
            if (e.Args[i] == "--tools") toolsDir = e.Args[i + 1];   // lets a test copy share the real yt-dlp/ffmpeg
        }
        // after an update the new copy waits for the old one to finish closing
        int waitIndex = Array.IndexOf(e.Args, "--wait-for-pid");
        if (waitIndex >= 0 && waitIndex + 1 < e.Args.Length && int.TryParse(e.Args[waitIndex + 1], out int waitPid))
        {
            try { System.Diagnostics.Process.GetProcessById(waitPid).WaitForExit(20000); } catch (Exception) { /* already gone */ }
        }
        // tests only: look for updates on a local server, as if this were another version
        if (e.Args.Contains("--no-register"))
        {
            int api = Array.IndexOf(e.Args, "--update-api"), ver = Array.IndexOf(e.Args, "--update-version");
            if (api >= 0 && api + 1 < e.Args.Length && Uri.TryCreate(e.Args[api + 1], UriKind.Absolute, out var apiUri) && apiUri.IsLoopback) AppUpdater.ApiBase = apiUri.ToString().TrimEnd('/');
            if (ver >= 0 && ver + 1 < e.Args.Length) AppUpdater.PretendVersion(e.Args[ver + 1]);
        }
        if (!customData) MigrateLegacyData(Path.Combine(appData, "IDMClone"), dataDir);
        Tools.Dir = toolsDir ?? Path.Combine(dataDir, "tools");
        DataDir = dataDir;

        int fanSuffix = Array.IndexOf(e.Args, "--fan-suffix");
        if (fanSuffix >= 0 && fanSuffix + 1 < e.Args.Length) FanJson.Suffix = new string(e.Args[fanSuffix + 1].Where(char.IsLetterOrDigit).ToArray());
        // the administrator helper of the Fans tab: reads the hardware and sets fans for the normal Utylix, no windows
        // one-time setup / removal of the "start the fan helper without a prompt" task (started with administrator rights, then ends)
        int taskInstall = Array.IndexOf(e.Args, "--fan-task-install");
        if (taskInstall >= 0 && taskInstall + 1 < e.Args.Length) { Shutdown(FanTask.Install(e.Args[taskInstall + 1])); return; }
        if (e.Args.Contains("--fan-task-remove")) { Shutdown(FanTask.Remove()); return; }
        int fanHelper = Array.IndexOf(e.Args, "--fan-helper");
        if (fanHelper >= 0 && fanHelper + 1 < e.Args.Length)
        {
            string owner = e.Args[fanHelper + 1];
            _ = System.Threading.Tasks.Task.Run(() => { int code = FanHelper.Run(owner); Dispatcher.Invoke(() => Shutdown(code)); });
            return;
        }

        // the installed copy removes itself; a setup copy (Utylix-Setup.exe, --setup, first run on a PC) offers to install
        if (e.Args.Contains("--uninstall")) { ApplyTheme(); Installer.Uninstall(); Shutdown(); return; }
        if (Installer.WantsSetup(e.Args))
        {
            ApplyTheme();
            if (!Installer.RunSetup(e.Args)) { Shutdown(); return; }       // installed (or cancelled): done. "Just run it" falls through.
        }

        // Started by the browser on behalf of the extension ("please start Utylix")? Do just that and leave.
        if (NativeHost.IsHostLaunch(e.Args))
        {
            Shutdown(NativeHost.RunHost(e.Args, dataDir));
            return;
        }

        // "Convert" from Explorer's right-click menu: --convert-to jpg "a.png" ...   or   --convert "a.png" ...
        var convert = ParseConvert(e.Args);
        var archiveCmd = ParseArchive(e.Args);       // "Extract here", "Add to ZIP", "Open with Utylix" ...
        var toolCmd = ParseTool(e.Args);             // "Remove background", "Play with Utylix"

        // One copy per Windows user session ("Local" is per session). The port is NOT part of the name: another user signed in at
        // the same time has their own copy, which simply takes the next free port (kept in port.txt in each user's data folder).
        _mutex = new Mutex(true, explicitPort ? $"Local\\Utylix.SingleInstance.{port}" : "Local\\Utylix.SingleInstance", out bool first);
        if (!first)
        {
            int live = ApiPort.Current(dataDir, explicitPort ? port : null);      // where the running copy listens
            if (convert != null) SendConvert(live, convert.Value.Files, convert.Value.Format);
            else if (archiveCmd != null) SendArchive(live, archiveCmd.Value.Op, archiveCmd.Value.Files);
            else if (toolCmd != null) SendTool(live, toolCmd.Value.Op, toolCmd.Value.Files);
            else AskRunningInstanceToShow(live);
            Shutdown();
            return;
        }

        AppUpdater.CleanLeftovers();
        ApplyTheme();
        _manager = new Manager(dataDir);
        _ = System.Threading.Tasks.Task.Run(() => ExtensionFiles.Ensure());           // the browser extension is unpacked next to the settings (and kept current)
        // another Windows user (signed in at the same time) or another program may hold 6800: take the next free port
        int chosen = 0;
        foreach (int candidate in explicitPort ? new[] { port } : Enumerable.Range(port, 31))
        {
            _api = new ApiServer(_manager, candidate, which => Dispatcher.Invoke(() => ShowTab("downloads")),
                d => Dispatcher.BeginInvoke(() => new CaptureWindow(_manager, d).ShowOnTop()),
                (files, format) => Dispatcher.BeginInvoke(() => HandleConvert(files, format)),
                (op, files) => Dispatcher.BeginInvoke(() => HandleArchive(op, files)),
                (op, files) => Dispatcher.BeginInvoke(() => HandleTool(op, files)));
            if (_api.Start()) { chosen = candidate; break; }
        }
        if (chosen == 0)
        {
            MessageBox.Show($"Utylix could not find a free port to listen on (it tried 127.0.0.1:{port} to {(explicitPort ? port : port + 30)}).\nAnother program is probably using them.",
                "Utylix", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
        ApiPort.Publish(dataDir, chosen);             // the browser link and Explorer's menus find this copy through it

        _shell = new ShellWindow(_manager);
        _ = Dispatcher.BeginInvoke(() => _shell.Page<DashboardPage>("home"));            // starts the fan helper by itself when that is set up (no click)
        Recorder.CleanLeftovers();
        _shortcuts = new Shortcuts();
        _shortcuts.Pressed += StartCaptureFromShortcut;
        _shortcuts.RecordPressed += () => { if (_shell != null) _ = _shell.Page<RecorderPage>("recorder").ToggleAsync(); };
        _shortcuts.OpenPressed += OpenUtylix;
        // a hook can be lost while the PC sleeps or is locked: put it in again
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, ev) => { if (ev.Mode == Microsoft.Win32.PowerModes.Resume) _shortcuts?.Refresh(); };
        Microsoft.Win32.SystemEvents.SessionSwitch += (_, _) => _shortcuts?.Refresh();
        _shortcuts.PausePressed += () => { if (_recordingPage != null) _ = _recordingPage.TogglePauseAsync(); };
        ApplyShortcuts();
        CreateTray();
        // clicking the "download complete" notice opens the folder with the file selected
        _manager.Completed += d => Dispatcher.BeginInvoke(() =>
        {
            string? path = d.FinalPathOrNull;
            Balloon(4000, "Download complete", (d.FileName ?? "") + "\nClick to open its folder", System.Windows.Forms.ToolTipIcon.Info, () => Reveal(path));
            AfterDownload(d.AfterDone, path);
        });
        _manager.Notice += (title, text, error) => Dispatcher.BeginInvoke(() =>
            Balloon(7000, title, text, error ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info, null));
        SessionEnding += (_, _) => Quit();

        // let the browser extension start us when we're closed (registered for this user only; --no-register skips it in tests)
        // A copy started from a build folder (bin\Debug, bin\Release: tests, development) must not change the registrations of
        // the installed program (Explorer menus, browser link, Start with Windows) unless it is told to with --register.
        bool devBuild = (Environment.ProcessPath ?? "").Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase) ||
                        (Environment.ProcessPath ?? "").Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase);
        NoRegister = e.Args.Contains("--no-register") || (devBuild && !e.Args.Contains("--register"));
        if (!NoRegister)
        {
            MigrateAutoStart();
            NativeHost.Register(dataDir, _manager.Config.AllowBrowserStart);
            ShellMenu.Register(dataDir, _manager.Config.ExplorerMenu);       // right-click -> Convert on pictures, videos, music
            ShellMenu.RegisterArchive(dataDir, _manager.Config.ExplorerArchiveMenu);   // right-click -> Extract / Add to ZIP
            ShellMenu.RegisterBackground(dataDir, _manager.Config.ExplorerBgMenu);     // right-click -> Remove background
            ShellMenu.RegisterPlayer(dataDir, _manager.Config.ExplorerPlayMenu);       // right-click -> Play with Utylix, and "Open with"
        }
        _quickBackground = new QuickBackground((title, text, error, reveal) => Dispatcher.BeginInvoke(() =>
            Balloon(error ? 7000 : 5000, title, reveal != null ? text + "\nClick to open its folder" : text,
                    error ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info, reveal != null ? () => Reveal(reveal) : null)),
            () => Dispatcher.Invoke(EnsureBackgroundModel));
        _quick = new QuickConverter((title, text, error, reveal) => Dispatcher.BeginInvoke(() =>
            Balloon(error ? 7000 : 4000, title, reveal != null ? text + "\nClick to open its folder" : text,
                    error ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info, reveal != null ? () => Reveal(reveal) : null)));

        // copied a link to a file or a video page? offer to download it
        ClipboardWatch = new ClipboardWatcher(_manager, offer => new ToastWindow(offer, () => AcceptOffer(offer)).Show());
        ClipboardWatch.Start();

        // now and then: is there a newer Utylix? (asks before installing anything)
        new AppUpdateWatcher(_manager, release => Dispatcher.BeginInvoke(() =>
            Balloon(10000, "Utylix " + release.Version.ToString(3) + " is available", "Click to see what's new and update",
                    System.Windows.Forms.ToolTipIcon.Info, () => AppUpdateWindow.ShowWindow(_manager, release))),
            now: e.Args.Contains("--updates-now")).Start();
        if (e.Args.Contains("--updated"))
            Dispatcher.BeginInvoke(() => Balloon(5000, "Utylix updated", "You now have Utylix " + AppUpdater.CurrentText, System.Windows.Forms.ToolTipIcon.Info, null));

        // now and then: is there a newer yt-dlp? (asks before installing anything)
        new YtDlpUpdater(_manager, (latest, installed) => Dispatcher.BeginInvoke(() => new UpdateWindow(latest, installed).ShowOnTop()),
                         now: e.Args.Contains("--updates-now")).Start();

        if (toolCmd != null)                                                               // started by the right-click menu: no main window
        {
            if (toolCmd.Value.Op == "play") { _ephemeral = true; PlayerWindow.AnyClosed += MaybeQuitAfterArchive; }   // a double-clicked video: go away when the player is closed
            HandleTool(toolCmd.Value.Op, toolCmd.Value.Files);
        }
        else if (convert != null) HandleConvert(convert.Value.Files, convert.Value.Format);   // started by the right-click menu: no main window
        else if (archiveCmd != null)
        {
            _ephemeral = archiveCmd.Value.Op is "open" or "add";               // double-clicked a .zip: show it, and go away when it is closed
            ArchiveWindow.AnyClosed += MaybeQuitAfterArchive;
            HandleArchive(archiveCmd.Value.Op, archiveCmd.Value.Files);
        }
        else if (!e.Args.Contains("--minimized")) ShowTab("downloads");
    }

    // ---------- Remove background / Play (Explorer right-click) ----------
    private QuickBackground? _quickBackground;

    private static (string Op, List<string> Files)? ParseTool(string[] args)
    {
        if (args.Contains("--extension-help")) return ("extension", new List<string>());    // shows how to add the browser extension
        if (args.Contains("--snip")) return ("snip", new List<string>());                  // opens the Snip window
        if (args.Contains("--brightness")) return ("brightness", new List<string>());       // opens the brightness panel
        int i = Array.FindIndex(args, a => a is "--remove-bg" or "--play");
        if (i < 0) return null;
        var files = args.Skip(i + 1).Where(a => !a.StartsWith("--", StringComparison.Ordinal) && a.Length > 0).ToList();
        return files.Count == 0 ? null : (args[i] == "--remove-bg" ? "remove-bg" : "play", files);
    }

    private void HandleTool(string op, List<string> files)
    {
        if (op == "remove-bg") _quickBackground?.Enqueue(files.Where(BackgroundRemover.IsPicture));
        else if (op == "play") PlayerWindow.Open(files);
        else if (op == "brightness") BrightnessWindow.ShowPanel();
        else if (op == "update") AppUpdateWindow.ShowWindow(_manager!);
        else if (op == "snip") SnipWindow.Get(_manager!).Open();
        else if (op == "extension") ExtensionFiles.ShowHelp();
    }

    /// <summary>Asks before the one-time download of the AI model; true when the model is there.</summary>
    public static bool EnsureBackgroundModel()
    {
        if (BackgroundRemover.HasModel) return true;
        var window = new ToolDownloadWindow("Background Remover",
            "Background Remover needs an AI model",
            "It is downloaded once (about 170 MB) from the rembg project's GitHub page, checked against its fingerprint, and kept on this PC. " +
            "Everything runs on your computer: your pictures are never uploaded.",
            "Download (170 MB)", BackgroundRemover.InstallModelAsync);
        return window.ShowDialog() == true && BackgroundRemover.HasModel;
    }

    private static void SendTool(int port, string op, List<string> files)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new { op, files });
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                var r = http.PostAsync($"http://127.0.0.1:{port}/api/tool", new StringContent(json, System.Text.Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
                if (r.IsSuccessStatusCode) return;
            }
            catch (Exception) { /* not listening yet */ }
            Thread.Sleep(250);
        }
    }

    // ---------- Image Converter ----------
    private QuickConverter? _quick;
    private QuickArchive? _quickArchive;
    private bool _ephemeral;         // started only to show an archive: quit again when its window closes

    private void MaybeQuitAfterArchive()
    {
        if (!_ephemeral || ArchiveWindow.Count > 0 || PlayerWindow.Count > 0 || (_shell?.IsVisible ?? false) || _manager == null) return;
        if (_manager.All().Any(d => d.Status is DlStatus.Downloading or DlStatus.Queued)) return;      // it is doing something else too
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_ephemeral && ArchiveWindow.Count == 0 && PlayerWindow.Count == 0 && !(_shell?.IsVisible ?? false)) Quit();
        };
        timer.Start();
    }

    // ---------- archives (Explorer right-click, "Open with", drag onto the exe) ----------
    private static (string Op, List<string> Files)? ParseArchive(string[] args)
    {
        int i = Array.FindIndex(args, a => a is "--archive-open" or "--archive-add" or "--extract-here" or "--extract-to" or "--extract-files" or "--zip-add");
        if (i < 0) return null;
        string op = args[i] switch { "--archive-open" => "open", "--archive-add" => "add", "--extract-here" => "extract-here", "--extract-to" => "extract-to", "--extract-files" => "extract-ask", _ => "zip-add" };
        var files = args.Skip(i + 1).Where(a => !a.StartsWith("--", StringComparison.Ordinal) && a.Length > 0).ToList();
        return files.Count == 0 ? null : (op, files);
    }

    // Explorer starts one Utylix process PER selected file; each hands its file over here. Wait until they have all arrived, so the
    // whole selection ends up in ONE archive (the wait restarts with every arrival, and never lasts longer than a few seconds).
    private readonly Dictionary<string, List<string>> _packBatch = new();
    private System.Windows.Threading.DispatcherTimer? _packTimer;
    private DateTime _packFirst;

    private void BatchPack(string op, List<string> files)
    {
        if (!_packBatch.TryGetValue(op, out var list)) _packBatch[op] = list = new List<string>();
        foreach (var f in files) if (!list.Contains(f, StringComparer.OrdinalIgnoreCase)) list.Add(f);
        if (_packTimer == null)
        {
            _packFirst = DateTime.UtcNow;
            _packTimer = new System.Windows.Threading.DispatcherTimer();
            _packTimer.Tick += (_, _) =>
            {
                _packTimer!.Stop();
                var ready = _packBatch.ToList();
                _packBatch.Clear();
                _packTimer = null;
                foreach (var (o, fs) in ready) HandleArchive(o, fs, batched: true);
            };
        }
        _packTimer.Stop();
        _packTimer.Interval = (DateTime.UtcNow - _packFirst).TotalSeconds > 5 ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromMilliseconds(1800);
        _packTimer.Start();
    }

    private void HandleArchive(string op, List<string> files, bool batched = false)
    {
        if (!batched && op is "add" or "zip-add") { BatchPack(op, files); return; }
        if (op == "open") ArchiveWindow.OpenArchive(files[0]);            // its own window, like WinRAR: no main Utylix window
        else if (op == "add") ArchiveWindow.NewArchive(files);
        else if (op == "extract-ask") AskWhereToExtract(files);
        else
        {
            _quickArchive ??= NewQuickArchive();
            _quickArchive.Enqueue(op, files);
        }
    }

    private readonly List<string> _askPending = new();
    private bool _asking;

    /// <summary>"Extract files…": ask for a folder once, then extract every archive of the selection into it.</summary>
    private void AskWhereToExtract(List<string> files)
    {
        _askPending.AddRange(files);
        if (_asking) return;                       // the folder question is already open: these join the same answer
        _asking = true;
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Extract to…", InitialDirectory = Path.GetDirectoryName(files[0]) };
            bool ok = dlg.ShowDialog() == true;
            var chosen = _askPending.ToList();
            _askPending.Clear();
            if (!ok) return;
            _quickArchive ??= NewQuickArchive();
            _quickArchive.Enqueue("extract-to-folder", chosen, dlg.FolderName);
        }
        finally { _asking = false; }
    }

    private QuickArchive NewQuickArchive() => new(
        (title, text, error, reveal) => Dispatcher.BeginInvoke(() =>
            Balloon(error ? 7000 : 5000, title, reveal != null ? text + "\nClick to open its folder" : text,
                    error ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info, reveal != null ? () => Reveal(reveal) : null)),
        file => Dispatcher.BeginInvoke(() => HandleArchive("open", new List<string> { file })));     // needs a password: continue in its window

    private static void SendArchive(int port, string op, List<string> files)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new { op, files });
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                var r = http.PostAsync($"http://127.0.0.1:{port}/api/archive", new StringContent(json, System.Text.Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
                if (r.IsSuccessStatusCode) return;
            }
            catch (Exception) { /* not listening yet */ }
            Thread.Sleep(250);
        }
    }

    private static (List<string> Files, string? Format)? ParseConvert(string[] args)
    {
        int i = Array.FindIndex(args, a => a is "--convert-to" or "--convert");
        if (i < 0) return null;
        string? format = null;
        int start = i + 1;
        if (args[i] == "--convert-to")
        {
            if (i + 1 >= args.Length) return null;
            format = args[i + 1].Trim().TrimStart('.').ToLowerInvariant();
            if (format == "jpeg") format = "jpg";
            if (format == "tif") format = "tiff";
            if (!MediaConverter.IsTarget(format)) return null;
            start = i + 2;
        }
        var files = args.Skip(start).Where(a => !a.StartsWith("--", StringComparison.Ordinal) && a.Length > 0).ToList();
        return files.Count == 0 ? null : (files, format);
    }

    /// <summary>Right-click -> Convert: a format means "just do it in the background", none means "open the converter window".</summary>
    private void HandleConvert(List<string> files, string? format)
    {
        if (format != null) _quick?.Enqueue(files.Where(MediaConverter.IsSupported), format);
        else OpenConverter(files);
    }

    /// <summary>Opens the Image Converter tab, optionally with pictures already in its list.</summary>
    public static void OpenConverter(IEnumerable<string>? files)
    {
        var app = (App)Current;
        app.ShowTab("converter");
        if (files != null && app._shell != null) app._shell.Page<ConverterPage>("converter").AddFiles(files);
    }

    /// <summary>
    /// The copy of the program that Explorer started hands its file to the running one. The running one may still be
    /// starting up (first click after boot), so try for a few seconds.
    /// </summary>
    private static void SendConvert(int port, List<string> files, string? format)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new { files, to = format });
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                var r = http.PostAsync($"http://127.0.0.1:{port}/api/convert", new StringContent(json, System.Text.Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
                if (r.IsSuccessStatusCode) return;
            }
            catch (Exception) { /* not listening yet */ }
            Thread.Sleep(250);
        }
    }

    public static ClipboardWatcher? ClipboardWatch { get; private set; }
    public static string DataDir { get; private set; } = "";
    public static bool NoRegister { get; private set; }

    /// <summary>The user clicked "Download" on the copied-link popup.</summary>
    private async void AcceptOffer(ClipboardOffer offer)
    {
        if (_manager == null) return;
        bool confirm = _manager.Config.ConfirmCaptured;
        if (offer.Kind == OfferKind.File)
        {
            if (confirm) new CaptureWindow(_manager, _manager.AddAwaiting(offer.Url, null, null)).ShowOnTop();
            else _manager.Add(offer.Url, null, null);
            return;
        }

        // a video page (YouTube, Facebook, ...): yt-dlp reads it, then the usual "New download" window opens
        if (!Tools.HasYtDlp)
        {
            _tray?.ShowBalloonTip(6000, "Video support isn't installed",
                "Open Utylix > Settings > Video sites and click Install.", System.Windows.Forms.ToolTipIcon.Warning);
            return;
        }
        _tray?.ShowBalloonTip(3000, "Reading the video…", offer.Title, System.Windows.Forms.ToolTipIcon.Info);
        try
        {
            var info = await MediaService.GetInfoAsync(offer.Url, null, null, null, System.Threading.CancellationToken.None);
            // best quality up to 1080p, picture and sound in one file
            var best = info.Options.Where(o => o.Available && o.Id.StartsWith("h:") && int.Parse(o.Id[2..]) <= 1080)
                                   .OrderByDescending(o => int.Parse(o.Id[2..])).FirstOrDefault()
                       ?? info.Options.FirstOrDefault(o => o.Available && (o.Id == "best" || o.Id.StartsWith("h:")))
                       ?? info.Options.First(o => o.Available);
            var d = _manager.AddMedia(offer.Url, best.Id, info.Title, best.Size, null, null, awaiting: confirm);
            if (confirm) new CaptureWindow(_manager, d).ShowOnTop();
        }
        catch (Exception ex)
        {
            _tray?.ShowBalloonTip(7000, "Couldn't read that video", ex.Message, System.Windows.Forms.ToolTipIcon.Warning);
        }
    }

    // ---------- window / tray ----------
    private static void Bring(Window? w)
    {
        if (w == null) return;
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        w.Topmost = true;      // reliably bring to front, then let go
        w.Topmost = false;
    }

    /// <summary>true while a screen capture is being taken (Utylix hides itself, and must not announce that).</summary>
    public static bool Capturing { get; set; }

    /// <summary>Show Utylix on a tool from anywhere in the program.</summary>
    public static void Show(string key) => ((App)Current).ShowTab(key);

    /// <summary>A notice from the tray icon; clicking it shows the file in Explorer when a path is given.</summary>
    public static void Notify(string title, string text, string? revealPath) =>
        ((App)Current).Balloon(4000, title, revealPath != null ? text + "\nClick to open its folder" : text,
                               System.Windows.Forms.ToolTipIcon.Info, revealPath != null ? () => Reveal(revealPath) : null);

    // ---------- screen capture shortcuts ----------
    private Shortcuts? _shortcuts;

    /// <summary>Turn the capture shortcuts on/off to match the settings (also called after Settings are saved).</summary>
    public static void ApplyShortcuts()
    {
        var app = (App)Current;
        if (app._manager == null || app._shortcuts == null) return;
        var c = app._manager.Config;
        var (ctrlAlt, win, record, pause) = app._shortcuts.Apply(c.ShotCtrlAltS, c.ShotWinS, c.RecHotkey, c.OpenWinF);
        var refused = new List<string>();
        if (!ctrlAlt) refused.Add("Ctrl + Alt + S");
        if (!win) refused.Add(c.OpenWinF && !c.ShotWinS ? "Win + F" : "Win + S / Win + F");
        if (!record) refused.Add("Ctrl + Alt + R");
        if (!pause) refused.Add("Ctrl + Alt + P");
        if (refused.Count > 0)
            Notify("Shortcut not available", string.Join(" and ", refused) + " couldn't be used: another program already has it.", null);
    }

    // ---------- screen recorder: tray icon shows that it is recording ----------
    private RecorderPage? _recordingPage;
    private System.Windows.Forms.ToolStripItem? _stopRecordingItem;

    public static void SetRecordingState(bool recording, RecorderPage page)
    {
        var app = (App)Current;
        app._recordingPage = recording ? page : null;
        if (app._stopRecordingItem != null) app._stopRecordingItem.Enabled = recording;
        if (app._tray != null) app._tray.Text = recording ? "Utylix - recording…" : "Utylix";
    }

    /// <summary>Win + S / Ctrl + Alt + S / the tray entry: take a snip (the Snip window comes up afterwards with the picture).</summary>
    private void StartCaptureFromShortcut()
    {
        if (_manager == null || Capturing) return;
        SnipWindow.StartCapture(_manager);
    }

    /// <summary>Hides every Utylix window that is showing (so a capture never contains Utylix); the returned action shows them again.</summary>
    public static Action HideForCapture()
    {
        var shown = Current.Windows.Cast<Window>().Where(w => w.IsVisible && w is not CaptureOverlay).ToList();
        foreach (var w in shown) w.Hide();
        return () => { foreach (var w in shown) { try { w.Show(); } catch (InvalidOperationException) { /* closed meanwhile */ } } };
    }

    /// <summary>Bring the window forward on the given tool ("downloads", "converter", "capture", "recorder", "player").</summary>
    /// <summary>Win + F: bring Utylix up on the tool it was on; pressed again while it is in front, it goes back to the tray.</summary>
    private void OpenUtylix()
    {
        if (_shell == null) return;
        if (_shell.IsVisible && _shell.IsActive && _shell.WindowState != WindowState.Minimized) { _shell.Hide(); return; }
        ShowTab(string.IsNullOrEmpty(_shell.CurrentKey) ? "downloads" : _shell.CurrentKey);
    }

    private void ShowTab(string key)
    {
        if (_shell == null) return;
        _ephemeral = false;                    // the main window was asked for: Utylix stays
        _shell.SelectTab(key);
        Bring(_shell);
    }

    // ---------- notices from the tray icon (a click can do something) ----------
    private Action? _balloonClick;

    private void Balloon(int ms, string title, string text, System.Windows.Forms.ToolTipIcon icon, Action? onClick)
    {
        _balloonClick = onClick;
        _tray?.ShowBalloonTip(ms, title, text, icon);
    }

    /// <summary>What was chosen in the New download window: open the file, or show it in its folder.</summary>
    private void AfterDownload(string? what, string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        if (what == "folder") { Reveal(path); return; }
        if (what != "open") return;
        if (Util.IsRunnable(path))          // opening a program or script would run it: show it instead, and say why
        {
            Reveal(path);
            Balloon(6000, "Not opened automatically", "Programs and scripts are never run by themselves. It is selected in its folder.",
                    System.Windows.Forms.ToolTipIcon.Info, null);
            return;
        }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Reveal(path); }   // nothing installed to open it
    }

    /// <summary>Show a file (or folder) in Explorer.</summary>
    private static void Reveal(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
            else if (Directory.Exists(Path.GetDirectoryName(path) ?? "")) System.Diagnostics.Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"");
        }
        catch (Exception) { /* nothing more to do */ }
    }

    private static void LogError(Exception? e)
    {
        try { File.AppendAllText(Path.Combine(string.IsNullOrEmpty(DataDir) ? Path.GetTempPath() : DataDir, "error.log"), $"{DateTime.Now:s} {e}{Environment.NewLine}"); }
        catch (Exception) { /* nowhere to write it: nothing more to do */ }
    }

    // ---------- coming from the old name (IDM Clone) ----------
    /// <summary>First start after the rename: bring settings, history and the yt-dlp/ffmpeg tools over from the old folder.</summary>
    private static void MigrateLegacyData(string oldDir, string newDir)
    {
        if (!Directory.Exists(oldDir) || Directory.Exists(newDir)) return;
        try { Directory.Move(oldDir, newDir); return; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* still in use? copy instead */ }
        try { CopyDirectory(oldDir, newDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* start fresh rather than fail */ }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>"Start with Windows" was set under the old name, or the exe moved: point the entry at this exe.</summary>
    private static void MigrateAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            bool wasOn = key.GetValue(LegacyRunValue) != null || key.GetValue(RunValue) != null;
            key.DeleteValue(LegacyRunValue, false);
            if (wasOn) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --minimized");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* not fatal */ }
    }

    private void CreateTray()
    {
        var iconStream = GetResourceStream(new Uri("pack://application:,,,/app.ico")).Stream;
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Utylix", null, (_, _) => Dispatcher.Invoke(() => ShowTab("downloads")));
        menu.Items.Add("Downloads", null, (_, _) => Dispatcher.Invoke(() => ShowTab("downloads")));
        menu.Items.Add("Multi Convert", null, (_, _) => Dispatcher.Invoke(() => ShowTab("converter")));
        menu.Items.Add("Screen Capture", null, (_, _) => Dispatcher.Invoke(() => SnipWindow.Get(_manager!).Open()));      // opens the window like the Snipping Tool; Win + S snips at once
        menu.Items.Add("Browser extension…", null, (_, _) => Dispatcher.Invoke(() => ExtensionFiles.ShowHelp()));
        menu.Items.Add("Video Player", null, (_, _) => Dispatcher.Invoke(() => ShowTab("player")));
        menu.Items.Add("Screen Recorder", null, (_, _) => Dispatcher.Invoke(() => ShowTab("recorder")));
        _stopRecordingItem = menu.Items.Add("Stop recording", null, (_, _) => Dispatcher.Invoke(() => { if (_recordingPage != null) _ = _recordingPage.StopAsync(); }));
        _stopRecordingItem.Enabled = false;
        menu.Items.Add("Check for updates…", null, (_, _) => Dispatcher.Invoke(() => AppUpdateWindow.ShowWindow(_manager!)));
        menu.Items.Add("Pause all", null, (_, _) => _manager?.PauseAll());
        menu.Items.Add("Resume all", null, (_, _) => _manager?.ResumeAll());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Brightness", null, (_, _) => Dispatcher.Invoke(BrightnessWindow.ShowPanel));      // right above Exit
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(Quit));
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = "Utylix",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, ev) =>
        {
            if (ev.Button == System.Windows.Forms.MouseButtons.Left) Dispatcher.Invoke(() => ShowTab("downloads"));
        };
        _tray.BalloonTipClicked += (_, _) =>
        {
            var click = _balloonClick;
            _balloonClick = null;
            if (click != null) Dispatcher.BeginInvoke(click);
        };
        if (_shell != null)
            _shell.IsVisibleChanged += (_, _) =>
            {
                if (!_shell.IsVisible && !_hintShown && !_exiting && !Capturing)
                {
                    _hintShown = true;
                    _tray.ShowBalloonTip(3000, "Utylix is still running",
                        "It keeps running in the tray so it can take downloads from your browser.",
                        System.Windows.Forms.ToolTipIcon.Info);
                }
            };
    }

    /// <summary>false (with the reason) while something should not be interrupted by a restart.</summary>
    public bool CanRestartNow(out string reason)
    {
        reason = "";
        if (_recordingPage != null) { reason = "A screen recording is running. Stop it first, then update."; return false; }
        return true;
    }

    public void QuitForUpdate() => Quit();

    private void Quit()
    {
        if (_exiting) return;
        _exiting = true;
        _recordingPage?.StopForExit();                 // finish a recording in progress: nothing is lost
        _shortcuts?.Dispose();
        if (_shell != null) _shell.AllowClose = true;
        _api?.Stop();
        try { FanSettings.Client.StopAsync().Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { /* the helper also hands the fans back by itself when we are gone */ }
        ApiPort.Clear(DataDir);
        _manager?.Shutdown();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        Shutdown();
    }

    private static void AskRunningInstanceToShow(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            http.PostAsync($"http://127.0.0.1:{port}/api/show", new StringContent("{\"window\":\"hub\"}")).GetAwaiter().GetResult();
        }
        catch (Exception) { /* nothing to do: it may still be starting */ }
    }

    // ---------- theme ----------
    private void ApplyTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            IsDarkTheme = key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception) { IsDarkTheme = false; }

        void Set(string name, string hex) =>
            Resources[name] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        if (IsDarkTheme)
        {
            Set("BgBrush", "#12151C"); Set("CardBrush", "#1A1F2B"); Set("TextBrush", "#E6E9F0");
            Set("MutedBrush", "#8B93A5"); Set("LineBrush", "#2A3142"); Set("AccentBrush", "#5B8DEF");
            Set("OkBrush", "#4ADE80"); Set("ErrBrush", "#F87171"); Set("TrackBrush", "#2A3142");
            Set("ConnBrush", "#2DD4BF");
        }
        else
        {
            Set("BgBrush", "#F5F6F8"); Set("CardBrush", "#FFFFFF"); Set("TextBrush", "#1C2333");
            Set("MutedBrush", "#6B7280"); Set("LineBrush", "#E3E6EC"); Set("AccentBrush", "#2563EB");
            Set("OkBrush", "#16A34A"); Set("ErrBrush", "#DC2626"); Set("TrackBrush", "#E5E7EB");
            Set("ConnBrush", "#0D9488");
        }
    }

    // ---------- start with Windows ----------
    public static bool AutoStartEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) != null;
        }
    }

    public static void SetAutoStart(bool enable)
    {
        if (NoRegister) return;                    // a test/development copy must not point "Start with Windows" at itself
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enable) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --minimized");
        else key.DeleteValue(RunValue, false);
    }
}
