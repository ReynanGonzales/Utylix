using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IdmClone.Engine;

namespace IdmClone.Vid;

public enum VidFormat { Mp4H264, Mp4H265, WebmVp9, Gif, Mp3, Wav }

public sealed class VidExportOptions
{
    public string Path { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>0 = high, 1 = medium, 2 = small file.</summary>
    public int Quality { get; set; } = 1;
    public bool Gpu { get; set; } = true;
    public VidFormat Format { get; set; } = VidFormat.Mp4H264;
    /// <summary>The part of the sequence to make; To &lt;= From means all of it.</summary>
    public double From { get; set; }
    public double To { get; set; }

    public static string Extension(VidFormat f) => f switch
    {
        VidFormat.WebmVp9 => ".webm", VidFormat.Gif => ".gif", VidFormat.Mp3 => ".mp3", VidFormat.Wav => ".wav", _ => ".mp4",
    };

    public static bool HasPicture(VidFormat f) => f is not (VidFormat.Mp3 or VidFormat.Wav);
    public static bool HasSound(VidFormat f) => f != VidFormat.Gif;
}

/// <summary>Writes the timeline to a file: MP4 (H.264 or H.265), WebM, GIF, MP3 or WAV; the video card's encoder when there is one, else x264 / x265.</summary>
public static class VidExport
{
    private static bool? _nvenc;
    private static bool? _nvencHevc;

