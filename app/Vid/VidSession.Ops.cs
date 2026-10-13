using System;
using System.Collections.Generic;
using System.Linq;

namespace IdmClone.Vid;

/// <summary>The editing operations of a real timeline: markers, in / out, lift and extract, insert and overwrite, ripple / rolling / slip,
/// copy and paste, effects, transitions, keyframes and track settings.</summary>
public sealed partial class VidSession
{
    private const double Eps = 1e-6;

    // ---------- markers ----------
    public Marker AddMarker(double t, string name = "")
    {
        var m = new Marker { Time = Math.Max(0, t), Name = name };
        Do("Add a marker", () => Project.Markers.Add(m));
        return m;
    }

    public void RemoveMarker(Marker m) => Do("Delete a marker", () => Project.Markers.Remove(m));

    public void ClearMarkers() { if (Project.Markers.Count > 0) Do("Clear the markers", () => Project.Markers.Clear()); }

    public void RenameMarker(Marker m, string name) => Do("Rename a marker", () => m.Name = name);

    // ---------- work area (In / Out) ----------
    public void SetWorkIn(double t) => Do("Set the In point", () =>
    {
        Project.WorkIn = Math.Max(0, t);
        if (Project.WorkOut is double o && o <= t + 0.01) Project.WorkOut = null;
    });

    public void SetWorkOut(double t) => Do("Set the Out point", () =>
    {
        Project.WorkOut = Math.Max(0, t);
        if (Project.WorkIn is double i && i >= t - 0.01) Project.WorkIn = null;
    });

    public void ClearWorkArea() { if (Project.WorkIn != null || Project.WorkOut != null) Do("Clear In and Out", () => { Project.WorkIn = null; Project.WorkOut = null; }); }

    /// <summary>The range the In / Out points enclose (the whole sequence when one is missing).</summary>
    public (double From, double To) WorkRange()
    {
        double from = Project.WorkIn ?? 0;
        double to = Project.WorkOut ?? Project.Duration;
        return (from, Math.Max(from, to));
    }

    // ---------- cutting a range out ----------
    private static void TrimFront(Clip c, double newStart)
    {
        double d = newStart - c.Start;
        if (Math.Abs(d) < Eps) return;
        c.In += d * c.Speed;
        c.Length -= d;
        c.Start = newStart;
        foreach (var list in c.Keys.Values) foreach (var k in list) k.T -= d;       // keyframes stay where they were on the timeline
        c.FadeIn = Math.Min(c.FadeIn, c.Length);
    }

    private static void TrimBack(Clip c, double newEnd)
    {
        c.Length = Math.Max(0, newEnd - c.Start);
        c.FadeOut = Math.Min(c.FadeOut, c.Length);
    }

    /// <summary>Takes the time range out of the given tracks (clips are cut, trimmed or removed). Returns the clips that now begin at <paramref name="b"/>.</summary>
    private void CutRange(double a, double b, IEnumerable<Track> tracks)
    {
        foreach (var track in tracks)
        {
            foreach (var c in track.Clips.ToList())
            {
                if (c.End <= a + Eps || c.Start >= b - Eps) continue;
                bool startsBefore = c.Start < a - Eps, endsAfter = c.End > b + Eps;
                if (!startsBefore && !endsAfter) { track.Clips.Remove(c); continue; }
                if (startsBefore && endsAfter)
                {
                    var right = JsonCopy(c);
                    right.Id = Guid.NewGuid().ToString("N");
                    if (c.LinkId != null) right.LinkId = c.LinkId + "-r" + Guid.NewGuid().ToString("N")[..6];
                    right.Transition = TransitionKind.None;
                    right.FadeIn = 0;
                    TrimFront(right, b);
                    TrimBack(c, a);
                    c.FadeOut = 0;
                    track.Clips.Add(right);
                }
                else if (startsBefore) { TrimBack(c, a); c.FadeOut = 0; }
                else { TrimFront(c, b); c.FadeIn = 0; c.Transition = TransitionKind.None; }
            }
        }
    }

    private IEnumerable<Track> OpenTracks() => Project.Tracks.Where(t => !t.Locked);

