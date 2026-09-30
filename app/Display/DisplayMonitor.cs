using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace IdmClone;

/// <summary>How a monitor's brightness can be changed.</summary>
public enum BrightnessMode
{
    /// <summary>The monitor does not answer (DDC/CI switched off in its own menu, or a TV / adapter that does not pass it on).</summary>
    None,
    /// <summary>Over the video cable (DDC/CI): external monitors.</summary>
    Ddc,
    /// <summary>Windows' own control of a laptop's built-in screen.</summary>
    Wmi,
}

/// <summary>One screen whose brightness can be read and changed (the same idea as the free "Monitorian" program).</summary>
public sealed class DisplayMonitor
{
    private IntPtr _handle;                 // DDC: physical monitor handle
    private string? _wmiInstance;           // WMI: the built-in panel's instance name
    private uint _min, _max = 100;
    private int _wanted = -1;
    private int _running;

    public string Name { get; private set; } = "";
    public string Device { get; private set; } = "";
    public bool Primary { get; private set; }
    public BrightnessMode Mode { get; private set; }
    /// <summary>0 - 100, as last read from (or sent to) the monitor.</summary>
    public int Percent { get; private set; }
    public event Action<string>? Failed;

    /// <summary>Changes the brightness (0 - 100). Fast to call again and again while a slider moves: only the newest value is sent.</summary>
    public void SetPercent(int percent)
    {
        if (Mode == BrightnessMode.None) return;
        Percent = Math.Clamp(percent, 0, 100);
        Interlocked.Exchange(ref _wanted, Percent);
        Kick();
    }

