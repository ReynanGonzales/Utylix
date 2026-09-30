using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace IdmClone;

/// <summary>What the Video Player remembers between runs: recent files, where each file was left, the volume.</summary>
public sealed class PlayerState
{
    public List<string> Recent { get; set; } = new();
    public Dictionary<string, double> Positions { get; set; } = new();     // path -> seconds
    public int Volume { get; set; } = 100;
    public double Width { get; set; } = 1000;
    public double Height { get; set; } = 640;

    private static string PathOnDisk => System.IO.Path.Combine(App.DataDir, "player.json");
    private static PlayerState? _current;

    public static PlayerState Current => _current ??= Load();

    private static PlayerState Load()
    {
        try { if (File.Exists(PathOnDisk)) return JsonSerializer.Deserialize<PlayerState>(File.ReadAllText(PathOnDisk)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(this)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* forgetting is not worth an error */ }
    }

    public void Played(string path)
    {
        Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, path);
        if (Recent.Count > 20) Recent.RemoveRange(20, Recent.Count - 20);
        Save();
    }

    /// <summary>Where the file was left (seconds), or 0 when it should start from the beginning.</summary>
    public double ResumeAt(string path) => Positions.TryGetValue(path, out var s) ? s : 0;

    public void Remember(string path, double seconds, double length)
    {
        // a file that was watched to (nearly) the end starts from the beginning next time; very short ones aren't remembered
        if (length < 60 || seconds < 15 || seconds > length - 20) Positions.Remove(path);
        else Positions[path] = seconds;
        while (Positions.Count > 200) Positions.Remove(Positions.Keys.First());
    }
}