    /// <summary>Moves everything that starts at or after <paramref name="from"/> by <paramref name="by"/> seconds on the given tracks.</summary>
    private static void Shift(IEnumerable<Track> tracks, double from, double by)
    {
        foreach (var t in tracks)
            foreach (var c in t.Clips)
                if (c.Start >= from - Eps) c.Start = Math.Max(0, c.Start + by);
    }

    /// <summary>Lift: remove what is between In and Out and leave a gap.</summary>
    public bool Lift()
    {
        var (a, b) = WorkRange();
        if (b - a < 0.02 || Project.WorkIn == null && Project.WorkOut == null) return false;
        Do("Lift", () => CutRange(a, b, OpenTracks()));
        return true;
    }

    /// <summary>Extract: remove what is between In and Out and close the gap.</summary>
    public bool Extract()
    {
        var (a, b) = WorkRange();
        if (b - a < 0.02 || Project.WorkIn == null && Project.WorkOut == null) return false;
        Do("Extract", () =>
        {
            var tracks = OpenTracks().ToList();
            CutRange(a, b, tracks);
            Shift(tracks, b, -(b - a));
            Project.WorkIn = null; Project.WorkOut = null;
        });
        return true;
    }

    // ---------- insert and overwrite (from the Source monitor) ----------
    /// <summary>
    /// Puts the part of a file between <paramref name="srcIn"/> and <paramref name="srcOut"/> on the timeline at a time. Insert pushes everything after it
    /// to the right; overwrite replaces what was there.
    /// </summary>
    public IReadOnlyList<Clip> Place(MediaItem m, double srcIn, double srcOut, double at, bool overwrite, int? videoTrack = null, int? audioTrack = null)
    {
        var added = new List<Clip>();
        double len = Math.Max(MinLength, srcOut - srcIn);
        if (m.Kind != MediaKind.Image) len = Math.Min(len, Math.Max(MinLength, m.Duration - srcIn));
        at = Math.Max(0, at);
        Do(overwrite ? "Overwrite" : "Insert", () =>
        {
            if (m.Kind == MediaKind.Video && m.Width > 0 && !Project.AllClips.Any())
            {
                double scale = Math.Min(1, Math.Min(3840.0 / m.Width, 2160.0 / m.Height));
                Project.Width = Math.Max(2, (int)Math.Round(m.Width * scale) / 2 * 2);
                Project.Height = Math.Max(2, (int)Math.Round(m.Height * scale) / 2 * 2);
                double[] standard = { 24, 25, 30, 48, 50, 60 };
                if (m.Fps > 0) Project.Fps = standard.OrderBy(f => Math.Abs(f - m.Fps)).First();
            }
            Track? vt = m.HasVideo ? FirstTrack(TrackKind.Video, videoTrack) : null;
            Track? at2 = m.HasAudio ? FirstTrack(TrackKind.Audio, m.HasVideo ? audioTrack : (audioTrack ?? videoTrack)) : null;
            var targets = new[] { vt, at2 }.Where(t => t != null).Select(t => t!).ToList();
            if (overwrite) CutRange(at, at + len, targets);
            else
            {
                // open a gap on every unlocked track: clips under the cut are split, everything after moves right
                var open = OpenTracks().ToList();
                CutRange(at, at, open);
                foreach (var t in open)
                    foreach (var c in t.Clips.ToList())
                        if (c.Start < at - Eps && c.End > at + Eps)
                        {
                            var right = JsonCopy(c);
                            right.Id = Guid.NewGuid().ToString("N");
                            right.Transition = TransitionKind.None; right.FadeIn = 0;
                            TrimFront(right, at);
                            TrimBack(c, at); c.FadeOut = 0;
                            t.Clips.Add(right);
                        }
                Shift(open, at, len);
            }
            string? link = vt != null && at2 != null ? Guid.NewGuid().ToString("N") : null;
            string label = System.IO.Path.GetFileNameWithoutExtension(m.Path);
            if (vt != null) { var c = new Clip { MediaId = m.Id, Start = at, In = srcIn, Length = len, LinkId = link, Label = label }; vt.Clips.Add(c); added.Add(c); }
            if (at2 != null) { var c = new Clip { MediaId = m.Id, Start = at, In = srcIn, Length = len, LinkId = link, Label = label }; at2.Clips.Add(c); added.Add(c); }
        });
        return added;
    }

