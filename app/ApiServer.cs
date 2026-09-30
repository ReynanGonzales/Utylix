using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Tiny local HTTP API (127.0.0.1 only) used by the browser extension:
/// ping, list downloads, add a download, bring the window to the front.
/// </summary>
public sealed class ApiServer
{
    private const int MaxBody = 1024 * 1024;
    private readonly Manager _manager;
    private readonly int _port;
    private readonly Action<string?> _show;
    private readonly Action<Download> _prompt;
    private readonly Action<List<string>, string?> _convert;
    private readonly Action<string, List<string>> _archive;
    private readonly Action<string, List<string>> _tool;
    private HttpListener? _listener;

    public ApiServer(Manager manager, int port, Action<string?> show, Action<Download> prompt, Action<List<string>, string?> convert,
                     Action<string, List<string>> archive, Action<string, List<string>> tool)
    {
        _tool = tool;
        _manager = manager;
        _port = port;
        _show = show;
        _prompt = prompt;
        _convert = convert;
        _archive = archive;
    }

    /// <summary>What the extension needs to decide whether a browser download should be captured.</summary>
    private object CaptureSettings()
    {
        var c = _manager.Config;
        return new
        {
            video_button = c.VideoButton,
            types_only = c.CaptureTypesOnly,
            types = Config.SplitList(c.CaptureTypes),
            min_kb = c.CaptureMinKb,
            exclude = Config.SplitList(c.CaptureExclude),
        };
    }

