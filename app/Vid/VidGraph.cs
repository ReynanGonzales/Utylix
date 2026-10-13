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
    /// <summary>Little files the graph refers to (the words of titles): removed with the job.</summary>
    public List<string> TempFiles { get; } = new();

    public void Dispose()
    {
        foreach (string f in TempFiles) { try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        TempFiles.Clear();
        if (ScriptPath != null) { try { File.Delete(ScriptPath); } catch (IOException) { } catch (UnauthorizedAccessException) { } ScriptPath = null; }
    }
}

/// <summary>The fonts titles can use (files in the Windows Fonts folder; the first name that exists is used).</summary>
public static class VidFonts
{
    public static readonly (string Name, string Regular, string Bold)[] All =
    {
        ("Segoe UI", "segoeui.ttf", "segoeuib.ttf"), ("Arial", "arial.ttf", "arialbd.ttf"), ("Calibri", "calibri.ttf", "calibrib.ttf"),
        ("Times New Roman", "times.ttf", "timesbd.ttf"), ("Georgia", "georgia.ttf", "georgiab.ttf"), ("Verdana", "verdana.ttf", "verdanab.ttf"),
        ("Trebuchet MS", "trebuc.ttf", "trebucbd.ttf"), ("Impact", "impact.ttf", "impact.ttf"), ("Consolas", "consola.ttf", "consolab.ttf"),
        ("Courier New", "cour.ttf", "courbd.ttf"), ("Comic Sans MS", "comic.ttf", "comicbd.ttf"),
    };

    public static string[] Available()
    {
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        return All.Where(f => File.Exists(Path.Combine(dir, f.Regular))).Select(f => f.Name).ToArray();
    }

