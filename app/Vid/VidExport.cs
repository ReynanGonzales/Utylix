using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IdmClone.Engine;

namespace IdmClone.Vid;

public sealed class VidExportOptions
{
    public string Path { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>0 = high, 1 = medium, 2 = small file.</summary>
    public int Quality { get; set; } = 1;
    public bool Gpu { get; set; } = true;
}

/// <summary>Writes the timeline to an MP4 (H.264 + AAC): the video card's encoder when there is one, else x264.</summary>
public static class VidExport
{
    private static bool? _nvenc;

    /// <summary>True when ffmpeg can really start the NVIDIA encoder on this PC (checked once, with a tiny test encode).</summary>
    public static async Task<bool> NvencAvailableAsync()
    {
        if (_nvenc != null) return _nvenc.Value;
        try
        {
            var psi = new ProcessStartInfo(Tools.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-hide_banner", "-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=black:s=256x256:d=0.2", "-c:v", "h264_nvenc", "-f", "null", "-" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return (_nvenc = false).Value;
            var done = p.WaitForExitAsync();
            if (await Task.WhenAny(done, Task.Delay(8000)) != done) { try { p.Kill(true); } catch (InvalidOperationException) { } return (_nvenc = false).Value; }
            _nvenc = p.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { _nvenc = false; }
        return _nvenc.Value;
    }

    /// <summary>Runs the export. Returns null when it worked, otherwise the error text (cancelling returns "Cancelled").</summary>
    public static async Task<string?> RunAsync(VidProject project, VidExportOptions o, IProgress<double> progress, CancellationToken ct)
    {
        double duration = project.Duration;
        if (duration <= 0) return "The timeline is empty.";
        string partial = o.Path + ".partial";
        try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { }
        using var job = VidGraph.Both(project, 0, duration, o.Width, o.Height, "yuv420p");
        var psi = new ProcessStartInfo(Tools.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in job.Args) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "-map", "[" + job.VideoLabel + "]", "-map", "[" + job.AudioLabel + "]" }) psi.ArgumentList.Add(a);
        string[][] q = { new[] { "18", "21" }, new[] { "21", "25" }, new[] { "26", "30" } };        // x264 crf / nvenc cq
        int level = Math.Clamp(o.Quality, 0, 2);
        if (o.Gpu && await NvencAvailableAsync())
            foreach (var a in new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", q[level][1], "-b:v", "0", "-profile:v", "high" }) psi.ArgumentList.Add(a);
        else
            foreach (var a in new[] { "-c:v", "libx264", "-preset", level == 2 ? "faster" : "medium", "-crf", q[level][0], "-profile:v", "high" }) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-t", duration.ToString("0.###", CultureInfo.InvariantCulture),
                                  "-progress", "pipe:1", "-nostats", "-f", "mp4", "-y", partial }) psi.ArgumentList.Add(a);

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
}
