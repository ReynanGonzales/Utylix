using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IdmClone.Engine;

/// <summary>
/// Video-site downloads (YouTube, Facebook, ...): the same list entry as any download, but yt-dlp does the
/// fetching and ffmpeg joins video and audio. Progress is read from yt-dlp's output.
/// </summary>
public sealed partial class Download
{
    private string? _mediaOption, _mediaTitle, _mediaPhase, _mediaUa, _mediaFinal, _mediaCurrentFile, _mediaReferer;
    private List<MediaCookie>? _mediaCookies;
    private long _mediaCompleted, _mediaCurrentDone, _mediaCurrentTotal;
    private double _mediaSpeed;
    private readonly HashSet<string> _mediaFiles = new();

    public bool IsMedia => _mediaOption != null;
    public bool MediaIsAudio => _mediaOption is "audio" or "mp3";

    /// <summary>File extension suggested in the "New download" window (yt-dlp decides the real one).</summary>
    public string MediaExt => _mediaOption == "mp3" ? ".mp3" : _mediaOption == "audio" ? ".m4a" : ".mp4";

    /// <summary>The video's title as the site reports it (used as the suggested file name).</summary>
    public string? MediaTitle { get { lock (_lock) return _mediaTitle; } }

    /// <summary>yt-dlp output name: the user's own name if they typed one in the prompt, else the video's title.</summary>
    private string OutputTemplate()
    {
        string? name = _hint == null ? null : Path.GetFileNameWithoutExtension(_hint);
        // no name typed: use the tidied-up title we already know (not yt-dlp's own, which for Facebook is
        // "1.1M views · 8.1K reactions | caption | Page Name"); only if we have none, let yt-dlp name it
        if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(_mediaTitle))
        {
            name = Util.Sanitize(_mediaTitle);
            if (name.Length > 120) name = name[..120].TrimEnd(' ', '.');
        }
        return string.IsNullOrWhiteSpace(name) ? "%(title).120B.%(ext)s" : name.Replace("%", "%%") + ".%(ext)s";
    }

    public void SetMedia(string option, string? title, long expectedSize, List<MediaCookie>? cookies, string? userAgent, string? referer = null)
    {
        lock (_lock)
        {
            _mediaReferer = MediaService.CleanReferer(referer);
            _mediaOption = option;
            _mediaTitle = title;
            _size = expectedSize > 0 ? expectedSize : -1;
            _mediaCookies = cookies;
            _mediaUa = userAgent;
            _resumable = true;
        }
    }

    /// <summary>Snapshot for the UI. Called with the lock held.</summary>
    private DownloadInfo MediaInfo()
    {
        bool completed = _status == DlStatus.Completed;
        long expected = _size ?? -1;
        long size = completed ? expected : expected > 0 ? expected : _mediaCurrentTotal > 0 ? _mediaCompleted + _mediaCurrentTotal : -1;
        long done = completed ? size : _mediaCompleted + _mediaCurrentDone;
        if (size > 0) done = Math.Min(done, size);
        bool running = _status == DlStatus.Downloading;
        long speed = running ? (long)_mediaSpeed : 0;
        long? eta = running && speed > 1 && size > 0 && _mediaPhase == null ? (long)((size - done) / (double)speed) : null;
        string name = FileName ?? _mediaTitle ?? Url;
        string savePath = FileName == null ? Dir : Path.Combine(Dir, FileName);
        return new DownloadInfo(Id, Url, name, size, done, _status, _error, speed, eta, true, Created,
            running ? 1 : 0, completed ? FinalPath : null,
            size > 0 ? $"0:{size - 1}:{done}" : "", savePath, "video (via yt-dlp)", 0, 0,
            IsMedia: true, Phase: running ? _mediaPhase : null);
    }

    /// <summary>One progress/print line from yt-dlp (formats set with --progress-template / --print).</summary>
    private void OnMediaLine(string line)
    {
        if (line.Length < 5 || !line.StartsWith("IDM", StringComparison.Ordinal)) return;
        var f = line.Split('|', 8);
        lock (_lock)
        {
            switch (f[0])
            {
                case "IDMT" when f.Length >= 2:
                    _mediaTitle = f[1];
                    break;
                case "IDMF" when f.Length >= 2:
                    _mediaFinal = line[(line.IndexOf('|') + 1)..].Trim();
                    break;
                case "IDMQ" when f.Length >= 3:
                    _mediaPhase = f[2] switch
                    {
                        "Merger" => "Joining video and audio…",
                        "MoveFiles" => "Finishing…",
                        _ => "Processing…",
                    };
                    break;
                case "IDMP" when f.Length >= 8:
                    _mediaPhase = null;
                    string file = f[7];
                    if (_mediaCurrentFile != null && file != _mediaCurrentFile && _mediaCurrentDone > 0)
                    {
                        _mediaCompleted += _mediaCurrentTotal > 0 ? _mediaCurrentTotal : _mediaCurrentDone;   // previous stream is done
                        _mediaCurrentDone = _mediaCurrentTotal = 0;
                    }
                    _mediaCurrentFile = file;
                    _mediaFiles.Add(file);
                    long done = ParseLong(f[2]);
                    long total = ParseLong(f[3]) is > 0 and var t ? t : ParseLong(f[4]);
                    _mediaCurrentDone = done;
                    _mediaCurrentTotal = total;
                    _mediaSpeed = ParseDouble(f[5]);
                    if (f[1] == "finished")
                    {
                        _mediaCompleted += done;
                        _mediaCurrentDone = _mediaCurrentTotal = 0;
                        _mediaCurrentFile = null;
                    }
                    break;
            }
        }
    }

    private static long ParseLong(string s) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? (long)v : 0;
    private static double ParseDouble(string s) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    private async Task RunMediaAsync(CancellationTokenSource cts)
    {
        string? cookieFile = null;
        var errLines = new List<string>();
        try
        {
            if (!Tools.HasYtDlp)
                throw new InvalidOperationException("Video support isn't installed. Open Settings and click Install under Video sites.");

            // choose the folder outside our own lock (the manager takes its lock, the scheduler takes ours)
            string chosen;
            lock (_lock) chosen = _dirLocked ? Dir : "";
            string dir = chosen.Length > 0 ? chosen : _mgr.DirFor("file" + MediaExt);   // sound goes to Music, video to Video
            Directory.CreateDirectory(dir);
            lock (_lock)
            {
                if (!_dirLocked) Dir = dir;
                _mediaPhase = "Starting…";
                _mediaFinal = null;
                _mediaCompleted = _mediaCurrentDone = _mediaCurrentTotal = 0;
                _mediaSpeed = 0;
                _mediaCurrentFile = null;
            }

            var (selector, merge, sort) = MediaService.SelectorFor(_mediaOption!, Tools.HasFfmpeg);

            // Login cookies help with private/age-restricted videos but can make a site refuse the request, so if the
            // first try (with cookies) fails, run it once more without them before giving up.
            // Attempts in order: with cookies, without; then - only if the site answered "blocks download tools" -
            // the same again imitating Chrome's connection (yt-dlp --impersonate).
            bool hasCookies = _mediaCookies is { Count: > 0 };
            var plan = new List<(bool Cookies, bool Impersonate)>();
            if (hasCookies) plan.Add((true, false));
            plan.Add((false, false));
            int attempt = 0;
            bool addedImpersonation = false;
            string? blocked = null;
            while (true)
            {
                var (useCookies, impersonate) = plan[attempt];
                errLines.Clear();
                lock (_lock) { _mediaCompleted = _mediaCurrentDone = _mediaCurrentTotal = 0; _mediaCurrentFile = null; _mediaPhase = "Starting…"; }
                cookieFile = useCookies ? MediaService.WriteCookieFile(_mediaCookies) : null;

                var psi = MediaService.NewProcess();
                void Arg(params string[] a) { foreach (var x in a) psi.ArgumentList.Add(x); }
                Arg("--no-playlist", "--newline", "--windows-filenames", "--no-mtime", "--continue", "--no-warnings",
                    "--no-simulate", "--encoding", "utf-8", "--concurrent-fragments", "4", "--retries", "10", "--fragment-retries", "10",
                    "-P", dir, "-o", OutputTemplate(), "-f", selector);
                if (sort.Length > 0) Arg("-S", sort);
                if (merge) Arg("--merge-output-format", "mp4");
                if (_mediaOption == "mp3") Arg("-x", "--audio-format", "mp3", "--audio-quality", "192K");   // YouTube's audio is ~130 kbps; more only wastes space

                // Subtitles (only for downloads that have a picture): a separate .srt next to the video, or embedded in it.
                var cfg = _mgr.Config;
                bool hasPicture = _mediaOption != "audio" && _mediaOption != "mp3" && _mediaOption?.StartsWith("v:") != true;
                if (hasPicture && cfg.SubMode != "off")
                {
                    bool ffmpeg = Tools.HasFfmpeg;
                    Arg("--ignore-errors");        // a subtitle that can't be fetched must never cost you the video
                    Arg("--sub-langs", MediaService.SubLangsArg(cfg.SubLangs), "--sub-format", ffmpeg ? "srt/vtt/best" : "vtt/best");
                    if (cfg.SubAuto) Arg("--write-auto-subs");
                    if (ffmpeg && cfg.SubMode == "embed")
                        Arg("--embed-subs", "--compat-options", "no-keep-subs");   // inside the MP4; no loose .srt left behind
                    else
                    {
                        Arg("--write-subs");
                        if (ffmpeg) Arg("--convert-subs", "srt");                   // a separate, widely supported .srt file
                    }
                }
                if (Tools.HasFfmpeg) Arg("--ffmpeg-location", Tools.FfmpegDir);
                if (cookieFile != null) Arg("--cookies", cookieFile);
                if (!string.IsNullOrEmpty(_mediaUa) && !_mediaUa.Contains('\n')) Arg("--user-agent", _mediaUa);
                if (_mediaReferer != null) Arg("--referer", _mediaReferer);
                if (impersonate) Arg("--impersonate", "chrome");
                Arg("--progress",
                    "--progress-template", "download:IDMP|%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(progress.filename)s",
                    "--progress-template", "postprocess:IDMQ|%(progress.status)s|%(progress.postprocessor)s",
                    "--print", "before_dl:IDMT|%(title)s",
                    "--print", "after_move:IDMF|%(filepath)s");
                Arg("--", Url);                    // "--": the address can never be mistaken for an option

                int exitCode;
                using (var proc = new Process { StartInfo = psi })
                {
                    proc.OutputDataReceived += (_, e) => { if (e.Data != null) OnMediaLine(e.Data); };
                    proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errLines) errLines.Add(e.Data); };
                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    using (cts.Token.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch (Exception) { } }))
                        await proc.WaitForExitAsync(CancellationToken.None);
                    proc.WaitForExit();            // let the last output lines arrive
                    exitCode = proc.ExitCode;
                }
                if (cookieFile != null) { try { File.Delete(cookieFile); } catch (Exception) { } cookieFile = null; }

                if (cts.IsCancellationRequested) return;
                // exit code 0 = all fine. Non-zero but the video file exists = only something optional (a subtitle) failed.
                if (exitCode == 0 || (_mediaFinal != null && File.Exists(_mediaFinal))) break;

                string message = MediaService.ErrorFrom(errLines.ToArray());
                if (MediaService.IsBlockMessage(message)) blocked ??= message;
                attempt++;
                if (attempt >= plan.Count)
                {
                    if (blocked == null || addedImpersonation) throw new InvalidOperationException(blocked ?? message);
                    addedImpersonation = true;                       // plain attempts are used up and the site blocks scripts
                    if (hasCookies) plan.Add((true, true));
                    plan.Add((false, true));
                }
            }
            if (_mediaFinal == null || !File.Exists(_mediaFinal))
                throw new InvalidOperationException("yt-dlp finished but no file was produced.");
        }
        catch (Exception e) when (!cts.IsCancellationRequested)
        {
            _failed = e.Message;
        }
        finally
        {
            if (cookieFile != null) try { File.Delete(cookieFile); } catch (Exception) { }
            FinishMedia();
        }
    }

    private void FinishMedia()
    {
        bool completed = false;
        lock (_lock)
        {
            _speed = 0;
            _mediaSpeed = 0;
            if (_removed)
            {
                if (_status != DlStatus.Completed) CleanMediaTemp();
                return;
            }
            if (_failed != null) { _status = DlStatus.Error; _error = _failed; }
            else if (_mediaFinal != null && File.Exists(_mediaFinal))
            {
                try
                {
                    Dir = Path.GetDirectoryName(_mediaFinal)!;
                    FileName = Path.GetFileName(_mediaFinal);
                    _size = new FileInfo(_mediaFinal).Length;
                    Util.MarkOfTheWeb(_mediaFinal, Url, null);
                    _status = DlStatus.Completed;
                    _mediaCookies = null;
                    _headers = new();
                    completed = true;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _status = DlStatus.Error;
                    _error = "Could not read the finished file: " + e.Message;
                }
            }
            else if (_status == DlStatus.Downloading) _status = DlStatus.Paused;
        }
        _mgr.Save();
        if (completed) _mgr.RaiseCompleted(this);
    }

    /// <summary>Remove the half-finished pieces yt-dlp left behind (only files we saw it write).</summary>
    private void CleanMediaTemp()
    {
        foreach (var f in _mediaFiles.ToList())
            foreach (var path in new[] { f, f + ".part", f + ".ytdl" })
                TryDelete(path);
    }
}
