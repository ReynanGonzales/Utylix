using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Changing the pages themselves, from the strip at the side: turn left / right, move up / down (or drag), delete. They are made in the open
/// document at once and written when you save; "Undo" goes back step by step (each step keeps the whole document as it was, a few at most).
/// Edits that were added to the pages before are put into the pages first (their positions would not fit the new pages), so Undo then starts from there.
/// </summary>
public sealed partial class PdfWindow
{
    private const string DragFormat = "utylix-pages";
    private readonly List<byte[]> _pageUndo = new(), _pageRedo = new();
    private StackPanel _pageTools = null!;
    private Button _turnLeft = null!, _turnRight = null!, _moveUp = null!, _moveDown = null!, _deletePages = null!, _morePages = null!;
    private static readonly SolidColorBrush DropLine = new(Color.FromRgb(0x5B, 0x8D, 0xEF));

    // ---------- the buttons above the small pages ----------
    private UIElement PageTools()
    {
        Button Make(string glyph, string font, double size, string tip, string id, Action action)
        {
            var b = new Button
            {
                Content = new TextBlock { Text = glyph, FontFamily = new FontFamily(font), FontSize = size, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                Template = BarButtonTemplate(), Width = 26, Height = 28, Padding = new Thickness(0), Margin = new Thickness(0, 0, 3, 0), Focusable = false, ToolTip = tip, IsEnabled = false,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(b, id);
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            b.Click += (_, _) => action();
            return b;
        }
        _turnLeft = Make("↺", "Segoe UI Symbol", 20, "Turn the page(s) a quarter to the left (counter-clockwise). Saved in the PDF.", "PdfPagesTurnLeft", () => TurnSelectedPages(-1));
        _turnRight = Make("↻", "Segoe UI Symbol", 20, "Turn the page(s) a quarter to the right (clockwise). Saved in the PDF.", "PdfPagesTurnRight", () => TurnSelectedPages(1));
        _moveUp = Make("", "Segoe MDL2 Assets", 12, "Move the page(s) up (you can also drag them)", "PdfPagesUp", () => MoveSelectedPages(-1));
        _moveDown = Make("", "Segoe MDL2 Assets", 12, "Move the page(s) down (you can also drag them)", "PdfPagesDown", () => MoveSelectedPages(1));
        _deletePages = Make("", "Segoe MDL2 Assets", 13, "Delete the page(s) (Delete key). Undo brings them back until you save.", "PdfPagesDelete", DeleteSelectedPages);
        _morePages = Make("", "Segoe MDL2 Assets", 14, "More: add pages from a file, take pages out, split, save as pictures, make searchable (OCR)", "PdfPagesMore", () => ShowPagesMenu());
        _pageTools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 4, 4, 4) };
        foreach (var b in new[] { _turnLeft, _turnRight, _moveUp, _moveDown, _deletePages, _morePages }) _pageTools.Children.Add(b);
        return _pageTools;
    }

    /// <summary>Hooks up choosing several pages, dragging them to a new place and the right-click menu.</summary>
    private void SetUpStrip()
    {
        _strip.SelectionMode = SelectionMode.Extended;
        _strip.AllowDrop = true;
        _strip.PreviewMouseLeftButtonDown += (_, e) => { _stripArmed = ThumbOf(e.OriginalSource) != null; _stripDown = e.GetPosition(_strip); };
        _strip.PreviewMouseLeftButtonUp += (_, _) => _stripArmed = false;
        _strip.PreviewMouseMove += StripMove;
        _strip.DragOver += StripDragOver;
        _strip.DragLeave += (_, _) => ClearDropLine();
        _strip.Drop += StripDrop;
        _strip.PreviewMouseRightButtonDown += (_, e) =>
        {
            var t = ThumbOf(e.OriginalSource);
            if (t != null && !_strip.SelectedItems.Contains(t)) { _strip.SelectedItems.Clear(); _strip.SelectedItems.Add(t); }
        };

        var menu = new ContextMenu();
        void Item(string text, Action action) { var m = new MenuItem { Header = text }; m.Click += (_, _) => action(); menu.Items.Add(m); }
        Item("Turn left", () => TurnSelectedPages(-1));
        Item("Turn right", () => TurnSelectedPages(1));
        menu.Items.Add(new Separator());
        Item("Move up", () => MoveSelectedPages(-1));
        Item("Move down", () => MoveSelectedPages(1));
        Item("Move to the start", () => MoveSelectedPagesTo(0));
        Item("Move to the end", () => MoveSelectedPagesTo(int.MaxValue));
        menu.Items.Add(new Separator());
        Item("Delete", DeleteSelectedPages);
        menu.Items.Add(new Separator());
        Item("Add pages from a file…", InsertPagesFromFiles);
        Item("Take the page(s) out as a new PDF…", ExtractSelectedPages);
        _strip.ContextMenu = Themed(menu);
    }