    private void Kick()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;         // a sender is already busy: it will pick up the new value
        _ = Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    int value = Interlocked.Exchange(ref _wanted, -1);
                    if (value < 0) break;
                    Send(value);
                }
            }
            catch (Exception e) when (e is ManagementException or COMException or InvalidOperationException)
            {
                Failed?.Invoke(e.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
            if (Volatile.Read(ref _wanted) >= 0) Kick();                           // a value arrived while we were finishing
        });
    }

    private void Send(int percent)
    {
        if (Mode == BrightnessMode.Ddc)
        {
            uint raw = _min + (uint)Math.Round((_max - _min) * percent / 100.0);
            for (int attempt = 0; attempt < 3; attempt++)                           // monitors sometimes miss a command
            {
                if (SetMonitorBrightness(_handle, raw)) return;
                Thread.Sleep(40);
            }
            throw new InvalidOperationException("The monitor did not accept the brightness change.");
        }
        if (Mode == BrightnessMode.Wmi && _wmiInstance != null)
        {
            using var search = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject o in search.Get())
                using (o)
                    if (string.Equals((string)o["InstanceName"], _wmiInstance, StringComparison.OrdinalIgnoreCase))
                    {
                        o.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)percent });
                        return;
                    }
            throw new InvalidOperationException("Windows did not find this screen's brightness control.");
        }
    }

    /// <summary>Reads the brightness again (the person may have used the monitor's own buttons).</summary>
    public void Refresh()
    {
        if (Mode == BrightnessMode.Ddc && GetMonitorBrightness(_handle, out uint min, out uint cur, out uint max) && max > min)
        {
            _min = min; _max = max;
            Percent = (int)Math.Round((cur - min) * 100.0 / (max - min));
        }
    }

    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Looks at every screen. Slow (a monitor can take a while to answer), so call it from a background thread.</summary>
    public static List<DisplayMonitor> Enumerate()
    {
        var list = new List<DisplayMonitor>();
        var targets = TargetNames();
        var wmi = WmiPanels();

        var handles = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref RECT r, IntPtr data) => { handles.Add(h); return true; }, IntPtr.Zero);

        foreach (var hm in handles)
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(hm, ref info)) continue;
            targets.TryGetValue(info.szDevice, out var target);
            bool primary = (info.dwFlags & 1) != 0;

            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hm, out uint count) || count == 0) count = 0;
            var physical = new PHYSICAL_MONITOR[count];
            if (count > 0 && !GetPhysicalMonitorsFromHMONITOR(hm, count, physical)) physical = Array.Empty<PHYSICAL_MONITOR>();

            string name = !string.IsNullOrWhiteSpace(target.Name) ? target.Name : "";
            // a laptop's own screen: Windows controls it directly
            string? wmiName = target.Path != null ? wmi.Keys.FirstOrDefault(k => SamePanel(k, target.Path)) : null;
            if (wmiName == null && target.Internal && wmi.Count == 1) wmiName = wmi.Keys.First();
            if (wmiName != null)
            {
                var m = new DisplayMonitor
                {
                    Name = name.Length > 0 ? name : "Built-in screen", Device = info.szDevice, Primary = primary,
                    Mode = BrightnessMode.Wmi, _wmiInstance = wmiName, Percent = wmi[wmiName],
                };
                list.Add(m);
                foreach (var p in physical) DestroyPhysicalMonitor(p.Handle);
                continue;
            }

            if (physical.Length == 0)
            {
                list.Add(new DisplayMonitor { Name = name.Length > 0 ? name : "Screen", Device = info.szDevice, Primary = primary, Mode = BrightnessMode.None });
                continue;
            }
            for (int i = 0; i < physical.Length; i++)
            {
                var p = physical[i];
                string display = name.Length > 0 ? name : (string.IsNullOrWhiteSpace(p.Description) ? "Screen" : p.Description);
                if (physical.Length > 1) display += " (" + (i + 1) + ")";
                var m = new DisplayMonitor { Name = display, Device = info.szDevice, Primary = primary, _handle = p.Handle };
                for (int attempt = 0; attempt < 3 && m.Mode == BrightnessMode.None; attempt++)
                {
                    if (GetMonitorBrightness(p.Handle, out uint min, out uint cur, out uint max) && max > min)
                    {
                        m._min = min; m._max = max; m.Mode = BrightnessMode.Ddc;
                        m.Percent = (int)Math.Round((cur - min) * 100.0 / (max - min));
                    }
                    else Thread.Sleep(60);
                }
                if (m.Mode == BrightnessMode.None) DestroyPhysicalMonitor(p.Handle);
                list.Add(m);
            }
        }
        return list.OrderByDescending(m => m.Primary).ThenBy(m => m.Device, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Lets go of the monitor handles.</summary>
    public static void Release(IEnumerable<DisplayMonitor> monitors)
    {
        foreach (var m in monitors)
            if (m._handle != IntPtr.Zero) { DestroyPhysicalMonitor(m._handle); m._handle = IntPtr.Zero; }
    }

    /// <summary>"DISPLAY\BOE0747\4&amp;abc&amp;0&amp;UID8388688_0" (WMI) and "\\?\DISPLAY#BOE0747#4&amp;abc&amp;0&amp;UID8388688#{guid}" (display path) are the same panel.</summary>
    private static bool SamePanel(string wmiInstance, string devicePath)
    {
        string a = wmiInstance;
        int us = a.LastIndexOf('_');
        if (us > 0 && us >= a.Length - 3) a = a[..us];
        string b = devicePath;
        int start = b.IndexOf("DISPLAY#", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return false;
        b = b[start..];
        int end = b.IndexOf("#{", StringComparison.Ordinal);
        if (end > 0) b = b[..end];
        return string.Equals(a, b.Replace('#', '\\'), StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, int> WmiPanels()
    {
        var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var search = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightness");
            foreach (ManagementObject o in search.Get())
                using (o)
                    found[(string)o["InstanceName"]] = Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException) { /* a desktop PC has none */ }
        return found;
    }

    private readonly record struct Target(string? Name, string? Path, bool Internal);

    /// <summary>Windows' own list of the connected screens: friendly name, device path and whether it is a built-in panel, by "\\.\DISPLAYn".</summary>
    private static Dictionary<string, Target> TargetNames()
    {
        var result = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(2, out uint pathCount, out uint modeCount) != 0) return result;      // QDC_ONLY_ACTIVE_PATHS
            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(2, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return result;
            for (int i = 0; i < pathCount; i++)
            {
                var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME { header = Header(1, Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(), paths[i].sourceAdapter, paths[i].sourceId) };
                var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME { header = Header(2, Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(), paths[i].targetAdapter, paths[i].targetId) };
                if (DisplayConfigGetDeviceInfo(ref src) != 0) continue;
                string? friendly = null, devicePath = null;
                if (DisplayConfigGetDeviceInfo(ref tgt) == 0) { friendly = tgt.monitorFriendlyDeviceName; devicePath = tgt.monitorDevicePath; }
                uint tech = paths[i].outputTechnology;
                bool internalPanel = tech == 0x80000000 || tech == 11 || tech == 13;      // INTERNAL, DISPLAYPORT_EMBEDDED, UDI_EMBEDDED
                if (friendly is { Length: 0 }) friendly = internalPanel ? "Built-in screen" : null;
                if (!result.ContainsKey(src.viewGdiDeviceName)) result[src.viewGdiDeviceName] = new Target(friendly, devicePath, internalPanel);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException) { /* just no friendly names */ }
        return result;
    }

    private static DISPLAYCONFIG_DEVICE_INFO_HEADER Header(uint type, int size, LUID adapter, uint id) =>
        new() { type = type, size = (uint)size, adapterId = adapter, id = id };

    // ---------------------------------------------------- Windows API ----------------------------------------------------
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }
    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type, size; public LUID adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public LUID sourceAdapter; public uint sourceId, sourceModeIdx, sourceStatus;
        public LUID targetAdapter; public uint targetId, targetModeIdx, outputTechnology, rotation, scaling, refreshNum, refreshDen, scanLine;
        public int targetAvailable; public uint targetStatus;
        public uint flags;
    }
    [StructLayout(LayoutKind.Sequential, Size = 64)] private struct DISPLAYCONFIG_MODE_INFO { public uint infoType, id; public LUID adapterId; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags, outputTechnology; public ushort edidManufactureId, edidProductCodeId; public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] DISPLAYCONFIG_PATH_INFO[] pathInfo, ref uint modes, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfo, IntPtr topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME info);
    [DllImport("dxva2.dll")] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
    [DllImport("dxva2.dll")] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] private static extern bool DestroyPhysicalMonitor(IntPtr handle);
    [DllImport("dxva2.dll")] private static extern bool GetMonitorBrightness(IntPtr handle, out uint min, out uint current, out uint max);
    [DllImport("dxva2.dll")] private static extern bool SetMonitorBrightness(IntPtr handle, uint brightness);
}
