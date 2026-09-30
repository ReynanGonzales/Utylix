using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using IdmClone.Engine;

namespace IdmClone;

public enum OfferKind { File, Video }

public sealed record ClipboardOffer(string Url, OfferKind Kind, string Title, string Subtitle);

/// <summary>
/// Notices when you copy a link to a downloadable file or a video page and offers to download it.
/// It only ever looks at text that is a single web address; nothing is sent anywhere.
/// </summary>
public sealed class ClipboardWatcher
{
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

    private static readonly Regex VideoPage = new(
        @"^(https?://)(www\.|m\.|music\.)?(" +
        @"youtube\.com/(watch\?(.*&)?v=|shorts/|live/)|youtu\.be/[\w-]+|youtube-nocookie\.com/embed/" +
        @"|facebook\.com/(.+/)?(videos|reel|reels)/|facebook\.com/watch/?\?(.*&)?v=|fb\.watch/" +
        @"|instagram\.com/(reel|reels|p|tv)/|tiktok\.com/.+/video/|vm\.tiktok\.com/|vimeo\.com/\d+" +
        @"|(twitter|x)\.com/.+/status/|dailymotion\.com/video/|twitch\.tv/(videos/|.+/clip/)|clips\.twitch\.tv/)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Manager _manager;
    private readonly Action<ClipboardOffer> _offer;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly Dictionary<string, DateTime> _seen = new();
    private uint _sequence;
    private string? _ignore;

    public ClipboardWatcher(Manager manager, Action<ClipboardOffer> offer)
    {
        _manager = manager;
        _offer = offer;
    }

    public void Start()
    {
        _sequence = GetClipboardSequenceNumber();      // whatever is already on the clipboard is not offered
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    /// <summary>The app itself is about to copy this text (e.g. "Copy link"): don't offer it back.</summary>
    public void IgnoreNext(string text) => _ignore = text;

    private void Poll()
    {
        uint now = GetClipboardSequenceNumber();
        if (now == _sequence) return;
        string? text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch (Exception e) when (e is COMException or System.Runtime.InteropServices.ExternalException)
        {
            return;                                    // another program holds the clipboard: look again on the next tick
        }
        _sequence = now;
        if (text != null) Consider(text.Trim());
    }

    private void Consider(string text)
    {
        var cfg = _manager.Config;
        if (!cfg.WatchClipboard || text.Length == 0 || text.Length > 2048 || text.Contains('\n') || text.Contains(' ')) return;
        if (text == _ignore) { _ignore = null; return; }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;

        var offer = Classify(text, uri, cfg);
        if (offer == null) return;
        if (_seen.TryGetValue(text, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(10)) return;   // don't nag about the same link
        _seen[text] = DateTime.UtcNow;
        if (_manager.All().Any(d => string.Equals(d.Url, text, StringComparison.Ordinal))) return;           // already downloading/downloaded
        _offer(offer);
    }

    private static ClipboardOffer? Classify(string text, Uri uri, Config cfg)
    {
        string host = uri.Host.ToLowerInvariant();
        if (Config.SplitList(cfg.CaptureExclude).Any(s => host == s || host.EndsWith("." + s))) return null;

        if (VideoPage.IsMatch(text))
            return new ClipboardOffer(text, OfferKind.Video, "Video from " + (host.StartsWith("www.") ? host[4..] : host), "Choose picture, sound or both when it starts");

        string ext = System.IO.Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToLowerInvariant();
        if (ext.Length > 0 && Config.SplitList(cfg.CaptureTypes).Contains(ext))
        {
            string name = Uri.UnescapeDataString(System.IO.Path.GetFileName(uri.AbsolutePath));
            return new ClipboardOffer(text, OfferKind.File, name.Length > 0 ? name : uri.Host, uri.Host);
        }
        return null;
    }
}
