using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using IdmClone.Engine;
using IdmClone.Vid;

namespace IdmClone;

/// <summary>
/// "Utylix Vid Editor" is the same program started under another name: UtylixVid.exe is a copy of Utylix.exe's launcher that points at the same
/// Utylix.dll (made by tools\pack-setup.ps1), and this file is where that copy turns into the editor instead of the toolbox. It is its own
/// process (own taskbar button, own memory) and shares nothing with the running Utylix: no tray icon, no single-instance lock, no local API.
/// </summary>
public partial class App
{
    /// <summary>True when this process was started as UtylixVid.exe (or with --vid, handy for tests and for a single-file copy).</summary>
    internal static bool WantsVidEditor(string[] args)
    {
        if (args.Contains("--vid")) return true;
        try { return string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "UtylixVid", StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    /// <summary>Starts Utylix Vid Editor (UtylixVid.exe beside this program; a single-file copy starts itself in editor mode).</summary>
    public static void OpenVidEditor(string? file = null)
    {
        try
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "UtylixVid.exe");
            var psi = File.Exists(exe)
                ? new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false }
                : new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--vid") { UseShellExecute = false };
            if (file != null) psi.ArgumentList.Add(file);
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            UMessage.Show("The video editor could not be started:\n\n" + e.Message, "Utylix", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StartVidEditor(string[] args)
    {
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        DispatcherUnhandledException += (_, ev) =>
        {
            LogError(ev.Exception);
            UMessage.Show(null, "Something went wrong in the video editor:\n\n" + ev.Exception.Message, "Utylix Vid Editor", MessageBoxButton.OK, MessageBoxImage.Warning);
            ev.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => LogError(ev.ExceptionObject as Exception);

        // The editor's settings are its own (%APPDATA%\UtylixVid\settings.json, changed in the editor's Settings window): Utylix's settings file
        // is never read or written here. Only the big ffmpeg program is shared, so it isn't downloaded twice.
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dataDir = Path.Combine(appData, "UtylixVid");
        string? toolsDir = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--data") dataDir = args[i + 1];
            if (args[i] == "--tools") toolsDir = args[i + 1];
        }
        string utylixTools = Path.Combine(appData, "Utylix", "tools");
        string ownTools = Path.Combine(dataDir, "tools");
        Tools.Dir = toolsDir ?? (File.Exists(Path.Combine(ownTools, "ffmpeg", "ffmpeg.exe")) ? ownTools : utylixTools);
        DataDir = dataDir;
        VidSettings.Load(dataDir);
        ApplyTheme(VidSettings.Current.Theme, VidSettings.Current.Accent);

        string? file = args.FirstOrDefault(a => !a.StartsWith("--") && File.Exists(a));
        var window = new VidWindow(file);
        MainWindow = window;
        if (args.Contains("--topmost")) window.Topmost = true;          // (tests: keeps the window in front of everything while it is photographed)
        window.Show();
    }
}
