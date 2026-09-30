using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace IdmClone.Engine;

public sealed record MediaCookie(string Domain, string Name, string Value, string Path, bool Secure, bool HttpOnly, long Expires);

/// <summary>One quality the user can pick for a video, e.g. "1080p" (~45 MB).</summary>
public sealed record MediaOption(string Id, string Label, long Size, bool Available, string Note);

public sealed record MediaInfoResult(string Title, double Duration, string Uploader, List<MediaOption> Options, bool Ffmpeg);

/// <summary>Talks to yt-dlp: which qualities does this page's video have, and how do we download one.</summary>
public static class MediaService
{
    /// <summary>Only these option names are accepted from the browser; the real yt-dlp selector is built here.</summary>
    public static readonly Regex OptionRx = new(@"^(best|audio|mp3|h:\d{3,4}|v:\d{3,4})$", RegexOptions.Compiled);

    /// <summary>
    /// yt-dlp's format selector, whether to merge into an MP4, and a sort order. The quality is chosen with the sort
    /// ("res:1080" = best picture up to 1080p, measured on the SHORTER side, so a vertical 1080x1920 reel or Short counts
    /// as 1080p) rather than with height filters, which count the long side and left vertical videos with "Requested
    /// format is not available". H.264 + AAC are preferred at the same size because every player handles them.
    /// </summary>
    public static (string Selector, bool Merge, string Sort) SelectorFor(string option, bool hasFfmpeg)
    {
        if (option == "audio") return ("ba[ext=m4a]/ba/b", false, "");            // sound only, as-is (M4A)
        if (option == "mp3") return ("ba/b", false, "");                          // sound only, converted to MP3 (needs ffmpeg)
        if (option == "best") return (hasFfmpeg ? "bv*+ba/b" : "b", hasFfmpeg, "");
        if (option.StartsWith("v:", StringComparison.Ordinal))                    // picture only, no sound
            return ("bv/bv*", false, $"res:{int.Parse(option[2..])},vcodec:h264");
        // Always ONE file with picture and sound together: with ffmpeg the best video + best audio are joined into
        // an MP4; without ffmpeg only ready-made single-file versions are chosen, so a video is never left as
        // separate video/audio files.
        int h = int.Parse(option[2..]);
        return hasFfmpeg ? ("bv*+ba/b", true, $"res:{h},vcodec:h264,acodec:aac") : ("b", false, $"res:{h}");
    }

    // ---------- a good file name for the video ----------
    private static readonly Regex StatsPart = new(@"^\s*[\d.,]+\s*[KMBkmb]?\s*(views?|plays?|reactions?|likes?|comments?|shares?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The title yt-dlp reports, made fit to be a file name. Facebook gives things like
    /// "1.1M views · 8.1K reactions | the caption | Page Name" (or just "Facebook" when you are logged in): keep the caption.
    /// </summary>
    internal static string BestTitle(JsonElement root)
    {
        string uploader = Str(root, "uploader") ?? Str(root, "channel") ?? "";
        string extractor = Str(root, "extractor_key") ?? Str(root, "extractor") ?? "";
        string title = TidyText(Str(root, "title") ?? "");

        if (extractor.StartsWith("Facebook", StringComparison.OrdinalIgnoreCase) && title.Contains('|'))
        {
            var keep = title.Split('|').Select(p => p.Trim())
                            .Where(p => p.Length > 0 && !StatsPart.IsMatch(p) && !p.Equals(uploader, StringComparison.OrdinalIgnoreCase));
            title = string.Join(" - ", keep);
        }
        else if (StatsPart.IsMatch(title) && !title.Contains(' ', StringComparison.Ordinal)) title = "";

        bool generic = title.Length == 0 || title.Equals("Video", StringComparison.OrdinalIgnoreCase) ||
                       title.Equals(extractor, StringComparison.OrdinalIgnoreCase) || title.Equals(Str(root, "extractor") ?? "", StringComparison.OrdinalIgnoreCase) ||
                       title.Equals("Facebook Watch", StringComparison.OrdinalIgnoreCase) || title.Equals("Watch", StringComparison.OrdinalIgnoreCase);
        if (generic) title = Snippet(Str(root, "description") ?? "");                          // the post's own text
        if (title.Length == 0)
        {
            string id = Str(root, "id") ?? "";
            title = uploader.Length > 0 ? (id.Length > 0 ? $"{uploader} - {id}" : uploader) : "Video";
        }
        return Shorten(title, 100);
    }

    /// <summary>One line, single spaces, and no emoji (they end up as "__" in a Windows file name).</summary>
    private static string TidyText(string text) =>
        Regex.Replace(Regex.Replace(text, @"[\p{Cs}\p{So}️‍]", ""), @"\s+", " ").Trim();

    /// <summary>First line of some text, tidied up.</summary>
    private static string Snippet(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            string t = TidyText(line);
            if (t.Length > 0) return Shorten(t, 100);
        }
        return "";
    }