    public bool Start()
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Start();
        }
        catch (HttpListenerException)
        {
            return false;
        }
        _ = Task.Run(LoopAsync);
        return true;
    }

    public void Stop()
    {
        try { _listener?.Close(); } catch (Exception) { }
    }

    private async Task LoopAsync()
    {
        while (_listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    // Browsers always attach Origin to cross-site POSTs and web pages can't forge it, so this stops
    // random websites from driving the local API. Extension pages are allowed.
    private bool OriginAllowed(string origin) =>
        origin == $"http://127.0.0.1:{_port}" || origin == $"http://localhost:{_port}" ||
        origin.StartsWith("chrome-extension://", StringComparison.Ordinal) ||
        origin.StartsWith("moz-extension://", StringComparison.Ordinal);

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            string? origin = req.Headers["Origin"];
            string host = (req.Headers["Host"] ?? "").Split(':')[0].ToLowerInvariant();
            if ((host != "127.0.0.1" && host != "localhost") || (!string.IsNullOrEmpty(origin) && !OriginAllowed(origin)))
            {
                Send(ctx, 403, new { error = "forbidden" }, null);
                return;
            }
            if (!string.IsNullOrEmpty(origin))
            {
                res.Headers["Access-Control-Allow-Origin"] = origin;
                res.Headers["Access-Control-Allow-Headers"] = "Content-Type";
                res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                res.Headers["Vary"] = "Origin";
            }
            string path = req.Url?.AbsolutePath ?? "/";
            string method = req.HttpMethod;

            if (method == "OPTIONS") { res.StatusCode = 204; res.Close(); return; }
            if (method == "GET" && path == "/")
                Send(ctx, 200, null, "Utylix is running. Use the desktop window or the browser extension.");
            else if (method == "GET" && path == "/api/ping")
                Send(ctx, 200, new
                {
                    ok = true, app = "idm-clone", version = "1.0.0", capture = CaptureSettings(),
                    tools = new { ytdlp = Tools.HasYtDlp, ffmpeg = Tools.HasFfmpeg },   // is video-site support installed?
                }, null);
            else if (method == "GET" && path == "/api/downloads")
                Send(ctx, 200, new { downloads = _manager.All().Select(d => ToApi(d.Info())).ToList() }, null);
            else if (method == "POST" && path == "/api/show")
            {
                string? which = null;                               // {"window": "downloads"} or "hub" (default)
                if (req.ContentLength64 is > 0 and < 1024)
                    using (var doc = JsonDocument.Parse(new StreamReader(req.InputStream).ReadToEnd()))
                        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("window", out var w) && w.ValueKind == JsonValueKind.String)
                            which = w.GetString();
                _show(which);
                Send(ctx, 200, new { ok = true }, null);
            }
            else if (method == "POST" && path == "/api/convert")
                Convert(ctx);
            else if (method == "POST" && path == "/api/archive")
                Archive(ctx);
            else if (method == "POST" && path == "/api/tool")
                Tool(ctx);
            else if (method == "POST" && path == "/api/add")
                Add(ctx);
            else if (method == "POST" && path == "/api/discard")
                Discard(ctx);
            else if (method == "POST" && path == "/api/media/info")
                MediaInfo(ctx);
            else if (method == "POST" && path == "/api/media/add")
                MediaAdd(ctx);
            else
                Send(ctx, 404, new { error = "not found" }, null);
        }
        catch (ArgumentException e) { Send(ctx, 400, new { error = e.Message }, null); }
        catch (JsonException) { Send(ctx, 400, new { error = "invalid JSON" }, null); }
        catch (Exception e) { Send(ctx, 500, new { error = e.Message }, null); }
    }

    private void Add(HttpListenerContext ctx)
    {
        if (ctx.Request.ContentLength64 > MaxBody) throw new ArgumentException("body too large");
        using var doc = JsonDocument.Parse(new StreamReader(ctx.Request.InputStream).ReadToEnd());
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("expected a JSON object");

        string? Str(string name) =>
            root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        var urls = new List<string>();
        if (root.TryGetProperty("urls", out var arr) && arr.ValueKind == JsonValueKind.Array)
            urls.AddRange(arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()));
        else if (Str("url") is { } single) urls.Add(single.Trim());
        if (urls.Count == 0 || !urls.All(ValidUrl))
            throw new ArgumentException("Only http:// and https:// URLs are supported");

        var headers = new Dictionary<string, string>();
        foreach (var (key, field) in new[] { ("Referer", "referer"), ("Cookie", "cookie"), ("User-Agent", "user_agent") })
        {
            string? v = Str(field);
            if (string.IsNullOrEmpty(v)) continue;
            if (v.Contains('\r') || v.Contains('\n') || v.Length > 16384) throw new ArgumentException("bad " + field);
            headers[key] = v;
        }
        string? hint = urls.Count == 1 ? Str("filename") : null;

        // Browser hand-offs ask for a confirmation window (like IDM's "Download File Info"), if enabled.
        bool prompt = urls.Count == 1 && _manager.Config.ConfirmCaptured &&
                      root.TryGetProperty("prompt", out var pr) && pr.ValueKind == JsonValueKind.True;
        if (prompt)
        {
            var d = _manager.AddAwaiting(urls[0], headers, hint);
            _prompt(d);
            Send(ctx, 200, new { added = new[] { d.Id }, prompt = true }, null);
            return;
        }
        var added = urls.Select(u => _manager.Add(u, headers, hint).Id).ToList();
        Send(ctx, 200, new { added }, null);
    }

    /// <summary>
    /// Explorer's right-click "Convert" (a second copy of the program) hands its pictures to the running one.
    /// Body: { "files": ["C:\...\a.png"], "to": "jpg" }  ("to" missing = open the Multi Convert tab instead).
    /// </summary>
    private void Convert(HttpListenerContext ctx)
    {
        if (ctx.Request.ContentLength64 > MaxBody) throw new ArgumentException("body too large");
        using var doc = JsonDocument.Parse(new StreamReader(ctx.Request.InputStream).ReadToEnd());
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("files", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("expected {\"files\": [...]}");

        string? format = null;                               // e.g. "jpg", "mp4", "mp3"; missing = open the Multi Convert tab instead
        if (root.TryGetProperty("to", out var to) && to.ValueKind == JsonValueKind.String)
        {
            format = (to.GetString() ?? "").Trim().TrimStart('.').ToLowerInvariant();
            if (format == "jpeg") format = "jpg";
            if (format == "tif") format = "tiff";
            if (!MediaConverter.IsTarget(format)) throw new ArgumentException("unknown format");
        }
        var files = new List<string>();
        foreach (var e in arr.EnumerateArray().Take(500))
        {
            string? p = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            // only real, absolute paths of picture files that exist: nothing else is ever read
            if (p != null && p.Length < 32768 && Path.IsPathFullyQualified(p) && !p.StartsWith(@"\\", StringComparison.Ordinal) &&
                MediaConverter.IsSupported(p) && File.Exists(p))
                files.Add(Path.GetFullPath(p));
        }
        if (files.Count > 0) _convert(files, format);
        Send(ctx, 200, new { accepted = files.Count }, null);
    }

    /// <summary>
    /// Explorer's "Remove background" and "Play" commands hand their files to the running copy.
    /// Body: { "op": "remove-bg" | "play", "files": ["C:\\...\\a.png"] }
    /// </summary>
    private void Tool(HttpListenerContext ctx)
    {
        if (ctx.Request.ContentLength64 > MaxBody) throw new ArgumentException("body too large");
        using var doc = JsonDocument.Parse(new StreamReader(ctx.Request.InputStream).ReadToEnd());
        var root = doc.RootElement;
        string op = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() ?? "" : "";
        if (op is not ("remove-bg" or "play" or "brightness" or "update")) throw new ArgumentException("unknown operation");
        if (op is "brightness" or "update") { _tool(op, new List<string>()); Send(ctx, 200, new { accepted = 0 }, null); return; }     // opens the brightness panel / the update window
        if (!root.TryGetProperty("files", out var arr) || arr.ValueKind != JsonValueKind.Array) throw new ArgumentException("expected {\"files\": [...]}");
        var files = new List<string>();
        foreach (var e in arr.EnumerateArray().Take(500))
        {
            string? p = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            // only real, absolute paths of local files of the right kind: nothing else is ever read
            if (p == null || p.Length >= 32768 || !Path.IsPathFullyQualified(p) || p.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(p)) continue;
            if (op == "remove-bg" ? !BackgroundRemover.IsPicture(p) : !PlayerMedia.IsPlayable(p)) continue;
            files.Add(Path.GetFullPath(p));
        }
        if (files.Count > 0) _tool(op, files);
        Send(ctx, 200, new { accepted = files.Count }, null);
    }

    /// <summary>
    /// Explorer's archive commands (a second copy of the program) hand their files to the running one.
    /// Body: { "op": "open" | "add" | "extract-here" | "extract-to" | "extract-ask" | "zip-add", "files": ["C:\\...\\a.zip"] }
    /// </summary>
    private void Archive(HttpListenerContext ctx)
    {
        if (ctx.Request.ContentLength64 > MaxBody) throw new ArgumentException("body too large");
        using var doc = JsonDocument.Parse(new StreamReader(ctx.Request.InputStream).ReadToEnd());
        var root = doc.RootElement;
        string op = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() ?? "" : "";
        if (op is not ("open" or "add" or "extract-here" or "extract-to" or "extract-ask" or "zip-add")) throw new ArgumentException("unknown operation");
        if (!root.TryGetProperty("files", out var arr) || arr.ValueKind != JsonValueKind.Array) throw new ArgumentException("expected {\"files\": [...]}");

        bool needsArchive = op is "open" or "extract-here" or "extract-to" or "extract-ask";
        var files = new List<string>();
        foreach (var e in arr.EnumerateArray().Take(500))
        {
            string? p = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (p == null || p.Length >= 32768 || !Path.IsPathFullyQualified(p) || p.StartsWith(@"\\", StringComparison.Ordinal)) continue;
            bool ok = needsArchive ? File.Exists(p) && ArchiveService.IsArchiveName(p) : File.Exists(p) || Directory.Exists(p);
            if (ok) files.Add(Path.GetFullPath(p));
        }
        if (files.Count > 0) _archive(op, files);
        Send(ctx, 200, new { accepted = files.Count }, null);
    }

    // ---------- video sites (YouTube, Facebook, ...) ----------
    private static (string Url, List<MediaCookie> Cookies, string? UserAgent, JsonElement Root, JsonDocument Doc) ReadMediaRequest(HttpListenerContext ctx)
    {
        if (ctx.Request.ContentLength64 > MaxBody * 4) throw new ArgumentException("body too large");
        var doc = JsonDocument.Parse(new StreamReader(ctx.Request.InputStream).ReadToEnd());
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("expected a JSON object");
        string url = root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString()!.Trim() : "";
        if (!ValidUrl(url)) throw new ArgumentException("Only http:// and https:// addresses are supported");

        var cookies = new List<MediaCookie>();
        if (root.TryGetProperty("cookies", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var c in arr.EnumerateArray().Take(1000))
            {
                string S(string n) => c.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
                bool B(string n) => c.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.True;
                long E = c.TryGetProperty("expires", out var ex) && ex.ValueKind == JsonValueKind.Number ? (long)ex.GetDouble() : 0;
                if (S("name").Length > 0 && S("domain").Length > 0)
                    cookies.Add(new MediaCookie(S("domain"), S("name"), S("value"), S("path"), B("secure"), B("httpOnly"), E));
            }
        string? ua = root.TryGetProperty("user_agent", out var uaEl) && uaEl.ValueKind == JsonValueKind.String ? uaEl.GetString() : null;
        return (url, cookies, ua, root, doc);
    }

    /// <summary>The page the player was embedded in / loaded from; many sites refuse a stream without it.</summary>
    private static string? RefererOf(JsonElement root) =>
        root.TryGetProperty("referer", out var r) && r.ValueKind == JsonValueKind.String ? MediaService.CleanReferer(r.GetString()) : null;

    /// <summary>What qualities does the video on this page have? (asks yt-dlp; takes a few seconds)</summary>
    private void MediaInfo(HttpListenerContext ctx)
    {
        if (!Tools.HasYtDlp) { Send(ctx, 409, new { error = "Video support isn't installed. Open Utylix → Settings → Video sites.", code = "not_installed" }, null); return; }
        var (url, cookies, ua, root, doc) = ReadMediaRequest(ctx);
        using (doc)
        {
            try
            {
                var info = MediaService.GetInfoAsync(url, cookies, ua, RefererOf(root), CancellationToken.None).GetAwaiter().GetResult();
                Send(ctx, 200, new
                {
                    title = info.Title, duration = info.Duration, uploader = info.Uploader, ffmpeg = info.Ffmpeg,
                    options = info.Options.Select(o => new { id = o.Id, label = o.Label, size = o.Size, available = o.Available, note = o.Note }),
                }, null);
            }
            catch (Exception e) when (e is InvalidOperationException or TimeoutException or JsonException or IOException)
            {
                Send(ctx, 502, new { error = e.Message }, null);
            }
        }
    }

    private void MediaAdd(HttpListenerContext ctx)
    {
        if (!Tools.HasYtDlp) { Send(ctx, 409, new { error = "Video support isn't installed. Open Utylix → Settings → Video sites.", code = "not_installed" }, null); return; }
        var (url, cookies, ua, root, doc) = ReadMediaRequest(ctx);
        using (doc)
        {
            string option = root.TryGetProperty("option", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() ?? "" : "";
            if (!MediaService.OptionRx.IsMatch(option)) throw new ArgumentException("unknown quality option");
            string? title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            long size = root.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? (long)s.GetDouble() : 0;
            // like any browser hand-off: show the "New download" window first, unless it is switched off in Settings
            bool prompt = _manager.Config.ConfirmCaptured && root.TryGetProperty("prompt", out var pr) && pr.ValueKind == JsonValueKind.True;
            var d = _manager.AddMedia(url, option, title?.Length > 200 ? title[..200] : title, Math.Max(0, size), cookies, ua,
                                      awaiting: prompt, referer: RefererOf(root));
            if (prompt) _prompt(d);
            Send(ctx, 200, new { added = new[] { d.Id }, prompt }, null);
        }
    }

    /// <summary>
    /// Lets the extension take back a hand-off that failed before any byte arrived (it then gives
    /// the URL back to the browser). Refuses anything that made progress or isn't in the error state.
    /// </summary>
    private void Discard(HttpListenerContext ctx)
    {
        if (ctx.Request.ContentLength64 > MaxBody) throw new ArgumentException("body too large");
        using var doc = JsonDocument.Parse(new StreamReader(ctx.Request.InputStream).ReadToEnd());
        string? id = doc.RootElement.ValueKind == JsonValueKind.Object &&
                     doc.RootElement.TryGetProperty("id", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;
        var d = id == null ? null : _manager.Get(id);
        bool ok = d != null && d.Status == DlStatus.Error && d.Info().Downloaded == 0;
        if (ok) _manager.Remove(id!, false);
        Send(ctx, 200, new { discarded = ok }, null);
    }

    private static bool ValidUrl(string url) =>
        url.Length <= 8192 && Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private static object ToApi(DownloadInfo i) => new
    {
        id = i.Id, url = i.Url, filename = i.FileName, size = i.Size, downloaded = i.Downloaded,
        status = i.Status.ToString().ToLowerInvariant(), error = i.Error, speed = i.Speed, eta = i.Eta,
        resumable = i.Resumable, connections = i.Connections,
        created = new DateTimeOffset(DateTime.SpecifyKind(i.Created, DateTimeKind.Utc)).ToUnixTimeSeconds(),
    };

    private static void Send(HttpListenerContext ctx, int code, object? json, string? text)
    {
        var res = ctx.Response;
        byte[] body = text != null
            ? System.Text.Encoding.UTF8.GetBytes(text)
            : JsonSerializer.SerializeToUtf8Bytes(json);
        res.StatusCode = code;
        res.ContentType = text != null ? "text/plain; charset=utf-8" : "application/json";
        res.Headers["Cache-Control"] = "no-store";
        res.Headers["X-Content-Type-Options"] = "nosniff";
        res.ContentLength64 = body.Length;
        try
        {
            res.OutputStream.Write(body, 0, body.Length);
            res.Close();
        }
        catch (Exception) { /* client went away */ }
    }
}
