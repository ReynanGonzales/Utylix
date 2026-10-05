using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace IdmClone.Engine;

/// <summary>A piece of the pages that looks like a watermark (or any other thing repeated on the pages): what it is, where it first shows, how many pages have it.</summary>
public sealed record WatermarkCandidate(string Key, string Kind, string Title, int PagesWith, int PagesLooked, int FirstPage, Rect Box, bool Marked);

/// <summary>
/// Finds and removes watermarks. A watermark that is a piece of its own in the page (a big diagonal text, a logo, a form that every page draws) is found
/// because it is the SAME piece in the same place on page after page, or because the program that made it marked it as a watermark. One that is part of a
/// scanned picture can't be taken out cleanly: it is the picture.
/// </summary>
public static class PdfWatermarks
{
    private const int AnnotWatermark = 24;
    private const int MaxPagesLooked = 80;

    private sealed record Piece(string Key, string Kind, string Title, Rect Box, bool Marked, double Weight);

    private static string Grid(Rect r) => $"{(int)Math.Round(r.X / 6)}:{(int)Math.Round(r.Y / 6)}:{(int)Math.Round(r.Width / 6)}:{(int)Math.Round(r.Height / 6)}";

    private static string Utf16(byte[] buffer, uint length) => length <= 2 ? "" : Encoding.Unicode.GetString(buffer, 0, (int)length - 2);

