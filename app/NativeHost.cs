using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace IdmClone;

/// <summary>
/// Lets the browser extension start Utylix when it is closed, without any pop-up.
///
/// Browsers allow this through "native messaging": the extension asks the browser to run a registered program,
/// the browser starts it and talks to it over stdin/stdout. Here that program is Utylix.exe itself, started
/// with the extension's address as its first argument. In that mode it does exactly one thing - if the app isn't
/// running it starts it - and exits. It only answers the one Utylix extension (fixed ID) and does nothing else.
/// </summary>
public static class NativeHost
{
    public const string HostName = "com.idmclone.host";
    /// <summary>Derived from the "key" in extension/manifest.json, so it is the same on every PC.</summary>
    public const string ExtensionId = "cpggenldolfdkngbboephajkoankclkj";

    private static readonly string[] BrowserKeys =
    {
        @"Software\Google\Chrome", @"Software\BraveSoftware\Brave-Browser", @"Software\Microsoft\Edge",
        @"Software\Chromium", @"Software\Vivaldi",
    };

    // ---------- registering the host with the browsers (per user, no admin rights) ----------
    public static void Register(string dataDir, bool enabled)
    {
        try
        {
            string manifest = Path.Combine(dataDir, "native", HostName + ".json");
            if (enabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
                File.WriteAllText(manifest, JsonSerializer.Serialize(new
                {
                    name = HostName,
                    description = "Lets the Utylix browser extension start the Utylix app",
                    path = Environment.ProcessPath,
                    type = "stdio",
                    allowed_origins = new[] { $"chrome-extension://{ExtensionId}/" },
                }));
            }
            foreach (var browser in BrowserKeys)
            {
                string key = $@"{browser}\NativeMessagingHosts\{HostName}";
                if (enabled)
                {
                    using var k = Registry.CurrentUser.CreateSubKey(key);
                    k.SetValue("", manifest);                       // the default value points at the manifest file
                }
                else Registry.CurrentUser.DeleteSubKey(key, throwOnMissingSubKey: false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* not fatal */ }
    }

    // ---------- host mode ----------
    public static bool IsHostLaunch(string[] args) =>
        args.Any(a => a.StartsWith("chrome-extension://", StringComparison.Ordinal) || a.StartsWith("moz-extension://", StringComparison.Ordinal));

    /// <summary>Answers one "start" message: makes sure the app is running, then returns (the process exits).</summary>
    public static int RunHost(string[] args, string dataDir)
    {
        try
        {
            string origin = args.First(a => a.Contains("-extension://"));
            // This is a windowed app, so .NET's Console streams can be empty stand-ins; use the handles the browser gave us.
            using var stdin = new FileStream(new SafeFileHandle(GetStdHandle(-10), false), FileAccess.Read, 1, false);
            using var stdout = new FileStream(new SafeFileHandle(GetStdHandle(-11), false), FileAccess.Write, 1, false);

            var read = Task.Run(() => ReadMessage(stdin));
            if (!read.Wait(TimeSpan.FromSeconds(8)) || read.Result == null)
            {
                Note(dataDir, $"no message received from the browser (stdin handle {GetStdHandle(-10)}, stdout handle {GetStdHandle(-11)}, completed: {read.IsCompleted}, problem: {LastReadProblem})");
                return 1;
            }
            string? cmd;
            using (var doc = JsonDocument.Parse(read.Result))
                cmd = doc.RootElement.TryGetProperty("cmd", out var c) ? c.GetString() : null;

            // the port belongs to the Windows user who runs this browser: with several users signed in, each has their own copy of Utylix
            int explicitPort = 0;
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--port" && int.TryParse(args[i + 1], out int ep)) explicitPort = ep;
            object reply;
            if (origin != $"chrome-extension://{ExtensionId}/") reply = new { ok = false, error = "unknown extension" };
            else if (cmd == "info") { int live = ApiPort.Current(dataDir, explicitPort > 0 ? explicitPort : null); reply = new { ok = true, port = live, user = ApiPort.UserId, running = Ping(live) }; }
            else if (cmd != "start") reply = new { ok = false, error = "unknown command" };
            else if (!AllowedByUser(dataDir)) reply = new { ok = false, error = "disabled in Utylix settings" };
            else reply = EnsureRunning(args, dataDir, explicitPort > 0 ? explicitPort : null);

            WriteMessage(stdout, reply);
            return 0;
        }
        catch (Exception e)
        {
            Note(dataDir, e.ToString());
            return 1;
        }
    }

    /// <summary>There is no console to print to: leave a note where a person can find it.</summary>
    private static void Note(string dataDir, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(dataDir, "native"));
            File.AppendAllText(Path.Combine(dataDir, "native", "host-error.log"), $"{DateTime.Now:s} {text}\n");
        }
        catch (Exception) { /* nothing more we can do */ }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int stdHandle);