    private static string Shorten(string text, int max)
    {
        if (text.Length <= max) return text;
        int cut = text.LastIndexOf(' ', max);
        return text[..(cut > max / 2 ? cut : max)].TrimEnd(' ', ',', '-', ':', ';', '.');
    }

    /// <summary>What the user typed in Settings for subtitle languages: keep only sane characters, e.g. "en, es" -> "en, es".</summary>
    public static string CleanSubInput(string? text)
    {
        var parts = Regex.Split(text ?? "", @"[\s,;]+").Select(p => Regex.Replace(p, @"[^A-Za-z0-9_\-*.]", "")).Where(p => p.Length is > 0 and <= 12).Distinct();
        string joined = string.Join(", ", parts.Take(12));
        return joined.Length == 0 ? "en" : joined;
    }

    /// <summary>
    /// yt-dlp's --sub-langs value. "en, es" -> "en(-orig)?,es(-orig)?,-live_chat": the language itself (and its "original"
    /// track) but NOT YouTube's machine translations (en-de, en-en, ...): a loose "en.*" matches ~160 of those and gets
    /// the request rate-limited (HTTP 429). Type a full code like "en-US" to get exactly that one.
    /// </summary>
    public static string SubLangsArg(string? typed)
    {
        var langs = CleanSubInput(typed).Split(", ");
        if (langs.Contains("all", StringComparer.OrdinalIgnoreCase)) return "all,-live_chat";
        // "en" -> "en(-orig)?": that language and its "original" track, never the machine translations (en-de, en-en, ...)
        return string.Join(",", langs.Select(l => l.Contains('*') || l.Contains('.') || l.Contains('-') ? l : $"{l}(-orig)?")) + ",-live_chat";
    }

    /// <summary>Cookies in the Netscape format yt-dlp reads. The file is deleted right after use.</summary>
    public static string? WriteCookieFile(IEnumerable<MediaCookie>? cookies)
    {
        var list = cookies?.Take(1000).ToList();
        if (list == null || list.Count == 0) return null;
        var sb = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var c in list)
        {
            if (c.Name.Contains('\t') || c.Value.Contains('\t') || c.Value.Contains('\n') || c.Domain.Contains('\n')) continue;
            string domain = (c.HttpOnly ? "#HttpOnly_" : "") + c.Domain;
            sb.Append(domain).Append('\t').Append(c.Domain.StartsWith('.') ? "TRUE" : "FALSE").Append('\t')
              .Append(string.IsNullOrEmpty(c.Path) ? "/" : c.Path).Append('\t').Append(c.Secure ? "TRUE" : "FALSE").Append('\t')
              .Append(c.Expires > 0 ? c.Expires : 0).Append('\t').Append(c.Name).Append('\t').Append(c.Value).Append('\n');
        }
        string path = Path.Combine(Path.GetTempPath(), "utylix-cookies-" + Guid.NewGuid().ToString("N")[..10] + ".txt");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    public static ProcessStartInfo NewProcess()
    {
        var psi = new ProcessStartInfo(Tools.YtDlp)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        return psi;
    }

