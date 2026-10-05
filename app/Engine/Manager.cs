using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace IdmClone.Engine;

public sealed class Config
{
    [JsonPropertyName("download_dir")] public string DownloadDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    [JsonPropertyName("connections")] public int Connections { get; set; } = 8;
    [JsonPropertyName("max_active")] public int MaxActive { get; set; } = 3;

    // ---- type folders (Compressed, Picture, ...) ----
    /// <summary>Put files into a folder per type inside the download folder.</summary>
    [JsonPropertyName("sort_by_type")] public bool SortByType { get; set; } = true;
    /// <summary>Only folders the user changed; missing types use "&lt;download folder&gt;\&lt;Type&gt;".</summary>
    [JsonPropertyName("category_dirs")] public Dictionary<string, string> CategoryDirs { get; set; } = new();

    // ---- video sites ----
    /// <summary>"off", "file" (separate .srt next to the video) or "embed" (inside the MP4).</summary>
    [JsonPropertyName("sub_mode")] public string SubMode { get; set; } = "file";
    /// <summary>Languages, e.g. "en, es". "all" saves every language.</summary>
    [JsonPropertyName("sub_langs")] public string SubLangs { get; set; } = "en";
    [JsonPropertyName("sub_auto")] public bool SubAuto { get; set; } = true;
    /// <summary>Check for a newer yt-dlp now and then and ask before installing it.</summary>
    [JsonPropertyName("auto_update_ytdlp")] public bool AutoUpdateYtDlp { get; set; } = true;
    [JsonPropertyName("last_ytdlp_check")] public long LastYtDlpCheck { get; set; }        // unix seconds
    [JsonPropertyName("last_ytdlp_offer")] public string LastYtDlpOffer { get; set; } = ""; // version we already asked about
    /// <summary>Look on GitHub now and then for a newer Utylix and ask before installing it.</summary>
    [JsonPropertyName("auto_update_app")] public bool AutoUpdateApp { get; set; } = true;
    [JsonPropertyName("last_app_check")] public long LastAppCheck { get; set; }            // unix seconds
    [JsonPropertyName("last_app_offer")] public string LastAppOffer { get; set; } = "";    // version we already asked about
    /// <summary>GitHub access token for a private repository (kept encrypted for this Windows user; "" = none).</summary>
    [JsonPropertyName("update_token")] public string UpdateToken { get; set; } = "";
    /// <summary>Offer to download links you copy.</summary>
    [JsonPropertyName("watch_clipboard")] public bool WatchClipboard { get; set; } = true;
    /// <summary>Let the browser extension start Utylix when it is closed.</summary>
    [JsonPropertyName("allow_autostart_by_browser")] public bool AllowBrowserStart { get; set; } = true;

    // ---- looks and notices ----
    /// <summary>"system" (follow Windows), "dark" or "light".</summary>
    [JsonPropertyName("theme")] public string Theme { get; set; } = "system";
    /// <summary>The accent colour: a key of App.Accents ("blue", "purple" ...).</summary>
    [JsonPropertyName("accent")] public string Accent { get; set; } = "blue";
    /// <summary>Show the "Utylix is still running in the tray" notice when the main window is closed (off by default).</summary>
    [JsonPropertyName("tray_notice")] public bool TrayNotice { get; set; }

    // ---- utilities ----
    /// <summary>"Convert" entry in Explorer's right-click menu for pictures.</summary>
    [JsonPropertyName("explorer_convert_menu")] public bool ExplorerMenu { get; set; } = true;

    // ---- the New download window ----
    /// <summary>What to do when a download finishes: "none", "open" or "folder" (remembered from the last time).</summary>
    [JsonPropertyName("after_download")] public string AfterDownload { get; set; } = "none";
    /// <summary>Keep uploading a finished torrent while Utylix runs (off: it stops as soon as the download is complete).</summary>
    [JsonPropertyName("torrent_seed")] public bool TorrentSeed { get; set; }
    /// <summary>Speed limits for torrents in KB/s (0 = no limit).</summary>
    [JsonPropertyName("torrent_down_kb")] public int TorrentDownKb { get; set; }
    [JsonPropertyName("torrent_up_kb")] public int TorrentUpKb { get; set; }
    /// <summary>Offer Utylix to Windows for magnet links and .torrent files.</summary>
    [JsonPropertyName("torrent_handler")] public bool TorrentHandler { get; set; } = true;

