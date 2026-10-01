using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;

namespace IdmClone.Engine;

/// <summary>
/// Torrent and magnet-link downloads: the same entry in the list as any download, but the torrent engine (TorrentService) fetches the
/// pieces from other people. A multi-file torrent is saved as a folder named after the torrent.
/// </summary>
public sealed partial class Download
{
    private bool _torrent;
    private string? _torrentFile;                 // the .torrent kept in the data folder (for a .torrent given as a file or a web address)
    private string? _tName, _tPhase;
    private long _tDone, _tSize, _tUp;
    private double _tSpeed;
    private int _tPeers, _tSeeds;
    private bool _tSeeding;

    public bool IsTorrent => _torrent;

    private static readonly string[] PublicTrackers =
    {
        "udp://tracker.opentrackr.org:1337/announce", "udp://open.stealth.si:80/announce", "udp://exodus.desync.com:6969/announce",
        "udp://tracker.torrent.eu.org:451/announce", "udp://open.demonii.com:1337/announce",
    };
    private DateTime _tStarted;

    public void SetTorrent() { lock (_lock) { _torrent = true; _resumable = true; } }

    private DownloadInfo TorrentInfo()
    {
        bool completed = _status == DlStatus.Completed;
        bool running = _status == DlStatus.Downloading;
        long size = _tSize > 0 ? _tSize : _size ?? -1;
        long done = completed ? size : Math.Min(_tDone, size > 0 ? size : _tDone);
        long speed = running ? (long)_tSpeed : 0;
        long? eta = running && speed > 1 && size > 0 && _tPhase == null ? (long)((size - done) / (double)speed) : null;
        string name = FileName ?? _tName ?? (TorrentSource.IsMagnet(Url) ? TorrentSource.MagnetName(Url) : null) ?? (TorrentSource.IsMagnet(Url) ? "Magnet link" : Path.GetFileName(Url));
        string savePath = FileName == null ? Dir : Path.Combine(Dir, FileName);
        string phase = running ? (_tPhase ?? (_tSeeding ? "Seeding" : null)) ?? "" : "";
        return new DownloadInfo(Id, Url, name, size, done, _status, _error, speed, eta, true, Created,
            running ? _tPeers : 0, completed ? FinalPathOrNull : null,
            size > 0 ? $"0:{size - 1}:{done}" : "", savePath, "torrent", 0, 0,
            Phase: phase.Length > 0 ? phase : null, IsTorrent: true, Seeds: _tSeeds, Uploaded: _tUp);
    }

    /// <summary>Where the .torrent of this download is kept (the data folder), whatever it was given as.</summary>
    private async Task<string?> PrepareTorrentFileAsync(CancellationToken ct)
    {
        if (TorrentSource.IsMagnet(Url)) return null;
        string cache = Path.Combine(TorrentService.CacheDir, "files");
        Directory.CreateDirectory(cache);
        string target = _torrentFile ?? Path.Combine(cache, Id + ".torrent");
        if (File.Exists(target)) return target;
        if (Uri.TryCreate(Url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            using var resp = await Net.SendAsync(Url, _headers, null, ct);
            Net.EnsureSuccess(resp);
            if ((resp.Content.Headers.ContentLength ?? 0) > 20 * 1024 * 1024) throw new InvalidOperationException("That does not look like a .torrent file (it is too big).");
            byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > 20 * 1024 * 1024 || bytes.Length < 20 || bytes[0] != (byte)'d')
                throw new InvalidOperationException("The address did not give a .torrent file.");
            await File.WriteAllBytesAsync(target, bytes, ct);
        }
        else
        {
            if (!File.Exists(Url)) throw new FileNotFoundException("The .torrent file is gone: " + Url);
            File.Copy(Url, target, true);
        }
        lock (_lock) _torrentFile = target;
        return target;
    }

