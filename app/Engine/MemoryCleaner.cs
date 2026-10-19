using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

namespace IdmClone.Engine;

/// <summary>
/// "Free up memory" (the desktop right-click entry): asks the biggest programs that Windows lets us reach to give back the memory they are not using
/// right now (the working set is trimmed: the pages go to Windows' standby list and come back by themselves when the program needs them). Nothing is
/// closed and nothing is lost.
///
/// Why it is careful: every page taken from a program has to be WRITTEN to the page file first (the "System" process is busy for a moment - that is the
/// disk activity people see) and READ back when the program needs it. So it only trims until a part of the memory is free (65 %; "go further": 80 %),
/// does nothing when that is already so, takes the biggest programs first (the fewest give the most), one after the other, waiting for the page file
/// to calm down in between. It no longer asks Windows to empty every program's memory at once (that was the "deep" step that made the disk 100 % busy)
/// and it does not flush the modified page list or purge the standby list. "Going further" (<paramref name="deep"/>, needs administrator rights) also
/// reaches programs that run as administrator and trims smaller ones.
/// </summary>
public static class MemoryCleaner
{
    public sealed record Result(long FreeBefore, long FreeAfter, long Total, int Trimmed, bool Deep, bool DeepFailed, bool AlreadyFine = false);

    public static bool IsElevated
    {
        get
        {
            try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
            catch (Exception) { return false; }
        }
    }

    public static (long Free, long Total) Physical()
    {
        var m = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref m) ? ((long)m.AvailPhys, (long)m.TotalPhys) : (0, 0);
    }

    public static Result Free(bool deep)
    {
        var (before, total) = Physical();
        double fraction = deep ? 0.80 : 0.65;
        if (double.TryParse(Environment.GetEnvironmentVariable("UTYLIX_FREE_TARGET"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double forTests) && forTests > 0 && forTests < 1) fraction = forTests;
        long target = (long)(total * fraction);
        if (before >= target) return new Result(before, before, total, 0, false, false, true);       // already fine: every program is left alone
        long minimum = deep ? MinimumDeep : MinimumToTrim;
        int trimmed = 0;
        uint foreground = ForegroundProcessId();
        int self = Environment.ProcessId;
        var candidates = new List<(int Id, long Ws)>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                int id = p.Id;
                if (id is 0 or 4 || id == self || (uint)id == foreground) continue;       // (what you are looking at stays snappy)
                string name = p.ProcessName;
                if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase) || name.Equals("dwm", StringComparison.OrdinalIgnoreCase)) continue;
                long ws = p.WorkingSet64;                                                  // (small programs give back next to nothing and would only be read back from the disk)
                if (ws >= minimum) candidates.Add((id, ws));
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { /* gone, or not ours */ }
            finally { p.Dispose(); }
        }
        PerformanceCounter? pagesOut = null;
        try { pagesOut = new PerformanceCounter("Memory", "Pages Output/sec", true); pagesOut.NextValue(); }
        catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { pagesOut = null; }
        try
        {
            foreach (var (id, _) in candidates.OrderByDescending(c => c.Ws))              // the biggest first: the fewest programs give the most
            {
                IntPtr h = OpenProcess(ProcessSetQuota | ProcessQueryInformation, false, id);
                if (h == IntPtr.Zero) continue;
                try { if (EmptyWorkingSet(h)) trimmed++; }
                finally { CloseHandle(h); }
                if (Physical().Free >= target) break;                                     // enough
                WaitForPageFile(pagesOut);                                                // (one program after the other: the page file gets time to write)
            }
        }
        finally { pagesOut?.Dispose(); }
        Thread.Sleep(250);                                                                 // (let the numbers settle)
        var (after, _) = Physical();
        return new Result(before, after, total, trimmed, deep, false, false);
    }

    /// <summary>Waits (at most a few seconds) until Windows has written out most of what was just trimmed.</summary>
    private static void WaitForPageFile(PerformanceCounter? pagesOut)
    {
        Thread.Sleep(300);
        if (pagesOut == null) return;
        try
        {
            for (int i = 0; i < 16; i++)
            {
                if (pagesOut.NextValue() < 150) return;                                    // (pages written per second: calm)
                Thread.Sleep(250);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Trims the working set of one program. False when Windows does not let this copy reach it (it runs as administrator, or is protected).</summary>
    public static bool TrimProcess(int pid)
    {
        IntPtr h = OpenProcess(ProcessSetQuota | ProcessQueryInformation, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return EmptyWorkingSet(h); }
        finally { CloseHandle(h); }
    }

    private static uint ForegroundProcessId()
    {
        try { GetWindowThreadProcessId(GetForegroundWindow(), out uint pid); return pid; }
        catch (Exception) { return 0; }
    }

    public static string Bytes(long b) => b >= 1L << 30 ? (b / (double)(1L << 30)).ToString("0.0") + " GB" : (b / (double)(1L << 20)).ToString("0") + " MB";

    private const uint ProcessSetQuota = 0x0100, ProcessQueryInformation = 0x0400;
    private const long MinimumToTrim = 100L * 1024 * 1024;                  // programs holding less than this are left alone
    private const long MinimumDeep = 40L * 1024 * 1024;                     // ... when going further

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool EmptyWorkingSet(IntPtr process);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
}
