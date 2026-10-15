using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace IdmClone.Engine;

/// <summary>
/// The helper programs Utylix normally downloads after it is installed (yt-dlp, ffmpeg, the video player engine, the background remover's
/// AI model, the JavaScript helper) as one plain folder, "Utylix-tools", that can be carried on a USB stick. A PC that has them saves the
/// folder (<see cref="Export"/>); a PC without internet uses it (<see cref="Import"/>): the parts are copied into Utylix's own tools folder,
/// where every feature already looks for them, so nothing else has to know the difference.
/// </summary>
public static class ToolPack
{
    public const string FolderName = "Utylix-tools";

    /// <summary>One part: its name for people, where it lives inside a tools folder, and how to tell that it is whole (not half copied).</summary>
    public sealed record Part(string Name, string Relative, bool IsFolder, Func<string, bool> IsWhole);

    public static readonly Part[] Parts =
    {
        new("yt-dlp (video sites)", "yt-dlp.exe", false, File.Exists),
        new("ffmpeg (convert, record, merge)", "ffmpeg", true, d => File.Exists(Path.Combine(d, "ffmpeg.exe"))),
        new("the video player engine", "vlc", true, d => File.Exists(Path.Combine(d, "libvlc.dll")) && File.Exists(Path.Combine(d, "libvlccore.dll")) && Directory.Exists(Path.Combine(d, "plugins"))),
        new("the background remover's AI model", "models", true, d =>
        {
            string f = Path.Combine(d, Path.GetFileName(BackgroundRemover.ModelPath));
            return File.Exists(f) && new FileInfo(f).Length == BackgroundRemover.ModelBytes;
        }),
        new("the JavaScript helper", "deno", true, d => File.Exists(Path.Combine(d, "deno.exe"))),
        new("RivaTuner (frame limiter)", "rtss", true, d => File.Exists(Path.Combine(d, "RTSS.exe")) && File.Exists(Path.Combine(d, "RTSSHooks64.dll"))),
    };

    private static bool Whole(Part p, string root)
    {
        string path = Path.Combine(root, p.Relative);
        return p.IsFolder ? Directory.Exists(path) && p.IsWhole(path) : p.IsWhole(path);
    }

    /// <summary>The parts of a tools folder (a pack, or Utylix's own) that are complete.</summary>
    public static List<Part> WholeParts(string root) => Parts.Where(p => Whole(p, root)).ToList();

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder that holds a pack: <paramref name="folder"/> itself or its "Utylix-tools" folder; null when neither has any part.</summary>
    public static string? FindPack(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            foreach (string candidate in new[] { Path.Combine(folder, FolderName), folder })
                if (Directory.Exists(candidate) && WholeParts(candidate).Count > 0) return candidate;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return null;
    }

    /// <summary>A pack lying next to the setup file, next to this program, or at the top of a USB stick that is plugged in.</summary>
    public static string? FindNearby()
    {
        var places = new List<string?> { Environment.GetEnvironmentVariable("UTYLIX_SETUP_DIR"), Path.GetDirectoryName(Environment.ProcessPath) };
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
                if (drive.DriveType == DriveType.Removable && drive.IsReady) places.Add(drive.RootDirectory.FullName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        foreach (string? place in places)
        {
            string? pack = FindPack(place);
            if (pack != null && !SameFolder(pack, Tools.Dir)) return pack;
        }
        return null;
    }

    /// <summary>Copies the parts of <paramref name="pack"/> that this PC does not have yet into Utylix's tools folder (parts already there are left alone). Returns the names copied.</summary>
    public static List<string> Import(string pack, Action<string> status, CancellationToken ct = default)
    {
        var copied = new List<string>();
        if (SameFolder(pack, Tools.Dir)) return copied;
        var todo = WholeParts(pack).Where(p => !Whole(p, Tools.Dir)).ToList();
        Directory.CreateDirectory(Tools.Dir);
        CopyParts(todo, pack, Tools.Dir, status, ct, copied);
        return copied;
    }

    /// <summary>Saves the tools this PC has into "Utylix-tools" inside <paramref name="destination"/> (or into it, when it is already called that). Returns the folder and what was saved.</summary>
    public static (string Folder, List<string> Names) Export(string destination, Action<string> status, CancellationToken ct = default)
    {
        string trimmed = destination.TrimEnd('\\', '/');
        string root = string.Equals(Path.GetFileName(trimmed), FolderName, StringComparison.OrdinalIgnoreCase) ? trimmed : Path.Combine(trimmed, FolderName);
        if (SameFolder(root, Tools.Dir)) throw new IOException("Choose a different folder: that is where Utylix keeps its own tools.");
        var parts = WholeParts(Tools.Dir);
        if (parts.Count == 0) throw new IOException("There are no tools on this PC yet: install them first (Settings > Video sites, or the player's first start).");
        Directory.CreateDirectory(root);
        var names = new List<string>();
        CopyParts(parts, Tools.Dir, root, status, ct, names);
        File.WriteAllText(Path.Combine(root, "README.txt"),
            "Utylix tools - for a PC without internet\r\n\r\n" +
            "This folder holds the programs Utylix normally downloads by itself: " + string.Join(", ", names) + ".\r\n\r\n" +
            "On the other PC:\r\n" +
            "  1. Put this folder next to Utylix-Setup.exe (or leave it at the top of this USB stick) and run Utylix-Setup.exe.\r\n" +
            "     The setup finds it and offers to use it. Or, in an installed Utylix: Settings > Video sites > Use a folder...\r\n" +
            "  2. Fan control and RAM lighting also need the PawnIO driver (pawnio.eu); it is not part of this folder.\r\n");
        return (root, names);
    }

    // ---------- copying ----------
    private static void CopyParts(List<Part> parts, string from, string to, Action<string> status, CancellationToken ct, List<string> done)
    {
        long total = parts.Sum(p => SizeOf(Path.Combine(from, p.Relative)));
        long copiedBytes = 0;
        foreach (var part in parts)
        {
            ct.ThrowIfCancellationRequested();
            string src = Path.Combine(from, part.Relative), dst = Path.Combine(to, part.Relative), staging = dst + ".new";
            Remove(staging);
            void Progress(long n)
            {
                copiedBytes += n;
                status($"Copying {part.Name}…  {copiedBytes / 1048576.0:0.#} MB / {total / 1048576.0:0.#} MB");
            }
            if (part.IsFolder) CopyFolder(src, staging, Progress, ct); else CopyFile(src, staging, Progress, ct);
            Remove(dst);                                                    // (a half-copied older version is replaced as a whole)
            if (part.IsFolder) Directory.Move(staging, dst); else File.Move(staging, dst);
            done.Add(part.Name);
        }
    }

    private static long SizeOf(string path) =>
        Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : File.Exists(path) ? new FileInfo(path).Length : 0;

    private static void Remove(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
        else if (File.Exists(path)) File.Delete(path);
    }

    private static void CopyFolder(string src, string dst, Action<long> progress, CancellationToken ct)
    {
        string root = Path.GetFullPath(src).TrimEnd('\\') + "\\";
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetFullPath(file)[root.Length..];
            CopyFile(file, Path.Combine(dst, relative), progress, ct);
        }
        Directory.CreateDirectory(dst);
    }

    private static void CopyFile(string src, string dst, Action<long> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[1 << 20];
        int n;
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            output.Write(buffer, 0, n);
            progress(n);
        }
    }
}
