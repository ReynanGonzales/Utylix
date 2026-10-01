using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IdmClone.Engine;
using Microsoft.Win32;

namespace IdmClone;

/// <summary>
/// Utylix installs itself: the setup program IS Utylix.exe (started as "Utylix-Setup.exe", with --setup, or the first time on a PC
/// where Utylix has never run). It copies itself into the programs folder, adds Start menu / desktop shortcuts, Start with Windows
/// and an entry in "Installed apps", and starts the installed copy. "Just for me" needs no administrator rights; "All users" asks
/// Windows for them (UAC). The installed copy removes everything again with --uninstall.
/// </summary>
internal static partial class Installer
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Utylix";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ExeName = "Utylix.exe";

    public static string UserDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Utylix");
    public static string AllUsersDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Utylix");

    private static RegistryKey Root(bool allUsers) => allUsers ? Registry.LocalMachine : Registry.CurrentUser;
    private static string StartMenuLink(bool allUsers) => Path.Combine(Environment.GetFolderPath(allUsers ? Environment.SpecialFolder.CommonPrograms : Environment.SpecialFolder.Programs), "Utylix.lnk");
    private static string DesktopLink(bool allUsers) => Path.Combine(Environment.GetFolderPath(allUsers ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory), "Utylix.lnk");

    public static bool IsAdmin
    {
        get
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Where Utylix was installed (and for whom), or null.</summary>
    public static (string Dir, bool AllUsers)? Installed()
    {
        foreach (bool all in new[] { false, true })
        {
            try
            {
                using var k = Root(all).OpenSubKey(UninstallKey);
                if (k?.GetValue("InstallLocation") is string dir && dir.Length > 0) return (dir, all);
            }
            catch (Exception e) when (e is System.Security.SecurityException or IOException) { }
        }
        return null;
    }

    private static bool SameFolder(string a, string? b) =>
        b != null && string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool IsInstalledCopy => Installed() is { } i && SameFolder(i.Dir, Path.GetDirectoryName(Environment.ProcessPath));

    /// <summary>Should this start show the installer instead of the program?</summary>
    public static bool WantsSetup(string[] args)
    {
        if (args.Contains("--setup") || args.Contains("--setup-auto")) return true;
        if (args.Length != 0 || IsInstalledCopy) return false;
        string name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        if (name.Contains("setup", StringComparison.OrdinalIgnoreCase)) return true;
        // a PC where Utylix has never run, started by double-click: offer to install it
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return !File.Exists(Path.Combine(appData, "Utylix", "config.json")) && !File.Exists(Path.Combine(appData, "IDMClone", "config.json"));
    }

    // ------------------------------------------------------------------------------------------------ setup window

    /// <summary>
    /// Copies this program into <paramref name="dir"/> and sets everything up; returns the path of the installed exe. The steps are
    /// paced on purpose so the progress can be followed (copying alone takes a second or two).
    /// </summary>
    public static string Install(Choices c, IProgress<(double, string)> progress)
    {
        string dir = c.Dir; bool allUsers = c.AllUsers, desktopShortcut = c.Desktop, autoStart = c.AutoStart;
        if (string.IsNullOrWhiteSpace(dir)) throw new ArgumentException("Choose a folder.");
        dir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dir)).TrimEnd('\\');
        string self = Environment.ProcessPath ?? throw new IOException("Utylix cannot tell where it is.");
        string exe = Path.Combine(dir, ExeName);

        progress.Report((3, "Getting ready…")); Thread.Sleep(700);
        progress.Report((8, "Creating the folder…"));
        Directory.CreateDirectory(dir);
        Thread.Sleep(600);

        if (!string.Equals(Path.GetFullPath(self), exe, StringComparison.OrdinalIgnoreCase))
        {
            progress.Report((12, "Closing the old copy, if one is running…"));
            StopRunning(exe);
            Thread.Sleep(500);
            string tmp = exe + ".new";
            using (var src = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                var buf = new byte[1024 * 1024];
                long done = 0; int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n);
                    done += n;
                    progress.Report((12 + 58.0 * done / src.Length, $"Copying Utylix…  {done / 1048576} of {src.Length / 1048576} MB"));
                    Thread.Sleep(35);
                }
            }
            File.Move(tmp, exe, true);
        }

        progress.Report((74, "Creating shortcuts…")); Thread.Sleep(500);
        MakeShortcut(StartMenuLink(allUsers), exe);
        if (desktopShortcut) MakeShortcut(DesktopLink(allUsers), exe);
        Thread.Sleep(400);

        progress.Report((84, "Adding Utylix to Windows' list of apps…")); Thread.Sleep(500);
        using (var k = Root(allUsers).CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "Utylix");
            k.SetValue("DisplayVersion", AppUpdater.CurrentText);
            k.SetValue("Publisher", "Utylix");
            k.SetValue("InstallLocation", dir);
            k.SetValue("DisplayIcon", $"\"{exe}\",0");
            k.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
        }

        progress.Report((92, autoStart ? "Setting Utylix to run when Windows starts…" : "Almost done…")); Thread.Sleep(600);
        using (var run = Root(allUsers).CreateSubKey(RunKey))
        {
            if (autoStart) run.SetValue("Utylix", $"\"{exe}\" --minimized"); else run.DeleteValue("Utylix", false);
        }
        // a copy installed for this user replaces an older "all users" one's start entry, not the other way round
        progress.Report((97, "Finishing…")); Thread.Sleep(700);
        return exe;
    }

    /// <summary>Starts the installed program as the normal user (even from the administrator copy of this window).</summary>
    private static void Launch(string exe, string? argument = null)
    {
        try
        {
            if (IsAdmin)
            {
                // Explorer runs what it is given without administrator rights; it cannot pass arguments, so it is given a small script
                string target = exe;
                if (argument != null)
                {
                    target = Path.Combine(Path.GetTempPath(), "utylix_start.cmd");
                    File.WriteAllText(target, "@echo off" + Environment.NewLine + "start " + (char)34 + (char)34 + " " + (char)34 + exe + (char)34 + " " + argument + Environment.NewLine);
                }
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { target }, UseShellExecute = false });
            }
            else
            {
                var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
                if (argument != null) psi.ArgumentList.Add(argument);
                Process.Start(psi);
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    /// <summary>Ends a running copy of Utylix that lives at <paramref name="exe"/> (so it can be replaced), other than this one.</summary>
    private static void StopRunning(string exe)
    {
        foreach (var p in Process.GetProcessesByName("Utylix"))
        {
            try
            {
                if (p.Id != Environment.ProcessId && string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(5000); }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            finally { p.Dispose(); }
        }
    }

    private static void MakeShortcut(string path, string exe)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type == null) return;
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = exe;
        link.WorkingDirectory = Path.GetDirectoryName(exe);
        link.Description = "Utylix";
        link.IconLocation = exe + ",0";
        link.Save();
    }

    // ------------------------------------------------------------------------------------------------ uninstall

    /// <summary>Removes Utylix from this PC (asks first). Run by the installed copy with --uninstall.</summary>
    public static void Uninstall()
    {
        string self = Environment.ProcessPath!;
        bool allUsers = Installed() is { AllUsers: true } i && SameFolder(i.Dir, Path.GetDirectoryName(self));
        if (allUsers && !IsAdmin)
        {
            // installed for everyone: removing it needs administrator rights
            try { Process.Start(new ProcessStartInfo(self) { UseShellExecute = true, Verb = "runas", ArgumentList = { "--uninstall" } }); }
            catch (System.ComponentModel.Win32Exception) { MessageBox.Show("Administrator permission was not given, so Utylix was not removed.", "Uninstall Utylix", MessageBoxButton.OK, MessageBoxImage.Information); }
            return;
        }
        if (MessageBox.Show("Remove Utylix from this PC?\n\nYour downloaded files, recordings and screenshots stay where they are.",
                "Uninstall Utylix", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        bool wipe = MessageBox.Show("Also delete Utylix's own settings, download list and the video tools it downloaded (yt-dlp, ffmpeg, the player engine, the AI model)?\n\n" +
                                    "Choose No to keep them in case you install Utylix again.", "Uninstall Utylix", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        // a progress window like the installer's, paced so each step can be followed
        var bar = new ProgressBar { Height = 10, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 18, 0, 0), Foreground = (Brush)Application.Current.FindResource("AccentBrush") };
        var status = new TextBlock { Text = "Getting ready…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), MinHeight = 20 };
        var close = new Button { Content = "Close", Style = (Style)Application.Current.FindResource("DialogPrimary"), IsDefault = true, IsCancel = true, MinWidth = 100, IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(status, "UninstallStatus");
        System.Windows.Automation.AutomationProperties.SetAutomationId(bar, "UninstallProgress");
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/logo.png")), Width = 44, Height = 44, Margin = new Thickness(0, 0, 14, 0) });
        head.Children.Add(new TextBlock { Text = "Removing Utylix", FontSize = 22, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var panel = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
        panel.Children.Add(head); panel.Children.Add(bar); panel.Children.Add(status); panel.Children.Add(close);
        var window = new Window
        {
            Title = "Uninstall Utylix", Width = 520, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = panel, Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/logo.png")),
            Background = (Brush)Application.Current.FindResource("BgBrush"), Foreground = (Brush)Application.Current.FindResource("TextBrush"), FontFamily = new FontFamily("Segoe UI"), FontSize = 13.5,
        };
        close.Click += (_, _) => window.Close();
        string? cleanup = null;
        window.Loaded += async (_, _) =>
        {
            var progress = new Progress<(double Percent, string Text)>(p => { bar.Value = p.Percent; status.Text = p.Text; });
            cleanup = await Task.Run(() => RemoveEverything(self, allUsers, wipe, progress));
            bar.Value = 100;
            status.Text = "Done. Utylix has been removed from this PC.";
            close.IsEnabled = true;
        };
        window.ShowDialog();
        // the program ends right after this: a helper waits a moment, then removes the exe and its folder (if empty)
        if (cleanup != null) Process.Start(new ProcessStartInfo("cmd.exe", cleanup) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
    }

    /// <returns>The command that deletes this exe once the program has ended (started when the window is closed).</returns>
    private static string RemoveEverything(string self, bool allUsers, bool wipe, IProgress<(double, string)> progress)
    {
        string dataDir = App.DataDir;
        progress.Report((5, "Closing Utylix…")); StopRunning(self); Thread.Sleep(700);
        progress.Report((20, "Removing Explorer's right-click entries and file types…"));
        try
        {
            ShellMenu.Register(dataDir, false);
            ShellMenu.RegisterArchive(dataDir, false);
            ShellMenu.RegisterBackground(dataDir, false);
            ShellMenu.RegisterPlayer(dataDir, false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        Thread.Sleep(800);
        progress.Report((40, "Removing the browser link…"));
        try { NativeHost.Register(dataDir, false); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        Thread.Sleep(700);
        progress.Report((55, "Turning off Start with Windows…"));
        try { using var run = Root(allUsers).CreateSubKey(RunKey); run.DeleteValue("Utylix", false); run.DeleteValue("IDMClone", false); } catch (Exception) { }
        if (allUsers) try { using var userRun = Registry.CurrentUser.CreateSubKey(RunKey); userRun.DeleteValue("Utylix", false); } catch (Exception) { }
        Thread.Sleep(700);
        progress.Report((68, "Removing the shortcuts…"));
        foreach (var link in new[] { StartMenuLink(allUsers), DesktopLink(allUsers) }) try { File.Delete(link); } catch (Exception) { }
        Thread.Sleep(700);
        progress.Report((80, "Removing Utylix from Windows' list of apps…"));
        try { Root(allUsers).DeleteSubKeyTree(UninstallKey, false); } catch (Exception) { }
        Thread.Sleep(600);
        if (wipe)
        {
            progress.Report((88, "Deleting Utylix's settings and tools…"));
            try { Directory.Delete(dataDir, true); } catch (Exception) { }
            Thread.Sleep(600);
        }
        if (FanTask.Exists())
        {
            progress.Report((90, "Removing the fan control helper (Windows asks for permission)…"));
            try { FanTask.RemoveWithPromptAsync().GetAwaiter().GetResult(); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        progress.Report((95, "Removing the program files…"));
        // this exe cannot delete itself while it runs: a helper waits a moment, removes it and the folder (only if empty)
        string dir = Path.GetDirectoryName(self)!;
        string cmd = $"/c ping -n 4 127.0.0.1 >nul & del /f /q \"{self}\" \"{self}.old\" \"{self}.update\" & rmdir \"{dir}\"";
        Thread.Sleep(700);
        return cmd;
    }
}
