using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// The text in Utylix PDF: select and copy it, search it (Ctrl+F), follow links, read notes, the bookmarks panel, and the comments on
/// text (highlight / underline / strike out the selected words, sticky notes) - saved as real PDF comments other readers show too.
/// </summary>
public sealed partial class PdfWindow
{
    // ---------- comments on text ----------
    private sealed class TextMarkupItem : EditItem
    {
        public int Subtype;                       // Pdfium.AnnotHighlight / AnnotUnderline / AnnotStrikeOut
        public List<Rect> Rects = new();
        public override Rect Bounds { get { var r = Rect.Empty; foreach (var b in Rects) r.Union(b); return r; } }
        public override bool Hit(Point p) => Rects.Any(r => Inflate(r, 1).Contains(p));
        public override EditItem Clone() { var c = (TextMarkupItem)MemberwiseClone(); c.Rects = Rects.ToList(); return c; }
        public override FrameworkElement Build()
        {
            var canvas = new Canvas();
            foreach (var r in Rects)
            {
                double h = Subtype == Pdfium.AnnotHighlight ? r.Height : Math.Max(0.8, r.Height * 0.07);
                double y = Subtype == Pdfium.AnnotHighlight ? r.Y : Subtype == Pdfium.AnnotUnderline ? r.Bottom - h * 1.5 : r.Y + r.Height * 0.52 - h / 2;
                var mark = new Rectangle { Width = r.Width, Height = h, Fill = new SolidColorBrush(Subtype == Pdfium.AnnotHighlight ? Color.FromArgb(105, Color.R, Color.G, Color.B) : Color) };
                Canvas.SetLeft(mark, r.X); Canvas.SetTop(mark, y);
                canvas.Children.Add(mark);
            }
            return canvas;
        }
        public override IEnumerable<PdfMark> Marks() { yield return new PdfAnnotMark(Page, Subtype, Rects, Color, null); }
        public override void MoveBy(Vector d) { }                   // (it belongs to its words)
        public override void ResizeTo(Rect r) { }
    }

