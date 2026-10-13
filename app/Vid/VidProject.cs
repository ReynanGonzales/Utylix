using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IdmClone.Vid;

public enum MediaKind { Video, Audio, Image }
public enum TrackKind { Video, Audio }
public enum ClipKind { Media, Text }

/// <summary>How a clip comes in over the clip before it on the same track.</summary>
public enum TransitionKind
{
    None, Dissolve, DipToBlack, WipeRight, WipeLeft, WipeDown, WipeUp,
    // added later: new members go at the end so saved projects keep their numbers
    DipToWhite, PushLeft, PushRight, PushUp, PushDown, SlideLeft, SlideRight, SlideUp, SlideDown, Iris, BarnDoors, BarnDoorsV, ZoomIn,
}

/// <summary>A value at a time (seconds from the start of the clip, on the timeline); between two keyframes the value moves in a straight line.</summary>
public sealed class Keyframe
{
    public double T { get; set; }
    public double V { get; set; }
}

/// <summary>An effect on a clip. A, B, C, D are its numbers (what they mean depends on <see cref="Type"/>, see VidEffects).</summary>
public sealed class ClipEffect
{
    public string Type { get; set; } = "";
    public bool On { get; set; } = true;
    public double A { get; set; }
    public double B { get; set; }
    public double C { get; set; }
    public double D { get; set; }
    /// <summary>A colour as #RRGGBB (the key colour of the colour key).</summary>
    public string Color { get; set; } = "";
}

public sealed class Marker
{
    public double Time { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Hex colour like #FF4D5E.</summary>
    public string Color { get; set; } = "#22C55E";
}

/// <summary>A file the person brought in (kept once; clips on the timeline point at it by Id).</summary>
public sealed class MediaItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public string Name => System.IO.Path.GetFileName(Path);
    public MediaKind Kind { get; set; }
    public double Duration { get; set; }          // seconds (a picture: 5)
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public bool HasVideo { get; set; }
    public bool HasAudio { get; set; }
}

/// <summary>A piece on a track. Times are seconds; a clip with Speed 2 shows twice as much of the file in the same Length.</summary>
public sealed class Clip
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Clips with the same LinkId (the picture and the sound of one video) are moved, cut and trimmed together.</summary>
    public string? LinkId { get; set; }
    public ClipKind Kind { get; set; } = ClipKind.Media;
    public string? MediaId { get; set; }
    public double Start { get; set; }             // place on the timeline
    public double In { get; set; }                // where in the file it begins
    public double Length { get; set; }            // length on the timeline
    public double Speed { get; set; } = 1;
    public double Volume { get; set; } = 1;       // 0 .. 2
    public double Opacity { get; set; } = 1;      // 0 .. 1
    public double FadeIn { get; set; }
    public double FadeOut { get; set; }
    /// <summary>Centre of the picture on the canvas (0 .. 1) and its size relative to "fits inside the canvas".</summary>
    public double X { get; set; } = 0.5;
    public double Y { get; set; } = 0.5;
    public double Scale { get; set; } = 1;
    public string? Label { get; set; }
    // ---- title (Kind = Text) ----
    public string Text { get; set; } = "";
    /// <summary>Letter height as a fraction of the picture height.</summary>
    public double TextSize { get; set; } = 0.09;
    public string TextColor { get; set; } = "#FFFFFF";
    public bool TextBold { get; set; } = true;
    public string TextFont { get; set; } = "Segoe UI";
    /// <summary>A dark box behind the words (opacity 0 = none).</summary>
    public double TextBoxOpacity { get; set; }
    public bool TextOutline { get; set; }
    // ---- motion, effects, transition ----
    /// <summary>Turn in degrees (clockwise).</summary>
    public double Rotation { get; set; }
    /// <summary>Animated properties: "X", "Y", "Scale", "Rotation", "Opacity", "Volume". A property with keyframes ignores its plain value.</summary>
    public Dictionary<string, List<Keyframe>> Keys { get; set; } = new();
    public List<ClipEffect> Effects { get; set; } = new();
    public TransitionKind Transition { get; set; }
    public double TransitionLength { get; set; } = 1;

    [JsonIgnore] public double End => Start + Length;

    public bool IsAnimated(string prop) => Keys.TryGetValue(prop, out var k) && k.Count > 0;

    /// <summary>The value of a property at a time on the timeline (the plain value when it has no keyframes).</summary>
    public double ValueAt(string prop, double timelineTime, double plain)
    {
        if (!Keys.TryGetValue(prop, out var keys) || keys.Count == 0) return plain;
        var sorted = keys.OrderBy(k => k.T).ToList();
        double t = timelineTime - Start;
        if (t <= sorted[0].T) return sorted[0].V;
        for (int i = 1; i < sorted.Count; i++)
        {
            if (t <= sorted[i].T)
            {
                double span = Math.Max(1e-9, sorted[i].T - sorted[i - 1].T);
                return sorted[i - 1].V + (sorted[i].V - sorted[i - 1].V) * (t - sorted[i - 1].T) / span;
            }
        }
        return sorted[^1].V;
    }
    /// <summary>How much of the file this clip uses.</summary>
    [JsonIgnore] public double SourceLength => Length * Speed;
}

