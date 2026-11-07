using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// The Table tool: a real table. Rows and columns of any size, text in the cells (they grow to fit it), merged cells, a shaded header row, striped rows, fills, and a choice of
/// lines. It is saved into the page as lines, filled rectangles and normal text, so it looks the same in every PDF reader.
/// </summary>
public sealed partial class PdfWindow
{
    private int _tableRows = 3, _tableCols = 3;
    private double _tableLineWidth = 0.5;                  // a new table starts with fine lines (the line size buttons change this one while the Table tool or a table is chosen)

    private sealed class TableCell
    {
        public string Text = "";
        public Color? Fill;
        public bool Bold;
        public int Align = -1;                         // -1 = like the table (left), 0 left, 1 middle, 2 right
        public int VAlign;                             // where the words sit between the top and the bottom of the cell: 0 top, 1 middle, 2 bottom
        public int ColSpan = 1, RowSpan = 1;           // of the cell at the top-left of a merged block
        public bool Covered;                           // part of a merged block, not its top-left cell
        public int AnchorRow, AnchorCol;               // the top-left cell of the block this cell belongs to (itself when not merged)
        public BitmapSource? Pic;                      // a picture in the cell (fits the cell, in the middle, keeps its shape)
        public byte[]? PicJpeg;                        // the same picture as a ready JPEG (kept small in the PDF), when there is one
        public TableCell Copy() => (TableCell)MemberwiseClone();
    }

    private sealed class TableItem : EditItem
    {
        public Point Origin;
        public List<double> ColW = new(), RowH = new();
        public List<List<TableCell>> Cells = new();
        public double Width = 1;                       // line thickness (Color = line colour)
        public int Borders;                            // 0 every line, 1 the frame only, 2 the frame and the lines between rows, 3 no lines
        public bool Header, Stripes;
        public Color HeaderFill = Color.FromRgb(0xDD, 0xE5, 0xF4), StripeFill = Color.FromRgb(0xEA, 0xEF, 0xF7), TextColor = Colors.Black;
        public double FontSize = 11;
        public int SelR1 = -1, SelC1 = -1, SelR2 = -1, SelC2 = -1;          // the chosen cells (screen only: never saved)

        public const double Pad = 3, MinCol = 16, MinRow = 14;

        public int Rows => RowH.Count;
        public int Cols => ColW.Count;

        public static TableItem Create(int page, int rows, int cols, Color color, double width)
        {
            var t = new TableItem { Page = page, Color = color, Width = width };
            for (int c = 0; c < cols; c++) t.ColW.Add(0);
            for (int r = 0; r < rows; r++) { t.RowH.Add(0); t.Cells.Add(Enumerable.Range(0, cols).Select(_ => new TableCell()).ToList()); }
            t.RebuildSpans();
            return t;
        }

        // ---------- size and place ----------
        public Rect Box { get => new(Origin, new Size(ColW.Sum(), RowH.Sum())); set => SetBox(value); }
        public override Rect Bounds => Box;
        public override void MoveBy(Vector d) => Origin += d;
        public override void ResizeTo(Rect r) => SetBox(r);

        private void SetBox(Rect r)
        {
            Origin = r.TopLeft;
            double sw = ColW.Sum(), sh = RowH.Sum();
            for (int c = 0; c < Cols; c++) ColW[c] = sw < 0.5 ? r.Width / Cols : ColW[c] * r.Width / sw;
            for (int i = 0; i < Rows; i++) RowH[i] = sh < 0.5 ? r.Height / Rows : RowH[i] * r.Height / sh;
        }

        public override EditItem Clone()
        {
            var c = (TableItem)MemberwiseClone();
            c.ColW = ColW.ToList(); c.RowH = RowH.ToList();
            c.Cells = Cells.Select(row => row.Select(x => x.Copy()).ToList()).ToList();
            return c;
        }

        public override bool Hit(Point p) => Inflate(Box, 3).Contains(p) || (SelR1 >= 0 && Grip(1).Contains(p));

