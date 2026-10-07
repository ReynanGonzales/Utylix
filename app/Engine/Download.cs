using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace IdmClone.Engine;

/// <summary>Awaiting = caught from the browser, waiting for the user to confirm in the capture window.</summary>
public enum DlStatus { Queued, Downloading, Paused, Completed, Error, Awaiting }

public sealed class Segment
{
    public long Start, End, Pos;
    public long Remaining => End - Pos + 1;
}

/// <summary>Immutable snapshot for the UI / API (record equality lets the UI skip unchanged rows).</summary>
public sealed record DownloadInfo(
    string Id, string Url, string FileName, long Size, long Downloaded, DlStatus Status,
    string? Error, long Speed, long? Eta, bool Resumable, DateTime Created, int Connections, string? FilePath,
    // ---- extra detail shown under "Show more details" ----
    /// <summary>"start:end:pos;..." for each connection's part of the file (compact string so records compare cheaply).</summary>
    string Segments, string SavePath, string? ContentType, int Retries, long AvgSpeed,
    /// <summary>true for YouTube/Facebook/... downloads run through yt-dlp; Phase says what it is doing right now.</summary>
    bool IsMedia = false, string? Phase = null,
    /// <summary>A torrent or magnet link (peers are in Connections).</summary>
    bool IsTorrent = false, int Seeds = 0, long Uploaded = 0);

public sealed class DownloadState
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public Dictionary<string, string> Headers { get; set; } = new();
    public string? Hint { get; set; }
    public string? ContentType { get; set; }
    public string Dir { get; set; } = "";
    public int Connections { get; set; } = 8;
    public string? FileName { get; set; }
    public long? Size { get; set; }
    public bool Resumable { get; set; }
    public string Status { get; set; } = nameof(DlStatus.Paused);
    public string? Error { get; set; }
    public DateTime Created { get; set; }
    public List<long[]> Segments { get; set; } = new();
    public string? MediaOption { get; set; }
    public string? MediaTitle { get; set; }
    public string? MediaReferer { get; set; }
    public string? ConvertTo { get; set; }
    public bool KeepOriginal { get; set; }
    public string? AfterDone { get; set; }
    public bool IsTorrent { get; set; }
    public string? TorrentFile { get; set; }
}

/// <summary>
/// A segmented, resumable download. The file is split into byte ranges fetched in parallel; a
/// connection that finishes early steals half of the biggest remaining range so all stay busy.
/// </summary>
public sealed partial class Download
{
    public const string PartSuffix = ".idmpart";
    private const int Chunk = 128 * 1024;
    private const long MinSegment = 1024 * 1024;
    private const long MinSteal = 2 * 1024 * 1024;
    private const int MaxRetries = 6;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(20);
    private const long UnknownEnd = 1L << 62;

    private readonly Manager _mgr;
    private readonly object _lock = new();
    private Dictionary<string, string> _headers;
    private string? _hint;
    private readonly int _connections;
    private Task? _probeTask;
    private bool _dirLocked;        // true once the user picked the folder in the prompt
    private string? _contentType;
    private int _retries;
    private double _sessionSeconds; // time spent actually transferring since the last (re)start
    private long _sessionDone0;     // bytes we already had when this run started
    private long _sessionBytes;     // bytes fetched in this run
    private List<Segment> _segments = new();
    private readonly List<Segment> _orphans = new();   // parts whose connection was refused by the server's connection limit: the others take them over
    private int _active;                               // connections working right now
    /// <summary>What a server showed it allows (some answer "403" to the 4th connection at once): remembered per host while Utylix runs.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> HostCap = new(StringComparer.OrdinalIgnoreCase);
    private long? _size;                 // null = not probed yet, -1 = unknown length
    private bool _resumable;
    private DlStatus _status = DlStatus.Queued;
    private string? _error;
    private CancellationTokenSource _cts = new();
    private Task? _task;
    private SafeFileHandle? _fh;
    private bool _removed;
    private string? _failed;
    private double _speed;
    private long _lastDone;
    private DateTime _lastT;

    public string Id { get; }
    public string Url { get; }
    public string Dir { get; private set; }
    public string? FileName { get; private set; }
    public DateTime Created { get; private set; } = DateTime.UtcNow;

