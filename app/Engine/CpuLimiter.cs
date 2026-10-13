using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace IdmClone.Engine;

/// <summary>What to do about a program's processor use (see <see cref="CpuLimiter"/>).</summary>
public enum CpuMode { None = 0, LowerPriority = 1, Efficiency = 2, Cap = 3 }

/// <summary>
/// Reducing the processor use of ONE running program, three ways that Windows itself supports:
/// lower priority (everything else gets the processor first), efficiency mode (idle priority + "EcoQoS": the program is moved to the efficient /
/// slower side of the CPU) and a hard cap (a job object limits the program to a share of the whole CPU, for example 25 %).
/// Nothing is closed and nothing is changed in the program. The settings go away when the program closes (or are taken back by <see cref="Revert"/>).
/// </summary>
public static class CpuLimiter
{
    /// <summary>What was applied to one process (so it can be taken back).</summary>
    public sealed class Applied
    {
        public CpuMode Mode; public int Cap; public IntPtr Job;
    }

    private const uint ProcessSetInformation = 0x0200, ProcessSetQuota = 0x0100, ProcessTerminate = 0x0001, ProcessQueryLimited = 0x1000;
    private const uint BelowNormal = 0x4000, Idle = 0x40, Normal = 0x20;

    /// <summary>Applies <paramref name="mode"/> to the process. Null when Windows does not let this copy reach it.</summary>
    public static Applied? Apply(int pid, CpuMode mode, int capPercent)
    {
        if (mode == CpuMode.None) return null;
        IntPtr h = OpenProcess(ProcessSetInformation | ProcessSetQuota | ProcessTerminate | ProcessQueryLimited, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var a = new Applied { Mode = mode, Cap = capPercent };
            bool ok;
            switch (mode)
            {
                case CpuMode.LowerPriority:
                    ok = SetPriorityClass(h, BelowNormal);
                    break;
                case CpuMode.Efficiency:
                    ok = SetPriorityClass(h, Idle);
                    SetThrottling(h, true);
                    break;
                default:
                    ok = CapJob(h, Math.Clamp(capPercent, 1, 100), a);
                    break;
            }
            return ok ? a : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Takes the change back (priority normal, efficiency mode off, cap removed) as far as Windows allows for a process in a job.</summary>
    public static void Revert(int pid, Applied a)
    {
        IntPtr h = OpenProcess(ProcessSetInformation | ProcessSetQuota | ProcessQueryLimited, false, pid);
        try
        {
            if (h != IntPtr.Zero)
            {
                if (a.Mode is CpuMode.LowerPriority or CpuMode.Efficiency) SetPriorityClass(h, Normal);
                if (a.Mode == CpuMode.Efficiency) SetThrottling(h, false);
            }
            if (a.Job != IntPtr.Zero)
            {
                var info = new CpuRateInfo { ControlFlags = 0, CpuRate = 0 };         // not enabled = no limit
                SetInformationJobObject(a.Job, 15, ref info, Marshal.SizeOf<CpuRateInfo>());
                CloseHandle(a.Job);
                a.Job = IntPtr.Zero;
            }
        }
        finally { if (h != IntPtr.Zero) CloseHandle(h); }
    }

    private static void SetThrottling(IntPtr process, bool on)
    {
        var state = new PowerThrottling { Version = 1, ControlMask = 1 /* execution speed */, StateMask = on ? 1u : 0u };
        SetProcessInformation(process, 4 /* ProcessPowerThrottling */, ref state, Marshal.SizeOf<PowerThrottling>());
    }

    private static bool CapJob(IntPtr process, int percent, Applied a)
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return false;
        var info = new CpuRateInfo { ControlFlags = 0x1 | 0x4 /* enable + hard cap */, CpuRate = (uint)(percent * 100) };
        if (!SetInformationJobObject(job, 15 /* JobObjectCpuRateControlInformation */, ref info, Marshal.SizeOf<CpuRateInfo>())
            || !AssignProcessToJobObject(job, process))
        {
            CloseHandle(job);
            return false;
        }
        a.Job = job;                                      // (kept open while the guard runs)
        return true;
    }

    [StructLayout(LayoutKind.Sequential)] private struct CpuRateInfo { public uint ControlFlags; public uint CpuRate; }
    [StructLayout(LayoutKind.Sequential)] private struct PowerThrottling { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetPriorityClass(IntPtr process, uint priority);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessInformation(IntPtr process, int cls, ref PowerThrottling info, int size);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int cls, ref CpuRateInfo info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
