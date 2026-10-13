# Makes UtylixVid.exe (the video editor) in a published program folder: a copy of Utylix.exe's launcher, which starts the same Utylix.dll
# (the program decides from its own file name that it is the editor: app\Vid\App.Vid.cs), with the editor's icon put into the copy.
# So the editor costs ~150 KB on disk, shares the .NET runtime and every library, and is still a program of its own (its own window, taskbar
# button, memory and Task Manager entry). Usage:  make-vid-exe.ps1 [-Dir app\publish]
param([string]$Dir = (Join-Path $PSScriptRoot "..\app\publish"), [string]$Icon = (Join-Path $PSScriptRoot "..\app\vid.ico"))
$ErrorActionPreference = "Stop"
$Dir = (Resolve-Path $Dir).Path
$src = Join-Path $Dir "Utylix.exe"
$dst = Join-Path $Dir "UtylixVid.exe"
if (-not (Test-Path $src)) { throw "No Utylix.exe in $Dir" }
Copy-Item $src $dst -Force

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

public static class VidIconPatch
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryEx(string file, IntPtr zero, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeLibrary(IntPtr module);
    delegate bool EnumNames(IntPtr module, IntPtr type, IntPtr name, IntPtr param);
    delegate bool EnumLangs(IntPtr module, IntPtr type, IntPtr name, ushort lang, IntPtr param);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumNames callback, IntPtr param);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool EnumResourceLanguages(IntPtr module, IntPtr type, IntPtr name, EnumLangs callback, IntPtr param);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr BeginUpdateResource(string file, bool deleteExisting);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateResource(IntPtr update, IntPtr type, IntPtr name, ushort lang, byte[] data, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool EndUpdateResource(IntPtr update, bool discard);

    const int RT_ICON = 3, RT_GROUP_ICON = 14;

    static List<KeyValuePair<long, ushort>> List(IntPtr module, int type)
    {
        var names = new List<long>();
        EnumResourceNames(module, (IntPtr)type, (m, t, n, p) => { names.Add(n.ToInt64()); return true; }, IntPtr.Zero);
        var result = new List<KeyValuePair<long, ushort>>();
        foreach (long n in names)
        {
            if (n > 0xFFFF) throw new InvalidOperationException("a named icon resource is not supported");
            ushort lang = 0;
            bool first = true;
            EnumResourceLanguages(module, (IntPtr)type, (IntPtr)n, (m, t, nm, l, p) => { if (first) { lang = l; first = false; } return true; }, IntPtr.Zero);
            result.Add(new KeyValuePair<long, ushort>(n, lang));
        }
        return result;
    }

    public static void Patch(string exe, string ico)
    {
        IntPtr module = LoadLibraryEx(exe, IntPtr.Zero, 0x2 | 0x20);              // as data + as image resource
        if (module == IntPtr.Zero) throw new InvalidOperationException("could not read " + exe);
        List<KeyValuePair<long, ushort>> groups, icons;
        try { groups = List(module, RT_GROUP_ICON); icons = List(module, RT_ICON); }
        finally { FreeLibrary(module); }

        byte[] file = File.ReadAllBytes(ico);
        int count = BitConverter.ToUInt16(file, 4);
        IntPtr h = BeginUpdateResource(exe, false);
        if (h == IntPtr.Zero) throw new InvalidOperationException("could not open " + exe + " for updating");
        bool ok = false;
        try
        {
            foreach (var i in icons) UpdateResource(h, (IntPtr)RT_ICON, (IntPtr)i.Key, i.Value, null, 0);
            ushort lang = groups.Count > 0 ? groups[0].Value : (ushort)0;
            long groupName = groups.Count > 0 ? groups[0].Key : 1;
            for (int g = 1; g < groups.Count; g++) UpdateResource(h, (IntPtr)RT_GROUP_ICON, (IntPtr)groups[g].Key, groups[g].Value, null, 0);
            var dir = new MemoryStream();
            var w = new BinaryWriter(dir);
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)count);
            for (int n = 0; n < count; n++)
            {
                int e = 6 + 16 * n;
                int size = BitConverter.ToInt32(file, e + 8), offset = BitConverter.ToInt32(file, e + 12);
                byte[] image = new byte[size];
                Array.Copy(file, offset, image, 0, size);
                ushort id = (ushort)(100 + n);
                if (!UpdateResource(h, (IntPtr)RT_ICON, (IntPtr)id, lang, image, (uint)size)) throw new InvalidOperationException("UpdateResource failed");
                w.Write(file[e]); w.Write(file[e + 1]); w.Write(file[e + 2]); w.Write((byte)0);
                w.Write(BitConverter.ToUInt16(file, e + 4)); w.Write(BitConverter.ToUInt16(file, e + 6));
                w.Write((uint)size); w.Write(id);
            }
            byte[] group = dir.ToArray();
            if (!UpdateResource(h, (IntPtr)RT_GROUP_ICON, (IntPtr)groupName, lang, group, (uint)group.Length)) throw new InvalidOperationException("UpdateResource failed (group)");
            ok = true;
        }
        finally { EndUpdateResource(h, !ok); }
    }
}
"@
[VidIconPatch]::Patch($dst, (Resolve-Path $Icon).Path)

# does the copy start (the self-test is the same code path as Utylix.exe's)?
$report = Join-Path ([IO.Path]::GetTempPath()) "utylix-selftest.txt"
Remove-Item $report -ErrorAction SilentlyContinue
$p = Start-Process $dst -ArgumentList "--selftest", "--no-register" -PassThru
$finished = $p.WaitForExit(90000)
$text = if (Test-Path $report) { Get-Content $report -Raw } else { "(no report)" }
if (-not ($finished -and $text -match "0 problems")) {
    if (-not $finished) { try { $p.Kill() } catch { } }
    Remove-Item $dst -Force -ErrorAction SilentlyContinue
    Write-Warning "make-vid-exe: UtylixVid.exe did not pass the self-test and was removed. Report: $text"
    exit 1
}
Write-Host ("make-vid-exe: UtylixVid.exe made ({0:0} KB), self-test passed." -f ((Get-Item $dst).Length / 1KB))
exit 0
