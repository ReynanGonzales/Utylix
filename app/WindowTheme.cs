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

    /// <summary>Dark title bar when Windows is in dark mode (Windows 10 20H1+/11).</summary>
    public static void DarkTitleBar(Window w) =>
        w.SourceInitialized += (_, _) =>
        {
            if (!App.IsDarkTheme) return;
            int on = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(w).Handle, 20, ref on, sizeof(int));
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