    /// <summary>
    /// A short explanation of a failed yt-dlp run. yt-dlp's message can span several lines and often ends with a
    /// hint fragment, so the whole error is read (not just its last line) and known cases are put in plain words.
    /// </summary>
    public static string ErrorFrom(IEnumerable<string> stderr)
    {
        var lines = stderr.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
        int first = lines.FindIndex(l => l.StartsWith("ERROR:", StringComparison.Ordinal));
        string full = string.Join(" ", first >= 0 ? lines.Skip(first) : lines.TakeLast(3));
        if (full.Length == 0) full = "yt-dlp failed";

        bool Has(string s) => full.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("needs to be reloaded"))
            return "YouTube refused the request. Try again in a moment; if it keeps happening, click Update on yt-dlp in Settings.";
        if (Has("Sign in to confirm"))
            return "YouTube wants a login for this video: be signed in to YouTube in your browser and try again.";
        if (Has("Unsupported URL"))
            return "This page's video isn't in a form yt-dlp can read. If the player loads a stream, play the video for a few seconds first, then try again.";
        if (Has("impersonate") || Has("cloudflare") || Has("HTTP Error 403") || Has("anti-bot") || Has("captcha"))
            return "This site blocks download tools (anti-bot protection), so the video can't be fetched.";
        if (Has("DRM"))
            return "This video is copy-protected (DRM) and can't be downloaded.";
        if (Has("Private video") || Has("members-only") || Has("login required") || Has("This video is only available"))
            return "This video is private or needs an account. Be logged in to the site in your browser and try again.";

