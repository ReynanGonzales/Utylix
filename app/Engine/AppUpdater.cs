using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace IdmClone.Engine;

/// <summary>A published Utylix version on GitHub.</summary>
public sealed record ReleaseInfo(string Tag, Version Version, string Notes, string AssetApiUrl, long Size, string? Sha256, string? ShaAssetApiUrl, string AssetName = "Utylix.exe")
{
    /// <summary>The release carries the setup (a program folder is updated by running it), not just one exe.</summary>
    public bool IsSetup => AssetName == "Utylix-Setup.exe";
}

/// <summary>Something went wrong while looking for or installing an update, in words the person can act on.</summary>
public sealed class UpdateException : Exception
{
    /// <summary>The access token is missing or wrong (private repository).</summary>
    public bool NeedsToken { get; }
    public UpdateException(string message, bool needsToken = false) : base(message) { NeedsToken = needsToken; }
}

/// <summary>
/// Updates Utylix itself from the "Releases" of its GitHub repository. Only the file Utylix.exe of the newest release is used,
/// it must match the SHA-256 checksum GitHub publishes for it, and nothing is installed without a click.
/// </summary>
public static class AppUpdater
{
    public const string Repo = "ReynanGonzales/Utylix";
    private const string AssetName = "Utylix.exe";               // (older releases, and older copies of Utylix, use this one)
    private const string SetupName = "Utylix-Setup.exe";         // preferred: it updates a program folder (and a single exe) in place

    /// <summary>Normally GitHub. A test can point it at a local server (only loopback addresses are accepted, see App).</summary>
    public static string ApiBase { get; set; } = "https://api.github.com";
    private static Version? _fakeVersion;