    public Download(Manager mgr, string url, Dictionary<string, string>? headers, string? hint,
                    string dir, int connections, string? id = null)
    {
        _mgr = mgr;
        Id = id ?? Guid.NewGuid().ToString("N")[..10];
        Url = url;
        _headers = headers ?? new();
        _hint = hint;
        Dir = dir;
        _connections = connections;
    }

    public static Download FromState(Manager mgr, DownloadState st)
    {
        var d = new Download(mgr, st.Url, st.Headers, st.Hint, st.Dir, st.Connections, st.Id)
        {
            FileName = st.FileName,
            Created = st.Created,
            _size = st.Size,
            _contentType = st.ContentType,
            _resumable = st.Resumable,
            _error = st.Error,
            _mediaOption = st.MediaOption,
            _mediaTitle = st.MediaTitle,
            _mediaReferer = st.MediaReferer,
            _convertTo = st.ConvertTo,
            _keepOriginal = st.KeepOriginal,
            _afterDone = st.AfterDone,
            _segments = st.Segments.Select(s => new Segment { Start = s[0], End = s[1], Pos = s[2] }).ToList(),
            _torrent = st.IsTorrent,
            _torrentFile = st.TorrentFile,
        };
        if (d._torrent)
        {
            d._tSize = st.Size ?? 0;
            if (st.Segments.Count == 1) d._tDone = st.Segments[0][2];
            d._segments = new();
        }
        d._status = Enum.TryParse<DlStatus>(st.Status, out var s) ? s : DlStatus.Paused;
        if (d._status is DlStatus.Queued or DlStatus.Downloading) d._status = DlStatus.Paused;
        return d;
    }

    public DownloadState ToState()
    {
        lock (_lock)
            return new DownloadState
            {
                Id = Id, Url = Url, Headers = _headers, Hint = _hint, ContentType = _contentType, Dir = Dir, Connections = _connections,
                FileName = FileName, Size = _size, Resumable = _resumable, Status = _status.ToString(),
                Error = _error, Created = Created, MediaOption = _mediaOption, MediaTitle = _mediaTitle, MediaReferer = _mediaReferer,
                ConvertTo = _convertTo, KeepOriginal = _keepOriginal, AfterDone = _afterDone,
                Segments = _torrent ? new() { new[] { 0L, Math.Max(0, _tSize - 1), _tDone } } : _segments.Select(s => new[] { s.Start, s.End, s.Pos }).ToList(),
                IsTorrent = _torrent, TorrentFile = _torrentFile,
            };
    }

    public DlStatus Status { get { lock (_lock) return _status; } }
    public string? FinalPathOrNull { get { lock (_lock) return FileName == null ? null : Path.Combine(Dir, FileName); } }
    private string FinalPath => Path.Combine(Dir, FileName!);
    private string PartPath => Path.Combine(Dir, FileName + PartSuffix);
    public string? Referer { get { lock (_lock) return _headers.GetValueOrDefault("Referer"); } }

    public DownloadInfo Info()
    {
        lock (_lock)
        {
            if (_torrent) return TorrentInfo();
            if (_mediaOption != null) return MediaInfo();
            long size = _size ?? -1;
            long done = _status == DlStatus.Completed ? size : _segments.Sum(s => s.Pos - s.Start);
            if (size > 0) done = Math.Min(done, size);
            long? eta = _status == DlStatus.Downloading && _speed > 1 && size > 0
                ? (long)((size - done) / _speed) : null;
            string name = FileName ?? _hint
                ?? Uri.UnescapeDataString(Url[(Url.LastIndexOf('/') + 1)..].Split('?', '#')[0]);
            if (name.Length == 0) name = Url;
            string segments = _segments.Count == 0 ? "" : string.Join(';', _segments.Select(s => $"{s.Start}:{s.End}:{s.Pos}"));
            string savePath = FileName == null ? Dir : Path.Combine(Dir, FileName);
            long avg = _sessionSeconds > 1.5 ? (long)(_sessionBytes / _sessionSeconds) : 0;
            return new DownloadInfo(Id, Url, name, size, done, _status, _error,
                _status == DlStatus.Downloading ? (long)_speed : 0, eta, _resumable, Created,
                _status == DlStatus.Downloading ? _segments.Count(s => s.Remaining > 0) : 0,
                _status == DlStatus.Completed ? FinalPath : null,
                segments, savePath, _contentType, _retries, avg);
        }
    }

