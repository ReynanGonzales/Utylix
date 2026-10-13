using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace IdmClone.Engine;

/// <summary>One program with a memory limit: when it uses more than <see cref="LimitMb"/> the guard trims it (or, with <see cref="Deep"/>, frees everything).</summary>
public sealed class MemoryRule
{
    public string Exe { get; set; } = "";
    public int LimitMb { get; set; } = 200;
    public bool Deep { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class GuardRow { public string Exe { get; set; } = ""; public long WorkingMb { get; set; } public string LastTrim { get; set; } = ""; public int Trims { get; set; } }
public sealed class GuardStatus { public bool Elevated { get; set; } public DateTime Updated { get; set; } public List<GuardRow> Rows { get; set; } = new(); }

/// <summary>The list of limits (memory-limits.json in the data folder). The guard re-reads it whenever it changes.</summary>
public static class MemoryLimits
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string FileIn(string dataDir) => Path.Combine(dataDir, "memory-limits.json");
    public static string StatusFileIn(string dataDir) => Path.Combine(dataDir, "memory-guard-status.json");

    /// <summary>"C:\Games\Cabal\Cabal.exe" or "cabal" -> "cabal.exe" (only a file name: it is matched against running programs).</summary>
    public static string CleanExe(string? text)
    {
        string name = Path.GetFileName((text ?? "").Trim().Trim('"'));
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
        if (name.Length == 0) return "";
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    public static List<MemoryRule> Load(string dataDir)
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<MemoryRule>>(File.ReadAllText(FileIn(dataDir)), Options) ?? new();
            foreach (var r in list) { r.Exe = CleanExe(r.Exe); r.LimitMb = Math.Clamp(r.LimitMb, 20, 65536); }
            return list.Where(r => r.Exe.Length > 0).ToList();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public static void Save(string dataDir, List<MemoryRule> rules)
    {
        try { File.WriteAllText(FileIn(dataDir), JsonSerializer.Serialize(rules, Options)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* next change will try again */ }
    }

    public static GuardStatus? LoadStatus(string dataDir)
    {
        try { return JsonSerializer.Deserialize<GuardStatus>(File.ReadAllText(StatusFileIn(dataDir)), Options); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>
/// Watches the programs of the list every few seconds (nothing runs when the list is empty) and trims the ones above their limit. It runs inside
/// Utylix in the tray, or - when the person allowed it once - as a small separate copy with administrator rights ("--memory-guard", started
/// by a scheduled task), which can also reach programs that run as administrator (game clients). Only one guard runs at a time (a named mutex).
/// </summary>
public static class MemoryGuard
{
    private const string MutexName = "Local\\Utylix.MemoryGuard";
    private static Thread? _local;
    private static volatile bool _stop;
    private static Mutex? _heldMutex;

    /// <summary>True while some guard (here or in the administrator copy) is running.</summary>
    public static bool IsRunning
    {
        get
        {
            try { using var m = Mutex.OpenExisting(MutexName); return true; }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (UnauthorizedAccessException) { return true; }          // (exists, made by an elevated copy)
        }
    }

    /// <summary>Starts the guard that is right for now: the administrator copy when its task exists, otherwise a thread in this program.</summary>
    public static void Ensure(string dataDir)
    {
        if (MemoryLimits.Load(dataDir).All(r => !r.Enabled)) return;
        if (IsRunning) return;
        if (MemoryTask.GuardExists() && MemoryTask.RunGuard()) return;
        StartLocal(dataDir);
    }

    /// <summary>Hands over to the administrator copy (after the person allowed it): the thread in this program ends.</summary>
    public static void Restart(string dataDir)
    {
        StopLocal();
        Thread.Sleep(300);
        Ensure(dataDir);
    }

    public static void StartLocal(string dataDir)
    {
        if (_local != null && _local.IsAlive) return;
        _stop = false;
        _local = new Thread(() => { Loop(dataDir, ownerIsMutex: false); }) { IsBackground = true, Name = "Utylix memory guard" };
        _local.Start();
    }

    public static void StopLocal()
    {
        _stop = true;
        var t = _local;
        if (t != null && t.IsAlive) t.Join(7000);
        _local = null;
    }

    /// <summary>The administrator copy: runs until Utylix (the tray) is gone or the list has been empty for a minute.</summary>
    public static int RunProcess(string dataDir)
    {
        Loop(dataDir, ownerIsMutex: true);
        return 0;
    }

    private static bool TrayAlive()
    {
        try { using var m = Mutex.OpenExisting("Local\\Utylix.SingleInstance"); return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static void Loop(string dataDir, bool ownerIsMutex)
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(true, MutexName, out bool first);
            if (!first) { mutex.Dispose(); return; }
            _heldMutex = mutex;
            bool elevated = MemoryCleaner.IsElevated;
            var cooldown = new Dictionary<int, DateTime>();
            var trims = new Dictionary<string, (int Count, DateTime Last)>(StringComparer.OrdinalIgnoreCase);
            DateTime stamp = DateTime.MinValue;
            var rules = new List<MemoryRule>();
            int idle = 0;
            while (!_stop)
            {
                if (ownerIsMutex && !TrayAlive()) break;
                try
                {
                    var t = File.GetLastWriteTimeUtc(MemoryLimits.FileIn(dataDir));
                    if (t != stamp) { stamp = t; rules = MemoryLimits.Load(dataDir); }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                var active = rules.Where(r => r.Enabled).ToList();
                if (active.Count == 0) { if (++idle > 12) break; } else idle = 0;

                var rows = new List<GuardRow>();
                bool freedAll = false;
                foreach (var rule in active)
                {
                    string name = Path.GetFileNameWithoutExtension(rule.Exe);
                    long biggest = 0;
                    Process[] procs;
                    try { procs = Process.GetProcessesByName(name); }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { continue; }
                    foreach (var p in procs)
                    {
                        try
                        {
                            p.Refresh();
                            long ws = p.WorkingSet64;
                            biggest = Math.Max(biggest, ws);
                            if (ws <= rule.LimitMb * 1024L * 1024L) continue;
                            double wait = rule.Deep ? 60 : 20;                     // (a limit the program needs more than keeps being passed: do not thrash)
                            if (cooldown.TryGetValue(p.Id, out var last) && (DateTime.UtcNow - last).TotalSeconds < wait) continue;
                            cooldown[p.Id] = DateTime.UtcNow;
                            if (rule.Deep && !freedAll) { MemoryCleaner.Free(elevated); freedAll = true; }
                            else if (!rule.Deep) MemoryCleaner.TrimProcess(p.Id);
                            trims.TryGetValue(rule.Exe, out var c);
                            trims[rule.Exe] = (c.Count + 1, DateTime.Now);
                        }
                        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                        finally { p.Dispose(); }
                    }
                    trims.TryGetValue(rule.Exe, out var tr);
                    rows.Add(new GuardRow { Exe = rule.Exe, WorkingMb = biggest / (1024 * 1024), Trims = tr.Count, LastTrim = tr.Count > 0 ? tr.Last.ToString("HH:mm:ss") : "" });
                }
                foreach (int dead in cooldown.Keys.Where(k => (DateTime.UtcNow - cooldown[k]).TotalMinutes > 10).ToList()) cooldown.Remove(dead);
                try
                {
                    File.WriteAllText(MemoryLimits.StatusFileIn(dataDir), System.Text.Json.JsonSerializer.Serialize(new GuardStatus { Elevated = elevated, Updated = DateTime.Now, Rows = rows }));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                for (int i = 0; i < 25 && !_stop; i++) Thread.Sleep(200);           // 5 s, but quick to stop
            }
        }
        finally
        {
            try { mutex?.ReleaseMutex(); } catch (ApplicationException) { }
            mutex?.Dispose();
            _heldMutex = null;
        }
    }
}
