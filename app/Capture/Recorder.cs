using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using IdmClone.Engine;

namespace IdmClone;

/// <param name="Area">The part of the screen to record, in screen pixels.</param>
/// <param name="Quality">"low", "normal" or "high".</param>
/// <param name="Microphone">null = no microphone, "" = the default one, otherwise a device id.</param>
/// <param name="Format">"mp4" or "gif".</param>
public sealed record RecordOptions(Int32Rect Area, int Fps, string Quality, bool System, string? Microphone, bool Cursor, string Format, string OutDir);

/// <summary>
/// Screen recording. ffmpeg films the screen (gdigrab) and packs it, the <see cref="AudioMixer"/> feeds it the sound through a pipe.
/// ffmpeg can't pause, so every stretch between Start/Resume and Pause/Stop is its own short file; Stop glues them together
/// (no re-encoding) and, for a GIF, turns the result into an animation.
/// </summary>
public sealed class Recorder
{
    public enum RecState { Idle, Recording, Paused, Finishing }

    private sealed class Segment
    {
        public Process Process = null!;
        public AudioMixer? Mixer;
        public NamedPipeServerStream? Pipe;
        public string Path = "";
        public readonly Queue<string> Errors = new();
        public bool StoppedByUs;

        public string LastError { get { lock (Errors) return string.Join(" ", Errors); } }
    }