    // ---------- control ----------
    public bool CanLaunch => _task == null || _task.IsCompleted;

    public void Launch()
    {
        lock (_lock)
        {
            var cts = _cts = new CancellationTokenSource();
            _failed = null;
            _error = null;
            _status = DlStatus.Downloading;
            _speed = 0;
            _lastT = DateTime.UtcNow;
            _lastDone = 0;
            _retries = 0;
            _sessionSeconds = 0;
            _sessionBytes = 0;
            _sessionDone0 = _segments.Sum(s => s.Pos - s.Start);
            _task = Task.Run(() => RunAsync(cts));
        }
    }

    public void Pause()
    {
        lock (_lock)
            if (_status is DlStatus.Downloading or DlStatus.Queued)
            {
                _status = DlStatus.Paused;
                _cts.Cancel();
            }
    }

    public void Resume()
    {
        lock (_lock)
            if (_status is DlStatus.Paused or DlStatus.Error) _status = DlStatus.Queued;
    }

    public void Remove(bool deleteFile)
    {
        bool running, completed;
        lock (_lock)
        {
            _removed = true;
            _cts.Cancel();
            running = _task is { IsCompleted: false };
            completed = _status == DlStatus.Completed;
        }
        if (IsTorrent) { if (!running) RemoveTorrent(deleteFile, completed); return; }   // a running one stops itself, see FinishTorrentAsync
        if (IsMedia && !completed && !running) CleanMediaTemp();   // a running one cleans up after itself
        if (FileName == null) return;
        if (completed && deleteFile) TryDelete(FinalPath);
        else if (!completed && !running) TryDelete(PartPath);   // a running one cleans up after itself
    }

    // ---------- capture confirmation (the "New download" window) ----------
    /// <summary>Ask the server about the file (name, size, Range support) without starting the transfer.</summary>
    public void BeginProbe()
    {
        if (IsMedia || IsTorrent) return;         // a video-site download has nothing to probe: the page address isn't the file
        var ct = _cts.Token;
        _probeTask = Task.Run(async () =>
        {
            try { await ProbeAsync(ct); }
            catch (Exception e) { ProbeError = Net.Describe(e); }
        });
    }

    public bool ProbeDone => _probeTask?.IsCompleted ?? true;
    public string? ProbeError { get; private set; }
    public bool Probed { get { lock (_lock) return _size != null; } }

    /// <summary>Apply the name/folder the user picked. Nothing has been written to disk yet.</summary>
    public void SetTarget(string fileName, string dir)
    {
        var reserved = _mgr.ReservedNames(this);   // manager lock first, never while holding ours
        lock (_lock)
        {
            Dir = dir;
            _dirLocked = true;     // the user chose this folder: don't re-sort by type
            _hint = Util.Sanitize(fileName);
            if (_size != null)   // already probed: re-reserve a free name in the (possibly new) folder
                FileName = Util.UniqueName(dir, _hint, reserved);
        }
    }

    public void MarkAwaiting() { lock (_lock) _status = DlStatus.Awaiting; }
    public void Begin() { lock (_lock) if (_status == DlStatus.Awaiting) _status = DlStatus.Queued; }
    public void Later() { lock (_lock) if (_status == DlStatus.Awaiting) _status = DlStatus.Paused; }