    // ---------- ripple, rolling, slip ----------
    /// <summary>Ripple edit of the end of a clip: it gets shorter / longer and everything after it follows.</summary>
    public void RippleTrimEnd(Clip clip, double newEnd)
    {
        var group = WithLinked(new[] { clip }).ToList();
        double lo = double.MinValue, hi = double.MaxValue;
        foreach (var c in group)
        {
            var m = Project.MediaOf(c);
            double min = c.Start + MinLength;
            double max = m != null && m.Kind != MediaKind.Image ? c.Start + (m.Duration - c.In) / c.Speed : double.MaxValue;
            double off = c.End - clip.End;
            lo = Math.Max(lo, min - off); hi = Math.Min(hi, max - off);
        }
        double target = Math.Clamp(newEnd, lo, Math.Max(lo, hi));
        double delta = target - clip.End;
        if (Math.Abs(delta) < Eps) return;
        Do("Ripple trim", () =>
        {
            var tracks = group.Select(c => Project.TrackOf(c)!).Distinct().ToList();
            double from = clip.End;
            foreach (var c in group) c.Length += delta;
            foreach (var t in OpenTracks())
                foreach (var o in t.Clips)
                    if (!group.Contains(o) && o.Start >= from - Eps) o.Start = Math.Max(0, o.Start + delta);
        });
    }

    /// <summary>Ripple edit of the start of a clip: what it shows begins later / earlier, it stays where it is, and everything after it follows.</summary>
    public void RippleTrimStart(Clip clip, double delta)
    {
        var group = WithLinked(new[] { clip }).ToList();
        double lo = double.MinValue, hi = double.MaxValue;
        foreach (var c in group)
        {
            var m = Project.MediaOf(c);
            double min = m != null && m.Kind != MediaKind.Image ? -c.In / c.Speed : -1e9;       // can't go before the start of the file
            double max = c.Length - MinLength;
            lo = Math.Max(lo, min); hi = Math.Min(hi, max);
        }
        delta = Math.Clamp(delta, lo, Math.Max(lo, hi));
        if (Math.Abs(delta) < Eps) return;
        Do("Ripple trim", () =>
        {
            double from = clip.End;
            foreach (var c in group)
            {
                c.In += delta * c.Speed;
                c.Length -= delta;
                // keyframes keep their place on the timeline relative to what is shown
                foreach (var list in c.Keys.Values) foreach (var k in list) k.T -= 0;
            }
            foreach (var t in OpenTracks())
                foreach (var o in t.Clips)
                    if (!group.Contains(o) && o.Start >= from - Eps) o.Start = Math.Max(0, o.Start - delta);
        });
    }

    /// <summary>The clip that begins where <paramref name="left"/> ends on the same track.</summary>
    public Clip? RightNeighbour(Clip left)
    {
        var t = Project.TrackOf(left);
        return t?.Clips.FirstOrDefault(o => o != left && Math.Abs(o.Start - left.End) < 0.002);
    }

    /// <summary>Rolling edit: moves the cut between two clips; the sum of their lengths stays the same.</summary>
    public bool Roll(Clip left, double newCut, bool commit = true)
    {
        var right = RightNeighbour(left);
        if (right == null) return false;
        var pairs = new List<(Clip L, Clip R)> { (left, right) };
        foreach (var l in WithLinked(new[] { left }).Where(c => c != left))
        {
            var tr = Project.TrackOf(l);
            var r = tr?.Clips.FirstOrDefault(o => o != l && Math.Abs(o.Start - l.End) < 0.002 && WithLinked(new[] { right }).Contains(o));
            if (r != null) pairs.Add((l, r));
        }
        double lo = double.MinValue, hi = double.MaxValue;
        foreach (var (l, r) in pairs)
        {
            var lm = Project.MediaOf(l); var rm = Project.MediaOf(r);
            double cut = l.End;
            double min = l.Start + MinLength;
            double max = r.End - MinLength;
            if (lm != null && lm.Kind != MediaKind.Image) max = Math.Min(max, l.Start + (lm.Duration - l.In) / l.Speed);
            if (rm != null && rm.Kind != MediaKind.Image) min = Math.Max(min, cut - r.In / r.Speed);
            lo = Math.Max(lo, min - cut); hi = Math.Min(hi, max - cut);
        }
        double delta = Math.Clamp(newCut - left.End, lo, Math.Max(lo, hi));
        if (Math.Abs(delta) < Eps) return false;
        void Apply()
        {
            foreach (var (l, r) in pairs)
            {
                l.Length += delta;
                r.In += delta * r.Speed;
                r.Length -= delta;
                r.Start += delta;
                foreach (var list in r.Keys.Values) foreach (var k in list) k.T -= delta;
            }
        }
        if (commit) Do("Rolling edit", Apply); else Apply();
        return true;
    }

