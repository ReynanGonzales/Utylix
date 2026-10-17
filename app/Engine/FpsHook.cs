using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace IdmClone.Engine;

/// <summary>
/// Utylix's own frame limiter (what RivaTuner's limiter does, nothing more). A small native DLL (<c>hook\UtylixFpsHook64.dll</c> / <c>32.dll</c> in the program
/// folder, source in native\UtylixFpsHook.c) is loaded into a program that has a frame limit in "Memory and CPU limits": inside it, the Present call of
/// Direct3D 9 / 10 / 11 / 12 waits until the next frame is due. The limit is a number in <c>limit-&lt;process id&gt;.txt</c> next to the DLL, which the DLL looks at
/// twice a second; 0 or no file = no limit. The DLL is put in with the usual LoadLibrary-in-the-other-program's-thread method, so it works for programs that
/// run as the same user (an administrator program needs the administrator guard). Nothing is added to the program besides that one DLL.
/// </summary>
public static class FpsHook
{
    private static string SourceDir => Path.Combine(AppContext.BaseDirectory, "hook");

    /// <summary>The DLLs travel with Utylix (program folder); a build without them has no frame limiter of its own.</summary>
    public static bool Available => File.Exists(Path.Combine(SourceDir, "UtylixFpsHook64.dll"));

    public static string Dir(string dataDir) => Path.Combine(dataDir, "fpshook");

