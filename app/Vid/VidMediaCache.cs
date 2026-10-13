using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IdmClone.Engine;

namespace IdmClone.Vid;

/// <summary>Little pictures of each file for the timeline (thumbnails) and the loudness drawing of sounds (waveform), made in the background.</summary>
public sealed class VidMediaCache : IDisposable
{
    private sealed class Entry
    {
        public string Dir = "";
        public double Step = 1;                 // seconds between thumbnails
        public int Count;
        public bool ThumbsStarted, ThumbsDone, PeaksStarted;
        public byte[]? Peaks;                   // one value (0..255) per PeakStep seconds
    }

    public const double PeakStep = 0.02;

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly ConcurrentDictionary<string, BitmapSource?> _images = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _gate = new(2);
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "UtylixVid", "thumbs");

    /// <summary>Something finished: the timeline should draw again.</summary>
    public event Action? Updated;

    private Entry For(MediaItem m)
    {
        return _entries.GetOrAdd(m.Id, _ =>
        {
            string key;
            try
            {
                var fi = new FileInfo(m.Path);
                key = m.Path + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            }
            catch (IOException) { key = m.Path; }
            string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16];
            var e = new Entry { Dir = Path.Combine(Root, hash) };
            e.Step = m.Kind == MediaKind.Image ? 1 : Math.Max(1, Math.Ceiling(m.Duration / 150.0));
            e.Count = m.Kind == MediaKind.Image ? 1 : Math.Max(1, (int)Math.Ceiling(m.Duration / e.Step));
            return e;
        });
    }

    private static string Quote(string s) => s;

    // ---------- thumbnails ----------
    public ImageSource? Thumbnail(MediaItem m, double sourceTime)
    {
        if (!m.HasVideo) return null;
        var e = For(m);
        if (!e.ThumbsStarted) { e.ThumbsStarted = true; _ = Task.Run(() => MakeThumbsAsync(m, e)); }
        int index = (int)Math.Clamp(Math.Floor(sourceTime / e.Step + 0.5), 0, e.Count - 1) + 1;
        string file = Path.Combine(e.Dir, "t" + index.ToString("0000") + ".jpg");
        if (_images.TryGetValue(file, out var cached)) return cached;
        if (!File.Exists(file))
        {
            // the nearest one that exists already (the picture is still being made)
            for (int d = 1; d < 6; d++)
            {
                foreach (int i in new[] { index - d, index + d })
                {
                    string other = Path.Combine(e.Dir, "t" + i.ToString("0000") + ".jpg");
                    if (i >= 1 && i <= e.Count && File.Exists(other)) return Load(other);
                }
            }
            return null;
        }
        return Load(file);
    }

    private BitmapSource? Load(string file)
    {
        if (_images.TryGetValue(file, out var cached)) return cached;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(file);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.EndInit();
            bmp.Freeze();
            if (_images.Count > 600) _images.Clear();
            _images[file] = bmp;
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException) { return null; }
    }

    private async Task MakeThumbsAsync(MediaItem m, Entry e)
    {
        try
        {
            await _gate.WaitAsync(_cts.Token);
            try
            {
                Directory.CreateDirectory(e.Dir);
                string marker = Path.Combine(e.Dir, "done");
                if (File.Exists(marker)) { e.ThumbsDone = true; Notify(); return; }
                var psi = new ProcessStartInfo(Tools.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (var a in new[] { "-hide_banner", "-nostdin", "-v", "error", "-i", m.Path, "-an", "-sn" }) psi.ArgumentList.Add(a);
                if (m.Kind == MediaKind.Image)
                    foreach (var a in new[] { "-frames:v", "1", "-vf", "scale=-2:96", "-q:v", "4", "-y", Path.Combine(e.Dir, "t0001.jpg") }) psi.ArgumentList.Add(a);
                else
                    foreach (var a in new[] { "-vf", $"fps=1/{e.Step.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}:start_time=0,scale=-2:96", "-q:v", "5", "-fps_mode", "vfr", "-y", Path.Combine(e.Dir, "t%04d.jpg") }) psi.ArgumentList.Add(a);
                using var proc = Process.Start(psi);
                if (proc == null) return;
                try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                using var reg = _cts.Token.Register(() => { try { proc.Kill(true); } catch (InvalidOperationException) { } });
                // tell the timeline as the pictures appear (every second or so)
                var wait = proc.WaitForExitAsync(_cts.Token);
                while (!wait.IsCompleted) { await Task.WhenAny(wait, Task.Delay(1000)); Notify(); }
                await wait;
                if (proc.ExitCode == 0) File.WriteAllText(marker, "ok");
                e.ThumbsDone = true;
                Notify();
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }

    // ---------- waveform ----------
    /// <summary>The loudness over time (value 0..255 every <see cref="PeakStep"/> seconds), or null while it is being read.</summary>
    public byte[]? Peaks(MediaItem m)
    {
        if (!m.HasAudio) return null;
        var e = For(m);
        if (e.Peaks != null) return e.Peaks;
        if (!e.PeaksStarted) { e.PeaksStarted = true; _ = Task.Run(() => MakePeaksAsync(m, e)); }
        return null;
    }

    private async Task MakePeaksAsync(MediaItem m, Entry e)
    {
        try
        {
            await _gate.WaitAsync(_cts.Token);
            try
            {
                var psi = new ProcessStartInfo(Tools.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "-hide_banner", "-nostdin", "-v", "error", "-i", m.Path, "-vn", "-sn", "-ac", "1", "-ar", "4000", "-f", "s16le", "pipe:1" }) psi.ArgumentList.Add(a);
                using var proc = Process.Start(psi);
                if (proc == null) return;
                try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                using var reg = _cts.Token.Register(() => { try { proc.Kill(true); } catch (InvalidOperationException) { } });
                var peaks = new List<byte>(Math.Max(16, (int)(m.Duration / PeakStep) + 8));
                int per = (int)Math.Round(4000 * PeakStep);                  // samples per value
                var buf = new byte[per * 2 * 64];
                var stream = proc.StandardOutput.BaseStream;
                int have = 0; int peak = 0, inValue = 0;
                while (true)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(have), _cts.Token);
                    if (n <= 0) break;
                    have += n;
                    int usable = have / 2;
                    for (int i = 0; i < usable; i++)
                    {
                        int s = Math.Abs((short)(buf[2 * i] | (buf[2 * i + 1] << 8)));
                        if (s > peak) peak = s;
                        if (++inValue >= per) { peaks.Add((byte)Math.Min(255, peak / 128)); peak = 0; inValue = 0; }
                    }
                    if (have % 2 == 1) { buf[0] = buf[have - 1]; have = 1; } else have = 0;
                }
                if (inValue > 0) peaks.Add((byte)Math.Min(255, peak / 128));
                e.Peaks = peaks.ToArray();
                Notify();
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
    }

    private void Notify()
    {
        var d = Application.Current?.Dispatcher;
        d?.BeginInvoke(new Action(() => Updated?.Invoke()), System.Windows.Threading.DispatcherPriority.Background);
    }

    public void Dispose() => _cts.Cancel();
}
