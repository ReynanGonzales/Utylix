using System;
using System.IO;
using System.Text.Json;

namespace IdmClone.Vid;

/// <summary>
/// The editor's OWN settings (theme, accent, export choices, autosave). They live in %APPDATA%\UtylixVid\settings.json and are changed only
/// in the editor's Settings window: Utylix's own settings are never read or written by the editor, and the editor's never touch Utylix.
/// </summary>
public sealed class VidSettings
{
    /// <summary>"premiere" (the default), "system", "dark" or "light".</summary>
    public string Theme { get; set; } = "premiere";
    public string Accent { get; set; } = "blue";
    /// <summary>Export with the video card's encoder when it works.</summary>
    public bool UseGpu { get; set; } = true;
    /// <summary>0 = high, 1 = balanced, 2 = small file.</summary>
    public int Quality { get; set; } = 1;
    /// <summary>Seconds between automatic recovery copies of an unsaved project; 0 = off.</summary>
    public int AutosaveSeconds { get; set; } = 45;
    /// <summary>The folder the last export went to.</summary>
    public string ExportFolder { get; set; } = "";

    public static VidSettings Current { get; internal set; } = new();
    private static string _file = "";

    public static string Folder => Path.GetDirectoryName(_file) ?? "";

    public static void Load(string dir)
    {
        _file = Path.Combine(dir, "settings.json");
        try
        {
            if (File.Exists(_file)) Current = JsonSerializer.Deserialize<VidSettings>(File.ReadAllText(_file)) ?? new VidSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { Current = new VidSettings(); }
    }

    public void Save()
    {
        if (_file.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the choices simply are not remembered */ }
    }

    public VidSettings Copy() => (VidSettings)MemberwiseClone();
}
