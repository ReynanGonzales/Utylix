using System;
using System.IO;
using System.Runtime.InteropServices;

namespace IdmClone.Engine;

/// <summary>
/// The path a program is asked to open is not always the file itself: Explorer's Recent list and shortcuts can hand over a ".lnk" or a "file:" address,
/// and a file on a server may not answer the first time (a drive that is reconnecting). This turns what we are given into the real path.
/// </summary>
public static class OpenPath
{
    /// <summary>The real path behind <paramref name="given"/>: quotes taken off, "file:" addresses and ".lnk" shortcuts followed.</summary>
    public static string Resolve(string given)
    {
        string path = (given ?? "").Trim().Trim('"');
        try
        {
            if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile)
                path = uri.IsUnc ? uri.LocalPath : Uri.UnescapeDataString(uri.LocalPath);
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string? target = ShortcutTarget(path);
                if (!string.IsNullOrEmpty(target)) path = target;
            }
        }
        catch (Exception e) when (e is UriFormatException or IOException or COMException or ArgumentException) { /* use it as it is */ }
        return path;
    }

    /// <summary>True for a path on another computer (\\server\share) or on a mapped network drive.</summary>
    public static bool IsNetwork(string path)
    {
        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            string? root = System.IO.Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && GetDriveType(root) == DriveRemote;
        }
        catch (Exception e) when (e is ArgumentException or IOException) { return false; }
    }

    private static string? ShortcutTarget(string lnk)
    {
        Type? shell = Type.GetTypeFromProgID("WScript.Shell");
        if (shell == null) return null;
        object? ws = null, link = null;
        try
        {
            ws = Activator.CreateInstance(shell);
            link = shell.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, ws, new object[] { lnk });
            return link?.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, link, null) as string;
        }
        catch (Exception e) when (e is COMException or System.Reflection.TargetInvocationException or MissingMemberException) { return null; }
        finally
        {
            if (link != null) Marshal.ReleaseComObject(link);
            if (ws != null) Marshal.ReleaseComObject(ws);
        }
    }

    private const uint DriveRemote = 4;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetDriveType(string root);
}
