using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// "Edit a table": drag a box round a table that is already in the PDF. Its words are read with where they sit, grouped into rows (by their height on the page) and columns
/// (by the gaps between the words), and made into a real table (the same as the Table tool makes). The old words are taken out and the old lines covered with white, so only
/// the new table is seen; Undo gives the page back as it was.
/// </summary>
public sealed partial class PdfWindow
{
    private void MakeTableEditable(PageView pv, Rect box)
    {
        _toolButtons[EditTool.Select].IsChecked = true;
        if (_pdf == null || box.Width < 20 || box.Height < 14) { Toast("Drag a box round the whole table"); return; }
        var edited = EditedRunIndices(pv.Index);
        var runs = Runs(pv.Index).Where(r => !edited.Contains(r.Index) && Overlap(r.Box, box) >= 0.5 && r.Text.Trim().Length > 0).ToList();
        if (runs.Count < 2) { Toast(Text(pv.Index)?.Text.Length > 0 ? "No words of a table inside that box" : "This page has no text to read: it is probably a scanned picture"); return; }

        // rows: words whose middles are at about the same height
        double median = runs.Select(r => r.Box.Height).OrderBy(h => h).ElementAt(runs.Count / 2);
        var rows = new List<List<PdfTextRun>>();
        double rowMid = 0;
        foreach (var run in runs.OrderBy(r => r.Box.Top + r.Box.Height / 2))
        {
            double mid = run.Box.Top + run.Box.Height / 2;
            if (rows.Count == 0 || mid - rowMid > median * 0.6) { rows.Add(new List<PdfTextRun> { run }); rowMid = mid; }
            else { rows[^1].Add(run); rowMid = rows[^1].Average(r => r.Box.Top + r.Box.Height / 2); }
        }

        // columns: stretches of the page's width that have words in them; a gap wider than a word space starts the next column
        var bands = new List<(double L, double R)>();
        foreach (var (l, r) in runs.Select(x => (x.Box.Left, x.Box.Right)).OrderBy(x => x.Left))
        {
            if (bands.Count > 0 && l <= bands[^1].R + median * 0.7) bands[^1] = (bands[^1].L, Math.Max(bands[^1].R, r));
            else bands.Add((l, r));
        }
        if (bands.Count < 1) return;
        // the table is as big as its words (a little round them), not as big as the box that was dragged: it then sits where the old table was
        var content = runs.Aggregate(Rect.Empty, (acc, r) => { acc.Union(r.Box); return acc; });
        content.Inflate(TableItem.Pad + 1, TableItem.Pad + 1);
        var area = Rect.Intersect(box, content);
        if (area.IsEmpty) area = box;
        var whiteOut = box;
        box = area;
        int BandOf(PdfTextRun run) { double mid = run.Box.Left + run.Box.Width / 2; int i = bands.FindIndex(b => mid >= b.L - 0.5 && mid <= b.R + 0.5); return i < 0 ? 0 : i; }

        var table = TableItem.Create(pv.Index, rows.Count, bands.Count, Colors.Black, _tableLineWidth);
        table.Origin = box.TopLeft;
        table.FontSize = Math.Clamp(runs.Select(r => r.Size).OrderBy(s => s).ElementAt(runs.Count / 2), 6, 28);
        for (int c = 0; c < bands.Count; c++)
        {
            double left = c == 0 ? box.Left : (bands[c - 1].R + bands[c].L) / 2;
            double right = c == bands.Count - 1 ? box.Right : (bands[c].R + bands[c + 1].L) / 2;
            table.ColW[c] = Math.Max(TableItem.MinCol, right - left);
        }
        for (int r = 0; r < rows.Count; r++)
        {
            double top = r == 0 ? box.Top : (rows[r - 1].Max(x => x.Box.Bottom) + rows[r].Min(x => x.Box.Top)) / 2;
            double bottom = r == rows.Count - 1 ? box.Bottom : (rows[r].Max(x => x.Box.Bottom) + rows[r + 1].Min(x => x.Box.Top)) / 2;
            table.RowH[r] = Math.Max(10, bottom - top);
            foreach (var group in rows[r].GroupBy(BandOf))
            {
                var ordered = group.OrderBy(x => x.Box.Left).ToList();
                var cell = table.Cells[r][group.Key];
                cell.Text = string.Join(" ", ordered.Select(x => x.Text.Trim()));
                cell.Bold = ordered.All(x => x.Bold);
                bool numbers = cell.Text.Length > 0 && cell.Text.All(ch => char.IsDigit(ch) || ch is '.' or ',' or '-' or '%' or ' ');
                if (numbers && rows.Count > 1) cell.Align = 2;                              // (figures sit at the right of their column)
            }
        }
        if (rows.Count > 1 && Enumerable.Range(0, bands.Count).All(c => table.Cells[0][c].Text.Length == 0 || table.Cells[0][c].Bold)) table.Header = true;

        Snapshot();
        var removed = new List<RunEditItem>();
        foreach (var run in runs)                                                           // (the old words come out of the page)
        {
            var item = new RunEditItem { Page = pv.Index, Run = run, NewText = "", Cover = CoverColour(pv, run.Box) };
            removed.Add(item);
            _items.Add(item);
        }
        AttachRunCovers(pv.Index, removed);
        var pad = new Vector(1, 1);                                                         // (and the old lines are covered, a little beyond the box)
        _items.Add(new ShapeItem { Page = pv.Index, Kind = ShapeKind.WhiteOut, A = whiteOut.TopLeft - pad, B = whiteOut.BottomRight + pad, Color = Colors.White, Width = 1 });
        _items.Add(table);
        table.FitRows();
        _selected = null;
        Select(table);
        table.SetSel(0, 0);
        RenderItems(pv.Index);
        UpdateEditButtons();
        Toast($"Table read: {rows.Count} rows, {bands.Count} columns. Click a cell to change it; Undo gives the old table back");
    }
}