    /// <summary>True when the program that made the PDF marked this piece as a watermark.</summary>
    private static bool IsMarked(IntPtr obj)
    {
        int n = Pdfium.FPDFPageObj_CountMarks(obj);
        for (uint i = 0; i < n; i++)
        {
            IntPtr mark = Pdfium.FPDFPageObj_GetMark(obj, i);
            if (mark == IntPtr.Zero) continue;
            Pdfium.FPDFPageObjMark_GetName(mark, null, 0, out uint len);
            if (len == 0 || len > 512) continue;
            var buf = new byte[len];
            if (Pdfium.FPDFPageObjMark_GetName(mark, buf, len, out len) == 0) continue;
            string name = Utf16(buf, len);
            if (name.Equals("Watermark", StringComparison.OrdinalIgnoreCase)) return true;
            if (!name.Equals("Artifact", StringComparison.OrdinalIgnoreCase)) continue;
            Pdfium.FPDFPageObjMark_GetParamStringValue(mark, "Subtype", null, 0, out uint vlen);
            if (vlen == 0 || vlen > 512) continue;
            var vb = new byte[vlen];
            if (Pdfium.FPDFPageObjMark_GetParamStringValue(mark, "Subtype", vb, vlen, out vlen) != 0 && Utf16(vb, vlen).Equals("Watermark", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>What a piece of the page is, in a form that is the same on every page that has it. Null for pieces that can't be a watermark.</summary>
    private static Piece? Describe(IntPtr obj, PageMapping map, IntPtr textPage, Size page)
    {
        int type = Pdfium.FPDFPageObj_GetType(obj);
        if (Pdfium.FPDFPageObj_GetBounds(obj, out float l, out float b, out float r, out float t) == 0) return null;
        var box = map.ToShown(l, b, r, t);
        if (box.Width < 8 || box.Height < 8) return null;
        double share = box.Width * box.Height / Math.Max(1, page.Width * page.Height);
        bool marked = IsMarked(obj);
        string g = Grid(box);
        switch (type)
        {
            case Pdfium.ObjText:
            {
                string text = PdfTextRuns.TextOf(obj, textPage).Trim();
                if (text.Length == 0) return null;
                double angle = 0;
                if (Pdfium.FPDFPageObj_GetMatrix(obj, out var m) != 0) angle = Math.Atan2(m.B, m.A) * 180 / Math.PI;
                bool turned = Math.Abs(angle) > 8 && Math.Abs(Math.Abs(angle) - 180) > 8;
                string shown = text.Length > 60 ? text[..60] + "…" : text;
                return new Piece((marked ? "W|T|" : "T|") + text + "|" + g, "text", $"Text “{shown}”", box, marked, (turned ? 2 : 1) + share * 3);
            }
            case Pdfium.ObjImage:
            {
                if (share < 0.02 && !marked) return null;
                uint len = Pdfium.FPDFImageObj_GetImageDataRaw(obj, null, 0);
                string hash = "x";
                if (len > 0 && len < 64 * 1024 * 1024)
                {
                    var data = new byte[len];
                    Pdfium.FPDFImageObj_GetImageDataRaw(obj, data, len);
                    hash = Convert.ToHexString(SHA1.HashData(data.AsSpan(0, (int)Math.Min(len, 65536u)))) + len;
                }
                return new Piece((marked ? "W|I|" : "I|") + hash + "|" + g, "picture", $"Picture, about {box.Width:0} × {box.Height:0} points", box, marked, 1 + share * 3);
            }
            case Pdfium.ObjForm:
            {
                if (share < 0.02 && !marked) return null;
                int inside = Pdfium.FPDFFormObj_CountObjects(obj);
                return new Piece((marked ? "W|F|" : "F|") + inside + "|" + g, "form", "A drawing the pages share (a page overlay)", box, marked, 0.8 + share * 3);
            }
            case Pdfium.ObjPath:
            {
                if (share < 0.25 && !marked) return null;                  // (lines and boxes of the layout are everywhere: only big shapes are looked at)
                return new Piece((marked ? "W|P|" : "P|") + g, "shape", "A big shape", box, marked, 0.5 + share * 3);
            }
            default: return null;
        }
    }

    /// <summary>Looks at the pages (the first 80) for pieces that repeat, and for pieces marked as watermarks.</summary>
    public static List<WatermarkCandidate> Find(PdfFile pdf)
    {
        var seen = new Dictionary<string, (Piece Piece, int Pages, int First)>();
        int looked;
        lock (Pdfium.Sync)
        {
            IntPtr doc = pdf.Handle;
            int count = Pdfium.FPDF_GetPageCount(doc);
            looked = Math.Min(count, MaxPagesLooked);
            for (int i = 0; i < looked; i++)
            {
                IntPtr page = Pdfium.FPDF_LoadPage(doc, i);
                if (page == IntPtr.Zero) continue;
                IntPtr tp = Pdfium.FPDFText_LoadPage(page);
                try
                {
                    Pdfium.FPDF_GetPageSizeByIndexF(doc, i, out var size);
                    var map = new PageMapping(page, size.Width, size.Height);
                    var pageKeys = new HashSet<string>();
                    int n = Pdfium.FPDFPage_CountObjects(page);
                    for (int k = 0; k < n; k++)
                    {
                        IntPtr obj = Pdfium.FPDFPage_GetObject(page, k);
                        if (obj == IntPtr.Zero) continue;
                        var piece = Describe(obj, map, tp, new Size(size.Width, size.Height));
                        if (piece == null || !pageKeys.Add(piece.Key)) continue;
                        seen[piece.Key] = seen.TryGetValue(piece.Key, out var old) ? (old.Piece, old.Pages + 1, old.First) : (piece, 1, i);
                    }
                    int annots = Pdfium.FPDFPage_GetAnnotCount(page);
                    for (int a = 0; a < annots; a++)
                    {
                        IntPtr annot = Pdfium.FPDFPage_GetAnnot(page, a);
                        if (annot == IntPtr.Zero) continue;
                        try
                        {
                            if (Pdfium.FPDFAnnot_GetSubtype(annot) != AnnotWatermark || !pageKeys.Add("A|24")) continue;
                            var piece = new Piece("A|24", "annotation", "A watermark comment (annotation)", new Rect(0, 0, size.Width, size.Height), true, 3);
                            seen[piece.Key] = seen.TryGetValue(piece.Key, out var old) ? (old.Piece, old.Pages + 1, old.First) : (piece, 1, i);
                        }
                        finally { Pdfium.FPDFPage_CloseAnnot(annot); }
                    }
                }
                finally
                {
                    if (tp != IntPtr.Zero) Pdfium.FPDFText_ClosePage(tp);
                    Pdfium.FPDF_ClosePage(page);
                }
            }
        }

        var list = new List<(WatermarkCandidate C, double Score)>();
        foreach (var (key, (piece, pages, first)) in seen)
        {
            bool repeats = looked >= 2 && pages >= 2 && pages >= looked * 0.5;
            bool single = looked == 1 && piece.Weight >= 2.2;                 // (one page: only what looks like a watermark: turned text, a big picture)
            if (!piece.Marked && !repeats && !single) continue;
            list.Add((new WatermarkCandidate(key, piece.Kind, piece.Title, pages, looked, first, piece.Box, piece.Marked), (piece.Marked ? 100 : 0) + pages * 2 + piece.Weight));
        }
        return list.OrderByDescending(x => x.Score).Take(30).Select(x => x.C).ToList();
    }

    /// <summary>Takes the chosen pieces out of every page. Returns how many pieces went. Call under no lock.</summary>
    public static int Remove(PdfFile pdf, IEnumerable<string> keys)
    {
        var wanted = new HashSet<string>(keys);
        int removed = 0;
        lock (Pdfium.Sync)
        {
            IntPtr doc = pdf.Handle;
            int count = Pdfium.FPDF_GetPageCount(doc);
            for (int i = 0; i < count; i++)
            {
                IntPtr page = Pdfium.FPDF_LoadPage(doc, i);
                if (page == IntPtr.Zero) continue;
                IntPtr tp = Pdfium.FPDFText_LoadPage(page);
                bool changed = false;
                try
                {
                    Pdfium.FPDF_GetPageSizeByIndexF(doc, i, out var size);
                    var map = new PageMapping(page, size.Width, size.Height);
                    for (int k = Pdfium.FPDFPage_CountObjects(page) - 1; k >= 0; k--)
                    {
                        IntPtr obj = Pdfium.FPDFPage_GetObject(page, k);
                        if (obj == IntPtr.Zero) continue;
                        var piece = Describe(obj, map, tp, new Size(size.Width, size.Height));
                        if (piece == null || !wanted.Contains(piece.Key)) continue;
                        if (Pdfium.FPDFPage_RemoveObject(page, obj) == 0) continue;
                        Pdfium.FPDFPageObj_Destroy(obj);
                        removed++; changed = true;
                    }
                    if (wanted.Contains("A|24"))
                        for (int a = Pdfium.FPDFPage_GetAnnotCount(page) - 1; a >= 0; a--)
                        {
                            IntPtr annot = Pdfium.FPDFPage_GetAnnot(page, a);
                            if (annot == IntPtr.Zero) continue;
                            bool isWatermark = Pdfium.FPDFAnnot_GetSubtype(annot) == AnnotWatermark;
                            Pdfium.FPDFPage_CloseAnnot(annot);
                            if (isWatermark && Pdfium.FPDFPage_RemoveAnnot(page, a) != 0) { removed++; changed = true; }
                        }
                    if (changed && Pdfium.FPDFPage_GenerateContent(page) == 0) throw new IOException("Page " + (i + 1) + " couldn't be written.");
                }
                finally
                {
                    if (tp != IntPtr.Zero) Pdfium.FPDFText_ClosePage(tp);
                    Pdfium.FPDF_ClosePage(page);
                }
            }
        }
        pdf.ForgetText();
        return removed;
    }
}
