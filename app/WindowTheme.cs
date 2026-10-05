using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IdmClone;

/// <summary>Small helpers the newer windows share.</summary>
public static class WindowTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>A dark or light title bar to match the app's theme (Windows 10 20H1+/11).</summary>
    public static void DarkTitleBar(Window w) =>
        w.SourceInitialized += (_, _) => RefreshTitleBar(w);

    /// <summary>
    /// Brings a window to the front for real. A plain Activate() is refused by Windows when the request comes from a program that is not the one
    /// the person is using (a second copy forwarding "open this PDF"): the window then opens behind everything. This attaches to the foreground
    /// thread for a moment, which Windows allows, and nudges the window to the top.
    /// </summary>
    public static void BringToFront(Window w)
    {
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        IntPtr handle = new WindowInteropHelper(w).Handle;
        if (handle != IntPtr.Zero) ScreenGrab.ForceForeground(handle);
        bool top = w.Topmost; w.Topmost = true; w.Topmost = top;
    }

    /// <summary>Sets the title bar of an open window to the app's current theme (after the theme was changed).</summary>
    public static void RefreshTitleBar(Window w)
    {
        IntPtr handle = new WindowInteropHelper(w).Handle;
        if (handle == IntPtr.Zero) return;
        int on = App.IsDarkTheme ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref on, sizeof(int));
    }

    // ---------- a taskbar button of its own ----------
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey { public Guid Format; public uint Id; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Text; }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, out IPropertyStore store);

    /// <summary>
    /// Windows groups every window of one program under one taskbar button. Giving a window its own "application id" makes it a
    /// button (and a thumbnail) of its own, with its own icon, like the Utylix Player or the Archives window deserve.
    /// </summary>
    public static void OwnTaskbarButton(Window w, string appId) =>
        w.SourceInitialized += (_, _) =>
        {
            try
            {
                var iid = typeof(IPropertyStore).GUID;
                if (SHGetPropertyStoreForWindow(new WindowInteropHelper(w).Handle, ref iid, out var store) != 0 || store == null) return;
                var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };       // System.AppUserModel.ID
                var value = new PropVariant { Type = 31, Text = Marshal.StringToCoTaskMemUni(appId) };                 // VT_LPWSTR
                try { store.SetValue(ref key, ref value); store.Commit(); }
                finally { Marshal.FreeCoTaskMem(value.Text); Marshal.ReleaseComObject(store); }
            }
            catch (Exception e) when (e is COMException or InvalidCastException or EntryPointNotFoundException) { /* it stays in the main group */ }
        };

    private static ImageSource? _convertIcon;

    /// <summary>The biggest picture inside convert.ico, for showing large in a window.</summary>
    public static ImageSource ConvertIcon => _convertIcon ??= Load();

    private static ImageSource Load()
    {
        var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/convert.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var best = decoder.Frames[0];
        foreach (var f in decoder.Frames) if (f.PixelWidth > best.PixelWidth) best = f;
        best.Freeze();
        return best;
    }
}
