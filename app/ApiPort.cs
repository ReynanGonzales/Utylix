using System;
using System.IO;

namespace IdmClone;

/// <summary>
/// Which local port this Windows user's copy of Utylix listens on. Usually 6800; when another user signed in at the same time
/// (or another program) already has it, the next free one. It is written to port.txt in the user's own data folder, so Explorer's
/// menus and the browser link (which run as the same user) find the right copy.
/// </summary>
internal static class ApiPort
{
    public const int Default = 6800;

    /// <summary>Who this copy belongs to (a fingerprint of the Windows user's SID): tells one user's copy from another's on the same PC.</summary>
    public static string UserId { get; } = Compute();

    /// <summary>The id for a given Windows user SID (the fan helper runs as an administrator, but names things after the user who started it).</summary>
    public static string UserIdOf(string sid) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sid)))[..12].ToLowerInvariant();

    private static string Compute()
    {
        try
        {
            return UserIdOf(System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);
        }
        catch (Exception e) when (e is InvalidOperationException or System.Security.SecurityException) { return "unknown"; }
    }
    private static string FileIn(string dataDir) => Path.Combine(dataDir, "port.txt");

    public static void Publish(string dataDir, int port)
    {
        try { Directory.CreateDirectory(dataDir); File.WriteAllText(FileIn(dataDir), port.ToString()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* then everything falls back to the default port */ }
    }

    public static void Clear(string dataDir)
    {
        try { File.Delete(FileIn(dataDir)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The port of the running copy: an explicit one (tests), else what port.txt says, else the default.</summary>
    public static int Current(string dataDir, int? explicitPort = null)
    {
        if (explicitPort is { } p) return p;
        try
        {
            if (int.TryParse(File.ReadAllText(FileIn(dataDir)).Trim(), out int port) && port is >= 1024 and <= 65535) return port;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return Default;
    }
}