    /// <summary>The copy of the DLL a program will load (kept in the data folder: the program folder may be read-only for the program's own account).</summary>
    private static string DllFor(string dataDir, bool is32)
    {
        string name = is32 ? "UtylixFpsHook32" : "UtylixFpsHook64";
        string src = Path.Combine(SourceDir, name + ".dll");
        // The copy is named after its content: a program that already has an OLDER copy loaded keeps that file locked, and a new version could never
        // replace it (the program would go on running the old one). A different file name is a different DLL for Windows.
        string tag = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(src)))[..10].ToLowerInvariant();
        string dst = Path.Combine(Dir(dataDir), $"{name}-{tag}.dll");
        Directory.CreateDirectory(Dir(dataDir));
        try { if (!File.Exists(dst)) File.Copy(src, dst, true); }
        catch (IOException) { /* (another Utylix is copying it right now) */ }
        try   // (older copies that no program has loaded any more)
        {
            foreach (string old in Directory.GetFiles(Dir(dataDir), name + "*.dll"))
                if (!string.Equals(old, dst, StringComparison.OrdinalIgnoreCase)) { try { File.Delete(old); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
        }
        catch (IOException) { }
        return dst;
    }

    private static string LimitFile(string dataDir, int pid) => Path.Combine(Dir(dataDir), $"limit-{pid}.txt");

    /// <summary>Sets the limit a program with this process id is held to (0 = free again).</summary>
    public static void SetLimit(string dataDir, int pid, int fps)
    {
        try
        {
            string path = LimitFile(dataDir, pid);
            if (fps <= 0) { if (File.Exists(path)) File.Delete(path); return; }
            Directory.CreateDirectory(Dir(dataDir));
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, fps.ToString());
            File.Move(tmp, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The frames per second the program really shows (written by the DLL once a second), or null when the DLL is not in it / the number is stale.</summary>
    public static (double Fps, string Api)? ReadFps(string dataDir, int pid)
    {
        try
        {
            string path = Path.Combine(Dir(dataDir), $"fps-{pid}.txt");
            if (!File.Exists(path) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds > 4) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            string[] parts = sr.ReadToEnd().Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);       // "59.9 D3D11" (the API is there once the program has drawn a frame)
            if (parts.Length == 0 || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)) return null;
            return (v, parts.Length > 1 ? parts[1] : "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Removes the limit and fps files of programs that have closed.</summary>
    public static void Cleanup(string dataDir)
    {
        try
        {
            if (!Directory.Exists(Dir(dataDir))) return;
            foreach (string f in Directory.GetFiles(Dir(dataDir), "*-*.txt").Where(x => { string b = Path.GetFileName(x); return b.StartsWith("limit-") || b.StartsWith("fps-"); }))
            {
                string stem = Path.GetFileNameWithoutExtension(f);
                string n = stem[(stem.IndexOf('-') + 1)..];
                if (!int.TryParse(n, out int pid)) continue;
                bool alive;
                try { using var p = System.Diagnostics.Process.GetProcessById(pid); alive = !p.HasExited; }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { alive = false; }
                if (!alive) File.Delete(f);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ---------- putting the DLL into a program ----------
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process(IntPtr process, out bool wow64);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr addr, UIntPtr size, uint type, uint prot);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(IntPtr p, IntPtr addr, UIntPtr size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr p, IntPtr addr, byte[] buf, UIntPtr size, out UIntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateRemoteThread(IntPtr p, IntPtr attr, UIntPtr stack, IntPtr start, IntPtr param, uint flags, out uint id);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeThread(IntPtr h, out uint code);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool EnumProcessModulesEx(IntPtr process, IntPtr[] modules, int cb, out int needed, uint filter);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetModuleFileNameEx(IntPtr process, IntPtr module, StringBuilder name, int size);

    /// <summary>Loads the DLL into the program. Null when it is in (or already was); otherwise a short plain reason.</summary>
    public static string? Inject(int pid, string dataDir)
    {
        IntPtr h = OpenProcess(0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400, false, pid);       // create thread, VM operation / read / write, query information
        if (h == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            return err == 5 ? "it runs as administrator: allow the administrator guard" : $"could not be reached (error {err})";
        }
        try
        {
            bool is32 = Environment.Is64BitOperatingSystem && IsWow64Process(h, out bool w) && w;
            string dll = DllFor(dataDir, is32);
            IntPtr loadLibrary = is32 ? LoadLibraryAddress32(h) : GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
            if (loadLibrary == IntPtr.Zero) return "the 32-bit loader could not be found";
            byte[] path = Encoding.Unicode.GetBytes(dll + "\0");
            IntPtr mem = VirtualAllocEx(h, IntPtr.Zero, (UIntPtr)path.Length, 0x3000, 0x04);
            if (mem == IntPtr.Zero) return "no room in the program";
            try
            {
                if (!WriteProcessMemory(h, mem, path, (UIntPtr)path.Length, out _)) return "could not write into the program";
                IntPtr t = CreateRemoteThread(h, IntPtr.Zero, UIntPtr.Zero, loadLibrary, mem, 0, out _);
                if (t == IntPtr.Zero) return $"the program did not accept it (error {Marshal.GetLastWin32Error()})";
                try
                {
                    if (WaitForSingleObject(t, 8000) != 0) return "the program did not answer";
                    GetExitCodeThread(t, out uint code);
                    return code == 0 ? "the program refused to load it" : null;                   // (the thread's result is the low half of LoadLibrary's answer)
                }
                finally { CloseHandle(t); }
            }
            finally { VirtualFreeEx(h, mem, UIntPtr.Zero, 0x8000); }
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException) { return e.Message; }
        finally { CloseHandle(h); }
    }

    // ---------- a 32-bit program: where is ITS LoadLibraryW? (this program is 64-bit: it is found in the 32-bit kernel32.dll file and the base it has there) ----------
    private static IntPtr LoadLibraryAddress32(IntPtr process)
    {
        try
        {
            var mods = new IntPtr[1024];
            if (!EnumProcessModulesEx(process, mods, mods.Length * IntPtr.Size, out int needed, 0x01)) return IntPtr.Zero;       // LIST_MODULES_32BIT
            int count = Math.Min(mods.Length, needed / IntPtr.Size);
            string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
            IntPtr BaseOf(string file)
            {
                for (int i = 0; i < count; i++)
                {
                    var sb = new StringBuilder(520);
                    if (GetModuleFileNameEx(process, mods[i], sb, sb.Capacity) == 0) continue;
                    // (a 32-bit program's modules are listed as C:\Windows\System32\...: the 32-bit folder's files, by name; this list holds 32-bit modules only)
                    if (string.Equals(Path.GetFileName(sb.ToString()), file, StringComparison.OrdinalIgnoreCase)) return mods[i];
                }
                return IntPtr.Zero;
            }
            string dllName = "kernel32.dll", func = "LoadLibraryW";
            for (int step = 0; step < 4; step++)                                                // (a forwarded function is looked up in the DLL it points to)
            {
                IntPtr dllBase = BaseOf(dllName);
                if (dllBase == IntPtr.Zero) return IntPtr.Zero;
                var (rva, forward) = ExportOf(Path.Combine(sys32, dllName), func);
                if (rva != 0) return dllBase + (int)rva;
                if (forward == null) return IntPtr.Zero;
                int dot = forward.IndexOf('.');
                if (dot <= 0) return IntPtr.Zero;
                dllName = forward[..dot] + ".dll";
                func = forward[(dot + 1)..];
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or IndexOutOfRangeException) { }
        return IntPtr.Zero;
    }

    /// <summary>The address (relative to the DLL's base) of an exported function of a 32-bit DLL file, or the "dll.function" it is forwarded to.</summary>
    private static (uint Rva, string? Forward) ExportOf(string file, string function)
    {
        byte[] d = File.ReadAllBytes(file);
        int pe = BitConverter.ToInt32(d, 0x3C);
        int sections = BitConverter.ToUInt16(d, pe + 6), optSize = BitConverter.ToUInt16(d, pe + 20), opt = pe + 24;
        uint expRva = BitConverter.ToUInt32(d, opt + 0x60), expSize = BitConverter.ToUInt32(d, opt + 0x64);
        if (expRva == 0) return (0, null);
        int secTable = opt + optSize;
        int Offset(uint rva)
        {
            for (int i = 0; i < sections; i++)
            {
                int s = secTable + i * 40;
                uint va = BitConverter.ToUInt32(d, s + 12), raw = BitConverter.ToUInt32(d, s + 20), size = Math.Max(BitConverter.ToUInt32(d, s + 8), BitConverter.ToUInt32(d, s + 16));
                if (rva >= va && rva < va + size) return (int)(rva - va + raw);
            }
            return -1;
        }
        int exp = Offset(expRva);
        uint count = BitConverter.ToUInt32(d, exp + 0x18);
        int funcs = Offset(BitConverter.ToUInt32(d, exp + 0x1C)), names = Offset(BitConverter.ToUInt32(d, exp + 0x20)), ords = Offset(BitConverter.ToUInt32(d, exp + 0x24));
        for (int i = 0; i < count; i++)
        {
            int n = Offset(BitConverter.ToUInt32(d, names + i * 4));
            int end = Array.IndexOf(d, (byte)0, n);
            if (Encoding.ASCII.GetString(d, n, end - n) != function) continue;
            uint rva = BitConverter.ToUInt32(d, funcs + BitConverter.ToUInt16(d, ords + i * 2) * 4);
            if (rva >= expRva && rva < expRva + expSize)
            {
                int f = Offset(rva), fe = Array.IndexOf(d, (byte)0, f);
                return (0, Encoding.ASCII.GetString(d, f, fe - f));
            }
            return (rva, null);
        }
        return (0, null);
    }
}
