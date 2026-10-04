using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace IdmClone.Engine;

/// <summary>
/// Talks to the PC's SMBus through the PawnIO driver (the same free driver the fan control uses for its sensors) with the
/// "SmbusPIIX4" module that LibreHardwareMonitor already carries. Needs administrator rights, so it only runs inside the elevated helper.
/// Every transaction is expected to happen while <see cref="Lock"/> is held: the board's own software (Armoury Crate, HWiNFO ...) shares the same
/// wires and agrees on the "Access_SMBUS.HTP.Method" mutex.
/// </summary>
internal sealed class PawnSmbus : IDisposable
{
    private const int Quick = 0, Byte = 1, ByteData = 2, WordData = 3, BlockData = 5;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OpenFn(out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int LoadFn(IntPtr handle, byte[] blob, UIntPtr size);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ExecFn(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string name, ulong[]? input, UIntPtr inSize, ulong[]? output, UIntPtr outSize, out UIntPtr returned);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CloseFn(IntPtr handle);

    private IntPtr _lib, _handle;
    private ExecFn? _exec;
    private CloseFn? _close;
    private Mutex? _mutex;

    /// <summary>Why <see cref="Open"/> failed, in plain words.</summary>
    public static string Problem { get; private set; } = "";

    public static PawnSmbus? Open()
    {
        Problem = "";
        var bus = new PawnSmbus();
        try
        {
            string[] places =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PawnIO", "PawnIOLib.dll"),
            };
            string? lib = null;
            foreach (var p in places) if (File.Exists(p)) { lib = p; break; }
            if (lib == null) { Problem = "The free PawnIO driver is not installed (pawnio.eu, or in a terminal: winget install namazso.PawnIO)."; return null; }
            bus._lib = NativeLibrary.Load(lib);
            var open = Marshal.GetDelegateForFunctionPointer<OpenFn>(NativeLibrary.GetExport(bus._lib, "pawnio_open"));
            var load = Marshal.GetDelegateForFunctionPointer<LoadFn>(NativeLibrary.GetExport(bus._lib, "pawnio_load"));
            bus._exec = Marshal.GetDelegateForFunctionPointer<ExecFn>(NativeLibrary.GetExport(bus._lib, "pawnio_execute"));
            bus._close = Marshal.GetDelegateForFunctionPointer<CloseFn>(NativeLibrary.GetExport(bus._lib, "pawnio_close"));
            if (open(out bus._handle) < 0 || bus._handle == IntPtr.Zero) { Problem = "PawnIO could not be opened (it needs administrator rights)."; bus.Dispose(); return null; }
            byte[]? blob = ModuleBlob();
            if (blob == null) { Problem = "The SMBus module of PawnIO is missing."; bus.Dispose(); return null; }
            if (load(bus._handle, blob, (UIntPtr)blob.Length) < 0) { Problem = "PawnIO did not accept the SMBus module."; bus.Dispose(); return null; }
            // the AMD chipset's SMBus controller answers on this PC only if the module can identify it
            var id = new ulong[3];
            if (Exec("ioctl_identity", null, id, out _) < 0 || id[0] == 0) { Problem = "This PC's SMBus controller is not one PawnIO knows."; bus.Dispose(); return null; }
            return bus;

            int Exec(string name, ulong[]? i, ulong[]? o, out UIntPtr r) =>
                bus._exec!(bus._handle, name, i, (UIntPtr)(i?.Length ?? 0), o, (UIntPtr)(o?.Length ?? 0), out r);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or MarshalDirectiveException or IOException)
        {
            Problem = "PawnIO could not be used: " + e.Message;
            bus.Dispose();
            return null;
        }
    }