    private List<int> SelectedPages()
    {
        var list = _strip.SelectedItems.Cast<PdfThumb>().Select(t => t.Index).OrderBy(i => i).ToList();
        if (list.Count == 0 && _current >= 0 && _current < _thumbs.Count) list.Add(_current);
        return list;
    }

    /// <summary>The buttons follow what is chosen: nothing moves up from the top, and the last page can't be deleted.</summary>
    private void UpdatePageTools()
    {
        if (_turnLeft == null) return;
        bool has = _pdf != null && _thumbs.Count > 0;
        var sel = has ? SelectedPages() : new List<int>();
        _turnLeft.IsEnabled = _turnRight.IsEnabled = has && sel.Count > 0;
        _moveUp.IsEnabled = has && sel.Count > 0 && sel[0] > 0;
        _moveDown.IsEnabled = has && sel.Count > 0 && sel[^1] < _thumbs.Count - 1;
        _deletePages.IsEnabled = has && sel.Count > 0 && sel.Count < _thumbs.Count;
        _morePages.IsEnabled = has;
    }

    // ---------- doing it ----------
    private void TurnSelectedPages(int turns)
    {
        var sel = SelectedPages();
        PageOp(p => p.TurnPages(sel, turns), sel, sel.Count == 1 ? $"Page {sel[0] + 1} turned {(turns > 0 ? "right" : "left")}" : $"{sel.Count} pages turned {(turns > 0 ? "right" : "left")}");
    }

    private void MoveSelectedPages(int by)
    {
        var sel = SelectedPages();
        if (by < 0 && sel[0] == 0 || by > 0 && sel[^1] == _thumbs.Count - 1) return;
        MoveSelectedPagesTo(by < 0 ? sel[0] - 1 : sel[0] + 1);
    }

    /// <summary>The chosen pages go to this place (the first of them ends up there; int.MaxValue = the end).</summary>
    private void MoveSelectedPagesTo(int destination) => MovePagesTo(SelectedPages(), destination);

    private void MovePagesTo(IReadOnlyList<int> sel, int destination)
    {
        if (sel.Count == 0) return;
        destination = Math.Clamp(destination, 0, _thumbs.Count - sel.Count);
        bool together = sel[^1] - sel[0] == sel.Count - 1;
        if (together && destination == sel[0]) return;                       // (it would stay where it is)
        var after = Enumerable.Range(destination, sel.Count).ToList();
        PageOp(p => p.MovePages(sel, destination), after, sel.Count == 1 ? $"Page {sel[0] + 1} is now page {destination + 1}" : $"{sel.Count} pages moved");
    }

    private void DeleteSelectedPages()
    {
        var sel = SelectedPages();
        if (_pdf == null || sel.Count == 0) return;
        if (sel.Count >= _thumbs.Count) { Toast("A PDF needs at least one page: this one can't be deleted"); return; }
        int stay = Math.Min(sel[0], _thumbs.Count - sel.Count - 1);
        PageOp(p => p.DeletePages(sel), new[] { stay }, (sel.Count == 1 ? $"Page {sel[0] + 1} deleted" : $"{sel.Count} pages deleted") + " (Undo brings it back until you save)");
    }

    /// <summary>Makes a change to the pages of the document: edits so far go into the pages, the old state is kept for Undo, the pages are drawn again.</summary>
    private void PageOp(Action<PdfFile> change, IReadOnlyList<int> select, string message) => PageOp(change, () => select, () => message);

