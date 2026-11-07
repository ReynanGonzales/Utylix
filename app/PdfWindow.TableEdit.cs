using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Working with a table: click a cell, type in it (Tab / Enter move on), drag the lines to change a column or a row, drag over cells to choose several (to merge or colour them),
/// right-click or the Table button for rows, columns, merging, colours and lines, and paste cells copied from Excel.
/// </summary>
public sealed partial class PdfWindow
{
    private TextBox? _cellBox;
    private TableItem? _cellTable;
    private int _cellRow, _cellCol;
    private bool _cellMoving;                                  // the page is being drawn again: the box is put back and must keep the keyboard
    private int _tableLine;
    private double _tableA, _tableB;
    private Button? _tableButton;

    // ---------- the mouse ----------
    /// <summary>A click on a table. True = handled (the table is chosen, a cell chosen, a line or the table itself picked up).</summary>
    private bool TableDown(PageView pv, TableItem t, Point p, int clicks)
    {
        CloseCellEditor(commit: true);
        bool was = _selected == t;
        if (!was) Select(t);
        double k = 1 / Math.Max(0.01, pv.OverlayScale.ScaleX), tol = 4 * k;
        if ((t.SelR1 >= 0 && t.Grip(1).Contains(p)) || t.OnMoveEdge(p, tol)) { _drag = DragMode.Move; _dragBox = t.Bounds; pv.Overlay.CaptureMouse(); return true; }
        if (was && t.LineAt(p, tol) is { } line)
        {
            _drag = line.Column ? DragMode.TableCol : DragMode.TableRow;
            _tableLine = line.Index;
            _tableA = line.Column ? t.ColW[line.Index - 1] : t.RowH[line.Index - 1];
            _tableB = line.Index < (line.Column ? t.Cols : t.Rows) ? (line.Column ? t.ColW[line.Index] : t.RowH[line.Index]) : 0;
            pv.Overlay.CaptureMouse();
            return true;
        }
        if (t.CellAt(p) is not { } cell) { _drag = DragMode.Move; _dragBox = t.Bounds; pv.Overlay.CaptureMouse(); return true; }       // (just outside the lines: the table moves)
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (shift) t.ExtendSel(cell.Row, cell.Col); else t.SetSel(cell.Row, cell.Col);
        RenderItems(t.Page);
        UpdateEditButtons();
        if (clicks >= 2 && !shift) { _dragPage = null; OpenCellEditor(pv, t, cell.Row, cell.Col); return true; }
        _drag = DragMode.TableSel;
        pv.Overlay.CaptureMouse();
        return true;
    }

    /// <summary>The mouse moved over the chosen table with nothing pressed: the pointer shows what a drag would do.</summary>
    private void TableHover(PageView pv, TableItem t, Point p)
    {
        double tol = 4 / Math.Max(0.01, pv.OverlayScale.ScaleX);
        Cursor cursor = (t.SelR1 >= 0 && t.Grip(1).Contains(p)) || t.OnMoveEdge(p, tol) ? Cursors.SizeAll
                      : t.LineAt(p, tol) is { } line ? (line.Column ? Cursors.SizeWE : Cursors.SizeNS)
                      : CursorFor(_tool);
        if (pv.Overlay.Cursor != cursor) pv.Overlay.Cursor = cursor;
    }

    /// <summary>The mouse moves with a table drag going on. True = handled.</summary>
    private bool TableDrag(PageView pv, Point p)
    {
        if (_selected is not TableItem t) return false;
        switch (_drag)
        {
            case DragMode.TableSel:
                if (t.CellAt(p, clamp: true) is { } cell && (cell.Row != t.SelR2 || cell.Col != t.SelC2)) { t.ExtendSel(cell.Row, cell.Col); RenderItems(pv.Index); }
                return true;
            case DragMode.TableCol:
            {
                double dx = p.X - _dragStart.X;
                if (!_dragSnapshotTaken) { if (Math.Abs(dx) < 1.5) return true; Snapshot(); _dragSnapshotTaken = true; }
                int i = _tableLine;
                double a = Math.Max(TableItem.MinCol, _tableA + dx);
                if (i < t.Cols) { a = Math.Min(a, _tableA + _tableB - TableItem.MinCol); t.ColW[i - 1] = a; t.ColW[i] = _tableA + _tableB - a; }
                else t.ColW[i - 1] = a;
                t.FitRows();
                RenderItems(pv.Index);
                return true;
            }
            case DragMode.TableRow:
            {
                double dy = p.Y - _dragStart.Y;
                if (!_dragSnapshotTaken) { if (Math.Abs(dy) < 1.5) return true; Snapshot(); _dragSnapshotTaken = true; }
                t.RowH[_tableLine - 1] = Math.Max(TableItem.MinRow, _tableA + dy);
                RenderItems(pv.Index);
                return true;
            }
        }
        return false;
    }

