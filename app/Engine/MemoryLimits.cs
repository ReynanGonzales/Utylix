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
    /// <summary>0 = no memory limit (the rule may only be about the processor).</summary>
    public int LimitMb { get; set; } = 200;
    public bool Deep { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Only the copies of the program whose main window title contains this text (empty = every copy). Tells several clients of one game apart.</summary>
    public string Title { get; set; } = "";
    /// <summary>Which copies this row is for: 0 every copy (or, when a title is typed and this is still 0, only those containing it - rows made before this setting),
    /// 1 only the windows whose title contains <see cref="Title"/>, 2 every copy EXCEPT those whose title contains it (the main account stays free),
    /// 3 every copy except the one that was last in front (the one being played).</summary>
    public int Scope { get; set; }
    /// <summary>0 = no frame-rate limit. Otherwise the copies this row is for are held to about this many frames per second: the guard counts their frames
    /// (nothing is put into the game) and moves a processor cap up or down until the number fits. Needs the administrator guard.</summary>
    public int TargetFps { get; set; }
    /// <summary>Show the frames per second of the copies this row is for, on top of the program's window (Utylix's own counter, like RivaTuner's).</summary>
    public bool ShowFps { get; set; }

    /// <summary>Does this row apply to the copy with this window title? <paramref name="frontPid"/> = the copy of this program that was last in front.</summary>
    public bool Matches(string title, int pid, int frontPid)
    {
        string t = (Title ?? "").Trim();
        int scope = Scope == 0 && t.Length > 0 ? 1 : Scope;
        return scope switch
        {
            1 => t.Length == 0 || (title ?? "").Contains(t, StringComparison.OrdinalIgnoreCase),
            2 => t.Length == 0 || !(title ?? "").Contains(t, StringComparison.OrdinalIgnoreCase),
            3 => frontPid == 0 || pid != frontPid,
            _ => true,
        };
    }
    /// <summary>What to do about the processor use of this program (see CpuLimiter): 0 nothing, 1 lower priority, 2 efficiency mode, 3 cap.</summary>
    public int Cpu { get; set; }
    /// <summary>For Cpu = 3: the most processor the program may use, in percent of the whole CPU.</summary>
    public int CapPercent { get; set; } = 50;
}

public sealed class GuardRow
{
    public string Exe { get; set; } = ""; public long WorkingMb { get; set; } public string LastTrim { get; set; } = ""; public int Trims { get; set; } public bool CpuApplied { get; set; }
    /// <summary>Empty when the frame limiter is in (or not wanted); otherwise why it could not be put into the program.</summary>
    public string HookState { get; set; } = "";
}
public sealed class GuardStatus
{
    public bool Elevated { get; set; } public DateTime Updated { get; set; } public List<GuardRow> Rows { get; set; } = new();
    /// <summary>Frames per second by process id (only while the window is open and watching), and why there are none when that is the case.</summary>
    public Dictionary<int, int> Fps { get; set; } = new();
    public string FpsState { get; set; } = "";
}

