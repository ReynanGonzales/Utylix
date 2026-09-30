using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace IdmClone.Engine;

public enum MediaKind { Image, Video, Audio }

/// <param name="VideoQuality">"high", "balanced" or "small".</param>
/// <param name="MaxHeight">0 = keep the size; otherwise the picture is shrunk to at most this many pixels high.</param>
/// <param name="AudioKbps">Bit rate for MP3 / M4A / OGG (WAV and FLAC are lossless).</param>
public sealed record MediaOptions(string VideoQuality = "balanced", int MaxHeight = 0, int AudioKbps = 192);

/// <summary>
/// Video and music conversion with ffmpeg (pictures are ImageConverter's job). A "target" is a short key:
/// png jpg webp bmp gif tiff ico | mp4 mkv webm mov avi | mp3 m4a wav flac ogg. "gif" from a video is an animated GIF.
/// </summary>
public static class MediaConverter
{
    public static readonly string[] ImageTargets = { "png", "jpg", "webp", "bmp", "gif", "tiff", "ico" };
    public static readonly string[] VideoTargets = { "mp4", "mkv", "webm", "mov", "avi", "gif" };
    public static readonly string[] AudioTargets = { "mp3", "m4a", "wav", "flac", "ogg" };

    public static readonly string[] VideoExtensions =
        { "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpg", "mpeg", "3gp", "mts", "m2ts", "ogv", "vob" };
    public static readonly string[] AudioExtensions =
        { "mp3", "flac", "wav", "aac", "ogg", "m4a", "opus", "wma", "aiff", "aif", "amr", "mka" };