        full = Regex.Replace(full, @"^ERROR:\s*(\[[^\]]+\]\s*[^:]*:\s*)?", "");
        return full.Length > 300 ? full[..300] + "…" : full;
    }

    /// <summary>
    /// Login cookies help with private/age-restricted videos but can also make a site refuse the request (YouTube
    /// answers "The page needs to be reloaded" to a live session's cookies). So if it fails with cookies, try once without.
    /// </summary>
    public static async Task<MediaInfoResult> GetInfoAsync(string url, List<MediaCookie>? cookies, string? userAgent, string? referer, CancellationToken ct)
    {
        // Attempts, in order: with cookies, without; then, only if the site answered "blocks download tools",
        // the same again while imitating Chrome's connection (yt-dlp's --impersonate).
        bool hasCookies = cookies is { Count: > 0 };
        var plan = new List<(List<MediaCookie>? Cookies, bool Impersonate)>();
        if (hasCookies) plan.Add((cookies, false));
        plan.Add((null, false));
        string? blocked = null;
        bool addedImpersonation = false;
        InvalidOperationException? last = null;
        for (int i = 0; i < plan.Count; i++)
        {
            try { return await GetInfoOnceAsync(url, plan[i].Cookies, userAgent, referer, plan[i].Impersonate, ct); }
            catch (InvalidOperationException e)
            {
                last = e;
                if (IsBlockMessage(e.Message)) blocked ??= e.Message;
                if (i == plan.Count - 1 && blocked != null && !addedImpersonation)
                {
                    addedImpersonation = true;
                    if (hasCookies) plan.Add((cookies, true));
                    plan.Add((null, true));
                }
            }
        }
        throw new InvalidOperationException(blocked ?? last!.Message);
    }

    /// <summary>The site refused because it doesn't like scripted clients (see ErrorFrom).</summary>
    public static bool IsBlockMessage(string message) => message.Contains("blocks download tools", StringComparison.Ordinal);

    /// <summary>Only an http(s) address is passed on as the referrer, and never one that could break the command line.</summary>
    public static string? CleanReferer(string? referer) =>
        !string.IsNullOrEmpty(referer) && referer.Length <= 2000 && !referer.Contains('\n') && !referer.Contains('\r') &&
        Uri.TryCreate(referer, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? referer : null;

    private static async Task<MediaInfoResult> GetInfoOnceAsync(string url, List<MediaCookie>? cookies, string? userAgent, string? referer, bool impersonate, CancellationToken ct)
    {
        if (!Tools.HasYtDlp) throw new InvalidOperationException("yt-dlp is not installed.");
        string? cookieFile = WriteCookieFile(cookies);
        try
        {
            var psi = NewProcess();
            foreach (var a in new[] { "-J", "--no-playlist", "--no-warnings", "--socket-timeout", "20", "--encoding", "utf-8" }) psi.ArgumentList.Add(a);
            if (cookieFile != null) { psi.ArgumentList.Add("--cookies"); psi.ArgumentList.Add(cookieFile); }
            if (!string.IsNullOrEmpty(userAgent) && !userAgent.Contains('\n')) { psi.ArgumentList.Add("--user-agent"); psi.ArgumentList.Add(userAgent); }
            if (CleanReferer(referer) is { } ref1) { psi.ArgumentList.Add("--referer"); psi.ArgumentList.Add(ref1); }
            if (impersonate) { psi.ArgumentList.Add("--impersonate"); psi.ArgumentList.Add("chrome"); }
            psi.ArgumentList.Add("--");                 // everything after this is the address, never an option
            psi.ArgumentList.Add(url);

            using var proc = new Process { StartInfo = psi };
            var err = new List<string>();
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.Add(e.Data); };
            proc.Start();
            proc.BeginErrorReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            using var kill = timeout.Token.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch (Exception) { } });
            string json = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested) throw new TimeoutException("Reading the video took too long.");
            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException(ErrorFrom(err.ToArray()));
            return Parse(json);
        }
        finally { if (cookieFile != null) try { File.Delete(cookieFile); } catch (Exception) { } }
    }

    private static MediaInfoResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string title = BestTitle(root);
        if (root.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("Live streams can't be downloaded.");

        bool ffmpeg = Tools.HasFfmpeg;
        var formats = root.TryGetProperty("formats", out var f) && f.ValueKind == JsonValueKind.Array
            ? f.EnumerateArray().Select(ReadFmt).ToList() : new List<Fmt>();

        var video = formats.Where(x => x.HasVideo && x.Height >= 144).ToList();
        long audioBest = formats.Where(x => !x.HasVideo && x.HasAudio).Select(x => x.Size).DefaultIfEmpty(0).Max();
        var options = new List<MediaOption>();

        foreach (var g in video.GroupBy(x => x.Height).OrderByDescending(g => g.Key))
        {
            var progressive = g.Where(x => x.HasAudio).ToList();
            long size = progressive.Count > 0 ? progressive.Max(x => x.Size) : g.Max(x => x.Size) + audioBest;
            bool available = ffmpeg || progressive.Count > 0;
            string label = $"{g.Key}p" + (g.Max(x => x.Fps) >= 50 ? $"{(int)Math.Round(g.Max(x => x.Fps))}" : "");
            options.Add(new MediaOption($"h:{g.Key}", label, size, available, available ? "" : "needs ffmpeg"));
        }
        // picture only (no sound): every height that has a separate video stream; needs no ffmpeg
        foreach (var g in video.Where(x => !x.HasAudio).GroupBy(x => x.Height).OrderByDescending(g => g.Key))
        {
            string label = $"{g.Key}p" + (g.Max(x => x.Fps) >= 50 ? $"{(int)Math.Round(g.Max(x => x.Fps))}" : "");
            options.Add(new MediaOption($"v:{g.Key}", label, g.Max(x => x.Size), true, "video only"));
        }
        // sound only: as-is (M4A), or converted to MP3
        if (formats.Any(x => !x.HasVideo && x.HasAudio))
        {
            options.Add(new MediaOption("audio", "Audio only", audioBest, true, ""));
            options.Add(new MediaOption("mp3", "Audio only (MP3)", audioBest, ffmpeg, ffmpeg ? "" : "needs ffmpeg"));
        }
        if (options.Count == 0)
            options.Add(new MediaOption("best", "Best quality", 0, true, ""));

        double duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
        return new MediaInfoResult(title, duration, Str(root, "uploader") ?? Str(root, "channel") ?? "", options, ffmpeg);
    }

    private sealed record Fmt(int Height, bool HasVideo, bool HasAudio, long Size, double Fps);

    private static Fmt ReadFmt(JsonElement e)
    {
        string v = Str(e, "vcodec") ?? "none", a = Str(e, "acodec") ?? "none";
        long size = Num(e, "filesize");
        if (size <= 0) size = Num(e, "filesize_approx");
        int height = (int)Num(e, "height"), width = (int)Num(e, "width");
        int res = height > 0 && width > 0 ? Math.Min(height, width) : height;     // a vertical 1080x1920 video is "1080p"
        return new Fmt(res, v != "none", a != "none", size, Dbl(e, "fps"));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var n) ? n
        : e.TryGetProperty(name, out var q) && q.ValueKind == JsonValueKind.Number ? (long)q.GetDouble() : 0;

    private static double Dbl(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;
}