        /// <summary>The little square at the top-left that moves the table (shown while it is chosen); <paramref name="k"/> = points per screen pixel.</summary>
        public Rect Grip(double k) => new(Origin.X - 12 * k, Origin.Y - 12 * k, 10 * k, 10 * k);

        private double[] Xs() { var xs = new double[Cols + 1]; xs[0] = Origin.X; for (int c = 0; c < Cols; c++) xs[c + 1] = xs[c] + ColW[c]; return xs; }
        private double[] Ys() { var ys = new double[Rows + 1]; ys[0] = Origin.Y; for (int r = 0; r < Rows; r++) ys[r + 1] = ys[r] + RowH[r]; return ys; }

        public Rect CellRect(int r, int c)
        {
            var cell = Cells[r][c];
            var xs = Xs(); var ys = Ys();
            int c2 = Math.Min(Cols, c + Math.Max(1, cell.ColSpan)), r2 = Math.Min(Rows, r + Math.Max(1, cell.RowSpan));
            return new Rect(xs[c], ys[r], xs[c2] - xs[c], ys[r2] - ys[r]);
        }

        /// <summary>The top-left cell of the block at <paramref name="p"/> (null when it is outside the table).</summary>
        public (int Row, int Col)? CellAt(Point p, bool clamp = false)
        {
            if (Rows == 0 || Cols == 0) return null;
            var xs = Xs(); var ys = Ys();
            if (!clamp && (p.X < xs[0] || p.X > xs[Cols] || p.Y < ys[0] || p.Y > ys[Rows])) return null;
            int c = 0, r = 0;
            while (c < Cols - 1 && p.X >= xs[c + 1]) c++;
            while (r < Rows - 1 && p.Y >= ys[r + 1]) r++;
            var cell = Cells[r][c];
            return (cell.AnchorRow, cell.AnchorCol);
        }

        /// <summary>A line between two columns (Column = true) or rows that the mouse is on, including the right and bottom edge; the top and left edge move the table.</summary>
        public (bool Column, int Index)? LineAt(Point p, double tol)
        {
            if (Rows == 0 || Cols == 0) return null;
            var xs = Xs(); var ys = Ys();
            if (p.Y < ys[0] - tol || p.Y > ys[Rows] + tol || p.X < xs[0] - tol || p.X > xs[Cols] + tol) return null;
            for (int k = 1; k <= Cols; k++)
                if (Math.Abs(p.X - xs[k]) <= tol && (k == Cols || Enumerable.Range(0, Rows).Any(r => Anchor(r, k - 1) != Anchor(r, k)))) return (true, k);
            for (int k = 1; k <= Rows; k++)
                if (Math.Abs(p.Y - ys[k]) <= tol && (k == Rows || Enumerable.Range(0, Cols).Any(c => Anchor(k - 1, c) != Anchor(k, c)))) return (false, k);
            return null;
        }

        /// <summary>On the top or left edge: where the table is picked up and moved.</summary>
        public bool OnMoveEdge(Point p, double tol)
        {
            if (Rows == 0 || Cols == 0) return false;
            var xs = Xs(); var ys = Ys();
            bool inside = p.X >= xs[0] - tol && p.X <= xs[Cols] + tol && p.Y >= ys[0] - tol && p.Y <= ys[Rows] + tol;
            return inside && (Math.Abs(p.Y - ys[0]) <= tol || Math.Abs(p.X - xs[0]) <= tol);
        }

        // ---------- merged cells ----------
        public void RebuildSpans()
        {
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++) { var x = Cells[r][c]; x.Covered = false; x.AnchorRow = r; x.AnchorCol = c; }
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                {
                    var x = Cells[r][c];
                    if (x.Covered) { x.ColSpan = x.RowSpan = 1; continue; }
                    x.ColSpan = Math.Clamp(x.ColSpan, 1, Cols - c); x.RowSpan = Math.Clamp(x.RowSpan, 1, Rows - r);
                    for (int rr = r; rr < r + x.RowSpan; rr++)
                        for (int cc = c; cc < c + x.ColSpan; cc++)
                            if (rr != r || cc != c) { var y = Cells[rr][cc]; y.Covered = true; y.AnchorRow = r; y.AnchorCol = c; y.ColSpan = y.RowSpan = 1; }
                }
        }

