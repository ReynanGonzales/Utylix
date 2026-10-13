using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace IdmClone.Vid;

/// <summary>
/// The project being edited plus everything about editing it: selection, playhead, undo / redo and the edit operations
/// (add, move, trim, split, delete, ripple). The window and the timeline only ask this class to change things.
/// </summary>
public sealed class VidSession
{
    public const double MinLength = 0.1;
    private const int UndoLimit = 120;

    private readonly List<string> _undo = new();
    private readonly List<string> _redo = new();
    private string? _undoLabel;

    public VidProject Project { get; private set; } = VidProject.NewEmpty();
    public HashSet<string> Selected { get; } = new();
    public double Playhead { get; set; }
    public bool Dirty { get; private set; }
    /// <summary>Raised after every change of the project (timeline, inspector and preview refresh).</summary>
    public event Action? Changed;
    public event Action? SelectionChanged;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? NextUndoLabel => _undoLabel;
    public void ClearDirty() => Dirty = false;
    public void MarkDirty() => Dirty = true;

    public void Load(VidProject p)
    {
        Project = p;
        _undo.Clear(); _redo.Clear(); _undoLabel = null;
        Selected.Clear();
        Playhead = 0;
        Dirty = false;
        Changed?.Invoke();
        SelectionChanged?.Invoke();
    }

    // ---------- history ----------
    private readonly List<string> _labels = new();

    /// <summary>Runs one edit as a single undo step.</summary>
    public void Do(string label, Action edit)
    {
        string before = Project.ToJson();
        edit();
        _undo.Add(before); _labels.Add(label);
        if (_undo.Count > UndoLimit) { _undo.RemoveAt(0); _labels.RemoveAt(0); }
        _redo.Clear();
        _undoLabel = label;
        Dirty = true;
        Changed?.Invoke();
    }

    /// <summary>For drags that change the project live (trimming): remember the project as it was, then <see cref="CommitEdit"/> when the drag ends.</summary>
    public string BeginEdit() => Project.ToJson();

