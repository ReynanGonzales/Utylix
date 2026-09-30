using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using Microsoft.Win32;

namespace IdmClone.Engine;

/// <summary>
/// The playback engine of the Video Player: the same "libvlc" library the VLC player is made of (it plays practically every
/// video and music format, with subtitles and audio tracks). Utylix uses its own copy in its data folder; when there is none
/// it uses a VLC that is already installed on the PC, and otherwise offers to download it once from videolan.org (checked against
/// the checksum VideoLAN publishes next to it).
/// </summary>
public static class VlcEngine
{
    // VideoLAN's download address picks a mirror near you (sometimes a slow one); its own server is the fallback.
    private static readonly string[] Hosts = { "https://get.videolan.org/vlc/last/win64/", "https://download.videolan.org/pub/videolan/vlc/last/win64/" };
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Utylix-ToolInstaller" } },
    };

    public static string OwnDir => Path.Combine(Tools.Dir, "vlc");
    private static bool HasEngine(string? dir) => dir != null && File.Exists(Path.Combine(dir, "libvlc.dll")) && File.Exists(Path.Combine(dir, "libvlccore.dll")) && Directory.Exists(Path.Combine(dir, "plugins"));

    /// <summary>The folder with libvlc.dll to use, or null when there is none.</summary>
    public static string? Find()
    {
        if (HasEngine(OwnDir)) return OwnDir;
        var candidates = new List<string?>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\VideoLAN\VLC");
            candidates.Add(key?.GetValue("InstallDir") as string);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "VideoLAN", "VLC"));
        return candidates.FirstOrDefault(HasEngine);
    }

    public static bool Available => Find() != null;

    /// <summary>true if the engine in use is Utylix's own copy (not a VLC installed on the PC).</summary>
    public static bool UsingOwnCopy => HasEngine(OwnDir);

    private static LibVLC? _instance;
    private static readonly object Lock = new();

    /// <summary>The shared engine (created on first use). Throws when the engine is missing.</summary>
    public static LibVLC Instance
    {
        get
        {
            lock (Lock)
            {
                if (_instance != null) return _instance;
                string dir = Find() ?? throw new IOException("The playback engine is not installed.");
                Core.Initialize(dir);
                _instance = new LibVLC("--no-video-title-show", "--no-snapshot-preview", "--no-osd", "--quiet", "--no-plugins-cache", "--no-stats", "--no-sub-autodetect-file");
                return _instance;
            }
        }
    }

    // ---------- download ----------
    public static async Task InstallAsync(Action<string> status, CancellationToken ct)
    {
        Directory.CreateDirectory(Tools.Dir);
        string tmp = Path.Combine(Tools.Dir, "vlc-engine.zip.download");
        string staging = OwnDir + ".new";
        try
        {
            status("Looking for the current version…");
            string? fileName = null, expected = null;
            IOException? lastError = null;
            foreach (string host in Hosts)
            {
                try
                {
                    string listing = await Http.GetStringAsync(host, ct);
                    fileName = Regex.Matches(listing, @"vlc-(\d+(?:\.\d+)+)-win64\.zip(?!\.)").Select(m => m.Value).Distinct().FirstOrDefault()
                               ?? throw new IOException("Couldn't find the player engine on videolan.org.");
                    string sums = await Http.GetStringAsync(host + fileName + ".sha256", ct);
                    expected = sums.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(p => p.Length == 64)?.ToLowerInvariant()
                               ?? throw new InvalidDataException("No checksum was published for the download.");
                    bool sameFile = File.Exists(tmp);                                             // (a partly downloaded copy from the other host is continued: same file)
                    await Tools.DownloadAsync(host + fileName, tmp, "Downloading the player engine", status, ct, attempts: 3, resume: sameFile);
                    lastError = null;
                    break;
                }
                catch (Exception e) when (!ct.IsCancellationRequested && (e is IOException or HttpRequestException or TaskCanceledException))
                {
                    lastError = e as IOException ?? new IOException(e.Message);
                    status("That server is not answering well, trying VideoLAN's other server…");
                }
            }
            if (lastError != null || expected == null) throw lastError ?? new IOException("The download failed.");
            status("Verifying…");
            Tools.VerifySha256(tmp, expected!);

            status("Unpacking…");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            string root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            using (var zip = ZipFile.OpenRead(tmp))
            {
                foreach (var entry in zip.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    int slash = name.IndexOf('/');
                    if (slash < 0 || entry.Name.Length == 0) continue;                       // (folders)
                    string relative = name[(slash + 1)..];                                   // drop the "vlc-3.0.x/" folder
                    bool wanted = relative is "libvlc.dll" or "libvlccore.dll" || relative.StartsWith("plugins/", StringComparison.Ordinal);
                    if (!wanted) continue;                                                    // vlc.exe, skins, translations ... are not needed
                    string dest = Path.GetFullPath(Path.Combine(staging, relative));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // never write outside the folder
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, true);
                }
            }
            if (!HasEngine(staging)) throw new IOException("The download did not contain the player engine.");
            if (Directory.Exists(OwnDir)) Directory.Delete(OwnDir, true);
            Directory.Move(staging, OwnDir);
            status("Ready.");
        }
        finally
        {
            Tools.TryDelete(tmp);
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (Exception) { }
        }
    }
}