        public void UnmergeAll()
        {
            foreach (var row in Cells) foreach (var x in row) x.ColSpan = x.RowSpan = 1;
            RebuildSpans();
        }

        // ---------- the chosen cells ----------
        public void SetSel(int r, int c) { SelR1 = SelR2 = r; SelC1 = SelC2 = c; }
        public void ExtendSel(int r, int c) { if (SelR1 < 0) { SetSel(r, c); return; } SelR2 = r; SelC2 = c; }
        public void ClearSel() { SelR1 = SelR2 = SelC1 = SelC2 = -1; }
        /// <summary>The chosen block, grown until it holds every merged cell it touches completely (so a merged cell is chosen whole, and a drag never cuts one in two).</summary>
        public (int R1, int C1, int R2, int C2) SelRange()
        {
            int r1 = Math.Min(SelR1, SelR2), c1 = Math.Min(SelC1, SelC2), r2 = Math.Max(SelR1, SelR2), c2 = Math.Max(SelC1, SelC2);
            if (Rows == 0 || Cols == 0 || r1 < 0) return (r1, c1, r2, c2);
            r1 = Math.Clamp(r1, 0, Rows - 1); r2 = Math.Clamp(r2, 0, Rows - 1); c1 = Math.Clamp(c1, 0, Cols - 1); c2 = Math.Clamp(c2, 0, Cols - 1);
            bool grew = true;
            while (grew)
            {
                grew = false;
                for (int r = r1; r <= r2; r++)
                    for (int c = c1; c <= c2; c++)
                    {
                        var cell = Cells[r][c];
                        int ar = cell.AnchorRow, ac = cell.AnchorCol;
                        var a = Cells[ar][ac];
                        int er = Math.Min(Rows - 1, ar + Math.Max(1, a.RowSpan) - 1), ec = Math.Min(Cols - 1, ac + Math.Max(1, a.ColSpan) - 1);
                        if (ar < r1) { r1 = ar; grew = true; }
                        if (ac < c1) { c1 = ac; grew = true; }
                        if (er > r2) { r2 = er; grew = true; }
                        if (ec > c2) { c2 = ec; grew = true; }
                    }
            }
            return (r1, c1, r2, c2);
        }
        public IEnumerable<(int R, int C)> SelAnchors()
        {
            if (SelR1 < 0) yield break;
            var (r1, c1, r2, c2) = SelRange();
            for (int r = Math.Max(0, r1); r <= Math.Min(Rows - 1, r2); r++)
                for (int c = Math.Max(0, c1); c <= Math.Min(Cols - 1, c2); c++)
                    if (!Cells[r][c].Covered) yield return (r, c);
        }

        // ---------- rows and columns ----------
        public void InsertRow(int at)
        {
            UnmergeAll();
            at = Math.Clamp(at, 0, Rows);
            double h = Rows == 0 ? 22 : RowH[Math.Clamp(at == Rows ? at - 1 : at, 0, Rows - 1)];
            RowH.Insert(at, Math.Max(MinRow, h));
            Cells.Insert(at, Enumerable.Range(0, Cols).Select(_ => new TableCell()).ToList());
            RebuildSpans();
        }

        public void DeleteRow(int at)
        {
            if (Rows <= 1) return;
            UnmergeAll();
            RowH.RemoveAt(at); Cells.RemoveAt(at);
            RebuildSpans();
        }

        public void InsertCol(int at)
        {
            UnmergeAll();
            at = Math.Clamp(at, 0, Cols);
            double w = Cols == 0 ? 80 : ColW[Math.Clamp(at == Cols ? at - 1 : at, 0, Cols - 1)];
            ColW.Insert(at, Math.Max(MinCol, w));
            foreach (var row in Cells) row.Insert(at, new TableCell());
            RebuildSpans();
        }

        public void DeleteCol(int at)
        {
            if (Cols <= 1) return;
            UnmergeAll();
            ColW.RemoveAt(at);
            foreach (var row in Cells) row.RemoveAt(at);
            RebuildSpans();
        }

