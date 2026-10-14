using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Memory limits: choose a program (for example cabal.exe), give it a limit in MB, and Utylix trims it - or frees everything, deeply - whenever it
/// goes above that. The watching is done by <see cref="MemoryGuard"/>: only while the list has something on, one check every five seconds.
/// </summary>
internal sealed class MemoryLimitsWindow : Window
{
    private static MemoryLimitsWindow? _open;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _allow;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly List<MemoryRule> _rules;
    private readonly Dictionary<MemoryRule, TextBlock> _now = new();
    private readonly Dictionary<MemoryRule, TextBlock> _inst = new();
    private readonly Dictionary<string, (TimeSpan Cpu, DateTime At)> _cpuBefore = new(StringComparer.OrdinalIgnoreCase);
    private bool _loading;
    private DateTime _lastEnsure = DateTime.MinValue;

    public static void ShowPanel()
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new MemoryLimitsWindow();
        _open.Show();
    }

    private MemoryLimitsWindow()
    {
        Title = "Memory limits - Utylix";   // (also the CPU settings: kept under this name so the tile, the tray item and --memory-limits stay the same)
        Width = 940; Height = 640; MinWidth = 820; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13.5;
        WindowTheme.DarkTitleBar(this);
        var plain = (Style)Application.Current.FindResource("DialogButton");
        var primary = (Style)Application.Current.FindResource("DialogPrimary");

        _rules = MemoryLimits.Load(App.DataDir);

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        root.Children.Add(new TextBlock { Text = "Memory and CPU limits", FontSize = 22, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock
        {
            Text = "Pick a program. Memory: when it uses more than the limit, Utylix gives back what it is not using right now (nothing is closed); a limit that is too low only makes a game stutter, start with 500 MB or more, 0 = no memory limit. " +
                   "CPU: lower its priority, put it in efficiency mode, or cap the share of the whole processor it may use. Nothing is closed.",
            Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14),
        });

        var header = new Grid { Margin = new Thickness(4, 0, 4, 4) };
        foreach (var w in new[] { 34.0, 0, 150, 100, 120, 160, 100 })
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        void Head(string text, int col) { var t = new TextBlock { Text = text, Opacity = 0.65, FontSize = 12 }; Grid.SetColumn(t, col); header.Children.Add(t); }
        Head("On", 0); Head("Program", 1); Head("Using now", 2); Head("Memory (MB)", 3); Head("When over", 4); Head("CPU", 5);
        root.Children.Add(header);
        root.Children.Add(_rows);

        var add = new Button { Content = "Add a program…", Style = primary, MinWidth = 160, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(add, "MemLimitAdd");
        add.Click += (_, _) => ShowAddMenu(add);
        root.Children.Add(add);

        AutomationProperties.SetAutomationId(_state, "MemLimitState");
        root.Children.Add(_state);
        _allow = new Button { Content = "Allow it to limit programs that run as administrator (asks once)", Style = plain, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(_allow, "MemLimitAllow");
        _allow.Click += (_, _) => AllowAdministrator();
        root.Children.Add(_allow);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        BuildRows();
        Refresh();
        _tick.Tick += (_, _) => Refresh();
        _tick.Start();
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        Closed += (_, _) => { _tick.Stop(); if (_saveTimer.IsEnabled) { _saveTimer.Stop(); SaveNow(); } _open = null; };
    }

    private void SaveNow()
    {
        MemoryLimits.Save(App.DataDir, _rules);
        MemoryGuard.Ensure(App.DataDir);                       // (starts the guard when there is something to watch; it reads the file by itself)
        Refresh();
    }

    private void Changed() { if (!_loading) { _saveTimer.Stop(); _saveTimer.Start(); } }

    private void BuildRows()
    {
        _loading = true;
        _rows.Children.Clear();
        _now.Clear();
        _inst.Clear();
        if (_rules.Count == 0)
        {
            _rows.Children.Add(new TextBlock { Text = "No program yet. Choose \"Add a program…\", for example Cabal's cabal.exe.", Opacity = 0.65, Margin = new Thickness(4, 8, 0, 8), TextWrapping = TextWrapping.Wrap });
        }
        foreach (var rule in _rules)
        {
            var r = rule;
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4), Background = new SolidColorBrush(Color.FromArgb(20, 128, 128, 128)) };
            foreach (var w in new[] { 34.0, 0, 150, 100, 120, 160, 100 })
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
            var on = new CheckBox { IsChecked = r.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Watch this program" };
            on.Click += (_, _) => { r.Enabled = on.IsChecked == true; Changed(); };
            var name = new TextBlock { Text = r.Exe, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 8, 6, 8) };
            var now = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
            _now[r] = now;
            var limit = new TextBox { Text = r.LimitMb.ToString(), Width = 90, Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            limit.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            limit.Background = new SolidColorBrush(Color.FromArgb(34, 128, 128, 128));
            limit.SetResourceReference(Control.BorderBrushProperty, "MutedBrush");
            AutomationProperties.SetAutomationId(limit, "MemLimitMb_" + r.Exe);
            limit.TextChanged += (_, _) =>
            {
                if (int.TryParse(limit.Text.Trim(), out int mb) && (mb == 0 || (mb >= 20 && mb <= 65536))) { r.LimitMb = mb; limit.ClearValue(Control.ForegroundProperty); limit.SetResourceReference(Control.ForegroundProperty, "TextBrush"); Changed(); }
                else limit.Foreground = Brushes.IndianRed;
            };
            var mode = new ComboBox { Width = 118, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            mode.Items.Add("Trim it"); mode.Items.Add("Deep clean");
            mode.SelectedIndex = r.Deep ? 1 : 0;
            mode.ToolTip = "Trim it: only this program gives back memory.\nDeep clean: every program, and Windows' cached memory too (needs administrator rights).";
            mode.SelectionChanged += (_, _) => { r.Deep = mode.SelectedIndex == 1; Changed(); };
            var removeText = new TextBlock { Text = "Remove" };
            removeText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var cpu = new ComboBox { Width = 150, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            var caps = new[] { 75, 50, 25, 10 };
            foreach (string item in new[] { "No change", "Lower priority", "Efficiency mode", "Cap at 75 %", "Cap at 50 %", "Cap at 25 %", "Cap at 10 %" }) cpu.Items.Add(item);
            cpu.SelectedIndex = r.Cpu switch { 1 => 1, 2 => 2, 3 => 3 + Math.Max(0, Array.IndexOf(caps, caps.OrderBy(c => Math.Abs(c - r.CapPercent)).First())), _ => 0 };
            cpu.ToolTip = "Lower priority: other programs get the processor first." + Environment.NewLine + "Efficiency mode: idle priority and the power-saving side of the CPU." + Environment.NewLine + "Cap: the most of the WHOLE processor it may use (a hard limit).";
            AutomationProperties.SetAutomationId(cpu, "MemLimitCpu_" + r.Exe);
            cpu.SelectionChanged += (_, _) =>
            {
                int i = cpu.SelectedIndex;
                r.Cpu = i <= 0 ? 0 : i >= 3 ? 3 : i;
                if (i >= 3) r.CapPercent = caps[i - 3];
                Changed();
            };
            var remove = new Button { Content = removeText, Style = (Style)Application.Current.FindResource("DialogButton"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 10, 0), MinWidth = 76, VerticalAlignment = VerticalAlignment.Center };
            remove.Click += (_, _) => { _rules.Remove(r); BuildRows(); Changed(); };
            Grid.SetColumn(on, 0); Grid.SetColumn(name, 1); Grid.SetColumn(now, 2); Grid.SetColumn(limit, 3); Grid.SetColumn(mode, 4); Grid.SetColumn(cpu, 5); Grid.SetColumn(remove, 6);
            foreach (UIElement u in new UIElement[] { on, name, now, limit, mode, cpu, remove }) row.Children.Add(u);
            // under the row: only the window whose title contains ... (tells several clients of one game apart), and the copies that are running now
            var titleLabel = new TextBlock { Text = "Only the window whose title contains:", Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var titleBox = new TextBox { Text = r.Title ?? "", Width = 260, Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center };
            titleBox.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            titleBox.Background = new SolidColorBrush(Color.FromArgb(34, 128, 128, 128));
            titleBox.SetResourceReference(Control.BorderBrushProperty, "MutedBrush");
            titleBox.ToolTip = "Leave empty to use this row for every copy of the program. Type part of a window title to use it only for that client.";
            AutomationProperties.SetAutomationId(titleBox, "MemLimitTitle_" + r.Exe);
            titleBox.TextChanged += (_, _) => { r.Title = titleBox.Text; Changed(); };
            var titleLine = new StackPanel { Orientation = Orientation.Horizontal };
            titleLine.Children.Add(titleLabel); titleLine.Children.Add(titleBox);
            var inst = new TextBlock { FontSize = 12, Opacity = 0.75, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap };
            _inst[r] = inst;
            var sub = new StackPanel { Margin = new Thickness(40, 0, 0, 8) };
            sub.Children.Add(titleLine); sub.Children.Add(inst);
            var holder = new StackPanel();
            holder.Children.Add(row); holder.Children.Add(sub);
            _rows.Children.Add(holder);
        }
        _loading = false;
    }

    private void ShowAddMenu(Button anchor)
    {
        var menu = new ContextMenu { Style = (Style)Application.Current.FindResource("ThemedMenu"), PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var choose = new MenuItem { Header = "Choose a program file (.exe)…" };
        choose.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose the program", Filter = "Programs (*.exe)|*.exe", CheckFileExists = true };
            if (dlg.ShowDialog(this) == true) AddRule(dlg.FileName);
        };
        menu.Items.Add(choose);
        menu.Items.Add(new Separator());
        var running = new List<(string Exe, string Title)>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0 && p.Id != Environment.ProcessId)
                    running.Add((p.ProcessName + ".exe", p.MainWindowTitle));
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { p.Dispose(); }
        }
        foreach (var (exe, title) in running.GroupBy(r => r.Exe, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(r => r.Exe, StringComparer.OrdinalIgnoreCase).Take(40))
        {
            string e = exe;
            var item = new MenuItem { Header = $"{exe}   —   {(title.Length > 40 ? title[..40] + "…" : title)}" };
            item.Click += (_, _) => AddRule(e);
            menu.Items.Add(item);
        }
        if (running.Count == 0) menu.Items.Add(new MenuItem { Header = "(no program with a window is open)", IsEnabled = false });
        menu.IsOpen = true;
    }

    private void AddRule(string pathOrName)
    {
        string exe = MemoryLimits.CleanExe(pathOrName);
        if (exe.Length == 0) return;
        if (_rules.Any(r => r.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase))) { UMessage.Show(this, $"{exe} is already in the list.", "Memory limits", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        _rules.Add(new MemoryRule { Exe = exe, LimitMb = 500, Enabled = true, Cpu = 0, CapPercent = 50 });
        BuildRows();
        Changed();
    }

    private void AllowAdministrator()
    {
        try
        {
            string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
            var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--memory-guard-setup " + sid) { UseShellExecute = true, Verb = "runas" });
            p?.WaitForExit(20000);
        }
        catch (System.ComponentModel.Win32Exception) { return; }                       // (the permission was refused)
        MemoryGuard.Restart(App.DataDir);
        Refresh();
    }

    private void Refresh()
    {
        foreach (var (rule, text) in _now)
        {
            long best = 0;
            var cpuTotal = TimeSpan.Zero;
            string name = System.IO.Path.GetFileNameWithoutExtension(rule.Exe);
            var lines = new List<string>();
            string filter = (rule.Title ?? "").Trim();
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        p.Refresh();
                        string title = p.MainWindowTitle ?? "";
                        bool match = filter.Length == 0 || title.Contains(filter, StringComparison.OrdinalIgnoreCase);
                        long ws = p.WorkingSet64;
                        lines.Add($"#{p.Id}   {(title.Length > 0 ? "\"" + (title.Length > 60 ? title[..60] + "…" : title) + "\"" : "(no window)")}   {ws / (1024 * 1024)} MB" + (match ? "" : "   — not used by this row"));
                        if (!match) continue;
                        best = Math.Max(best, ws); cpuTotal += p.TotalProcessorTime;
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    finally { p.Dispose(); }
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            if (_inst.TryGetValue(rule, out var instText)) instText.Text = lines.Count == 0 ? "" : "Running now:  " + string.Join("     ", lines);
            string cpuText = "";
            var nowAt = DateTime.UtcNow;
            if (best > 0 && _cpuBefore.TryGetValue(rule.Exe, out var before) && cpuTotal >= before.Cpu && (nowAt - before.At).TotalSeconds > 0.5)
                cpuText = $"  ·  CPU {Math.Min(100.0, 100.0 * (cpuTotal - before.Cpu).TotalSeconds / (nowAt - before.At).TotalSeconds / Environment.ProcessorCount):0} %";
            _cpuBefore[rule.Exe] = (cpuTotal, nowAt);
            text.Text = best == 0 ? "not running" : (best / (1024 * 1024)) + " MB" + cpuText;
            text.Foreground = rule.LimitMb > 0 && best > rule.LimitMb * 1024L * 1024L ? Brushes.Orange : (Brush)FindResource("TextBrush");
        }
        bool active = _rules.Any(r => r.Enabled);
        var status = MemoryLimits.LoadStatus(App.DataDir);
        bool running = MemoryGuard.IsRunning;
        bool task = MemoryTask.GuardExists();
        string line;
        if (!active) line = "Nothing is being watched.";
        else if (!running) line = "The guard is starting…";
        else if (status != null && status.Elevated) line = "Watching with administrator rights: programs that run as administrator are reached too.";
        else if (!task && MemoryTask.CanBeSilent) line = "Watching. Programs that run as administrator (many game clients) cannot be reached until you allow it below.";
        else line = "Watching. Programs that run as administrator (many game clients) cannot be reached by this copy of Utylix.";
        _state.Text = line;
        // a guard that is not there although something is to watch (the tray was restarted, the guard ended): start it again
        if (active && !running && (DateTime.UtcNow - _lastEnsure).TotalSeconds > 8) { _lastEnsure = DateTime.UtcNow; MemoryGuard.Ensure(App.DataDir); }
        _allow.Visibility = active && !(status?.Elevated ?? false) && !task && MemoryTask.CanBeSilent ? Visibility.Visible : Visibility.Collapsed;
    }
}