    public void WaitStopped(int ms) { try { _task?.Wait(ms); } catch (Exception) { } }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception) { } }

    // ---------- probing / preparing ----------
    private async Task ProbeAsync(CancellationToken ct, bool ranged = true)
    {
        var resp = await Net.SendAsync(Url, _headers, ranged ? "bytes=0-0" : null, ct);
        using (resp)
        {
            if (ranged && resp.StatusCode is HttpStatusCode.RequestedRangeNotSatisfiable or HttpStatusCode.Forbidden or HttpStatusCode.MethodNotAllowed or HttpStatusCode.BadRequest)
            {                                                    // (some servers turn a Range request away: look again without it)
                resp.Dispose();
                await ProbeAsync(ct, false);
                return;
            }
            Net.EnsureSuccess(resp);
            long total;
            bool resumable;
            if (resp.StatusCode == HttpStatusCode.PartialContent)
            {
                total = resp.Content.Headers.ContentRange?.Length ?? -1;
                resumable = total > 0;
            }
            else
            {
                total = resp.Content.Headers.ContentLength ?? -1;
                resumable = false;
            }
            string name = Util.Sanitize(_hint ?? Util.FileNameFromResponse(resp, resp.RequestMessage?.RequestUri ?? new Uri(Url)));
            name = Util.FixPictureName(name, resp.Content.Headers.ContentType?.MediaType);      // "photo.img" that the server calls image/png -> photo.png

            // Ask the manager BEFORE taking our own lock: the scheduler holds the manager's lock while it
            // reads our status, so taking the two locks in the opposite order here could deadlock.
            string chosen;
            lock (_lock) chosen = _dirLocked ? Dir : "";
            string dir = chosen.Length > 0 ? chosen : _mgr.DirFor(name);   // no folder picked by the user: sort by file type
            Directory.CreateDirectory(dir);
            var reserved = _mgr.ReservedNames(this);
            lock (_lock)
            {
                if (!_dirLocked) Dir = dir;
                FileName = Util.UniqueName(Dir, name, reserved);
                _size = total;
                _resumable = resumable;
                _contentType = resp.Content.Headers.ContentType?.MediaType;
                FreshSegments();
            }
        }
    }

    private string HostKey() { try { return new Uri(Url).Host; } catch (UriFormatException) { return ""; } }

    /// <summary>How many connections to open at once: the setting, or less if this server already turned some away.</summary>
    private int Allowed() => HostCap.TryGetValue(HostKey(), out int cap) ? Math.Max(1, Math.Min(_connections, cap)) : _connections;

    private void FreshSegments()
    {
        long total = _size ?? -1;
        if (_resumable)
        {
            int n = (int)Math.Max(1, Math.Min(Allowed(), total / MinSegment));
            long step = total / n;
            _segments = Enumerable.Range(0, n).Select(i => new Segment
            {
                Start = i * step, Pos = i * step, End = i < n - 1 ? (i + 1) * step - 1 : total - 1,
            }).ToList();
        }
        else _segments = new() { new Segment { Start = 0, Pos = 0, End = total > 0 ? total - 1 : UnknownEnd } };
    }

    private void PrepareFile()
    {
        long size = _size ?? -1;
        bool progress = _segments.Any(s => s.Pos > s.Start);
        bool ok = File.Exists(PartPath) && (size <= 0 || new FileInfo(PartPath).Length == size);
        if (_resumable && ok) return;
        if (progress || !_resumable) FreshSegments();
        using var f = File.Create(PartPath);
        if (size > 0) f.SetLength(size);
    }

    // ---------- transfer ----------
    private Segment? Steal()
    {
        if (!_resumable) return null;
        lock (_lock)
        {
            Segment? best = null;
            foreach (var s in _segments) if (best == null || s.Remaining > best.Remaining) best = s;
            if (best == null || best.Remaining < MinSteal) return null;
            long mid = best.Pos + best.Remaining / 2;
            var created = new Segment { Start = mid, Pos = mid, End = best.End };
            best.End = mid - 1;
            _segments.Add(created);
            return created;
        }
    }

    private async Task FetchAsync(Segment seg, CancellationToken ct)
    {
        bool ranged = _resumable;
        long start = 0, end = 0;
        lock (_lock)
        {
            if (ranged)
            {
                if (seg.Remaining <= 0) return;
                start = seg.Pos;
                end = seg.End;
            }
            else
            {
                seg.Pos = 0;
                RandomAccess.SetLength(_fh!, 0);
            }
        }
        using var resp = await Net.SendAsync(Url, _headers, ranged ? $"bytes={start}-{end}" : null, ct);
        Net.EnsureSuccess(resp);
        if (ranged)
        {
            if (resp.StatusCode != HttpStatusCode.PartialContent)
                throw new FatalException("Server stopped honouring Range requests");
            if (resp.Content.Headers.ContentRange?.From != start)
                throw new FatalException("Server returned the wrong byte range");
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buf = ArrayPool<byte>.Shared.Rent(Chunk);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int want = Chunk;
                if (ranged)
                    lock (_lock)
                    {
                        long room = seg.Remaining;
                        if (room <= 0) return;
                        want = (int)Math.Min(Chunk, room);
                    }
                int n;
                using (var rc = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    rc.CancelAfter(ReadTimeout);
                    n = await stream.ReadAsync(buf.AsMemory(0, want), rc.Token);
                }
                if (n == 0) break;
                lock (_lock)
                {
                    int len = n;
                    if (ranged)
                    {
                        long room = seg.Remaining;   // another connection may have taken our tail
                        if (room <= 0) return;
                        len = (int)Math.Min(len, room);
                    }
                    RandomAccess.Write(_fh!, buf.AsSpan(0, len), seg.Pos);
                    seg.Pos += len;
                }
            }
            if (ct.IsCancellationRequested) return;
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }

        lock (_lock)   // clean end of stream
        {
            if (ranged)
            {
                if (seg.Remaining > 0) throw new IOException("Connection closed early");
            }
            else
            {
                if (_size > 0 && seg.Pos != _size) throw new IOException("Connection closed early");
                seg.End = seg.Pos - 1;
                _size = seg.Pos;
            }
        }
    }

    /// <summary>Called when a connection has finished its part: the next part to do (a refused one first), or null (and this connection is done).</summary>
    private Segment? NextSegment()
    {
        lock (_lock)
        {
            Segment? next = null;
            int i = _orphans.FindIndex(o => o.Remaining > 0);
            if (i >= 0) { next = _orphans[i]; _orphans.RemoveAt(i); }
            next ??= Steal();
            if (next == null) _active--;
            return next;
        }
    }

    /// <summary>
    /// The server refused this connection ("403", "429", "503") while others work: it limits connections per person. Stop using this one,
    /// leave its part for the others, and remember the number that works. False when this is the only connection (then it is a real refusal).
    /// </summary>
    private bool TryShed(Segment seg)
    {
        lock (_lock)
        {
            if (_active <= 1) return false;
            _active--;
            _orphans.Add(seg);
            HostCap[HostKey()] = Math.Max(1, _active);
            return true;
        }
    }

    private async Task WorkerAsync(Segment? seg, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        int fails = 0;
        while (seg != null && !ct.IsCancellationRequested)
        {
            try
            {
                await FetchAsync(seg, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                if (e is HttpStatusException { Code: 403 or 429 or 503 } && TryShed(seg)) return;
                fails++;
                Interlocked.Increment(ref _retries);
                if (Net.IsFatal(e) || fails > MaxRetries)
                {
                    _failed = Net.Describe(e);
                    cts.Cancel();
                    return;
                }
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 << fails, 15)), ct); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            fails = 0;
            if (ct.IsCancellationRequested) return;
            seg = NextSegment();
        }
    }

    private bool AllDone() { lock (_lock) return _segments.Count > 0 && _segments.All(s => s.Remaining <= 0); }

    private async Task RunAsync(CancellationTokenSource cts)
    {
        if (IsTorrent) { await RunTorrentAsync(cts); return; }
        if (IsMedia) { await RunMediaAsync(cts); return; }   // video sites: yt-dlp does the work (Download.Media.cs)
        try
        {
            if (_size == null)
            {
                await ProbeAsync(cts.Token);
                _mgr.Save();
            }
            if (_size == 0)
            {
                File.WriteAllBytes(PartPath, Array.Empty<byte>());
                lock (_lock) _segments = new() { new Segment { Start = 0, Pos = 0, End = -1 } };
            }
            else PrepareFile();
            _fh = File.OpenHandle(PartPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            await TransferAsync(cts);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception e) { _failed = Net.Describe(e); }
        finally
        {
            lock (_lock) { _fh?.Dispose(); _fh = null; }
            Finish();
        }
    }

    private async Task TransferAsync(CancellationTokenSource cts)
    {
        List<Segment> todo;
        lock (_lock)
        {
            todo = _segments.Where(s => s.Remaining > 0).ToList();
            int allowed = Allowed();
            while (_resumable && todo.Count > 0 && todo.Count < allowed)
            {
                var created = Steal();
                if (created == null) break;
                todo.Add(created);
            }
            _orphans.Clear();
            if (todo.Count > allowed) { _orphans.AddRange(todo.Skip(allowed)); todo = todo.Take(allowed).ToList(); }     // more parts than this server lets us open
            _active = todo.Count;
        }
        var all = Task.WhenAll(todo.Select(s => Task.Run(() => WorkerAsync(s, cts))));
        var lastSave = DateTime.UtcNow;
        while (!all.IsCompleted)
        {
            await Task.WhenAny(all, Task.Delay(400));
            var now = DateTime.UtcNow;
            lock (_lock)
            {
                long done = _segments.Sum(s => s.Pos - s.Start);
                double dt = (now - _lastT).TotalSeconds;
                if (dt > 0)
                {
                    double inst = Math.Max(0, (done - _lastDone) / dt);
                    _speed = _speed == 0 ? inst : 0.7 * _speed + 0.3 * inst;
                }
                if (dt > 0)
                {
                    _sessionSeconds += dt;
                    _sessionBytes = Math.Max(0, done - _sessionDone0);
                }
                _lastT = now;
                _lastDone = done;
            }
            if ((now - lastSave).TotalSeconds > 2)
            {
                lastSave = now;
                _mgr.Save();
            }
        }
    }

    /// <summary>
    /// The server did not say what the file is, but its first bytes say "picture": a photo called ".img"/".bin" must not be
    /// filed as a disk image or program. Renames it (and moves it to the Picture folder unless the person chose a folder).
    /// Runs before Finish takes our lock, because choosing a folder asks the manager, which must never happen under it.
    /// </summary>
    private void RenameIfPicture()
    {
        string name, part, dir;
        bool locked;
        lock (_lock)
        {
            if (_removed || _failed != null || FileName == null || !AllDone()) return;
            name = FileName; part = PartPath; dir = Dir; locked = _dirLocked;
        }
        if (Util.RealPictureEnding(name, part) is not { } ending) return;
        string newName = Util.WithEnding(name, ending);
        string newDir = locked ? dir : _mgr.DirFor(newName);
        var reserved = _mgr.ReservedNames(this);
        try
        {
            Directory.CreateDirectory(newDir);
            lock (_lock)
            {
                if (FileName != name) return;
                string unique = Util.UniqueName(newDir, newName, reserved);
                File.Move(part, Path.Combine(newDir, unique + PartSuffix));
                Dir = newDir;
                FileName = unique;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* keep the old name: the download itself is fine */ }
    }

    private void Finish()
    {
        RenameIfPicture();
        bool completed = false;
        lock (_lock)
        {
            _speed = 0;
            if (_removed)
            {
                if (_status != DlStatus.Completed && FileName != null) TryDelete(PartPath);
                return;
            }
            if (_failed != null) { _status = DlStatus.Error; _error = _failed; }
            else if (AllDone())
            {
                try { Complete(); completed = true; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _status = DlStatus.Error;
                    _error = "Could not save file: " + e.Message;
                }
            }
            else if (_status == DlStatus.Downloading) _status = DlStatus.Paused;
        }
        if (completed) ConvertIfWanted();     // outside the lock: it can take a moment
        _mgr.Save();
        if (completed) _mgr.RaiseCompleted(this);
    }

    private void Complete()
    {
        string part = PartPath;                                             // (taken before the name can change below)
        if (File.Exists(FinalPath)) FileName = Util.UniqueName(Dir, FileName!);
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(part, FinalPath, true); break; }
            catch (Exception e) when (attempt < 4 && e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);   // antivirus can briefly lock a fresh file
            }
        }
        Util.MarkOfTheWeb(FinalPath, Url, _headers.GetValueOrDefault("Referer"));
        if (_size is not > 0) _size = new FileInfo(FinalPath).Length;
        _status = DlStatus.Completed;
        _segments.Clear();
        _headers = new();   // don't keep cookies around once we no longer need them
    }
}
