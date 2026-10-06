using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// The Bookmarks tab of the side panel is an editor: Add (a bookmark for the page in view, named after the selected words if there are some), Edit (name + page),
/// Delete, Up / Down, → (becomes part of the one above) and ← (back out one level). Every change is put into the open document at once (like page changes):
/// Undo takes it back, Save writes it. The list is worked on as a copy (<see cref="BmNode"/>) so a failed change leaves it as it was.
/// </summary>
public sealed partial class PdfWindow
{
    /// <summary>One bookmark while it is being edited.</summary>
    private sealed class BmNode
    {
        public string Title = "";
        public int Page;
        public List<BmNode> Children = new();
        public static BmNode From(PdfBookmark b) => new() { Title = b.Title, Page = b.Page, Children = b.Children.Select(From).ToList() };
        public PdfBookmark ToBookmark() => new(Title, Page, Children.Select(c => c.ToBookmark()).ToList());
        public BmNode Copy() => new() { Title = Title, Page = Page, Children = Children.Select(c => c.Copy()).ToList() };
    }

    private List<BmNode> _bmModel = new();
    private bool _stayOnBookmarks;                  // the next document reload comes from a change to the bookmarks: keep the tab open
    private List<int>? _bmSelectPath;               // ... and choose this entry again (indexes from the top level down)
    private TextBlock _bmHint = null!;
    private readonly List<Button> _bmNeedsChoice = new();

