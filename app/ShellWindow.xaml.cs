using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// The one Utylix window: a tab bar on top, one tool below it. Each tool has its own preferred window size, and the
/// window glides to it when you switch tabs (and remembers a size you dragged it to, per tool).
/// </summary>
public partial class ShellWindow : Window
{
    private sealed record Tool(string Key, string Title, Func<UserControl> Create, double Width, double Height, double MinWidth, double MinHeight);

    private readonly Manager _manager;
    private readonly List<Tool> _tools = new();
    private readonly Dictionary<string, UserControl> _pages = new();
    private readonly Dictionary<string, Size> _userSizes = new();
    private readonly Dictionary<string, RadioButton> _tabs = new();
    private readonly TextBlock _downloadBadge = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _current = "";
    private bool _selecting;
    private bool _animating;

    public bool AllowClose { get; set; }
    private string _beforeHome = "downloads";

    /// <summary>Key of the tool that is showing.</summary>
    public string CurrentKey => _current;

    public ShellWindow(Manager manager)
    {
        InitializeComponent();
        WindowTheme.DarkTitleBar(this);
        _manager = manager;

        //          key          tab text            page                                              size (w x h)   smallest allowed
        Add(new("downloads", "Downloads", () => new DownloadsPage(manager), 960, 680, 810, 440));
        Add(new("converter", "Multi Convert", () => new ConverterPage(), 820, 820, 810, 720));
        Add(new("player", "Video Player", () => new PlayerPage(manager), 820, 640, 810, 480));
        Add(new("recorder", "Screen Recorder", () => new RecorderPage(manager), 860, 760, 810, 560));
        Add(new("home", "Dashboard", () => new DashboardPage(manager), 1000, 780, 810, 560), inBar: false);     // opened by the logo

        SizeChanged += (_, _) =>
        {
            if (!_animating && IsLoaded && WindowState == WindowState.Normal && _current.Length > 0)
                _userSizes[_current] = new Size(ActualWidth, ActualHeight);     // remember what you dragged it to
        };
        ElevatedBar.Visibility = IsElevated() ? Visibility.Visible : Visibility.Collapsed;
        _timer.Tick += (_, _) => RefreshBadge();
        _timer.Start();
        SelectTab("downloads");
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception) { return false; }
    }

    private void Add(Tool tool, bool inBar = true)
    {
        _tools.Add(tool);
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = tool.Title, VerticalAlignment = VerticalAlignment.Center });
        var tab = new RadioButton { Style = (Style)FindResource("NavTab"), GroupName = "tools", Tag = tool.Key, Content = content };
        AutomationProperties.SetName(tab, tool.Title);
        if (tool.Key == "downloads")
        {
            _downloadBadge.FontSize = 11;
            _downloadBadge.FontWeight = FontWeights.SemiBold;
            _downloadBadge.Margin = new Thickness(7, 0, 0, 0);
            _downloadBadge.VerticalAlignment = VerticalAlignment.Center;
            _downloadBadge.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            content.Children.Add(_downloadBadge);
        }
        tab.Checked += (_, _) => { if (!_selecting) SelectTab(tool.Key); };
        _tabs[tool.Key] = tab;
        if (inBar) Tabs.Children.Add(tab);
    }

    private void RefreshBadge()
    {
        int active = _manager.All().Count(d => d.Status == DlStatus.Downloading);
        _downloadBadge.Text = active > 0 ? "●  " + active : "";
    }

    /// <summary>The page of a tool (created the first time it is opened, then kept so its state stays).</summary>
    public T Page<T>(string key) where T : UserControl
    {
        var tool = _tools.First(t => t.Key == key);
        if (!_pages.TryGetValue(key, out var page)) _pages[key] = page = tool.Create();
        return (T)page;
    }

    public void SelectTab(string key)
    {
        var tool = _tools.FirstOrDefault(t => t.Key == key);
        if (tool == null || key == _current) return;
        _current = key;

        _selecting = true;
        foreach (var other in _tabs.Values) other.IsChecked = false;                // (the dashboard has no tab in the bar)
        if (key != "home") { _tabs[key].IsChecked = true; _beforeHome = key; }
        _selecting = false;
        LogoLine.Opacity = key == "home" ? 1 : 0;

        var page = Page<UserControl>(key);
        Host.Content = page;
        if (IsVisible)
        {
            page.Opacity = 0;                                                  // soft fade-in
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
        }
        ApplySize(tool, animate: IsVisible && WindowState == WindowState.Normal);
        if (key == "downloads") RefreshBadge();
    }

    private void ApplySize(Tool tool, bool animate)
    {
        var size = _userSizes.TryGetValue(tool.Key, out var s) ? s : new Size(tool.Width, tool.Height);
        var work = SystemParameters.WorkArea;
        double w = Math.Min(size.Width, work.Width), h = Math.Min(size.Height, work.Height);
        MinWidth = Math.Min(tool.MinWidth, w);                                 // lower the floor first so we can shrink
        MinHeight = Math.Min(tool.MinHeight, h);
        if (WindowState != WindowState.Normal) return;

        if (!animate)
        {
            Width = w; Height = h;
            if (IsVisible) Clamp(w, h);
            return;
        }

        double fromW = ActualWidth, fromH = ActualHeight;
        double left = Left + (fromW - w) / 2;                                  // grow/shrink around the middle, top stays
        double top = Top;
        left = Math.Max(work.Left, Math.Min(left, work.Right - w));
        top = Math.Max(work.Top, Math.Min(top, work.Bottom - h));

        _animating = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(230);
        void Animate(DependencyProperty p, double from, double to, bool last = false)
        {
            var a = new DoubleAnimation(from, to, dur) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
            if (last)
                a.Completed += (_, _) =>
                {
                    foreach (var prop in new[] { WidthProperty, HeightProperty, LeftProperty, TopProperty }) BeginAnimation(prop, null);
                    Width = w; Height = h; Left = left; Top = top;
                    _animating = false;
                };
            BeginAnimation(p, a);
        }
        Animate(LeftProperty, Left, left);
        Animate(TopProperty, Top, top);
        Animate(WidthProperty, fromW, w);
        Animate(HeightProperty, fromH, h, last: true);
    }

    private void Clamp(double w, double h)
    {
        var work = SystemParameters.WorkArea;
        Left = Math.Max(work.Left, Math.Min(Left, work.Right - w));
        Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - h));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;        // keep running in the tray so the browser extension still works
            Hide();
        }
        base.OnClosing(e);
    }

    private void Logo_Click(object sender, RoutedEventArgs e) => SelectTab(_current == "home" ? _beforeHome : "home");

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        new SettingsWindow(_manager, _current) { Owner = this }.ShowDialog();      // opens on the settings of the tab you are on
}