    private static byte[]? ModuleBlob()
    {
        var asm = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly;
        using var s = asm.GetManifestResourceStream("LibreHardwareMonitor.Resources.PawnIo.SmbusPIIX4.bin");
        if (s == null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Takes the shared SMBus mutex; dispose it to give it back.</summary>
    public IDisposable? Lock(int waitMs = 5000)
    {
        _mutex ??= new Mutex(false, @"Global\Access_SMBUS.HTP.Method");
        try { if (!_mutex.WaitOne(waitMs)) return null; }
        catch (AbandonedMutexException) { /* the previous owner ended; it is ours now */ }
        return new Release(_mutex);
    }

    private sealed class Release : IDisposable
    {
        private Mutex? _m;
        public Release(Mutex m) => _m = m;
        public void Dispose() { try { _m?.ReleaseMutex(); } catch (ApplicationException) { } _m = null; }
    }

    private int Call(string name, ulong[]? input, ulong[]? output)
    {
        if (_exec == null) return -1;
        return _exec(_handle, name, input, (UIntPtr)(input?.Length ?? 0), output, (UIntPtr)(output?.Length ?? 0), out _);
    }

    public bool SelectPort(int port) => Call("ioctl_piix4_port_sel", new ulong[] { (ulong)port }, new ulong[1]) >= 0;

    /// <summary>Does anything answer at this address? (An address-only write: it carries no data.)</summary>
    public bool Probe(int address) => Call("ioctl_smbus_xfer", new ulong[] { (ulong)address, 0, 0, Quick }, null) >= 0;

    public int ReadByteData(int address, int command)
    {
        var o = new ulong[1];
        return Call("ioctl_smbus_xfer", new ulong[] { (ulong)address, 1, (ulong)command, ByteData }, o) >= 0 ? (int)(o[0] & 0xFF) : -1;
    }

    public bool WriteByteData(int address, int command, int value) =>
        Call("ioctl_smbus_xfer", new ulong[] { (ulong)address, 0, (ulong)command, ByteData, (ulong)(value & 0xFF) }, null) >= 0;

    public bool WriteWordData(int address, int command, int value) =>
        Call("ioctl_smbus_xfer", new ulong[] { (ulong)address, 0, (ulong)command, WordData, (ulong)(value & 0xFFFF) }, null) >= 0;

    public bool WriteBlock(int address, int command, byte[] data)
    {
        if (data.Length is < 1 or > 32) return false;
        var packed = new byte[40];
        packed[0] = (byte)data.Length;
        Array.Copy(data, 0, packed, 1, data.Length);
        var input = new ulong[9];
        input[0] = (ulong)address; input[1] = 0; input[2] = (ulong)command; input[3] = BlockData;
        for (int i = 0; i < 5; i++) input[4 + i] = BitConverter.ToUInt64(packed, i * 8);
        return Call("ioctl_smbus_xfer", input, null) >= 0;
    }

    public void Dispose()
    {
        try { if (_handle != IntPtr.Zero) _close?.Invoke(_handle); } catch (Exception) { }
        _handle = IntPtr.Zero;
        try { if (_lib != IntPtr.Zero) NativeLibrary.Free(_lib); } catch (Exception) { }
        _lib = IntPtr.Zero;
        _mutex?.Dispose();
    }
}

/// <summary>One RAM stick's lighting chip (the "ENE" controller that Aura Sync, T-Force Delta RGB and many other lit sticks use).</summary>
internal sealed class EneModule
{
    // registers of the chip (written as a 16-bit address, then data; they count up by themselves)
    private const int RegName = 0x1000, RegConfig = 0x1C00, RegDirect = 0x8020, RegMode = 0x8021, RegSpeed = 0x8022, RegDirection = 0x8023, RegApply = 0x80A0;
    private const int RegSlotIndex = 0x80F8, RegI2cAddress = 0x80F9;
    private const int ColorsDirectV1 = 0x8000, ColorsEffectV1 = 0x8010, ColorsDirectV2 = 0x8100, ColorsEffectV2 = 0x8160;
    private const int ApplyValue = 0x01, SaveValue = 0xAA;

    public int Port { get; }
    public int Address { get; }
    public string Name { get; private set; } = "";
    public int Leds { get; private set; }
    private readonly bool _v2;
    public byte[] Config { get; private set; } = Array.Empty<byte>();

    private EneModule(int port, int address, string name, byte[] config)
    {
        Port = port; Address = address; Name = name; Config = config;
        _v2 = !name.StartsWith("DIMM_LED", StringComparison.Ordinal);
        int n = config.Length > 2 ? config[0x02] : 0;
        if (n is < 1 or > 30) n = config.Length > 3 ? config[0x03] : 0;
        Leds = n is < 1 or > 30 ? 0 : n;
    }

    public static readonly string[] ModeNames =
    {
        "Off", "Static", "Breathing", "Flashing", "Colour cycle", "Rainbow", "Colour cycle breathing", "Chase fade", "Colour cycle chase fade",
        "Chase", "Colour cycle chase", "Colour cycle wave", "Rainbow pulse", "Random flicker", "Double fade",
    };
    /// <summary>Modes that use the chosen colour (the others make their own colours).</summary>
    public static bool UsesColour(int mode) => mode is 1 or 2 or 3 or 7 or 9 or 13 or 14;

    // ----------------------------------------------------------------------------------------------- registers

    private static bool Reg(PawnSmbus b, int address, int reg) => b.WriteWordData(address, 0x00, ((reg << 8) & 0xFF00) | ((reg >> 8) & 0x00FF));

    private static int ReadReg(PawnSmbus b, int address, int reg) => Reg(b, address, reg) ? b.ReadByteData(address, 0x81) : -1;

    private static bool WriteReg(PawnSmbus b, int address, int reg, int value) => Reg(b, address, reg) && b.WriteByteData(address, 0x01, value);

    private static bool WriteBlockReg(PawnSmbus b, int address, int reg, byte[] data)
    {
        if (!Reg(b, address, reg)) return false;
        if (data.Length <= 32 && b.WriteBlock(address, 0x03, data)) return true;
        foreach (var x in data) if (!b.WriteByteData(address, 0x01, x)) return false;
        return true;
    }

    // ----------------------------------------------------------------------------------------------- finding

    /// <summary>True when the thing at this address answers like an ENE lighting chip (its registers 0xA0..0xAF read 0..15).</summary>
    private static bool Looks(PawnSmbus b, int address)
    {
        if (!b.Probe(address)) return false;
        for (int i = 0; i < 16; i++)
            if (b.ReadByteData(address, 0xA0 + i) != i) return false;
        return true;
    }

    private static string ReadName(PawnSmbus b, int address)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 16; i++)
        {
            int c = ReadReg(b, address, RegName + i);
            if (c <= 0) break;
            sb.Append(c is >= 32 and < 127 ? (char)c : '?');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Looks at the addresses 0x70..0x77 on both SMBus ports (read-only: nothing is changed). The caller holds the lock.
    /// Sticks that were never given their own address (everything answers at 0x77) are NOT touched here: see <see cref="Separate"/>.
    /// </summary>
    public static List<EneModule> Scan(PawnSmbus b, List<string>? log = null)
    {
        var found = new List<EneModule>();
        for (int port = 0; port <= 1; port++)
        {
            if (!b.SelectPort(port)) { log?.Add($"port {port}: not available"); continue; }
            for (int address = 0x70; address <= 0x77; address++)
            {
                if (!Looks(b, address)) continue;
                string name = ReadName(b, address);
                if (name.StartsWith("Micron", StringComparison.OrdinalIgnoreCase)) { log?.Add($"port {port} 0x{address:X2}: Micron memory, skipped"); continue; }
                var cfg = new byte[64];
                for (int i = 0; i < cfg.Length; i++) { int v = ReadReg(b, address, RegConfig + i); cfg[i] = (byte)(v < 0 ? 0 : v); }
                var m = new EneModule(port, address, name, cfg);
                log?.Add($"port {port} 0x{address:X2}: \"{name}\", {m.Leds} lights");
                if (m.Leds > 0) found.Add(m);
            }
        }
        return found;
    }

    // ----------------------------------------------------------------------------------------------- lighting

    private void Rgb(PawnSmbus b, int effectBase, byte[] colour)
    {
        var buf = new byte[Leds * 3];
        for (int i = 0; i < Leds; i++) { buf[i * 3] = colour[0]; buf[i * 3 + 1] = colour[2]; buf[i * 3 + 2] = colour[1]; }       // the chip wants red, BLUE, green
        for (int off = 0; off < buf.Length; off += 15)
        {
            var piece = new byte[Math.Min(15, buf.Length - off)];
            Array.Copy(buf, off, piece, 0, piece.Length);
            WriteBlockReg(b, Address, effectBase + off, piece);
        }
    }

    /// <summary>Sets the lights now (they go back to what is saved in the stick when the PC is turned off). The caller holds the lock.</summary>
    public bool Apply(PawnSmbus b, int mode, byte r, byte g, byte bl, int speed, bool reverse)
    {
        if (!b.SelectPort(Port)) return false;
        mode = Math.Clamp(mode, 0, ModeNames.Length - 1);
        speed = Math.Clamp(speed, 0, 4);                      // 0 = fastest .. 4 = slowest, as the chip counts
        if (!WriteReg(b, Address, RegDirect, 0)) return false;
        WriteReg(b, Address, RegApply, ApplyValue);
        Rgb(b, _v2 ? ColorsEffectV2 : ColorsEffectV1, new[] { r, g, bl });
        bool ok = WriteReg(b, Address, RegMode, mode) && WriteReg(b, Address, RegSpeed, speed) && WriteReg(b, Address, RegDirection, reverse ? 1 : 0);
        WriteReg(b, Address, RegApply, ApplyValue);
        return ok;
    }

    /// <summary>Stores the current look inside the stick, so it comes back by itself (even with Utylix closed). Flash memory wears out: only on request.</summary>
    public bool Save(PawnSmbus b)
    {
        if (!b.SelectPort(Port)) return false;
        return WriteReg(b, Address, RegApply, SaveValue);
    }

    /// <summary>What the stick is doing right now (mode, speed, reverse, colour of its first light), for showing in the page.</summary>
    public (int Mode, int Speed, bool Reverse, byte R, byte G, byte B)? Read(PawnSmbus b)
    {
        if (!b.SelectPort(Port)) return null;
        int mode = ReadReg(b, Address, RegMode), speed = ReadReg(b, Address, RegSpeed), dir = ReadReg(b, Address, RegDirection);
        int basis = _v2 ? ColorsEffectV2 : ColorsEffectV1;
        int r = ReadReg(b, Address, basis), bl = ReadReg(b, Address, basis + 1), g = ReadReg(b, Address, basis + 2);
        if (mode < 0 || speed < 0 || dir < 0 || r < 0 || g < 0 || bl < 0) return null;
        return (mode, speed, dir == 1, (byte)r, (byte)g, (byte)bl);
    }
}
