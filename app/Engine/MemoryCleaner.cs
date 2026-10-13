using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace IdmClone.Engine;

/// <summary>
/// "Free up memory" (the desktop right-click entry): asks every program that Windows lets us reach to give back the memory it is not using right
/// now (its working set is trimmed: the pages go to Windows' standby list and come back by themselves when a program needs them), and, when this
/// copy runs with administrator rights, also empties Windows' own standby and modified page lists. Nothing is closed and nothing is lost.
/// </summary>
public static class MemoryCleaner
{
    public sealed record Result(long FreeBefore, long FreeAfter, long Total, int Trimmed, bool Deep, bool DeepFailed);

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

    /// <summary>Trims the working sets (and with <paramref name="deep"/> and administrator rights, Windows' standby / modified lists too).</summary>
    public static Result Free(bool deep)
    {
        var (before, total) = Physical();
        int trimmed = 0;
        uint foreground = ForegroundProcessId();
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                int id = p.Id;
                if (id is 0 or 4 || id == self || (uint)id == foreground) continue;       // (what you are looking at stays snappy)
                string name = p.ProcessName;
                if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase) || name.Equals("dwm", StringComparison.OrdinalIgnoreCase)) continue;
                IntPtr h = OpenProcess(ProcessSetQuota | ProcessQueryInformation, false, id);
                if (h == IntPtr.Zero) continue;
                try { if (EmptyWorkingSet(h)) trimmed++; }
                finally { CloseHandle(h); }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { /* gone, or not ours */ }
            finally { p.Dispose(); }
        }
        bool deepDone = false, deepFailed = false;
        if (deep)
        {
            if (IsElevated) { deepDone = PurgeSystemLists(); deepFailed = !deepDone; }
            else deepFailed = true;
        }
        System.Threading.Thread.Sleep(250);                                // (let the numbers settle)
        var (after, _) = Physical();
        return new Result(before, after, total, trimmed, deepDone, deepFailed);
    }

    private static bool PurgeSystemLists()
    {
        try
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x20 | 0x08, out IntPtr token)) return false;
            try
            {
                if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out long luid)) return false;
                var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
                if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            }
            finally { CloseHandle(token); }
            bool ok = true;
            foreach (int command in new[] { 2 /* empty working sets */, 3 /* flush the modified list */, 4 /* purge the standby list */ })
            {
                IntPtr buffer = Marshal.AllocHGlobal(4);
                try
                {
                    Marshal.WriteInt32(buffer, command);
                    if (NtSetSystemInformation(80 /* SystemMemoryListInformation */, buffer, 4) != 0) ok = false;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return ok;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    private static uint ForegroundProcessId()
    {
        try { GetWindowThreadProcessId(GetForegroundWindow(), out uint pid); return pid; }
        catch (Exception) { return 0; }
    }

    public static string Bytes(long b) => b >= 1L << 30 ? (b / (double)(1L << 30)).ToString("0.0") + " GB" : (b / (double)(1L << 20)).ToString("0") + " MB";

    private const uint ProcessSetQuota = 0x0100, ProcessQueryInformation = 0x0400;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TokenPrivileges { public uint Count; public long Luid; public uint Attributes; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool EmptyWorkingSet(IntPtr process);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, int length, IntPtr previous, IntPtr returnLength);
    [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int informationClass, IntPtr information, int length);
}
