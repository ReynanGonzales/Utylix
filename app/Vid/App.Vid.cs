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

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dataDir = Path.Combine(appData, "Utylix");
        string? toolsDir = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--data") dataDir = args[i + 1];
            if (args[i] == "--tools") toolsDir = args[i + 1];
        }
        Tools.Dir = toolsDir ?? Path.Combine(dataDir, "tools");
        DataDir = dataDir;

        // the theme and accent the person chose in Utylix (read from its settings file; this process never writes them)
        string theme = "system", accent = "blue";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDir, "config.json")));
            if (doc.RootElement.TryGetProperty("Theme", out var t) && t.ValueKind == JsonValueKind.String) theme = t.GetString() ?? theme;
            if (doc.RootElement.TryGetProperty("Accent", out var a) && a.ValueKind == JsonValueKind.String) accent = a.GetString() ?? accent;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { /* first run: the system theme */ }
        ApplyTheme(theme, accent);

        string? file = args.FirstOrDefault(a => !a.StartsWith("--") && File.Exists(a));
        var window = new VidWindow(file);
        MainWindow = window;
        window.Show();
    }
}