    // ---- background remover ----
    /// <summary>"Remove background" in Explorer's right-click menu for pictures.</summary>
    [JsonPropertyName("explorer_bg_menu")] public bool ExplorerBgMenu { get; set; } = true;

    // ---- video player ----
    /// <summary>"Play with Utylix" in Explorer's right-click menu for video and music files.</summary>
    [JsonPropertyName("explorer_play_menu")] public bool ExplorerPlayMenu { get; set; } = true;
    [JsonPropertyName("explorer_pdf_menu")] public bool ExplorerPdfMenu { get; set; } = true;

    // ---- archives ----
    /// <summary>Extract / Add to ZIP entries in Explorer's right-click menu, and Utylix in "Open with" for archives.</summary>
    [JsonPropertyName("explorer_archive_menu")] public bool ExplorerArchiveMenu { get; set; } = true;

    // ---- screen capture ----
    [JsonPropertyName("shot_copy")] public bool ShotCopy { get; set; } = true;            // copy every capture to the clipboard
    [JsonPropertyName("shot_autosave")] public bool ShotAutoSave { get; set; }            // save every capture as a file
    [JsonPropertyName("shot_dir")] public string ShotDir { get; set; } = "";              // "" = Pictures\Screenshots
    [JsonPropertyName("open_win_f")] public bool OpenWinF { get; set; } = true;           // Win + F brings Utylix up
    [JsonPropertyName("shot_win_s")] public bool ShotWinS { get; set; } = true;           // Win + S starts a capture
    [JsonPropertyName("shot_ctrl_alt_s")] public bool ShotCtrlAltS { get; set; } = true;  // Ctrl + Alt + S starts a capture

    // ---- screen recorder ----
    [JsonPropertyName("rec_dir")] public string RecDir { get; set; } = "";                // "" = Videos\Utylix
    [JsonPropertyName("rec_hotkey")] public bool RecHotkey { get; set; } = true;          // Ctrl + Alt + R starts / stops a recording

    // ---- browser capture ----
    /// <summary>Show the "New download" window when the browser hands something over.</summary>
    /// <summary>Bring Utylix up on its Downloads tab when the browser hands it a download.</summary>
    [JsonPropertyName("show_on_download")] public bool ShowOnDownload { get; set; } = true;
    [JsonPropertyName("confirm_captured")] public bool ConfirmCaptured { get; set; } = true;
    /// <summary>false = capture every download the browser makes; true = only the types below.</summary>
    [JsonPropertyName("capture_types_only")] public bool CaptureTypesOnly { get; set; }
    [JsonPropertyName("capture_types")] public string CaptureTypes { get; set; } = DefaultTypes;
    /// <summary>Show the floating "Download this video" button on videos. Right-click download always stays available.</summary>
    [JsonPropertyName("video_button")] public bool VideoButton { get; set; } = true;
    [JsonPropertyName("capture_min_kb")] public int CaptureMinKb { get; set; }
    [JsonPropertyName("capture_exclude")] public string CaptureExclude { get; set; } = "";

    public const string DefaultTypes =
        "zip, rar, 7z, tar, gz, bz2, xz, iso, img, exe, msi, msu, dmg, pkg, deb, rpm, apk, cab, bin, " +
        "mp4, mkv, avi, mov, wmv, flv, webm, m4v, mp3, flac, wav, aac, ogg, m4a, " +
        "jpg, jpeg, png, gif, webp, bmp, svg, tif, tiff, avif, heic, " +
        "pdf, doc, docx, xls, xlsx, ppt, pptx, epub, " +
        "torrent, txt, csv, json, xml, rtf, odt, ods, odp, srt, vtt, ttf, otf, psd, ico, jfif, heif, " +
        "mpg, mpeg, ts, 3gp, wma, opus, mid, zst, lz, lzma, tgz, wim, vhd, vhdx, vmdk, ova, " +
        "appx, msix, msixbundle, jar, crx, vsix, whl, ps1, bat, sh";