    /// <summary>
    /// A Windows "job": everything in it is ended by Windows when Utylix ends, however it ends (Task Manager, a crash, a shutdown).
    /// Without it an ffmpeg that is filming the screen would go on forever and fill the disk.
    /// </summary>
    private static class KillWithUtylix
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits { public long PerProcessUserTime, PerJobUserTime; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters { public ulong A, B, C, D, E, F; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, uint size);
        [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        private static IntPtr _job;

        public static void Add(Process process)
        {
            try
            {
                if (_job == IntPtr.Zero)
                {
                    _job = CreateJobObject(IntPtr.Zero, null);
                    var limits = new ExtendedLimits { Basic = { LimitFlags = 0x2000 } };        // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    if (_job == IntPtr.Zero || !SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) { _job = IntPtr.Zero; return; }
                }
                AssignProcessToJobObject(_job, process.Handle);
            }
            catch (Exception) { /* a nice-to-have: recording works without it */ }
        }
    }

    /// <summary>Remove what an earlier run left behind if it was killed while recording (those half files can't be played).</summary>
    public static void CleanLeftovers()
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(App.DataDir, "recording-*"))
                try { Directory.Delete(dir, true); } catch (Exception) { }
        }
        catch (Exception) { /* nothing to clean */ }
    }

    private RecordOptions _options = null!;
    private string _temp = "";
    private readonly List<string> _parts = new();
    private Segment? _segment;
    private DateTime _segmentStart;
    private TimeSpan _before;               // recorded before the current stretch (earlier stretches)
    private readonly object _gate = new();

    public RecState State { get; private set; } = RecState.Idle;
    /// <summary>Something that didn't work but did not stop the recording (e.g. "no microphone").</summary>
    public string? Warning { get; private set; }
    public event Action? StateChanged;
    /// <summary>ffmpeg stopped by itself while recording. The message says why; call <see cref="StopAsync"/> to keep what was recorded.</summary>
    public event Action<string>? Faulted;

    public TimeSpan Elapsed => State == RecState.Recording ? _before + (DateTime.UtcNow - _segmentStart) : _before;

    private void SetState(RecState state)
    {
        State = state;
        StateChanged?.Invoke();
    }

    // ---------- start / pause / resume ----------
    public async Task StartAsync(RecordOptions options)
    {
        if (State != RecState.Idle) throw new InvalidOperationException("Already recording.");
        if (!Tools.HasFfmpeg) throw new IOException("Recording needs ffmpeg. Install it from the button on the Screen Recorder tab.");
        _options = options with { Area = Even(options.Area) };
        if (_options.Area.Width < 16 || _options.Area.Height < 16) throw new IOException("That area is too small to record.");
        Directory.CreateDirectory(_options.OutDir);
        _temp = Path.Combine(App.DataDir, "recording-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_temp);
        _parts.Clear();
        _before = TimeSpan.Zero;
        Warning = null;
        try { await BeginSegmentAsync(); }
        catch { CleanUp(); throw; }
    }

    private static Int32Rect Even(Int32Rect r)
    {
        var screen = ScreenGrab.VirtualScreen;
        int x = Math.Max(r.X, screen.X), y = Math.Max(r.Y, screen.Y);
        int right = Math.Min(r.X + r.Width, screen.X + screen.Width), bottom = Math.Min(r.Y + r.Height, screen.Y + screen.Height);
        int w = (right - x) & ~1, h = (bottom - y) & ~1;      // video needs even sizes
        return new Int32Rect(x, y, Math.Max(0, w), Math.Max(0, h));
    }

    private async Task BeginSegmentAsync()
    {
        var o = _options;
        var seg = new Segment { Path = Path.Combine(_temp, $"part{_parts.Count + 1}.mp4") };
        bool wantSound = o.Format == "mp4" && (o.System || o.Microphone != null);
        string? pipeName = null;
        if (wantSound)
        {
            var mixer = new AudioMixer(o.System, o.Microphone);
            if (mixer.Problem != null) Warning = mixer.Problem;
            if (mixer.HasInput)
            {
                seg.Mixer = mixer;
                pipeName = "utylix-rec-" + Guid.NewGuid().ToString("N");
                seg.Pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 4096);      // small on purpose: the sound must not run ahead of ffmpeg
            }
            else { mixer.Dispose(); Warning ??= "No sound source could be used, so this is recorded without sound."; }
        }

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "gdigrab", "-framerate", o.Fps.ToString(CultureInfo.InvariantCulture), "-draw_mouse", o.Cursor ? "1" : "0",
            "-offset_x", o.Area.X.ToString(CultureInfo.InvariantCulture), "-offset_y", o.Area.Y.ToString(CultureInfo.InvariantCulture),
            "-video_size", $"{o.Area.Width}x{o.Area.Height}", "-i", "desktop",
        };
        if (pipeName != null)
            args.AddRange(new[]
            {
                "-f", "s16le", "-ar", AudioMixer.Rate.ToString(CultureInfo.InvariantCulture), "-ac", AudioMixer.Channels.ToString(CultureInfo.InvariantCulture),
                "-probesize", "32", "-analyzeduration", "1",
                "-i", @"\\.\pipe\" + pipeName, "-map", "0:v", "-map", "1:a",
            });
        string crf = o.Quality switch { "low" => "31", "high" => "20", _ => "25" };
        args.AddRange(new[]
        {
            "-c:v", "libx264", "-preset", o.Fps >= 60 ? "ultrafast" : "veryfast", "-crf", crf, "-pix_fmt", "yuv420p",
            "-fps_mode", "cfr", "-r", o.Fps.ToString(CultureInfo.InvariantCulture),
        });
        if (pipeName != null) args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k" });
        args.Add(seg.Path);

        var psi = new ProcessStartInfo(Tools.Ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        _segmentStart = DateTime.UtcNow;                            // ffmpeg is already filming while it sets up: count from here
        try { seg.Process = Process.Start(psi) ?? throw new IOException("ffmpeg didn't start."); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            seg.Pipe?.Dispose(); seg.Mixer?.Dispose();
            throw new IOException("ffmpeg didn't start: " + e.Message);
        }
        KillWithUtylix.Add(seg.Process);
        seg.Process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (seg.Errors) { seg.Errors.Enqueue(e.Data.Trim()); while (seg.Errors.Count > 4) seg.Errors.Dequeue(); }
        };
        seg.Process.OutputDataReceived += (_, _) => { };
        seg.Process.BeginErrorReadLine();
        seg.Process.BeginOutputReadLine();

        try
        {
            if (seg.Pipe != null)
            {
                // ffmpeg opens the pipe as soon as it starts; give it a few seconds, and stop waiting if it dies
                var connect = seg.Pipe.WaitForConnectionAsync();
                var died = seg.Process.WaitForExitAsync();
                var first = await Task.WhenAny(connect, died, Task.Delay(8000));
                if (first != connect) throw new IOException(SegmentFailure(seg, "ffmpeg didn't open the sound stream."));
                seg.Mixer!.Start(seg.Pipe);
            }
            await Task.Delay(700);                                  // a wrong area or a broken setup makes ffmpeg quit right away
            if (seg.Process.HasExited) throw new IOException(SegmentFailure(seg, "ffmpeg stopped right after starting."));
        }
        catch
        {
            await EndSegmentAsync(seg);
            throw;
        }

        _segment = seg;
        _ = Task.Run(async () =>
        {
            await seg.Process.WaitForExitAsync();
            if (!seg.StoppedByUs && ReferenceEquals(_segment, seg) && State == RecState.Recording)
                Faulted?.Invoke(SegmentFailure(seg, "ffmpeg stopped unexpectedly."));
        });
        SetState(RecState.Recording);
    }

    private static string SegmentFailure(Segment seg, string fallback)
    {
        string err = seg.LastError;
        return err.Length > 0 ? err : fallback;
    }

    /// <summary>Ask ffmpeg to finish its file properly, then tidy up.</summary>
    private static async Task EndSegmentAsync(Segment seg)
    {
        seg.StoppedByUs = true;
        try
        {
            if (!seg.Process.HasExited)
            {
                try { seg.Process.StandardInput.Write("q"); seg.Process.StandardInput.Flush(); } catch (Exception) { }
                var exited = seg.Process.WaitForExitAsync();
                if (await Task.WhenAny(exited, Task.Delay(15000)) != exited)
                    try { seg.Process.Kill(true); } catch (Exception) { }
            }
        }
        finally
        {
            try { seg.Pipe?.Dispose(); } catch (Exception) { }         // also frees the sound thread if it was waiting to write
            try { seg.Mixer?.Stop(); } catch (Exception) { }
            try { seg.Process.Dispose(); } catch (Exception) { }
        }
    }

    public async Task PauseAsync()
    {
        if (State != RecState.Recording || _segment is not { } seg) return;
        _before += DateTime.UtcNow - _segmentStart;
        _segment = null;
        SetState(RecState.Paused);
        await EndSegmentAsync(seg);
        if (File.Exists(seg.Path) && new FileInfo(seg.Path).Length > 0) _parts.Add(seg.Path);
    }

    public async Task ResumeAsync()
    {
        if (State != RecState.Paused) return;
        await BeginSegmentAsync();
    }

    // ---------- stop ----------
    /// <summary>Finish the recording and return the path of the finished file.</summary>
    public async Task<string> StopAsync()
    {
        if (State is RecState.Idle or RecState.Finishing) throw new InvalidOperationException("Not recording.");
        var seg = _segment;
        if (seg != null) _before += DateTime.UtcNow - _segmentStart;
        _segment = null;
        SetState(RecState.Finishing);
        string? final = null;
        try
        {
            if (seg != null)
            {
                await EndSegmentAsync(seg);
                if (File.Exists(seg.Path) && new FileInfo(seg.Path).Length > 0) _parts.Add(seg.Path);
            }
            if (_parts.Count == 0) throw new IOException("Nothing was recorded.");

            string ext = _options.Format == "gif" ? "gif" : "mp4";
            final = ImageConverter.ReserveName(_options.OutDir, "Utylix Record " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture), ext);

            string video = _parts[0];
            if (_parts.Count > 1)
            {
                string list = Path.Combine(_temp, "list.txt");
                File.WriteAllLines(list, _parts.Select(p => "file '" + p.Replace("\\", "/").Replace("'", "'\\''") + "'"));
                video = Path.Combine(_temp, "joined.mp4");
                await RunFfmpegAsync(new[] { "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", video });
            }

            if (_options.Format == "gif")
            {
                int fps = Math.Min(_options.Fps, 15);
                await RunFfmpegAsync(new[]
                {
                    "-i", video, "-vf", $"fps={fps},scale='min(800,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4",
                    "-loop", "0", final,
                });
            }
            else
            {
                await RunFfmpegAsync(new[] { "-i", video, "-c", "copy", "-movflags", "+faststart", final });     // "fast start": plays at once when shared or opened from a browser
            }
            return final;
        }
        catch
        {
            try { if (final != null && File.Exists(final) && new FileInfo(final).Length == 0) File.Delete(final); } catch (Exception) { }     // the name was only reserved
            throw;
        }
        finally
        {
            CleanUp();
            SetState(RecState.Idle);
        }
    }

    /// <summary>Stop right now (the program is closing), keeping what was recorded. Never throws.</summary>
    public void StopForExit()
    {
        try
        {
            if (State is RecState.Idle or RecState.Finishing) return;
            Task.Run(StopAsync).Wait(TimeSpan.FromSeconds(40));
        }
        catch (Exception) { /* closing anyway */ }
    }

    private static async Task RunFfmpegAsync(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(Tools.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" }.Concat(args)) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new IOException("ffmpeg didn't start.");
        var errorText = proc.StandardError.ReadToEndAsync();
        _ = proc.StandardOutput.ReadToEndAsync();
        proc.StandardInput.Close();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0) throw new IOException("Couldn't finish the video: " + (await errorText).Trim().Split('\n').LastOrDefault()?.Trim());
    }

    private void CleanUp()
    {
        try { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); } catch (Exception) { }
    }
}
