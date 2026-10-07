using System;
using System.IO;
using System.Windows;

namespace IdmClone;

/// <summary>The editor opens maximized when it was maximized the last time it was closed (a marker file in the data folder, like the other small settings).</summary>
public sealed partial class PdfWindow
{
    private static string MaximizedMarker => Path.Combine(App.DataDir, "pdf-window-maximized");

    private void RestoreWindowState()
    {
        try { if (File.Exists(MaximizedMarker)) WindowState = WindowState.Maximized; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void RememberWindowState()
    {
        if (WindowState == WindowState.Minimized) return;                 // (minimized says nothing about how it was wanted)
        try
        {
            if (WindowState == WindowState.Maximized) File.WriteAllText(MaximizedMarker, "1");
            else File.Delete(MaximizedMarker);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
