using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IdmClone.Engine;

/// <summary>
/// The helper programs used for video sites (YouTube, Facebook, ...): yt-dlp finds the video and
/// ffmpeg joins separate video and audio into one file. They live in Utylix's own data folder
/// and every download is checked against the checksum the project publishes next to it.
/// </summary>
public static class Tools
{
    // Official release locations. "latest" always points at the newest published version.
    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string YtDlpSums = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
    private const string FfmpegZipName = "ffmpeg-master-latest-win64-gpl.zip";
    private const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/" + FfmpegZipName;
    private const string FfmpegSums = "https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/checksums.sha256";

    // The JavaScript helper (Deno): YouTube hides some videos (age / sign-in restricted ones, and anything fetched with login cookies) behind a small
    // puzzle in its player script that yt-dlp can only solve with a JavaScript engine. Official release; the zip holds the one file deno.exe.
    private const string DenoZipName = "deno-x86_64-pc-windows-msvc.zip";
    private const string DenoUrl = "https://github.com/denoland/deno/releases/latest/download/" + DenoZipName;
    private const string DenoSums = DenoUrl + ".sha256sum";
    public static string DenoDir => Path.Combine(Dir, "deno");
    public static string Deno => Path.Combine(DenoDir, "deno.exe");
    public static bool HasJsRuntime => File.Exists(Deno);

