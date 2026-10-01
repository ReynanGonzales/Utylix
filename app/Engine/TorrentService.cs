using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;

namespace IdmClone.Engine;

/// <summary>What a download address is: a magnet link, or a .torrent file (on the web or on this PC).</summary>
public static class TorrentSource
{
    public static bool IsMagnet(string? text) =>
        text != null && text.Length < 8192 && text.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) && text.Contains("xt=urn:bt", StringComparison.OrdinalIgnoreCase);

    /// <summary>"https://site/file.torrent" or "C:\...\file.torrent".</summary>
    public static bool IsTorrentFile(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 8192) return false;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return uri.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
        return Path.IsPathFullyQualified(text) && text.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
    }

    public static bool Is(string? text) => IsMagnet(text) || IsTorrentFile(text);

    /// <summary>The name a magnet link carries ("dn="), if any.</summary>
    public static string? MagnetName(string text)
    {
        try { return MagnetLink.TryParse(text, out var m) && !string.IsNullOrWhiteSpace(m!.Name) ? m.Name : null; }
        catch (Exception e) when (e is ArgumentException or FormatException) { return null; }
    }
}

/// <summary>
/// The one torrent engine of this program (MonoTorrent): peers, DHT, trackers, port forwarding. Every torrent in the downloads list is a
/// <see cref="TorrentManager"/> in it; resuming a stopped torrent re-adds it and the engine reads back what it had already saved.
/// </summary>
public static class TorrentService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static ClientEngine? _engine;
    private static string _cacheDir = "";

    /// <summary>Torrents that are being downloaded or seeded right now, by the id of their entry in the list.</summary>
    private static readonly ConcurrentDictionary<string, TorrentManager> Running = new();

    public static string CacheDir => _cacheDir;

    /// <summary>The port this copy listens on: each Windows user gets a different one, so two users can both run Utylix.</summary>
    private static int Port()
    {
        int h = 0;
        foreach (char c in ApiPort.UserId) h = (h * 31 + c) & 0x7fffffff;
        return 52000 + h % 2000;
    }

    private static EngineSettings Build(Config cfg) => new EngineSettingsBuilder
    {
        CacheDirectory = _cacheDir,
        AllowPortForwarding = true,
        AutoSaveLoadFastResume = true,
        AutoSaveLoadMagnetLinkMetadata = true,
        ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new IPEndPoint(IPAddress.Any, Port()) },
        DhtEndPoint = new IPEndPoint(IPAddress.Any, Port()),
        MaximumDownloadRate = Math.Max(0, cfg.TorrentDownKb) * 1024,
        MaximumUploadRate = Math.Max(0, cfg.TorrentUpKb) * 1024,
    }.ToSettings();

    public static async Task<ClientEngine> EngineAsync(string dataDir, Config cfg)
    {
        if (_engine != null) return _engine;
        await Gate.WaitAsync();
        try
        {
            if (_engine == null)
            {
                _cacheDir = Path.Combine(dataDir, "torrents");
                Directory.CreateDirectory(_cacheDir);
                _engine = new ClientEngine(Build(cfg));
            }
            return _engine;
        }
        finally { Gate.Release(); }
    }

    /// <summary>The speed limits in Settings changed.</summary>
    public static void Apply(Config cfg)
    {
        var engine = _engine;
        if (engine == null) return;
        _ = Task.Run(async () =>
        {
            try { await engine.UpdateSettingsAsync(Build(cfg)); }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException) { }
        });
    }

    public static bool TryGet(string id, out TorrentManager manager) => Running.TryGetValue(id, out manager!);
    public static void Track(string id, TorrentManager m) => Running[id] = m;

    /// <summary>Stops this torrent and takes it out of the engine (what it saved stays, so it can be resumed). Optionally deletes the files too.</summary>
    public static async Task ReleaseAsync(string id, TorrentManager m, bool deleteFiles)
    {
        Running.TryRemove(id, out _);
        var engine = _engine;
        try { await m.StopAsync(TimeSpan.FromSeconds(8)); }
        catch (Exception e) when (e is InvalidOperationException or TimeoutException or OperationCanceledException) { }
        if (engine == null) return;
        try { await engine.RemoveAsync(m, deleteFiles ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.CacheDataOnly); }
        catch (Exception e) when (e is InvalidOperationException or IOException or ArgumentException) { }
    }

    /// <summary>Utylix is closing: stop everything that is still seeding.</summary>
    public static void Shutdown()
    {
        var engine = _engine;
        if (engine == null) return;
        try { engine.StopAllAsync().Wait(5000); } catch (Exception e) when (e is AggregateException or InvalidOperationException) { }
        try { engine.Dispose(); } catch (Exception e) when (e is InvalidOperationException or AggregateException) { }
        _engine = null;
    }
}