    /// <summary>As above, but the pages to choose afterwards and the message are worked out after the change (inserting pages: how many came in).</summary>
    private void PageOp(Action<PdfFile> change, Func<IReadOnlyList<int>> select, Func<string> message)
    {
        if (!PreparePageOp()) return;
        var pdf = _pdf!;
        byte[]? before = null;
        try
        {
            before = pdf.SaveToBytes();
            change(pdf);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or OutOfMemoryException or ObjectDisposedException or PdfProtectedException)
        {
            if (before != null) { try { pdf.Restore(before); } catch (Exception) { } }
            RefreshAfterPageChange(Array.Empty<int>());
            Toast("Couldn't do that: " + e.Message);
            return;
        }
        _pageUndo.Add(before); _pageRedo.Clear();
        TrimPageUndo();
        _dirty = true;
        UpdateTitle();
        RefreshAfterPageChange(select());
        Toast(message());
    }

    /// <summary>Makes ready for a change of the pages: editing is on, and what was added to the pages is written into them. False = not now.</summary>
    private bool PreparePageOp()
    {
        if (_pdf == null || _path == null) return false;
        CloseTextBox(commit: true);
        if (!_editing) { EnterEditing(); if (!_editing) return false; }
        return CommitItems();
    }

    /// <summary>What was added to the pages (text, shapes ...) is written into them, so the pages can be copied, moved or split with it. False = not now.</summary>
    private bool CommitItems()
    {
        if (_pdf == null) return false;
        CloseTextBox(commit: true);
        if (_items.Any(i => i.Marks().OfType<PdfRedactMark>().Any()))
        {
            UMessage.Show(this, "There are black boxes (Redact) that are not saved yet. Save them first (Ctrl+S): redacting is checked when it is saved, and it has to happen before the pages are changed.", "Pages");
            return false;
        }
        if (_items.Count > 0)
        {
            try { PdfMarkWriter.Apply(_pdf, _items.SelectMany(i => i.Marks()).ToList()); }
            catch (Exception e) when (e is IOException or InvalidOperationException or OutOfMemoryException or ObjectDisposedException)
            {
                Toast("Couldn't put your changes into the pages: " + e.Message);
                return false;
            }
            _items.Clear(); _undo.Clear(); _redo.Clear(); _selected = null;
            _dirty = true; UpdateTitle();
            foreach (var p in _pages) p.Overlay.Children.Clear();
            RedrawPages();
        }
        return true;
    }

    private void TrimPageUndo()
    {
        while (_pageUndo.Count > 1 && (_pageUndo.Count > 15 || _pageUndo.Sum(b => (long)b.Length) > 400_000_000)) _pageUndo.RemoveAt(0);
    }

    /// <summary>Undo / Redo of a change of the pages. False when there is none.</summary>
    private bool PageHistory(bool redo)
    {
        var from = redo ? _pageRedo : _pageUndo;
        var to = redo ? _pageUndo : _pageRedo;
        if (_pdf == null || from.Count == 0) return false;
        try
        {
            byte[] now = _pdf.SaveToBytes();
            byte[] target = from[^1];
            _pdf.Restore(target);
            from.RemoveAt(from.Count - 1);
            to.Add(now);
        }
        catch (Exception e) when (e is IOException or OutOfMemoryException or ObjectDisposedException)
        {
            Toast("Couldn't go back: " + e.Message);
            return true;
        }
        _undo.Clear(); _redo.Clear(); _items.Clear(); _selected = null;
        _dirty = true;
        UpdateTitle();
        RefreshAfterPageChange(Array.Empty<int>());
        Toast(redo ? "Page change redone" : "Page change undone");
        return true;
    }

