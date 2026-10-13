using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace IdmClone.Vid;

/// <summary>The Project panel like Premiere's: the project's name, a search box, "N items", an icon view (thumbnails with their length) or a list view,
/// and the little toolbar at the bottom (list / icon view, size slider, new title, delete).</summary>
public sealed partial class VidWindow
{
    private ScrollViewer _binScroll = null!;
    private TextBlock _binCount = null!, _binPath = null!;
    private TextBox _binSearch = null!;
    private bool _binIcons = true;
    private double _tileWidth = 118;
    private ToggleButton _listToggle = null!, _iconToggle = null!;

    private UIElement BuildProjectPanelBody()
    {
        _binScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _bin = new WrapPanel { Margin = new Thickness(8) };
        _binScroll.Content = _bin;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // the project file
        var pathRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 10, 0) };
        pathRow.Children.Add(Glyph("", 14));
        _binPath = new TextBlock { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _binPath.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        pathRow.Children.Add(_binPath);
        Grid.SetRow(pathRow, 0);
        grid.Children.Add(pathRow);

        // search and the number of items
        var searchRow = new Grid { Margin = new Thickness(8, 6, 8, 2) };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _binSearch = new TextBox { Padding = new Thickness(8, 4, 8, 4), ToolTip = "Search the project's files" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_binSearch, "VidBinSearch");
        _binSearch.TextChanged += (_, _) => RebuildBin();
        _binCount = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
        _binCount.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Grid.SetColumn(_binCount, 1);
        searchRow.Children.Add(_binSearch);
        searchRow.Children.Add(_binCount);
        Grid.SetRow(searchRow, 1);
        grid.Children.Add(searchRow);

        Grid.SetRow(_binScroll, 2);
        grid.Children.Add(_binScroll);

        // the toolbar at the bottom
        var bar = new DockPanel { Margin = new Thickness(8, 4, 8, 6) };
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var small = (Style)XamlReader.Parse(ToolToggleStyleSmall);
        _listToggle = new ToggleButton { Content = Glyph("", 14), ToolTip = "List view", Style = small, Width = 28, Height = 26, Margin = new Thickness(0, 0, 2, 0), IsChecked = !_binIcons };
        _iconToggle = new ToggleButton { Content = Glyph("", 14), ToolTip = "Icon view", Style = small, Width = 28, Height = 26, Margin = new Thickness(0, 0, 8, 0), IsChecked = _binIcons };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_listToggle, "VidBinList");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_iconToggle, "VidBinIcons");
        _listToggle.Click += (_, _) => { _binIcons = false; _listToggle.IsChecked = true; _iconToggle.IsChecked = false; RebuildBin(); };
        _iconToggle.Click += (_, _) => { _binIcons = true; _iconToggle.IsChecked = true; _listToggle.IsChecked = false; RebuildBin(); };
        left.Children.Add(_listToggle); left.Children.Add(_iconToggle);
        var size = new Slider { Minimum = 84, Maximum = 190, Value = _tileWidth, Width = 90, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Size of the thumbnails" };
        size.ValueChanged += (_, e) => { _tileWidth = e.NewValue; if (_binIcons) RebuildBin(); };
        left.Children.Add(size);
        DockPanel.SetDock(left, Dock.Left);
        bar.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(MakeButton("", "Import", "Bring in videos, sounds and pictures (Ctrl+I)", () => ImportDialog(), id: "VidImport"));
        var title = MakeButton("", "T", "New title (Ctrl+T)", AddTitle, id: "VidBinNewTitle");
        right.Children.Add(title);
        var del = MakeButton("", "", "Take the chosen file out of the project", () =>
        {
            var m = _s.Project.Media.FirstOrDefault(x => x.Id == _binSelected);
            if (m != null) RemoveMedia(m);
        }, id: "VidBinDelete");
        del.Margin = new Thickness(0);
        right.Children.Add(del);
        bar.Children.Add(right);
        Grid.SetRow(bar, 3);
        grid.Children.Add(bar);
        return grid;
    }

    private static string Duration(MediaItem m) => m.Kind == MediaKind.Image ? "still" : VidTimeline.TimeText(m.Duration);

    private void RebuildBin()
    {
        if (_bin == null) return;
        string name = _s.Project.Path != null ? Path.GetFileName(_s.Project.Path) : "Untitled.utvid";
        if (_binPath != null) _binPath.Text = name;
        string filter = _binSearch?.Text.Trim() ?? "";
        var items = _s.Project.Media.Where(m => filter.Length == 0 || m.Name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
        if (_binCount != null) _binCount.Text = items.Count + (items.Count == 1 ? " item" : " items");

        // icon view is a wrapping grid, list view a column of rows
        if (_binIcons && _bin is not WrapPanel) { _bin = new WrapPanel { Margin = new Thickness(8) }; _binScroll.Content = _bin; }
        if (!_binIcons && _bin is not StackPanel) { _bin = new StackPanel { Margin = new Thickness(8) }; _binScroll.Content = _bin; }
        _bin.Children.Clear();
        if (_s.Project.Media.Count == 0)
        {
            var hint = new TextBlock { Text = "Import media to start: Import below, Ctrl+I, or drop files here. Double-click a file to open it in the Source monitor, drag it onto the timeline.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 4, 6, 0), Width = 300 };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            _bin.Children.Add(hint);
            return;
        }
        foreach (var m in items) _bin.Children.Add(_binIcons ? BinTile(m) : BinRow(m));
    }

    private UIElement BinTile(MediaItem m)
    {
        double w = _tileWidth, thumbH = Math.Round(w * 0.58);
        var stack = new StackPanel { Width = w };
        var thumbBox = new Grid { Height = thumbH, Background = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)) };
        ImageSource? img = m.HasVideo ? _cache.Thumbnail(m, Math.Min(1.0, m.Duration / 2)) : null;
        if (img != null) thumbBox.Children.Add(new System.Windows.Controls.Image { Source = img, Stretch = Stretch.Uniform });
        else thumbBox.Children.Add(new TextBlock { Text = m.Kind == MediaKind.Audio ? "" : "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 });
        // the length sits in the corner, like Premiere's
        var badge = new Border { Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        badge.Child = new TextBlock { Text = Duration(m), FontSize = 10.5, Foreground = Brushes.White };
        thumbBox.Children.Add(badge);
        stack.Children.Add(thumbBox);
        var label = new TextBlock { Text = m.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 4, 2, 2) };
        stack.Children.Add(label);
        if (!File.Exists(m.Path))
        {
            var off = new TextBlock { Text = "file not found", FontSize = 10.5, Margin = new Thickness(2, 0, 2, 0) };
            off.SetResourceReference(TextBlock.ForegroundProperty, "ErrBrush");
            stack.Children.Add(off);
        }
        var tile = new Border { Child = stack, Padding = new Thickness(4), Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = m.Path };
        bool selected = _binSelected == m.Id;
        if (selected) { tile.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); tile.SetResourceReference(Border.BackgroundProperty, "TrackBrush"); }
        tile.MouseEnter += (_, _) => { if (_binSelected != m.Id) tile.SetResourceReference(Border.BackgroundProperty, "TrackBrush"); };
        tile.MouseLeave += (_, _) => { if (_binSelected != m.Id) tile.Background = Brushes.Transparent; };
        Point down = default; bool pressed = false;
        tile.MouseLeftButtonDown += (_, e) =>
        {
            _binSelected = m.Id;
            if (e.ClickCount == 2) { LoadSource(m); e.Handled = true; return; }
            down = e.GetPosition(tile); pressed = true;
            foreach (var other in _bin.Children.OfType<Border>()) { other.Background = Brushes.Transparent; other.BorderBrush = Brushes.Transparent; }
            tile.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            tile.SetResourceReference(Border.BackgroundProperty, "TrackBrush");
        };
        tile.MouseMove += (_, e) =>
        {
            if (!pressed || e.LeftButton != MouseButtonState.Pressed) { pressed = false; return; }
            var p = e.GetPosition(tile);
            if (Math.Abs(p.X - down.X) + Math.Abs(p.Y - down.Y) < 8) return;
            pressed = false;
            var data = new DataObject();
            data.SetData("UtylixVidMedia", m.Id);
            DragDrop.DoDragDrop(tile, data, DragDropEffects.Copy);
        };
        tile.MouseLeftButtonUp += (_, _) => pressed = false;
        var menu = new ContextMenu { Style = (Style)FindResource("ThemedMenu") };
        menu.Items.Add(Item("Open in the Source monitor", () => LoadSource(m)));
        menu.Items.Add(Item("Insert into the sequence at the playhead", () => AddToTimeline(m, _s.Playhead, null)));
        menu.Items.Add(Item("Add to the end of the sequence", () => AddToTimeline(m, EndOfTimeline(), null)));
        menu.Items.Add(new Separator { Style = (Style)FindResource("ThemedSeparator") });
        menu.Items.Add(Item("Reveal in Explorer", () => { try { Process.Start("explorer.exe", "/select,\"" + m.Path + "\""); } catch (Win32Exception) { } }));
        menu.Items.Add(Item("Clear (take out of the project)", () => RemoveMedia(m)));
        tile.ContextMenu = menu;
        return tile;
    }
}
