using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace IdmClone.Vid;

/// <summary>
/// A ready-to-run ffmpeg command for part of the timeline: <see cref="Args"/> = inputs + the filter script (the caller adds its output options and maps
/// <see cref="VideoLabel"/> / <see cref="AudioLabel"/>). The script lives in a temp file (a long timeline would not fit on a command line).
/// </summary>
public sealed class FfJob : IDisposable
{
    public List<string> Args { get; } = new();
    public string? VideoLabel { get; set; }
    public string? AudioLabel { get; set; }
    public string? ScriptPath { get; set; }
    public double Duration { get; set; }

    public void Dispose()
    {
        if (ScriptPath != null) { try { File.Delete(ScriptPath); } catch (IOException) { } catch (UnauthorizedAccessException) { } ScriptPath = null; }
    }
}

/// <summary>
/// Turns the timeline into an ffmpeg filter graph. The picture and sound that the preview plays and the file that is exported come from the SAME
/// graph (only size and quality differ), so what you see is what you get.
/// </summary>
public static class VidGraph
{
    public const int AudioRate = 48000;

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    public static FfJob Video(VidProject p, double t0, double t1, int w, int h, string pixFmt) => Build(p, t0, t1, w, h, pixFmt, video: true, audio: false);
    public static FfJob Audio(VidProject p, double t0, double t1) => Build(p, t0, t1, 0, 0, "yuv420p", video: false, audio: true);
    public static FfJob Both(VidProject p, double t0, double t1, int w, int h, string pixFmt) => Build(p, t0, t1, w, h, pixFmt, video: true, audio: true);