    private static object EnsureRunning(string[] args, string dataDir, int? explicitPort)
    {
        int port = ApiPort.Current(dataDir, explicitPort);
        if (Ping(port)) return new { ok = true, started = false, port };

        // start a normal, minimized copy (a separate process, so it lives on after this helper exits)
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true };
        psi.ArgumentList.Add("--minimized");
        for (int i = 0; i < args.Length - 1; i++)                   // pass through --port/--data/--tools (used by tests)
            if (args[i] is "--port" or "--data" or "--tools") { psi.ArgumentList.Add(args[i]); psi.ArgumentList.Add(args[i + 1]); }
        if (args.Contains("--no-register")) psi.ArgumentList.Add("--no-register");
        Process.Start(psi);

        for (int i = 0; i < 40; i++)                                 // wait until it answers (up to ~12 s)
        {
            System.Threading.Thread.Sleep(300);
            port = ApiPort.Current(dataDir, explicitPort);                   // the new copy may have taken another port
            if (Ping(port)) return new { ok = true, started = true, port };
        }
        return new { ok = false, error = "the app did not start" };
    }

    private static bool Ping(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            string body = http.GetStringAsync($"http://127.0.0.1:{port}/api/ping").GetAwaiter().GetResult();
            return body.Contains("\"idm-clone\"", StringComparison.Ordinal) && body.Contains($"\"user\":\"{ApiPort.UserId}\"", StringComparison.Ordinal);      // OUR copy, not another user's
        }
        catch (Exception) { return false; }
    }

    private static bool AllowedByUser(string dataDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDir, "config.json")));
            return !(doc.RootElement.TryGetProperty("allow_autostart_by_browser", out var v) && v.ValueKind == JsonValueKind.False);
        }
        catch (Exception) { return true; }                           // no settings file yet: default is allowed
    }

    private static string LastReadProblem = "";

    // Native messaging framing: 4-byte little-endian length, then that many bytes of UTF-8 JSON.
    private static string? ReadMessage(Stream s)
    {
        var len = new byte[4];
        if (!ReadFully(s, len)) return null;
        int n = BitConverter.ToInt32(len);
        if (n <= 0 || n > 65536) { LastReadProblem = $"bad message length {n} (first bytes {BitConverter.ToString(len)})"; return null; }
        var body = new byte[n];
        return ReadFully(s, body) ? Encoding.UTF8.GetString(body) : null;
    }

    private static bool ReadFully(Stream s, byte[] buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int r;
            try { r = s.Read(buf, off, buf.Length - off); }
            catch (Exception e) { LastReadProblem = e.GetType().Name + ": " + e.Message; return false; }
            if (r <= 0) { LastReadProblem = $"end of input after {off} of {buf.Length} bytes"; return false; }
            off += r;
        }
        return true;
    }

    private static void WriteMessage(Stream s, object reply)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(reply);
        s.Write(BitConverter.GetBytes(body.Length));
        s.Write(body);
        s.Flush();
    }
}