/// <summary>The list of limits (memory-limits.json in the data folder). The guard re-reads it whenever it changes.</summary>
public static class MemoryLimits
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string FileIn(string dataDir) => Path.Combine(dataDir, "memory-limits.json");
    /// <summary>The window touches this file every couple of seconds while it is open: only then does the guard measure frames per second.</summary>
    public static string FpsWantedIn(string dataDir) => Path.Combine(dataDir, "memory-fps-wanted.txt");
    public static void WantFps(string dataDir) { try { File.WriteAllText(FpsWantedIn(dataDir), DateTime.UtcNow.ToString("O")); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
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
            foreach (var r in list) { r.Exe = CleanExe(r.Exe); r.LimitMb = r.LimitMb <= 0 ? 0 : Math.Clamp(r.LimitMb, 20, 65536); r.Cpu = Math.Clamp(r.Cpu, 0, 3); r.Scope = Math.Clamp(r.Scope, 0, 3); r.TargetFps = r.TargetFps <= 0 ? 0 : Math.Clamp(r.TargetFps, 1, 500); r.CapPercent = Math.Clamp(r.CapPercent, 1, 100); }
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
    // (UTYLIX_TEST_GUARD_MUTEX lets a test harness run beside the real guard)
    private static readonly string MutexName = Environment.GetEnvironmentVariable("UTYLIX_TEST_GUARD_MUTEX") is { Length: > 0 } testName ? "Local\\" + testName : "Local\\Utylix.MemoryGuard";
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

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    /// <summary>The process that owns the window in front right now (0 = none).</summary>
    public static int ForegroundPid()
    {
        try { GetWindowThreadProcessId(GetForegroundWindow(), out uint pid); return (int)pid; } catch (Exception e) when (e is System.ComponentModel.Win32Exception or DllNotFoundException) { return 0; }
    }

    // (with Utylix's own frame limiter (FpsHook) a frame-rate limit is a wait inside the program's Present call, so the processor is not touched for it;
    //  a build without the hook DLLs falls back to the rough processor cap)
    private static bool UsesHook(MemoryRule r) => r.TargetFps > 0 && FpsHook.Available;
    private static CpuMode EffMode(MemoryRule r) => UsesHook(r) ? (CpuMode)r.Cpu : r.TargetFps > 0 ? CpuMode.Cap : (CpuMode)r.Cpu;
    private static int EffCap(MemoryRule r) => UsesHook(r) ? r.CapPercent : r.TargetFps > 0 ? -r.TargetFps : r.CapPercent;     // (a frame-rate limit is remembered as a negative number)

    private static bool TrayAlive()
    {
        try { using var m = Mutex.OpenExisting("Local\\Utylix.SingleInstance"); return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static void Loop(string dataDir, bool ownerIsMutex)
    {
        Mutex? mutex = null;
        bool owned = false;
        var cpuDone = new Dictionary<int, (string Exe, CpuMode Mode, int Cap, CpuLimiter.Applied? Result)>();
        var lastFront = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);       // per program: the copy that was last in front
        using var fps = new FpsMeter();
        var fpsTried = DateTime.MinValue;
        var hooked = new HashSet<int>();                                        // programs that have Utylix's frame limiter in them
        var hookRetry = new Dictionary<int, DateTime>();                        // when to try a program again that would not take it
        var limitSet = new Dictionary<int, int>();                              // the limit written for each program (process id -> fps)
        var wantedNow = new Dictionary<int, int>();                             // what this round of the loop wants
        var hookState = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);       // per program name: why the limiter is not in (shown in the window)
        var hookCleaned = DateTime.UtcNow;
        var cpuAdjusted = new Dictionary<int, DateTime>();
        var fpsPrev = new Dictionary<int, double>();
        var cpuSeen = new Dictionary<int, (TimeSpan Cpu, DateTime At)>();       // for the frame-rate limit: how much processor a copy really used

        // Utylix's frame limiter in one copy of a program: the limit is written first (the DLL reads it as soon as it is in), then the DLL is put in once
        void HookCopy(MemoryRule rule, int pid, int want)
        {
            wantedNow[pid] = want;
            limitSet.TryGetValue(pid, out int had);
            if (had != want) { FpsHook.SetLimit(dataDir, pid, want); limitSet[pid] = want; }
            if ((want <= 0 && !rule.ShowFps) || hooked.Contains(pid)) return;
            if (hookRetry.TryGetValue(pid, out var at) && DateTime.UtcNow < at) return;
            string? problem = FpsHook.Inject(pid, dataDir);
            if (problem == null) { hooked.Add(pid); hookState[rule.Exe] = ""; }
            else { hookRetry[pid] = DateTime.UtcNow.AddSeconds(30); hookState[rule.Exe] = problem; }
        }

        try
        {
            mutex = new Mutex(true, MutexName, out bool first);
            if (!first) return;                                  // (another guard is running: this one is not needed)
            owned = true;
            _heldMutex = mutex;
            bool elevated = MemoryCleaner.IsElevated;
            var cooldown = new Dictionary<int, DateTime>();
            var trims = new Dictionary<string, (int Count, DateTime Last)>(StringComparer.OrdinalIgnoreCase);
            DateTime stamp = DateTime.MinValue;
            var rules = new List<MemoryRule>();
            int idle = 0, trayMissing = 0;
            while (!_stop)
            {
                // (the tray may be restarting: it has to be gone for three checks in a row, about 15 s, before the guard ends)
                if (ownerIsMutex) { if (!TrayAlive()) { if (++trayMissing >= 3) break; } else trayMissing = 0; }
                try
                {
                    var t = File.GetLastWriteTimeUtc(MemoryLimits.FileIn(dataDir));
                    if (t != stamp) { stamp = t; rules = MemoryLimits.Load(dataDir); }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                var active = rules.Where(r => r.Enabled).ToList();
                if (active.Count == 0) { if (++idle > 12) break; } else idle = 0;

                // frames per second: while the window is open (it touches a file every few seconds) or while a row has a frame-rate limit; otherwise nothing runs
                bool wantFps = active.Any(r => r.TargetFps > 0);
                try { if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(MemoryLimits.FpsWantedIn(dataDir))).TotalSeconds < 8) wantFps = true; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                var fpsAll = new Dictionary<int, int>();
                if (wantFps)
                {
                    if (!fps.Running && (DateTime.UtcNow - fpsTried).TotalSeconds > 30) { fpsTried = DateTime.UtcNow; fps.Start(); }
                    if (fps.Running) fpsAll = fps.Snapshot();
                }
                else if (fps.Running) fps.Stop();

                wantedNow.Clear();
                var rows = new List<GuardRow>();
                var seen = new HashSet<int>();
                bool freedAll = false;
                // processor settings that no longer match a rule (rule removed, switched off or changed) are taken back
                foreach (int pid in cpuDone.Keys.ToList())
                {
                    var d = cpuDone[pid];
                    var rule = active.FirstOrDefault(r => r.Exe.Equals(d.Exe, StringComparison.OrdinalIgnoreCase));
                    if (rule != null && EffMode(rule) == d.Mode && EffCap(rule) == d.Cap) continue;
                    if (d.Result != null) { try { CpuLimiter.Revert(pid, d.Result); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { } }
                    cpuDone.Remove(pid);
                }
                foreach (var rule in active)
                {
                    string name = Path.GetFileNameWithoutExtension(rule.Exe);
                    long biggest = 0;
                    Process[] procs;
                    try { procs = Process.GetProcessesByName(name); }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { continue; }
                    int front = ForegroundPid();
                    if (front != 0 && procs.Any(x => x.Id == front)) lastFront[rule.Exe] = front;
                    lastFront.TryGetValue(rule.Exe, out int frontPid);
                    foreach (var p in procs)
                    {
                        try
                        {
                            p.Refresh();
                            seen.Add(p.Id);
                            if (!rule.Matches(p.MainWindowTitle ?? "", p.Id, frontPid))
                            {
                                // (a copy that was limited and is now the one in front / the main one: set free again)
                                if (cpuDone.TryGetValue(p.Id, out var old) && old.Exe.Equals(rule.Exe, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (old.Result != null) { try { CpuLimiter.Revert(p.Id, old.Result); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { } }
                                    cpuDone.Remove(p.Id);
                                }
                                if (limitSet.TryGetValue(p.Id, out int wasLimited) && wasLimited > 0) HookCopy(rule, p.Id, 0);       // (and its frame limit lifted)
                                continue;
                            }
                            long ws = p.WorkingSet64;
                            biggest = Math.Max(biggest, ws);
                            if (UsesHook(rule) || (rule.ShowFps && FpsHook.Available)) HookCopy(rule, p.Id, UsesHook(rule) ? rule.TargetFps : 0);
                            // the processor: once per program (again when it was restarted: a new process id)
                            var mode = EffMode(rule);
                            bool canApply = rule.TargetFps <= 0 || UsesHook(rule) || fps.Running;          // (a frame-rate limit without frame counting would only be a blind cap: not applied)
                            if (mode != CpuMode.None && canApply && !cpuDone.ContainsKey(p.Id))
                                cpuDone[p.Id] = (rule.Exe, mode, EffCap(rule), CpuLimiter.Apply(p.Id, mode, rule.TargetFps > 0 ? 100 : rule.CapPercent));
                            // frame-rate limit: aim the cap at (what it really uses now) x (target / frames now)
                            if (rule.TargetFps > 0 && !UsesHook(rule) && fps.Running && cpuDone.TryGetValue(p.Id, out var fd) && fd.Result != null && fd.Result.Job != IntPtr.Zero)
                            {
                                var cpuNow = p.TotalProcessorTime; var at = DateTime.UtcNow;
                                if (cpuSeen.TryGetValue(p.Id, out var prev) && (at - prev.At).TotalSeconds > 0.5 && fpsAll.TryGetValue(p.Id, out int nowFps) && nowFps > 0
                                    && (!cpuAdjusted.TryGetValue(p.Id, out var lastAdj) || (at - lastAdj).TotalSeconds >= 3.5))        // (let the last change show in the numbers first)
                                {
                                    double usage = 100.0 * (cpuNow - prev.Cpu).TotalSeconds / (at - prev.At).TotalSeconds / Environment.ProcessorCount;
                                    // (two readings are averaged: a frame count over a second or so jumps about at these small caps)
                                    double smooth = fpsPrev.TryGetValue(p.Id, out double pf) ? (pf + nowFps) / 2 : nowFps;
                                    fpsPrev[p.Id] = nowFps;
                                    double ratio = rule.TargetFps / Math.Max(1.0, smooth);
                                    if (Math.Abs(ratio - 1) > 0.06)
                                    {
                                        double rate = fd.Result.Rate, next;
                                        if (ratio > 1) next = rate * Math.Clamp(Math.Pow(ratio, 0.5), 1.05, 1.25);         // too slow: let it have a little more
                                        else
                                        {
                                            // too fast: a program that does not even use its cap is not held by it, so start from what it really uses
                                            double basis = usage < rate * 0.8 ? Math.Max(usage, 0.05) : rate;
                                            next = basis * Math.Clamp(Math.Pow(ratio, 0.6), 0.6, 0.95);
                                        }
                                        CpuLimiter.SetRate(fd.Result, Math.Clamp(next, 0.3, 100));
                                        cpuAdjusted[p.Id] = at;
                                    }
                                }
                                cpuSeen[p.Id] = (cpuNow, at);
                            }
                            if (rule.LimitMb <= 0 || ws <= rule.LimitMb * 1024L * 1024L) continue;
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
                    rows.Add(new GuardRow
                    {
                        Exe = rule.Exe, WorkingMb = biggest / (1024 * 1024), Trims = tr.Count, LastTrim = tr.Count > 0 ? tr.Last.ToString("HH:mm:ss") : "",
                        CpuApplied = cpuDone.Values.Any(v => v.Exe.Equals(rule.Exe, StringComparison.OrdinalIgnoreCase) && v.Result != null),
                        HookState = (rule.TargetFps > 0 || rule.ShowFps) && FpsHook.Available ? (hookState.TryGetValue(rule.Exe, out var hs) ? hs : "") : "",
                    });
                }
                // frame limits that no longer apply (row switched off or cleared, copy not matched any more) are lifted; closed programs are forgotten
                foreach (var (pid, had) in limitSet.ToList())
                    if (had > 0 && !wantedNow.ContainsKey(pid)) { FpsHook.SetLimit(dataDir, pid, 0); limitSet[pid] = 0; }
                if ((DateTime.UtcNow - hookCleaned).TotalSeconds > 60)
                {
                    hookCleaned = DateTime.UtcNow;
                    FpsHook.Cleanup(dataDir);
                    var running = new HashSet<int>();
                    foreach (var q in Process.GetProcesses()) { running.Add(q.Id); q.Dispose(); }
                    foreach (int pid in hooked.Concat(limitSet.Keys).Where(x => !running.Contains(x)).ToList()) { hooked.Remove(pid); limitSet.Remove(pid); hookRetry.Remove(pid); }
                }
                foreach (int dead in cooldown.Keys.Where(k => (DateTime.UtcNow - cooldown[k]).TotalMinutes > 10).ToList()) cooldown.Remove(dead);
                // programs that closed: forget them (their job object is closed)
                foreach (int pid in cpuDone.Keys.ToList())
                {
                    bool alive;
                    try { using var q = Process.GetProcessById(pid); alive = !q.HasExited; }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { alive = false; }
                    if (alive) continue;
                    if (cpuDone[pid].Result is { } done && done.Job != IntPtr.Zero) { try { CpuLimiter.Revert(pid, done); } catch (Exception e) when (e is System.ComponentModel.Win32Exception) { } }
                    cpuDone.Remove(pid);
                }
                var fpsNow = new Dictionary<int, int>();
                foreach (var (pid, n) in fpsAll) if (seen.Contains(pid)) fpsNow[pid] = n;
                foreach (int gone in cpuSeen.Keys.Where(k => !seen.Contains(k)).ToList()) cpuSeen.Remove(gone);
                try
                {
                    File.WriteAllText(MemoryLimits.StatusFileIn(dataDir), System.Text.Json.JsonSerializer.Serialize(new GuardStatus { Elevated = elevated, Updated = DateTime.Now, Rows = rows, Fps = fpsNow, FpsState = wantFps ? (fps.Running ? "" : fps.Problem) : "" }));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                bool wantFast = fps.Running || active.Any(r => r.Scope == 3 && (r.Cpu != 0 || r.TargetFps > 0));
                int ticks = wantFast ? 10 : 25;                                      // 2 s while a row follows the window in front or frames are counted, else 5 s
                for (int i = 0; i < ticks && !_stop; i++) Thread.Sleep(200);        // (quick to stop)
            }
        }
        finally
        {
            // the guard ends: what it set on programs is taken back (a cap would otherwise stay on a game that keeps running)
            try { foreach (var (pid, d) in cpuDone) if (d.Result != null) CpuLimiter.Revert(pid, d.Result); } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            try { foreach (var (pid, had) in limitSet) if (had > 0) FpsHook.SetLimit(dataDir, pid, 0); } catch (InvalidOperationException) { }       // (the frame limits are lifted too)
            try { if (owned) mutex?.ReleaseMutex(); } catch (Exception e) when (e is ApplicationException or ObjectDisposedException) { }
            try { mutex?.Dispose(); } catch (ObjectDisposedException) { }
            if (owned) _heldMutex = null;
        }
    }
}
