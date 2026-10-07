using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Drivers: save the drivers of this PC (all of them, or just network + Bluetooth) to a folder or a USB drive, put them back after a format (Windows asks for permission once)
/// and see which devices have no driver. Built so a PC with no Wi-Fi can get its network driver from a USB drive that holds Utylix-Setup.exe and a drivers folder.
/// Nothing is downloaded; Windows still refuses unsigned drivers.
/// </summary>
internal sealed class DriversWindow : Window
{
    private static DriversWindow? _open;

    private readonly TextBlock _now = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _saveSummary = new() { Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBox _saveFolder = new() { Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _saveStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _save = new() { Content = "Save the drivers", MinWidth = 150, Margin = new Thickness(0, 10, 10, 0) };
    private readonly Button _openSaved = new() { Content = "Open the folder", Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBox _installFolder = new() { Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _installSummary = new() { Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _installStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _install = new() { Content = "Install the drivers", MinWidth = 170, Margin = new Thickness(0, 10, 0, 0) };
    private readonly StackPanel _problems = new();
    private readonly TextBlock _problemsHead = new() { Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly List<(RadioButton Chip, bool NetworkOnly)> _saveChoice = new(), _installChoice = new();
    private List<DriverPackage> _packages = new();
    private List<DriverProblem> _problemList = new();
    private List<InfFile> _infs = new();
    private bool _busy;

    private static string LastFolderFile => Path.Combine(App.DataDir, "drivers-folder.txt");

    public static void ShowPanel()
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new DriversWindow();
        _open.Show();
    }

    private DriversWindow()
    {
        Title = "Drivers - Utylix";
        Width = 720; Height = 800; MinWidth = 600; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13.5;
        WindowTheme.DarkTitleBar(this);
        var chipStyle = (Style)Application.Current.FindResource("ChipButton");
        var primary = (Style)Application.Current.FindResource("DialogPrimary");
        var plain = (Style)Application.Current.FindResource("DialogButton");
        _save.Style = primary; _install.Style = primary; _openSaved.Style = plain;
        foreach (var box in new[] { _saveFolder, _installFolder })
        {
            box.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            box.Background = new SolidColorBrush(Color.FromArgb(34, 128, 128, 128));
            box.SetResourceReference(Control.BorderBrushProperty, "MutedBrush");
        }

        string Id(string id, DependencyObject o) { AutomationProperties.SetAutomationId(o, id); return id; }
        Border Card(string title, string? hint, params UIElement[] content)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold });
            if (hint != null) stack.Children.Add(new TextBlock { Text = hint, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) });
            foreach (var c in content) stack.Children.Add(c);
            var border = new Border { Padding = new Thickness(16, 14, 16, 16), Margin = new Thickness(0, 0, 0, 14), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = stack };
            border.SetResourceReference(Border.BorderBrushProperty, "MutedBrush");
            border.Background = new SolidColorBrush(Color.FromArgb(20, 128, 128, 128));
            return border;
        }
        WrapPanel Chips(List<(RadioButton, bool)> into, string group, string idPrefix, string netLabel, string allLabel, Action changed)
        {
            var panel = new WrapPanel();
            var net = new RadioButton { Content = netLabel, Style = chipStyle, GroupName = group, IsChecked = true };
            var all = new RadioButton { Content = allLabel, Style = chipStyle, GroupName = group };
            Id(idPrefix + "Network", net); Id(idPrefix + "All", all);
            net.Checked += (_, _) => changed(); all.Checked += (_, _) => changed();
            into.Add((net, true)); into.Add((all, false));
            panel.Children.Add(net); panel.Children.Add(all);
            return panel;
        }
        Grid FolderRow(TextBox box, string id, string chooseId, Action choose, Action? extra = null)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Id(id, box);
            var b = new Button { Content = "Choose…", Style = plain, Margin = new Thickness(8, 0, 0, 0) };
            Id(chooseId, b);
            b.Click += (_, _) => choose();
            Grid.SetColumn(b, 1);
            row.Children.Add(box); row.Children.Add(b);
            return row;
        }

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock { Text = "Drivers", FontSize = 22, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock { Text = "Save this PC's drivers to a folder or a USB drive, and put them back after a format, so you don't have to hunt for a Wi-Fi driver with no internet. Nothing is downloaded; Windows refuses a driver that isn't signed.", Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 16) });

