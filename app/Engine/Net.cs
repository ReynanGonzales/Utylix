using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace IdmClone.Engine;

/// <summary>An error that retrying will not fix (404, server dropped Range support...).</summary>
public sealed class FatalException(string message) : Exception(message);

public sealed class HttpStatusException(int code, string reason) : Exception($"HTTP {code} {reason}")
{
    public int Code { get; } = code;
}

public static class Net
{
    public const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                             "(KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,               // redirects are followed by hand (see SendAsync)
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        MaxConnectionsPerServer = 64,
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>GET with manual redirects. Cookies/credentials are dropped when the host changes.</summary>
    public static async Task<HttpResponseMessage> SendAsync(
        string url, IReadOnlyDictionary<string, string> headers, string? range, CancellationToken ct)
    {
        var uri = new Uri(url);
        bool dropAuth = false;
        for (int hop = 0; hop < 10; hop++)
        {
            var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["User-Agent"] = UA, ["Accept"] = "*/*", ["Accept-Encoding"] = "identity",
            };
            foreach (var kv in headers) h[kv.Key] = kv.Value;
            if (dropAuth) { h.Remove("Cookie"); h.Remove("Authorization"); }
            if (range != null) h["Range"] = range;

            var req = new HttpRequestMessage(HttpMethod.Get, uri);
            foreach (var kv in h) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            int code = (int)resp.StatusCode;
            if ((code is 301 or 302 or 303 or 307 or 308) && resp.Headers.Location is { } loc)
            {
                var next = loc.IsAbsoluteUri ? loc : new Uri(uri, loc);
                resp.Dispose();
                if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps)
                    throw new FatalException("Redirected to an unsupported address");
                if (!string.Equals(next.Host, uri.Host, StringComparison.OrdinalIgnoreCase) || next.Port != uri.Port)
                    dropAuth = true;
                uri = next;
                continue;
            }
            return resp;
        }
        throw new FatalException("Too many redirects");
    }

    public static void EnsureSuccess(HttpResponseMessage r)
    {
        if (!r.IsSuccessStatusCode)
            throw new HttpStatusException((int)r.StatusCode, r.ReasonPhrase ?? "");
    }

    public static bool IsFatal(Exception e) =>
        e is FatalException || (e is HttpStatusException h && h.Code < 500 && h.Code != 408 && h.Code != 429);

    public static string Describe(Exception e) => e switch
    {
        HttpRequestException hr => "Connection failed: " + (hr.InnerException?.Message ?? hr.Message),
        OperationCanceledException => "Timed out",
        _ => string.IsNullOrEmpty(e.Message) ? e.GetType().Name : e.Message,
    };
}

