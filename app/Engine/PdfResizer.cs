using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace IdmClone.Engine;

/// <summary>A paper size, in points (1/72 inch), portrait.</summary>
public readonly record struct PaperSize(string Name, double Width, double Height)
{
    public string Millimetres => $"{Width / 72 * 25.4:0.#} × {Height / 72 * 25.4:0.#} mm";
}

/// <summary>
/// "Page size": every page of a PDF is put on a sheet of another size (A4, Letter, Long ...), shrunk or enlarged to fit and centred, never
/// stretched. Each page of the original becomes a placed, scaled copy on a new sheet, so text stays text and drawings stay sharp. Pages keep
/// their own direction (a wide page goes on a wide sheet) unless told otherwise. Links, form fields and comments of the original do not
/// come along (they belong to the old pages): the caller warns when there are any. Everything runs on this PC.
/// </summary>
public static class PdfResizer
{
    public static readonly PaperSize[] Papers =
    {
        new("A4", 595.28, 841.89),
        new("Letter (8.5 × 11 in)", 612, 792),
        new("Long / Folio (8.5 × 13 in)", 612, 936),
        new("Legal (8.5 × 14 in)", 612, 1008),
        new("A5", 419.53, 595.28),
        new("A3", 841.89, 1190.55),
        new("Tabloid (11 × 17 in)", 792, 1224),
    };

    /// <summary>Does the PDF have form fields (they are lost when the pages are put on new sheets)?</summary>
    public static bool HasForm(PdfFile pdf) { lock (Pdfium.Sync) return Pdfium.FPDF_GetFormType(pdf.Handle) != 0; }

    /// <summary>
    /// Makes the resized copy. <paramref name="keepDirection"/>: wide pages go on a wide sheet and tall pages on a tall one;
    /// otherwise every page goes on the sheet exactly as given (<paramref name="width"/> x <paramref name="height"/>, points).
    /// </summary>
    public static byte[] Resize(PdfFile pdf, double width, double height, bool keepDirection, IProgress<(int Done, int Total)>? progress, CancellationToken cancel)
    {
        if (pdf.IsProtected) throw new PdfProtectedException("This PDF is protected with a password, so a copy with other page sizes can't be made.");
        int count = pdf.PageCount;
        if (count == 0) throw new IOException("This PDF has no pages.");
        double shortSide = Math.Min(width, height), longSide = Math.Max(width, height);
        IntPtr target;
        lock (Pdfium.Sync) target = Pdfium.FPDF_CreateNewDocument();
        if (target == IntPtr.Zero) throw new IOException("Couldn't start the new PDF.");
        try
        {
            for (int i = 0; i < count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Report((i, count));
                var shown = pdf.PageSize(i);                                           // as the page looks (turned the way the PDF says)
                double w, h;
                if (keepDirection) (w, h) = shown.Width > shown.Height * 1.0001 ? (longSide, shortSide) : (shortSide, longSide);
                else (w, h) = (width, height);
                lock (Pdfium.Sync) PlacePage(target, pdf.Handle, i, shown.Width, shown.Height, w, h);
            }
            progress?.Report((count, count));
            using var ms = new MemoryStream();
            lock (Pdfium.Sync) if (!Pdfium.Save(target, ms)) throw new IOException("The PDF couldn't be written.");
            return ms.ToArray();
        }
        finally { lock (Pdfium.Sync) Pdfium.FPDF_CloseDocument(target); }
    }

    /// <summary>
    /// One page of the source (<paramref name="dw"/> x <paramref name="dh"/> points as it looks), scaled to fit and centred on a new sheet
    /// of w x h points. PDFium's copy of a page already has the page's box and turn built in, so only a scale and a shift are needed.
    /// </summary>
    private static void PlacePage(IntPtr target, IntPtr source, int index, double dw, double dh, double w, double h)
    {
        double s = Math.Min(w / dw, h / dh), ox = (w - dw * s) / 2, oy = (h - dh * s) / 2;
        IntPtr xobject = Pdfium.FPDF_NewXObjectFromPage(target, source, index);
        if (xobject == IntPtr.Zero) throw new IOException($"Page {index + 1} couldn't be copied.");
        IntPtr dest = Pdfium.FPDFPage_New(target, Pdfium.FPDF_GetPageCount(target), w, h);
        if (dest == IntPtr.Zero) { Pdfium.FPDF_CloseXObject(xobject); throw new IOException("Couldn't add a page."); }
        try
        {
            IntPtr form = Pdfium.FPDF_NewFormObjectFromXObject(xobject);
            if (form == IntPtr.Zero) throw new IOException($"Page {index + 1} couldn't be placed.");
            Pdfium.FPDFPageObj_Transform(form, s, 0, 0, s, ox, oy);
            Pdfium.FPDFPage_InsertObject(dest, form);
            Pdfium.FPDFPage_GenerateContent(dest);
        }
        finally { Pdfium.FPDF_ClosePage(dest); Pdfium.FPDF_CloseXObject(xobject); }
    }
}
