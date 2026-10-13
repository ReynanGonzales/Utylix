using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IdmClone.Engine;
using NAudio.Wave;

namespace IdmClone.Vid;

/// <summary>
/// The preview. A single frame (paused, scrubbing) or continuous playback comes from the SAME filter graph the export uses, run by ffmpeg at a
/// small size: the video goes through one pipe and the sound through another; the sound device is the clock the frames are shown by.
/// </summary>
public sealed class VidPlayer : IDisposable
{
    private readonly VidSession _session;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _timer;
    private int _generation;                                    // every start / stop / seek makes the older work obsolete
    private Process? _video, _audio;
    private FfJob? _videoJob, _audioJob;
    private WasapiOut? _out;
    private BufferedWaveProvider? _buffer;
    private MeterProvider? _meter;
    private readonly ConcurrentQueue<(double Pts, byte[] Data)> _frames = new();
    private double _from;                                       // timeline time where this playback began
    private double _end;
    private double _fps = 30;
    private bool _videoDone, _audioDone;
    private readonly Stopwatch _wall = new();                   // the clock when there is no sound device
    private long _audioStartBytes;
    private long _audioBytesWritten;
    private bool _clockStarted;
    private CancellationTokenSource? _scrubCts;
    private DateTime _lastScrub = DateTime.MinValue;

    public const int MaxPreviewWidth = 960;

    public WriteableBitmap? Bitmap { get; private set; }
    public int PreviewWidth { get; private set; }
    public int PreviewHeight { get; private set; }
    public bool IsPlaying { get; private set; }
    /// <summary>The picture control should show this bitmap (it changed because the canvas size changed).</summary>
    public event Action? BitmapChanged;
    /// <summary>The time on the timeline moved because of playback.</summary>
    public event Action<double>? Position;
    /// <summary>Playing stopped by itself (the end was reached) or because of an error.</summary>
    public event Action? Stopped;
    public event Action<string>? Problem;
    /// <summary>The loudness (0..1) of what is being played, left and right, about 120 times a second while playing.</summary>
    public event Action<double, double>? Levels;