    public void CommitEdit(string before, string label)
    {
        string now = Project.ToJson();
        if (now == before) { Changed?.Invoke(); return; }
        _undo.Add(before); _labels.Add(label);
        if (_undo.Count > UndoLimit) { _undo.RemoveAt(0); _labels.RemoveAt(0); }
        _redo.Clear();
        _undoLabel = label;
        Dirty = true;
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(Project.ToJson());
        Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1); _labels.RemoveAt(_labels.Count - 1);
        _undoLabel = _labels.Count > 0 ? _labels[^1] : null;
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(Project.ToJson()); _labels.Add("Redo");
        Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        _undoLabel = "Redo";
    }

    private void Restore(string json)
    {
        string? path = Project.Path;
        Project = VidProject.FromJson(json);
        Project.Path = path;
        var ids = new HashSet<string>(Project.AllClips.Select(c => c.Id));
        Selected.RemoveWhere(id => !ids.Contains(id));
        Dirty = true;
        Changed?.Invoke();
        SelectionChanged?.Invoke();
    }

    // ---------- selection ----------
    public IEnumerable<Clip> SelectedClips => Project.AllClips.Where(c => Selected.Contains(c.Id));

    public void Select(IEnumerable<Clip> clips, bool add = false)
    {
        var next = new HashSet<string>(add ? Selected : Enumerable.Empty<string>());
        foreach (var c in clips) next.Add(c.Id);
        if (next.SetEquals(Selected)) return;
        Selected.Clear();
        foreach (var id in next) Selected.Add(id);
        SelectionChanged?.Invoke();
    }

    public void ClearSelection() { if (Selected.Count == 0) return; Selected.Clear(); SelectionChanged?.Invoke(); }

    /// <summary>The clips that always move together with the given one (the picture and the sound of one video).</summary>
    public IEnumerable<Clip> WithLinked(IEnumerable<Clip> clips)
    {
        var set = new HashSet<Clip>(clips);
        var links = new HashSet<string>(set.Where(c => c.LinkId != null).Select(c => c.LinkId!));
        if (links.Count > 0) foreach (var c in Project.AllClips) if (c.LinkId != null && links.Contains(c.LinkId)) set.Add(c);
        return set;
    }

    // ---------- room on a track ----------
    private static bool Overlaps(double a0, double a1, double b0, double b1) => a0 < b1 - 1e-6 && b0 < a1 - 1e-6;

    public bool IsFree(Track track, double start, double end, IEnumerable<Clip>? ignore = null)
    {
        var skip = ignore == null ? null : ignore as ICollection<Clip> ?? ignore.ToList();
        foreach (var c in track.Clips)
        {
            if (skip != null && skip.Contains(c)) continue;
            if (Overlaps(start, end, c.Start, c.End)) return false;
        }
        return true;
    }

    /// <summary>The first start time at or after <paramref name="at"/> where a piece of this length fits on every one of these tracks.</summary>
    public double FindStart(IEnumerable<Track> tracks, double at, double length)
    {
        double start = Math.Max(0, at);
        var list = tracks.ToList();
        for (int guard = 0; guard < 500; guard++)
        {
            double push = start;
            foreach (var t in list)
                foreach (var c in t.Clips)
                    if (Overlaps(start, start + length, c.Start, c.End)) push = Math.Max(push, c.End);
            if (push <= start + 1e-9) return start;
            start = push;
        }
        return start;
    }

    // ---------- adding ----------
    public Track FirstTrack(TrackKind kind, int? index = null)
    {
        var tracks = Project.Tracks.Where(t => t.Kind == kind).ToList();
        if (index != null && index.Value >= 0 && index.Value < Project.Tracks.Count && Project.Tracks[index.Value].Kind == kind) return Project.Tracks[index.Value];
        return tracks.FirstOrDefault(t => !t.Locked) ?? tracks[0];
    }

    /// <summary>Puts a file on the timeline: its picture on a video track and its sound on an audio track, linked.</summary>
    public IReadOnlyList<Clip> AddToTimeline(MediaItem m, double at, int? trackIndex = null)
    {
        var added = new List<Clip>();
        Do("Add " + m.Name, () =>
        {
            // the first video on an empty timeline decides the canvas (size and frame rate), like editing programs do
            if (m.Kind == MediaKind.Video && m.Width > 0 && m.Height > 0 && !Project.AllClips.Any())
            {
                double scale = Math.Min(1, Math.Min(3840.0 / m.Width, 2160.0 / m.Height));
                Project.Width = Math.Max(2, (int)Math.Round(m.Width * scale) / 2 * 2);
                Project.Height = Math.Max(2, (int)Math.Round(m.Height * scale) / 2 * 2);
                double[] standard = { 24, 25, 30, 48, 50, 60 };
                if (m.Fps > 0) Project.Fps = standard.OrderBy(f => Math.Abs(f - m.Fps)).First();
            }
            double length = m.Kind == MediaKind.Image ? 5 : m.Duration;
            Track? vt = m.HasVideo ? FirstTrack(TrackKind.Video, trackIndex) : null;
            Track? at2 = m.HasAudio ? FirstTrack(TrackKind.Audio, m.HasVideo ? null : trackIndex) : null;
            var tracks = new[] { vt, at2 }.Where(t => t != null).Select(t => t!).ToList();
            double start = FindStart(tracks, at, length);
            string? link = vt != null && at2 != null ? Guid.NewGuid().ToString("N") : null;
            if (vt != null) { var c = new Clip { MediaId = m.Id, Start = start, Length = length, LinkId = link, Label = Path.GetFileNameWithoutExtension(m.Path) }; vt.Clips.Add(c); added.Add(c); }
            if (at2 != null) { var c = new Clip { MediaId = m.Id, Start = start, Length = length, LinkId = link, Label = Path.GetFileNameWithoutExtension(m.Path) }; at2.Clips.Add(c); added.Add(c); }
        });
        return added;
    }

    // ---------- moving ----------
    /// <summary>
    /// Moves clips by a time and a number of tracks (clamped, and pushed to the nearest free place when it would overlap another clip).
    /// Returns the delta that was really applied. <paramref name="commit"/> = false only previews (nothing recorded).
    /// </summary>
    public (double Time, int Tracks) Move(IReadOnlyCollection<Clip> clips, double dt, int dTracks, bool commit, double[]? snapEdges = null, double snapWithin = 0)
    {
        if (clips.Count == 0) return (0, 0);
        double minStart = clips.Min(c => c.Start);
        if (minStart + dt < 0) dt = -minStart;
        var home = clips.ToDictionary(c => c, c => Project.TrackOf(c)!);
        // tracks can only change within their own kind, and only if every clip can go (else the tracks stay)
        int trackShift = dTracks;
        Dictionary<Clip, Track>? dest = null;
        while (true)
        {
            dest = new Dictionary<Clip, Track>();
            bool ok = true;
            foreach (var c in clips)
            {
                int i = Project.Tracks.IndexOf(home[c]) + trackShift;
                if (i < 0 || i >= Project.Tracks.Count || Project.Tracks[i].Kind != home[c].Kind || Project.Tracks[i].Locked) { ok = false; break; }
                dest[c] = Project.Tracks[i];
            }
            if (ok) break;
            if (trackShift == 0) { dest = home; break; }
            trackShift += trackShift > 0 ? -1 : 1;
        }

        // snap the group's edges to the playhead / other clips' edges
        if (snapEdges != null && snapWithin > 0)
        {
            double best = snapWithin; double adjust = 0;
            foreach (var c in clips)
                foreach (double edge in new[] { c.Start + dt, c.End + dt })
                    foreach (double s in snapEdges)
                        if (Math.Abs(edge - s) < best) { best = Math.Abs(edge - s); adjust = s - edge; }
            if (best < snapWithin) dt += adjust;
            if (minStart + dt < 0) dt = -minStart;
        }

        bool Valid(double d)
        {
            foreach (var c in clips)
                if (!IsFree(dest![c], c.Start + d, c.End + d, clips)) return false;
            return true;
        }
        if (!Valid(dt))
        {
            // nearest free place: line the group up against the edges of the clips in the way
            var candidates = new List<double>();
            foreach (var c in clips)
                foreach (var other in dest![c].Clips.Where(o => !clips.Contains(o)))
                {
                    candidates.Add(other.End - c.Start);
                    candidates.Add(other.Start - c.End);
                }
            double? pick = candidates.Where(d => minStart + d >= -1e-9 && Valid(d)).OrderBy(d => Math.Abs(d - dt)).Select(d => (double?)d).FirstOrDefault();
            if (pick == null) { dt = 0; trackShift = 0; dest = home; if (!Valid(0)) return (0, 0); }
            else dt = pick.Value;
        }

        void Apply()
        {
            foreach (var c in clips)
            {
                var from = home[c]; var to = dest![c];
                c.Start = Math.Max(0, c.Start + dt);
                if (from != to) { from.Clips.Remove(c); to.Clips.Add(c); }
            }
        }
        if (commit && (Math.Abs(dt) > 1e-9 || trackShift != 0)) Do(clips.Count > 1 ? "Move clips" : "Move clip", Apply);
        return (dt, trackShift);
    }

    // ---------- trimming ----------
    /// <summary>The limits for trimming the start of a clip: earliest allowed new start and latest.</summary>
    public (double Min, double Max) StartLimits(Clip c)
    {
        var m = Project.MediaOf(c);
        var track = Project.TrackOf(c)!;
        double min = 0;
        foreach (var o in track.Clips) if (o != c && o.End <= c.Start + 1e-9) min = Math.Max(min, o.End);
        if (m != null && m.Kind != MediaKind.Image) min = Math.Max(min, c.Start - c.In / c.Speed);        // can't go before the start of the file
        return (min, c.End - MinLength);
    }

    public (double Min, double Max) EndLimits(Clip c)
    {
        var m = Project.MediaOf(c);
        var track = Project.TrackOf(c)!;
        double max = double.MaxValue;
        foreach (var o in track.Clips) if (o != c && o.Start >= c.End - 1e-9) max = Math.Min(max, o.Start);
        if (m != null && m.Kind != MediaKind.Image) max = Math.Min(max, c.Start + (m.Duration - c.In) / c.Speed);
        return (c.Start + MinLength, max);
    }

    public void TrimStart(IEnumerable<Clip> clips, double newStart, bool commit = true)
    {
        var all = WithLinked(clips).ToList();
        double lo = double.MinValue, hi = double.MaxValue;
        // the clip being dragged defines how far everybody moves
        var lead = clips.First();
        foreach (var c in all)
        {
            var (mn, mx) = StartLimits(c);
            double offset = c.Start - lead.Start;
            lo = Math.Max(lo, mn - offset); hi = Math.Min(hi, mx - offset);
        }
        double target = Math.Clamp(newStart, lo, Math.Max(lo, hi));
        double delta = target - lead.Start;
        if (Math.Abs(delta) < 1e-9) return;
        void Apply()
        {
            foreach (var c in all)
            {
                double ns = c.Start + delta;
                c.In += delta * c.Speed;
                c.Length -= delta;
                c.Start = ns;
                if (c.In < 0) { c.Length += c.In / c.Speed; c.Start -= c.In / c.Speed; c.In = 0; }
            }
        }
        if (commit) Do("Trim start", Apply); else Apply();
    }

    public void TrimEnd(IEnumerable<Clip> clips, double newEnd, bool commit = true)
    {
        var all = WithLinked(clips).ToList();
        var lead = clips.First();
        double lo = double.MinValue, hi = double.MaxValue;
        foreach (var c in all)
        {
            var (mn, mx) = EndLimits(c);
            double offset = c.End - lead.End;
            lo = Math.Max(lo, mn - offset); hi = Math.Min(hi, mx - offset);
        }
        double target = Math.Clamp(newEnd, lo, Math.Max(lo, hi));
        double delta = target - lead.End;
        if (Math.Abs(delta) < 1e-9) return;
        void Apply() { foreach (var c in all) c.Length += delta; }
        if (commit) Do("Trim end", Apply); else Apply();
    }

    // ---------- splitting ----------
    /// <summary>Cuts clips at a time: the selected ones under it, or every clip under it when nothing is selected.</summary>
    public bool Split(double t)
    {
        var targets = (Selected.Count > 0 ? WithLinked(SelectedClips) : Project.AllClips).Where(c => c.Start < t - 0.02 && c.End > t + 0.02)
            .Where(c => !(Project.TrackOf(c)?.Locked ?? false)).ToList();
        if (targets.Count == 0) return false;
        var newLinks = new Dictionary<string, string>();
        Do("Split", () =>
        {
            var created = new List<Clip>();
            foreach (var c in targets)
            {
                var track = Project.TrackOf(c)!;
                double leftLength = t - c.Start;
                var right = new Clip
                {
                    Kind = c.Kind, MediaId = c.MediaId, Start = t, In = c.In + leftLength * c.Speed, Length = c.End - t, Speed = c.Speed,
                    Volume = c.Volume, Opacity = c.Opacity, FadeIn = 0, FadeOut = c.FadeOut, X = c.X, Y = c.Y, Scale = c.Scale, Label = c.Label,
                };
                if (c.LinkId != null)
                {
                    if (!newLinks.TryGetValue(c.LinkId, out var nl)) newLinks[c.LinkId] = nl = Guid.NewGuid().ToString("N");
                    right.LinkId = nl;
                }
                c.Length = leftLength;
                c.FadeOut = 0;
                track.Clips.Add(right);
                created.Add(right);
            }
        });
        return true;
    }

    // ---------- deleting ----------
    public void Delete(bool ripple)
    {
        var clips = WithLinked(SelectedClips).ToList();
        if (clips.Count == 0) return;
        Do(ripple ? "Delete and close the gap" : "Delete", () =>
        {
            var gaps = new List<(Track Track, double Start, double Length)>();
            foreach (var c in clips)
            {
                var t = Project.TrackOf(c);
                if (t == null) continue;
                t.Clips.Remove(c);
                gaps.Add((t, c.Start, c.Length));
            }
            if (ripple)
            {
                // close each gap on its track and on the tracks of clips that were linked (same start)
                foreach (var g in gaps.OrderByDescending(g => g.Start))
                    foreach (var other in g.Track.Clips.Where(o => o.Start >= g.Start + g.Length - 1e-6).ToList())
                        other.Start -= g.Length;
            }
        });
        Selected.Clear();
        SelectionChanged?.Invoke();
    }

    public void Duplicate()
    {
        var clips = WithLinked(SelectedClips).ToList();
        if (clips.Count == 0) return;
        Do("Duplicate", () =>
        {
            double end = clips.Max(c => c.End), begin = clips.Min(c => c.Start);
            double shift = end - begin;
            var links = new Dictionary<string, string>();
            foreach (var c in clips)
            {
                var track = Project.TrackOf(c)!;
                var copy = JsonCopy(c);
                copy.Id = Guid.NewGuid().ToString("N");
                copy.Start = c.Start + shift;
                if (c.LinkId != null) { if (!links.TryGetValue(c.LinkId, out var l)) links[c.LinkId] = l = Guid.NewGuid().ToString("N"); copy.LinkId = l; }
                // push to the next free place on that track
                copy.Start = FindStart(new[] { track }, copy.Start, copy.Length);
                track.Clips.Add(copy);
            }
        });
    }

    private static Clip JsonCopy(Clip c) => System.Text.Json.JsonSerializer.Deserialize<Clip>(System.Text.Json.JsonSerializer.Serialize(c))!;

    // ---------- properties of clips (inspector) ----------
    public void SetProperty(IEnumerable<Clip> clips, string label, Action<Clip> change)
    {
        var list = clips.ToList();
        if (list.Count == 0) return;
        Do(label, () => { foreach (var c in list) change(c); });
    }

    /// <summary>Speed changes the length on the timeline; the file part used stays the same. Refused when it would hit the next clip.</summary>
    public bool SetSpeed(Clip c, double speed)
    {
        speed = Math.Clamp(speed, 0.1, 16);
        double newLength = c.SourceLength / speed;
        var track = Project.TrackOf(c)!;
        var group = WithLinked(new[] { c }).ToList();
        foreach (var g in group)
        {
            double len = g.SourceLength / speed;
            if (!IsFree(Project.TrackOf(g)!, g.Start, g.Start + len, group)) return false;
        }
        Do("Speed", () => { foreach (var g in group) { double src = g.SourceLength; g.Speed = speed; g.Length = src / speed; } });
        return true;
    }

    public void AddTrack(TrackKind kind)
    {
        Do("Add a track", () =>
        {
            int n = Project.Tracks.Count(t => t.Kind == kind) + 1;
            var t = new Track { Kind = kind, Name = (kind == TrackKind.Video ? "Video " : "Audio ") + n };
            // video tracks go above the audio ones; the newest video track is the topmost layer
            int at = kind == TrackKind.Video ? Project.Tracks.FindLastIndex(x => x.Kind == TrackKind.Video) + 1 : Project.Tracks.Count;
            Project.Tracks.Insert(at, t);
        });
    }

    public void RemoveTrackIfEmpty(Track t)
    {
        if (t.Clips.Count > 0 || Project.Tracks.Count(x => x.Kind == t.Kind) <= 1) return;
        Do("Remove a track", () => Project.Tracks.Remove(t));
    }

    /// <summary>Times the playhead and the edges of clips can snap to.</summary>
    public double[] SnapPoints(ICollection<Clip>? exclude = null)
    {
        var list = new List<double> { 0, Playhead };
        foreach (var c in Project.AllClips)
        {
            if (exclude != null && exclude.Contains(c)) continue;
            list.Add(c.Start); list.Add(c.End);
        }
        return list.ToArray();
    }
}
