using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace IdmClone.Engine;

/// <summary>The PDFs opened lately and the folders the person keeps (their server folders, a drawings folder): kept in pdf-recent.json in the data folder.</summary>
public static class PdfRecent
{
    private sealed class Data
    {
        public List<string> Files { get; set; } = new();
        public List<string> Folders { get; set; } = new();
    }

    private const int MaxFiles = 15;
    private static Data? _data;

    private static string FilePath => Path.Combine(App.DataDir, "pdf-recent.json");

    private static Data Load()
    {
        if (_data != null) return _data;
        try { _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return _data ??= new Data();
    }

    private static void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Load(), new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* not kept this time */ }
    }

    public static IReadOnlyList<string> Files => Load().Files;
    public static IReadOnlyList<string> Folders => Load().Folders;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A PDF that was opened: it goes to the top of the list.</summary>
    public static void Add(string path)
    {
        var d = Load();
        d.Files.RemoveAll(f => Same(f, path));
        d.Files.Insert(0, path);
        if (d.Files.Count > MaxFiles) d.Files.RemoveRange(MaxFiles, d.Files.Count - MaxFiles);
        Save();
    }

    public static void Forget(string path) { Load().Files.RemoveAll(f => Same(f, path)); Save(); }
    public static void ClearFiles() { Load().Files.Clear(); Save(); }

    public static bool IsFavourite(string folder) => Load().Folders.Any(f => Same(f, folder));

    public static void AddFolder(string folder)
    {
        var d = Load();
        if (d.Folders.Any(f => Same(f, folder))) return;
        d.Folders.Add(folder);
        Save();
    }

    public static void RemoveFolder(string folder) { Load().Folders.RemoveAll(f => Same(f, folder)); Save(); }
}
