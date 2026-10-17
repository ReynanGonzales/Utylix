using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace IdmClone.Engine;

/// <summary>What the full on-screen display shows. A value that this PC cannot give (no driver access for the processor temperature, no such sensor) is null: "--".</summary>
public sealed class OsdReading
{
    public double? GpuTemp, GpuLoad, GpuClock, GpuPower, GpuFan;
    public double? VramUsedMb, VramClock, VramGameMb;
    public double? CpuTemp, CpuLoad, CpuClock, CpuPower;
    public double RamUsedMb, RamGameMb;
}

/// <summary>
/// Reads the sensors for the on-screen display with LibreHardwareMonitor (the library Utylix's fan control already uses): processor, graphics card. It is only
/// opened while a display is on screen and closed again soon after (<see cref="Close"/>), so nothing stays loaded. Without administrator rights the processor's
/// temperature, clock and power are not available (they need a driver); the load, the graphics card and the memory are.
/// </summary>
public sealed class OsdSensors : IDisposable
{
    private Computer? _pc;
    private bool _failed;
    private DateTime _gameVramAt = DateTime.MinValue;
    private double? _gameVram;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX { public uint Length, Load; public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExt; }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX s);

    public bool IsOpen => _pc != null;

    private void Open()
    {
        if (_pc != null || _failed) return;
        try
        {
            _pc = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
            _pc.Open();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _failed = true;
            try { _pc?.Close(); } catch (Exception) { }
            _pc = null;
        }
    }

    public void Close()
    {
        try { _pc?.Close(); } catch (Exception) { }
        _pc = null; _failed = false;
        _perf?.Dispose(); _perf = null; _perfFailed = false;
    }

    public void Dispose() => Close();

    private static IEnumerable<IHardware> All(IEnumerable<IHardware> list)
    {
        foreach (var h in list) { yield return h; foreach (var s in All(h.SubHardware)) yield return s; }
    }

    private static double? Pick(IHardware h, SensorType type, params string[] names)
    {
        var sensors = h.Sensors.Where(s => s.SensorType == type && s.Value is float f && !float.IsNaN(f)).ToList();
        foreach (string n in names)
        {
            var hit = sensors.FirstOrDefault(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (hit?.Value is float v) return v;
        }
        return sensors.Count > 0 && names.Length == 0 ? sensors[0].Value : null;
    }

    /// <summary>The newest values. <paramref name="gamePid"/> is the program the display is for (its own memory use is shown).</summary>
    public OsdReading Read(int gamePid)
    {
        Open();
        var r = new OsdReading();
        var ms = new MEMORYSTATUSEX { Length = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref ms)) r.RamUsedMb = (ms.TotalPhys - ms.AvailPhys) / 1048576.0;
        try { using var p = Process.GetProcessById(gamePid); r.RamGameMb = p.WorkingSet64 / 1048576.0; } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { }
        if (_pc == null) return r;
        try
        {
            IHardware? gpu = null;
            foreach (var h in All(_pc.Hardware))
            {
                h.Update();
                if (h.HardwareType == HardwareType.Cpu)
                {
                    r.CpuTemp ??= Pick(h, SensorType.Temperature, "CPU Package", "Core (Tctl/Tdie)", "Core Max", "CPU Die (average)", "Core Average");
                    r.CpuLoad ??= Pick(h, SensorType.Load, "CPU Total");
                    r.CpuPower ??= Pick(h, SensorType.Power, "CPU Package", "Package");
                    var clocks = h.Sensors.Where(s => s.SensorType == SensorType.Clock && s.Name.StartsWith("CPU Core", StringComparison.OrdinalIgnoreCase) && s.Value is float f && f > 0).Select(s => (double)s.Value!.Value).ToList();
                    if (clocks.Count > 0) r.CpuClock ??= clocks.Average();
                }
                else if (h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                {
                    // the card that does the work: a graphics card beats the processor's built-in graphics
                    bool better = gpu == null || (gpu.HardwareType == HardwareType.GpuIntel && h.HardwareType != HardwareType.GpuIntel);
                    if (better) gpu = h;
                }
            }
            if (gpu != null)
            {
                r.GpuTemp = Pick(gpu, SensorType.Temperature, "GPU Core", "GPU Hot Spot");
                r.GpuLoad = Pick(gpu, SensorType.Load, "GPU Core");
                r.GpuClock = Pick(gpu, SensorType.Clock, "GPU Core");
                r.GpuPower = Pick(gpu, SensorType.Power, "GPU Package", "GPU Power", "GPU Core");
                r.GpuFan = Pick(gpu, SensorType.Control, "GPU Fan") ?? Pick(gpu, SensorType.Load, "GPU Fan");
                r.VramUsedMb = Pick(gpu, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
                r.VramClock = Pick(gpu, SensorType.Clock, "GPU Memory");
            }
        }
        catch (Exception e) when (e is InvalidOperationException or NullReferenceException or ArgumentException) { }
        // (a driver-less read gives 0 for what it cannot measure)
        if (r.CpuTemp is <= 0) r.CpuTemp = null;
        if (r.GpuTemp is <= 0) r.GpuTemp = null;
        if (r.CpuClock is null or <= 0) r.CpuClock = EstimatedCpuClock();
        r.VramGameMb = GameVram(gamePid);
        return r;
    }

    // The processor clock without a driver: Windows' own "% Processor Performance" (as Task Manager shows the speed) times the base speed.
    private PerformanceCounter? _perf;
    private bool _perfFailed;
    private double _baseMhz;

    private double? EstimatedCpuClock()
    {
        if (_perfFailed) return null;
        try
        {
            if (_perf == null)
            {
                _baseMhz = Convert.ToDouble(Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz", 0) ?? 0);
                _perf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", true);
                _perf.NextValue();                                                // (the first reading is always 0)
                return null;
            }
            double v = _perf.NextValue();
            return v > 0 && _baseMhz > 0 ? v / 100.0 * _baseMhz : null;
        }
        catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or FormatException)
        {
            _perfFailed = true;
            return null;
        }
    }

    /// <summary>The graphics memory of the program itself (Windows' own "GPU Process Memory" counters, as Task Manager shows it), looked at every few seconds.</summary>
    private double? GameVram(int pid)
    {
        if ((DateTime.UtcNow - _gameVramAt).TotalSeconds < 3) return _gameVram;
        _gameVramAt = DateTime.UtcNow;
        try
        {
            var cat = new PerformanceCounterCategory("GPU Process Memory");
            double total = 0; bool any = false;
            foreach (string inst in cat.GetInstanceNames().Where(n => n.StartsWith($"pid_{pid}_", StringComparison.OrdinalIgnoreCase)))
            {
                using var c = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", inst, true);
                total += c.NextValue(); any = true;
            }
            _gameVram = any ? total / 1048576.0 : null;
        }
        catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { _gameVram = null; }
        return _gameVram;
    }
}