    /// <summary>True when ffmpeg can really start the NVIDIA encoder on this PC (checked once, with a tiny test encode).</summary>
    public static async Task<bool> NvencAvailableAsync(bool hevc = false)
    {
        if ((hevc ? _nvencHevc : _nvenc) is bool known) return known;
        bool ok = false;
        try
        {
            var psi = new ProcessStartInfo(Tools.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-hide_banner", "-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=black:s=256x256:d=0.2", "-c:v", hevc ? "hevc_nvenc" : "h264_nvenc", "-f", "null", "-" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p != null)
            {
                var done = p.WaitForExitAsync();
                if (await Task.WhenAny(done, Task.Delay(8000)) != done) { try { p.Kill(true); } catch (InvalidOperationException) { } }
                else ok = p.ExitCode == 0;
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { ok = false; }
        if (hevc) _nvencHevc = ok; else _nvenc = ok;
        return ok;
    }

    /// <summary>Runs the export. Returns null when it worked, otherwise the error text (cancelling returns "Cancelled").</summary>
    public static async Task<string?> RunAsync(VidProject project, VidExportOptions o, IProgress<double> progress, CancellationToken ct)
    {
        double from = Math.Max(0, o.From);
        double to = o.To > from + 0.01 ? Math.Min(o.To, Math.Max(project.Duration, o.To)) : project.Duration;
        double duration = to - from;
        if (duration <= 0.01) return "There is nothing to export in that range.";
        string partial = o.Path + ".partial";
        try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { }
        bool picture = VidExportOptions.HasPicture(o.Format), sound = VidExportOptions.HasSound(o.Format);
        FfJob job;
        if (!picture) job = VidGraph.Audio(project, from, to);
        else if (o.Format == VidFormat.Gif) job = VidGraph.Video(project, from, to, o.Width, o.Height, "rgb24", gifFps: 15);
        else job = VidGraph.Both(project, from, to, o.Width, o.Height, "yuv420p");
        using var jobDispose = job;

        var psi = new ProcessStartInfo(Tools.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in job.Args) psi.ArgumentList.Add(a);
        if (picture) { psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("[" + job.VideoLabel + "]"); }
        if (picture && sound) { psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("[" + job.AudioLabel + "]"); }
        if (!picture) { psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("[" + job.AudioLabel + "]"); }
        int level = Math.Clamp(o.Quality, 0, 2);
        string[] crf264 = { "18", "21", "26" }, cq264 = { "21", "25", "30" };
        string[] crf265 = { "21", "25", "29" }, cq265 = { "23", "27", "31" };
        string[] crfVp9 = { "26", "32", "38" };
        string container = "mp4";
        switch (o.Format)
        {
            case VidFormat.Mp4H264:
                if (o.Gpu && await NvencAvailableAsync())
                    foreach (var a in new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", cq264[level], "-b:v", "0", "-profile:v", "high" }) psi.ArgumentList.Add(a);
                else
                    foreach (var a in new[] { "-c:v", "libx264", "-preset", level == 2 ? "faster" : "medium", "-crf", crf264[level], "-profile:v", "high" }) psi.ArgumentList.Add(a);
                foreach (var a in new[] { "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart" }) psi.ArgumentList.Add(a);
                break;
            case VidFormat.Mp4H265:
                if (o.Gpu && await NvencAvailableAsync(hevc: true))
                    foreach (var a in new[] { "-c:v", "hevc_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", cq265[level], "-b:v", "0", "-tag:v", "hvc1" }) psi.ArgumentList.Add(a);
                else
                    foreach (var a in new[] { "-c:v", "libx265", "-preset", level == 2 ? "faster" : "medium", "-crf", crf265[level], "-tag:v", "hvc1" }) psi.ArgumentList.Add(a);
                foreach (var a in new[] { "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart" }) psi.ArgumentList.Add(a);
                break;
            case VidFormat.WebmVp9:
                container = "webm";
                foreach (var a in new[] { "-c:v", "libvpx-vp9", "-crf", crfVp9[level], "-b:v", "0", "-row-mt", "1", "-cpu-used", "4", "-pix_fmt", "yuv420p", "-c:a", "libopus", "-b:a", "128k" }) psi.ArgumentList.Add(a);
                break;
            case VidFormat.Gif:
                container = "gif";
                foreach (var a in new[] { "-loop", "0" }) psi.ArgumentList.Add(a);
                break;
            case VidFormat.Mp3:
                container = "mp3";
                foreach (var a in new[] { "-c:a", "libmp3lame", "-q:a", level == 0 ? "0" : level == 1 ? "2" : "5" }) psi.ArgumentList.Add(a);
                break;
            case VidFormat.Wav:
                container = "wav";
                foreach (var a in new[] { "-c:a", "pcm_s16le" }) psi.ArgumentList.Add(a);
                break;
        }
        foreach (var a in new[] { "-t", duration.ToString("0.###", CultureInfo.InvariantCulture), "-progress", "pipe:1", "-nostats", "-f", container, "-y", partial }) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi);
        if (proc == null) return "ffmpeg could not be started.";
        using var reg = ct.Register(() => { try { proc.Kill(true); } catch (InvalidOperationException) { } });
        var err = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null && err.Length < 4000) err.AppendLine(e.Data); };
        proc.BeginErrorReadLine();
        string? line;
        while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(12), out long us) && us >= 0)
                progress.Report(Math.Clamp(us / 1e6 / duration, 0, 1));
        }
        await proc.WaitForExitAsync();
        if (ct.IsCancellationRequested)
        {
            try { File.Delete(partial); } catch (IOException) { }
            return "Cancelled";
        }
        if (proc.ExitCode != 0)
        {
            try { File.Delete(partial); } catch (IOException) { }
            return "ffmpeg stopped with an error:\n" + err.ToString().Trim();
        }
        File.Move(partial, o.Path, true);
        progress.Report(1);
        return null;
    }

    /// <summary>Saves one picture of the sequence (PNG or JPG by the file name) at a time, at the sequence's own size.</summary>
    public static async Task<string?> FrameAsync(VidProject project, double t, string path)
    {
        using var job = VidGraph.Video(project, t, t + 0.2, project.Width, project.Height, "yuv420p");
        var psi = new ProcessStartInfo(Tools.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in job.Args) psi.ArgumentList.Add(a);
        bool jpg = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
        foreach (var a in new[] { "-map", "[" + job.VideoLabel + "]", "-frames:v", "1" }) psi.ArgumentList.Add(a);
        if (jpg) foreach (var a in new[] { "-q:v", "2", "-pix_fmt", "yuvj420p" }) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("-y"); psi.ArgumentList.Add(path);
        using var proc = Process.Start(psi);
        if (proc == null) return "ffmpeg could not be started.";
        string err = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return proc.ExitCode == 0 && File.Exists(path) ? null : "The picture could not be made:\n" + err.Trim();
    }
}
