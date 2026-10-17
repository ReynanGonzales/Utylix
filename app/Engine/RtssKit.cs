using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IdmClone.Engine;

/// <summary>
/// RivaTuner Statistics Server (RTSS, by Unwinder / Guru3D) as the frame limiter. Utylix does NOT carry it (Guru3D's terms do not allow
/// redistribution): on the person's own PC it is downloaded from the public Guru3D mirror that the winget catalogue also uses, checked against
/// the published SHA-256, and unpacked into the tools folder (no installation, no registry entries, no start-up entry). The setup file is an
/// NSIS package, so 7-Zip's console program is taken out of its MSI the same way (also checked) just to unpack it.
/// RTSS then runs from <see cref="Dir"/>; the limit of a program is its profile file <c>Profiles\&lt;exe&gt;.cfg</c> (<c>[Framerate]</c> <c>Limit=</c>).
/// RTSS hooks the game's Present call from inside the game - the same as when it is used on its own.
/// </summary>
public static class RtssKit
{
    // Guru3D RTSS 7.3.7 (build 28314), same file and SHA-256 as the winget package Guru3D.RTSS.
    private const string ZipUrl = "https://ftp.nluug.nl/pub/games/PC/guru3d/afterburner/%5BGuru3D%5D-RTSSSetup737Build28314.zip";
    private const string ZipSha256 = "9b084a8cb3e53ec1a673894d0b66e22b16c9fd8785636b020b2d422f3f2a820e";
    private const string SetupName = "RTSSSetup737.exe";
    // 7-Zip 25.01 (x64 MSI from 7-zip.org), only used to open the NSIS setup.
    private const string SevenUrl = "https://www.7-zip.org/a/7z2501-x64.msi";
    private const string SevenSha256 = "e7eb0b7ed5efa4e087b7b17f191797f7af5b7f442d1290c66f3a21777005ef57";

    public static string Dir => Path.Combine(Tools.Dir, "rtss");
    public static string Exe => Path.Combine(Dir, "RTSS.exe");
    public static string ProfilesDir => Path.Combine(Dir, "Profiles");
    public static bool Has => File.Exists(Exe) && File.Exists(Path.Combine(Dir, "RTSSHooks64.dll")) && File.Exists(Path.Combine(Dir, "RTSSHooksLoader64.exe"));

    public static bool IsRunning
    {
        get { try { var p = Process.GetProcessesByName("RTSS"); foreach (var x in p) x.Dispose(); return p.Length > 0; } catch (InvalidOperationException) { return false; } }
    }

    /// <summary>Downloads and unpacks RTSS into <see cref="Dir"/> (about 18 MB download, about 15 MB on disk).</summary>
    public static async Task InstallAsync(Action<string> status, CancellationToken ct)
    {
        Directory.CreateDirectory(Tools.Dir);
        string work = Path.Combine(Tools.Dir, "rtss-work");
        try
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);

