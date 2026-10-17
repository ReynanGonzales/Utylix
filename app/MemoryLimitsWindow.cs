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
    private readonly Dictionary<string, int> _front = new(StringComparer.OrdinalIgnoreCase);
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
            Text = "Pick a program, then give it a memory limit, a CPU setting or a frame limit.",
            Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14),
            ToolTip = "Memory: above the limit, Utylix gives back what the program is not using right now (nothing is closed). Start with 500 MB or more; 0 = no memory limit." + Environment.NewLine +
                      "CPU: lower priority, efficiency mode, or a cap on the share of the whole processor." + Environment.NewLine +
                      "Several clients of one game: under each row choose which copies it is for." + Environment.NewLine +
                      "Limit to FPS: Utylix's own frame limiter (Direct3D 9 - 12). A program that runs as administrator needs the administrator guard. Some anti-cheat programs do not like it.",
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
        FpsOverlay.Apply(App.DataDir);                         // (the on-screen counters follow the "Show FPS" boxes)
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
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2), Background = new SolidColorBrush(Color.FromArgb(20, 128, 128, 128)) };
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
            var caps = new[] { 75, 50, 25, 10, 5 };
            foreach (string item in new[] { "No change", "Lower priority", "Efficiency mode", "Cap at 75 %", "Cap at 50 %", "Cap at 25 %", "Cap at 10 %", "Cap at 5 %" }) cpu.Items.Add(item);
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
            if (r.Scope == 0 && !string.IsNullOrWhiteSpace(r.Title)) r.Scope = 1;     // (rows made before the choice existed)
            var scope = new ComboBox { Width = 400, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            foreach (string item in new[] { "Use for every copy of this program", "Only the windows whose title contains:", "Every copy EXCEPT the title containing: (my main)", "Every copy except the one I last played (in front)" }) scope.Items.Add(item);
            scope.SelectedIndex = Math.Clamp(r.Scope, 0, 3);
            scope.ToolTip = "Several clients of one game: choose which ones this row is for." + Environment.NewLine + "Example: Cap at 10 % + 'Every copy EXCEPT the title containing' + your main account's name = all other accounts are held back and the main one runs free.";
            AutomationProperties.SetAutomationId(scope, "MemLimitScope_" + r.Exe);
            var titleBox = new TextBox { Text = r.Title ?? "", Width = 260, Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center };
            titleBox.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            titleBox.Background = new SolidColorBrush(Color.FromArgb(34, 128, 128, 128));
            titleBox.SetResourceReference(Control.BorderBrushProperty, "MutedBrush");
            titleBox.ToolTip = "Leave empty to use this row for every copy of the program. Type part of a window title to use it only for that client.";
            AutomationProperties.SetAutomationId(titleBox, "MemLimitTitle_" + r.Exe);
            titleBox.TextChanged += (_, _) => { r.Title = titleBox.Text; Changed(); };
            scope.SelectionChanged += (_, _) => { r.Scope = scope.SelectedIndex; titleBox.IsEnabled = r.Scope == 1 || r.Scope == 2; Changed(); };
            titleBox.IsEnabled = r.Scope == 1 || r.Scope == 2;
            var fpsLabel = new TextBlock { Text = "Limit to", Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 6, 0) };
            var fpsBox = new TextBox { Text = r.TargetFps > 0 ? r.TargetFps.ToString() : "", Width = 56, Padding = new Thickness(6, 3, 6, 3), VerticalContentAlignment = VerticalAlignment.Center };
            fpsBox.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            fpsBox.Background = new SolidColorBrush(Color.FromArgb(34, 128, 128, 128));
            fpsBox.SetResourceReference(Control.BorderBrushProperty, "MutedBrush");
            fpsBox.ToolTip = "Frames per second these copies should be held to, 1 to 500 (empty = no limit)." + Environment.NewLine + "Utylix counts their frames and keeps their processor use just low enough. Needs the administrator guard; only about right for programs that are limited by the processor.";
            AutomationProperties.SetAutomationId(fpsBox, "MemLimitFps_" + r.Exe);
            fpsBox.TextChanged += (_, _) =>
            {
                string t = fpsBox.Text.Trim();
                if (t.Length == 0) { r.TargetFps = 0; fpsBox.SetResourceReference(Control.ForegroundProperty, "TextBrush"); Changed(); }
                else if (int.TryParse(t, out int v) && v >= 1 && v <= 500) { r.TargetFps = v; fpsBox.SetResourceReference(Control.ForegroundProperty, "TextBrush"); Changed(); }
                else fpsBox.Foreground = Brushes.IndianRed;
            };
            var fpsUnit = new TextBlock { Text = "FPS", Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            // the second line: which copies | the title (only when the choice needs one) | the frame limit, kept to the right
            var titleLine = new Grid();
            titleLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleBox.Width = double.NaN; titleBox.MaxWidth = 300; titleBox.HorizontalAlignment = HorizontalAlignment.Left; titleBox.VerticalAlignment = VerticalAlignment.Center;
            titleBox.Visibility = titleBox.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            scope.SelectionChanged += (_, _) => titleBox.Visibility = r.Scope == 1 || r.Scope == 2 ? Visibility.Visible : Visibility.Collapsed;
            var fpsGroup = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 10, 0) };
            fpsLabel.Margin = new Thickness(0, 0, 6, 0);
            var showFps = new CheckBox { Content = "Show FPS", IsChecked = r.ShowFps, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0),
                                         ToolTip = "Shows the frames per second on top of the program's window while it is in front (top-left corner). Needs the same access as the frame limit." };
            AutomationProperties.SetAutomationId(showFps, "MemLimitShowFps_" + r.Exe);
            showFps.Click += (_, _) => { r.ShowFps = showFps.IsChecked == true; Changed(); };
            fpsGroup.Children.Add(showFps);
            fpsGroup.Children.Add(fpsLabel); fpsGroup.Children.Add(fpsBox); fpsGroup.Children.Add(fpsUnit);
            Grid.SetColumn(scope, 0); Grid.SetColumn(titleBox, 1); Grid.SetColumn(fpsGroup, 2);
            titleLine.Children.Add(scope); titleLine.Children.Add(titleBox); titleLine.Children.Add(fpsGroup);
            var inst = new TextBlock { FontSize = 12, Opacity = 0.75, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
            _inst[r] = inst;
            var sub = new StackPanel { Margin = new Thickness(40, 0, 10, 8) };
            sub.Children.Add(titleLine); sub.Children.Add(inst);
            var holder = new StackPanel();
            holder.Children.Add(row); holder.Children.Add(sub);
            // one bordered card per program, so it is clear which lines belong together
            var card = new Border { BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 6, 0, 6), Padding = new Thickness(0, 2, 0, 2), Child = holder };
            card.SetResourceReference(Border.BorderBrushProperty, "MutedBrush");
            _rows.Children.Add(card);
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
        MemoryLimits.WantFps(App.DataDir);                      // (the guard counts frames only while this window says it is looking)
        var status = MemoryLimits.LoadStatus(App.DataDir);
        var fpsMap = status?.Fps ?? new Dictionary<int, int>();
        foreach (var (rule, text) in _now)
        {
            long best = 0;
            int bestFps = 0;
            var cpuTotal = TimeSpan.Zero;
            string name = System.IO.Path.GetFileNameWithoutExtension(rule.Exe);
            var lines = new List<string>();
            int hiddenCount = 0;                                    // processes without a window (helpers: a browser has dozens): one summary line, not a wall of text
            long hiddenBytes = 0;
            int front = MemoryGuard.ForegroundPid();
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        p.Refresh();
                        string title = p.MainWindowTitle ?? "";
                        if (front == p.Id) _front[rule.Exe] = p.Id;
                        _front.TryGetValue(rule.Exe, out int frontPid);
                        bool match = rule.Matches(title, p.Id, frontPid);
                        long ws = p.WorkingSet64;
                        if (title.Length == 0) { hiddenCount++; hiddenBytes += ws; }
                        else lines.Add($"•  #{p.Id}   \"{(title.Length > 60 ? title[..60] + "…" : title)}\"   {ws / (1024 * 1024)} MB" + (fpsMap.TryGetValue(p.Id, out int f) ? $"   {f} FPS" : "") + (match ? "" : "   — not used by this row"));
                        if (match && fpsMap.TryGetValue(p.Id, out int f2)) bestFps = Math.Max(bestFps, f2);
                        if (!match) continue;
                        best = Math.Max(best, ws); cpuTotal += p.TotalProcessorTime;
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    finally { p.Dispose(); }
                }
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            if (_inst.TryGetValue(rule, out var instText))
            {
                if (hiddenCount > 0) lines.Add($"•  {hiddenCount} more without a window (helpers), {hiddenBytes / (1024 * 1024)} MB together");
                string hook = rule.TargetFps > 0 && status?.Rows.FirstOrDefault(r => r.Exe.Equals(rule.Exe, StringComparison.OrdinalIgnoreCase))?.HookState is { Length: > 0 } hs
                    ? Environment.NewLine + "Frame limit not applied: " + hs : "";
                instText.Text = (lines.Count == 0 ? "" : "Running now:" + Environment.NewLine + string.Join(Environment.NewLine, lines)) + hook;
            }
            string cpuText = "";
            var nowAt = DateTime.UtcNow;
            if (best > 0 && _cpuBefore.TryGetValue(rule.Exe, out var before) && cpuTotal >= before.Cpu && (nowAt - before.At).TotalSeconds > 0.5)
                cpuText = $"  ·  CPU {Math.Min(100.0, 100.0 * (cpuTotal - before.Cpu).TotalSeconds / (nowAt - before.At).TotalSeconds / Environment.ProcessorCount):0} %";
            _cpuBefore[rule.Exe] = (cpuTotal, nowAt);
            text.Text = best == 0 ? "not running" : (best / (1024 * 1024)) + " MB" + cpuText + (bestFps > 0 ? $"  ·  {bestFps} FPS" : "");
            text.Foreground = rule.LimitMb > 0 && best > rule.LimitMb * 1024L * 1024L ? Brushes.Orange : (Brush)FindResource("TextBrush");
        }
        bool active = _rules.Any(r => r.Enabled);
        bool running = MemoryGuard.IsRunning;
        bool task = MemoryTask.GuardExists();
        string line;
        if (!active) line = "Nothing is being watched.";
        else if (!running) line = "The guard is starting…";
        else if (status != null && status.Elevated) line = "Watching with administrator rights: programs that run as administrator are reached too.";
        else if (!task && MemoryTask.CanBeSilent) line = "Watching. Programs that run as administrator (many game clients) cannot be reached until you allow it below.";
        else line = "Watching. Programs that run as administrator (many game clients) cannot be reached by this copy of Utylix.";
        if (active && running && !string.IsNullOrEmpty(status?.FpsState)) line += "  Frames per second: " + status!.FpsState + ".";
        _state.Text = line;
        // a guard that is not there although something is to watch (the tray was restarted, the guard ended): start it again
        if (active && !running && (DateTime.UtcNow - _lastEnsure).TotalSeconds > 8) { _lastEnsure = DateTime.UtcNow; MemoryGuard.Ensure(App.DataDir); }
        _allow.Visibility = active && !(status?.Elevated ?? false) && !task && MemoryTask.CanBeSilent ? Visibility.Visible : Visibility.Collapsed;
    }
}
