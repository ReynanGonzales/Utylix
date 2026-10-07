using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;

namespace IdmClone.Engine;

/// <param name="Inf">the name Windows gave the package in its driver store ("oem26.inf")</param>
public sealed record DriverPackage(string Inf, string ClassName, string Provider, string Version, IReadOnlyList<string> Devices)
{
    public bool IsNetwork => DriverKit.IsNetworkClass(ClassName) && !DriverKit.IsVirtual(string.Join(" ", Devices) + " " + Provider);
}

/// <summary>A driver file (.inf) found in a folder.</summary>
public sealed record InfFile(string Path, string ClassName, string Provider)
{
    public bool IsNetwork => DriverKit.IsNetworkClass(ClassName);
}

/// <summary>A device Windows lists with a problem (most often: no driver).</summary>
public sealed record DriverProblem(string Name, string HardwareId, int Code, string Why);

/// <summary>A network card of this PC.</summary>
public sealed record NetCard(string Name, bool IsWifi, bool Enabled);

/// <summary>
/// The "Drivers" tool: save the drivers installed on this PC to a folder (Windows' own export, no administrator rights needed), put them back with Windows' own
/// installer (needs administrator rights) and list what this PC is missing. Nothing is downloaded and no driver is shipped inside Utylix; Windows still refuses a driver
/// that isn't signed.
/// </summary>
public static class DriverKit
{
    private static string Pnputil => Path.Combine(Environment.SystemDirectory, "pnputil.exe");