    /// <summary>"zip, .RAR; 7z" -> ["zip","rar","7z"]</summary>
    public static List<string> SplitList(string? text) =>
        System.Text.RegularExpressions.Regex.Split((text ?? "").ToLowerInvariant(), @"[\s,;]+")
            .Select(s => s.TrimStart('.', '*')).Where(s => s.Length > 0).Distinct().ToList();
}

public sealed class Manager
{
    private readonly object _lock = new();
    private readonly object _saveLock = new();
    private readonly Dictionary<string, Download> _downloads = new();
    private readonly string _dataDir;

    public Config Config { get; private set; } = new();
    public event Action<Download>? Completed;
    /// <summary>Something the user should hear about that no window is open for (title, text, isError).</summary>
    public event Action<string, string, bool>? Notice;
    public void RaiseNotice(string title, string text, bool error) => Notice?.Invoke(title, text, error);

    public Manager(string dataDir)
    {
        _dataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        Load();
        EnsureCategoryFolders();
        new Thread(SchedulerLoop) { IsBackground = true, Name = "scheduler" }.Start();
    }

    // ---------- type folders ----------
    /// <summary>Where files of this type go (the user's choice, else a sub-folder of the download folder).</summary>
    public string CategoryDir(string category)
    {
        lock (_lock)
            return Config.CategoryDirs.TryGetValue(category, out var dir) && !string.IsNullOrWhiteSpace(dir)
                ? dir : Path.Combine(Config.DownloadDir, category);
    }

    /// <summary>The folder a file should be saved to, based on its type.</summary>
    public string DirFor(string? fileName)
    {
        lock (_lock)
        {
            if (!Config.SortByType) return Config.DownloadDir;
            string category = Categories.Of(fileName);
            return category == Categories.Other ? Config.DownloadDir : CategoryDir(category);
        }
    }

    /// <summary>The user picked a different folder for this type in the prompt: make it the new default.</summary>
    public void RememberCategoryDir(string category, string dir)
    {
        if (Array.IndexOf(Categories.All, category) < 0) return;
        lock (_lock)
        {
            string full = Path.GetFullPath(dir);
            if (SamePath(full, Path.Combine(Config.DownloadDir, category))) Config.CategoryDirs.Remove(category);
            else Config.CategoryDirs[category] = full;
            File.WriteAllText(Path.Combine(_dataDir, "config.json"), JsonSerializer.Serialize(Config));
        }
    }

    /// <summary>Change a setting the Settings window doesn't own (e.g. when we last checked for a yt-dlp update).</summary>
    public void UpdateConfig(Action<Config> change)
    {
        lock (_lock)
        {
            change(Config);
            File.WriteAllText(Path.Combine(_dataDir, "config.json"), JsonSerializer.Serialize(Config));
        }
    }

