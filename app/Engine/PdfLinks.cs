using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace IdmClone.Engine;

/// <summary>Making and removing links (clickable boxes that open a web address or jump to a page). PDFium can't make them, so they are written with PDFsharp.</summary>
public sealed partial class PdfFile
{
    /// <summary>Puts a link on each box (points, as seen on the page): to <paramref name="uri"/>, or (uri empty) to page <paramref name="targetPage"/> (0-based). A change of the open document.</summary>
    public void AddLinks(int pageIndex, IReadOnlyList<Rect> shownBoxes, string? uri, int targetPage)
    {
        var rects = new List<(double L, double B, double R, double T)>();
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            Pdfium.FPDF_GetPageSizeByIndexF(_doc, pageIndex, out var size);
            IntPtr page = LoadPageOrThrow(pageIndex);
            try
            {
                var map = new PageMapping(page, size.Width, size.Height);
                foreach (var box in shownBoxes)
                {
                    var a = map.ToPage(box.TopLeft); var b = map.ToPage(box.BottomRight);
                    rects.Add((Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
                }
            }
            finally { Pdfium.FPDF_ClosePage(page); }
        }
        byte[] bytes = SaveToBytes();
        Restore(PdfLinkWriter.Add(bytes, pageIndex, rects, uri, targetPage));
    }

    /// <summary>Takes away the link whose box is (nearly) this one. False when there is none.</summary>
    public bool RemoveLink(int pageIndex, Rect shownBox)
    {
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            Pdfium.FPDF_GetPageSizeByIndexF(_doc, pageIndex, out var size);
            IntPtr page = LoadPageOrThrow(pageIndex);
            try
            {
                var map = new PageMapping(page, size.Width, size.Height);
                int count = Pdfium.FPDFPage_GetAnnotCount(page);
                for (int i = 0; i < count; i++)
                {
                    IntPtr annot = Pdfium.FPDFPage_GetAnnot(page, i);
                    if (annot == IntPtr.Zero) continue;
                    bool match = false;
                    try
                    {
                        if (Pdfium.FPDFAnnot_GetSubtype(annot) != 2 || Pdfium.FPDFAnnot_GetRect(annot, out var r) == 0) continue;
                        var shown = map.ToShown(r.Left, r.Bottom, r.Right, r.Top);
                        match = Math.Abs(shown.X - shownBox.X) < 1 && Math.Abs(shown.Y - shownBox.Y) < 1 && Math.Abs(shown.Width - shownBox.Width) < 1 && Math.Abs(shown.Height - shownBox.Height) < 1;
                    }
                    finally { Pdfium.FPDFPage_CloseAnnot(annot); }
                    if (match) { Pdfium.FPDFPage_RemoveAnnot(page, i); _texts.Remove(pageIndex); return true; }
                }
                return false;
            }
            finally { Pdfium.FPDF_ClosePage(page); }
        }
    }
}

internal static class PdfLinkWriter
{
    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

    public static byte[] Add(byte[] bytes, int pageIndex, IReadOnlyList<(double L, double B, double R, double T)> rects, string? uri, int targetPage)
    {
        using var input = new MemoryStream(bytes);
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(input, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        var page = doc.Pages[pageIndex];
        var annots = Resolve(page.Elements["/Annots"]) as PdfArray;
        if (annots == null) { annots = new PdfArray(doc); page.Elements["/Annots"] = annots; }
        foreach (var (l, b, r, t) in rects)
        {
            var link = new PdfDictionary(doc);
            link.Elements.SetName("/Type", "/Annot");
            link.Elements.SetName("/Subtype", "/Link");
            link.Elements["/Rect"] = new PdfArray(doc, new PdfReal(l), new PdfReal(b), new PdfReal(r), new PdfReal(t));
            link.Elements["/Border"] = new PdfArray(doc, new PdfInteger(0), new PdfInteger(0), new PdfInteger(0));      // (no frame round the link)
            link.Elements.SetInteger("/F", 4);
            if (!string.IsNullOrEmpty(uri))
            {
                var action = new PdfDictionary(doc);
                action.Elements.SetName("/S", "/URI");
                action.Elements.SetString("/URI", uri);
                link.Elements["/A"] = action;
            }
            else
            {
                var action = new PdfDictionary(doc);
                action.Elements.SetName("/S", "/GoTo");
                var dest = new PdfArray(doc);
                dest.Elements.Add(doc.Pages[Math.Clamp(targetPage, 0, doc.PageCount - 1)].Reference!);
                dest.Elements.Add(new PdfName("/Fit"));
                action.Elements["/D"] = dest;
                link.Elements["/A"] = action;
            }
            doc.Internals.AddObject(link);
            link.Elements["/P"] = page.Reference;
            annots.Elements.Add(link.Reference!);
        }
        using var output = new MemoryStream();
        doc.Save(output, false);
        return output.ToArray();
    }
}
