using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// A PDF that was asked for but can't be opened used to do nothing at all (the file is not visible from this copy of Utylix, so the window never appeared).
/// Now the person is told why, and the reason is also written to pdf-open.log: the usual cause for server files is a mapped drive (K:) that this copy
/// of Utylix can't see, because it runs as administrator (mapped drives belong to the normal Windows session) or the drive is not connected.
/// </summary>
public sealed partial class PdfWindow
{
    private static void SayNotOpened(List<string> files)
    {
        var lines = new List<string>();
        foreach (string file in files.Take(3))
        {
            string name = System.IO.Path.GetFileName(file);
            string reason;
            if (!IsPdf(file) && File.Exists(file)) reason = "it is not a .pdf file";
            else reason = WhyMissing(file);
            lines.Add($"“{name}”: {reason}\n{file}");
            Log(file, reason);
        }
        if (files.Count > 3) lines.Add($"… and {files.Count - 3} more");
        UMessage.Show(string.Join("\n\n", lines), "Utylix can't open this PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string WhyMissing(string file)
    {
        string? root = null, folder = null;
        try { root = System.IO.Path.GetPathRoot(file); folder = System.IO.Path.GetDirectoryName(file); } catch (ArgumentException) { }
        bool network = root != null && (root.StartsWith(@"\\", StringComparison.Ordinal) || IsNetworkDrive(root));
        bool rootThere = false, folderThere = false;
        try { rootThere = !string.IsNullOrEmpty(root) && Directory.Exists(root); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        try { folderThere = !string.IsNullOrEmpty(folder) && Directory.Exists(folder); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        if (!rootThere)
        {
            string drive = (root ?? "").TrimEnd('\\');
            return $"Utylix can't see {(string.IsNullOrEmpty(drive) ? "that location" : drive)} from here.\n" +
                   (MemoryCleaner.IsElevated
                       ? "This copy of Utylix is running as administrator, and Windows does not show mapped drives (like K:) to programs run as administrator. Close Utylix (right-click its icon by the clock > Exit) and start it normally, not \"as administrator\"."
                       : "Check that the drive / server is connected (open it once in File Explorer), then try again.");
        }
        if (!folderThere) return "its folder can't be found or you have no access to it" + (network ? " (a server folder: check the connection and your access)." : ".");
        return network ? "the file can't be found or you have no access to it (a server folder: it may have been moved, or your account may not be allowed to open it)."
                       : "the file can't be found (it may have been moved or deleted).";
    }

    private static bool IsNetworkDrive(string root)
    {
        try { return new DriveInfo(root).DriveType == DriveType.Network; }
        catch (Exception e) when (e is ArgumentException or IOException) { return false; }
    }

    /// <summary>A line in pdf-open.log (the same file the failing opens write to).</summary>
    internal static void Log(string path, string what)
    {
        try
        {
            File.AppendAllText(System.IO.Path.Combine(App.DataDir, "pdf-open.log"),
                $"{DateTime.Now:s}  {what.Replace('\n', ' ')}  admin={MemoryCleaner.IsElevated}  {path}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