    public static string Dir { get; set; } = "";
    public static string YtDlp => Path.Combine(Dir, "yt-dlp.exe");
    public static string FfmpegDir => Path.Combine(Dir, "ffmpeg");
    public static string Ffmpeg => Path.Combine(FfmpegDir, "ffmpeg.exe");
    public static bool HasYtDlp => File.Exists(YtDlp);
    public static bool HasFfmpeg => File.Exists(Ffmpeg);

    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Utylix-ToolInstaller" } },
    };

    /// <summary>"2025.09.26", or null if yt-dlp isn't installed or won't run.</summary>
    public static async Task<string?> YtDlpVersionAsync()
    {
        if (!HasYtDlp) return null;
        try
        {
            var psi = new ProcessStartInfo(YtDlp, "--version")
            {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var text = await p.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            return text.Trim().Length > 0 ? text.Trim() : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Newest published yt-dlp version ("2026.08.19"), read from where GitHub's "latest release" link points. Null if unreachable.</summary>
    public static async Task<string?> LatestYtDlpVersionAsync(CancellationToken ct)
    {
        try
        {
            using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Utylix-UpdateCheck");
            using var resp = await http.GetAsync("https://github.com/yt-dlp/yt-dlp/releases/latest", ct);
            string? tag = resp.Headers.Location?.ToString().TrimEnd('/').Split('/').LastOrDefault();
            return tag != null && System.Text.RegularExpressions.Regex.IsMatch(tag, @"^\d{4}\.\d{2}\.\d{2}(\.\d+)?$") ? tag : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>True if version <paramref name="latest"/> is newer than <paramref name="installed"/> (yt-dlp versions are dates: 2026.08.19).</summary>
    public static bool IsNewer(string latest, string installed)
    {
        static int[] Parts(string v) => v.Split('.').Select(p => int.TryParse(new string(p.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0).ToArray();
        var a = Parts(latest); var b = Parts(installed);
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    public static async Task InstallYtDlpAsync(Action<string> status, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        string tmp = Path.Combine(Dir, "yt-dlp.exe.download");
        try
        {
            status("Getting the checksum…");
            string expected = ParseSum(await Http.GetStringAsync(YtDlpSums, ct), "yt-dlp.exe");
            await DownloadAsync(YtDlpUrl, tmp, "Downloading yt-dlp", status, ct);
            status("Verifying…");
            VerifySha256(tmp, expected);
            File.Move(tmp, YtDlp, true);
            status("yt-dlp installed.");
        }
        finally { TryDelete(tmp); }
    }

    public static async Task InstallFfmpegAsync(Action<string> status, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        string tmp = Path.Combine(Dir, FfmpegZipName + ".download");
        try
        {
            status("Getting the checksum…");
            string expected = ParseSum(await Http.GetStringAsync(FfmpegSums, ct), FfmpegZipName);
            await DownloadAsync(FfmpegUrl, tmp, "Downloading ffmpeg", status, ct);
            status("Verifying…");
            VerifySha256(tmp, expected);

            status("Unpacking…");
            Directory.CreateDirectory(FfmpegDir);
            using (var zip = ZipFile.OpenRead(tmp))
            {
                foreach (var entry in zip.Entries)
                {
                    string full = entry.FullName.Replace('\\', '/');
                    if (!full.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase) &&
                        !full.EndsWith("/bin/ffprobe.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    string dest = Path.Combine(FfmpegDir, Path.GetFileName(full));   // file name only: never a path from the zip
                    entry.ExtractToFile(dest, true);
                }
            }
            if (!HasFfmpeg) throw new IOException("ffmpeg.exe was not found in the downloaded package.");
            status("ffmpeg installed.");
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>Installs the JavaScript helper (about 43 MB, from the official Deno release, checked against the checksum published next to it).</summary>
    public static async Task InstallJsRuntimeAsync(Action<string> status, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        string tmp = Path.Combine(Dir, DenoZipName + ".download");
        try
        {
            status("Getting the checksum…");
            var m = System.Text.RegularExpressions.Regex.Match(await Http.GetStringAsync(DenoSums, ct), @"\b[0-9a-fA-F]{64}\b");
            if (!m.Success) throw new InvalidDataException("No checksum for the JavaScript helper in the published list.");
            await DownloadAsync(DenoUrl, tmp, "Downloading the JavaScript helper", status, ct);
            status("Verifying…");
            VerifySha256(tmp, m.Value.ToLowerInvariant());

            status("Unpacking…");
            Directory.CreateDirectory(DenoDir);
            using (var zip = ZipFile.OpenRead(tmp))
            {
                var entry = zip.Entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.FullName), "deno.exe", StringComparison.OrdinalIgnoreCase));
                if (entry == null) throw new IOException("deno.exe was not found in the downloaded package.");
                entry.ExtractToFile(Deno, true);                                  // the file name only: never a path from the zip
            }
            status("JavaScript helper installed.");
        }
        finally { TryDelete(tmp); }
    }

    // ---------- helpers ----------
    /// <summary>
    /// Downloads a file. A connection that goes quiet (no data for 20 seconds) is dropped and the download continues from where it
    /// stopped (servers that support ranges), up to <paramref name="attempts"/> times; a file that was already partly there is
    /// continued when <paramref name="resume"/> is true.
    /// </summary>
    internal static async Task DownloadAsync(string url, string dest, string label, Action<string> status, CancellationToken ct, int attempts = 6, bool resume = false)
    {
        if (!resume && File.Exists(dest)) File.Delete(dest);
        int failures = 0;
        var started = DateTime.UtcNow;
        long startedAt = File.Exists(dest) ? new FileInfo(dest).Length : 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            long done = File.Exists(dest) ? new FileInfo(dest).Length : 0;
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (done > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(done, null);
                stall.CancelAfter(TimeSpan.FromSeconds(30));                                      // waiting for the server to answer
                using var resp = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
                if (done > 0 && resp.StatusCode != System.Net.HttpStatusCode.PartialContent) done = 0;   // the server can't continue: start again
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentRange?.Length ?? (resp.Content.Headers.ContentLength is long len ? len + done : -1);

                await using var src = await resp.Content.ReadAsStreamAsync(stall.Token);
                await using var dst = new FileStream(dest, done > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read);
                var buf = new byte[128 * 1024];
                var last = DateTime.UtcNow;
                int n;
                while (true)
                {
                    stall.CancelAfter(TimeSpan.FromSeconds(20));                                  // every piece of data must arrive within 20 s
                    n = await src.ReadAsync(buf, stall.Token);
                    if (n <= 0) break;
                    await dst.WriteAsync(buf.AsMemory(0, n), stall.Token);
                    done += n;
                    if ((DateTime.UtcNow - last).TotalMilliseconds > 250)
                    {
                        last = DateTime.UtcNow;
                        double seconds = Math.Max(1, (DateTime.UtcNow - started).TotalSeconds);
                        string speed = $"   ·   {(done - startedAt) / 1048576.0 / seconds:0.0} MB/s";
                        status(total > 0 ? $"{label}… {Format(done)} / {Format(total)}{speed}" : $"{label}… {Format(done)}{speed}");
                    }
                }
                await dst.FlushAsync(ct);
                if (total > 0 && done < total) throw new IOException("The connection closed early.");
                return;
            }
            catch (Exception e) when (!ct.IsCancellationRequested && (e is OperationCanceledException or HttpRequestException or IOException))
            {
                if (++failures >= attempts) throw new IOException($"The download keeps stopping ({(e is OperationCanceledException ? "no data arrives" : e.Message)}). Check the connection and try again.");
                status($"{label}… the connection stalled, continuing ({failures}/{attempts - 1})");
                await Task.Delay(1500, ct);
            }
        }
    }

    private static string Format(long bytes) => $"{bytes / 1048576.0:0.0} MB";

    /// <summary>Finds the hash for one file in a "hash  filename" checksum list.</summary>
    private static string ParseSum(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1].TrimStart('*') == fileName && parts[0].Length == 64) return parts[0].ToLowerInvariant();
        }
        throw new InvalidDataException($"No checksum for {fileName} in the published list.");
    }

    internal static void VerifySha256(string path, string expected)
    {
        using var f = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
        if (actual != expected)
            throw new InvalidDataException("The downloaded file does not match its published checksum, so it was discarded.");
    }

    internal static void TryDelete(string path) { try { File.Delete(path); } catch (Exception) { } }
}