        public void EqualColumns() { double each = ColW.Sum() / Math.Max(1, Cols); for (int c = 0; c < Cols; c++) ColW[c] = each; }
        public void EqualRows() { double each = RowH.Sum() / Math.Max(1, Rows); for (int r = 0; r < Rows; r++) RowH[r] = Math.Max(each, MinRow); FitRows(); }

        /// <summary>A row that is too low for its words gets higher (never lower).</summary>
        public void FitRows()
        {
            for (int r = 0; r < Rows; r++)
            {
                double need = MinRow;
                for (int c = 0; c < Cols; c++)
                {
                    var cell = Cells[r][c];
                    if (cell.Covered || cell.RowSpan > 1 || cell.Text.Length == 0) continue;
                    if (CellText(r, c) is { } t) need = Math.Max(need, t.Bounds.Height + 2 * Pad);
                }
                if (need > RowH[r]) RowH[r] = need;
            }
        }

        // ---------- drawing ----------
        private Color? FillOf(int r, int c)
        {
            var cell = Cells[r][c];
            if (cell.Fill != null) return cell.Fill;
            if (Header && r == 0) return HeaderFill;
            int first = Header ? 1 : 0;
            if (Stripes && r >= first && (r - first) % 2 == 1) return StripeFill;
            return null;
        }

        public IEnumerable<(Rect Box, Color Fill)> Fills()
        {
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    if (!Cells[r][c].Covered && FillOf(r, c) is { } f) yield return (CellRect(r, c), f);
        }

        public TextItem? CellText(int r, int c)
        {
            var cell = Cells[r][c];
            if (cell.Covered || cell.Text.Length == 0) return null;
            var rect = CellRect(r, c);
            var item = new TextItem
            {
                Page = Page, Color = TextColor, Text = cell.Text, FontSize = FontSize, Bold = cell.Bold || (Header && r == 0), Align = cell.Align < 0 ? 0 : cell.Align,
                TopLeft = new Point(rect.X + Pad, rect.Y + Pad), BoxWidth = Math.Max(4, rect.Width - 2 * Pad),
            };
            if (cell.VAlign is 1 or 2)                                                     // (middle / bottom: by the height of the words)
            {
                double h = item.Bounds.Height;
                double y = cell.VAlign == 1 ? rect.Y + (rect.Height - h) / 2 : rect.Bottom - Pad - h;
                item.TopLeft = new Point(rect.X + Pad, Math.Max(rect.Y + 1, y));
            }
            return item;
        }

        /// <summary>Where the picture of a cell sits: as big as fits inside the cell, with its own shape, in the middle (null = no picture).</summary>
        public Rect? PicBox(int r, int c)
        {
            var cell = Cells[r][c];
            if (cell.Covered || cell.Pic == null || cell.Pic.PixelWidth < 1 || cell.Pic.PixelHeight < 1) return null;
            var rect = CellRect(r, c);
            double aw = Math.Max(4, rect.Width - 2 * Pad), ah = Math.Max(4, rect.Height - 2 * Pad);
            double k = Math.Min(aw / cell.Pic.PixelWidth, ah / cell.Pic.PixelHeight);
            double w = cell.Pic.PixelWidth * k, h = cell.Pic.PixelHeight * k;
            return new Rect(rect.X + (rect.Width - w) / 2, rect.Y + (rect.Height - h) / 2, w, h);
        }

        private (int, int) Anchor(int r, int c) => (Cells[r][c].AnchorRow, Cells[r][c].AnchorCol);

