using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdmClone.Engine;

/// <summary>
/// A safety copy of a PDF that has changes which are not saved yet (the editor writes one every minute): if the program or the PC stops before the person saves, the changes can be
/// offered back the next time that PDF is opened. One copy per file, in the data folder (<c>pdf-recovery</c>); it is removed when the file is saved or closed. Never made for
/// password-protected PDFs (the copy would not be protected).
/// </summary>
public static class PdfRecovery
{
    public sealed record Entry(string Path, string File, DateTime SavedUtc);
    private sealed record Info(string Path, DateTime SavedUtc, long OriginalLength, long OriginalWriteTicks);

    private static string Dir => System.IO.Path.Combine(App.DataDir, "pdf-recovery");

    private static string Key(string path)
    {
        string full = System.IO.Path.GetFullPath(path).ToLowerInvariant();
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(full)))[..16];
    }

    private static string DataFile(string path) => System.IO.Path.Combine(Dir, Key(path) + ".pdf");
    private static string InfoFile(string path) => System.IO.Path.Combine(Dir, Key(path) + ".json");

    public static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Dir);
        var original = new FileInfo(path);
        string temp = DataFile(path) + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, DataFile(path), overwrite: true);
        File.WriteAllText(InfoFile(path), JsonSerializer.Serialize(new Info(System.IO.Path.GetFullPath(path), DateTime.UtcNow, original.Exists ? original.Length : -1, original.Exists ? original.LastWriteTimeUtc.Ticks : -1)));
        Tidy();
    }

    /// <summary>The safety copy of this file, when there is one and the file itself has not changed since (a copy of an older version would undo later work).</summary>
    public static Entry? Find(string path)
    {
        try
        {
            if (!File.Exists(DataFile(path)) || !File.Exists(InfoFile(path))) return null;
            var info = JsonSerializer.Deserialize<Info>(File.ReadAllText(InfoFile(path)));
            var original = new FileInfo(path);
            if (info == null || !original.Exists || original.Length != info.OriginalLength || original.LastWriteTimeUtc.Ticks != info.OriginalWriteTicks) { Delete(path); return null; }
            return new Entry(info.Path, DataFile(path), info.SavedUtc);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static void Delete(string path)
    {
        try
        {
            if (File.Exists(DataFile(path))) File.Delete(DataFile(path));
            if (File.Exists(InfoFile(path))) File.Delete(InfoFile(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Copies nobody came back for in a month go.</summary>
    private static void Tidy()
    {
        try
        {
            foreach (string f in Directory.EnumerateFiles(Dir))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromDays(30)) File.Delete(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
