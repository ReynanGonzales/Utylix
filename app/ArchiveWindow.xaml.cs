using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;

namespace IdmClone;

/// <summary>
/// A window for one archive, like WinRAR's: opening a .zip/.rar/.7z shows just this (no main Utylix window).
/// It also serves as the "new archive" window (Add to archive…).
/// </summary>
public partial class ArchiveWindow : Window
{
    private static readonly List<ArchiveWindow> Windows = new();
    private static ArchiveWindow? _lastCreate;
    private static DateTime _lastCreateAt;

    public ArchivePage Page { get; } = new();

    /// <summary>Raised after an archive window closed (the app may then quit if it was only started to show it).</summary>
    public static event Action? AnyClosed;
    public static int Count => Windows.Count;

    private ArchiveWindow()
    {
        InitializeComponent();
        WindowTheme.OwnTaskbarButton(this, "Utylix.Archives");
        WindowTheme.DarkTitleBar(this);
        Host.Content = Page;
        Page.TitleChanged += t => Title = t + " - Utylix Archives";
        Windows.Add(this);
        // cascade a little so several windows are not stacked exactly
        WindowStartupLocation = WindowStartupLocation.Manual;
        var work = SystemParameters.WorkArea;
        Left = Math.Max(work.Left, (work.Width - Width) / 2 + work.Left + 26 * (Windows.Count - 1) % 156);
        Top = Math.Max(work.Top, (work.Height - Height) / 2 + work.Top + 26 * (Windows.Count - 1) % 156);
        Closed += (_, _) => { Windows.Remove(this); if (_lastCreate == this) _lastCreate = null; AnyClosed?.Invoke(); };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        Page.CancelCurrent();            // closing while it is working stops the job (nothing half-written is left)
        base.OnClosing(e);
    }

    private static void Bring(Window w)
    {
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        w.Topmost = true;
        w.Topmost = false;
    }

    /// <summary>Show an archive in its own window (the window that already has it open, if there is one).</summary>
    public static void OpenArchive(string path)
    {
        var existing = Windows.FirstOrDefault(w => string.Equals(w.Page.CurrentArchive, path, StringComparison.OrdinalIgnoreCase));
        var window = existing ?? new ArchiveWindow();
        Bring(window);
        if (existing == null) _ = window.Page.OpenArchiveAsync(path);
    }

    /// <summary>
    /// "Add to archive…": a window to create an archive from these files. Explorer starts one command per selected file, so
    /// files that arrive within a few seconds of each other go into the same window.
    /// </summary>
    public static void NewArchive(IEnumerable<string> files)
    {
        ArchiveWindow window;
        if (_lastCreate is { IsLoaded: true } recent && (DateTime.Now - _lastCreateAt).TotalSeconds < 4) window = recent;
        else { window = new ArchiveWindow(); _lastCreate = window; }
        _lastCreateAt = DateTime.Now;
        Bring(window);
        window.Page.AddSources(files);
    }
}
