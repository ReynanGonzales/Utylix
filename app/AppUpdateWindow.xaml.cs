using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>Looks for a newer Utylix on GitHub, shows what is new and installs it when asked.</summary>
public partial class AppUpdateWindow : Window
{
    private static AppUpdateWindow? _open;

    private readonly Manager _manager;
    private readonly CancellationTokenSource _cts = new();
    private ReleaseInfo? _release;
    private bool _busy;

    /// <param name="release">A release the background check already found (null = check now).</param>
    private AppUpdateWindow(Manager manager, ReleaseInfo? release)
    {
        InitializeComponent();
        _manager = manager;
        _release = release;
        Closed += (_, _) => { _cts.Cancel(); if (_open == this) _open = null; };
        Loaded += async (_, _) => { if (_release != null) ShowRelease(_release); else await CheckAsync(); };
    }

    public static void ShowWindow(Manager manager, ReleaseInfo? release = null)
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new AppUpdateWindow(manager, release);
        _open.Topmost = true;
        _open.Show();
        _open.Activate();
        _open.Topmost = false;
    }

    private string Token => AppUpdater.Unprotect(_manager.Config.UpdateToken);

    private void SetStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrBrush" : "TextBrush");
    }

    private async Task CheckAsync()
    {
        _busy = true;
        UpdateBtn.Visibility = TokenBtn.Visibility = Visibility.Collapsed;
        HeadText.Text = "Checking for updates…";
        VersionText.Text = "You have Utylix " + AppUpdater.CurrentText;
        SetStatus("");
        try
        {
            var release = await AppUpdater.LatestAsync(Token, _cts.Token);
            _manager.UpdateConfig(c => c.LastAppCheck = DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            ShowRelease(release);
        }
        catch (OperationCanceledException) { return; }
        catch (UpdateException e)
        {
            HeadText.Text = "Couldn't check for updates";
            SetStatus(e.Message, error: true);
            TokenBtn.Visibility = e.NeedsToken ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _busy = false; }
    }

    private void ShowRelease(ReleaseInfo release)
    {
        _release = release;
        VersionText.Text = "You have Utylix " + AppUpdater.CurrentText + "   ·   newest on GitHub: " + release.Version.ToString(3);
        if (AppUpdater.IsNewer(release))
        {
            HeadText.Text = "Utylix " + release.Version.ToString(3) + " is available";
            if (!string.IsNullOrWhiteSpace(release.Notes)) { NotesBox.Text = release.Notes.Trim(); NotesBox.Visibility = Visibility.Visible; }
            UpdateBtn.Visibility = Visibility.Visible;
            SetStatus("Updating downloads the new Utylix.exe, checks it against its published checksum, then restarts Utylix. Downloads in progress are paused (Resume all continues them).");
        }
        else
        {
            HeadText.Text = "Utylix is up to date";
            SetStatus("");
        }
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Close();

    private void Token_Click(object sender, RoutedEventArgs e)
    {
        string? token = TextPrompt.Ask(this, "GitHub access token",
            "Only needed while the repository is private. Create a \"fine-grained personal access token\" on github.com for the Utylix repository with read-only permission for Contents, and paste it here. It is stored encrypted for this Windows user; leave it empty to remove it.",
            "");
        if (token == null) return;
        string protectedToken = AppUpdater.Protect(token);
        _manager.UpdateConfig(c => c.UpdateToken = protectedToken);
        _ = CheckAsync();
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _release == null) return;
        _busy = true;
        UpdateBtn.IsEnabled = LaterBtn.IsEnabled = false;
        SetStatus("");
        try
        {
            string file = await AppUpdater.DownloadAsync(_release, Token, s => Dispatcher.Invoke(() => SetStatus(s)), _cts.Token);
            if (!((App)Application.Current).CanRestartNow(out string why))
            {
                System.IO.File.Delete(file);
                throw new UpdateException(why);
            }
            SetStatus("Installing and restarting…");
            AppUpdater.Apply(file);
            ((App)Application.Current).QuitForUpdate();
        }
        catch (OperationCanceledException) { }
        catch (UpdateException ex)
        {
            SetStatus(ex.Message, error: true);
            UpdateBtn.IsEnabled = LaterBtn.IsEnabled = true;
            _busy = false;
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Net.Http.HttpRequestException or UnauthorizedAccessException)
        {
            SetStatus("Couldn't update: " + ex.Message, error: true);
            UpdateBtn.IsEnabled = LaterBtn.IsEnabled = true;
            _busy = false;
        }
    }
}

/// <summary>Every few hours checks (at most once a day) whether a newer Utylix exists and tells the app to ASK once per version.</summary>
public sealed class AppUpdateWatcher
{
    private readonly Manager _manager;
    private readonly Action<ReleaseInfo> _offer;
    private readonly bool _now;

    public AppUpdateWatcher(Manager manager, Action<ReleaseInfo> offer, bool now = false) { _manager = manager; _offer = offer; _now = now; }

    public void Start() => _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(_now ? 3 : 90));
        while (true)
        {
            try { await CheckAsync(); } catch (Exception) { /* offline, private and no token ...: the manual check explains */ }
            await Task.Delay(TimeSpan.FromHours(6));
        }
    });

    private async Task CheckAsync()
    {
        var cfg = _manager.Config;
        if (!cfg.AutoUpdateApp) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!_now && now - cfg.LastAppCheck < 86400) return;
        var release = await AppUpdater.LatestAsync(AppUpdater.Unprotect(cfg.UpdateToken), default);
        _manager.UpdateConfig(c => c.LastAppCheck = now);
        if (!AppUpdater.IsNewer(release) || cfg.LastAppOffer == release.Tag) return;
        _manager.UpdateConfig(c => c.LastAppOffer = release.Tag);
        _offer(release);
    }
}