    /// <summary>Slip: shows another part of the file in the same place and length.</summary>
    public void Slip(IEnumerable<Clip> clips, double delta, bool commit = true)
    {
        var group = WithLinked(clips).ToList();
        double lo = double.MinValue, hi = double.MaxValue;
        foreach (var c in group)
        {
            var m = Project.MediaOf(c);
            if (m == null || m.Kind == MediaKind.Image) { lo = hi = 0; break; }
            lo = Math.Max(lo, -c.In / c.Speed);
            hi = Math.Min(hi, (m.Duration - c.In - c.SourceLength) / c.Speed);
        }
        delta = Math.Clamp(delta, lo, Math.Max(lo, hi));
        if (Math.Abs(delta) < Eps) return;
        void Apply() { foreach (var c in group) c.In += delta * c.Speed; }
        if (commit) Do("Slip", Apply); else Apply();
    }

    // ---------- copy and paste ----------
    private readonly List<(Clip Clip, string TrackId)> _clipboard = new();

    public bool CanPaste => _clipboard.Count > 0;

    public void Copy()
    {
        _clipboard.Clear();
        foreach (var c in WithLinked(SelectedClips))
        {
            var t = Project.TrackOf(c);
            if (t != null) _clipboard.Add((JsonCopy(c), t.Id));
        }
    }

    public void Cut()
    {
        Copy();
        if (_clipboard.Count > 0) Delete(false);
    }

    /// <summary>Puts the copied clips back at a time, on the tracks they came from (or the same kind if those are gone); they land in free space.</summary>
    public IReadOnlyList<Clip> Paste(double at)
    {
        var made = new List<Clip>();
        if (_clipboard.Count == 0) return made;
        double first = _clipboard.Min(x => x.Clip.Start);
        Do("Paste", () =>
        {
            var targets = new List<(Clip C, Track T)>();
            foreach (var (clip, trackId) in _clipboard)
            {
                var t = Project.Tracks.FirstOrDefault(x => x.Id == trackId) ?? FirstTrack(Project.MediaOf(clip) is { HasVideo: false } ? TrackKind.Audio : TrackKind.Video);
                targets.Add((clip, t));
            }
            // all the pasted clips move together to the first place where each of them fits
            double shift = Math.Max(0, at) - first;
            for (int guard = 0; guard < 400; guard++)
            {
                double push = 0;
                foreach (var (c, t) in targets)
                    foreach (var o in t.Clips)
                        if (Overlaps(c.Start + shift, c.End + shift, o.Start, o.End)) push = Math.Max(push, o.End - (c.Start + shift));
                if (push <= 0) break;
                shift += push;
            }
            var links = new Dictionary<string, string>();
            foreach (var (c, t) in targets)
            {
                var copy = JsonCopy(c);
                copy.Id = Guid.NewGuid().ToString("N");
                copy.Start = c.Start + shift;
                if (c.LinkId != null) { if (!links.TryGetValue(c.LinkId, out var l)) links[c.LinkId] = l = Guid.NewGuid().ToString("N"); copy.LinkId = l; }
                t.Clips.Add(copy);
                made.Add(copy);
            }
        });
        Select(made);
        return made;
    }