            string zip = Path.Combine(work, "rtss.zip");
            await Tools.DownloadAsync(ZipUrl, zip, "Downloading RivaTuner", status, ct);
            status("Verifying…");
            Tools.VerifySha256(zip, ZipSha256);
            string setup = Path.Combine(work, SetupName);
            using (var z = ZipFile.OpenRead(zip))
            {
                var entry = z.Entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.FullName), SetupName, StringComparison.OrdinalIgnoreCase))
                            ?? throw new IOException("The RivaTuner setup file was not found in the download.");
                entry.ExtractToFile(setup, true);
            }

            string msi = Path.Combine(work, "7z.msi");
            await Tools.DownloadAsync(SevenUrl, msi, "Downloading the unpacker", status, ct);
            Tools.VerifySha256(msi, SevenSha256);
            status("Unpacking…");
            string sevenDir = Path.Combine(work, "7z");
            // an "administrative install" only extracts the files of the MSI to a folder: nothing is installed
            await RunAsync("msiexec.exe", $"/a \"{msi}\" /qn TARGETDIR=\"{sevenDir}\"", ct);
            string seven = Directory.EnumerateFiles(sevenDir, "7z.exe", SearchOption.AllDirectories).FirstOrDefault()
                           ?? throw new IOException("The unpacker could not be prepared.");
            string unpacked = Path.Combine(work, "files");
            await RunAsync(seven, $"x \"{setup}\" -o\"{unpacked}\" -y", ct);

            // keep what RTSS needs to run (not the SDK, help, languages, installer leftovers, the Intel capture libraries)
            string staging = Path.Combine(work, "ready");
            Directory.CreateDirectory(staging);
            foreach (string file in Directory.GetFiles(unpacked))
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith("libmfxsw", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase) || name.StartsWith("$")) continue;
                if (name.EndsWith(".dll.copy", StringComparison.OrdinalIgnoreCase)) name = name[..^5];        // the installer renames these at the end
                File.Copy(file, Path.Combine(staging, name), true);
            }
            foreach (string sub in new[] { "Plugins", "Plugins64", "Fonts", "Skins", "Vulkan", "Codec", "ProfileTemplates" })
                CopyTree(Path.Combine(unpacked, sub), Path.Combine(staging, sub));
            if (!File.Exists(Path.Combine(staging, "RTSS.exe")) || !File.Exists(Path.Combine(staging, "RTSSHooks64.dll")))
                throw new IOException("RivaTuner could not be unpacked completely.");

            status("Putting it in place…");
            bool wasRunning = IsRunning;
            if (wasRunning) throw new IOException("RivaTuner is running: close it first, then try again.");
            Directory.CreateDirectory(Dir);
            string profiles = ProfilesDir;                               // (the person's limits stay)
            CopyTree(staging, Dir);
            Directory.CreateDirectory(profiles);
            status("RivaTuner is ready.");
        }
        finally { try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    private static void CopyTree(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(from, f);
            string dest = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, true);
        }
    }

    private static async Task RunAsync(string file, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi) ?? throw new IOException("Could not start " + Path.GetFileName(file));
        var drain = p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(5), ct);
        await drain;
        if (p.ExitCode != 0) throw new IOException($"{Path.GetFileName(file)} stopped with code {p.ExitCode}.");
    }

    /// <summary>Sets the frame limit RTSS holds one program to (0 = none): the <c>Limit=</c> line in its profile file.</summary>
    public static bool SetLimit(string exeName, int fps)
    {
        try
        {
            string clean = MemoryLimits.CleanExe(exeName);
            if (clean.Length == 0 || !Has) return false;
            Directory.CreateDirectory(ProfilesDir);
            string path = Path.Combine(ProfilesDir, clean + ".cfg");
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new System.Collections.Generic.List<string>();
            int section = lines.FindIndex(l => l.Trim().Equals("[Framerate]", StringComparison.OrdinalIgnoreCase));
            string line = "Limit=" + Math.Max(0, fps);
            if (section < 0) { if (fps <= 0 && lines.Count == 0) return true; lines.Add("[Framerate]"); lines.Add(line); }
            else
            {
                int i = section + 1;
                while (i < lines.Count && !lines[i].TrimStart().StartsWith("[")) { if (lines[i].TrimStart().StartsWith("Limit=", StringComparison.OrdinalIgnoreCase) || lines[i].TrimStart().StartsWith("Limit\t", StringComparison.OrdinalIgnoreCase)) break; i++; }
                if (i < lines.Count && !lines[i].TrimStart().StartsWith("[")) lines[i] = line; else lines.Insert(i, line);
            }
            string text = string.Join("\r\n", lines) + "\r\n";
            if (File.Exists(path) && File.ReadAllText(path) == text) return true;
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Starts RTSS from its folder when it is not running (it keeps itself in the notification area, with no window). RTSS.exe asks for administrator
    /// rights in its manifest, so only an administrator copy of Utylix (or its guard) can start it without a question; anything else returns false
    /// (the Memory limits window then offers <see cref="StartElevated"/>). A normal start used to fail here without a word: no RivaTuner, no limit.
    /// </summary>
    public static bool EnsureRunning()
    {
        try
        {
            if (!Has) return false;
            if (IsRunning) return true;
            if (!MemoryCleaner.IsElevated) return false;
            Process.Start(new ProcessStartInfo(Exe) { WorkingDirectory = Dir, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })?.Dispose();
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    /// <summary>Ends RivaTuner (and its hook loaders) when Utylix started it and no limit needs it any more. Only an administrator copy can end it.</summary>
    public static void Stop()
    {
        try
        {
            if (!MemoryCleaner.IsElevated) return;
            foreach (string name in new[] { "RTSS", "RTSSHooksLoader64", "RTSSHooksLoader", "EncoderServer", "EncoderServer64" })
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { if (p.MainModule?.FileName?.StartsWith(Dir, StringComparison.OrdinalIgnoreCase) == true) p.Kill(); }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
                    finally { p.Dispose(); }
                }
        }
        catch (InvalidOperationException) { }
    }

    /// <summary>Starts RTSS with the administrator question (Windows asks once). False when it was refused or could not be started.</summary>
    public static bool StartElevated()
    {
        try
        {
            if (!Has) return false;
            if (IsRunning) return true;
            Process.Start(new ProcessStartInfo(Exe) { WorkingDirectory = Dir, UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })?.Dispose();
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }      // (the question was answered No)
    }
}