public static class Util
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly Dictionary<string, string> Extensions = new()
    {
        ["video/mp4"] = ".mp4", ["video/webm"] = ".webm", ["audio/mpeg"] = ".mp3", ["audio/mp4"] = ".m4a",
        ["audio/ogg"] = ".ogg", ["audio/wav"] = ".wav", ["application/pdf"] = ".pdf", ["application/zip"] = ".zip",
        ["image/jpeg"] = ".jpg", ["image/png"] = ".png", ["image/gif"] = ".gif", ["image/webp"] = ".webp",
        ["text/plain"] = ".txt", ["application/x-7z-compressed"] = ".7z",
    };

    // ---------- pictures with a misleading name (a photo called "photo.img" is not a disk image) ----------
    private static readonly Dictionary<string, string> PictureTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = ".jpg", ["image/jpg"] = ".jpg", ["image/pjpeg"] = ".jpg", ["image/png"] = ".png", ["image/gif"] = ".gif",
        ["image/webp"] = ".webp", ["image/bmp"] = ".bmp", ["image/avif"] = ".avif", ["image/heic"] = ".heic", ["image/heif"] = ".heic",
        ["image/tiff"] = ".tif", ["image/svg+xml"] = ".svg", ["image/x-icon"] = ".ico", ["image/vnd.microsoft.icon"] = ".ico",
    };
    private static readonly HashSet<string> PictureExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".webp", ".bmp", ".avif", ".heic", ".heif", ".tif", ".tiff", ".svg", ".ico" };
    /// <summary>Endings that say nothing about what a file is (or, like .img, mean something else): a picture may hide behind them.</summary>
    private static readonly HashSet<string> WeakExtensions = new(StringComparer.OrdinalIgnoreCase)
        { "", ".img", ".bin", ".dat", ".tmp", ".php", ".asp", ".aspx", ".ashx", ".jsp", ".cgi", ".download", ".file", ".image" };

    /// <summary>
    /// The server says it is a picture (image/jpeg, image/png ...) but the name does not: give it a picture ending, so it
    /// is saved as a picture (right folder, right icon) instead of, say, a "Disk Image" because it ends in .img.
    /// </summary>
    public static string FixPictureName(string name, string? contentType)
    {
        string type = (contentType ?? "").Split(';')[0].Trim();
        if (!PictureTypes.TryGetValue(type, out var wanted)) return name;
        string ext = Path.GetExtension(name);
        if (PictureExtensions.Contains(ext)) return name;                              // already named like a picture
        return (WeakExtensions.Contains(ext) ? name[..^ext.Length] : name) + wanted;   // .img -> .png, "x.v2" -> "x.v2.png"
    }

    /// <summary>The picture type a file really is, judged by its first bytes (null = not a picture we recognise).</summary>
    public static string? PictureExtensionFromBytes(string path)
    {
        try
        {
            var b = new byte[32];
            int n;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) n = fs.Read(b, 0, b.Length);
            if (n < 12) return null;
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A) return ".png";
            if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
            if (b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8' && (b[4] == '7' || b[4] == '9') && b[5] == 'a') return ".gif";
            if (b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return ".webp";
            if (b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p')
            {
                string brand = Encoding.ASCII.GetString(b, 8, 4);
                if (brand is "avif" or "avis") return ".avif";
                if (brand is "heic" or "heix" or "mif1" or "msf1") return ".heic";
            }
            // BMP: "BM" is only two bytes, so also check the header fields make sense
            if (b[0] == 'B' && b[1] == 'M' && b[6] == 0 && b[7] == 0 && b[8] == 0 && b[9] == 0 && n >= 18 &&
                BitConverter.ToInt32(b, 14) is 12 or 40 or 52 or 56 or 64 or 108 or 124) return ".bmp";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>If a file ending in .img / .bin / nothing turns out to be a picture, the picture ending; else null.</summary>
    public static string? RealPictureEnding(string fileName, string path)
    {
        string ext = Path.GetExtension(fileName);
        if (!WeakExtensions.Contains(ext)) return null;
        return PictureExtensionFromBytes(path);
    }

    /// <summary>Programs and scripts: opening one would RUN it, so it is never done automatically.</summary>
    public static bool IsRunnable(string fileName) => RunnableExtensions.Contains(Path.GetExtension(fileName));
    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".appx", ".appxbundle", ".msp", ".bat", ".cmd", ".com", ".scr", ".pif", ".ps1", ".psm1", ".vbs", ".vbe",
        ".js", ".jse", ".wsf", ".wsh", ".hta", ".jar", ".reg", ".lnk", ".url", ".cpl", ".msc", ".gadget", ".application", ".dll", ".sys",
    };

    public static string WithEnding(string name, string ending) => Path.GetFileNameWithoutExtension(name) + ending;

    public static string Sanitize(string? name)
    {
        name = Path.GetFileName((name ?? "").Replace('\\', '/'));
        name = Regex.Replace(name, "[<>:\"/\\\\|?*\\x00-\\x1f]", "_").Trim(' ', '.');
        int dot = name.IndexOf('.');
        if (Reserved.Contains(dot < 0 ? name : name[..dot])) name = "_" + name;
        if (name.Length > 180) name = name[..180];
        return name.Length == 0 ? "download" : name;
    }

    private static string PercentDecode(string s, Encoding enc)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 2 < s.Length + 0 && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
            {
                bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                i += 2;
            }
            else bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
        }
        return enc.GetString(bytes.ToArray());
    }

    public static string FileNameFromResponse(HttpResponseMessage r, Uri finalUri)
    {
        string cd = r.Content.Headers.TryGetValues("Content-Disposition", out var v) ? string.Join(";", v) : "";
        string name = "";
        var m = Regex.Match(cd, @"filename\*\s*=\s*([^']*)'[^']*'([^;]+)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            Encoding enc;
            try { enc = Encoding.GetEncoding(m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : "utf-8"); }
            catch (ArgumentException) { enc = Encoding.UTF8; }
            name = PercentDecode(m.Groups[2].Value.Trim(), enc);
        }
        else
        {
            m = Regex.Match(cd, "filename\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (!m.Success) m = Regex.Match(cd, @"filename\s*=\s*([^;]+)", RegexOptions.IgnoreCase);
            if (m.Success) name = m.Groups[1].Value.Trim();
        }
        if (name.Length == 0)
        {
            string last = finalUri.AbsolutePath.Substring(finalUri.AbsolutePath.LastIndexOf('/') + 1);
            name = Uri.UnescapeDataString(last);
        }
        name = Sanitize(name);
        var ctype = r.Content.Headers.ContentType?.MediaType;
        if (!name.Contains('.') && ctype != null && Extensions.TryGetValue(ctype, out var ext)) name += ext;
        return name;
    }

    public static string UniqueName(string dir, string name, ISet<string>? taken = null)
    {
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        string cand = name;
        int i = 0;
        while ((taken?.Contains(cand) ?? false) || File.Exists(Path.Combine(dir, cand))
               || File.Exists(Path.Combine(dir, cand + Download.PartSuffix)))
            cand = $"{stem} ({++i}){ext}";
        return cand;
    }

    /// <summary>Tag the file as downloaded from the internet so SmartScreen still checks it.</summary>
    public static void MarkOfTheWeb(string path, string url, string? referer)
    {
        try
        {
            var sb = new StringBuilder("[ZoneTransfer]\r\nZoneId=3\r\n");
            if (!string.IsNullOrEmpty(referer)) sb.Append("ReferrerUrl=").Append(referer).Append("\r\n");
            sb.Append("HostUrl=").Append(url).Append("\r\n");
            File.WriteAllText(path + ":Zone.Identifier", sb.ToString(), Encoding.ASCII);
        }
        catch (Exception) { /* not NTFS, or blocked: harmless */ }
    }
}
