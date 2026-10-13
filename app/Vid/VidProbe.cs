using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IdmClone.Engine;

namespace IdmClone.Vid;

/// <summary>Reads what a file is (ffprobe, next to ffmpeg) so the editor knows its length, size and whether it has sound.</summary>
public static class VidProbe
{
    public static readonly string[] PictureTypes = { ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".webp", ".tif", ".tiff" };

    public static string FfprobePath => Path.Combine(Tools.FfmpegDir, "ffprobe.exe");

    public static bool Available => File.Exists(FfprobePath) && Tools.HasFfmpeg;

    public static async Task<MediaItem> ReadAsync(string path, CancellationToken ct = default)
    {
        var item = new MediaItem { Path = path };
        string ext = Path.GetExtension(path).ToLowerInvariant();
        var psi = new ProcessStartInfo(FfprobePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path }) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new IOException("ffprobe could not be started.");
        var err = proc.StandardError.ReadToEndAsync();
        string json = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
            throw new IOException("This file could not be read as a video, a sound or a picture." + (string.IsNullOrWhiteSpace(await err) ? "" : "\n" + (await err).Trim()));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        double formatDuration = 0;
        if (root.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var fd)) formatDuration = Num(fd);
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                string type = s.TryGetProperty("codec_type", out var ct2) ? ct2.GetString() ?? "" : "";
                if (type == "audio") { item.HasAudio = true; continue; }
                if (type != "video" || item.HasVideo) continue;
                if (s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1) continue;   // cover art
                item.HasVideo = true;
                item.Width = s.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                item.Height = s.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
                item.Fps = Fraction(s, "avg_frame_rate");
                if (item.Fps <= 0) item.Fps = Fraction(s, "r_frame_rate");
                int rotation = 0;
                if (s.TryGetProperty("side_data_list", out var sd))
                    foreach (var d in sd.EnumerateArray()) if (d.TryGetProperty("rotation", out var r)) rotation = (int)Math.Round(Num(r));
                if (rotation == 0 && s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rt)) int.TryParse(rt.GetString(), out rotation);
                if (Math.Abs(rotation) % 180 == 90) (item.Width, item.Height) = (item.Height, item.Width);
                if (s.TryGetProperty("duration", out var sdur) && Num(sdur) > 0 && formatDuration <= 0) formatDuration = Num(sdur);
            }
        }
        bool isPicture = PictureTypes.Contains(ext);
        if (isPicture && item.HasVideo) { item.Kind = MediaKind.Image; item.Duration = 5; item.HasAudio = false; item.Fps = 0; }
        else if (item.HasVideo) { item.Kind = MediaKind.Video; item.Duration = formatDuration; }
        else if (item.HasAudio) { item.Kind = MediaKind.Audio; item.Duration = formatDuration; }
        else throw new IOException("This file has no picture or sound that Utylix Vid Editor can use.");
        if (item.Kind != MediaKind.Image && item.Duration <= 0) throw new IOException("The length of this file could not be found.");
        return item;
    }

    private static double Num(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number ? e.GetDouble()
        : double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double Fraction(JsonElement s, string name)
    {
        if (!s.TryGetProperty(name, out var e)) return 0;
        string[] parts = (e.GetString() ?? "").Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b > 0) return a / b;
        return 0;
    }
}