    // ---------- typing in a cell ----------
    private void OpenCellEditor(PageView pv, TableItem t, int r, int c, string? first = null)
    {
        CloseCellEditor(commit: true);
        if (r < 0 || c < 0 || r >= t.Rows || c >= t.Cols) return;
        var cell = t.Cells[r][c];
        if (cell.Covered) { r = cell.AnchorRow; c = cell.AnchorCol; cell = t.Cells[r][c]; }
        t.SetSel(r, c);
        var rect = t.CellRect(r, c);
        var box = new TextBox
        {
            Text = first ?? cell.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Arial"), FontSize = t.FontSize,
            FontWeight = cell.Bold || (t.Header && r == 0) ? FontWeights.Bold : FontWeights.Normal, Foreground = new SolidColorBrush(t.TextColor),
            TextAlignment = cell.Align switch { 1 => TextAlignment.Center, 2 => TextAlignment.Right, _ => TextAlignment.Left },
            Padding = new Thickness(TableItem.Pad - 1), BorderThickness = new Thickness(0), Width = rect.Width, MinHeight = rect.Height,
            VerticalContentAlignment = cell.VAlign switch { 1 => VerticalAlignment.Center, 2 => VerticalAlignment.Bottom, _ => VerticalAlignment.Top },
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Template = (ControlTemplate)XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='TextBox'>" +
                "<Border Background='#FFFFFFFF' BorderBrush='#FF2F6BEA' BorderThickness='1.4'><ScrollViewer x:Name='PART_ContentHost' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Margin='{TemplateBinding Padding}' Padding='0' Focusable='False' VerticalAlignment='{TemplateBinding VerticalContentAlignment}' HorizontalScrollBarVisibility='Hidden' VerticalScrollBarVisibility='Hidden' /></Border></ControlTemplate>"),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(box, "PdfTableCell");
        Canvas.SetLeft(box, rect.X); Canvas.SetTop(box, rect.Y);
        box.PreviewKeyDown += CellBoxKey;
        box.LostKeyboardFocus += (_, _) => { if (!_cellMoving && _cellBox == box) CloseCellEditor(commit: true); };
        _cellBox = box; _cellTable = t; _cellRow = r; _cellCol = c;
        RenderItems(t.Page);                                       // (puts the box on the page)
        box.UpdateLayout();
        box.Focus(); Keyboard.Focus(box);                          // (at once: letters typed right after Tab must not be lost)
        if (first == null) box.SelectAll(); else box.CaretIndex = box.Text.Length;
    }

    private void CellBoxKey(object sender, KeyEventArgs e)
    {
        if (_cellTable == null) return;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0, alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        if (e.Key == Key.Escape) { e.Handled = true; CloseCellEditor(commit: false); return; }
        if (e.Key == Key.Tab) { e.Handled = true; var t = _cellTable; int r = _cellRow, c = _cellCol; CloseCellEditor(commit: true); StepCell(t, r, c, shift ? -1 : 1, edit: true); return; }
        if (e.Key == Key.Enter && !shift && !alt) { e.Handled = true; var t = _cellTable; int r = _cellRow, c = _cellCol; CloseCellEditor(commit: true); MoveCell(t, r + 1, c, edit: r + 1 < t.Rows); return; }       // (Enter goes down the column; on the last row it just finishes)
    }

    private void CloseCellEditor(bool commit)
    {
        var box = _cellBox; var t = _cellTable;
        _cellBox = null; _cellTable = null;
        if (box == null || t == null) return;
        if (commit && _cellRow < t.Rows && _cellCol < t.Cols)
        {
            var cell = t.Cells[_cellRow][_cellCol];
            string text = box.Text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
            if (text != cell.Text) { Snapshot(); cell.Text = text; t.FitRows(); }
        }
        (box.Parent as Panel)?.Children.Remove(box);
        RenderItems(t.Page);
        _scroll.Focus();
    }