    // ---------- effects and transitions ----------
    /// <summary>Applies an effect to the clips: a picture effect to the picture clips, a sound effect to the sound clips (also those linked to a chosen picture).</summary>
    public int AddEffect(IEnumerable<Clip> clips, string type)
    {
        var info = VidEffects.Info(type);
        if (info == null) return 0;
        var targets = WithLinked(clips).Where(c =>
        {
            var t = Project.TrackOf(c);
            if (t == null || c.Kind == ClipKind.Text) return false;
            return info.Audio ? t.Kind == TrackKind.Audio : t.Kind == TrackKind.Video;
        }).ToList();
        if (targets.Count == 0) return 0;
        Do("Apply " + info.Name, () => { foreach (var c in targets) c.Effects.Add(VidEffects.Create(type)); });
        return targets.Count;
    }

    public void RemoveEffect(Clip c, ClipEffect e) => Do("Remove an effect", () => c.Effects.Remove(e));

    public void SetTransition(IEnumerable<Clip> clips, TransitionKind kind, double length)
    {
        var list = clips.Where(c => Project.TrackOf(c)?.Kind == TrackKind.Video).ToList();
        if (list.Count == 0) return;
        Do(kind == TransitionKind.None ? "Remove the transition" : "Add a transition", () =>
        {
            foreach (var c in list)
            {
                c.Transition = kind;
                c.TransitionLength = Math.Clamp(length, 0.1, Math.Max(0.1, c.Length));
            }
        });
    }

    // ---------- keyframes ----------
    public const double KeyEps = 0.017;

    public void SetKeyframe(Clip c, string prop, double timelineTime, double value) =>
        Do("Keyframe", () => PutKey(c, prop, timelineTime, value));

    /// <summary>Same, without recording an undo step (a drag that is committed later).</summary>
    public void PutKey(Clip c, string prop, double timelineTime, double value)
    {
        double t = Math.Clamp(timelineTime - c.Start, 0, c.Length);
        if (!c.Keys.TryGetValue(prop, out var list)) c.Keys[prop] = list = new List<Keyframe>();
        var at = list.FirstOrDefault(k => Math.Abs(k.T - t) < KeyEps);
        if (at != null) at.V = value; else list.Add(new Keyframe { T = t, V = value });
        list.Sort((a, b) => a.T.CompareTo(b.T));
    }

    public void RemoveKeyframe(Clip c, string prop, double timelineTime) => Do("Delete a keyframe", () =>
    {
        if (!c.Keys.TryGetValue(prop, out var list)) return;
        double t = timelineTime - c.Start;
        list.RemoveAll(k => Math.Abs(k.T - t) < KeyEps);
        if (list.Count == 0) c.Keys.Remove(prop);
    });

    /// <summary>Stops animating a property: it keeps the value it has at the given time.</summary>
    public void ClearKeyframes(Clip c, string prop, double keepValue, Action<Clip, double> setPlain) => Do("Stop animating", () =>
    {
        c.Keys.Remove(prop);
        setPlain(c, keepValue);
    });

    public static double? NextKeyTime(Clip c, string prop, double after)
    {
        if (!c.Keys.TryGetValue(prop, out var l)) return null;
        var k = l.Where(x => c.Start + x.T > after + KeyEps).OrderBy(x => x.T).FirstOrDefault();
        return k == null ? null : c.Start + k.T;
    }

    public static double? PreviousKeyTime(Clip c, string prop, double before)
    {
        if (!c.Keys.TryGetValue(prop, out var l)) return null;
        var k = l.Where(x => c.Start + x.T < before - KeyEps).OrderByDescending(x => x.T).FirstOrDefault();
        return k == null ? null : c.Start + k.T;
    }

    // ---------- tracks ----------
    public void RenameTrack(Track t, string name) => Do("Rename a track", () => t.Name = name);

    public bool DeleteTrack(Track t)
    {
        if (Project.Tracks.Count(x => x.Kind == t.Kind) <= 1) return false;
        Do("Delete a track", () => Project.Tracks.Remove(t));
        var ids = new HashSet<string>(Project.AllClips.Select(c => c.Id));
        Selected.RemoveWhere(id => !ids.Contains(id));
        return true;
    }

    // ---------- selecting more ----------
    /// <summary>Everything on a track that starts at or after a time (the "track select forward" tool).</summary>
    public IEnumerable<Clip> SelectForward(Track t, double from) => t.Clips.Where(c => c.End > from + Eps);
}
