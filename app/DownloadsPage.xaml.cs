using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using IdmClone.Engine;

namespace IdmClone;

public partial class DownloadsPage : UserControl
{
    private readonly Manager _manager;
    private readonly Dictionary<string, DownloadItem> _items = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<DownloadItem> _list = new();
    private readonly ICollectionView _view;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string _filter = "all";

    public DownloadsPage(Manager manager)
    {
        InitializeComponent();
        _manager = manager;
        _view = CollectionViewSource.GetDefaultView(_list);
        _view.SortDescriptions.Add(new SortDescription(nameof(DownloadItem.Created), ListSortDirection.Descending));
        _view.Filter = o => _filter switch
        {
            "completed" => ((DownloadItem)o).IsCompleted,
            "active" => ((DownloadItem)o).IsActive,
            _ => true,
        };
        List.ItemsSource = _view;
        _timer.Tick += (_, _) => RefreshList();
        _timer.Start();
        RefreshList();
    }

    // ---------- list ----------
    private void RefreshList()
    {
        // Downloads still waiting for the capture window's OK stay out of the list.
        var infos = _manager.All().Select(d => d.Info()).Where(i => i.Status != DlStatus.Awaiting).ToList();
        bool changedStatus = false;
        foreach (var info in infos)
        {
            if (_items.TryGetValue(info.Id, out var item)) changedStatus |= item.Update(info);
            else
            {
                item = new DownloadItem(info);
                _items[info.Id] = item;
                _list.Add(item);
            }
        }
        var alive = infos.Select(i => i.Id).ToHashSet();
        foreach (var id in _items.Keys.Where(id => !alive.Contains(id)).ToList())
        {
            _list.Remove(_items[id]);
            _items.Remove(id);
        }
        if (changedStatus) _view.Refresh();

        long speed = infos.Sum(i => i.Speed);
        int active = infos.Count(i => i.Status == DlStatus.Downloading);
        StatsText.Text = $"{Format.Bytes(speed)}/s  ·  {active} active";
        EmptyText.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private static DownloadItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as DownloadItem;

    private void Pause_Click(object sender, RoutedEventArgs e) { _manager.Get(ItemOf(sender)?.Id ?? "")?.Pause(); RefreshList(); }
    private void Resume_Click(object sender, RoutedEventArgs e) { _manager.Get(ItemOf(sender)?.Id ?? "")?.Resume(); RefreshList(); }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (MessageBox.Show(Window.GetWindow(this), "Cancel this download and discard what was downloaded?", "Utylix",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _manager.Remove(item.Id, false);
        RefreshList();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) _manager.Remove(item.Id, false);
        RefreshList();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (MessageBox.Show(Window.GetWindow(this), $"Remove from the list and delete \"{item.FileName}\" from disk?", "Utylix",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            _manager.Remove(item.Id, true);
        RefreshList();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender)?.FilePath is not { } path) return;
        if (!System.IO.File.Exists(path)) { ShowMessage("That file no longer exists."); return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { ShowMessage(ex.Message); }
    }

    private void Details_Click(object sender, RoutedEventArgs e) => ItemOf(sender)?.ToggleDetails();

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        try { App.ClipboardWatch?.IgnoreNext(item.Url); Clipboard.SetText(item.Url); ShowMessage(""); }   // (our own copy must not trigger the popup)
        catch (Exception ex) { ShowMessage("Couldn't copy: " + ex.Message); }   // the clipboard can be locked by another app
    }

    private void OpenSaveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        string path = item.DetSavePath;
        if (System.IO.File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(path) ?? "")) Process.Start("explorer.exe", $"\"{System.IO.Path.GetDirectoryName(path)}\"");
        else ShowMessage("That folder doesn't exist yet.");
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender)?.FilePath is not { } path) return;
        if (!System.IO.File.Exists(path)) { ShowMessage("That file no longer exists."); return; }
        Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    // ---------- toolbar ----------
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var urls = UrlBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (urls.Count == 0) return;
        if (!urls.All(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) &&
                           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
        {
            ShowMessage("Only http:// and https:// links are supported.");
            return;
        }
        foreach (var url in urls) _manager.Add(url, null, null);
        UrlBox.Clear();
        ShowMessage("");
        RefreshList();
    }

    /// <summary>Enter adds the link(s); Shift+Enter starts a new line for a second link.</summary>
    private void UrlBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0) return;   // newline
        e.Handled = true;
        Add_Click(sender, e);
    }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e) =>
        UrlHint.Visibility = UrlBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void PauseAll_Click(object sender, RoutedEventArgs e) { _manager.PauseAll(); RefreshList(); }
    private void ResumeAll_Click(object sender, RoutedEventArgs e) { _manager.ResumeAll(); RefreshList(); }
    private void ClearCompleted_Click(object sender, RoutedEventArgs e) { _manager.ClearCompleted(); RefreshList(); }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (_view == null) return;    // fires during InitializeComponent
        _filter = (string)((RadioButton)sender).Tag;
        _view.Refresh();
        RefreshList();
    }

    private DispatcherTimer? _msgTimer;

    private async void UpdateRetry_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        ShowMessage("Updating yt-dlp…", error: false);
        try
        {
            await Tools.InstallYtDlpAsync(s => Dispatcher.Invoke(() => ShowMessage(s, error: false)), System.Threading.CancellationToken.None);
            _manager.Get(item.Id)?.Resume();
            ShowMessage("yt-dlp updated. Trying the download again…", error: false);
        }
        catch (Exception ex)
        {
            ShowMessage("Couldn't update yt-dlp: " + ex.Message +
                        (ex is System.IO.IOException ? "  (another video download may be running; try again when it has finished)" : ""));
        }
        RefreshList();
    }

    private void ShowMessage(string text, bool error = true)
    {
        MessageText.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrBrush" : "TextBrush");
        MessageText.Text = text;
        _msgTimer?.Stop();
        if (text.Length == 0) return;
        _msgTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _msgTimer.Tick += (_, _) => { MessageText.Text = ""; _msgTimer.Stop(); };
        _msgTimer.Start();
    }
}