    public static bool IsNetworkClass(string cls) => cls.Equals("Net", StringComparison.OrdinalIgnoreCase) || cls.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase);

    /// <summary>Virtual adapters (VMware, Hyper-V, VPNs ...) are of no use on another PC.</summary>
    public static bool IsVirtual(string text) => Regex.IsMatch(text, @"virtual|vmware|vmnet|hyper-v|vpn|tap-windows|wintun|loopback|miniport|pawnio", RegexOptions.IgnoreCase);

    /// <summary>The drivers that are not part of Windows itself (they have an "oemNN.inf" name in the driver store), one entry per package.</summary>
    public static List<DriverPackage> Installed()
    {
        var map = new Dictionary<string, (string Cls, string Provider, string Version, List<string> Devices)>(StringComparer.OrdinalIgnoreCase);
        using var search = new ManagementObjectSearcher("SELECT InfName, DeviceClass, DriverProviderName, DriverVersion, DeviceName FROM Win32_PnPSignedDriver WHERE InfName LIKE 'oem%.inf'");
        foreach (ManagementObject row in search.Get())
        {
            string inf = (row["InfName"] as string ?? "").Trim();
            if (!Regex.IsMatch(inf, @"^oem\d{1,6}\.inf$", RegexOptions.IgnoreCase)) continue;
            if (!map.TryGetValue(inf, out var entry)) map[inf] = entry = (row["DeviceClass"] as string ?? "", row["DriverProviderName"] as string ?? "", row["DriverVersion"] as string ?? "", new List<string>());
            string device = row["DeviceName"] as string ?? "";
            if (device.Length > 0 && !entry.Devices.Contains(device)) entry.Devices.Add(device);
        }
        return map.Select(kv => new DriverPackage(kv.Key.ToLowerInvariant(), kv.Value.Cls, kv.Value.Provider, kv.Value.Version, kv.Value.Devices))
                  .OrderBy(p => p.ClassName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Provider, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<NetCard> NetworkCards()
    {
        var list = new List<NetCard>();
        using var search = new ManagementObjectSearcher("SELECT Name, NetEnabled, PhysicalAdapter FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE");
        foreach (ManagementObject row in search.Get())
        {
            string name = row["Name"] as string ?? "";
            if (name.Length == 0 || IsVirtual(name)) continue;
            list.Add(new NetCard(name, Regex.IsMatch(name, @"wi-?fi|wireless|wlan|802\.11", RegexOptions.IgnoreCase), row["NetEnabled"] as bool? ?? false));
        }
        return list;
    }

    private static string Why(int code) => code switch
    {
        28 => "no driver installed",
        1 => "not set up correctly",
        3 => "the driver is damaged or memory is low",
        10 => "the device can't start",
        12 => "no free resources for it",
        18 => "the driver has to be installed again",
        22 => "turned off",
        24 => "not there or not working",
        31 => "Windows can't load the driver",
        37 => "the driver failed to start",
        39 => "the driver is damaged or missing",
        43 => "Windows stopped it because it reported a problem",
        _ => "problem " + code,
    };

    /// <summary>Devices Windows lists with a problem.</summary>
    public static List<DriverProblem> Problems()
    {
        var list = new List<DriverProblem>();
        using var search = new ManagementObjectSearcher("SELECT Name, DeviceID, HardwareID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
        foreach (ManagementObject row in search.Get())
        {
            int code = Convert.ToInt32(row["ConfigManagerErrorCode"] ?? 0);
            if (code == 22) continue;                                           // (turned off by the person: not a missing driver)
            string id = row["HardwareID"] is string[] ids && ids.Length > 0 ? ids[0] : (row["DeviceID"] as string ?? "");
            list.Add(new DriverProblem((row["Name"] as string) is { Length: > 0 } n ? n : "Unknown device", id, code, Why(code)));
        }
        return list;
    }

    // ---------- saving ----------
    private static (int Code, string Output) RunPnputil(string arguments)
    {
        var psi = new ProcessStartInfo(Pnputil, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi) ?? throw new IOException("pnputil could not be started.");
        string text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(10 * 60 * 1000);
        return (p.ExitCode, text);
    }

    private static bool Ok(int code) => code is 0 or 3010;                       // (3010 = done, a restart is needed)

    private static void CheckFolder(string folder)
    {
        if (!Path.IsPathRooted(folder) || folder.Contains('"')) throw new ArgumentException("That folder can't be used.");
    }

    /// <summary>Saves driver packages (by their oemNN.inf names) into <paramref name="dest"/>, one subfolder each. Returns how many were saved.</summary>
    public static int Export(string dest, IEnumerable<DriverPackage> packages, IProgress<string>? progress, System.Threading.CancellationToken cancel)
    {
        CheckFolder(dest);
        Directory.CreateDirectory(dest);
        int saved = 0;
        foreach (var package in packages)
        {
            cancel.ThrowIfCancellationRequested();
            string inf = package.Inf;
            if (!Regex.IsMatch(inf, @"^oem\d{1,6}\.inf$", RegexOptions.IgnoreCase)) continue;
            progress?.Report(inf);
            // one folder for each package: two packages may carry files with the same name, which must not overwrite each other
            string label = $"{package.ClassName} - {package.Devices.FirstOrDefault() ?? package.Provider} ({inf[..^4]})";
            foreach (char c in Path.GetInvalidFileNameChars()) label = label.Replace(c, '_');
            string folder = Path.Combine(dest, label.Length > 90 ? label[..90].TrimEnd() + ")" : label);
            Directory.CreateDirectory(folder);
            var (code, _) = RunPnputil($"/export-driver {inf} \"{folder}\"");
            if (Ok(code)) saved++;
        }
        return saved;
    }

    /// <summary>A short list in the saved folder: when and where it was saved from, and what is in it.</summary>
    public static void WriteNote(string dest, IReadOnlyList<DriverPackage> packages)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Drivers saved by Utylix on {DateTime.Now:yyyy-MM-dd HH:mm} from {Environment.MachineName}.");
        sb.AppendLine("Put them back with Utylix > Drivers > Install drivers from a folder (or Device Manager > Update driver > Browse).");
        sb.AppendLine();
        foreach (var p in packages) sb.AppendLine($"{p.ClassName,-16} {p.Provider}  {p.Version}  -  {string.Join(", ", p.Devices)}");
        File.WriteAllText(Path.Combine(dest, "Utylix-drivers.txt"), sb.ToString());
    }

    // ---------- installing ----------
    private static string Clean(string s) => s.Trim().Trim('"');

    /// <summary>The class and the maker written in a driver file (the maker is often a %name% explained at the end of the file).</summary>
    public static (string ClassName, string Provider) ReadInf(string path)
    {
        string text;
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            text = bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xFE ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ("", ""); }
        string section = "", cls = "", provider = "", guid = "";
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int semi = line.IndexOf(';');
            if (semi >= 0) line = line[..semi].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim(), value = Clean(line[(eq + 1)..]);
            if (section.Equals("Version", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("Class", StringComparison.OrdinalIgnoreCase)) cls = value;
                else if (key.Equals("ClassGUID", StringComparison.OrdinalIgnoreCase)) guid = value;
                else if (key.Equals("Provider", StringComparison.OrdinalIgnoreCase)) provider = value;
            }
            else if (section.Equals("Strings", StringComparison.OrdinalIgnoreCase)) strings[key] = value;
        }
        if (provider.StartsWith('%') && provider.EndsWith('%') && strings.TryGetValue(provider.Trim('%'), out var named)) provider = named;
        if (cls.Length == 0)
        {
            guid = guid.Trim('{', '}');
            if (guid.Equals("4d36e972-e325-11ce-bfc1-08002be10318", StringComparison.OrdinalIgnoreCase)) cls = "Net";
            else if (guid.Equals("e0cbf06c-cd8b-4647-bb8a-263b43f0f974", StringComparison.OrdinalIgnoreCase)) cls = "Bluetooth";
        }
        return (cls, provider);
    }

    /// <summary>Every driver file (.inf) in a folder and its subfolders.</summary>
    public static List<InfFile> FindInfs(string folder)
    {
        var list = new List<InfFile>();
        if (!Directory.Exists(folder)) return list;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return list; }
        foreach (string f in files)
        {
            var (cls, provider) = ReadInf(f);
            list.Add(new InfFile(f, cls, provider));
        }
        return list;
    }

    /// <summary>Runs with administrator rights ("--drivers-install folder 0|1"): puts the drivers of a folder into Windows and installs them. 0 = all went in, 1 = some did not, 2 = bad request.</summary>
    public static int RunInstall(string folder, bool networkOnly)
    {
        try { CheckFolder(folder); } catch (ArgumentException) { return 2; }
        if (!Directory.Exists(folder)) return 2;
        int failed = 0;
        foreach (var inf in FindInfs(folder))
        {
            if (networkOnly && !inf.IsNetwork) continue;
            var (code, _) = RunPnputil($"/add-driver \"{inf.Path}\" /install");
            if (!Ok(code)) failed++;
        }
        return failed == 0 ? 0 : 1;
    }
}