        public List<PdfFigure> LineFigures()
        {
            var figures = new List<PdfFigure>();
            if (Borders == 3 || Rows == 0 || Cols == 0) return figures;
            var xs = Xs(); var ys = Ys();
            static PdfFigure Line(Point a, Point b) => new(a, new[] { PdfSegment.Line(b) }, false);
            for (int k = 0; k <= Rows; k++)                                      // lines across
            {
                bool outer = k == 0 || k == Rows;
                if (!outer && Borders == 1) continue;
                double? start = null;
                for (int c = 0; c < Cols; c++)
                {
                    bool hidden = !outer && Anchor(k - 1, c) == Anchor(k, c);
                    if (!hidden) start ??= xs[c];
                    if (start != null && (hidden || c == Cols - 1)) { figures.Add(Line(new Point(start.Value, ys[k]), new Point(hidden ? xs[c] : xs[c + 1], ys[k]))); start = null; }
                }
            }
            for (int k = 0; k <= Cols; k++)                                      // lines down
            {
                bool outer = k == 0 || k == Cols;
                if (!outer && Borders != 0) continue;
                double? start = null;
                for (int r = 0; r < Rows; r++)
                {
                    bool hidden = !outer && Anchor(r, k - 1) == Anchor(r, k);
                    if (!hidden) start ??= ys[r];
                    if (start != null && (hidden || r == Rows - 1)) { figures.Add(Line(new Point(xs[k], start.Value), new Point(xs[k], hidden ? ys[r] : ys[r + 1]))); start = null; }
                }
            }
            return figures;
        }

        public override FrameworkElement Build()
        {
            var canvas = new Canvas { IsHitTestVisible = false };
            foreach (var (box, fill) in Fills())
            {
                var rect = new System.Windows.Shapes.Rectangle { Width = box.Width, Height = box.Height, Fill = new SolidColorBrush(fill) };
                Canvas.SetLeft(rect, box.X); Canvas.SetTop(rect, box.Y);
                canvas.Children.Add(rect);
            }
            var blue = new SolidColorBrush(Color.FromRgb(0x2F, 0x6B, 0xEA));
            if (SelR1 >= 0 && Rows > 0)
            {
                var (r1, c1, r2, c2) = SelRange();
                var xs = Xs(); var ys = Ys();
                r1 = Math.Clamp(r1, 0, Rows - 1); r2 = Math.Clamp(r2, 0, Rows - 1); c1 = Math.Clamp(c1, 0, Cols - 1); c2 = Math.Clamp(c2, 0, Cols - 1);
                var mark = new System.Windows.Shapes.Rectangle
                {
                    Width = xs[c2 + 1] - xs[c1], Height = ys[r2 + 1] - ys[r1], Fill = new SolidColorBrush(Color.FromArgb(46, 0x2F, 0x6B, 0xEA)), Stroke = blue, StrokeThickness = 1.4,
                };
                Canvas.SetLeft(mark, xs[c1]); Canvas.SetTop(mark, ys[r1]);
                canvas.Children.Add(mark);
            }
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    if (PicBox(r, c) is { } pb)
                    {
                        var img = new Image { Source = Cells[r][c].Pic, Width = pb.Width, Height = pb.Height, Stretch = Stretch.Fill, IsHitTestVisible = false };
                        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                        Canvas.SetLeft(img, pb.X); Canvas.SetTop(img, pb.Y);
                        canvas.Children.Add(img);
                    }
            var lines = LineFigures();
            if (lines.Count > 0)
                canvas.Children.Add(new System.Windows.Shapes.Path { Data = Geometry(lines), Stroke = new SolidColorBrush(Color), StrokeThickness = Width, StrokeLineJoin = PenLineJoin.Miter, SnapsToDevicePixels = true });
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    if (CellText(r, c) is { } text) canvas.Children.Add(text.Build());
            if (SelR1 >= 0)
            {
                var g = Grip(1);
                var grip = new System.Windows.Shapes.Rectangle { Width = g.Width, Height = g.Height, Fill = Brushes.White, Stroke = blue, StrokeThickness = 1.4 };
                Canvas.SetLeft(grip, g.X); Canvas.SetTop(grip, g.Y);
                canvas.Children.Add(grip);
            }
            return canvas;
        }