    private async Task RunTorrentAsync(CancellationTokenSource cts)
    {
        var ct = cts.Token;
        TorrentManager? manager = null;
        bool completed = false;
        try
        {
            lock (_lock) { _tPhase = "Starting…"; _tSpeed = 0; _tSeeding = false; _tStarted = DateTime.UtcNow; }
            var cfg = _mgr.Config;
            var engine = await TorrentService.EngineAsync(App.DataDir, cfg);

            string chosen;
            lock (_lock) chosen = _dirLocked ? Dir : "";
            string dir = chosen.Length > 0 ? chosen : Path.Combine(cfg.DownloadDir, "Torrents");
            Directory.CreateDirectory(dir);
            lock (_lock) { if (!_dirLocked) Dir = dir; }

            if (!TorrentService.TryGet(Id, out manager))
            {
                string? file = await PrepareTorrentFileAsync(ct);
                if (file != null)
                {
                    var torrent = await Torrent.LoadAsync(file);
                    manager = await engine.AddAsync(torrent, dir);
                }
                else
                {
                    if (!MagnetLink.TryParse(Url, out var magnet)) throw new InvalidOperationException("That magnet link is not valid.");
                    manager = await engine.AddAsync(magnet!, dir);
                    // old magnet links often list trackers that are long dead: add a few public ones that answer
                    foreach (string t in PublicTrackers)
                    {
                        try { await manager.TrackerManager.AddTrackerAsync(new Uri(t)); }
                        catch (Exception e) when (e is ArgumentException or InvalidOperationException or UriFormatException) { }
                    }
                }
                TorrentService.Track(Id, manager);
            }
            if (manager.State is TorrentState.Stopped) await manager.StartAsync();
            else if (manager.State is TorrentState.Paused) await manager.StartAsync();

            while (!ct.IsCancellationRequested)
            {
                lock (_lock) UpdateTorrentSnapshot(manager);
                if (manager.State == TorrentState.Error) throw new InvalidOperationException(manager.Error?.Exception?.Message ?? manager.Error?.Reason.ToString() ?? "The torrent failed.");
                if (manager.HasMetadata && manager.Complete) { completed = true; break; }
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            _failed = e is MonoTorrent.TorrentException ? "Torrent: " + e.Message : e.Message;
        }
        finally
        {
            await FinishTorrentAsync(manager, completed);
        }
    }

    /// <summary>Called with the lock held.</summary>
    private void UpdateTorrentSnapshot(TorrentManager m)
    {
        _tSpeed = m.Monitor.DownloadRate;
        _tUp = m.Monitor.DataBytesSent;
        _tPeers = m.OpenConnections;
        _tSeeds = m.Peers.Seeds;
        if (m.HasMetadata)
        {
            var t = m.Torrent!;
            _tName = t.Name;
            _tSize = t.Size;
            _size = t.Size;
            FileName = t.Name;
            _tDone = (long)(t.Size * (m.Progress / 100.0));
            _tPhase = m.State switch
            {
                TorrentState.Hashing or TorrentState.HashingPaused => "Checking files… " + (int)m.Progress + "%",
                TorrentState.Starting or TorrentState.FetchingHashes or TorrentState.Metadata => "Starting…",
                _ => _tPeers == 0 && _tSpeed < 1 && !m.Complete ? "Finding peers…" : null,
            };
        }
        else
        {
            bool slow = (DateTime.UtcNow - _tStarted).TotalSeconds > 60;
            _tPhase = _tPeers > 0 ? "Getting the torrent's details…"
                : slow ? "No peers found yet - the torrent may be dead, or your network may block torrents" : "Finding peers…";
        }
    }

    private async Task FinishTorrentAsync(TorrentManager? manager, bool completed)
    {
        bool seed = completed && !_removed && _mgr.Config.TorrentSeed;
        if (manager != null)
        {
            if (seed) { lock (_lock) _tSeeding = true; }                    // keep it in the engine: it uploads until Utylix closes
            else await TorrentService.ReleaseAsync(Id, manager, deleteFiles: _removed && !completed);
        }
        bool raise = false;
        lock (_lock)
        {
            _speed = 0; _tSpeed = 0; _tPeers = 0; _tPhase = null;
            if (_removed) return;
            if (_failed != null) { _status = DlStatus.Error; _error = _failed; }
            else if (completed)
            {
                _status = DlStatus.Completed;
                _tDone = _tSize;
                raise = true;
            }
            else if (_status == DlStatus.Downloading) _status = DlStatus.Paused;
        }
        _mgr.Save();
        if (raise) _mgr.RaiseCompleted(this);
    }

    /// <summary>The finished torrent's files: a file, or the folder of a multi-file torrent.</summary>
    private void RemoveTorrent(bool deleteFile, bool completed)
    {
        // a torrent still running or seeding stops (and deletes what it had) on its own, see FinishTorrentAsync
        if (TorrentService.TryGet(Id, out var m)) { _ = TorrentService.ReleaseAsync(Id, m, deleteFiles: deleteFile || !completed); }
        else if (completed && deleteFile && FileName != null)
        {
            string path = Path.Combine(Dir, FileName);
            try { if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        else if (!completed && FileName != null)
        {
            string path = Path.Combine(Dir, FileName);
            try { if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (_torrentFile != null) TryDelete(_torrentFile);
    }
}