public sealed class Track
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public TrackKind Kind { get; set; }
    public string Name { get; set; } = "";
    public bool Muted { get; set; }               // audio: silent; video: hidden
    public bool Locked { get; set; }
    /// <summary>Only for sound tracks: only the soloed tracks are heard when any is soloed.</summary>
    public bool Solo { get; set; }
    /// <summary>Sound tracks: the track's own volume (0 .. 2) and balance (-1 left .. 1 right).</summary>
    public double Volume { get; set; } = 1;
    public double Pan { get; set; }
    public List<Clip> Clips { get; set; } = new();
}

public sealed class VidProject
{
    public int Version { get; set; } = 1;
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public double Fps { get; set; } = 30;
    public List<MediaItem> Media { get; set; } = new();
    /// <summary>Video tracks first (the LAST of them is drawn on top), then audio tracks.</summary>
    public List<Track> Tracks { get; set; } = new();
    public List<Marker> Markers { get; set; } = new();
    /// <summary>The work area (In / Out points): what Export "work area" renders, and what Lift / Extract cut out.</summary>
    public double? WorkIn { get; set; }
    public double? WorkOut { get; set; }

    [JsonIgnore] public string? Path { get; set; }

    public static VidProject NewEmpty()
    {
        var p = new VidProject();
        p.Tracks.Add(new Track { Kind = TrackKind.Video, Name = "Video 1" });
        p.Tracks.Add(new Track { Kind = TrackKind.Video, Name = "Video 2" });
        p.Tracks.Add(new Track { Kind = TrackKind.Audio, Name = "Audio 1" });
        p.Tracks.Add(new Track { Kind = TrackKind.Audio, Name = "Audio 2" });
        return p;
    }

    public double Duration => Tracks.SelectMany(t => t.Clips).Select(c => c.End).DefaultIfEmpty(0).Max();

    public MediaItem? MediaOf(Clip c) => c.MediaId == null ? null : Media.FirstOrDefault(m => m.Id == c.MediaId);

    public Track? TrackOf(Clip c) => Tracks.FirstOrDefault(t => t.Clips.Contains(c));

    public IEnumerable<Clip> AllClips => Tracks.SelectMany(t => t.Clips);

    // ---------- file ----------
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static VidProject FromJson(string json) => JsonSerializer.Deserialize<VidProject>(json, Options) ?? NewEmpty();

    public VidProject Clone() => FromJson(ToJson());

    public void Save(string path)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson());
        File.Move(tmp, path, true);
        Path = path;
    }

    public static VidProject Load(string path)
    {
        var p = FromJson(File.ReadAllText(path));
        p.Path = path;
        return p;
    }
}