    /// <summary>Called by <see cref="RenderItems"/> after it cleared the page: the open cell box goes back where it was.</summary>
    private void PutCellBoxBack(int page, System.Windows.Controls.Canvas overlay)
    {
        if (_cellBox == null || _cellTable == null || _cellTable.Page != page) return;
        var rect = _cellRow < _cellTable.Rows && _cellCol < _cellTable.Cols ? _cellTable.CellRect(_cellRow, _cellCol) : Rect.Empty;
        if (!rect.IsEmpty) { Canvas.SetLeft(_cellBox, rect.X); Canvas.SetTop(_cellBox, rect.Y); _cellBox.Width = rect.Width; _cellBox.MinHeight = rect.Height; }
        overlay.Children.Add(_cellBox);
        var box = _cellBox;
        box.UpdateLayout();
        if (!box.IsKeyboardFocusWithin) { box.Focus(); Keyboard.Focus(box); }
        Dispatcher.BeginInvoke(() => { _cellMoving = false; }, DispatcherPriority.Input);
    }

    // ---------- moving between cells ----------
    private void MoveCell(TableItem t, int r, int c, bool edit)
    {
        if (!_items.Contains(t)) return;
        r = Math.Clamp(r, 0, t.Rows - 1); c = Math.Clamp(c, 0, t.Cols - 1);
        var anchor = t.Cells[r][c];
        r = anchor.AnchorRow; c = anchor.AnchorCol;
        t.SetSel(r, c);
        if (edit) OpenCellEditor(_pages[t.Page], t, r, c); else RenderItems(t.Page);
    }

    /// <summary>One cell on in reading order. Tab at the very last cell adds a row, like in Word.</summary>
    private void StepCell(TableItem t, int r, int c, int step, bool edit)
    {
        if (!_items.Contains(t)) return;
        int index = r * t.Cols + c + step;
        if (index >= t.Rows * t.Cols) { Snapshot(); t.InsertRow(t.Rows); }
        index = Math.Clamp(index, 0, t.Rows * t.Cols - 1);
        MoveCell(t, index / t.Cols, index % t.Cols, edit);
    }

    // ---------- the keyboard ----------
    private bool TableKey(TableItem t, KeyEventArgs e)
    {
        if (t.SelR1 < 0 || t.Page >= _pages.Count) return false;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var page = _pages[t.Page];
        switch (e.Key)
        {
            case Key.Enter or Key.F2 when !ctrl: OpenCellEditor(page, t, t.SelR1, t.SelC1); return true;
            case Key.Tab: StepCell(t, t.SelR2, t.SelC2, shift ? -1 : 1, edit: false); return true;
            case Key.Left or Key.Right or Key.Up or Key.Down when !ctrl:
            {
                int dr = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0, dc = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
                int r = Math.Clamp(t.SelR2 + dr, 0, t.Rows - 1), c = Math.Clamp(t.SelC2 + dc, 0, t.Cols - 1);
                if (shift) t.ExtendSel(r, c); else { var a = t.Cells[r][c]; t.SetSel(a.AnchorRow, a.AnchorCol); }
                RenderItems(t.Page);
                return true;
            }
            case Key.Delete or Key.Back when !ctrl:
            {
                var full = t.SelAnchors().Where(x => t.Cells[x.R][x.C].Text.Length > 0).ToList();
                if (full.Count == 0) return false;                                 // (nothing in the cells: Delete takes the table away, like any other thing)
                Snapshot();
                foreach (var (r, c) in full) t.Cells[r][c].Text = "";
                RenderItems(t.Page);
                return true;
            }
            case Key.V when ctrl && !shift && ClipboardHasText(): PasteIntoTable(t); return true;
        }
        return false;
    }