    public VidPlayer(VidSession session)
    {
        _session = session;
        _ui = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Render, _ui) { Interval = TimeSpan.FromMilliseconds(8) };
        _timer.Tick += (_, _) => Tick();
        EnsureBitmap();
    }

    /// <summary>1 = as big as the program monitor needs (up to 960 wide), 0.5 and 0.25 = quicker previews on a slow PC.</summary>
    public double PreviewScale { get; set; } = 1;

    private void EnsureBitmap()
    {
        var p = _session.Project;
        double aspect = (double)p.Width / Math.Max(1, p.Height);
        int w = (int)Math.Round(Math.Min(MaxPreviewWidth, Math.Max(160, p.Width)) * Math.Clamp(PreviewScale, 0.2, 1));
        int h = (int)Math.Round(w / aspect);
        if (h > 540 * Math.Clamp(PreviewScale, 0.2, 1)) { h = (int)Math.Round(540 * Math.Clamp(PreviewScale, 0.2, 1)); w = (int)Math.Round(h * aspect); }
        w = Math.Max(2, w / 2 * 2); h = Math.Max(2, h / 2 * 2);
        if (Bitmap != null && w == PreviewWidth && h == PreviewHeight) return;
        PreviewWidth = w; PreviewHeight = h;
        Bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr24, null);
        var black = new byte[w * h * 3];
        Bitmap.WritePixels(new Int32Rect(0, 0, w, h), black, w * 3, 0);
        BitmapChanged?.Invoke();
    }

    // ---------- one frame ----------
    /// <summary>Shows the picture at this time (paused / scrubbing). Quick successive calls only run the last one.</summary>
    public void ShowFrame(double t)
    {
        if (IsPlaying) return;
        EnsureBitmap();
        _scrubCts?.Cancel();
        var cts = _scrubCts = new CancellationTokenSource();
        int gen = ++_generation;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(35, cts.Token);                    // (a drag fires many times: wait a moment for the last one)
                var data = await RenderFrameAsync(t, cts.Token);
                if (data == null || cts.IsCancellationRequested) return;
                await _ui.InvokeAsync(() =>
                {
                    if (gen != _generation || IsPlaying || Bitmap == null || data.Length < PreviewWidth * PreviewHeight * 3) return;
                    Bitmap.WritePixels(new Int32Rect(0, 0, PreviewWidth, PreviewHeight), data, PreviewWidth * 3, 0);
                });
            }
            catch (OperationCanceledException) { }
        });
    }

    private async Task<byte[]?> RenderFrameAsync(double t, CancellationToken ct)
    {
        var project = _session.Project;
        int w = PreviewWidth, h = PreviewHeight;
        double fps = project.Fps > 0 ? project.Fps : 30;
        using var job = VidGraph.Video(project, t, t + 3.0 / fps + 0.05, w, h, "bgr24");
        var psi = NewFfmpeg(job, redirectOut: true);
        foreach (var a in new[] { "-map", "[" + job.VideoLabel + "]", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgr24", "pipe:1" }) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi);
        if (proc == null) return null;
        using var reg = ct.Register(() => { try { proc.Kill(true); } catch (InvalidOperationException) { } });
        var data = new byte[w * h * 3];
        int got = 0;
        var stream = proc.StandardOutput.BaseStream;
        while (got < data.Length)
        {
            int n = await stream.ReadAsync(data.AsMemory(got), ct);
            if (n <= 0) break;
            got += n;
        }
        try { await proc.WaitForExitAsync(ct); } catch (OperationCanceledException) { throw; }
        return got == data.Length ? data : null;
    }

    private static ProcessStartInfo NewFfmpeg(FfJob job, bool redirectOut)
    {
        var psi = new ProcessStartInfo(Tools.Ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = redirectOut, RedirectStandardError = true,
        };
        foreach (var a in job.Args) psi.ArgumentList.Add(a);
        return psi;
    }

    // ---------- playing ----------
    public void Toggle()
    {
        if (IsPlaying) Pause(); else Play();
    }

    public void Play()
    {
        if (IsPlaying) return;
        double end = _session.Project.Duration;
        if (end <= 0.05) return;
        double from = _session.Playhead;
        if (from >= end - 0.05) from = 0;
        _session.Playhead = from;
        StartAt(from);
    }

    public void Pause()
    {
        if (!IsPlaying) return;
        double now = CurrentTime();
        StopProcesses();
        IsPlaying = false;
        _timer.Stop();
        _session.Playhead = Math.Clamp(now, 0, _session.Project.Duration);
        Position?.Invoke(_session.Playhead);
        ShowFrame(_session.Playhead);
    }

    /// <summary>The project changed: while paused the picture is redrawn; while playing playback goes on from the current time with the new project.</summary>
    public void Refresh()
    {
        EnsureBitmap();
        if (IsPlaying)
        {
            double now = CurrentTime();
            StopProcesses();
            IsPlaying = false;
            if (now >= _session.Project.Duration - 0.05) { _timer.Stop(); _session.Playhead = _session.Project.Duration; Stopped?.Invoke(); return; }
            _session.Playhead = Math.Max(0, now);
            StartAt(_session.Playhead);
        }
        else ShowFrame(_session.Playhead);
    }

    private void StartAt(double from)
    {
        StopProcesses();
        EnsureBitmap();
        var project = _session.Project;
        _fps = project.Fps > 0 ? project.Fps : 30;
        _from = from;
        _end = Math.Max(project.Duration, from + 0.1);
        _videoDone = _audioDone = false;
        _clockStarted = false;
        _audioBytesWritten = 0;
        while (_frames.TryDequeue(out var f)) ArrayPool<byte>.Shared.Return(f.Data);
        int gen = ++_generation;
        int w = PreviewWidth, h = PreviewHeight;
        try
        {
            _videoJob = VidGraph.Video(project, from, _end, w, h, "bgr24");
            var vpsi = NewFfmpeg(_videoJob, redirectOut: true);
            foreach (var a in new[] { "-map", "[" + _videoJob.VideoLabel + "]", "-an", "-f", "rawvideo", "-pix_fmt", "bgr24", "-r", _fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "pipe:1" }) vpsi.ArgumentList.Add(a);
            _video = Process.Start(vpsi);

            _audioJob = VidGraph.Audio(project, from, _end);
            var apsi = NewFfmpeg(_audioJob, redirectOut: true);
            foreach (var a in new[] { "-map", "[" + _audioJob.AudioLabel + "]", "-vn", "-f", "s16le", "-ar", VidGraph.AudioRate.ToString(), "-ac", "2", "pipe:1" }) apsi.ArgumentList.Add(a);
            _audio = Process.Start(apsi);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            StopProcesses();
            Problem?.Invoke("The preview could not start: " + e.Message);
            return;
        }
        if (_video == null || _audio == null) { StopProcesses(); Problem?.Invoke("The preview could not start."); return; }

        var video = _video; var audio = _audio;
        int frameBytes = w * h * 3;
        Task.Run(() => ReadVideo(video, frameBytes, gen));
        _buffer = new BufferedWaveProvider(new WaveFormat(VidGraph.AudioRate, 16, 2)) { BufferDuration = TimeSpan.FromSeconds(4), DiscardOnBufferOverflow = false, ReadFully = true };
        Task.Run(() => ReadAudio(audio, gen));
        try
        {
            _out = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 80);
            _meter = new MeterProvider(_buffer);
            _out.Init(_meter);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            _out?.Dispose(); _out = null;                           // no sound device: the picture still plays, by the wall clock
        }
        IsPlaying = true;
        _timer.Start();
    }

    private void ReadVideo(Process proc, int frameBytes, int gen)
    {
        try
        {
            var stream = proc.StandardOutput.BaseStream;
            long k = 0;
            while (gen == _generation)
            {
                var buf = ArrayPool<byte>.Shared.Rent(frameBytes);
                int got = 0;
                while (got < frameBytes)
                {
                    int n = stream.Read(buf, got, frameBytes - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got < frameBytes) { ArrayPool<byte>.Shared.Return(buf); break; }
                _frames.Enqueue((_from + k / _fps, buf));
                k++;
                while (gen == _generation && _frames.Count > 10) Thread.Sleep(4);       // (the clock is behind: wait)
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { /* stopped */ }
        if (gen == _generation) _videoDone = true;
    }

    private void ReadAudio(Process proc, int gen)
    {
        try
        {
            var stream = proc.StandardOutput.BaseStream;
            var buf = new byte[16384];
            while (gen == _generation)
            {
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                while (gen == _generation && _buffer != null && _buffer.BufferedBytes + n > _buffer.BufferLength) Thread.Sleep(5);
                if (gen != _generation || _buffer == null) break;
                _buffer.AddSamples(buf, 0, n);
                Interlocked.Add(ref _audioBytesWritten, n);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { /* stopped */ }
        if (gen == _generation) _audioDone = true;
    }

    /// <summary>The playback clock: where on the timeline the sound (or, without a device, the wall clock) is now.</summary>
    private double CurrentTime()
    {
        if (!_clockStarted) return _from;
        if (_out != null)
        {
            long played = _out.GetPosition() - _audioStartBytes;
            return _from + Math.Max(0, played) / (double)(VidGraph.AudioRate * 4);
        }
        return _from + _wall.Elapsed.TotalSeconds;
    }

    private void Tick()
    {
        if (!IsPlaying) return;
        if (!_clockStarted)
        {
            // begin when there is enough picture and sound queued to not stutter
            bool picture = _frames.Count >= 3 || _videoDone;
            bool sound = _buffer == null || _buffer.BufferedBytes >= VidGraph.AudioRate * 4 / 4 || _audioDone;
            if (!(picture && sound)) return;
            try { _out?.Play(); } catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { _out?.Dispose(); _out = null; }
            _audioStartBytes = _out?.GetPosition() ?? 0;
            _wall.Restart();
            _clockStarted = true;
        }
        double now = CurrentTime();
        // show the newest frame that is due
        (double Pts, byte[] Data)? show = null;
        while (_frames.TryPeek(out var f) && f.Pts <= now + 0.001)
        {
            _frames.TryDequeue(out f);
            if (show != null) ArrayPool<byte>.Shared.Return(show.Value.Data);
            show = f;
        }
        if (show != null && Bitmap != null)
        {
            Bitmap.WritePixels(new Int32Rect(0, 0, PreviewWidth, PreviewHeight), show.Value.Data, PreviewWidth * 3, 0);
            ArrayPool<byte>.Shared.Return(show.Value.Data);
        }
        _session.Playhead = Math.Min(now, _end);
        Position?.Invoke(_session.Playhead);
        if (_meter != null) Levels?.Invoke(_meter.L, _meter.R);
        bool finished = (_videoDone && _frames.IsEmpty && now >= _end - 0.5 / _fps) || now >= _end + 0.25;
        if (finished)
        {
            StopProcesses();
            IsPlaying = false;
            _timer.Stop();
            _session.Playhead = _session.Project.Duration;
            Position?.Invoke(_session.Playhead);
            Stopped?.Invoke();
        }
    }

    private void StopProcesses()
    {
        _generation++;
        try { _out?.Stop(); } catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
        try { _out?.Dispose(); } catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
        _out = null; _buffer = null; _meter = null;
        foreach (var p in new[] { _video, _audio })
        {
            if (p == null) continue;
            try { if (!p.HasExited) p.Kill(true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            p.Dispose();
        }
        _video = _audio = null;
        _videoJob?.Dispose(); _audioJob?.Dispose(); _videoJob = _audioJob = null;
        while (_frames.TryDequeue(out var f)) ArrayPool<byte>.Shared.Return(f.Data);
        _clockStarted = false;
    }

    public void Dispose()
    {
        _timer.Stop();
        _scrubCts?.Cancel();
        StopProcesses();
        IsPlaying = false;
    }

    /// <summary>Passes the sound on to the device and remembers how loud the last piece was (for the meters).</summary>
    private sealed class MeterProvider : IWaveProvider
    {
        private readonly IWaveProvider _source;
        public volatile float L, R;
        public MeterProvider(IWaveProvider source) { _source = source; }
        public WaveFormat WaveFormat => _source.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            int n = _source.Read(buffer, offset, count);
            float l = 0, r = 0;
            for (int i = offset; i + 3 < offset + n; i += 4)
            {
                float a = Math.Abs((short)(buffer[i] | (buffer[i + 1] << 8))) / 32768f;
                float b = Math.Abs((short)(buffer[i + 2] | (buffer[i + 3] << 8))) / 32768f;
                if (a > l) l = a;
                if (b > r) r = b;
            }
            L = l; R = r;
            return n;
        }
    }
}
