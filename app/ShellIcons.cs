using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace IdmClone;

/// <summary>
/// The icon Windows shows for a file type (WinRAR's for .rar, your video player's for .mp4, ...),
/// fetched by extension so no file has to exist. Returns null when no program is registered for the type.
/// </summary>
public static class ShellIcons
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? ForFile(string? fileName)
    {
        string ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (ext.Length < 2 || ext.Length > 12) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(ext, out var cached)) return cached;
            ImageSource? icon = null;
            try { icon = Load(ext); }
            catch (Exception e) when (e is COMException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { }
            Cache[ext] = icon;
            return icon;
        }
    }

    private static ImageSource? Load(string ext)
    {
        // no registered program => Windows would give a blank generic page; better to fall back to our own symbol
        using (var key = Registry.ClassesRoot.OpenSubKey(ext))
            if (key == null) return null;

        int index = IconIndex(ext);
        if (index == GenericIndex.Value) return null;   // Windows only has its blank "page" for this type: not a real logo

        var info = new SHFILEINFO { iIcon = index };
        var iid = IID_IImageList;
        if (SHGetImageList(SHIL_EXTRALARGE, ref iid, out IntPtr imageList) != 0 || imageList == IntPtr.Zero) return null;
        try
        {
            IntPtr hIcon = ImageList_GetIcon(imageList, info.iIcon, ILD_TRANSPARENT);
            if (hIcon == IntPtr.Zero) return null;
            try
            {
                var bitmap = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                return bitmap;
            }
            finally { DestroyIcon(hIcon); }
        }
        finally { Marshal.Release(imageList); }
    }

    /// <summary>Index in Windows' system icon list of the icon used for this extension.</summary>
    private static int IconIndex(string ext)
    {
        var info = new SHFILEINFO();
        SHGetFileInfo("file" + ext, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
                      SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES);
        return info.iIcon;
    }

    /// <summary>The index Windows uses for file types nobody has registered (the blank page icon).</summary>
    private static readonly Lazy<int> GenericIndex = new(() => IconIndex(".idmclone-unregistered-type"));

    // ---------- Win32 ----------
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint SHGFI_SYSICONINDEX = 0x4000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const int SHIL_EXTRALARGE = 2;      // 48x48
    private const uint ILD_TRANSPARENT = 1;
    private static readonly Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int list, ref Guid riid, out IntPtr imageList);

    [DllImport("comctl32.dll")]
    private static extern IntPtr ImageList_GetIcon(IntPtr imageList, int index, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
