using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;

namespace IdmClone;

/// <summary>
/// The program folder keeps its files in a few subfolders (dotnet\ = the .NET runtime's libraries, wpf\ = Windows' window libraries, libs\ =
/// Utylix's own libraries) so the main folder isn't 300 files long. .NET itself only looks for libraries next to the exe, so this finds them
/// in those folders: it runs before anything else in the program (a module initializer) and answers when .NET can't find a library.
/// Only a handful of core libraries stay beside the exe (tools/organize-publish.ps1 decides which). A flat folder, a single-file build
/// or a test build run from bin\ simply has no such folders, and nothing happens.
/// </summary>
internal static class Program
{
    /// <summary>
    /// The entry point. It is not in App itself because App's base class lives in a window library, which .NET would have to find (in a
    /// subfolder) before any of our code could run: this class has no such dependency, so the folders are known first.
    /// </summary>
    [STAThread]
    public static int Main()
    {
        AppFolders.Init();
        return Run();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}

internal static class AppFolders
{
    public static readonly string[] Names = { "dotnet", "wpf", "libs" };
    private static Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    private static string? _log;

    private static bool _started;

    internal static void Init()
    {
        if (_started) return;
        _started = true;
        string root = AppContext.BaseDirectory;
        bool any = false;
        foreach (string n in Names) if (Directory.Exists(Path.Combine(root, n))) any = true;
        if (!any) return;                                                              // (a flat folder: nothing to do)
        _log = Environment.GetEnvironmentVariable("UTYLIX_FOLDERS_LOG");
        Scan(root);
        AssemblyLoadContext.Default.Resolving += Resolve;
    }

    private static void Scan(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in Names)
        {
            string dir = Path.Combine(root, name);
            try { if (Directory.Exists(dir)) foreach (string file in Directory.EnumerateFiles(dir, "*.dll")) map.TryAdd(Path.GetFileNameWithoutExtension(file), file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log("scan of " + name + " failed: " + e.Message); }
        }
        lock (Gate) _map = map;
        Log("scanned: " + map.Count + " libraries");
    }

    [ThreadStatic] private static bool _inside;

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        // anything this method itself needs that isn't beside the exe would come back here: answer "not found" to that, never loop
        if (_inside || name.Name == null) return null;
        _inside = true;
        try
        {
            Dictionary<string, string> map; lock (Gate) map = _map;
            if (!map.TryGetValue(name.Name, out string? path)) return null;         // (a missing satellite / optional library: normal)
            try { return context.LoadFromAssemblyPath(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Retry(context, name.Name, path, e); }
        }
        finally { _inside = false; }
    }

    /// <summary>A file that Windows is still looking over can fail once: try again for a moment (kept apart, so the common path needs fewer libraries).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Assembly? Retry(AssemblyLoadContext context, string name, string path, Exception first)
    {
        Log("load of " + name + " failed: " + first.Message);
        for (int attempt = 0; attempt < 40; attempt++)
        {
            Thread.Sleep(50);
            try { return context.LoadFromAssemblyPath(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private static void Log(string text)
    {
        if (_log == null) return;
        try { File.AppendAllText(_log, DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + Environment.NewLine); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>"--selftest": loads every library of the program folder and opens a hidden window, then ends. The build uses it to be sure a folder with subfolders really starts.</summary>
internal static class SelfTest
{
    public static int Run()
    {
        var problems = new List<string>();
        int loaded = 0, native = 0;
        string root = AppContext.BaseDirectory;
        foreach (string file in Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories))
        {
            AssemblyName name;
            try { name = AssemblyName.GetAssemblyName(file); }
            catch (BadImageFormatException) { native++; continue; }                  // (a native library: not loaded here)
            catch (Exception e) when (e is IOException or FileLoadException) { problems.Add(Path.GetRelativePath(root, file) + ": " + e.Message); continue; }
            try { AssemblyLoadContext.Default.LoadFromAssemblyName(name); loaded++; }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException) { problems.Add(Path.GetRelativePath(root, file) + ": " + e.Message); }
        }
        try
        {
            // a window, text and a PDF page: the window libraries, their native parts and PDFium
            var window = new System.Windows.Window { Width = 40, Height = 40, ShowInTaskbar = false, WindowStyle = System.Windows.WindowStyle.None, ShowActivated = false, Content = new System.Windows.Controls.TextBlock { Text = "ok" } };
            window.Show(); window.UpdateLayout(); window.Close();
            Engine.Pdfium.Init();
            using var pdf = Engine.PdfFile.Open(Path.Combine(root, "selftest.pdf"));
        }
        catch (FileNotFoundException e) when (e.FileName != null && e.FileName.EndsWith("selftest.pdf", StringComparison.OrdinalIgnoreCase)) { /* no test page next to the exe: fine */ }
        catch (Exception e) { problems.Add("window / PDF: " + e.GetType().Name + ": " + e.Message); }

        // a real call into each kind of library the program uses (each one on its own, so one failure doesn't hide the others)
        void Check(string what, Action action)
        {
            try { action(); }
            catch (Exception e) { problems.Add(what + ": " + e.GetType().Name + ": " + e.Message); }
        }
        Check("PdfSharp", () => { using var doc = new PdfSharp.Pdf.PdfDocument(); doc.AddPage(); using var ms = new MemoryStream(); doc.Save(ms, false); if (ms.Length < 100) throw new InvalidOperationException("empty"); });
        Check("Windows text reader (OCR)", () => { var engine = Engine.PdfOcr.CreateEngine(out _); _ = engine?.RecognizerLanguage.LanguageTag; });
        Check("MonoTorrent", () => _ = new MonoTorrent.Client.EngineSettingsBuilder().ToSettings());
        Check("ONNX Runtime", () => { using var options = new Microsoft.ML.OnnxRuntime.SessionOptions(); });
        Check("SharpCompress", () => { _ = typeof(SharpCompress.Archives.ArchiveFactory).Assembly.GetName().Version; _ = SharpCompress.Common.CompressionType.Deflate; });
        Check("NAudio", () => _ = new NAudio.Wave.WaveFormat(44100, 16, 2));
        Check("LibVLCSharp", () => _ = typeof(LibVLCSharp.Shared.LibVLC).Assembly.GetName().Version);
        Check("Hardware monitor", () => _ = typeof(LibreHardwareMonitor.Hardware.Computer).Assembly.GetName().Version);
        // (tests only) UTYLIX_ACLTEST=folder;folder: is each one a folder only administrators can change?
        string? acl = Environment.GetEnvironmentVariable("UTYLIX_ACLTEST");
        var extra = new List<string>();
        if (acl != null) foreach (string folder in acl.Split(';', StringSplitOptions.RemoveEmptyEntries)) extra.Add("acl: " + folder + " -> protected: " + FanTask.FolderIsProtected(folder));
        extra.Add("fan helper: direct install = " + FanTask.DirectInstall + ", folder will be locked = " + FanTask.WillLockFolder);
        string report = string.Join(Environment.NewLine, extra) + Environment.NewLine + $"selftest: {loaded} libraries loaded, {native} native, {problems.Count} problems" + Environment.NewLine + string.Join(Environment.NewLine, problems);
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "utylix-selftest.txt"), report); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return problems.Count == 0 ? 0 : 1;
    }
}
