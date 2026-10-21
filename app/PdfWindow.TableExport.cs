using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// Convert tab > "Table to Excel": drag a box round a table (or any part of a page) and the words inside it are saved as an Excel sheet, one row for each line and one
/// column for each column of words. A page without text (a scan) is read with Windows' text reader first. It is the same engine as To Excel, on the part you chose.
/// </summary>
public sealed partial class PdfWindow
{
    private bool _tableBusy;

    private async void TableToExcel(PageView pv, Rect box)
    {
        _toolButtons[EditTool.Select].IsChecked = true;
        if (_pdf == null || _path == null || _tableBusy) return;
        if (box.Width < 12 || box.Height < 12) { Toast("Drag a box round the table"); return; }
        var pdf = _pdf; int page = pv.Index; string source = _path;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the table as an Excel workbook", Filter = "Excel workbook|*.xlsx", OverwritePrompt = true,
            FileName = Path.GetFileNameWithoutExtension(source) + " (table, page " + (page + 1) + ").xlsx", InitialDirectory = Path.GetDirectoryName(source),
        };
        if (dlg.ShowDialog(this) != true) return;
        string target = dlg.FileName;

        _tableBusy = true;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            Toast("Reading the table…");
            List<OcrWord>? words = null; (int Width, int Height) pixels = (0, 0);
            bool hasText = await Task.Run(() => pdf.GetAllTextRuns(page).Count > 0);
            if (!hasText)
            {
                var engine = PdfOcr.CreateEngine(out string problem);
                if (engine == null) { Toast(problem); return; }
                pixels = PdfOcr.PixelSize(pdf.PageSize(page));
                var px = pixels;
                var picture = await Task.Run(() => pdf.Render(page, px.Width, px.Height, 0, forScreen: false));
                words = await PdfOcr.ReadAsync(engine, picture);
            }
            var ocr = words; var pix = pixels;
            var exported = await Task.Run(() => PdfExport.Read(pdf, page, ocr, pix));
            var inside = exported.Runs.Where(r => Overlap(r.Box, box) >= 0.5).ToList();
            if (inside.Count == 0) { Toast(hasText ? "No words inside that box" : "No words could be read inside that box"); return; }
            byte[] bytes = await Task.Run(() => PdfExport.ToXlsx(new[] { new ExportPage(page, exported.Size, inside, new()) }, oneSheet: true));
            await Task.Run(() => File.WriteAllBytes(target, bytes));
            var answer = UMessage.Ask(this, $"Saved {Path.GetFileName(target)} ({inside.Count} pieces of text from page {page + 1}).", "Table to Excel", MessageBoxImage.Information, MessageBoxResult.Yes, MessageBoxResult.No,
                                      ("Open it", MessageBoxResult.Yes), ("Close", MessageBoxResult.No));
            if (answer == MessageBoxResult.Yes) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OutOfMemoryException or ObjectDisposedException or System.Runtime.InteropServices.COMException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Toast("Couldn't save the table: " + e.Message);
        }
        finally { Mouse.OverrideCursor = null; _tableBusy = false; }
    }

    /// <summary>How much of <paramref name="piece"/> lies inside <paramref name="box"/> (0 .. 1).</summary>
    private static double Overlap(Rect piece, Rect box)
    {
        var both = Rect.Intersect(piece, box);
        if (both.IsEmpty || piece.Width <= 0 || piece.Height <= 0) return 0;
        return both.Width * both.Height / (piece.Width * piece.Height);
    }
}
