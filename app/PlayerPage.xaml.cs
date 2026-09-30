using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>The "Video Player" tab: open something, or pick up where you left off. The player itself is its own window.</summary>
public partial class PlayerPage : UserControl
{
    private sealed record RecentItem(string Path, string Name, string Folder, string Resume);

    private readonly Manager _manager;

    public PlayerPage(Manager manager)
    {
        InitializeComponent();
        _manager = manager;
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
        Refresh();
    }

    private void Refresh()
    {
        EngineBanner.Visibility = VlcEngine.Available ? Visibility.Collapsed : Visibility.Visible;
        var state = PlayerState.Current;
        var items = state.Recent.Where(File.Exists).Take(20).Select(p =>
        {
            double at = state.ResumeAt(p);
            string resume = at > 0 ? "continue from " + Fmt(at) : "";
            return new RecentItem(p, Path.GetFileNameWithoutExtension(p), Path.GetDirectoryName(p) ?? "", resume);
        }).ToList();
        RecentList.ItemsSource = items;
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentList.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ClearBtn.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string Fmt(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private void Engine_Click(object sender, RoutedEventArgs e)
    {
        PlayerWindow.EnsureEngine();
        Refresh();
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a video or music file",
            Filter = "Video and music|" + string.Join(";", PlayerMedia.VideoExtensions.Concat(PlayerMedia.AudioExtensions).Select(x => "*." + x)) + "|All files|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) PlayerWindow.Open(dlg.FileNames);
        Refresh();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Play a folder" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        PlayerWindow.Open(new[] { dlg.FolderName });
    }

    private void OpenNetwork_Click(object sender, RoutedEventArgs e)
    {
        var url = TextPrompt.Ask(Window.GetWindow(this)!, "Open network stream", "Address of a video or radio stream (http, https, rtsp, mms …):", "");
        if (string.IsNullOrWhiteSpace(url)) return;
        url = url.Trim();
        PlayerWindow.Open(new[] { url.Contains("://") ? url : "http://" + url });
    }

    private void Recent_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecentList.SelectedItem is RecentItem item) PlayerWindow.Open(new[] { item.Path });
    }

    private void Recent_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && RecentList.SelectedItem is RecentItem item) { PlayerWindow.Open(new[] { item.Path }); e.Handled = true; }
    }

    private void ClearRecent_Click(object sender, RoutedEventArgs e)
    {
        PlayerState.Current.Recent.Clear();
        PlayerState.Current.Save();
        Refresh();
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Page_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) PlayerWindow.Open(paths);
        e.Handled = true;
    }
}
