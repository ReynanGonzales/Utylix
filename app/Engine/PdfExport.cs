using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IdmClone.Engine;

/// <summary>One page ready to be saved as Word or Excel: its size, its pieces of text, and its pictures (cut out of the page).</summary>
public sealed record ExportPage(int Index, Size Size, List<PdfTextRun> Runs, List<(Rect Box, byte[] Png)> Pictures);

/// <summary>
/// PDF to Word (.docx) and Excel (.xlsx), written by hand as plain Office files (a zip of XML) on this PC. The text of a page is grouped into
/// lines and paragraphs (Word) or rows and columns (Excel) from where each piece sits on the page, with its font, size, bold, italic and colour.
/// It is a good start for editing, not a perfect copy: complicated layouts (columns, text round pictures) come out simpler.
/// </summary>
public static class PdfExport
{
    // ---------- reading a page ----------
    /// <summary>The text pieces and pictures of one page. <paramref name="ocr"/> = the words read from a picture of the page (for a scan without text).</summary>
    public static ExportPage Read(PdfFile pdf, int page, IReadOnlyList<OcrWord>? ocr, (int Width, int Height) pixels)
    {
        var size = pdf.PageSize(page);
        var runs = pdf.GetAllTextRuns(page);
        if (runs.Count == 0 && ocr is { Count: > 0 }) runs = RunsFromOcr(ocr, size, pixels);
        var pictures = new List<(Rect, byte[])>();
        var boxes = pdf.GetPictureBoxes(page);
        if (boxes.Count > 0)
        {
            double dpi = 150, k = dpi / 72;
            var sheet = pdf.Render(page, Math.Max(1, (int)(size.Width * k)), Math.Max(1, (int)(size.Height * k)), 0, forScreen: false);
            foreach (var box in boxes)
            {
                int x = Math.Clamp((int)Math.Floor(box.X * k), 0, sheet.PixelWidth - 1), y = Math.Clamp((int)Math.Floor(box.Y * k), 0, sheet.PixelHeight - 1);
                int w = Math.Clamp((int)Math.Ceiling(box.Width * k), 1, sheet.PixelWidth - x), h = Math.Clamp((int)Math.Ceiling(box.Height * k), 1, sheet.PixelHeight - y);
                var crop = new CroppedBitmap(sheet, new Int32Rect(x, y, w, h));
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(crop));
                using var ms = new MemoryStream(); enc.Save(ms);
                pictures.Add((box, ms.ToArray()));
            }
        }
        return new ExportPage(page, size, runs, pictures);
    }

    /// <summary>The words of an OCR read as text pieces: the size is worked out from how tall the words are on the page.</summary>
    private static List<PdfTextRun> RunsFromOcr(IReadOnlyList<OcrWord> words, Size page, (int Width, int Height) pixels)
    {
        double kx = page.Width / pixels.Width, ky = page.Height / pixels.Height;
        var heights = words.Select(w => w.Box.Height * ky).OrderBy(h => h).ToList();
        double median = heights[heights.Count / 2];
        double basic = Math.Clamp(median / 0.76, 6, 40);
        var runs = new List<PdfTextRun>();
        int i = 0;
        foreach (var w in words)
        {
            double h = w.Box.Height * ky, ratio = h / Math.Max(1, median);
            double size = ratio > 1.35 ? Math.Clamp(basic * ratio * 0.85, 6, 72) : basic;
            var box = new Rect(w.Box.X * kx, w.Box.Y * ky, w.Box.Width * kx, h);
            runs.Add(new PdfTextRun(i++, box, w.Text, Math.Round(size * 2) / 2, "Arial", ratio > 1.35, false, Colors.Black, new Point(box.Left, box.Bottom - h * 0.1)));
        }
        return runs;
    }

    // ---------- lines and paragraphs ----------
    private sealed class Piece
    {
        public string Text = ""; public double Left, Right, Size; public string Family = "Arial"; public bool Bold, Italic; public Color Color;
        public bool SameLook(Piece o) => Family == o.Family && Bold == o.Bold && Italic == o.Italic && Color == o.Color && Math.Abs(Size - o.Size) < 0.6;
    }

    private sealed class Line
    {
        public List<Piece> Pieces = new();
        public double Baseline, Top, Left, Right, Size;
        public List<(double Gap, int Before)> Gaps = new();       // (width of the gap in points, the piece it comes before)
    }

    private sealed class Para
    {
        public List<Line> Lines = new();
        public double Top => Lines[0].Top;
        public double GapBefore;
    }

    private static List<Line> Lines(IEnumerable<PdfTextRun> runs)
    {
        var lines = new List<Line>();
        foreach (var run in runs.OrderBy(r => r.Baseline.Y).ThenBy(r => r.Box.Left))
        {
            var line = lines.LastOrDefault();
            if (line == null || Math.Abs(run.Baseline.Y - line.Baseline) > Math.Max(2, Math.Min(run.Size, line.Size) * 0.5))
            {
                line = new Line { Baseline = run.Baseline.Y, Top = run.Box.Top, Size = run.Size };
                lines.Add(line);
            }
            line.Pieces.Add(new Piece { Text = run.Text, Left = run.Box.Left, Right = run.Box.Right, Size = run.Size, Family = run.Family, Bold = run.Bold, Italic = run.Italic, Color = run.Color });
            line.Top = Math.Min(line.Top, run.Box.Top);
            line.Size = Math.Max(line.Size, run.Size);
        }
        foreach (var line in lines)
        {
            line.Pieces.Sort((a, b) => a.Left.CompareTo(b.Left));
            line.Left = line.Pieces[0].Left; line.Right = line.Pieces.Max(p => p.Right);
        }
        return lines;
    }

    private static List<Para> Paragraphs(List<Line> lines)
    {
        var paras = new List<Para>();
        if (lines.Count == 0) return paras;
        double left = lines.Min(l => l.Left);
        var rights = lines.Select(l => l.Right).OrderBy(r => r).ToList();
        double right = rights[Math.Min(rights.Count - 1, (int)(rights.Count * 0.75))];      // (where the body text usually ends: a lone line far out, like a footer, doesn't count)
        double width = Math.Max(1, right - left);
        Line? prev = null;
        foreach (var line in lines)
        {
            double gap = prev == null ? 0 : line.Baseline - prev.Baseline;
            double pitch = Math.Max(prev?.Size ?? line.Size, line.Size) * 1.25;
            bool shortPrev = prev != null && prev.Right < right - width * 0.30;           // (a line that stops well short of the right edge ends its paragraph; ragged lines don't)
            bool columns = Columned(line) || (prev != null && Columned(prev));            // (a row of a table: each row is its own paragraph)
            bool breakHere = prev == null || gap > pitch * 1.35 || shortPrev || columns || line.Left - prev.Left > line.Size * 1.5 || Math.Abs(line.Size - prev.Size) > Math.Max(prev.Size, line.Size) * 0.2;
            if (breakHere) paras.Add(new Para { GapBefore = gap });
            paras[^1].Lines.Add(line);
            prev = line;
        }
        return paras;
    }

    /// <summary>The line has wide gaps in it: a row of columns rather than running text.</summary>
    private static bool Columned(Line line)
    {
        for (int i = 1; i < line.Pieces.Count; i++) if (line.Pieces[i].Left - line.Pieces[i - 1].Right > line.Size * 2.5) return true;
        return false;
    }

    /// <summary>Every line of the paragraph sits on the middle of the page, and the paragraph is narrower than the text block.</summary>
    private static bool IsCentered(Para para, double pageWidth, double blockWidth)
    {
        double minLeft = para.Lines.Min(l => l.Left), maxRight = para.Lines.Max(l => l.Right);
        return maxRight - minLeft < pageWidth * 0.8 && para.Lines.All(l => Math.Abs((l.Left + l.Right) / 2 - pageWidth / 2) < pageWidth * 0.02);
    }

    /// <summary>The line as text pieces with a space (or a tab for a wide gap) where the page has a gap.</summary>
    private static List<(string Text, Piece Look, bool Tab)> Flow(Line line)
    {
        var parts = new List<(string, Piece, bool)>();
        Piece? prev = null;
        foreach (var piece in line.Pieces)
        {
            bool tab = false; string text = piece.Text;
            if (prev != null)
            {
                double gap = piece.Left - prev.Right;
                if (gap > line.Size * 2.5) tab = true;
                else if (gap > line.Size * 0.18 && !prev.Text.EndsWith(' ') && !text.StartsWith(' ')) text = " " + text;
            }
            parts.Add((text, piece, tab));
            prev = piece;
        }
        return parts;
    }

    // ---------- tables (Word) ----------
    /// <summary>Span = how many columns the cell covers (a merged cell); More = the next lines of a cell whose text wraps onto several lines.</summary>
    private sealed class TCell { public int Col; public int Span = 1; public double Left, Right; public List<Piece> Pieces = new(); public List<List<Piece>> More = new(); }
    private sealed class TRow { public Line Line = null!; public List<TCell> Cells = new(); public double Pitch, LastBaseline; public bool Centred; }
    private sealed class Table
    {
        public List<TRow> Rows = new();
        public List<Line> Absorbed = new();                // lines that are the next line of a cell above (not rows of their own)
        public List<(double From, double To)> Columns = new();
        public bool[] Numeric = Array.Empty<bool>();
        public double Top => Rows[0].Line.Top;
    }

    /// <summary>The line cut into cells: pieces that are far apart (more than about a letter's width) are different cells.</summary>
    private static List<TCell> CellsOf(Line line)
    {
        var cells = new List<TCell>();
        TCell? cell = null; Piece? prev = null;
        foreach (var piece in line.Pieces)
        {
            if (cell == null || prev == null || piece.Left - prev.Right > Math.Max(line.Size * 0.9, 7)) { cell = new TCell { Left = piece.Left }; cells.Add(cell); }
            cell.Pieces.Add(piece); cell.Right = piece.Right;
            prev = piece;
        }
        return cells;
    }

    /// <summary>
    /// Finds the tables of a page: neighbouring lines that are made of several cells which line up in columns. A line with a single cell between two
    /// such lines is a row with empty cells. Lines that belong to a table are taken out of <paramref name="lines"/>.
    /// </summary>
    private static List<Table> FindTables(List<Line> lines, double pageText)
    {
        var tables = new List<Table>();
        var cellsOf = lines.Select(CellsOf).ToList();
        var inTable = new HashSet<Line>();
        int i = 0;
        while (i < lines.Count)
        {
            if (cellsOf[i].Count < 2) { i++; continue; }
            int j = i, multi = 1;
            while (j + 1 < lines.Count)
            {
                // the next row: up to three lines with a single cell may lie between two rows (an empty-cells row, or the lines of a wrapped cell)
                int n = 1; bool found = false;
                while (j + n < lines.Count && n <= 4)
                {
                    int at = j + n;
                    if (lines[at].Baseline - lines[at - 1].Baseline > Math.Max(lines[at].Size, lines[at - 1].Size) * 2.6) break;
                    if (cellsOf[at].Count >= 2) { found = true; break; }
                    n++;
                }
                if (!found) break;
                j += n; multi++;
            }
            if (multi >= 2 && TryBuildTable(lines, cellsOf, i, j, pageText) is Table table)
            {
                // a line just above the table, alone, that sits on the middle of the table (or reaches over several columns) is its heading row: one cell across all the columns
                if (i > 0 && cellsOf[i - 1].Count == 1 && !inTable.Contains(lines[i - 1]) && lines[i].Baseline - lines[i - 1].Baseline <= Math.Max(lines[i].Size, lines[i - 1].Size) * 2.6)
                {
                    var head = cellsOf[i - 1][0];
                    double tableLeft = table.Columns[0].From, tableRight = Math.Max(table.Columns[^1].To, table.Rows.SelectMany(r => r.Cells).Max(c => c.Right));
                    double mid = (head.Left + head.Right) / 2, tableMid = (tableLeft + tableRight) / 2;
                    bool centred = Math.Abs(mid - tableMid) < (tableRight - tableLeft) * 0.2 && head.Left >= tableLeft - 2 && head.Right <= tableRight + 2;
                    if (centred && head.Right - head.Left < (tableRight - tableLeft) * 0.95)
                    {
                        head.Col = 0; head.Span = table.Columns.Count;
                        table.Rows.Insert(0, new TRow { Line = lines[i - 1], Cells = { head }, Pitch = lines[i].Baseline - lines[i - 1].Baseline, LastBaseline = lines[i - 1].Baseline, Centred = true });
                        i--;
                    }
                }
                tables.Add(table); foreach (var r in table.Rows) inTable.Add(r.Line); foreach (var l in table.Absorbed) inTable.Add(l); i = j + 1;
            }
            else i++;
        }
        lines.RemoveAll(inTable.Contains);
        return tables;
    }

    private static Table? TryBuildTable(List<Line> lines, List<List<TCell>> cellsOf, int from, int to, double pageText)
    {
        // the columns: x ranges covered by the cells of rows that have several cells (a wide cell across the table is left out)
        double typical = lines.Skip(from).Take(to - from + 1).Average(l => l.Size);
        var spans = new List<TCell>();
        for (int k = from; k <= to; k++) if (cellsOf[k].Count >= 2) spans.AddRange(cellsOf[k].Where(c => c.Right - c.Left < pageText * 0.6));
        spans.Sort((a, b) => a.Left.CompareTo(b.Left));
        var columns = new List<(double From, double To)>();
        foreach (var span in spans)
        {
            if (columns.Count > 0 && span.Left <= columns[^1].To + typical * 0.9) columns[^1] = (columns[^1].From, Math.Max(columns[^1].To, span.Right));
            else columns.Add((span.Left, span.Right));
        }
        if (columns.Count < 2) return null;
        int ColumnOf(double x) { int best = 0; for (int c = 0; c < columns.Count; c++) if (columns[c].From <= x + typical * 0.5) best = c; return best; }

        var table = new Table { Columns = columns };
        int rowsWithSeveralColumns = 0;
        // how far apart the rows of the table usually are (the baselines of lines that have several cells): a line much closer than that to the one above is the next line of a wrapped cell
        var gaps = new List<double>();
        for (int k = from + 1; k <= to; k++) if (cellsOf[k].Count >= 2 && cellsOf[k - 1].Count >= 2) gaps.Add(lines[k].Baseline - lines[k - 1].Baseline);
        double rowGap = gaps.Count > 0 ? gaps.OrderBy(g => g).ElementAt(gaps.Count / 2) : typical * 1.5;
        for (int k = from; k <= to; k++)
        {
            var row = new TRow { Line = lines[k] };
            int last = -1;
            foreach (var cell in cellsOf[k])
            {
                int col = cellsOf[k].Count >= 2 ? Math.Max(ColumnOf((cell.Left + cell.Right) / 2), ColumnOfStart(columns, cell.Left, typical)) : ColumnOf(cell.Left);
                col = Math.Max(col, last + 1);
                if (col >= columns.Count) return null;                                     // (more cells than columns: not a tidy table)
                cell.Col = col; last = col;
                row.Cells.Add(cell);
            }
            // a cell that reaches over the start of the next column(s) is a merged cell
            for (int ci = 0; ci < row.Cells.Count; ci++)
            {
                var cell = row.Cells[ci];
                int end = cell.Col;
                for (int c = cell.Col + 1; c < columns.Count; c++) if (columns[c].From < cell.Right - typical * 0.5) end = c;
                int limit = ci + 1 < row.Cells.Count ? row.Cells[ci + 1].Col - 1 : columns.Count - 1;
                cell.Span = Math.Max(1, Math.Min(end, limit) - cell.Col + 1);
            }
            // the next line of a cell: nothing in the first column, tight under the row above, and every cell sits under a cell of that row that either looks
            // wrapped (its text reaches the edge of the column) or the lines are clearly closer together than the rows of the table are
            if (table.Rows.Count > 0 && row.Cells.Count > 0 && row.Cells[0].Col > 0 && lines[k].Baseline - table.Rows[^1].LastBaseline <= typical * 1.45)
            {
                var above = table.Rows[^1];
                bool closer = lines[k].Baseline - table.Rows[^1].LastBaseline < rowGap * 0.9;
                var targets = row.Cells.Select(c => above.Cells.FirstOrDefault(a => a.Col <= c.Col && c.Col < a.Col + a.Span)).ToList();
                bool wrapped = targets.All(t => t != null && Math.Max(t.Right, t.More.Count > 0 ? t.More.Max(m => m.Max(p => p.Right)) : 0) >= columns[Math.Min(columns.Count - 1, t.Col + t.Span - 1)].To - typical * 1.6);
                if (targets.All(t => t != null) && targets.Distinct().Count() == targets.Count && (closer || wrapped))
                {
                    for (int ci = 0; ci < row.Cells.Count; ci++) targets[ci]!.More.Add(row.Cells[ci].Pieces);
                    above.LastBaseline = lines[k].Baseline;
                    table.Absorbed.Add(lines[k]);
                    continue;
                }
            }
            row.LastBaseline = lines[k].Baseline;
            if (row.Cells.Select(c => c.Col).Distinct().Count() >= 2) rowsWithSeveralColumns++;
            table.Rows.Add(row);
        }
        if (rowsWithSeveralColumns < 2) return null;
        // the height of a row = from its first line to the first line of the next row
        for (int r = 0; r + 1 < table.Rows.Count; r++) table.Rows[r].Pitch = table.Rows[r + 1].Line.Baseline - table.Rows[r].Line.Baseline;
        double avgPitch = table.Rows.Where(r => r.Pitch > 0).Select(r => r.Pitch).DefaultIfEmpty(typical * 1.2).Average();
        foreach (var r in table.Rows) if (r.Pitch <= 0) r.Pitch = avgPitch;
        // columns of numbers are written against the right edge of their cell
        table.Numeric = new bool[columns.Count];
        for (int c = 0; c < columns.Count; c++)
        {
            var texts = table.Rows.SelectMany(r => r.Cells.Where(x => x.Col == c)).Select(x => string.Concat(x.Pieces.Select(p => p.Text)).Trim()).Where(t => t.Length > 0).ToList();
            table.Numeric[c] = texts.Count >= 2 && texts.Count(t => t.All(ch => char.IsDigit(ch) || ",.-()%$₱€£ ".IndexOf(ch) >= 0) && t.Any(char.IsDigit)) * 2 > texts.Count;
        }
        return table;
    }

    private static string TableXml(Table table, double pageLeft, double above)
    {
        var cols = table.Columns;
        var widths = new List<double>();
        double right = Math.Max(cols[^1].To, table.Rows.SelectMany(r => r.Cells).Max(c => c.Right));
        for (int c = 0; c < cols.Count; c++) widths.Add(c + 1 < cols.Count ? cols[c + 1].From - cols[c].From : Math.Max(24, right - cols[c].From + 8));
        double indent = Math.Max(0, cols[0].From - pageLeft - 2);
        var sb = new StringBuilder();
        // (the space the page has between the text above and the table: an empty paragraph of exactly that height)
        if (above > 3) sb.Append($"<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\" w:line=\"{Tw(above)}\" w:lineRule=\"exact\"/></w:pPr></w:p>");
        sb.Append("<w:tbl><w:tblPr>");
        sb.Append($"<w:tblW w:w=\"{Tw(widths.Sum())}\" w:type=\"dxa\"/><w:tblInd w:w=\"{Tw(indent)}\" w:type=\"dxa\"/>");
        sb.Append("<w:tblBorders>");
        foreach (var side in new[] { "top", "left", "bottom", "right", "insideH", "insideV" }) sb.Append($"<w:{side} w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/>");
        sb.Append("</w:tblBorders><w:tblLayout w:type=\"fixed\"/><w:tblCellMar><w:top w:w=\"0\" w:type=\"dxa\"/><w:left w:w=\"40\" w:type=\"dxa\"/><w:bottom w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"40\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr>");
        sb.Append("<w:tblGrid>" + string.Concat(widths.Select(w => $"<w:gridCol w:w=\"{Tw(w)}\"/>")) + "</w:tblGrid>");
        foreach (var row in table.Rows)
        {
            sb.Append($"<w:tr><w:trPr><w:cantSplit/><w:trHeight w:val=\"{Tw(Math.Clamp(row.Pitch, 8, 80))}\" w:hRule=\"atLeast\"/></w:trPr>");
            for (int c = 0; c < cols.Count; c++)
            {
                var cell = row.Cells.FirstOrDefault(x => x.Col == c);
                int span = cell == null ? 1 : Math.Max(1, Math.Min(cell.Span, cols.Count - c));
                double cellWidth = 0;
                for (int s = 0; s < span; s++) cellWidth += widths[c + s];
                sb.Append($"<w:tc><w:tcPr><w:tcW w:w=\"{Tw(cellWidth)}\" w:type=\"dxa\"/>{(span > 1 ? $"<w:gridSpan w:val=\"{span}\"/>" : "")}<w:vAlign w:val=\"{(cell is { More.Count: > 0 } ? "top" : "center")}\"/></w:tcPr>");
                double size = cell?.Pieces.Max(p => p.Size) ?? row.Line.Size;
                sb.Append($"<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\" w:line=\"{Tw(Math.Max(6, size * 1.2))}\" w:lineRule=\"atLeast\"/>{(row.Centred ? "<w:jc w:val=\"center\"/>" : span == 1 && table.Numeric[c] ? "<w:jc w:val=\"right\"/>" : "")}</w:pPr>");
                if (cell != null)
                {
                    // the pieces of the cell, then the pieces of the lines that wrapped under it (one paragraph: Word wraps it again at the cell's edge)
                    Piece? prev = null;
                    foreach (var piece in cell.Pieces.Concat(cell.More.SelectMany(m => m)))
                    {
                        string text = piece.Text;
                        bool newLine = prev != null && !cell.Pieces.Contains(piece) && cell.Pieces.Contains(prev) || prev != null && piece.Left < prev.Left - 1;       // (the next line of the cell starts further left)
                        if (prev != null && (newLine || piece.Left - prev.Right > row.Line.Size * 0.18) && !prev.Text.EndsWith(' ') && !text.StartsWith(' ')) text = " " + text;
                        sb.Append(RunXml(text, piece, false));
                        prev = piece;
                    }
                }
                sb.Append("</w:p></w:tc>");
                c += span - 1;
            }
            sb.Append("</w:tr>");
        }
        sb.Append("</w:tbl>");
        // (a table must be followed by a paragraph: a thin empty one)
        sb.Append("<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\" w:line=\"20\" w:lineRule=\"exact\"/></w:pPr></w:p>");
        return sb.ToString();
    }

    // ---------- Word ----------
    public static byte[] ToDocx(IReadOnlyList<ExportPage> pages)
    {
        var body = new StringBuilder();
        var media = new List<byte[]>();
        int docPr = 1;
        for (int p = 0; p < pages.Count; p++)
        {
            var page = pages[p];
            var lines = Lines(page.Runs);
            // the margins and the text block are worked out from ALL the lines (the tables included); the tables are then taken out of the running text
            var allLines = lines.ToList();
            double pageText = allLines.Count > 0 ? Math.Max(1, allLines.Max(l => l.Right) - allLines.Min(l => l.Left)) : 1;
            var tables = FindTables(lines, pageText);
            var paras = Paragraphs(lines);
            lines = allLines;
            double left = lines.Count > 0 ? Math.Max(0, lines.Min(l => l.Left)) : 36, right = lines.Count > 0 ? lines.Max(l => l.Right) : page.Size.Width - 36;
            double topMargin = lines.Count > 0 ? Math.Clamp(Math.Min(lines[0].Top, page.Pictures.Count > 0 ? page.Pictures.Min(pic => pic.Box.Top) : double.MaxValue) - 2, 12, page.Size.Height / 2) : 36;
            double rightMargin = Math.Max(12, page.Size.Width - right);
            double blockWidth = Math.Max(1, right - left);
            // with centred paragraphs the two margins are the same, so the middle of the text area is the middle of the page
            bool anyCentered = paras.Any(pa => IsCentered(pa, page.Size.Width, blockWidth));
            if (anyCentered) { left = rightMargin = Math.Min(left, rightMargin); right = page.Size.Width - rightMargin; }
            double textWidth = Math.Max(1, right - left);
            double pageWidth = page.Size.Width;

            // paragraphs and pictures in reading order
            var items = new List<(double Top, Func<string> Xml)>();
            foreach (var para in paras) items.Add((para.Top, () => ParagraphXml(para, left, right, textWidth, pageWidth)));
            foreach (var table in tables)
            {
                var t = table;
                var rowLines = new HashSet<Line>(t.Rows.Select(r => r.Line));
                var above = allLines.Where(l => l.Baseline < t.Top && !rowLines.Contains(l) && !tables.Any(o => o.Rows.Any(r => r.Line == l))).OrderByDescending(l => l.Baseline).FirstOrDefault();
                double gap = above == null ? 0 : Math.Clamp(t.Top - (above.Baseline + above.Size * 0.3), 0, 60);
                items.Add((t.Top, () => TableXml(t, left, gap)));
            }
            foreach (var (box, png) in page.Pictures)
            {
                media.Add(png);
                int id = media.Count, num = docPr++;
                var b = box;
                items.Add((box.Top, () => PictureXml(id, num, b, left)));
            }
            foreach (var item in items.OrderBy(i => i.Top)) body.Append(item.Xml());

            // each page is its own section: it starts a new page and has the page's own size and margins
            string sect = $"<w:sectPr><w:type w:val=\"nextPage\"/><w:pgSz w:w=\"{Tw(page.Size.Width)}\" w:h=\"{Tw(page.Size.Height)}\"/>" +
                          $"<w:pgMar w:top=\"{Tw(topMargin)}\" w:right=\"{Tw(rightMargin)}\" w:bottom=\"{Tw(36)}\" w:left=\"{Tw(left)}\" w:header=\"0\" w:footer=\"0\" w:gutter=\"0\"/></w:sectPr>";
            if (p < pages.Count - 1) body.Append("<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\" w:line=\"20\" w:lineRule=\"exact\"/>" + sect + "</w:pPr></w:p>");
            else body.Append(sect);
        }

        string document = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
            "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
            "<w:body>" + body + "</w:body></w:document>";
        var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        rels.Append("<Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        for (int i = 0; i < media.Count; i++) rels.Append($"<Relationship Id=\"rIdImg{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/image{i + 1}.png\"/>");
        rels.Append("</Relationships>");

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            Add(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"png\" ContentType=\"image/png\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/></Types>");
            Add(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Add(zip, "word/document.xml", document);
            Add(zip, "word/_rels/document.xml.rels", rels.ToString());
            Add(zip, "word/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:cs=\"Calibri\" w:eastAsia=\"Calibri\"/><w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/></w:rPr></w:rPrDefault>" +
                "<w:pPrDefault><w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
                "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style></w:styles>");
            for (int i = 0; i < media.Count; i++)
            {
                var entry = zip.CreateEntry($"word/media/image{i + 1}.png", CompressionLevel.NoCompression);
                using var s = entry.Open(); s.Write(media[i], 0, media[i].Length);
            }
        }
        return ms.ToArray();
    }

    private static long Tw(double points) => (long)Math.Round(points * 20);

    private static string ParagraphXml(Para para, double pageLeft, double pageRight, double textWidth, double pageWidth)
    {
        var lines = para.Lines;
        double minLeft = lines.Min(l => l.Left), maxRight = lines.Max(l => l.Right);
        double firstLeft = lines[0].Left, restLeft = lines.Count > 1 ? lines.Skip(1).Min(l => l.Left) : firstLeft;
        string align = "left";
        if (Math.Abs(pageLeft - (pageWidth - pageRight)) < 1 && IsCentered(para, pageWidth, textWidth)) align = "center";      // (margins are the same only when something is centred)
        else if (maxRight > pageRight - textWidth * 0.02 && minLeft > pageLeft + textWidth * 0.35 && lines.Count == 1) align = "right";
        double indent = align == "left" ? Math.Max(0, restLeft - pageLeft) : 0;
        double first = align == "left" ? firstLeft - restLeft : 0;
        double pitch = lines.Count > 1 ? (lines[^1].Baseline - lines[0].Baseline) / (lines.Count - 1) : lines[0].Size * 1.2;
        double before = Math.Clamp((para.GapBefore - Math.Max(pitch, lines[0].Size * 1.2)) , 0, 40);

        var sb = new StringBuilder("<w:p><w:pPr>");
        // tab stops where the page has wide gaps inside a line (columns): numbers are lined up on their right edge
        var stops = new SortedDictionary<long, string>();
        foreach (var line in lines)
            foreach (var (_, look, tab) in Flow(line))
                if (tab)
                {
                    bool numeric = look.Text.Trim().Length > 0 && look.Text.Trim().All(ch => char.IsDigit(ch) || ",.-()%$₱€£".IndexOf(ch) >= 0);
                    stops[Tw((numeric ? look.Right : look.Left) - pageLeft + 0.0)] = numeric ? "right" : "left";
                }
        if (stops.Count > 0) sb.Append("<w:tabs>" + string.Concat(stops.Select(s => $"<w:tab w:val=\"{s.Value}\" w:pos=\"{Math.Max(0, s.Key)}\"/>")) + "</w:tabs>");
        sb.Append($"<w:spacing w:before=\"{Tw(before)}\" w:after=\"0\" w:line=\"{Tw(Math.Max(pitch, 6))}\" w:lineRule=\"atLeast\"/>");
        if (indent > 1 || Math.Abs(first) > 1) sb.Append($"<w:ind w:left=\"{Tw(indent)}\"{(first > 1 ? $" w:firstLine=\"{Tw(first)}\"" : first < -1 ? $" w:hanging=\"{Tw(-first)}\"" : "")}/>");
        if (align != "left") sb.Append($"<w:jc w:val=\"{align}\"/>");
        sb.Append("</w:pPr>");
        for (int i = 0; i < lines.Count; i++)
        {
            var parts = Flow(lines[i]);
            if (i > 0 && parts.Count > 0 && !parts[0].Text.StartsWith(' ')) { /* a wrapped line goes on with a space */ var last = parts[0]; parts[0] = (" " + last.Text, last.Look, last.Tab); }
            foreach (var (text, look, tab) in parts) sb.Append(RunXml(text, look, tab));
        }
        sb.Append("</w:p>");
        return sb.ToString();
    }

    private static string RunXml(string text, Piece look, bool tabBefore)
    {
        var sb = new StringBuilder("<w:r><w:rPr>");
        string font = Escape(look.Family);
        sb.Append($"<w:rFonts w:ascii=\"{font}\" w:hAnsi=\"{font}\" w:cs=\"{font}\"/>");
        if (look.Bold) sb.Append("<w:b/>");
        if (look.Italic) sb.Append("<w:i/>");
        if (look.Color.A > 0 && !(look.Color.R == 0 && look.Color.G == 0 && look.Color.B == 0)) sb.Append($"<w:color w:val=\"{look.Color.R:X2}{look.Color.G:X2}{look.Color.B:X2}\"/>");
        int half = (int)Math.Clamp(Math.Round(look.Size * 2), 2, 400);
        sb.Append($"<w:sz w:val=\"{half}\"/><w:szCs w:val=\"{half}\"/></w:rPr>");
        if (tabBefore) sb.Append("<w:tab/>");
        sb.Append($"<w:t xml:space=\"preserve\">{Escape(text)}</w:t></w:r>");
        return sb.ToString();
    }

    private static string PictureXml(int mediaId, int number, Rect box, double pageLeft)
    {
        long cx = (long)(box.Width * 12700), cy = (long)(box.Height * 12700);
        return $"<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\"/><w:ind w:left=\"{Tw(Math.Max(0, box.Left - pageLeft))}\"/></w:pPr><w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"{cx}\" cy=\"{cy}\"/>" +
               $"<wp:docPr id=\"{number}\" name=\"Picture {number}\"/><a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic><pic:nvPicPr><pic:cNvPr id=\"{number}\" name=\"image{mediaId}.png\"/><pic:cNvPicPr/></pic:nvPicPr>" +
               $"<pic:blipFill><a:blip r:embed=\"rIdImg{mediaId}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>";
    }

    // ---------- Excel ----------
    private sealed record Cell(double Left, double Right, string Text, bool Bold);

    /// <param name="oneSheet">All pages one after another on a single sheet (otherwise one sheet for each page).</param>
    public static byte[] ToXlsx(IReadOnlyList<ExportPage> pages, bool oneSheet)
    {
        var sheets = new List<(string Name, List<List<(int Col, string Text, bool Bold)>> Rows, List<double> Widths)>();
        var combined = new List<List<(int Col, string Text, bool Bold)>>();
        var combinedWidths = new List<double>();
        foreach (var page in pages)
        {
            var (rows, widths) = Grid(page);
            if (oneSheet)
            {
                if (combined.Count > 0) combined.Add(new());                       // (a blank row between pages)
                combined.AddRange(rows);
                for (int c = 0; c < widths.Count; c++) { if (c >= combinedWidths.Count) combinedWidths.Add(widths[c]); else combinedWidths[c] = Math.Max(combinedWidths[c], widths[c]); }
            }
            else sheets.Add(("Page " + (page.Index + 1), rows, widths));
        }
        if (oneSheet) sheets.Add(("Pages", combined, combinedWidths));
        if (sheets.Count == 0) sheets.Add(("Page 1", new(), new()));

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var types = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            types.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            var wb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            var wbRels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int i = 0; i < sheets.Count; i++)
            {
                types.Append($"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
                wb.Append($"<sheet name=\"{Escape(sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
                wbRels.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
                Add(zip, $"xl/worksheets/sheet{i + 1}.xml", SheetXml(sheets[i].Rows, sheets[i].Widths));
            }
            wbRels.Append($"<Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
            wb.Append("</sheets></workbook>"); types.Append("</Types>");
            Add(zip, "[Content_Types].xml", types.ToString());
            Add(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Add(zip, "xl/workbook.xml", wb.ToString());
            Add(zip, "xl/_rels/workbook.xml.rels", wbRels.ToString());
            Add(zip, "xl/styles.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
                "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>" +
                "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
        }
        return ms.ToArray();
    }

    /// <summary>The rows and columns of a page: pieces on a line that are far apart are separate cells; the columns are where cells of several lines line up.</summary>
    private static (List<List<(int Col, string Text, bool Bold)>> Rows, List<double> Widths) Grid(ExportPage page)
    {
        var lines = Lines(page.Runs);
        var lineCells = new List<(Line Line, List<Cell> Cells)>();
        foreach (var line in lines)
        {
            var cells = new List<Cell>();
            Piece? prev = null; var texts = new StringBuilder(); double left = 0, right = 0; bool bold = true, any = false;
            void Close() { if (any) cells.Add(new Cell(left, right, texts.ToString().Trim(), bold)); texts.Clear(); any = false; bold = true; }
            foreach (var piece in line.Pieces)
            {
                if (prev != null && piece.Left - prev.Right > Math.Max(line.Size * 0.9, 7)) Close();
                if (!any) { left = piece.Left; any = true; }
                else if (piece.Left - prev!.Right > line.Size * 0.18 && !prev.Text.EndsWith(' ') && !piece.Text.StartsWith(' ')) texts.Append(' ');
                texts.Append(piece.Text); right = piece.Right; bold &= piece.Bold;
                prev = piece;
            }
            Close();
            lineCells.Add((line, cells));
        }

        // columns: x ranges covered by cells of lines that have several cells (a wide heading across the table is left out)
        double pageText = lines.Count > 0 ? Math.Max(1, lines.Max(l => l.Right) - lines.Min(l => l.Left)) : 1;
        var spans = lineCells.Where(lc => lc.Cells.Count >= 2).SelectMany(lc => lc.Cells.Where(c => c.Right - c.Left < pageText * 0.45)).OrderBy(c => c.Left).ToList();
        var columns = new List<(double From, double To)>();
        double typical = lines.Count > 0 ? lines.Average(l => l.Size) : 10;
        foreach (var span in spans)
        {
            if (columns.Count > 0 && span.Left <= columns[^1].To + typical * 0.9) columns[^1] = (columns[^1].From, Math.Max(columns[^1].To, span.Right));
            else columns.Add((span.Left, span.Right));
        }
        int ColumnOf(double x)
        {
            if (columns.Count == 0) return 0;
            int best = 0;
            for (int i = 0; i < columns.Count; i++) if (columns[i].From <= x + typical * 0.5) best = i;
            return best;
        }

        var rows = new List<List<(int, string, bool)>>();
        Line? previous = null;
        foreach (var (line, cells) in lineCells)
        {
            if (previous != null && line.Baseline - previous.Baseline > Math.Max(previous.Size, line.Size) * 2.1) rows.Add(new());          // (a gap of several lines: a blank row)
            var row = new List<(int, string, bool)>();
            int lastCol = -1;
            foreach (var cell in cells)
            {
                int col = cells.Count >= 2 ? Math.Max(ColumnOf((cell.Left + cell.Right) / 2), ColumnOfStart(columns, cell.Left, typical)) : ColumnOf(cell.Left);
                col = Math.Max(col, lastCol + 1);                                              // (two cells never share a column)
                lastCol = col;
                row.Add((col, cell.Text, cell.Bold));
            }
            rows.Add(row);
            previous = line;
        }
        int colCount = Math.Max(1, rows.SelectMany(r => r).Select(c => c.Item1 + 1).DefaultIfEmpty(1).Max());
        var widths = Enumerable.Repeat(8.0, colCount).ToList();
        foreach (var r in rows) foreach (var (col, text, _) in r) if (text.Length < 60 || col == colCount - 1) widths[col] = Math.Clamp(Math.Max(widths[col], text.Length * 1.1 + 2), 8, 60);
        return (rows, widths);
    }

    /// <summary>The column whose range contains the start of the cell (or the nearest one before it).</summary>
    private static int ColumnOfStart(List<(double From, double To)> columns, double x, double tolerance)
    {
        for (int i = 0; i < columns.Count; i++) if (x >= columns[i].From - tolerance && x <= columns[i].To + tolerance) return i;
        return 0;
    }

    private static readonly Regex Number = new(@"^-?\(?-?\d{1,3}(,\d{3})+(\.\d+)?\)?$|^-?\(?\d+(\.\d+)?\)?$", RegexOptions.Compiled);

    private static string SheetXml(List<List<(int Col, string Text, bool Bold)>> rows, List<double> widths)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        if (widths.Count > 0)
        {
            sb.Append("<cols>");
            for (int i = 0; i < widths.Count; i++) sb.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{widths[i].ToString("0.#", CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
            sb.Append("</cols>");
        }
        sb.Append("<sheetData>");
        for (int r = 0; r < rows.Count; r++)
        {
            sb.Append($"<row r=\"{r + 1}\">");
            foreach (var (col, text, bold) in rows[r].OrderBy(c => c.Col))
            {
                string reference = ColumnName(col) + (r + 1);
                string style = bold ? " s=\"1\"" : "";
                string plain = text.Trim();
                if (Number.IsMatch(plain))
                {
                    bool negative = plain.StartsWith('(') || plain.StartsWith('-');
                    string digits = plain.Replace(",", "").Trim('(', ')', '-');
                    if (double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && !(digits.Length > 1 && digits[0] == '0' && !digits.StartsWith("0.")))
                    {
                        sb.Append($"<c r=\"{reference}\"{style}><v>{(negative ? -value : value).ToString("R", CultureInfo.InvariantCulture)}</v></c>");
                        continue;
                    }
                }
                sb.Append($"<c r=\"{reference}\"{style} t=\"inlineStr\"><is><t xml:space=\"preserve\">{Escape(text)}</t></is></c>");
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static string ColumnName(int index)
    {
        var sb = new StringBuilder();
        for (index++; index > 0; index = (index - 1) / 26) sb.Insert(0, (char)('A' + (index - 1) % 26));
        return sb.ToString();
    }

    // ---------- shared ----------
    private static void Add(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(text);
        s.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Text for XML: the characters XML can't hold are left out.</summary>
    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') continue;
            if (char.IsSurrogate(c)) { sb.Append(c); continue; }
            if (c == 0xFFFE || c == 0xFFFF) continue;
            sb.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", _ => c.ToString() });
        }
        return sb.ToString();
    }
}