    public static MediaKind? KindOf(string path)
    {
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ImageConverter.InputExtensions.Contains(ext)) return MediaKind.Image;
        if (VideoExtensions.Contains(ext)) return MediaKind.Video;
        if (AudioExtensions.Contains(ext)) return MediaKind.Audio;
        return null;
    }

    public static bool IsSupported(string path) => KindOf(path) != null;

    public static bool IsTarget(string? key) => key != null && (ImageTargets.Contains(key) || VideoTargets.Contains(key) || AudioTargets.Contains(key));

    public static string Label(string target) => target switch
    {
        "png" => "PNG", "jpg" => "JPG", "webp" => "WebP", "bmp" => "BMP", "gif" => "GIF", "tiff" => "TIFF", "ico" => "ICO",
        "mp4" => "MP4", "mkv" => "MKV", "webm" => "WebM", "mov" => "MOV", "avi" => "AVI",
        "mp3" => "MP3", "m4a" => "M4A", "wav" => "WAV", "flac" => "FLAC", "ogg" => "OGG", _ => target.ToUpperInvariant(),
    };

    /// <summary>Can a file of this kind be turned into that target? (A song can't become a video, a picture can't become sound.)</summary>
    public static bool Compatible(MediaKind from, string target) => from switch
    {
        MediaKind.Image => ImageTargets.Contains(target),
        MediaKind.Video => VideoTargets.Contains(target) || AudioTargets.Contains(target),     // "gif" = animated GIF
        _ => AudioTargets.Contains(target),
    };

    public static string WhyNot(MediaKind from, string target)
    {
        string to = ImageTargets.Contains(target) && target != "gif" ? "picture" : AudioTargets.Contains(target) ? "sound file" : "video";
        return $"Skipped: a {from.ToString().ToLowerInvariant()} can't become a {to}.";
    }

    /// <summary>true when this conversion is done by ffmpeg (video/audio files, or WebP output handled elsewhere).</summary>
    public static bool NeedsFfmpeg(MediaKind from, string target) => from != MediaKind.Image;

    // ---------- converting ----------
    /// <summary>
    /// Converts one video or music file with ffmpeg and returns the new file's path. <paramref name="progress"/> gets 0..1
    /// (only when the length of the file is known). Throws IOException with a readable message; cancelling deletes the
    /// half-written file and throws OperationCanceledException.
    /// </summary>
    public static async Task<string> ConvertAsync(string source, string target, MediaOptions options, string? outDir,
                                                 IProgress<double>? progress, CancellationToken ct)
    {
        if (!Tools.HasFfmpeg) throw new IOException("Video and music need ffmpeg. Install it from the button above (or Settings → Video sites).");
        if (!File.Exists(source)) throw new IOException("The file no longer exists.");
        var kind = KindOf(source) ?? throw new IOException("Not a video or music type this converter reads.");
        if (!Compatible(kind, target)) throw new IOException(WhyNot(kind, target));

        string dir = outDir is { Length: > 0 } ? outDir : Path.GetDirectoryName(Path.GetFullPath(source))!;
        Directory.CreateDirectory(dir);
        string ext = target == "tiff" ? "tif" : target;
        string output = ImageConverter.ReserveName(dir, Path.GetFileNameWithoutExtension(source), ext);

        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats", "-i", source };
        args.AddRange(ArgumentsFor(kind, target, options));
        args.Add(output);

        double? duration = await ProbeDurationAsync(source, ct);
        var lastErrors = new Queue<string>();
        var psi = new ProcessStartInfo(Tools.Ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new IOException("ffmpeg didn't start.");

        proc.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (lastErrors) { lastErrors.Enqueue(e.Data.Trim()); while (lastErrors.Count > 6) lastErrors.Dequeue(); }
        };
        proc.OutputDataReceived += (_, e) =>
        {
            // ffmpeg -progress prints "out_time_us=1234567" (microseconds) every half second or so
            if (e.Data == null || duration is not > 0 || progress == null) return;
            if (e.Data.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(e.Data.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) && us >= 0)
                progress.Report(Math.Clamp(us / 1_000_000.0 / duration.Value, 0, 1));
        };
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();

        using var registration = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch (Exception) { } });
        await proc.WaitForExitAsync(CancellationToken.None);

        if (ct.IsCancellationRequested)
        {
            TryDelete(output);
            throw new OperationCanceledException(ct);
        }
        if (proc.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
        {
            TryDelete(output);
            string why;
            lock (lastErrors) why = lastErrors.Count > 0 ? lastErrors.Last() : "";
            throw new IOException("ffmpeg couldn't convert it" + (why.Length > 0 ? ": " + why : "."));
        }
        progress?.Report(1);
        return output;
    }

    /// <summary>The ffmpeg options (after -i, before the output name) for one target.</summary>
    internal static List<string> ArgumentsFor(MediaKind from, string target, MediaOptions o)
    {
        var a = new List<string>();
        int kbps = Math.Clamp(o.AudioKbps, 64, 320);
        string k = kbps + "k";

        if (AudioTargets.Contains(target))                                    // sound only (also out of a video)
        {
            a.AddRange(new[] { "-vn", "-map", "0:a:0", "-map_metadata", "0" });
            switch (target)
            {
                case "mp3": a.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", k, "-id3v2_version", "3" }); break;
                case "m4a": a.AddRange(new[] { "-c:a", "aac", "-b:a", k }); break;
                case "wav": a.AddRange(new[] { "-c:a", "pcm_s16le" }); break;
                case "flac": a.AddRange(new[] { "-c:a", "flac" }); break;
                default: a.AddRange(new[] { "-c:a", "libopus", "-b:a", k }); break;      // ogg (Opus)
            }
            return a;
        }

        string quality = o.VideoQuality;
        if (target == "gif")                                                  // animated GIF: a small palette-based clip
        {
            int height = o.MaxHeight > 0 ? o.MaxHeight : 480;
            a.AddRange(new[] { "-an", "-vf", $"fps=12,scale=-2:'min(ih,{height})':flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse", "-loop", "0" });
            return a;
        }

        a.AddRange(new[] { "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-map_metadata", "0" });
        if (o.MaxHeight > 0) a.AddRange(new[] { "-vf", $"scale=-2:'trunc(min(ih,{o.MaxHeight})/2)*2'" });     // never enlarged, always an even size
        if (target == "webm")
        {
            string crf = quality == "high" ? "28" : quality == "small" ? "38" : "33";
            a.AddRange(new[] { "-c:v", "libvpx-vp9", "-crf", crf, "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4",
                               "-pix_fmt", "yuv420p", "-c:a", "libopus", "-b:a", "128k" });
        }
        else
        {
            string crf = quality == "high" ? "18" : quality == "small" ? "28" : "23";
            a.AddRange(new[] { "-c:v", "libx264", "-preset", "fast", "-crf", crf, "-pix_fmt", "yuv420p" });
            a.AddRange(target == "avi" ? new[] { "-c:a", "libmp3lame", "-b:a", "192k" } : new[] { "-c:a", "aac", "-b:a", "192k" });
            if (target is "mp4" or "mov") a.AddRange(new[] { "-movflags", "+faststart" });                     // starts playing before it is fully loaded
        }
        return a;
    }

    private static async Task<double?> ProbeDurationAsync(string source, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(Tools.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-hide_banner", "-nostdin", "-i", source }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            string err = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(CancellationToken.None);
            var m = Regex.Match(err, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (!m.Success) return null;
            return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or FormatException) { return null; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { }
    }
}

/// <summary>Runs something on a single-threaded-apartment thread (Windows' picture code needs one) and awaits the result.</summary>
public static class Sta
{
    public static Task<T> Run<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception e) { done.SetException(e); }
        }) { IsBackground = true, Name = "sta-work" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}
