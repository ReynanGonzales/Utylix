using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace IdmClone.Engine;

/// <summary>Where you stopped in each PDF (page, zoom): the editor opens a file again at that page. Kept in pdf-places.json in the data folder (the 300 latest files).</summary>
internal static class PdfPlaces
{
    public sealed class Place
    {
        public int Page { get; set; }
        public double Zoom { get; set; }
        public string Fit { get; set; } = "width";          // "width", "page" or "none" (then Zoom is used)
        public DateTime At { get; set; } = DateTime.UtcNow;
    }

    private const int Keep = 300;
    private static Dictionary<string, Place>? _all;
    private static string PathOnDisk => Path.Combine(App.DataDir, "pdf-places.json");
    private static string Key(string path) { try { return Path.GetFullPath(path).ToLowerInvariant(); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path.ToLowerInvariant(); } }

    private static Dictionary<string, Place> All()
    {
        if (_all != null) return _all;
        try { if (File.Exists(PathOnDisk)) _all = JsonSerializer.Deserialize<Dictionary<string, Place>>(File.ReadAllText(PathOnDisk)); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return _all ??= new Dictionary<string, Place>();
    }

    public static Place? Get(string path) => All().TryGetValue(Key(path), out var p) ? p : null;

    /// <summary>Remembers the place; the start of a file in the default view is not worth remembering (it is forgotten).</summary>
    public static void Set(string path, int page, double zoom, string fit)
    {
        var all = All();
        string key = Key(path);
        if (page <= 0 && fit == "width") { if (!all.Remove(key)) return; }
        else all[key] = new Place { Page = page, Zoom = zoom, Fit = fit };
        if (all.Count > Keep) foreach (var old in all.OrderBy(kv => kv.Value.At).Take(all.Count - Keep).Select(kv => kv.Key).ToList()) all.Remove(old);
        try { Directory.CreateDirectory(App.DataDir); File.WriteAllText(PathOnDisk, JsonSerializer.Serialize(all)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* not kept this time */ }
    }
}