    private sealed class NoteItem : EditItem
    {
        public Point At;
        public string Text = "";
        public override Rect Bounds => new(At, new Size(18, 18));
        public override bool KeepAspect => true;
        public override EditItem Clone() => (NoteItem)MemberwiseClone();
        public override FrameworkElement Build()
        {
            var icon = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(2.5), Background = new SolidColorBrush(Color),
                BorderBrush = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), BorderThickness = new Thickness(0.6),
                Child = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            Canvas.SetLeft(icon, At.X); Canvas.SetTop(icon, At.Y);
            return icon;
        }
        public override IEnumerable<PdfMark> Marks() { if (Text.Trim().Length > 0) yield return new PdfAnnotMark(Page, Pdfium.AnnotText, new[] { Bounds }, Color, Text.Trim()); }
        public override void MoveBy(Vector d) => At += d;
        public override void ResizeTo(Rect r) => At = r.TopLeft;
    }

    /// <summary>Writes or changes a sticky note's text (a small window); an empty note is removed.</summary>
    private void OpenNote(NoteItem note, bool isNew)
    {
        var box = new TextBox { Text = note.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 130, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top };
        var dlg = new Window
        {
            Title = isNew ? "New note" : "Note", Width = 380, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)Application.Current.FindResource("BgBrush"), FontFamily = new FontFamily("Segoe UI"), FontSize = 13.5,
        };
        WindowTheme.DarkTitleBar(dlg);
        var ok = new Button { Content = "OK", Style = (Style)Application.Current.FindResource("DialogPrimary"), IsDefault = false, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.FindResource("DialogButton"), IsCancel = true };
        var remove = new Button { Content = "Delete note", Style = (Style)Application.Current.FindResource("DialogButton"), Visibility = isNew ? Visibility.Collapsed : Visibility.Visible };
        bool? result = null;
        ok.Click += (_, _) => { result = true; dlg.Close(); };
        remove.Click += (_, _) => { result = false; dlg.Close(); };
        var buttons = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(ok); right.Children.Add(cancel);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right); buttons.Children.Add(remove);
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = "Your note (other PDF readers show it as a comment):", Margin = new Thickness(0, 0, 0, 8), Opacity = 0.8 });
        root.Children.Add(box); root.Children.Add(buttons);
        dlg.Content = root;
        dlg.Loaded += (_, _) => { box.Focus(); box.CaretIndex = box.Text.Length; };
        dlg.ShowDialog();
        if (result == null) return;
        string text = box.Text.Trim();
        if (result == false || text.Length == 0)
        {
            if (!isNew) { Snapshot(); _items.Remove(note); if (_selected == note) _selected = null; RenderItems(note.Page); UpdateEditButtons(); }
            return;
        }
        if (isNew) { note.Text = text; Add(note); }
        else if (text != note.Text) { Snapshot(); note.Text = text; RenderItems(note.Page); }
    }

    // ---------- selecting text ----------
    private int _selPage = -1, _selAnchor, _selCaret;
    private bool _textDrag;
    private int? _markupKind;
    private PageView? _textPage;

    private PdfPageText? Text(int page)
    {
        try { return _pdf?.GetText(page); }
        catch (Exception e) when (e is ObjectDisposedException or System.IO.IOException) { return null; }
    }

    private bool TextUnder(PageView pv, Point p) => Text(pv.Index)?.IndexAt(p, 2) >= 0;

    /// <summary>Between which letters a point is (the letter nearest, then its left or right half).</summary>
    private static int Boundary(PdfPageText t, Point p)
    {
        int i = t.IndexNear(p);
        if (i < 0) return 0;
        var b = t.Boxes[i];
        return p.X > b.X + b.Width / 2 && p.Y >= b.Top - 2 ? i + 1 : i;
    }

    private (int Start, int End) SelectionRange => (Math.Min(_selAnchor, _selCaret), Math.Max(_selAnchor, _selCaret));
    private bool HasSelection => _selPage >= 0 && _selAnchor != _selCaret;

    private void TextDown(PageView pv, MouseButtonEventArgs e, int? markup)
    {
        // a form field (not while marking text): fill it
        if (markup == null && !_editing && FieldDown(pv, e)) return;
        var p = e.GetPosition(pv.Overlay);
        var t = Text(pv.Index);
        // a link (not while marking text)
        if (markup == null && !_editing && t != null && t.Links.LastOrDefault(l => l.Box.Contains(p)) is { } link) { Follow(link); e.Handled = true; return; }
        int oldPage = _selPage;
        _selPage = -1;
        if (oldPage >= 0) DrawTextLayer(oldPage);
        if (t == null || t.Text.Length == 0)
        {
            if (markup != null) Toast("This page has no text to mark (it's probably a scanned picture)");
            return;
        }
        _selPage = pv.Index;
        if (e.ClickCount >= 2 && markup == null)
        {
            int i = t.IndexAt(p, 2);
            if (i >= 0) { var (s, en) = e.ClickCount == 2 ? t.WordAt(i) : LineAt(t, i); _selAnchor = s; _selCaret = en; DrawTextLayer(pv.Index); }
            e.Handled = true;
            return;
        }
        _selAnchor = _selCaret = Boundary(t, p);
        _textDrag = true; _markupKind = markup; _textPage = pv;
        pv.Overlay.CaptureMouse();
        e.Handled = true;
        DrawTextLayer(pv.Index);
    }

    private static (int, int) LineAt(PdfPageText t, int i)
    {
        int s = i, e = i;
        while (s > 0 && t.Text[s - 1] is not ('\r' or '\n')) s--;
        while (e < t.Text.Length && t.Text[e] is not ('\r' or '\n')) e++;
        return (s, e);
    }

    private string? _hoverTip;

    private void TextMove(PageView pv, MouseEventArgs e)
    {
        var p = e.GetPosition(pv.Overlay);
        if (_textDrag)
        {
            if (_textPage != pv || Text(pv.Index) is not { } t) return;
            int caret = Boundary(t, p);
            if (caret == _selCaret) return;
            _selCaret = caret;
            DrawTextLayer(pv.Index);
            return;
        }
        if (_editing) return;
        // pointing: links show a hand and where they go, notes show their text, text shows the I-beam
        var text = Text(pv.Index);
        string? tip = null;
        Cursor cursor = Cursors.Arrow;
        if (FieldTip(pv.Index, p, out var fieldCursor) is string fieldTip) { tip = fieldTip; cursor = fieldCursor; }
        else if (text != null)
        {
            if (text.Links.LastOrDefault(l => l.Box.Contains(p)) is { } link) { cursor = Cursors.Hand; tip = link.Uri ?? $"Go to page {link.Page + 1}"; }
            else if (text.Notes.LastOrDefault(n => n.Box.Contains(p)) is { } note) tip = note.Text;
            else if (text.IndexAt(p, 1) >= 0) cursor = Cursors.IBeam;
        }
        pv.Overlay.Cursor = cursor;
        if (tip != _hoverTip) { _hoverTip = tip; pv.Overlay.ToolTip = tip == null ? null : new ToolTip { Content = new TextBlock { Text = tip, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 } }; }
    }

    private void TextUp(PageView pv, MouseButtonEventArgs e)
    {
        if (!_textDrag) return;
        _textDrag = false;
        pv.Overlay.ReleaseMouseCapture();
        var kind = _markupKind;
        _markupKind = null;
        if (kind is int k && HasSelection) MarkSelection(k);
    }

    private void ClearTextSelection()
    {
        int page = _selPage;
        _selPage = -1;
        if (page >= 0) DrawTextLayer(page);
    }

    /// <summary>Draws a page's search results and text selection.</summary>
    private void DrawTextLayer(int page)
    {
        if (page < 0 || page >= _pages.Count) return;
        var layer = _pages[page].TextLayer;
        layer.Children.Clear();
        var t = Text(page);
        if (t == null) return;
        for (int h = 0; h < _hits.Count; h++)
        {
            var hit = _hits[h];
            if (hit.Page != page) continue;
            bool current = h == _hitIndex;
            foreach (var r in t.LineBoxes(hit.Start, hit.End))
                Add(r, current ? Color.FromArgb(150, 255, 140, 0) : Color.FromArgb(110, 255, 220, 0));
        }
        if (_selPage == page && HasSelection)
        {
            var (s, e) = SelectionRange;
            foreach (var r in t.LineBoxes(s, e)) Add(r, Color.FromArgb(90, 40, 120, 255));
        }
        void Add(Rect r, Color c)
        {
            var box = new Rectangle { Width = r.Width, Height = r.Height, Fill = new SolidColorBrush(c) };
            Canvas.SetLeft(box, r.X); Canvas.SetTop(box, r.Y);
            layer.Children.Add(box);
        }
    }

    private void CopySelection()
    {
        if (!HasSelection || Text(_selPage) is not { } t) return;
        var (s, e) = SelectionRange;
        try { Clipboard.SetText(t.Slice(s, e)); Toast("Copied"); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException) { Toast("The clipboard is busy, try again"); }
    }

    private void SelectAllOnPage()
    {
        if (_pages.Count == 0 || Text(_current) is not { } t) return;
        if (t.Text.Length == 0) { Toast("This page has no text (it's probably a scanned picture)"); return; }
        ClearTextSelection();
        _selPage = _current; _selAnchor = 0; _selCaret = t.Text.Length;
        DrawTextLayer(_current);
    }

    /// <summary>Highlight / underline / strike out the selected words (starts editing when needed).</summary>
    private void MarkSelection(int subtype)
    {
        if (!HasSelection || Text(_selPage) is not { } t) return;
        var (s, e) = SelectionRange;
        int page = _selPage;
        var rects = t.LineBoxes(s, e);
        if (rects.Count == 0) return;
        if (!_editing) { EnterEditing(); if (!_editing) return; }
        var tool = subtype == Pdfium.AnnotHighlight ? EditTool.Highlight : subtype == Pdfium.AnnotUnderline ? EditTool.Underline : EditTool.Strike;
        Add(new TextMarkupItem { Page = page, Subtype = subtype, Rects = rects, Color = _toolColors[tool] });
        ClearTextSelection();
    }

    private void PageMenu(PageView pv, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(pv.Overlay);
        // right-click in a selection keeps it; elsewhere on a word, selects the word
        if (!(HasSelection && _selPage == pv.Index && Text(pv.Index) is { } t0 && t0.LineBoxes(SelectionRange.Start, SelectionRange.End).Any(r => r.Contains(p))))
        {
            var t = Text(pv.Index);
            int i = t?.IndexAt(p, 1) ?? -1;
            ClearTextSelection();
            if (t != null && i >= 0) { var (s, en) = t.WordAt(i); _selPage = pv.Index; _selAnchor = s; _selCaret = en; DrawTextLayer(pv.Index); }
        }
        var menu = new ContextMenu();
        void Item(string text, string keys, bool enabled, Action action)
        {
            var m = new MenuItem { Header = text, InputGestureText = keys, IsEnabled = enabled };
            m.Click += (_, _) => action();
            menu.Items.Add(m);
        }
        bool sel = HasSelection;
        Item("Copy", "Ctrl+C", sel, CopySelection);
        Item("Select all text on this page", "Ctrl+A", true, () => { _current = pv.Index; SelectAllOnPage(); });
        menu.Items.Add(new Separator());
        Item("Highlight", "", sel, () => MarkSelection(Pdfium.AnnotHighlight));
        Item("Underline", "", sel, () => MarkSelection(Pdfium.AnnotUnderline));
        Item("Strike out", "", sel, () => MarkSelection(Pdfium.AnnotStrikeOut));
        Item("Add a note here", "", true, () =>
        {
            if (!_editing) { EnterEditing(); if (!_editing) return; }
            OpenNote(new NoteItem { Page = pv.Index, At = new Point(p.X - 9, p.Y - 9), Color = _toolColors[EditTool.Note] }, isNew: true);
        });
        menu.Items.Add(new Separator());
        Item("Search…", "Ctrl+F", true, OpenSearch);
        Themed(menu);
        menu.PlacementTarget = pv.Overlay;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// A menu in the app's colours. (A stock menu takes the white text of this dark window but keeps its light background: unreadable.)
    /// </summary>
    private static ContextMenu Themed(ContextMenu menu)
    {
        menu.Style = (Style)Application.Current.FindResource("ThemedMenu");
        foreach (var item in menu.Items)
        {
            if (item is MenuItem m) m.Style = (Style)Application.Current.FindResource("ThemedMenuItem");
            else if (item is Separator s) s.Style = (Style)Application.Current.FindResource("ThemedSeparator");
        }
        return menu;
    }

    // ---------- links ----------
    private void Follow(PdfLink link)
    {
        if (link.Page >= 0) { GoTo(link.Page); return; }
        string uri = link.Uri ?? "";
        bool web = uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        bool mail = uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
        if (!web && !mail)
        {
            if (uri.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) { uri = "https://" + uri; web = true; }
            else { Toast("Utylix doesn't open this kind of link: " + uri); return; }
        }
        // a PDF can point anywhere: ask first, like Acrobat
        if (UMessage.Show(this, $"Open this link?\n\n{uri}", "Utylix Editor", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Toast("Couldn't open it: " + e.Message); }
    }

    // ---------- search ----------
    private readonly List<(int Page, int Start, int End)> _hits = new();
    private int _hitIndex = -1;
    private Border _searchBar = null!;
    private TextBox _searchText = null!;
    private TextBlock _searchCount = null!;
    private CancellationTokenSource? _searchCts;
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(280) };

    private UIElement SearchBar()
    {
        _searchText = new TextBox { Width = 210, Height = 30, Padding = new Thickness(8, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x30, 0x3D)), Foreground = Brushes.White, CaretBrush = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x4B, 0x5C)) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_searchText, "PdfSearchText");
        _searchCount = new TextBlock { Foreground = Soft, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0), MinWidth = 64 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_searchCount, "PdfSearchCount");
        var prev = SmallBar("", "Previous (Shift+Enter)", () => StepHit(-1));
        var next = SmallBar("", "Next (Enter)", () => StepHit(1));
        var close = SmallBar("", "Close (Esc)", CloseSearch);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = Soft, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) });
        row.Children.Add(_searchText); row.Children.Add(_searchCount); row.Children.Add(prev); row.Children.Add(next); row.Children.Add(close);
        _searchBar = new Border
        {
            Child = row, Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1F, 0x29)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x52)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 4, 4, 4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 26, 0), Visibility = Visibility.Collapsed,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.4 },
        };
        _searchText.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); };
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); _ = RunSearchAsync(); };
        _searchText.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { if (_searchDelay.IsEnabled) { _searchDelay.Stop(); _ = RunSearchAsync(); } else StepHit((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseSearch(); e.Handled = true; }
        };
        return _searchBar;
    }

    private void OpenSearch()
    {
        if (_pdf == null) return;
        if (HasSelection && Text(_selPage) is { } t)
        {
            var (s, e) = SelectionRange;
            string picked = t.Slice(s, e).Trim();
            if (picked.Length is > 0 and < 80 && !picked.Contains('\n')) _searchText.Text = picked;
        }
        _searchBar.Visibility = Visibility.Visible;
        _searchText.Focus();
        _searchText.SelectAll();
    }

    private void CloseSearch()
    {
        _searchCts?.Cancel();
        _searchBar.Visibility = Visibility.Collapsed;
        var pages = _hits.Select(h => h.Page).Distinct().ToList();
        _hits.Clear(); _hitIndex = -1;
        foreach (int p in pages) DrawTextLayer(p);
        _scroll.Focus();
    }

    private async Task RunSearchAsync()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var pdf = _pdf;
        string query = _searchText.Text;
        var oldPages = _hits.Select(h => h.Page).Distinct().ToList();
        _hits.Clear(); _hitIndex = -1;
        foreach (int p in oldPages) DrawTextLayer(p);
        if (pdf == null || query.Trim().Length == 0) { _searchCount.Text = ""; return; }
        _searchCount.Text = "Searching…";
        int count = pdf.PageCount, letters = 0;
        var found = new List<(int, int, int)>();
        try
        {
            await Task.Run(() =>
            {
                for (int i = 0; i < count; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var t = pdf.GetText(i);
                    letters += t.Text.Length;
                    foreach (var (s, e) in t.Find(query)) found.Add((i, s, e));
                }
            }, cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception e) when (e is ObjectDisposedException or System.IO.IOException) { return; }
        if (cts != _searchCts) return;
        _hits.AddRange(found);
        if (_hits.Count == 0)
        {
            _searchCount.Text = letters == 0 ? "No text in this PDF" : "Not found";
            if (letters == 0) Toast("This PDF has no text to search: it's probably scanned pictures");
            return;
        }
        // the first one from the current page on
        _hitIndex = Math.Max(0, _hits.FindIndex(h => h.Page >= _current));
        foreach (int p in _hits.Select(h => h.Page).Distinct()) DrawTextLayer(p);
        ShowHit();
    }

    private void StepHit(int by)
    {
        if (_hits.Count == 0) { if (_searchBar.Visibility != Visibility.Visible) OpenSearch(); return; }
        int old = _hitIndex;
        _hitIndex = ((_hitIndex + by) % _hits.Count + _hits.Count) % _hits.Count;
        DrawTextLayer(_hits[old].Page);
        if (_hits[_hitIndex].Page != _hits[old].Page) DrawTextLayer(_hits[_hitIndex].Page);
        ShowHit();
    }

    private void ShowHit()
    {
        var hit = _hits[_hitIndex];
        _searchCount.Text = $"{_hitIndex + 1} of {_hits.Count}";
        DrawTextLayer(hit.Page);
        if (Text(hit.Page) is { } t && t.LineBoxes(hit.Start, hit.End).FirstOrDefault() is { IsEmpty: false } r) ScrollToBox(hit.Page, r);
    }

    /// <summary>Scrolls so a box of a page is in view (a third from the top).</summary>
    private void ScrollToBox(int page, Rect r)
    {
        var pv = _pages[page];
        var top = pv.TranslatePoint(new Point(1 + r.X * pv.OverlayScale.ScaleX, 1 + r.Y * pv.OverlayScale.ScaleY), _column);
        double h = r.Height * pv.OverlayScale.ScaleY;
        if (top.Y < _scroll.VerticalOffset + 30 || top.Y + h > _scroll.VerticalOffset + _scroll.ViewportHeight - 30)
            _scroll.ScrollToVerticalOffset(top.Y - _scroll.ViewportHeight / 3);
        if (top.X < _scroll.HorizontalOffset || top.X > _scroll.HorizontalOffset + _scroll.ViewportWidth - 40)
            _scroll.ScrollToHorizontalOffset(top.X - _scroll.ViewportWidth / 3);
    }

    // ---------- bookmarks ----------
    private RadioButton _pagesTab = null!, _marksTab = null!;
    private readonly TreeView _bookmarks = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White, Visibility = Visibility.Collapsed, Padding = new Thickness(4, 6, 4, 6) };

    /// <summary>The side panel: the small pages, or the bookmarks (the PDF's table of contents).</summary>
    private UIElement SidePanel(UIElement strip)
    {
        RadioButton Tab(string text, string id)
        {
            var r = new RadioButton { Content = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 12 }, GroupName = "pdfside", Template = ToolChoiceTemplate(small: true), Focusable = false, Margin = new Thickness(0, 0, 4, 0) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(r, id);
            return r;
        }
        _pagesTab = Tab("Pages", "PdfTabPages");
        _marksTab = Tab("Bookmarks", "PdfTabBookmarks");
        _pagesTab.IsChecked = true;
        _pagesTab.Checked += (_, _) => { strip.Visibility = Visibility.Visible; _bookmarks.Visibility = Visibility.Collapsed; };
        _marksTab.Checked += (_, _) => { strip.Visibility = Visibility.Collapsed; _bookmarks.Visibility = Visibility.Visible; };
        _marksTab.IsEnabled = false;
        ScrollViewer.SetHorizontalScrollBarVisibility(_bookmarks, ScrollBarVisibility.Disabled);      // (long titles are cut with "…" instead)
        // the chosen entry: blue with white text, also when the list doesn't have the focus (Windows' own colour then is light grey: white on light grey)
        _bookmarks.Resources[SystemColors.HighlightBrushKey] = new SolidColorBrush(Color.FromRgb(0x2F, 0x6B, 0xEA));
        _bookmarks.Resources[SystemColors.HighlightTextBrushKey] = Brushes.White;
        _bookmarks.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = new SolidColorBrush(Color.FromRgb(0x33, 0x45, 0x6B));
        _bookmarks.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = Brushes.White;
        _bookmarks.SelectedItemChanged += (_, _) => { if (_bookmarks.SelectedItem is TreeViewItem { Tag: int page } && page >= 0) GoTo(page); };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_bookmarks, "PdfBookmarks");
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 2) };
        tabs.Children.Add(_pagesTab); tabs.Children.Add(_marksTab);
        var body = new Grid();
        body.Children.Add(strip); body.Children.Add(_bookmarks);
        var panel = new DockPanel { Background = Bar };
        DockPanel.SetDock(tabs, Dock.Top);
        panel.Children.Add(tabs);
        panel.Children.Add(body);
        return panel;
    }

    /// <summary>A new PDF is shown: forget the old selection and search, read its bookmarks.</summary>
    private void OnDocumentLoaded()
    {
        _selPage = -1; _textDrag = false;
        _hits.Clear(); _hitIndex = -1;
        if (_searchBar != null && _searchBar.Visibility == Visibility.Visible && _searchText.Text.Length > 0) _ = RunSearchAsync();
        OnFormLoaded();
        _bookmarks.Items.Clear();
        _marksTab.IsEnabled = false;
        _marksTab.ToolTip = "This PDF has no bookmarks";
        _pagesTab.IsChecked = true;
        var pdf = _pdf;
        if (pdf == null) return;
        _ = Task.Run(() => { try { return pdf.GetBookmarks(); } catch (Exception e) when (e is ObjectDisposedException or System.IO.IOException) { return new List<PdfBookmark>(); } })
            .ContinueWith(t =>
            {
                if (pdf != _pdf || t.Result.Count == 0) return;
                foreach (var b in t.Result) _bookmarks.Items.Add(TreeItem(b, 0));
                _marksTab.IsEnabled = true;
                _marksTab.ToolTip = "The PDF's table of contents";
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static TreeViewItem TreeItem(PdfBookmark b, int depth)
    {
        var item = new TreeViewItem
        {
            Header = new TextBlock { Text = b.Title.Length == 0 ? "(untitled)" : b.Title, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 - depth * 12, ToolTip = b.Title + (b.Page >= 0 ? $"  (page {b.Page + 1})" : "") },
            Tag = b.Page, IsExpanded = depth == 0 && b.Children.Count <= 12, Foreground = Brushes.White,
        };
        foreach (var c in b.Children) item.Items.Add(TreeItem(c, depth + 1));
        return item;
    }

    // ---------- keys for text ----------
    private bool TextKey(KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool typing = Keyboard.FocusedElement is TextBox;
        switch (e.Key)
        {
            case Key.F when ctrl: OpenSearch(); return true;
            case Key.F3: StepHit(shift ? -1 : 1); return true;
            case Key.C when ctrl && !typing && HasSelection: CopySelection(); return true;
            case Key.A when ctrl && !typing && !_editing: SelectAllOnPage(); return true;
            case Key.Escape when !typing && _searchBar.Visibility == Visibility.Visible: CloseSearch(); return true;
            case Key.Escape when !typing && HasSelection: ClearTextSelection(); return true;
        }
        return false;
    }
}