    public static string File_(string family, bool bold)
    {
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var f = All.FirstOrDefault(x => x.Name == family);
        if (f.Name != null)
        {
            string path = Path.Combine(dir, bold ? f.Bold : f.Regular);
            if (File.Exists(path)) return path;
        }
        foreach (string n in bold ? new[] { "segoeuib.ttf", "arialbd.ttf" } : new[] { "segoeui.ttf", "arial.ttf" })
            if (File.Exists(Path.Combine(dir, n))) return Path.Combine(dir, n);
        return Path.Combine(dir, "arial.ttf");
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

    /// <summary>A Windows path as a filter option value inside single quotes: forward slashes, and the colon of the drive escaped.</summary>
    private static string FilterPath(string path) => path.Replace('\\', '/').Replace(":", "\\:");

    public static FfJob Video(VidProject p, double t0, double t1, int w, int h, string pixFmt) => Build(p, t0, t1, w, h, pixFmt, video: true, audio: false);
    public static FfJob Audio(VidProject p, double t0, double t1) => Build(p, t0, t1, 0, 0, "yuv420p", video: false, audio: true);
    public static FfJob Both(VidProject p, double t0, double t1, int w, int h, string pixFmt) => Build(p, t0, t1, w, h, pixFmt, video: true, audio: true);

    private sealed class Ctx
    {
        public VidProject P = null!;
        public FfJob Job = null!;
        public StringBuilder Script = new();
        public int Inputs;
        public double T0, T1, Fps;
        public int W, H;
        public string Last = "b0";
        public int Layer;
    }

    // ---------- animated values ----------
    /// <summary>
    /// ffmpeg expression for a property that has keyframes (null when it has none). <paramref name="tv"/> is the name of the time variable
    /// ("t", or "T" inside geq); <paramref name="origin"/> is the timeline time that variable counts from.
    /// </summary>
    private static string? Keyed(Clip c, string prop, double origin, string tv)
    {
        if (!c.Keys.TryGetValue(prop, out var list) || list.Count == 0) return null;
        var k = list.OrderBy(x => x.T).ToList();
        double Tw(int i) => c.Start + k[i].T - origin;
        string e = F(k[^1].V);
        for (int i = k.Count - 1; i >= 1; i--)
        {
            double span = Math.Max(1e-6, Tw(i) - Tw(i - 1));
            e = $"if(lt({tv},{F(Tw(i))}),{F(k[i - 1].V)}+({F(k[i].V)}-{F(k[i - 1].V)})*({tv}-{F(Tw(i - 1))})/{F(span)},{e})";
        }
        return $"if(lt({tv},{F(Tw(0))}),{F(k[0].V)},{e})";
    }

    private static bool IsWipe(TransitionKind k) => k is TransitionKind.WipeRight or TransitionKind.WipeLeft or TransitionKind.WipeDown or TransitionKind.WipeUp;

    // ---------- the whole graph ----------
    private static FfJob Build(VidProject p, double t0, double t1, int w, int h, string pixFmt, bool video, bool audio)
    {
        double dur = Math.Max(0.04, t1 - t0);
        var job = new FfJob { Duration = dur };
        job.Args.AddRange(new[] { "-hide_banner", "-nostdin", "-v", "error" });
        var x = new Ctx { P = p, Job = job, T0 = t0, T1 = t1, Fps = p.Fps > 0 ? p.Fps : 30, W = w / 2 * 2, H = h / 2 * 2 };
        var script = x.Script;

        if (video)
        {
            script.Append($"color=c=black:s={x.W}x{x.H}:r={F(x.Fps)}:d={F(dur)},format=yuva420p[b0];\n");
            foreach (var track in p.Tracks.Where(t => t.Kind == TrackKind.Video && !t.Muted))
            {
                foreach (var c in track.Clips.OrderBy(c => c.Start))
                {
                    if (c.Kind == ClipKind.Text) AddTitle(x, track, c);
                    else if (c.Kind == ClipKind.Media) AddVideoClip(x, track, c);
                }
            }
            script.Append($"[{x.Last}]format={pixFmt}[vout];\n");
            job.VideoLabel = "vout";
        }

        if (audio)
        {
            AddAudio(x);
            job.AudioLabel = "aout";
        }

        string dir = Path.Combine(Path.GetTempPath(), "UtylixVid");
        Directory.CreateDirectory(dir);
        job.ScriptPath = Path.Combine(dir, "graph-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(job.ScriptPath, script.ToString().TrimEnd('\n', ';', ' '));
        job.Args.AddRange(new[] { "-/filter_complex", job.ScriptPath });
        return job;
    }

    // ---------- picture ----------
    private static void AddVideoClip(Ctx x, Track track, Clip c)
    {
        var m = x.P.MediaOf(c);
        if (m == null || !m.HasVideo) return;
        double T0 = x.T0, T1 = x.T1, fps = x.Fps;

        // the clip that comes in over this one (cross dissolve and wipes need this one to stay visible under it)
        var next = track.Clips.FirstOrDefault(o => o != c && Math.Abs(o.Start - c.End) < 0.002 && o.Transition != TransitionKind.None);
        double nextL = next != null ? Math.Max(0.05, next.TransitionLength) : 0;
        double ext = next != null && next.Transition != TransitionKind.DipToBlack ? nextL : 0;
        double visEnd = c.End + ext;
        if (visEnd <= T0 || c.Start >= T1) return;

        bool frozen = c.End <= T0;                                           // only the held last picture is inside the window
        double a = frozen ? T0 : Math.Max(c.Start, T0);
        double b = frozen ? T0 : Math.Min(c.End, T1);
        if (!frozen && b - a < 0.001) return;
        double speed = c.Speed > 0 ? c.Speed : 1;
        int idx = x.Inputs++;
        double tail;
        if (frozen)
        {
            double read = 2.0 / fps;
            if (m.Kind == MediaKind.Image) x.Job.Args.AddRange(new[] { "-loop", "1", "-framerate", F(fps), "-t", F(read), "-i", m.Path });
            else x.Job.Args.AddRange(new[] { "-ss", F(Math.Max(0, c.In + Math.Max(0, c.Length - read) * speed)), "-t", F(read * speed), "-i", m.Path });
            tail = Math.Min(visEnd, T1) - a;
        }
        else
        {
            if (m.Kind == MediaKind.Image) x.Job.Args.AddRange(new[] { "-loop", "1", "-framerate", F(fps), "-t", F(b - a), "-i", m.Path });
            else x.Job.Args.AddRange(new[] { "-ss", F(c.In + (a - c.Start) * speed), "-t", F((b - a) * speed), "-i", m.Path });
            tail = b >= c.End - 1e-6 ? Math.Min(visEnd, T1) - b : 0;
        }

        var ch = new StringBuilder();
        ch.Append($"[{idx}:v]scale={x.W}:{x.H}:force_original_aspect_ratio=decrease,setsar=1");
        foreach (var e in c.Effects)
        {
            string? f = VidEffects.VideoFilter(e);
            if (f != null) ch.Append(',').Append(f);
        }
        ch.Append($",setpts=(PTS-STARTPTS)/{F(speed)},fps={F(fps)}");
        if (tail > 0.0005) ch.Append($",tpad=stop_mode=clone:stop_duration={F(tail)}");
        ch.Append($",setpts=PTS+{F(a - T0)}/TB");

        var kScale = Keyed(c, "Scale", T0, "t");
        var kRot = Keyed(c, "Rotation", T0, "t");
        var kOpacity = Keyed(c, "Opacity", T0, "T");
        bool rotated = kRot != null || Math.Abs(c.Rotation) > 0.01;
        var tr = c.Transition;
        double trL = Math.Max(0.05, c.TransitionLength);
        bool alpha = kOpacity != null || c.Opacity < 0.999 || c.FadeIn > 0 || c.FadeOut > 0 || rotated || IsWipe(tr) || tr == TransitionKind.Dissolve;
        if (alpha) ch.Append(",format=yuva420p");

        if (kScale != null) ch.Append($",scale=w='max(2,trunc(iw*({kScale})/2)*2)':h='max(2,trunc(ih*({kScale})/2)*2)':eval=frame");
        else if (Math.Abs(c.Scale - 1) > 0.001) ch.Append($",scale=max(2\\,trunc(iw*{F(c.Scale)}/2)*2):max(2\\,trunc(ih*{F(c.Scale)}/2)*2)");
        if (rotated)
        {
            string ang = kRot != null ? $"({kRot})*PI/180" : F(c.Rotation * Math.PI / 180);
            ch.Append($",rotate=a='{ang}':ow='trunc(hypot(iw,ih)/2)*2':oh=ow:c=none");
        }
        if (kOpacity != null) ch.Append($",geq=lum='lum(X,Y)':cb='cb(X,Y)':cr='cr(X,Y)':a='alpha(X,Y)*clip({kOpacity},0,1)'");
        else if (c.Opacity < 0.999) ch.Append($",colorchannelmixer=aa={F(Math.Clamp(c.Opacity, 0, 1))}");
        if (c.FadeIn > 0) ch.Append($",fade=t=in:st={F(c.Start - T0)}:d={F(c.FadeIn)}:alpha=1");
        if (c.FadeOut > 0) ch.Append($",fade=t=out:st={F(c.End - c.FadeOut - T0)}:d={F(c.FadeOut)}:alpha=1");

        // the way this clip comes in
        double ts = c.Start - T0;
        switch (tr)
        {
            case TransitionKind.Dissolve:
                ch.Append($",fade=t=in:st={F(ts)}:d={F(trL)}:alpha=1");
                break;
            case TransitionKind.DipToBlack:
                ch.Append($",fade=t=in:st={F(ts)}:d={F(trL / 2)}:color=black");
                break;
            case TransitionKind.WipeRight:
            case TransitionKind.WipeLeft:
            case TransitionKind.WipeDown:
            case TransitionKind.WipeUp:
            {
                string p = $"clip((T-{F(ts)})/{F(trL)},0,1)";
                string visible = tr switch
                {
                    TransitionKind.WipeRight => $"lte(X,W*{p})",
                    TransitionKind.WipeLeft => $"gte(X,W*(1-{p}))",
                    TransitionKind.WipeDown => $"lte(Y,H*{p})",
                    _ => $"gte(Y,H*(1-{p}))",
                };
                ch.Append($",geq=lum='lum(X,Y)':cb='cb(X,Y)':cr='cr(X,Y)':a='alpha(X,Y)*{visible}'");
                break;
            }
        }
        // and the way it goes out when the next clip dips to black
        if (next != null && next.Transition == TransitionKind.DipToBlack)
            ch.Append($",fade=t=out:st={F(c.End - nextL / 2 - T0)}:d={F(nextL / 2)}:color=black");

        string vl = "v" + idx;
        x.Script.Append(ch).Append($"[{vl}];\n");
        string kx = Keyed(c, "X", T0, "t") ?? F(c.X);
        string ky = Keyed(c, "Y", T0, "t") ?? F(c.Y);
        string nextLabel = "o" + (++x.Layer);
        x.Script.Append($"[{x.Last}][{vl}]overlay=x='main_w*({kx})-overlay_w/2':y='main_h*({ky})-overlay_h/2':eof_action=pass:format=auto[{nextLabel}];\n");
        x.Last = nextLabel;
    }

    /// <summary>A title: a transparent layer the size of the picture with the words drawn on it, laid over what is below.</summary>
    private static void AddTitle(Ctx x, Track track, Clip c)
    {
        double T0 = x.T0, T1 = x.T1;
        double a = Math.Max(c.Start, T0), b = Math.Min(c.End, T1);
        if (b - a < 0.001) return;
        string dir = Path.Combine(Path.GetTempPath(), "UtylixVid");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "title-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(file, string.IsNullOrEmpty(c.Text) ? " " : c.Text.Replace("\r\n", "\n"), new UTF8Encoding(false));
        x.Job.TempFiles.Add(file);
        string color = System.Text.RegularExpressions.Regex.IsMatch(c.TextColor ?? "", "^#[0-9A-Fa-f]{6}$") ? c.TextColor!.Substring(1) : "FFFFFF";
        int size = Math.Max(8, (int)Math.Round(x.H * Math.Clamp(c.TextSize, 0.01, 0.5)));
        int n = ++x.Layer;
        string lid = "t" + n;
        var ch = new StringBuilder();
        ch.Append($"color=c=black@0:s={x.W}x{x.H}:r={F(x.Fps)}:d={F(b - a)},format=yuva420p,");
        ch.Append($"drawtext=fontfile='{FilterPath(VidFonts.File_(c.TextFont, c.TextBold))}':textfile='{FilterPath(file)}':expansion=none:fontsize={size}:fontcolor=0x{color}:");
        string kx = Keyed(c, "X", T0, "t") ?? F(c.X), ky = Keyed(c, "Y", T0, "t") ?? F(c.Y);
        ch.Append($"x='w*({kx})-text_w/2':y='h*({ky})-text_h/2':line_spacing=6:shadowx=2:shadowy=2:shadowcolor=black@0.55");
        if (c.TextBoxOpacity > 0.01) ch.Append($":box=1:boxcolor=0x000000@{F(Math.Clamp(c.TextBoxOpacity, 0, 1))}:boxborderw={Math.Max(6, size / 3)}");
        if (c.TextOutline) ch.Append($":borderw={Math.Max(2, size / 14)}:bordercolor=0x000000");
        ch.Append($",setpts=PTS-STARTPTS+{F(a - T0)}/TB");
        var kOpacity = Keyed(c, "Opacity", T0, "T");
        if (kOpacity != null) ch.Append($",geq=lum='lum(X,Y)':cb='cb(X,Y)':cr='cr(X,Y)':a='alpha(X,Y)*clip({kOpacity},0,1)'");
        else if (c.Opacity < 0.999) ch.Append($",colorchannelmixer=aa={F(Math.Clamp(c.Opacity, 0, 1))}");
        if (c.FadeIn > 0) ch.Append($",fade=t=in:st={F(c.Start - T0)}:d={F(c.FadeIn)}:alpha=1");
        if (c.FadeOut > 0) ch.Append($",fade=t=out:st={F(c.End - c.FadeOut - T0)}:d={F(c.FadeOut)}:alpha=1");
        if (c.Transition == TransitionKind.Dissolve || c.Transition == TransitionKind.DipToBlack)
            ch.Append($",fade=t=in:st={F(c.Start - T0)}:d={F(Math.Max(0.05, c.TransitionLength))}:alpha=1");
        x.Script.Append(ch).Append($"[{lid}];\n");
        string next = "o" + n + "x";
        x.Script.Append($"[{x.Last}][{lid}]overlay=eof_action=pass:format=auto[{next}];\n");
        x.Last = next;
    }

    // ---------- sound ----------
    private static void AddAudio(Ctx x)
    {
        var P = x.P; double T0 = x.T0, T1 = x.T1;
        double dur = Math.Max(0.04, T1 - T0);
        bool anySolo = P.Tracks.Any(t => t.Kind == TrackKind.Audio && t.Solo);
        var trackLabels = new List<string>();
        int trackNo = 0;
        foreach (var track in P.Tracks.Where(t => t.Kind == TrackKind.Audio && !t.Muted && (!anySolo || t.Solo)))
        {
            trackNo++;
            var labels = new List<string>();
            foreach (var c in track.Clips.OrderBy(c => c.Start))
            {
                if (c.Kind != ClipKind.Media || c.End <= T0 || c.Start >= T1) continue;
                bool keyedVol = c.IsAnimated("Volume");
                if (!keyedVol && c.Volume <= 0.0001) continue;
                var m = P.MediaOf(c);
                if (m == null || !m.HasAudio) continue;
                double a = Math.Max(c.Start, T0), b = Math.Min(c.End, T1);
                if (b - a < 0.001) continue;
                int idx = x.Inputs++;
                double speed = c.Speed > 0 ? c.Speed : 1;
                x.Job.Args.AddRange(new[] { "-ss", F(c.In + (a - c.Start) * speed), "-t", F((b - a) * speed), "-i", m.Path });
                var ch = new StringBuilder();
                ch.Append($"[{idx}:a]aresample={AudioRate},aformat=sample_fmts=fltp:channel_layouts=stereo,asetpts=PTS-STARTPTS");
                foreach (var e in c.Effects)
                {
                    string? f = VidEffects.AudioFilter(e);
                    if (f != null) ch.Append(',').Append(f);
                }
                for (double s = speed; Math.Abs(s - 1) > 0.001;)
                {
                    double step = s > 2 ? 2 : s < 0.5 ? 0.5 : s;
                    ch.Append($",atempo={F(step)}");
                    s /= step;
                }
                string? kv = Keyed(c, "Volume", a, "t");
                if (kv != null) ch.Append($",volume=volume='clip({kv},0,4)':eval=frame");
                else if (Math.Abs(c.Volume - 1) > 0.001) ch.Append($",volume={F(c.Volume)}");
                double local = b - a;
                if (c.FadeIn > 0 && a <= c.Start + 0.0005) ch.Append($",afade=t=in:st=0:d={F(Math.Min(c.FadeIn, local))}");
                if (c.FadeOut > 0) ch.Append($",afade=t=out:st={F(Math.Max(0, c.End - c.FadeOut - a))}:d={F(c.FadeOut)}");
                long delay = (long)Math.Round((a - T0) * 1000);
                if (delay > 0) ch.Append($",adelay={delay}:all=1");
                string al = "a" + idx;
                x.Script.Append(ch).Append($"[{al}];\n");
                labels.Add(al);
            }
            if (labels.Count == 0) continue;
            var tl = new StringBuilder();
            tl.Append(string.Concat(labels.Select(l => "[" + l + "]")));
            if (labels.Count == 1) tl.Append("anull");
            else tl.Append($"amix=inputs={labels.Count}:normalize=0:duration=longest:dropout_transition=0");
            if (Math.Abs(track.Volume - 1) > 0.001) tl.Append($",volume={F(Math.Clamp(track.Volume, 0, 4))}");
            if (Math.Abs(track.Pan) > 0.001)
            {
                double l = track.Pan <= 0 ? 1 : 1 - track.Pan, r = track.Pan >= 0 ? 1 : 1 + track.Pan;
                tl.Append($",pan=stereo|c0={F(l)}*c0|c1={F(r)}*c1");
            }
            string label = "tr" + trackNo;
            x.Script.Append(tl).Append($"[{label}];\n");
            trackLabels.Add(label);
        }
        x.Script.Append($"anullsrc=r={AudioRate}:cl=stereo,atrim=duration={F(dur)}[asil];\n");
        if (trackLabels.Count == 0) x.Script.Append("[asil]anull[aout];\n");
        else
        {
            x.Script.Append("[asil]").Append(string.Concat(trackLabels.Select(l => "[" + l + "]")));
            x.Script.Append($"amix=inputs={trackLabels.Count + 1}:normalize=0:duration=first:dropout_transition=0,alimiter=limit=0.98[aout];\n");
        }
    }
}
