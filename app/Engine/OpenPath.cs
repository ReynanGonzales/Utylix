using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace IdmClone.Engine;

/// <summary>
/// The path a file is asked for with is not always the plain path of the file: a shortcut (a .lnk: Windows' Recent list and the shortcut files of a server folder are
/// made of them), a link such as file:///K:/a.pdf, or the short 8.3 form of a long name. <see cref="Fix"/> turns those into the real path, and leaves a plain path as it is.
/// </summary>
public static class OpenPath
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint size);

    public static string Fix(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        path = path.Trim().Trim('"');
        try
        {
            if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile) path = uri.LocalPath;
            if (string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(path) && Target(path) is { Length: > 0 } target) path = target;
            if (path.Contains('~') && !File.Exists(path) && LongName(path) is { Length: > 0 } longer) path = longer;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or UriFormatException) { }
        return path;
    }

    /// <summary>Where a shortcut file points (null when it can't be read).</summary>
    public static string? Target(string lnk)
    {
        try
        {
            var shell = Type.GetTypeFromProgID("WScript.Shell");
            if (shell == null) return null;
            dynamic sh = Activator.CreateInstance(shell)!;
            try { return (string?)sh.CreateShortcut(lnk).TargetPath; }
            finally { Marshal.FinalReleaseComObject(sh); }
        }
        catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException or NotSupportedException) { return null; }
    }

    private static string? LongName(string path)
    {
        var sb = new StringBuilder(1024);
        uint n = GetLongPathName(path, sb, (uint)sb.Capacity);
        return n > 0 && n < sb.Capacity ? sb.ToString() : null;
    }
}