    private static FfJob Build(VidProject p, double t0, double t1, int w, int h, string pixFmt, bool video, bool audio)
    {
        double dur = Math.Max(0.04, t1 - t0);
        var job = new FfJob { Duration = dur };
        job.Args.AddRange(new[] { "-hide_banner", "-nostdin", "-v", "error" });
        var script = new StringBuilder();
        int inputs = 0;
        double fps = p.Fps > 0 ? p.Fps : 30;
        // an even size: yuv420 can't hold odd widths
        w = w / 2 * 2; h = h / 2 * 2;

        if (video)
        {
            script.Append($"color=c=black:s={w}x{h}:r={F(fps)}:d={F(dur)},format=yuva420p[b0];\n");
            string last = "b0";
            int layer = 0;
            foreach (var track in p.Tracks.Where(t => t.Kind == TrackKind.Video && !t.Muted))
            {
                foreach (var c in track.Clips.OrderBy(c => c.Start))
                {
                    if (c.Kind != ClipKind.Media || c.End <= t0 || c.Start >= t1) continue;
                    var m = p.MediaOf(c);
                    if (m == null || !m.HasVideo) continue;
                    double a = Math.Max(c.Start, t0), b = Math.Min(c.End, t1);
                    if (b - a < 0.001) continue;
                    int idx = inputs++;
                    if (m.Kind == MediaKind.Image)
                        job.Args.AddRange(new[] { "-loop", "1", "-framerate", F(fps), "-t", F(b - a), "-i", m.Path });
                    else
                        job.Args.AddRange(new[] { "-ss", F(c.In + (a - c.Start) * c.Speed), "-t", F((b - a) * c.Speed), "-i", m.Path });

                    double speed = c.Speed > 0 ? c.Speed : 1;
                    var chain = new StringBuilder();
                    chain.Append($"[{idx}:v]scale={w}:{h}:force_original_aspect_ratio=decrease,setsar=1");
                    if (Math.Abs(c.Scale - 1) > 0.001) chain.Append($",scale=trunc(iw*{F(c.Scale)}/2)*2:trunc(ih*{F(c.Scale)}/2)*2");
                    chain.Append($",setpts=(PTS-STARTPTS)/{F(speed)},fps={F(fps)},setpts=PTS+{F(a - t0)}/TB");
                    bool alpha = c.Opacity < 0.999 || c.FadeIn > 0 || c.FadeOut > 0;
                    if (alpha)
                    {
                        chain.Append(",format=yuva420p");
                        if (c.Opacity < 0.999) chain.Append($",colorchannelmixer=aa={F(Math.Clamp(c.Opacity, 0, 1))}");
                        if (c.FadeIn > 0) chain.Append($",fade=t=in:st={F(c.Start - t0)}:d={F(c.FadeIn)}:alpha=1");
                        if (c.FadeOut > 0) chain.Append($",fade=t=out:st={F(c.End - c.FadeOut - t0)}:d={F(c.FadeOut)}:alpha=1");
                    }
                    string vl = "v" + idx;
                    script.Append(chain).Append($"[{vl}];\n");
                    string next = "o" + (++layer);
                    script.Append($"[{last}][{vl}]overlay=x=main_w*{F(c.X)}-overlay_w/2:y=main_h*{F(c.Y)}-overlay_h/2:eof_action=pass:format=auto[{next}];\n");
                    last = next;
                }
            }
            script.Append($"[{last}]format={pixFmt}[vout];\n");
            job.VideoLabel = "vout";
        }

        if (audio)
        {
            var labels = new List<string>();
            foreach (var track in p.Tracks.Where(t => t.Kind == TrackKind.Audio && !t.Muted))
            {
                foreach (var c in track.Clips.OrderBy(c => c.Start))
                {
                    if (c.Kind != ClipKind.Media || c.End <= t0 || c.Start >= t1 || c.Volume <= 0.0001) continue;
                    var m = p.MediaOf(c);
                    if (m == null || !m.HasAudio) continue;
                    double a = Math.Max(c.Start, t0), b = Math.Min(c.End, t1);
                    if (b - a < 0.001) continue;
                    int idx = inputs++;
                    job.Args.AddRange(new[] { "-ss", F(c.In + (a - c.Start) * c.Speed), "-t", F((b - a) * c.Speed), "-i", m.Path });
                    double speed = c.Speed > 0 ? c.Speed : 1;
                    var chain = new StringBuilder();
                    chain.Append($"[{idx}:a]aresample={AudioRate},aformat=sample_fmts=fltp:channel_layouts=stereo,asetpts=PTS-STARTPTS");
                    for (double s = speed; Math.Abs(s - 1) > 0.001;)
                    {
                        double step = s > 2 ? 2 : s < 0.5 ? 0.5 : s;
                        chain.Append($",atempo={F(step)}");
                        s /= step;
                    }
                    if (Math.Abs(c.Volume - 1) > 0.001) chain.Append($",volume={F(c.Volume)}");
                    double local = b - a;
                    if (c.FadeIn > 0 && a <= c.Start + 0.0005) chain.Append($",afade=t=in:st=0:d={F(Math.Min(c.FadeIn, local))}");
                    if (c.FadeOut > 0) chain.Append($",afade=t=out:st={F(Math.Max(0, c.End - c.FadeOut - a))}:d={F(c.FadeOut)}");
                    long delay = (long)Math.Round((a - t0) * 1000);
                    if (delay > 0) chain.Append($",adelay={delay}:all=1");
                    string al = "a" + idx;
                    script.Append(chain).Append($"[{al}];\n");
                    labels.Add(al);
                }
            }
            script.Append($"anullsrc=r={AudioRate}:cl=stereo,atrim=duration={F(dur)}[asil];\n");
            if (labels.Count == 0) script.Append("[asil]anull[aout];\n");
            else
            {
                script.Append("[asil]").Append(string.Concat(labels.Select(l => "[" + l + "]")));
                script.Append($"amix=inputs={labels.Count + 1}:normalize=0:duration=first:dropout_transition=0,alimiter=limit=0.98[aout];\n");
            }
            job.AudioLabel = "aout";
        }

        string dir = Path.Combine(Path.GetTempPath(), "UtylixVid");
        Directory.CreateDirectory(dir);
        job.ScriptPath = Path.Combine(dir, "graph-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(job.ScriptPath, script.ToString().TrimEnd('\n', ';', ' '));
        job.Args.AddRange(new[] { "-/filter_complex", job.ScriptPath });
        return job;
    }
}