    /// <summary>The version of this program.</summary>
    public static Version Current => _fakeVersion ?? ParseVersion(
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0") ?? new Version(0, 0);

    public static string CurrentText => Current.Major + "." + Current.Minor + "." + Math.Max(0, Current.Build);

    /// <summary>For tests only: pretend to be another version.</summary>
    public static void PretendVersion(string version) => _fakeVersion = ParseVersion(version);

    /// <summary>"v1.2.3", "1.2.3+abc" or "1.2.3-beta" -> 1.2.3</summary>
    public static Version? ParseVersion(string text)
    {
        text = text.Trim().TrimStart('v', 'V');
        int cut = text.IndexOfAny(new[] { '+', '-', ' ' });
        if (cut >= 0) text = text[..cut];
        return Version.TryParse(text, out var v) ? v : null;
    }

    public static bool IsNewer(ReleaseInfo release) => release.Version > Normalize(Current);
    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    // ---------- the token is stored encrypted for this Windows user ----------
    public static string Protect(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return "";
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), null, DataProtectionScope.CurrentUser));
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return ""; }     // from another PC / user: act as if none
    }

    // ---------- looking ----------
    private static HttpClient NewClient(bool follow = true)
    {
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = follow }) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Utylix-Updater/" + CurrentText);
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    private static HttpRequestMessage Get(string url, string token, string accept)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        r.Headers.Accept.ParseAdd(accept);
        if (token.Length > 0) r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return r;
    }

    private static UpdateException Explain(HttpStatusCode status, string token)
    {
        if (status is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
            return token.Length == 0
                ? new UpdateException("GitHub says there is nothing to see here. If the repository is private, add an access token (it only needs permission to read the repository's files).", true)
                : new UpdateException("GitHub did not accept the access token, or it cannot see this repository. Check the token and its permission to read the repository's contents.", true);
        if (status == HttpStatusCode.Forbidden)
            return new UpdateException("GitHub is refusing the request (too many checks from this connection, or the token lacks permission). Try again later.", token.Length > 0);
        return new UpdateException($"GitHub answered with an error ({(int)status}). Try again later.");
    }

    /// <summary>The newest published release (whether or not it is newer than this program).</summary>
    public static async Task<ReleaseInfo> LatestAsync(string token, CancellationToken ct)
    {
        using var http = NewClient();
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(Get($"{ApiBase}/repos/{Repo}/releases/latest", token, "application/vnd.github+json"), ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new UpdateException("Could not reach GitHub. Check the internet connection.");
        }
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode == HttpStatusCode.NotFound && token.Length > 0 && await RepoExistsAsync(http, token, ct))
                    throw new UpdateException("No release has been published yet.");
                throw Explain(resp.StatusCode, token);
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            var version = ParseVersion(tag) ?? throw new UpdateException($"The newest release is called \"{tag}\", which is not a version number.");
            string notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";

            string? assetUrl = null, shaUrl = null, digest = null; long size = 0;
            var assets = root.GetProperty("assets").EnumerateArray().ToList();
            string wanted = assets.Any(a => a.GetProperty("name").GetString() == SetupName) ? SetupName : AssetName;
            foreach (var a in assets)
            {
                string name = a.GetProperty("name").GetString() ?? "";
                string url = a.GetProperty("url").GetString() ?? "";
                if (name == wanted)
                {
                    assetUrl = url;
                    size = a.GetProperty("size").GetInt64();
                    if (a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String && (d.GetString() ?? "").StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        digest = d.GetString()![7..].ToLowerInvariant();
                }
                else if (name == wanted + ".sha256") shaUrl = url;
            }
            if (assetUrl == null) throw new UpdateException($"Release {tag} has no {AssetName} file attached.");
            return new ReleaseInfo(tag, version, notes, assetUrl, size, digest, shaUrl, wanted);
        }
    }

    private static async Task<bool> RepoExistsAsync(HttpClient http, string token, CancellationToken ct)
    {
        using var r = await http.SendAsync(Get($"{ApiBase}/repos/{Repo}", token, "application/vnd.github+json"), ct);
        return r.IsSuccessStatusCode;
    }

    // ---------- installing ----------
    /// <summary>Path of the new copy while it is downloaded (next to the running one, so putting it in place is a rename).</summary>
    private static string ExePath => Environment.ProcessPath ?? throw new UpdateException("Utylix cannot tell where it is installed.");
    private static string UpdatePath => ExePath + ".update";

    /// <summary>Downloads the release and checks it against its published checksum. Returns the path of the verified file.</summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, string token, Action<string> status, CancellationToken ct)
    {
        string dest = release.IsSetup ? Path.Combine(Path.GetTempPath(), $"Utylix-Setup-{release.Version.ToString(3)}.exe") : UpdatePath;
        try
        {
            status("Checking the published checksum…");
            string? expected = release.Sha256;
            if (expected == null && release.ShaAssetApiUrl != null)
            {
                using var http = NewClient();
                using var r = await http.SendAsync(Get(release.ShaAssetApiUrl, token, "application/octet-stream"), ct);
                if (r.IsSuccessStatusCode)
                    expected = (await r.Content.ReadAsStringAsync(ct)).Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(t => t.Length == 64)?.ToLowerInvariant();
            }
            if (expected == null) throw new UpdateException("This release has no published checksum, so Utylix will not install it.");

            string url = await ResolveDownloadAsync(release.AssetApiUrl, token, ct);
            await Tools.DownloadAsync(url, dest, "Downloading Utylix " + release.Version.ToString(3), status, ct);
            status("Verifying…");
            long length = new FileInfo(dest).Length;
            if (release.Size > 0 && length != release.Size) throw new UpdateException("The download is not the size GitHub lists for it, so it was discarded.");
            try { Tools.VerifySha256(dest, expected); }
            catch (InvalidDataException e) { throw new UpdateException(e.Message); }
            return dest;
        }
        catch { Tools.TryDelete(dest); throw; }
    }

    /// <summary>The API link answers with a short-lived download address: ask for it with the token, then download that without it.</summary>
    private static async Task<string> ResolveDownloadAsync(string assetApiUrl, string token, CancellationToken ct)
    {
        using var http = NewClient(follow: false);
        using var r = await http.SendAsync(Get(assetApiUrl, token, "application/octet-stream"), HttpCompletionOption.ResponseHeadersRead, ct);
        if (r.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect)
        {
            var to = r.Headers.Location ?? throw new UpdateException("GitHub gave no download address.");
            bool loopback = to.IsLoopback && ApiBase.StartsWith("http://127.0.0.1", StringComparison.Ordinal);
            bool github = to.Scheme == Uri.UriSchemeHttps && (to.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase) || to.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) || to.Host == "github.com");
            if (!loopback && !github) throw new UpdateException("GitHub sent the download somewhere unexpected (" + to.Host + "), so Utylix stopped.");
            return to.ToString();
        }
        if (r.IsSuccessStatusCode && ApiBase.StartsWith("http://127.0.0.1", StringComparison.Ordinal)) return assetApiUrl;      // a test server that serves the file itself
        throw Explain(r.StatusCode, token);
    }

    /// <summary>
    /// Puts the downloaded file in place of the running program (a running exe can be renamed, not overwritten) and starts it;
    /// the new copy waits for this one to exit. The caller then quits.
    /// </summary>
    public static void Apply(string newFile)
    {
        if (Path.GetFileName(newFile).StartsWith("Utylix-Setup", StringComparison.OrdinalIgnoreCase))
        {
            // the setup replaces this program's files (it closes this copy first) and starts the new version
            var setup = new ProcessStartInfo(newFile) { UseShellExecute = false, WorkingDirectory = Path.GetTempPath() };
            setup.ArgumentList.Add("--setup-update");
            setup.ArgumentList.Add("--dir");
            setup.ArgumentList.Add(Path.GetDirectoryName(ExePath)!);
            Process.Start(setup);
            return;
        }
        string exe = ExePath, old = exe + ".old";
        try
        {
            Tools.TryDelete(old);
            File.Move(exe, old, true);
            try { File.Move(newFile, exe, true); }
            catch { File.Move(old, exe, true); throw; }                     // could not put the new one in place: keep the old
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Tools.TryDelete(newFile);
            throw new UpdateException("Utylix cannot replace itself here (" + e.Message + "). If it is in a protected folder, run the update as administrator or move Utylix to a normal folder.");
        }
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        // start again the way this copy was started (same port / data folder in tests), quietly, and tell it what to wait for
        var args = Environment.GetCommandLineArgs().Skip(1).ToList();
        for (int i = 0; i < args.Count; i++)
            if (args[i] == "--wait-for-pid") { args.RemoveAt(i); if (i < args.Count) args.RemoveAt(i); i--; }
        foreach (var a in args.Where(a => a is not ("--updated" or "--brightness" or "--minimized")))
            psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--minimized");
        psi.ArgumentList.Add("--updated");
        psi.ArgumentList.Add("--wait-for-pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(psi);
    }

    /// <summary>At start: remove what the last update left behind.</summary>
    public static void CleanLeftovers()
    {
        try { if (Environment.ProcessPath is { } exe) { Tools.TryDelete(exe + ".old"); Tools.TryDelete(exe + ".update"); } }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