    /// <summary>Create Compressed, Picture, Video, ... so they exist (and show up) before first use.</summary>
    public void EnsureCategoryFolders()
    {
        if (!Config.SortByType) return;
        foreach (var category in Categories.All)
        {
            try { Directory.CreateDirectory(CategoryDir(category)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    // ---------- persistence ----------
    private void Load()
    {
        try
        {
            var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(Path.Combine(_dataDir, "config.json")));
            if (cfg != null) Config = cfg;
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        try
        {
            var states = JsonSerializer.Deserialize<List<DownloadState>>(
                File.ReadAllText(Path.Combine(_dataDir, "state.json")));
            foreach (var st in states ?? new())
            {
                var d = Download.FromState(this, st);
                _downloads[d.Id] = d;
            }
        }
        catch (Exception e) when (e is IOException or JsonException) { }
    }

    public void Save()
    {
        List<Download> items;
        lock (_lock) items = _downloads.Values.ToList();
        var state = items.Where(d => d.Status != DlStatus.Awaiting).Select(d => d.ToState()).ToList();
        lock (_saveLock)
        {
            try
            {
                string tmp = Path.Combine(_dataDir, "state.json.tmp");
                File.WriteAllText(tmp, JsonSerializer.Serialize(state));
                File.Move(tmp, Path.Combine(_dataDir, "state.json"), true);
            }
            catch (IOException) { /* try again on the next save */ }
        }
    }

    /// <summary>Validate and store new settings (throws if the download folder can't be created).</summary>
    public Config ApplyConfig(Config c)
    {
        lock (_lock)
        {
            string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(
                string.IsNullOrWhiteSpace(c.DownloadDir) ? Config.DownloadDir : c.DownloadDir));
            Directory.CreateDirectory(full);

            // keep only folders that differ from the default "<download folder>\<Type>"
            var overrides = new Dictionary<string, string>();
            foreach (var (category, dir) in c.CategoryDirs)
            {
                if (Array.IndexOf(Categories.All, category) < 0 || string.IsNullOrWhiteSpace(dir)) continue;
                string path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dir));
                if (!SamePath(path, Path.Combine(full, category))) overrides[category] = path;
            }

            Config = new Config
            {
                DownloadDir = full,
                Connections = Math.Clamp(c.Connections, 1, 32),
                MaxActive = Math.Clamp(c.MaxActive, 1, 10),
                SortByType = c.SortByType,
                CategoryDirs = overrides,
                ConfirmCaptured = c.ConfirmCaptured,
                ShowOnDownload = c.ShowOnDownload,
                VideoButton = c.VideoButton,
                SubMode = c.SubMode is "off" or "file" or "embed" ? c.SubMode : "file",
                SubLangs = MediaService.CleanSubInput(c.SubLangs),
                SubAuto = c.SubAuto,
                AutoUpdateYtDlp = c.AutoUpdateYtDlp,
                LastYtDlpCheck = Config.LastYtDlpCheck,        // not a Settings field: keep what we have
                LastYtDlpOffer = Config.LastYtDlpOffer,
                AutoUpdateApp = c.AutoUpdateApp,
                LastAppCheck = Config.LastAppCheck,            // not Settings fields: keep what we have
                LastAppOffer = Config.LastAppOffer,
                UpdateToken = Config.UpdateToken,
                WatchClipboard = c.WatchClipboard,
                AllowBrowserStart = c.AllowBrowserStart,
                ExplorerMenu = c.ExplorerMenu,
                ExplorerArchiveMenu = c.ExplorerArchiveMenu,
                ExplorerBgMenu = c.ExplorerBgMenu,
                ExplorerPlayMenu = c.ExplorerPlayMenu,
                ExplorerPdfMenu = c.ExplorerPdfMenu,
                AfterDownload = Config.AfterDownload,          // not a Settings field: keep what we have
                Theme = c.Theme is "dark" or "light" ? c.Theme : "system",
                Accent = string.IsNullOrWhiteSpace(c.Accent) ? "blue" : c.Accent,
                TrayNotice = c.TrayNotice,
                TorrentSeed = c.TorrentSeed,
                TorrentDownKb = Math.Clamp(c.TorrentDownKb, 0, 10_000_000),
                TorrentUpKb = Math.Clamp(c.TorrentUpKb, 0, 10_000_000),
                TorrentHandler = c.TorrentHandler,
                ShotCopy = c.ShotCopy,
                ShotAutoSave = c.ShotAutoSave,
                ShotDir = c.ShotDir?.Trim() ?? "",
                ShotWinS = c.ShotWinS,
                OpenWinF = c.OpenWinF,
                ShotCtrlAltS = c.ShotCtrlAltS,
                RecDir = c.RecDir?.Trim() ?? "",
                RecHotkey = c.RecHotkey,
                CaptureTypesOnly = c.CaptureTypesOnly,
                CaptureTypes = string.Join(", ", Config.SplitList(c.CaptureTypes)),
                CaptureMinKb = Math.Max(0, c.CaptureMinKb),
                CaptureExclude = string.Join(", ", Config.SplitList(c.CaptureExclude)),
            };
            File.WriteAllText(Path.Combine(_dataDir, "config.json"), JsonSerializer.Serialize(Config));
            EnsureCategoryFolders();
            TorrentService.Apply(Config);
            return Config;
        }
    }

    // ---------- operations ----------
    public Download Add(string url, Dictionary<string, string>? headers, string? hint, bool awaiting = false)
    {
        var d = new Download(this, url, headers, string.IsNullOrEmpty(hint) ? null : Util.Sanitize(hint),
                             Config.DownloadDir, Config.Connections);
        if (TorrentSource.Is(url)) { d.SetTorrent(); awaiting = false; }      // a magnet link or .torrent: no confirmation window, the torrent engine takes it
        else if (awaiting) d.MarkAwaiting();
        lock (_lock) _downloads[d.Id] = d;
        Save();
        return d;
    }

    /// <summary>A YouTube/Facebook/... video: yt-dlp does the fetching (see Download.Media.cs).</summary>
    public Download AddMedia(string url, string option, string? title, long expectedSize, List<MediaCookie>? cookies, string? userAgent,
                             bool awaiting = false, string? referer = null)
    {
        var d = new Download(this, url, null, null, Config.DownloadDir, Config.Connections);
        d.SetMedia(option, title, expectedSize, cookies, userAgent, referer);
        if (awaiting) d.MarkAwaiting();                 // waits for the "New download" window's Start button
        lock (_lock) _downloads[d.Id] = d;
        Save();
        return d;
    }

    /// <summary>Register a browser-captured download that waits for the user's OK (see CaptureWindow).</summary>
    public Download AddAwaiting(string url, Dictionary<string, string>? headers, string? hint)
    {
        var d = Add(url, headers, hint, awaiting: true);
        d.BeginProbe();
        return d;
    }

    public List<Download> All() { lock (_lock) return _downloads.Values.ToList(); }

    public Download? Get(string id) { lock (_lock) return _downloads.GetValueOrDefault(id); }

    public ISet<string> ReservedNames(Download me)
    {
        lock (_lock)
            return _downloads.Values
                .Where(d => d != me && d.FileName != null && d.Dir == me.Dir && d.Status != DlStatus.Completed)
                .Select(d => d.FileName!).ToHashSet();
    }

    public void Remove(string id, bool deleteFile)
    {
        Download? d;
        lock (_lock)
        {
            _downloads.Remove(id, out d);
        }
        if (d == null) return;
        d.Remove(deleteFile);
        Save();
    }

    public void ClearCompleted()
    {
        lock (_lock)
        {
            foreach (var id in _downloads.Where(kv => kv.Value.Status == DlStatus.Completed).Select(kv => kv.Key).ToList())
                _downloads.Remove(id);
        }
        Save();
    }

    public void PauseAll() { foreach (var d in All()) d.Pause(); Save(); }
    public void ResumeAll() { foreach (var d in All()) d.Resume(); Save(); }

    public void RaiseCompleted(Download d) => Completed?.Invoke(d);

    public void Shutdown()
    {
        foreach (var d in All()) d.Pause();
        foreach (var d in All()) d.WaitStopped(3000);
        Save();
        TorrentService.Shutdown();
    }

    private void SchedulerLoop()
    {
        while (true)
        {
            Thread.Sleep(500);
            lock (_lock)
            {
                var items = _downloads.Values.OrderBy(d => d.Created).ToList();
                int active = items.Count(d => d.Status == DlStatus.Downloading);
                foreach (var d in items)
                {
                    if (active >= Config.MaxActive) break;
                    if (d.Status == DlStatus.Queued && d.CanLaunch)
                    {
                        d.Launch();
                        active++;
                    }
                }
            }
        }
    }
}
