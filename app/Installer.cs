using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IdmClone.Engine;
using Microsoft.Win32;

namespace IdmClone;

/// <summary>
/// Utylix installs itself: the setup program IS Utylix.exe (started as "Utylix-Setup.exe", with --setup, or the first time on a PC
/// where Utylix has never run). It copies itself into the user's programs folder, adds Start menu / desktop shortcuts, Start with
/// Windows and an entry in "Installed apps", and starts the installed copy. No administrator rights and no separate runtime needed.
/// The installed copy removes everything again with --uninstall.
/// </summary>
internal static class Installer
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Utylix";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ExeName = "Utylix.exe";

    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Utylix");
    private static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Utylix.lnk");
    private static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Utylix.lnk");

    /// <summary>The folder Utylix was installed into, or null.</summary>
    public static string? InstalledDir()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(UninstallKey); return k?.GetValue("InstallLocation") as string; }
        catch (Exception e) when (e is System.Security.SecurityException or IOException) { return null; }
    }

    private static bool IsInstalledCopy =>
        InstalledDir() is { } dir && string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetDirectoryName(Environment.ProcessPath)?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Should this start show the installer instead of the program?</summary>
    public static bool WantsSetup(string[] args)
    {
        if (args.Contains("--setup")) return true;
        if (args.Length != 0 || IsInstalledCopy) return false;
        string name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        if (name.Contains("setup", StringComparison.OrdinalIgnoreCase)) return true;
        // a PC where Utylix has never run, started by double-click: offer to install it
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return !File.Exists(Path.Combine(appData, "Utylix", "config.json")) && !File.Exists(Path.Combine(appData, "IDMClone", "config.json"));
    }

    // ------------------------------------------------------------------------------------------------ setup window

    /// <summary>Shows the setup window. Returns true when the app should carry on running from where it is (the "just run it" choice).</summary>
    public static bool RunSetup()
    {
        bool runHere = false;
        var dirBox = new TextBox { Text = InstalledDir() ?? DefaultDir, Style = (Style)Application.Current.FindResource("Field"), Margin = new Thickness(0, 6, 8, 0) };
        var browse = new Button { Content = "Browse…", Style = (Style)Application.Current.FindResource("DialogButton"), Margin = new Thickness(0, 6, 0, 0) };
        var desktop = new CheckBox { Content = "Put a Utylix shortcut on the desktop", Margin = new Thickness(0, 14, 0, 0), IsChecked = false };
        var autostart = new CheckBox { Content = "Start Utylix with Windows (it waits quietly in the tray)", Margin = new Thickness(0, 8, 0, 0), IsChecked = true };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0) };
        var install = new Button { Content = "Install", Style = (Style)Application.Current.FindResource("DialogPrimary"), IsDefault = true, MinWidth = 100 };
        var portable = new Button { Content = "Just run it, don't install", Style = (Style)Application.Current.FindResource("DialogButton"), Margin = new Thickness(0, 0, 10, 0) };
        var cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.FindResource("DialogButton"), IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(dirBox, "SetupDir");
        System.Windows.Automation.AutomationProperties.SetAutomationId(install, "SetupInstall");
        System.Windows.Automation.AutomationProperties.SetAutomationId(status, "SetupStatus");
        System.Windows.Automation.AutomationProperties.SetAutomationId(desktop, "SetupDesktop");
        System.Windows.Automation.AutomationProperties.SetAutomationId(autostart, "SetupAutostart");
        System.Windows.Automation.AutomationProperties.SetAutomationId(portable, "SetupPortable");

        var pathRow = new Grid();
        pathRow.ColumnDefinitions.Add(new ColumnDefinition());
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathRow.Children.Add(dirBox);
        Grid.SetColumn(browse, 1);
        pathRow.Children.Add(browse);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(portable); buttons.Children.Add(cancel); buttons.Children.Add(install);

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/logo.png")), Width = 44, Height = 44, Margin = new Thickness(0, 0, 14, 0) });
        head.Children.Add(new TextBlock { Text = "Install Utylix", FontSize = 22, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        var panel = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
        panel.Children.Add(head);
        panel.Children.Add(new TextBlock
        {
            Text = "Downloads, converter, screen capture and recorder, video player, archives, background remover and brightness - in one program. " +
                   "It installs just for you: no administrator rights needed.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0), Foreground = (Brush)Application.Current.FindResource("MutedBrush"),
        });
        panel.Children.Add(new TextBlock { Text = "Install into", Margin = new Thickness(0, 18, 0, 0) });
        panel.Children.Add(pathRow);
        panel.Children.Add(desktop);
        panel.Children.Add(autostart);
        panel.Children.Add(status);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = "Utylix Setup", Width = 560, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = panel, Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/logo.png")),
            Background = (Brush)Application.Current.FindResource("BgBrush"), Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 13.5,
        };
        string? installedExe = null;
        browse.Click += (_, _) =>
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose the folder to install Utylix into", UseDescriptionForTitle = true };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) dirBox.Text = Path.Combine(dlg.SelectedPath, "Utylix");
        };
        portable.Click += (_, _) => { runHere = true; window.Close(); };
        install.Click += async (_, _) =>
        {
            if (installedExe != null) { Launch(installedExe); window.Close(); return; }
            install.IsEnabled = portable.IsEnabled = cancel.IsEnabled = dirBox.IsEnabled = browse.IsEnabled = false;
            status.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            status.Text = "Installing…";
            string dir = dirBox.Text.Trim(); bool wantDesktop = desktop.IsChecked == true, wantAuto = autostart.IsChecked == true;
            try
            {
                installedExe = await Task.Run(() => Install(dir, wantDesktop, wantAuto));
                status.Text = "Utylix is installed. Explorer's right-click entries and file types are set up when it starts.";
                install.Content = "Open Utylix"; install.IsEnabled = true; cancel.Content = "Close"; cancel.IsEnabled = true;
                portable.Visibility = Visibility.Collapsed;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                status.SetResourceReference(TextBlock.ForegroundProperty, "ErrBrush");
                status.Text = "Couldn't install: " + e.Message;
                install.IsEnabled = portable.IsEnabled = cancel.IsEnabled = dirBox.IsEnabled = browse.IsEnabled = true;
            }
        };
        window.ShowDialog();
        return runHere;
    }

    /// <summary>Copies this program into <paramref name="dir"/> and sets everything up. Returns the path of the installed exe.</summary>
    public static string Install(string dir, bool desktopShortcut, bool autoStart)
    {
        if (string.IsNullOrWhiteSpace(dir)) throw new ArgumentException("Choose a folder.");
        dir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dir)).TrimEnd('\\');
        string self = Environment.ProcessPath ?? throw new IOException("Utylix cannot tell where it is.");
        string exe = Path.Combine(dir, ExeName);
        Directory.CreateDirectory(dir);

        if (!string.Equals(Path.GetFullPath(self), exe, StringComparison.OrdinalIgnoreCase))
        {
            StopRunning(exe);
            string tmp = exe + ".new";
            File.Copy(self, tmp, true);
            File.Move(tmp, exe, true);
        }

        MakeShortcut(StartMenuLink, exe);
        if (desktopShortcut) MakeShortcut(DesktopLink, exe);
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (autoStart) run.SetValue("Utylix", $"\"{exe}\" --minimized"); else run.DeleteValue("Utylix", false);
        }
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
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
        return exe;
    }

    private static void Launch(string exe)
    {
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! }); }
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
        if (MessageBox.Show("Remove Utylix from this PC?\n\nYour downloaded files, recordings and screenshots stay where they are.",
                "Uninstall Utylix", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        bool wipe = MessageBox.Show("Also delete Utylix's own settings, download list and the video tools it downloaded (yt-dlp, ffmpeg, the player engine, the AI model)?\n\n" +
                                    "Choose No to keep them in case you install Utylix again.", "Uninstall Utylix", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        string self = Environment.ProcessPath!;
        StopRunning(self);
        string dataDir = App.DataDir;
        try
        {
            ShellMenu.Register(dataDir, false);
            ShellMenu.RegisterArchive(dataDir, false);
            ShellMenu.RegisterBackground(dataDir, false);
            ShellMenu.RegisterPlayer(dataDir, false);
            NativeHost.Register(dataDir, false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        try { using var run = Registry.CurrentUser.CreateSubKey(RunKey); run.DeleteValue("Utylix", false); run.DeleteValue("IDMClone", false); } catch (Exception) { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch (Exception) { }
        foreach (var link in new[] { StartMenuLink, DesktopLink }) try { File.Delete(link); } catch (Exception) { }
        if (wipe) try { Directory.Delete(dataDir, true); } catch (Exception) { }

        // this exe cannot delete itself while it runs: a helper waits a moment, removes it and the folder (only if empty)
        string dir = Path.GetDirectoryName(self)!;
        string cmd = $"/c ping -n 4 127.0.0.1 >nul & del /f /q \"{self}\" \"{self}.old\" \"{self}.update\" & rmdir \"{dir}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        MessageBox.Show("Utylix has been removed.", "Uninstall Utylix", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