        Id("DrvNow", _now);
        root.Children.Add(Card("This PC now", null, _now));

        // ---- save ----
        var saveChips = Chips(_saveChoice, "drvsave", "DrvSave", "Network and Bluetooth only", "Every driver that isn't part of Windows", UpdateSaveSummary);
        _saveFolder.Text = DefaultSaveFolder();
        Id("DrvSaveFolder", _saveFolder); Id("DrvSave", _save); Id("DrvSaveStatus", _saveStatus); Id("DrvSaveSummary", _saveSummary); Id("DrvOpenSaved", _openSaved);
        var saveButtons = new StackPanel { Orientation = Orientation.Horizontal };
        saveButtons.Children.Add(_save); saveButtons.Children.Add(_openSaved);
        root.Children.Add(Card("Save my drivers", "Do this on a PC that works, before the format. Put the folder on a USB drive next to Utylix-Setup.exe.",
            saveChips, _saveSummary, new TextBlock { Text = "Save them in", Margin = new Thickness(0, 10, 0, 0), FontWeight = FontWeights.SemiBold },
            FolderRow(_saveFolder, "DrvSaveFolder", "DrvSaveChoose", ChooseSaveFolder), saveButtons, _saveStatus));

        // ---- install ----
        var installChips = Chips(_installChoice, "drvinst", "DrvInstall", "Network and Bluetooth only", "All of them", UpdateInstallSummary);
        try { if (File.Exists(LastFolderFile)) _installFolder.Text = File.ReadAllText(LastFolderFile).Trim(); } catch (IOException) { }
        if (_installFolder.Text.Length == 0) _installFolder.Text = _saveFolder.Text;
        Id("DrvInstallFolder", _installFolder); Id("DrvInstall", _install); Id("DrvInstallStatus", _installStatus); Id("DrvInstallSummary", _installSummary);
        _installFolder.LostFocus += (_, _) => ScanInstallFolder();
        _installFolder.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) ScanInstallFolder(); };
        root.Children.Add(Card("Install drivers from a folder", "On the PC that needs them (after a format). Windows asks for permission once. Restart afterwards if it says so.",
            installChips, FolderRow(_installFolder, "DrvInstallFolder", "DrvInstallChoose", ChooseInstallFolder), _installSummary, _install, _installStatus));

        // ---- what is missing ----
        var refresh = new Button { Content = "Check again", Style = plain, Margin = new Thickness(0, 4, 8, 0) };
        var copy = new Button { Content = "Copy the hardware IDs", Style = plain, Margin = new Thickness(0, 4, 0, 0) };
        Id("DrvRefresh", refresh); Id("DrvCopyIds", copy); Id("DrvProblems", _problems);
        refresh.Click += async (_, _) => await LoadAsync();
        copy.Click += (_, _) =>
        {
            if (_problemList.Count == 0) return;
            try { Clipboard.SetText(string.Join(Environment.NewLine, _problemList.Select(p => p.Name + "  -  " + p.HardwareId))); _saveStatus.Text = ""; _problemsHead.Text = "Copied. Search these IDs on the maker's site (or Microsoft Update Catalog) from a PC with internet."; }
            catch (System.Runtime.InteropServices.ExternalException) { _problemsHead.Text = "The clipboard is busy, try again."; }
        };
        var missingButtons = new StackPanel { Orientation = Orientation.Horizontal };
        missingButtons.Children.Add(refresh); missingButtons.Children.Add(copy);
        root.Children.Add(Card("What this PC is missing", "Devices Windows lists with a problem, usually because no driver is installed.", _problemsHead, _problems, missingButtons));

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
        _save.Click += async (_, _) => await SaveAsync();
        _install.Click += async (_, _) => await InstallAsync();
        _openSaved.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(_saveFolder.Text.Trim()) { UseShellExecute = true }); } catch (Exception e) when (e is Win32Exception or InvalidOperationException) { } };
        Closed += (_, _) => _open = null;
        Loaded += async (_, _) => { await LoadAsync(); ScanInstallFolder(); };
    }

    /// <summary>A USB drive if one is plugged in (that is where the drivers are wanted), else a local folder that OneDrive doesn't copy to the cloud.</summary>
    private static string DefaultSaveFolder()
    {
        try
        {
            var usb = DriveInfo.GetDrives().FirstOrDefault(d => d.DriveType == DriveType.Removable && d.IsReady);
            if (usb != null) return Path.Combine(usb.RootDirectory.FullName, "Utylix drivers", Environment.MachineName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "Utylix drivers", Environment.MachineName);
    }

    private bool SaveNetworkOnly => _saveChoice.First(c => c.Chip.IsChecked == true).NetworkOnly;
    private bool InstallNetworkOnly => _installChoice.First(c => c.Chip.IsChecked == true).NetworkOnly;

    private async Task LoadAsync()
    {
        _now.Text = "Reading this PC…";
        try
        {
            var (packages, cards, problems) = await Task.Run(() => (DriverKit.Installed(), DriverKit.NetworkCards(), DriverKit.Problems()));
            _packages = packages; _problemList = problems;
            var lines = new List<string>();
            foreach (var c in cards) lines.Add((c.IsWifi ? "Wi-Fi: " : "Network: ") + c.Name + (c.Enabled ? "  (working)" : "  (not connected or turned off)"));
            if (!cards.Any(c => c.IsWifi)) lines.Add("No Wi-Fi card found working" + (problems.Any(p => p.Name.Contains("network", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("wi-fi", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("wireless", StringComparison.OrdinalIgnoreCase)) ? " (one is listed below as missing its driver)." : " (this PC may have none, or its driver is missing)."));
            lines.Add($"{packages.Count} driver package{(packages.Count == 1 ? "" : "s")} that aren't part of Windows are installed.");
            _now.Text = string.Join(Environment.NewLine, lines);
            UpdateSaveSummary();
            RenderProblems();
        }
        catch (Exception e) when (e is ManagementException or System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            _now.Text = "Couldn't read this PC's drivers: " + e.Message;
        }
    }

    private void RenderProblems()
    {
        _problems.Children.Clear();
        _problemsHead.Text = _problemList.Count == 0 ? "Nothing is missing: every device has its driver." : $"{_problemList.Count} device{(_problemList.Count == 1 ? "" : "s")} need attention:";
        foreach (var p in _problemList)
        {
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4), ToolTip = p.HardwareId };
            line.Inlines.Add(new Run(p.Name) { FontWeight = FontWeights.SemiBold });
            line.Inlines.Add(new Run("  -  " + p.Why + "\n" + p.HardwareId) { Foreground = new SolidColorBrush(Color.FromArgb(170, 128, 128, 128)), FontSize = 12 });
            _problems.Children.Add(line);
        }
    }

    // ---------- save ----------
    private List<DriverPackage> ChosenToSave() => _packages.Where(p => !SaveNetworkOnly || p.IsNetwork).ToList();

    private void UpdateSaveSummary()
    {
        var chosen = ChosenToSave();
        _saveSummary.Text = chosen.Count == 0
            ? (SaveNetworkOnly ? "No network or Bluetooth driver of the PC is installed from outside Windows (Windows' own drivers can't be saved)." : "Nothing to save.")
            : $"{chosen.Count} driver package{(chosen.Count == 1 ? "" : "s")}: " + string.Join("; ", chosen.Select(p => p.Devices.FirstOrDefault() ?? p.Provider).Distinct().Take(6)) + (chosen.Count > 6 ? "; …" : "");
        _save.IsEnabled = !_busy && chosen.Count > 0;
    }

    private void ChooseSaveFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Where should the drivers be saved? (a USB drive is a good place)" };
        if (dlg.ShowDialog(this) == true) _saveFolder.Text = dlg.FolderName;
    }

    private async Task SaveAsync()
    {
        string dest = _saveFolder.Text.Trim();
        var chosen = ChosenToSave();
        if (chosen.Count == 0) return;
        if (dest.Length == 0 || !Path.IsPathRooted(dest) || dest.Contains('"')) { _saveStatus.Text = "Choose a folder to save them in."; return; }
        _busy = true; _save.IsEnabled = false; _openSaved.Visibility = Visibility.Collapsed;
        _saveStatus.Text = "Saving…";
        try
        {
            int saved = await Task.Run(() =>
            {
                int n = DriverKit.Export(dest, chosen, null, CancellationToken.None);
                DriverKit.WriteNote(dest, chosen);
                return n;
            });
            _saveStatus.Text = saved == chosen.Count ? $"Saved {saved} driver package{(saved == 1 ? "" : "s")} in {dest}." : $"Saved {saved} of {chosen.Count} driver packages in {dest}. The others couldn't be exported.";
            _openSaved.Visibility = Visibility.Visible;
            try { File.WriteAllText(LastFolderFile, dest); } catch (IOException) { }
            _installFolder.Text = dest; ScanInstallFolder();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception)
        {
            _saveStatus.Text = "Couldn't save them: " + e.Message;
        }
        finally { _busy = false; UpdateSaveSummary(); }
    }

    // ---------- install ----------
    private void ChooseInstallFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "The folder with the saved drivers" };
        if (dlg.ShowDialog(this) == true) { _installFolder.Text = dlg.FolderName; ScanInstallFolder(); }
    }

    private void ScanInstallFolder()
    {
        string folder = _installFolder.Text.Trim();
        _infs = folder.Length > 0 ? DriverKit.FindInfs(folder) : new List<InfFile>();
        UpdateInstallSummary();
    }

    private void UpdateInstallSummary()
    {
        string folder = _installFolder.Text.Trim();
        int net = _infs.Count(i => i.IsNetwork), count = InstallNetworkOnly ? net : _infs.Count;
        if (folder.Length == 0) _installSummary.Text = "Choose the folder with the saved drivers.";
        else if (!Directory.Exists(folder)) _installSummary.Text = "That folder isn't there.";
        else if (_infs.Count == 0) _installSummary.Text = "No driver files (.inf) in that folder.";
        else _installSummary.Text = $"Found {_infs.Count} driver file{(_infs.Count == 1 ? "" : "s")}, {net} for network or Bluetooth. {(InstallNetworkOnly ? "Will install the " + net + " network / Bluetooth one" + (net == 1 ? "" : "s") + "." : "Will install all " + _infs.Count + ".")}";
        _install.IsEnabled = !_busy && count > 0;
    }

    private static bool IsElevated()
    {
        try { using var id = System.Security.Principal.WindowsIdentity.GetCurrent(); return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }
        catch (Exception e) when (e is System.Security.SecurityException) { return false; }
    }

    private async Task InstallAsync()
    {
        string folder = _installFolder.Text.Trim();
        ScanInstallFolder();
        int count = InstallNetworkOnly ? _infs.Count(i => i.IsNetwork) : _infs.Count;
        if (count == 0) return;
        bool netOnly = InstallNetworkOnly;
        _busy = true; _install.IsEnabled = false;
        _installStatus.Text = IsElevated() ? "Installing…" : "Waiting for Windows' permission…";
        int before = _packages.Count;
        try
        {
            try { File.WriteAllText(LastFolderFile, folder); } catch (IOException) { }
            int code;
            if (IsElevated()) code = await Task.Run(() => DriverKit.RunInstall(folder, netOnly));
            else
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                psi.ArgumentList.Add("--drivers-install"); psi.ArgumentList.Add(folder); psi.ArgumentList.Add(netOnly ? "1" : "0");
                using var p = Process.Start(psi);
                if (p == null) { _installStatus.Text = "Windows didn't start the installer."; return; }
                _installStatus.Text = "Installing… (Windows is putting the drivers in)";
                await p.WaitForExitAsync();
                code = p.ExitCode;
            }
            await LoadAsync();
            int after = _packages.Count;
            _installStatus.Text = code == 0
                ? $"Done: {count} driver file{(count == 1 ? "" : "s")} went into Windows ({after - before} new package{(after - before == 1 ? "" : "s")}; the others were there already). Restart if a device still doesn't work."
                : code == 2 ? "That folder can't be used."
                : "Some drivers could not be installed (Windows refuses drivers that aren't signed, or that don't fit this PC's devices). The rest went in. See \"What this PC is missing\" below.";
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { _installStatus.Text = "Windows' permission was not given, so nothing was installed."; }
        catch (Exception e) when (e is IOException or InvalidOperationException or Win32Exception) { _installStatus.Text = "Couldn't install: " + e.Message; }
        finally { _busy = false; UpdateInstallSummary(); }
    }
}