        public override IEnumerable<PdfMark> Marks()
        {
            foreach (var (box, fill) in Fills())
                yield return new PdfPathMark(Page, new[] { new PdfFigure(box.TopLeft, new[] { PdfSegment.Line(box.TopRight), PdfSegment.Line(box.BottomRight), PdfSegment.Line(box.BottomLeft) }, true) }, null, 0, fill, false);
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    if (PicBox(r, c) is { } pb) yield return new PdfImageMark(Page, pb, Cells[r][c].PicJpeg, Cells[r][c].PicJpeg == null ? Cells[r][c].Pic : null);
            var lines = LineFigures();
            if (lines.Count > 0) yield return new PdfPathMark(Page, lines, Color, Width, null, false);
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                    if (CellText(r, c) is { } text)
                        foreach (var m in text.Marks()) yield return m;
        }
    }

    /// <summary>A small window for the number of rows and columns. Null = cancelled.</summary>
    private (int Rows, int Cols)? AskTableSize(int rows, int cols)
    {
        var rowsBox = new TextBox { Text = rows.ToString(), Width = 70, TextAlignment = TextAlignment.Center };
        var colsBox = new TextBox { Text = cols.ToString(), Width = 70, TextAlignment = TextAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetAutomationId(rowsBox, "PdfTableRows");
        System.Windows.Automation.AutomationProperties.SetAutomationId(colsBox, "PdfTableCols");
        var dlg = new Window
        {
            Title = "Table", Width = 330, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)Application.Current.FindResource("BgBrush"), FontFamily = new FontFamily("Segoe UI"), FontSize = 13.5,
        };
        WindowTheme.DarkTitleBar(dlg);
        var ok = new Button { Content = "OK", Style = (Style)Application.Current.FindResource("DialogPrimary"), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.FindResource("DialogButton"), IsCancel = true };
        System.Windows.Automation.AutomationProperties.SetAutomationId(ok, "PdfTableOk");
        bool accepted = false;
        ok.Click += (_, _) => { accepted = true; dlg.Close(); };
        var grid = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        grid.Children.Add(new TextBlock { Text = "Rows", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Foreground = Brushes.White });
        grid.Children.Add(rowsBox);
        grid.Children.Add(new TextBlock { Text = "Columns", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 8, 0), Foreground = Brushes.White });
        grid.Children.Add(colsBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = "How many rows and columns?", Opacity = 0.8, Foreground = Brushes.White });
        root.Children.Add(grid);
        root.Children.Add(new TextBlock { Text = "Then double-click a cell (or press Enter) and type; Tab goes to the next cell. Right-click the table for rows, columns, merging and colours.", Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Foreground = Brushes.White });
        root.Children.Add(buttons);
        dlg.Content = root;
        dlg.Loaded += (_, _) => { rowsBox.Focus(); rowsBox.SelectAll(); };
        dlg.ShowDialog();
        if (!accepted) return null;
        if (!int.TryParse(rowsBox.Text.Trim(), out int r) || !int.TryParse(colsBox.Text.Trim(), out int c)) return null;
        return (Math.Clamp(r, 1, 60), Math.Clamp(c, 1, 30));
    }

    /// <summary>Mouse let go after dragging a new table's size.</summary>
    private void FinishTable(TableItem table)
    {
        var r = new Rect(table.Box.TopLeft, table.Box.BottomRight);
        if (r.Width < 12 || r.Height < 12) { RenderItems(table.Page); return; }
        if (AskTableSize(_tableRows, _tableCols) is not { } size) { RenderItems(table.Page); return; }
        _tableRows = size.Rows; _tableCols = size.Cols;
        var made = TableItem.Create(table.Page, size.Rows, size.Cols, table.Color, table.Width);
        made.Box = r;
        made.FontSize = Math.Clamp(Math.Min(made.RowH[0] * 0.55, 12), 7, 12);
        Add(made, select: true);
        made.SetSel(0, 0);
        RenderItems(made.Page);
    }

    /// <summary>The "Table size…" entry: rows and columns are added or taken off the end.</summary>
    private void EditTable(TableItem table)
    {
        if (AskTableSize(table.Rows, table.Cols) is not { } size) return;
        Snapshot();
        while (table.Rows < size.Rows) table.InsertRow(table.Rows);
        while (table.Rows > size.Rows) table.DeleteRow(table.Rows - 1);
        while (table.Cols < size.Cols) table.InsertCol(table.Cols);
        while (table.Cols > size.Cols) table.DeleteCol(table.Cols - 1);
        table.FitRows();
        _tableRows = size.Rows; _tableCols = size.Cols;
        RenderItems(table.Page);
    }
}
