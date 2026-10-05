using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>The bigger tools of the "…" menu of the page tools: OCR, page numbers / watermark, saving as Word or Excel.</summary>
public sealed partial class PdfWindow
{
    // (the menu itself is built in ShowPagesMenu: grouped, with headings)
    private void ExportToOffice()
    {
        if (_pdf != null && _path != null && CommitItems()) new PdfExportDialog(this, _pdf, _path, SelectedPages()).ShowDialog();
    }

    // ---------- page numbers, header / footer, watermark ----------
    /// <param name="focus">"line" (page numbers, header, footer) or "mark" (watermark): which half the dialog starts on; "" = as the dialog starts</param>
    private void AddPageMarks(string focus = "")
    {
        if (_pdf == null || _path == null) return;
        var dialog = new PdfPageMarksDialog(this, _pdf, _path, _current, focus);
        if (dialog.ShowDialog() != true) return;
        var pdf = _pdf;
        string stem = Path.GetFileNameWithoutExtension(_path);
        int first = dialog.FirstPage, last = dialog.LastPage;
        var marks = PdfPageMarks.Build(pdf.PageCount, pdf.PageSize, stem, first, last, dialog.Line, dialog.Mark);
        if (marks.Count == 0) { Toast("Nothing to add (check the pages and the text)"); return; }
        int pages = last - first + 1;
        string what = dialog.Line != null && dialog.Mark != null ? "Text and watermark" : dialog.Mark != null ? "Watermark" : "Text";
        PageOp(p => PdfMarkWriter.Apply(p, marks), new[] { Math.Clamp(_current, 0, pdf.PageCount - 1) },
               $"{what} added to {(pages == 1 ? "page " + (first + 1) : pages == pdf.PageCount ? "all pages" : $"pages {first + 1} to {last + 1}")}. Undo takes it away until you save");
    }

    // ---------- OCR ----------
    /// <summary>Pages without any text (scans, photos) are read by Windows and get invisible text, so Search, select and copy work.</summary>
    private async void OcrPages()
    {
        if (_pdf == null || _path == null) return;
        var pdf = _pdf;
        var empty = await Task.Run(() => Enumerable.Range(0, pdf.PageCount).Where(i => pdf.GetText(i).Text.Trim().Length == 0).ToList());
        if (pdf != _pdf) return;
        if (empty.Count == 0) { Toast("Every page already has text: nothing to read"); return; }

        var engine = PdfOcr.CreateEngine(out string problem);
        if (engine == null) { UMessage.Show(this, problem, "Make searchable", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var answer = UMessage.Ask(this,
            $"{empty.Count} of {pdf.PageCount} page{(pdf.PageCount == 1 ? "" : "s")} {(empty.Count == 1 ? "has" : "have")} no text (a scan or a photo).\n\nWindows will read the words from {(empty.Count == 1 ? "it" : "them")} on this PC (nothing is sent anywhere). It takes a few seconds a page. The pictures stay as they are; the words are added as invisible text, so Search, select and copy work.\n\nYou can Undo it until you save.",
            "Make searchable", MessageBoxImage.Question, MessageBoxResult.Yes, MessageBoxResult.Cancel, ("Make searchable", MessageBoxResult.Yes), ("Cancel", MessageBoxResult.Cancel));
        if (answer != MessageBoxResult.Yes) return;

        var found = new Dictionary<int, (List<OcrWord> Words, (int Width, int Height) Pixels)>();
        bool finished;
        try
        {
            finished = await BusyDialog.RunAsync(this, "Making pages searchable", "Starting…", async (report, cancel) =>
            {
                for (int k = 0; k < empty.Count; k++)
                {
                    cancel.ThrowIfCancellationRequested();
                    int page = empty[k];
                    report(k, empty.Count, $"Reading page {page + 1}  ({k + 1} of {empty.Count})…");
                    var pixels = PdfOcr.PixelSize(pdf.PageSize(page));
                    var picture = await Task.Run(() => pdf.Render(page, pixels.Width, pixels.Height, 0, forScreen: false));
                    found[page] = (await PdfOcr.ReadAsync(engine, picture), pixels);
                }
                report(empty.Count, empty.Count, "");
            });
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or IOException or OutOfMemoryException or ObjectDisposedException)
        {
            UMessage.Show(this, "Couldn't read the pages: " + e.Message, "Make searchable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!finished) { Toast("Stopped: nothing was changed"); return; }

        var marks = found.SelectMany(kv => PdfOcr.Marks(kv.Key, pdf.PageSize(kv.Key), kv.Value.Pixels, kv.Value.Words)).Cast<PdfMark>().ToList();
        int words = found.Values.Sum(v => v.Words.Count);
        if (marks.Count == 0) { Toast("No words were found. Is the page upright and sharp enough to read?"); return; }
        int pagesWithWords = found.Count(kv => kv.Value.Words.Count > 0);
        PageOp(p => PdfMarkWriter.Apply(p, marks), new[] { Math.Clamp(_current, 0, pdf.PageCount - 1) },
               $"{pagesWithWords} page{(pagesWithWords == 1 ? "" : "s")} made searchable ({words} words found). Try Ctrl+F; Undo goes back until you save");
    }
}