    private UIElement BookmarkBar()
    {
        var wrap = new WrapPanel { Margin = new Thickness(8, 4, 8, 4) };
        Button Make(string glyph, string tip, string id, Action action, bool needsChoice)
        {
            var b = new Button { Content = new TextBlock { Text = glyph, Foreground = Brushes.White, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center }, Template = BarButtonTemplate(), Height = 28, MinWidth = glyph.Length > 2 ? 52 : 30, Margin = new Thickness(0, 0, 4, 4), Focusable = false, ToolTip = tip, IsEnabled = false };
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, id);
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            b.Click += (_, _) => action();
            if (needsChoice) _bmNeedsChoice.Add(b); else _needsDocument.Add(b);
            wrap.Children.Add(b);
            return b;
        }
        Make("+ Add", "Add a bookmark for the page in view (named after the selected words, if any)", "PdfBmAdd", BmAdd, false);
        Make("Edit", "Change the name or the page of the chosen bookmark", "PdfBmEdit", BmEdit, true);
        Make("Delete", "Delete the chosen bookmark (and the ones inside it)", "PdfBmDelete", BmDelete, true);
        Make("▲", "Move up", "PdfBmUp", () => BmMove(-1), true);
        Make("▼", "Move down", "PdfBmDown", () => BmMove(1), true);
        Make("→", "Put it inside the bookmark above", "PdfBmIn", BmIndent, true);
        Make("←", "Take it out of the bookmark it is in", "PdfBmOut", BmOutdent, true);
        _bmHint = new TextBlock { Text = "No bookmarks yet. Look at a page and press + Add.", Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 0, 10, 6), Visibility = Visibility.Collapsed };
        var box = new StackPanel();
        box.Children.Add(wrap); box.Children.Add(_bmHint);
        _bookmarks.SelectedItemChanged += (_, _) => UpdateBmButtons();
        _bookmarks.MouseDoubleClick += (_, e) => { if (_bookmarks.SelectedItem is TreeViewItem && e.OriginalSource is not System.Windows.Controls.Primitives.ToggleButton) { BmEdit(); e.Handled = true; } };
        _bookmarks.PreviewKeyDown += (_, e) => { if (e.Key == Key.Delete && _bookmarks.SelectedItem is TreeViewItem) { BmDelete(); e.Handled = true; } else if (e.Key == Key.F2) { BmEdit(); e.Handled = true; } };
        return box;
    }

    private void UpdateBmButtons()
    {
        bool chosen = _bookmarks.SelectedItem is TreeViewItem { Tag: BmNode } && _pdf != null;
        foreach (var b in _bmNeedsChoice) b.IsEnabled = chosen;
    }

    /// <summary>The tree shows the model. selectPath = the entry to choose again.</summary>
    private void RebuildBookmarkTree(List<int>? selectPath)
    {
        _bookmarks.Items.Clear();
        for (int i = 0; i < _bmModel.Count; i++) _bookmarks.Items.Add(BmItem(_bmModel[i], 0, _bmModel.Count));
        _bmHint.Visibility = _bmModel.Count == 0 && _pdf != null ? Visibility.Visible : Visibility.Collapsed;
        if (selectPath is { Count: > 0 })
        {
            ItemsControl level = _bookmarks;
            TreeViewItem? found = null;
            foreach (int i in selectPath)
            {
                if (i < 0 || i >= level.Items.Count || level.Items[i] is not TreeViewItem t) break;
                t.IsExpanded = true; found = t; level = t;
            }
            if (found != null) { found.IsSelected = true; found.BringIntoView(); }
        }
        UpdateBmButtons();
    }

    private static TreeViewItem BmItem(BmNode b, int depth, int siblings)
    {
        var item = new TreeViewItem
        {
            Header = new TextBlock { Text = b.Title.Length == 0 ? "(untitled)" : b.Title, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 - depth * 12, ToolTip = b.Title + (b.Page >= 0 ? $"  (page {b.Page + 1})" : "") },
            Tag = b, IsExpanded = depth == 0 && b.Children.Count <= 12, Foreground = Brushes.White,
        };
        foreach (var c in b.Children) item.Items.Add(BmItem(c, depth + 1, b.Children.Count));
        return item;
    }

    private List<int>? BmChosenPath()
    {
        if (_bookmarks.SelectedItem is not TreeViewItem t) return null;
        var path = new List<int>();
        DependencyObject? cur = t;
        while (cur is TreeViewItem item)
        {
            var parent = ItemsControl.ItemsControlFromItemContainer(item);
            if (parent == null) return null;
            path.Insert(0, parent.Items.IndexOf(item));
            cur = parent as TreeViewItem;
        }
        return path;
    }

    private static List<BmNode> BmListAt(List<BmNode> root, List<int> path, int depth)
    {
        var list = root;
        for (int i = 0; i < depth; i++) list = list[path[i]].Children;
        return list;
    }

    private List<BmNode> BmCopy() => _bmModel.Select(n => n.Copy()).ToList();

    private void BmApply(List<BmNode> model, List<int>? choose, string message)
    {
        if (_pdf == null) return;
        var list = model.Select(n => n.ToBookmark()).ToList();
        _stayOnBookmarks = true; _bmSelectPath = choose;
        if (!PageOp(p => p.SetBookmarks(list), new[] { _current }, message, keepView: true)) { _stayOnBookmarks = false; _bmSelectPath = null; }
    }

    /// <summary>Asks for the name and the page. Null = cancelled.</summary>
    private (string Title, int Page)? AskBookmark(string title, int page, string heading)
    {
        var dlg = new Window
        {
            Title = heading, Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)Application.Current.FindResource("BgBrush"), Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 13.5,
        };
        WindowTheme.DarkTitleBar(dlg);
        var name = new TextBox { Text = title, Margin = new Thickness(0, 4, 0, 0) };
        var number = new TextBox { Text = (page + 1).ToString(), Width = 90, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(name, "PdfBmTitle");
        System.Windows.Automation.AutomationProperties.SetAutomationId(number, "PdfBmPage");
        var ok = new Button { Content = "OK", Style = (Style)Application.Current.FindResource("DialogPrimary"), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.FindResource("DialogButton"), IsCancel = true };
        System.Windows.Automation.AutomationProperties.SetAutomationId(ok, "PdfBmOk");
        bool accepted = false;
        ok.Click += (_, _) => { accepted = true; dlg.Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = "Name", FontWeight = FontWeights.SemiBold });
        root.Children.Add(name);
        root.Children.Add(new TextBlock { Text = "Goes to page", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 0) });
        root.Children.Add(number);
        root.Children.Add(buttons);
        dlg.Content = root;
        dlg.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
        dlg.ShowDialog();
        if (!accepted) return null;
        string t = name.Text.Trim();
        if (t.Length == 0) return null;
        if (!int.TryParse(number.Text.Trim(), out int n) || _pdf == null) return null;
        return (t, Math.Clamp(n, 1, _pdf.PageCount) - 1);
    }

    private void BmAdd()
    {
        if (_pdf == null) return;
        string title = "Page " + (_current + 1);
        int page = _current;
        if (HasSelection && Text(_selPage) is { } t)
        {
            var (s, e) = SelectionRange;
            string words = string.Join(" ", t.Slice(s, e).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (words.Length > 0) { title = words.Length > 80 ? words[..80].TrimEnd() + "…" : words; page = _selPage; }
        }
        if (AskBookmark(title, page, "New bookmark") is not { } r) return;
        var model = BmCopy();
        var path = BmChosenPath();
        List<int> at;
        if (path == null) { model.Add(new BmNode { Title = r.Title, Page = r.Page }); at = new List<int> { model.Count - 1 }; }
        else
        {
            var list = BmListAt(model, path, path.Count - 1);
            int index = path[^1] + 1;
            list.Insert(index, new BmNode { Title = r.Title, Page = r.Page });
            at = path.Take(path.Count - 1).Append(index).ToList();
        }
        BmApply(model, at, $"Bookmark \"{r.Title}\" added. Undo takes it back until you save");
    }

    private void BmEdit()
    {
        if (BmChosenPath() is not { } path) return;
        var model = BmCopy();
        var node = BmListAt(model, path, path.Count - 1)[path[^1]];
        if (AskBookmark(node.Title, Math.Max(0, node.Page), "Bookmark") is not { } r) return;
        node.Title = r.Title; node.Page = r.Page;
        BmApply(model, path, "Bookmark changed");
    }

    private void BmDelete()
    {
        if (BmChosenPath() is not { } path) return;
        var model = BmCopy();
        var list = BmListAt(model, path, path.Count - 1);
        string name = list[path[^1]].Title;
        list.RemoveAt(path[^1]);
        var choose = list.Count == 0 ? (path.Count > 1 ? path.Take(path.Count - 1).ToList() : null) : path.Take(path.Count - 1).Append(Math.Min(path[^1], list.Count - 1)).ToList();
        BmApply(model, choose, $"Bookmark \"{name}\" deleted. Undo brings it back until you save");
    }

    private void BmMove(int direction)
    {
        if (BmChosenPath() is not { } path) return;
        var model = BmCopy();
        var list = BmListAt(model, path, path.Count - 1);
        int from = path[^1], to = from + direction;
        if (to < 0 || to >= list.Count) { Toast(direction < 0 ? "It is the first one already" : "It is the last one already"); return; }
        (list[from], list[to]) = (list[to], list[from]);
        BmApply(model, path.Take(path.Count - 1).Append(to).ToList(), "Bookmark moved");
    }

    private void BmIndent()
    {
        if (BmChosenPath() is not { } path) return;
        int index = path[^1];
        if (index == 0) { Toast("There is no bookmark above it to put it into"); return; }
        var model = BmCopy();
        var list = BmListAt(model, path, path.Count - 1);
        var node = list[index];
        list.RemoveAt(index);
        var into = list[index - 1];
        into.Children.Add(node);
        BmApply(model, path.Take(path.Count - 1).Append(index - 1).Append(into.Children.Count - 1).ToList(), "Bookmark moved inside the one above");
    }

    private void BmOutdent()
    {
        if (BmChosenPath() is not { } path) return;
        if (path.Count < 2) { Toast("It is not inside another bookmark"); return; }
        var model = BmCopy();
        var list = BmListAt(model, path, path.Count - 1);
        var node = list[path[^1]];
        list.RemoveAt(path[^1]);
        var outer = BmListAt(model, path, path.Count - 2);
        int at = path[^2] + 1;
        outer.Insert(at, node);
        BmApply(model, path.Take(path.Count - 2).Append(at).ToList(), "Bookmark moved out one level");
    }
}
