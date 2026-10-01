using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// The home page that opens when the Utylix logo is clicked: quick actions, what the downloads are doing, and the PC's fans and
/// temperatures (the fan controls live here now, not in a tab of their own).
/// </summary>
public sealed class DashboardPage : UserControl
{
    private readonly Manager _manager;
    private readonly TextBlock _downloadsLine = new() { FontSize = 15, FontWeight = FontWeights.SemiBold };
    private readonly StackPanel _recent = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _lastRecent = "";

    public DashboardPage(Manager manager)
    {
        _manager = manager;
        var R = (string key) => Application.Current.FindResource(key);
        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 24) };

        // ---- title ----
        var head = new Grid();
        head.Children.Add(new TextBlock { Text = "Dashboard", FontSize = 22, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = "Utylix " + Engine.AppUpdater.CurrentText, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)R("MutedBrush") });
        AutomationProperties.SetAutomationId(head, "DashboardTitle");
        root.Children.Add(head);

        // ---- quick actions ----
        root.Children.Add(Heading("Quick actions"));
        var tiles = new WrapPanel();
        void Tile(string glyph, string label, string id, Action action)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 20, Foreground = (Brush)R("AccentBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Content = content, Style = (Style)R("DialogButton"), MinWidth = 168, Height = 52, Margin = new Thickness(0, 0, 10, 10), HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 0, 16, 0) };
            AutomationProperties.SetAutomationId(b, id);
            b.Click += (_, _) => action();
            tiles.Children.Add(b);
        }
        Tile("", "Take a snip", "DashSnip", () => SnipWindow.StartCapture(_manager));
        Tile("", "Record the screen", "DashRecord", () => App.Show("recorder"));
        Tile("", "Video player", "DashPlayer", () => App.Show("player"));
        Tile("", "Convert files", "DashConvert", () => App.Show("converter"));
        Tile("", "Downloads", "DashDownloads", () => App.Show("downloads"));
        Tile("", "Brightness", "DashBrightness", BrightnessWindow.ShowPanel);
        Tile("", "Browser extension", "DashExtension", () => ExtensionFiles.ShowHelp(Window.GetWindow(this)));
        Tile("", "Check for updates", "DashUpdates", () => AppUpdateWindow.ShowWindow(_manager));
        root.Children.Add(tiles);

        // ---- downloads (in the fans' area, just above the safety settings) ----
        var downloads = new Border { Style = (Style)R("Section"), Padding = new Thickness(16, 12, 16, 14), Margin = new Thickness(0, 0, 0, 0) };
        var dstack = new StackPanel();
        AutomationProperties.SetAutomationId(_downloadsLine, "DashDownloadsLine");
        dstack.Children.Add(_downloadsLine);
        dstack.Children.Add(_recent);
        downloads.Child = dstack;
        Fans.ExtraSlot.Children.Add(Heading("Downloads"));
        Fans.ExtraSlot.Children.Add(downloads);

        // ---- the PC: fans and temperatures ----
        root.Children.Add(Heading("This PC"));
        root.Children.Add(Fans);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => { if (IsVisible) { Refresh(); _timer.Start(); } else _timer.Stop(); };
    }

    /// <summary>The fan controls, shown on the dashboard. It starts the fan helper by itself when that has been set up, so it is made as soon as Utylix starts.</summary>
    public FansPage Fans { get; } = new();

    private TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 20, 0, 10) };

    private static string Speed(long bytes) => bytes <= 0 ? "0 KB/s" : bytes >= 1048576 ? (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s" : (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB/s";

    private void Refresh()
    {
        var all = _manager.All().Select(d => d.Info()).ToList();
        int active = all.Count(d => d.Status == DlStatus.Downloading);
        int waiting = all.Count(d => d.Status is DlStatus.Queued or DlStatus.Paused or DlStatus.Awaiting);
        int done = all.Count(d => d.Status == DlStatus.Completed);
        int failed = all.Count(d => d.Status == DlStatus.Error);
        long speed = all.Where(d => d.Status == DlStatus.Downloading).Sum(d => d.Speed);
        _downloadsLine.Text = all.Count == 0 ? "Nothing downloaded yet. Downloads from your browser show up here."
            : (active > 0 ? $"{active} downloading at {Speed(speed)}" : "Nothing is downloading") + $"   ·   {waiting} waiting   ·   {done} finished" + (failed > 0 ? $"   ·   {failed} failed" : "");

        var recent = all.OrderByDescending(d => d.Created).Take(4).ToList();
        string key = string.Join("|", recent.Select(d => d.Id + d.Status + (d.Status == DlStatus.Downloading ? (d.Size > 0 ? (int)(100 * d.Downloaded / Math.Max(1, d.Size)) : 0) : 0)));
        if (key == _lastRecent) return;
        _lastRecent = key;
        _recent.Children.Clear();
        foreach (var d in recent)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 0) };
            row.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(d.FileName) ? d.Url : d.FileName, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 150, 0) });
            string status = d.Status switch
            {
                DlStatus.Downloading => d.Size > 0 ? (100 * d.Downloaded / Math.Max(1, d.Size)) + " %" : "downloading",
                DlStatus.Completed => "finished", DlStatus.Paused => "paused", DlStatus.Error => "failed", DlStatus.Queued => "waiting", _ => "waiting",
            };
            row.Children.Add(new TextBlock { Text = status, HorizontalAlignment = HorizontalAlignment.Right, Foreground = (Brush)Application.Current.FindResource("MutedBrush") });
            _recent.Children.Add(row);
        }
    }
}
