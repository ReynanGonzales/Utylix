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

    [JsonIgnore] public double End => Start + Length;
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