    /// <summary>Typing a letter with a cell chosen starts typing in it (like a spreadsheet).</summary>
    private void TableTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!_editing || _cellBox != null || _selected is not TableItem t || t.SelR1 < 0 || t.Page >= _pages.Count) return;
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0]) || Keyboard.FocusedElement is TextBox) return;
        OpenCellEditor(_pages[t.Page], t, t.SelR1, t.SelC1, e.Text);
        e.Handled = true;
    }

    // ---------- paste from Excel ----------
    /// <summary>Tab-separated text (what Excel and Word tables copy) as rows of cells; quoted cells may hold line breaks.</summary>
    private static List<string[]> ParseGrid(string text)
    {
        var rows = new List<string[]>();
        var cells = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false, wasQuoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; } else quoted = false; }
                else sb.Append(ch);
            }
            else if (ch == '"' && sb.Length == 0) { quoted = true; wasQuoted = true; }
            else if (ch == '\t') { cells.Add(sb.ToString()); sb.Clear(); wasQuoted = false; }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                cells.Add(sb.ToString()); sb.Clear(); wasQuoted = false;
                rows.Add(cells.ToArray()); cells.Clear();
            }
            else sb.Append(ch);
        }
        if (sb.Length > 0 || cells.Count > 0 || wasQuoted) { cells.Add(sb.ToString()); rows.Add(cells.ToArray()); }
        return rows;
    }

    private void PasteIntoTable(TableItem t)
    {
        string text;
        try { text = Clipboard.GetText(); } catch (System.Runtime.InteropServices.COMException) { return; }
        var grid = ParseGrid(text);
        if (grid.Count == 0) return;
        CloseCellEditor(commit: true);
        Snapshot();
        var (r1, c1, _, _) = t.SelRange();
        r1 = Math.Max(0, r1); c1 = Math.Max(0, c1);
        int oldCols = t.Cols, width = grid.Max(row => row.Length);
        while (r1 + grid.Count > t.Rows) t.InsertRow(t.Rows);
        while (c1 + width > t.Cols) t.InsertCol(t.Cols);
        for (int c = oldCols; c < t.Cols; c++)                                     // (new columns are as wide as their words, within reason)
        {
            int longest = grid.Select(row => c - c1 < row.Length && c >= c1 ? row[c - c1].Split('\n').Max(l => l.Length) : 0).DefaultIfEmpty(0).Max();
            t.ColW[c] = Math.Clamp(longest * t.FontSize * 0.52 + 2 * TableItem.Pad, 40, 220);
        }
        int count = 0;
        for (int r = 0; r < grid.Count; r++)
            for (int c = 0; c < grid[r].Length; c++)
            {
                var cell = t.Cells[r1 + r][c1 + c];
                if (cell.Covered) continue;
                cell.Text = grid[r][c].Trim(); count++;
            }
        t.FitRows();
        RenderItems(t.Page);
        Toast($"{count} cell{(count == 1 ? "" : "s")} pasted");
    }

    // ---------- the menu ----------
    private UIElement TableButton()
    {
        var b = new Button { Content = BarLabel("Table ▾"), Template = BarButtonTemplate(), Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(8, 0, 0, 0), Focusable = false, Background = Brushes.Transparent, Foreground = Brushes.White, ToolTip = "Rows, columns, merging, colours and lines of this table (also on the right-click menu)", Visibility = Visibility.Collapsed };
        System.Windows.Automation.AutomationProperties.SetAutomationId(b, "PdfTableOptions");
        b.Click += (_, _) =>
        {
            if (_selected is not TableItem t) return;
            var menu = new ContextMenu();
            AddTableMenu(menu, t);
            Themed(menu);
            menu.PlacementTarget = b; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
        };
        _tableButton = b;
        return b;
    }

    private Button? _tableAlignButton;

    /// <summary>"Align ▾" next to "Table ▾": where the words sit in the chosen cells, without opening the longer table menu.</summary>
    private UIElement TableAlignButton()
    {
        var b = new Button { Content = BarLabel("Align ▾"), Template = BarButtonTemplate(), Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(4, 0, 0, 0), Focusable = false, Background = Brushes.Transparent, Foreground = Brushes.White, ToolTip = "Put the words of the chosen cells left, in the middle or right, and at the top, in the middle or at the bottom of the cell", Visibility = Visibility.Collapsed };
        System.Windows.Automation.AutomationProperties.SetAutomationId(b, "PdfTableAlignButton");
        b.Click += (_, _) =>
        {
            if (_selected is not TableItem t) return;
            var menu = new ContextMenu();
            AddAlignEntries(menu, t);
            Themed(menu);
            menu.PlacementTarget = b; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true;
        };
        _tableAlignButton = b;
        return b;
    }

    private void AddAlignEntries(ContextMenu menu, TableItem t)
    {
        if (t.SelR1 < 0 && t.Rows > 0) t.SetSel(0, 0);
        var chosen = t.SelAnchors().ToList();
        void Entry(string text, bool vertical, int value, string id)
        {
            bool same = chosen.Count > 0 && chosen.All(x => vertical ? t.Cells[x.R][x.C].VAlign == value : Math.Max(0, t.Cells[x.R][x.C].Align) == value);
            var m = new MenuItem { Header = text, IsCheckable = true, IsChecked = same, IsEnabled = chosen.Count > 0 };
            System.Windows.Automation.AutomationProperties.SetAutomationId(m, id);
            m.Click += (_, _) =>
            {
                CloseCellEditor(commit: true);
                Snapshot();
                foreach (var (r, c) in chosen) { if (vertical) t.Cells[r][c].VAlign = value; else t.Cells[r][c].Align = value; }
                RenderItems(t.Page);
            };
            menu.Items.Add(m);
        }
        Entry("Left", false, 0, "PdfAlignLeft"); Entry("Centre", false, 1, "PdfAlignCentre"); Entry("Right", false, 2, "PdfAlignRight");
        menu.Items.Add(new Separator());
        Entry("Top", true, 0, "PdfAlignTop"); Entry("Middle", true, 1, "PdfAlignMiddle"); Entry("Bottom", true, 2, "PdfAlignBottom");
    }

    /// <summary>The entries for a table: on the right-click menu and under the Table button.</summary>
    private void AddTableMenu(ContextMenu menu, TableItem t)
    {
        void Change(Action change)
        {
            CloseCellEditor(commit: true);
            Snapshot(); change(); t.FitRows();
            RenderItems(t.Page); UpdateProperties();
        }
        MenuItem Entry(ItemsControl into, string text, Action action, bool enabled = true, bool? check = null, string? id = null)
        {
            var m = new MenuItem { Header = text, IsEnabled = enabled, IsCheckable = check != null, IsChecked = check == true };
            if (id != null) System.Windows.Automation.AutomationProperties.SetAutomationId(m, id);
            m.Click += (_, _) => action();
            into.Items.Add(m);
            return m;
        }
        MenuItem Sub(string text) { var m = new MenuItem { Header = text }; menu.Items.Add(m); return m; }
        if (t.SelR1 < 0 && t.Rows > 0) t.SetSel(0, 0);
        var (r1, c1, r2, c2) = t.SelRange();
        r1 = Math.Clamp(r1, 0, t.Rows - 1); r2 = Math.Clamp(r2, 0, t.Rows - 1); c1 = Math.Clamp(c1, 0, t.Cols - 1); c2 = Math.Clamp(c2, 0, t.Cols - 1);
        var chosen = t.SelAnchors().ToList();
        bool several = (r2 > r1 || c2 > c1) && chosen.Count > 1;
        bool anyMerged = chosen.Any(x => t.Cells[x.R][x.C].ColSpan > 1 || t.Cells[x.R][x.C].RowSpan > 1);

        Entry(menu, "Header row", () => Change(() => t.Header = !t.Header), true, t.Header, "PdfTableHeader");
        Entry(menu, "Striped rows", () => Change(() => t.Stripes = !t.Stripes), true, t.Stripes, "PdfTableStripes");
        var lines = Sub("Lines");
        foreach (var (label, mode) in new[] { ("Every line", 0), ("Only the frame", 1), ("Frame and lines between rows", 2), ("No lines", 3) })
            Entry(lines, label, () => Change(() => t.Borders = mode), true, t.Borders == mode, "PdfTableLines" + mode);
        var size = Sub("Text size");
        foreach (double s in new[] { 8, 9, 10, 11, 12, 14, 16, 18 })
            Entry(size, s.ToString(), () => Change(() => t.FontSize = s), true, Math.Abs(t.FontSize - s) < 0.1);
        menu.Items.Add(new Separator());

        var align = Sub("Align the words");
        foreach (var (label, value) in new[] { ("Left", 0), ("Centre", 1), ("Right", 2) })
            Entry(align, label, () => Change(() => { foreach (var (r, c) in chosen) t.Cells[r][c].Align = value; }), chosen.Count > 0, null, "PdfTableAlign" + value);
        foreach (var (label, value) in new[] { ("Top", 0), ("Middle", 1), ("Bottom", 2) })
            Entry(align, label + " of the cell", () => Change(() => { foreach (var (r, c) in chosen) t.Cells[r][c].VAlign = value; }), chosen.Count > 0, null, "PdfTableVAlign" + value);
        bool allBold = chosen.Count > 0 && chosen.All(x => t.Cells[x.R][x.C].Bold);
        Entry(menu, "Bold", () => Change(() => { foreach (var (r, c) in chosen) t.Cells[r][c].Bold = !allBold; }), chosen.Count > 0, allBold, "PdfTableBold");
        Entry(menu, "Fill colour…", () =>
        {
            using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            var colour = Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
            Change(() => { foreach (var (r, c) in chosen) t.Cells[r][c].Fill = colour; });
        }, chosen.Count > 0, null, "PdfTableFill");
        Entry(menu, "No fill colour", () => Change(() => { foreach (var (r, c) in chosen) t.Cells[r][c].Fill = null; }), chosen.Any(x => t.Cells[x.R][x.C].Fill != null), null, "PdfTableNoFill");
        Entry(menu, "Merge the chosen cells", () => Change(() =>
        {
            var anchor = t.Cells[r1][c1];
            var words = chosen.Select(x => t.Cells[x.R][x.C].Text).Where(s => s.Length > 0).ToList();
            foreach (var (r, c) in chosen) { var x = t.Cells[r][c]; x.ColSpan = x.RowSpan = 1; if (!(r == r1 && c == c1)) x.Text = ""; }
            anchor.Text = string.Join(" ", words);
            anchor.ColSpan = c2 - c1 + 1; anchor.RowSpan = r2 - r1 + 1;
            t.RebuildSpans();
            t.SetSel(r1, c1);
        }), several, null, "PdfTableMerge");
        Entry(menu, "Split merged cells", () => Change(() => { foreach (var (r, c) in chosen) { var x = t.Cells[r][c]; x.ColSpan = x.RowSpan = 1; } t.RebuildSpans(); }), anyMerged, null, "PdfTableSplit");
        menu.Items.Add(new Separator());

        var rows = Sub("Rows and columns");
        Entry(rows, "Add a row above", () => Change(() => t.InsertRow(r1)), true, null, "PdfTableRowAbove");
        Entry(rows, "Add a row below", () => Change(() => { t.InsertRow(r2 + 1); t.SetSel(r2 + 1, c1); }), true, null, "PdfTableRowBelow");
        Entry(rows, "Add a column on the left", () => Change(() => t.InsertCol(c1)), true, null, "PdfTableColLeft");
        Entry(rows, "Add a column on the right", () => Change(() => { t.InsertCol(c2 + 1); t.SetSel(r1, c2 + 1); }), true, null, "PdfTableColRight");
        Entry(rows, "Delete the chosen row" + (r2 > r1 ? "s" : ""), () => Change(() => { for (int r = r2; r >= r1; r--) t.DeleteRow(r); t.SetSel(Math.Min(r1, t.Rows - 1), Math.Min(c1, t.Cols - 1)); }), t.Rows > r2 - r1 + 1, null, "PdfTableDeleteRow");
        Entry(rows, "Delete the chosen column" + (c2 > c1 ? "s" : ""), () => Change(() => { for (int c = c2; c >= c1; c--) t.DeleteCol(c); t.SetSel(Math.Min(r1, t.Rows - 1), Math.Min(c1, t.Cols - 1)); }), t.Cols > c2 - c1 + 1, null, "PdfTableDeleteCol");
        Entry(rows, "Make all columns the same width", () => Change(t.EqualColumns), true, null, "PdfTableEqualCols");
        Entry(rows, "Make all rows the same height", () => Change(t.EqualRows), true, null, "PdfTableEqualRows");
        Entry(rows, "Number of rows and columns…", () => EditTable(t), true, null, "PdfTableSize");
        Entry(menu, "Paste cells from the clipboard (Excel, Word)", () => PasteIntoTable(t), ClipboardHasText(), null, "PdfTablePaste");
    }
}