    /// <summary>The pages are laid out again after their number, order or turn changed.</summary>
    private void RefreshAfterPageChange(IReadOnlyList<int> select)
    {
        var pdf = _pdf;
        if (pdf == null) return;
        _sizes = Enumerable.Range(0, pdf.PageCount).Select(pdf.PageSize).ToArray();
        _generation++;
        int keep = Math.Clamp(select.Count > 0 ? select[0] : _current, 0, Math.Max(0, _sizes.Length - 1));
        _current = keep;
        _info.Text = $"{System.IO.Path.GetFileName(_path)}   ·   {pdf.PageCount} page{(pdf.PageCount == 1 ? "" : "s")}   ·   {PdfReduceWindow.Bytes(pdf.Length)}";
        _pageCount.Text = "/ " + pdf.PageCount;
        BuildPages();
        OnDocumentLoaded();
        UpdateLayout();
        ApplyZoom(_fit == Fit.None ? _zoom : FitZoom(_fit), keepPlace: false);
        GoTo(keep);
        _syncingStrip = true;
        try
        {
            _strip.SelectedItems.Clear();
            foreach (int i in select) if (i >= 0 && i < _thumbs.Count) _strip.SelectedItems.Add(_thumbs[i]);
            if (select.Count > 0 && select[0] < _thumbs.Count) _strip.ScrollIntoView(_thumbs[select[0]]);
        }
        finally { _syncingStrip = false; }
        UpdatePageTools();
        UpdateEditButtons(); UpdateProperties();
    }

    // ---------- dragging the small pages ----------
    private Point _stripDown;
    private bool _stripArmed;
    private DateTime _lastStripScroll;

    private PdfThumb? ThumbOf(object? source)
    {
        var d = source as DependencyObject;
        while (d != null && d is not ListBoxItem) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return (d as ListBoxItem)?.DataContext as PdfThumb;
    }

    private void StripMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !_stripArmed) return;
        var p = e.GetPosition(_strip);
        if (Math.Abs(p.X - _stripDown.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - _stripDown.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _stripArmed = false;
        var t = ThumbOf(e.OriginalSource);
        if (t == null || _pdf == null) return;
        if (!_strip.SelectedItems.Contains(t)) { _strip.SelectedItems.Clear(); _strip.SelectedItems.Add(t); }
        var data = new DataObject(DragFormat, SelectedPages().ToArray());
        try { DragDrop.DoDragDrop(_strip, data, DragDropEffects.Move); }
        finally { ClearDropLine(); }
    }

    private void StripDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat)) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        ShowDropLine(DropIndex(e));
        // near the top or bottom edge the list scrolls
        var pos = e.GetPosition(_strip);
        if ((DateTime.UtcNow - _lastStripScroll).TotalMilliseconds > 120 && (pos.Y < 36 || pos.Y > _strip.ActualHeight - 36) && FindScroller(_strip) is { } sv)
        {
            _lastStripScroll = DateTime.UtcNow;
            if (pos.Y < 36) sv.LineUp(); else sv.LineDown();
        }
    }

    private void StripDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragFormat) || e.Data.GetData(DragFormat) is not int[] moved) return;
        int target = DropIndex(e);
        ClearDropLine();
        e.Handled = true;
        var sel = moved.OrderBy(i => i).ToList();
        int dest = target - sel.Count(i => i < target);
        Dispatcher.BeginInvoke(new Action(() => MovePagesTo(sel, dest)));       // (after the drag has ended)
    }

    /// <summary>Between which small pages the mouse is: the place (0 .. count) the dragged pages would go in front of.</summary>
    private int DropIndex(DragEventArgs e)
    {
        double y = e.GetPosition(_strip).Y;
        int lastSeen = -1;
        for (int i = 0; i < _thumbs.Count; i++)
        {
            if (_strip.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem c || !c.IsVisible) continue;
            lastSeen = i;
            double middle = c.TranslatePoint(new Point(0, c.ActualHeight / 2), _strip).Y;
            if (y < middle) return i;
        }
        return lastSeen < 0 ? _thumbs.Count : lastSeen + 1;
    }

    private PdfThumb? _dropMarked;
    private bool _dropMarkedBottom;

    private void ShowDropLine(int index)
    {
        ClearDropLine();
        if (_thumbs.Count == 0) return;
        if (index >= _thumbs.Count) { _dropMarked = _thumbs[^1]; _dropMarkedBottom = true; _dropMarked.BottomLine = DropLine; }
        else { _dropMarked = _thumbs[Math.Max(0, index)]; _dropMarkedBottom = false; _dropMarked.TopLine = DropLine; }
    }

    private void ClearDropLine()
    {
        if (_dropMarked == null) return;
        if (_dropMarkedBottom) _dropMarked.BottomLine = Brushes.Transparent; else _dropMarked.TopLine = Brushes.Transparent;
        _dropMarked = null;
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(root); i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScroller(child) is { } found) return found;
        }
        return null;
    }
}
