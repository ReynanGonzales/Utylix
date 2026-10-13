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

    public static FfJob Video(VidProject p, double t0, double t1, int w, int h, string pixFmt, int gifFps = 0) => Build(p, t0, t1, w, h, pixFmt, video: true, audio: false, gifFps);
    public static FfJob Audio(VidProject p, double t0, double t1) => Build(p, t0, t1, 0, 0, "yuv420p", video: false, audio: true, 0);
    public static FfJob Both(VidProject p, double t0, double t1, int w, int h, string pixFmt) => Build(p, t0, t1, w, h, pixFmt, video: true, audio: true, 0);

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
    private static FfJob Build(VidProject p, double t0, double t1, int w, int h, string pixFmt, bool video, bool audio, int gifFps)
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
            if (gifFps > 0)
                script.Append($"[{x.Last}]format=rgb24,fps={gifFps},split[gs0][gs1];[gs0]palettegen=stats_mode=diff[gpal];[gs1][gpal]paletteuse=dither=bayer:bayer_scale=4[vout];\n");
            else script.Append($"[{x.Last}]format={pixFmt}[vout];\n");
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
    private static bool IsDip(TransitionKind k) => k is TransitionKind.DipToBlack or TransitionKind.DipToWhite;
    private static bool IsPush(TransitionKind k) => k is TransitionKind.PushLeft or TransitionKind.PushRight or TransitionKind.PushUp or TransitionKind.PushDown;
    private static bool IsSlide(TransitionKind k) => k is TransitionKind.SlideLeft or TransitionKind.SlideRight or TransitionKind.SlideUp or TransitionKind.SlideDown;

    /// <summary>
    /// A fade in or out of the picture (to / from transparency, or to / from black or white). ffmpeg's own fade filter refuses a start before the
    /// beginning of the window, which is where a single preview frame or a playback that starts inside a fade has it; then the same fade is worked
    /// out per pixel from the time (slower, but only used for those windows).
    /// </summary>
    private static string FadeFilter(bool fadeIn, double st, double d, string? color)
    {
        d = Math.Max(0.001, d);
        if (st >= 0) return $",fade=t={(fadeIn ? "in" : "out")}:st={F(st)}:d={F(d)}" + (color != null ? $":color={color}" : ":alpha=1");
        string prog = $"clip((T-({F(st)}))/{F(d)},0,1)";
        string v = fadeIn ? prog : $"(1-{prog})";                          // how much of the picture is there
        if (color == null) return $",geq=lum='lum(X,Y)':cb='cb(X,Y)':cr='cr(X,Y)':a='alpha(X,Y)*{v}'";
        string luma = color == "white" ? $"235-(235-lum(X,Y))*{v}" : $"16+(lum(X,Y)-16)*{v}";
        // (an explicit pixel format first: left to itself ffmpeg may run geq on RGB planes, where lum / cb / cr mean something else)
        return $",format=yuva420p,geq=lum='{luma}':cb='128+(cb(X,Y)-128)*{v}':cr='128+(cr(X,Y)-128)*{v}':a='alpha(X,Y)'";
    }

    /// <summary>
    /// Moves the picture of the chain <paramref name="ch"/> by a transform of its four corners (scale about the middle by zx / zy, turned by an angle in
    /// radians), at the SAME frame size, with transparency outside: the picture goes through <c>perspective</c> (fast), and a white-on-black frame goes
    /// through the same transform to become the new outline (it is multiplied into the alpha). The expressions get the progress 0 .. 1 of the transition
    /// at the stream's own frame (<paramref name="timeOffset"/> = window time of its first frame). The chain is ended and a new one started from the result.
    /// </summary>
    private static void Transform(Ctx x, StringBuilder ch, string tag, double timeOffset, double start, double length, Func<string, (string Zx, string Zy, string? Ang)> motion)
    {
        string n = tag + (++x.Layer);
        string pg = $"clip((({F(timeOffset)}+in/{F(x.Fps)})-({F(start)}))/{F(Math.Max(0.05, length))},0,1)";
        var (zx, zy, ang) = motion(pg);
        string hx = $"max({zx},0.02)*W/2", hy = $"max({zy},0.02)*H/2";
        string Px(int sx, int sy) => ang == null ? $"W/2+({sx})*{hx}" : $"W/2+({sx})*{hx}*cos({ang})-({sy})*{hy}*sin({ang})";
        string Py(int sx, int sy) => ang == null ? $"H/2+({sy})*{hy}" : $"H/2+({sx})*{hx}*sin({ang})+({sy})*{hy}*cos({ang})";
        string quad = $"x0='{Px(-1, -1)}':y0='{Py(-1, -1)}':x1='{Px(1, -1)}':y1='{Py(1, -1)}':x2='{Px(-1, 1)}':y2='{Py(-1, 1)}':x3='{Px(1, 1)}':y3='{Py(1, 1)}':sense=destination:eval=frame";
        double dur = Math.Max(0.1, x.T1 - x.T0);
        x.Script.Append(ch).Append($"[ta{n}];\n");
        x.Script.Append($"color=c=black:s={x.W}x{x.H}:r={F(x.Fps)}:d={F(dur)},format=gray,drawbox=x=1:y=1:w={x.W - 2}:h={x.H - 2}:color=white:t=fill[mk{n}];\n");
        x.Script.Append($"[mk{n}][ta{n}]scale2ref[mm{n}][tb{n}];\n");
        x.Script.Append($"[mm{n}]perspective={quad}[mp{n}];\n");
        x.Script.Append($"[tb{n}]format=yuva420p,perspective={quad}[sp{n}];\n");
        x.Script.Append($"[sp{n}]split[sq{n}][sr{n}];[sr{n}]alphaextract[sa{n}];[sa{n}][mp{n}]blend=all_mode=multiply[am{n}];[sq{n}][am{n}]alphamerge[td{n}];\n");
        ch.Clear();
        ch.Append($"[td{n}]null");
    }

    // NOTE on "enable": frame times are rounded down to the stream's time base (1/15360 s), so a frame that is exactly at the start of a window can
    // be a hair BEFORE the number written in the expression: 'between(t, start, ...)' was false for the first frames of a mask transition and the whole
    // new picture showed for two frames. The streams only start at the start of their transition, so the windows are open at the start ('lte').

    /// <summary>A blur that is only on between two times of the window (whip pans, shake, blur dissolve); the strength follows the picture size.</summary>
    private static string BlurWindow(Ctx x, double start, double end, double sigmaAt1080)
        => $",gblur=sigma={F(Math.Max(2, x.W / 1920.0 * sigmaAt1080))}:enable='between(t,{F(start - 0.02)},{F(end)})'";

    /// <summary>Progress 0..1 of a transition as an ffmpeg expression in window time (variable <paramref name="tv"/>).</summary>
    private static string Progress(double start, double length, string tv) => $"clip(({tv}-{F(start)})/{F(Math.Max(0.05, length))},0,1)";

    private static void AddVideoClip(Ctx x, Track track, Clip c)
    {
        var m = x.P.MediaOf(c);
        if (m == null || !m.HasVideo) return;
        double T0 = x.T0, T1 = x.T1, fps = x.Fps;

        // the clip that comes in over this one (it has to stay visible under it, and push / slide transitions move it)
        var next = track.Clips.FirstOrDefault(o => o != c && Math.Abs(o.Start - c.End) < 0.002 && o.Transition != TransitionKind.None);
        double nextL = next != null ? Math.Max(0.05, next.TransitionLength) : 0;
        // a transition centred on the cut (or ending at it) has part of itself BEFORE the cut: this clip only has to stay visible for the rest
        double nextLead = next != null ? track.TransitionLead(next) : 0;
        double ext = next != null && !IsDip(next.Transition) ? Math.Max(0, nextL - nextLead) : 0;
        double visEnd = c.End + ext;
        // and the same clip comes in earlier by its own lead (its picture before the start is the file's picture before "In", held when there is none)
        double shift = IsDip(c.Transition) ? 0 : track.TransitionLead(c);
        double vStart = c.Start - shift;
        if (visEnd <= T0 || vStart >= T1) return;

        bool frozen = c.End <= T0;                                           // only the held / handle part is inside the window
        double a = frozen ? T0 : Math.Max(vStart, T0);
        double b = frozen ? T0 : Math.Min(c.End, T1);
        if (!frozen && b - a < 0.001) return;
        double speed = c.Speed > 0 ? c.Speed : 1;
        int idx = x.Inputs++;
        double tail, headPad = 0;
        if (frozen)
        {
            // the end of the clip may have more picture after it in the file (a "handle"): use it; hold the last picture for what is missing
            double handleAvail = m.Kind == MediaKind.Image ? 0 : Math.Max(0, (m.Duration - (c.In + c.SourceLength)) / speed);
            double need = Math.Min(visEnd, T1) - T0;
            double handleStart = T0 - c.End;
            double use = Math.Clamp(Math.Min(need, handleAvail - handleStart), 0, need);
            if (m.Kind == MediaKind.Image)
            {
                double read = 2.0 / fps;
                x.Job.Args.AddRange(new[] { "-loop", "1", "-framerate", F(fps), "-t", F(read), "-i", m.Path });
                tail = need;
            }
            else if (use > 0.05)
            {
                x.Job.Args.AddRange(new[] { "-ss", F(c.In + (c.Length + handleStart) * speed), "-t", F(use * speed), "-i", m.Path });
                tail = need - use;
            }
            else
            {
                double read = 2.0 / fps;
                x.Job.Args.AddRange(new[] { "-ss", F(Math.Max(0, c.In + Math.Max(0, c.Length - read) * speed)), "-t", F(read * speed), "-i", m.Path });
                tail = need;
            }
        }
        else
        {
            double handle = 0;
            if (b >= c.End - 1e-6 && ext > 0 && m.Kind != MediaKind.Image)
            {
                double avail = Math.Max(0, (m.Duration - (c.In + c.SourceLength)) / speed);
                handle = Math.Clamp(Math.Min(avail, Math.Min(ext, T1 - c.End)), 0, ext);
                if (handle < 0.05) handle = 0;
            }
            double srcStart = c.In + (a - c.Start) * speed;
            if (srcStart < 0 && m.Kind != MediaKind.Image) { headPad = -srcStart / speed; srcStart = 0; }          // before the start of the file: hold its first picture
            if (m.Kind == MediaKind.Image) x.Job.Args.AddRange(new[] { "-loop", "1", "-framerate", F(fps), "-t", F(b - a + Math.Min(ext, Math.Max(0, T1 - c.End))), "-i", m.Path });
            else x.Job.Args.AddRange(new[] { "-ss", F(srcStart), "-t", F(Math.Max(0.04, b - a - headPad + handle) * speed), "-i", m.Path });
            double wanted = b >= c.End - 1e-6 ? Math.Min(visEnd, T1) - b : 0;
            tail = m.Kind == MediaKind.Image ? 0 : Math.Max(0, wanted - handle);
        }

        var ch = new StringBuilder();
        ch.Append($"[{idx}:v]scale={x.W}:{x.H}:force_original_aspect_ratio=decrease,setsar=1");
        foreach (var e in c.Effects)
        {
            string? f = VidEffects.VideoFilter(e);
            if (f != null) ch.Append(',').Append(f);
        }
        ch.Append($",setpts=(PTS-STARTPTS)/{F(speed)},fps={F(fps)}");
        if (tail > 0.0005 || headPad > 0.0005)
        {
            var pad = new List<string>();
            if (headPad > 0.0005) pad.Add($"start_mode=clone:start_duration={F(headPad)}");
            if (tail > 0.0005) pad.Add($"stop_mode=clone:stop_duration={F(tail)}");
            ch.Append(",tpad=").Append(string.Join(":", pad));
        }
        ch.Append($",setpts=PTS+{F(a - T0)}/TB");

        var kScale = Keyed(c, "Scale", T0, "t");
        var kRot = Keyed(c, "Rotation", T0, "t");
        var kOpacity = Keyed(c, "Opacity", T0, "T");
        bool rotated = kRot != null || Math.Abs(c.Rotation) > 0.01;
        var tr = c.Transition;
        var trDir = VidTransitionDefs.Direction(tr);                        // a whip pan moves like a push (and gets blurred)
        double trL = Math.Max(0.05, c.TransitionLength);
        double ts0 = c.Start - T0;                                          // the clip's own start
        double ts = ts0 - shift;                                            // where its transition starts
        bool flip = tr is TransitionKind.FlipH or TransitionKind.FlipV;
        bool maskTransition = IsWipe(tr) || tr is TransitionKind.Iris or TransitionKind.BarnDoors or TransitionKind.BarnDoorsV || VidTransitionDefs.HasMask(tr) || flip;
        bool fadeKind = tr is TransitionKind.Dissolve or TransitionKind.ZoomIn or TransitionKind.ZoomOut or TransitionKind.ZoomThrough or TransitionKind.BlurDissolve or TransitionKind.Shake;
        bool spin = tr == TransitionKind.Spin;
        bool alpha = kOpacity != null || c.Opacity < 0.999 || c.FadeIn > 0 || c.FadeOut > 0 || rotated || maskTransition || fadeKind || spin;
        if (alpha) ch.Append(",format=yuva420p");

        // size: its own scale (maybe animated), and some transitions grow or shrink it
        string pTr = Progress(ts, trL, "t");
        string? scaleExpr = kScale;
        string baseScale = kScale ?? F(c.Scale);
        if (tr == TransitionKind.ZoomIn) scaleExpr = $"({baseScale})*(0.2+0.8*{pTr})";
        if (scaleExpr != null) ch.Append($",scale=w='max(2,trunc(iw*({scaleExpr})/2)*2)':h='max(2,trunc(ih*({scaleExpr})/2)*2)':eval=frame");
        else if (Math.Abs(c.Scale - 1) > 0.001) ch.Append($",scale=max(2\\,trunc(iw*{F(c.Scale)}/2)*2):max(2\\,trunc(ih*{F(c.Scale)}/2)*2)");
        if (rotated)
        {
            string ang = kRot != null ? $"({kRot})*PI/180" : F(c.Rotation * Math.PI / 180);
            ch.Append($",rotate=a='{ang}':ow='trunc(hypot(iw,ih)/2)*2':oh=ow:c=none");
        }
        if (kOpacity != null) ch.Append($",geq=lum='lum(X,Y)':cb='cb(X,Y)':cr='cr(X,Y)':a='alpha(X,Y)*clip({kOpacity},0,1)'");
        else if (c.Opacity < 0.999) ch.Append($",colorchannelmixer=aa={F(Math.Clamp(c.Opacity, 0, 1))}");
        if (c.FadeIn > 0) ch.Append(FadeFilter(true, ts0, c.FadeIn, null));
        if (c.FadeOut > 0) ch.Append(FadeFilter(false, c.End - c.FadeOut - T0, c.FadeOut, null));

        // zoom out / zoom through / spin / flip: the whole picture is moved by a corner transform (see Transform): same frame size, fast
        if (tr is TransitionKind.ZoomOut or TransitionKind.ZoomThrough or TransitionKind.Spin or TransitionKind.FlipH or TransitionKind.FlipV)
        {
            double off = a - T0;                                                // the window time of this stream's first frame
            switch (tr)
            {
                case TransitionKind.ZoomOut: Transform(x, ch, "i", off, ts, trL, pg => ($"(1.6-0.6*{pg})", $"(1.6-0.6*{pg})", null)); break;
                case TransitionKind.ZoomThrough: Transform(x, ch, "i", off, ts, trL, pg => ($"(0.6+0.4*{pg})", $"(0.6+0.4*{pg})", null)); break;
                case TransitionKind.Spin: Transform(x, ch, "i", off, ts, trL, pg => ($"{pg}", $"{pg}", $"(1-{pg})*{F(4 * Math.PI)}")); break;
                case TransitionKind.FlipH: Transform(x, ch, "i", off, ts, trL, pg => ($"clip(({pg}-0.5)*2,0,1)", "1", null)); break;
                case TransitionKind.FlipV: Transform(x, ch, "i", off, ts, trL, pg => ("1", $"clip(({pg}-0.5)*2,0,1)", null)); break;
            }
            // (a flip shows nothing of the new picture until the old one has turned away)
            if (flip) ch.Append($",colorchannelmixer=aa=0:enable='lte(t,{F(ts + trL / 2)})'");
        }

        // the way this clip comes in
        switch (tr)
        {
            case TransitionKind.Dissolve:
            case TransitionKind.ZoomIn:
            case TransitionKind.ZoomOut:
            case TransitionKind.ZoomThrough:
            case TransitionKind.BlurDissolve:
                ch.Append(FadeFilter(true, ts, trL, null));
                break;
            case TransitionKind.Shake:
                ch.Append(FadeFilter(true, ts, trL * 0.4, null));         // it does not pop in: it fades in quickly while it shakes
                break;
            case TransitionKind.DipToBlack:
                ch.Append(FadeFilter(true, ts, trL / 2, "black"));
                break;
            case TransitionKind.DipToWhite:
                ch.Append(FadeFilter(true, ts, trL / 2, "white"));
                break;
            case TransitionKind.WipeRight:
            case TransitionKind.WipeLeft:
            case TransitionKind.WipeDown:
            case TransitionKind.WipeUp:
            case TransitionKind.Iris:
            case TransitionKind.BarnDoors:
            case TransitionKind.BarnDoorsV:
            {
                string p = $"clip((T-{F(ts)})/{F(trL)},0,1)";
                string visible = tr switch
                {
                    TransitionKind.WipeRight => $"lte(X,W*{p})",
                    TransitionKind.WipeLeft => $"gte(X,W*(1-{p}))",
                    TransitionKind.WipeDown => $"lte(Y,H*{p})",
                    TransitionKind.WipeUp => $"gte(Y,H*(1-{p}))",
                    TransitionKind.Iris => $"lte(hypot(X-W/2,Y-H/2),hypot(W/2,H/2)*{p})",
                    TransitionKind.BarnDoors => $"lte(abs(X-W/2),W/2*{p})",
                    _ => $"lte(abs(Y-H/2),H/2*{p})",
                };
                // (gt(p,0): at progress 0 nothing may show - "lte(X, W*0)" is still true for the first column)
                ch.Append($",geq=lum='lum(X,Y)':cb='cb(X,Y)':cr='cr(X,Y)':a='alpha(X,Y)*({visible})*gt({p},0)':enable='lte(t,{F(ts + trL)})'");
                break;
            }
        }
        if (VidTransitionDefs.HasMask(tr))
        {
            string pm = $"clip((T-({F(ts)}))/{F(trL)},0,1)";
            string visible = VidTransitionDefs.Mask(tr, pm);
            ch.Append($",geq=lum='lum(X,Y)':cb='cb(X,Y)':cr='cr(X,Y)':a='alpha(X,Y)*({visible})*gt({pm},0)':enable='lte(t,{F(ts + trL)})'");
        }
        // blur while it moves (whip pans, shake) or while it dissolves
        if (VidTransitionDefs.IsWhip(tr) || tr is TransitionKind.Shake or TransitionKind.BlurDissolve)
            ch.Append(BlurWindow(x, ts, ts + trL, VidTransitionDefs.IsWhip(tr) ? 22 : tr == TransitionKind.Shake ? 8 : 14));
        // and the way it goes out when the next clip dips (to black or to white)
        if (next != null && IsDip(next.Transition))
            ch.Append(FadeFilter(false, c.End - nextL / 2 - T0, nextL / 2, next.Transition == TransitionKind.DipToWhite ? "white" : "black"));

        // and what the next clip's transition does to this one while it leaves
        if (next != null)
        {
            var nk = next.Transition;
            double ws = next.Start - T0 - nextLead, we = ws + nextL;
            string q = Progress(ws, nextL, "t");
            if (VidTransitionDefs.IsWhip(nk) || nk == TransitionKind.BlurDissolve) ch.Append(BlurWindow(x, ws, we, VidTransitionDefs.IsWhip(nk) ? 22 : 14));
            double offOut = a - T0;
            if (nk == TransitionKind.ZoomThrough)
                Transform(x, ch, "o", offOut, ws, nextL, pg => ($"(1+0.6*{pg})", $"(1+0.6*{pg})", null));
            if (nk == TransitionKind.FlipH)
                Transform(x, ch, "o", offOut, ws, nextL, pg => ($"(1-clip({pg}*2,0,1))", "1", null));
            if (nk == TransitionKind.FlipV)
                Transform(x, ch, "o", offOut, ws, nextL, pg => ("1", $"(1-clip({pg}*2,0,1))", null));
            // (a flip: the old picture is gone once it has turned away)
            if (nk is TransitionKind.FlipH or TransitionKind.FlipV)
                ch.Append($",colorchannelmixer=aa=0:enable='gte(t,{F(ws + nextL / 2)})'");
        }

        string vl = "v" + idx;
        x.Script.Append(ch).Append($"[{vl}];\n");

        // place it; push and slide transitions move the picture (both this one and the one it comes over)
        string kx = Keyed(c, "X", T0, "t") ?? F(c.X);
        string ky = Keyed(c, "Y", T0, "t") ?? F(c.Y);
        string offX = "0", offY = "0";
        if (tr == TransitionKind.Shake)
        {
            string inv = $"(1-{pTr})";                                          // it lands and shakes, calming down
            offX = $"(0.03*sin(60*t)*{inv})";
            offY = $"(0.02*sin(47*t+1)*{inv})";
        }
        if (IsPush(trDir) || IsSlide(trDir))
        {
            string inv = $"(1-{Progress(ts, trL, "t")})";                       // 1 -> 0 while it comes in
            switch (trDir)
            {
                case TransitionKind.PushLeft: case TransitionKind.SlideLeft: offX = inv; break;           // enters from the right
                case TransitionKind.PushRight: case TransitionKind.SlideRight: offX = $"(-{inv})"; break;
                case TransitionKind.PushUp: case TransitionKind.SlideUp: offY = inv; break;               // enters from below
                case TransitionKind.PushDown: case TransitionKind.SlideDown: offY = $"(-{inv})"; break;
            }
        }
        if (next != null && IsPush(VidTransitionDefs.Direction(next.Transition)))
        {
            string go = Progress(next.Start - T0 - nextLead, nextL, "t");         // 0 -> 1 while the next one pushes this one out
            switch (VidTransitionDefs.Direction(next.Transition))
            {
                case TransitionKind.PushLeft: offX = $"(-{go})"; break;
                case TransitionKind.PushRight: offX = go; break;
                case TransitionKind.PushUp: offY = $"(-{go})"; break;
                case TransitionKind.PushDown: offY = go; break;
            }
        }
        string nextLabel = "o" + (++x.Layer);
        x.Script.Append($"[{x.Last}][{vl}]overlay=x='main_w*(({kx})+{offX})-overlay_w/2':y='main_h*(({ky})+{offY})-overlay_h/2':eof_action=pass:format=auto[{nextLabel}];\n");
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
        if (c.FadeIn > 0) ch.Append(FadeFilter(true, c.Start - T0, c.FadeIn, null));
        if (c.FadeOut > 0) ch.Append(FadeFilter(false, c.End - c.FadeOut - T0, c.FadeOut, null));
        if (c.Transition != TransitionKind.None)
            ch.Append(FadeFilter(true, c.Start - T0, Math.Max(0.05, c.TransitionLength), null));
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
                // a transition on the picture fades the linked sound in (and the sound of the clip before it out), so the cut does not click
                double fadeIn = c.FadeIn, fadeOut = c.FadeOut;
                if (c.LinkId != null)
                {
                    var partner = P.Tracks.Where(t => t.Kind == TrackKind.Video).SelectMany(t => t.Clips).FirstOrDefault(o => o != c && o.LinkId == c.LinkId);
                    if (partner != null)
                    {
                        if (partner.Transition != TransitionKind.None) fadeIn = Math.Max(fadeIn, Math.Min(Math.Max(0.1, partner.TransitionLength), c.Length / 2));
                        var vtrack = P.TrackOf(partner);
                        var following = vtrack?.Clips.FirstOrDefault(o => o != partner && Math.Abs(o.Start - partner.End) < 0.002 && o.Transition != TransitionKind.None);
                        if (following != null) fadeOut = Math.Max(fadeOut, Math.Min(Math.Max(0.1, following.TransitionLength), c.Length / 2));
                    }
                }
                if (fadeIn > 0 && a <= c.Start + 0.0005) ch.Append($",afade=t=in:st=0:d={F(Math.Min(fadeIn, local))}");
                if (fadeOut > 0) ch.Append($",afade=t=out:st={F(Math.Max(0, c.End - fadeOut - a))}:d={F(fadeOut)}");
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
