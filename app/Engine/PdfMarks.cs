using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IdmClone.Engine;

public enum PdfFontKind { Sans, Serif, Mono }

/// <summary>Something to add to a page. Coordinates are in points (1/72 inch) from the top-left corner of the page as it is shown.</summary>
public abstract record PdfMark(int Page);

/// <summary>Lines of text. Baseline of line i = Top + i * LineSpacing * Size + Baseline * Size (the font's own numbers, as WPF shows them).</summary>
public sealed record PdfTextMark(int Page, Point TopLeft, string Text, PdfFontKind Font, bool Bold, double Size, Color Color, double LineSpacing, double Baseline, string? FontName = null, double Angle = 0, Point? Pivot = null, bool Invisible = false, double Stretch = 1, bool Watermark = false) : PdfMark(Page);

/// <summary>One figure of a path: a start, then lines (Curve = false, only To) or curves (C1, C2, To).</summary>
public sealed record PdfFigure(Point Start, IReadOnlyList<PdfSegment> Segments, bool Closed);
public readonly record struct PdfSegment(Point C1, Point C2, Point To, bool Curve)
{
    public static PdfSegment Line(Point to) => new(default, default, to, false);
    public static PdfSegment Bezier(Point c1, Point c2, Point to) => new(c1, c2, to, true);
}

/// <summary>Lines and shapes. Stroke / Fill null = none; Multiply = like a highlighter (the text under it stays dark).</summary>
public sealed record PdfPathMark(int Page, IReadOnlyList<PdfFigure> Figures, Color? Stroke, double Width, Color? Fill, bool Multiply) : PdfMark(Page);

/// <summary>A picture in a box: JPEG bytes as they are, or pixels (BGRA, transparency kept).</summary>
public sealed record PdfImageMark(int Page, Rect Box, byte[]? Jpeg, BitmapSource? Pixels, bool Watermark = false) : PdfMark(Page);

/// <summary>
/// A real PDF comment (other readers list it and can remove it): highlight / underline / strike-out over the given text boxes
/// (Subtype 9 / 10 / 12), or a sticky note (Subtype 1, Rects[0] is its icon, Contents its text).
/// </summary>
public sealed record PdfAnnotMark(int Page, int Subtype, IReadOnlyList<Rect> Rects, Color Color, string? Contents) : PdfMark(Page);

/// <summary>Writes marks into the pages of an open PDF (they become part of the page, seen the same in every PDF reader).</summary>
public static class PdfMarkWriter
{
    /// <summary>The pages that had to be made into a picture by the last <see cref="Apply"/> (redaction could not edit them: see <see cref="PdfRedactor"/>), 0-based.</summary>
    public static List<int> FlattenedPages { get; } = new();

    public static void Apply(PdfFile pdf, IEnumerable<PdfMark> marks)
    {
        FlattenedPages.Clear();
        var byPage = marks.GroupBy(m => m.Page).OrderBy(g => g.Key).ToList();
        lock (Pdfium.Sync)
        {
            IntPtr doc = pdf.Handle;
            var fonts = new Dictionary<string, IntPtr>();
            try
            {
                foreach (var group in byPage)
                {
                    IntPtr page = Pdfium.FPDF_LoadPage(doc, group.Key);
                    if (page == IntPtr.Zero) throw new IOException("Page " + (group.Key + 1) + " can't be changed.");
                    try
                    {
                        Pdfium.FPDF_GetPageSizeByIndexF(doc, group.Key, out var size);
                        var map = new PageMapping(page, size.Width, size.Height);
                        var changes = group.OfType<PdfReplaceTextMark>().ToList();
                        if (changes.Count > 0) PdfTextRuns.Replace(doc, page, map, changes, fonts);     // (first: it works with the page's own numbering of its pieces)
                        var redactions = group.OfType<PdfRedactMark>().Select(r => r.Box).ToList();
                        if (redactions.Count > 0 && PdfRedactor.RemoveUnder(doc, page, map, new Size(size.Width, size.Height), redactions, fonts)) FlattenedPages.Add(group.Key);      // (then what is under the black boxes goes, before anything new is drawn)
                        foreach (var mark in group)
                        {
                            switch (mark)
                            {
                                case PdfTextMark t: WriteText(doc, page, map, t, fonts); break;
                                case PdfPathMark p: WritePath(page, map, p); break;
                                case PdfImageMark i: WriteImage(doc, page, map, i); break;
                                case PdfAnnotMark a: WriteAnnot(page, map, a); break;
                            }
                        }
                        if (Pdfium.FPDFPage_GenerateContent(page) == 0) throw new IOException("Page " + (group.Key + 1) + " couldn't be written.");
                    }
                    finally { Pdfium.FPDF_ClosePage(page); }
                }
            }
            finally { foreach (var f in fonts.Values) Pdfium.FPDFFont_Close(f); }
        }
        pdf.ForgetText();                                              // (what was read from the pages before is no longer true)
    }

    private static (uint R, uint G, uint B, uint A) Rgba(Color c) => (c.R, c.G, c.B, c.A);

    // ---------- text ----------
    /// <summary>Marks a piece of content as a watermark the way Acrobat does (/Artifact, /Subtype /Watermark), so it can be found and removed later, even on a single page.</summary>
    private static void TagWatermark(IntPtr doc, IntPtr obj)
    {
        IntPtr mark = Pdfium.FPDFPageObj_AddMark(obj, "Artifact");
        if (mark != IntPtr.Zero) Pdfium.FPDFPageObjMark_SetStringParam(doc, obj, mark, "Subtype", "Watermark");
    }

    private static void WriteText(IntPtr doc, IntPtr page, PageMapping map, PdfTextMark t, Dictionary<string, IntPtr> fonts)
    {
        string[] lines = t.Text.Replace("\r\n", "\n").Split('\n');
        IntPtr font = Font(doc, t.Font, t.Bold, !lines.All(StandardFontCanShow), fonts, t.FontName);
        var (r, g, b, a) = Rgba(t.Color);
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            IntPtr obj = Pdfium.FPDFPageObj_CreateTextObj(doc, font, (float)t.Size);
            if (obj == IntPtr.Zero) throw new IOException("The text couldn't be added.");
            Pdfium.FPDFText_SetText(obj, lines[i]);
            Pdfium.FPDFPageObj_SetFillColor(obj, r, g, b, a);
            var shown = new Point(t.TopLeft.X, t.TopLeft.Y + (i * t.LineSpacing + t.Baseline) * t.Size);
            double turn = t.Angle * Math.PI / 180, cos = Math.Cos(turn), sin = Math.Sin(turn);
            if (t.Angle != 0 && t.Pivot is Point pivot)
            {
                double dx = shown.X - pivot.X, dy = shown.Y - pivot.Y;                       // (turned clockwise around the pivot, like a stamp on the page)
                shown = new Point(pivot.X + dx * cos - dy * sin, pivot.Y + dx * sin + dy * cos);
            }
            var baseline = map.ToPage(shown);
            // the text's own right / up directions, turned: shown right = (cos, sin), shown up = (sin, -cos); Stretch widens or narrows the letters
            double k = t.Stretch;
            Pdfium.FPDFPageObj_Transform(obj, (map.Right.X * cos - map.Up.X * sin) * k, (map.Right.Y * cos - map.Up.Y * sin) * k, map.Right.X * sin + map.Up.X * cos, map.Right.Y * sin + map.Up.Y * cos, baseline.X, baseline.Y);
            if (t.Invisible) Pdfium.FPDFTextObj_SetTextRenderMode(obj, Pdfium.TextInvisible);        // (text that is there for searching and copying only)
            if (t.Watermark) TagWatermark(doc, obj);
            Pdfium.FPDFPage_InsertObject(page, obj);
        }
    }

    /// <summary>The 14 standard PDF fonts show only the Windows-1252 letters (enough for English and Filipino, with ñ).</summary>
    private static bool StandardFontCanShow(string s) =>
        s.All(c => (c >= 0x20 && c <= 0x7E) || (c >= 0xA0 && c <= 0xFF) || "€‚ƒ„…†‡ˆ‰Š‹ŒŽ‘’“”•–—˜™š›œžŸ".IndexOf(c) >= 0);

    private static IntPtr Font(IntPtr doc, PdfFontKind kind, bool bold, bool unicode, Dictionary<string, IntPtr> fonts, string? family = null)
    {
        // a font chosen by name: the Windows font is embedded (when it can't be found, the plain kind is used)
        string? file = family == null ? null : PdfFonts.FileFor(family, bold);
        string key = file != null ? "file:" + file : $"{kind}{bold}{unicode}";
        if (fonts.TryGetValue(key, out var f)) return f;
        if (file != null)
        {
            byte[] chosen = File.ReadAllBytes(file);
            f = Pdfium.FPDFText_LoadFont(doc, chosen, (uint)chosen.Length, Pdfium.FontTrueType, 1);
        }
        else if (!unicode)
        {
            string name = kind switch
            {
                PdfFontKind.Serif => bold ? "Times-Bold" : "Times-Roman",
                PdfFontKind.Mono => bold ? "Courier-Bold" : "Courier",
                _ => bold ? "Helvetica-Bold" : "Helvetica",
            };
            f = Pdfium.FPDFText_LoadStandardFont(doc, name);                  // (nothing is embedded: every PDF reader has these)
        }
        else
        {
            // letters the standard fonts lack (₱, Greek, ...): the matching Windows font is embedded
            string plain = kind switch { PdfFontKind.Serif => bold ? "timesbd.ttf" : "times.ttf", PdfFontKind.Mono => bold ? "courbd.ttf" : "cour.ttf", _ => bold ? "arialbd.ttf" : "arial.ttf" };
            byte[] data = File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), plain));
            f = Pdfium.FPDFText_LoadFont(doc, data, (uint)data.Length, Pdfium.FontTrueType, 1);
        }
        if (f == IntPtr.Zero) throw new IOException("The font couldn't be loaded.");
        fonts[key] = f;
        return f;
    }

    // ---------- lines and shapes ----------
    private static void WritePath(IntPtr page, PageMapping map, PdfPathMark p)
    {
        foreach (var fig in p.Figures)
        {
            var s = map.ToPage(fig.Start);
            IntPtr path = Pdfium.FPDFPageObj_CreateNewPath((float)s.X, (float)s.Y);
            if (path == IntPtr.Zero) throw new IOException("A shape couldn't be added.");
            foreach (var seg in fig.Segments)
            {
                var to = map.ToPage(seg.To);
                if (seg.Curve)
                {
                    var c1 = map.ToPage(seg.C1); var c2 = map.ToPage(seg.C2);
                    Pdfium.FPDFPath_BezierTo(path, (float)c1.X, (float)c1.Y, (float)c2.X, (float)c2.Y, (float)to.X, (float)to.Y);
                }
                else Pdfium.FPDFPath_LineTo(path, (float)to.X, (float)to.Y);
            }
            if (fig.Closed) Pdfium.FPDFPath_Close(path);
            if (p.Stroke is Color sc)
            {
                var (r, g, b, a) = Rgba(sc);
                Pdfium.FPDFPageObj_SetStrokeColor(path, r, g, b, a);
                Pdfium.FPDFPageObj_SetStrokeWidth(path, (float)p.Width);
                Pdfium.FPDFPageObj_SetLineJoin(path, 1);                     // round
                Pdfium.FPDFPageObj_SetLineCap(path, 1);
            }
            if (p.Fill is Color fc)
            {
                var (r, g, b, a) = Rgba(fc);
                Pdfium.FPDFPageObj_SetFillColor(path, r, g, b, a);
            }
            Pdfium.FPDFPath_SetDrawMode(path, p.Fill != null ? Pdfium.FillWinding : Pdfium.FillNone, p.Stroke != null ? 1 : 0);
            if (p.Multiply) Pdfium.FPDFPageObj_SetBlendMode(path, "Multiply");
            Pdfium.FPDFPage_InsertObject(page, path);
        }
    }

    // ---------- comments ----------
    private static void WriteAnnot(IntPtr page, PageMapping map, PdfAnnotMark a)
    {
        if (a.Rects.Count == 0) return;
        IntPtr annot = Pdfium.FPDFPage_CreateAnnot(page, a.Subtype);
        if (annot == IntPtr.Zero) throw new IOException("The comment couldn't be added.");
        try
        {
            double l = double.MaxValue, b = double.MaxValue, r = double.MinValue, t = double.MinValue;
            foreach (var box in a.Rects)
            {
                var tl = map.ToPage(box.TopLeft); var tr = map.ToPage(box.TopRight); var bl = map.ToPage(box.BottomLeft); var br = map.ToPage(box.BottomRight);
                foreach (var p in new[] { tl, tr, bl, br }) { l = Math.Min(l, p.X); r = Math.Max(r, p.X); b = Math.Min(b, p.Y); t = Math.Max(t, p.Y); }
                if (a.Subtype != Pdfium.AnnotText)
                {
                    // (the order readers expect: upper left, upper right, lower left, lower right - of the text as read)
                    var q = new Pdfium.QuadF { X1 = (float)tl.X, Y1 = (float)tl.Y, X2 = (float)tr.X, Y2 = (float)tr.Y, X3 = (float)bl.X, Y3 = (float)bl.Y, X4 = (float)br.X, Y4 = (float)br.Y };
                    Pdfium.FPDFAnnot_AppendAttachmentPoints(annot, ref q);
                }
            }
            var rect = new Pdfium.RectF { Left = (float)l, Bottom = (float)b, Right = (float)r, Top = (float)t };
            Pdfium.FPDFAnnot_SetRect(annot, ref rect);
            Pdfium.FPDFAnnot_SetColor(annot, 0, a.Color.R, a.Color.G, a.Color.B, 255);
            if (!string.IsNullOrEmpty(a.Contents)) Pdfium.FPDFAnnot_SetStringValue(annot, "Contents", a.Contents);
            Pdfium.FPDFAnnot_SetStringValue(annot, "M", "D:" + DateTime.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture));
        }
        finally { Pdfium.FPDFPage_CloseAnnot(annot); }
    }

    // ---------- pictures ----------
    private static void WriteImage(IntPtr doc, IntPtr page, PageMapping map, PdfImageMark m)
    {
        IntPtr obj = Pdfium.FPDFPageObj_NewImageObj(doc);
        if (obj == IntPtr.Zero) throw new IOException("The picture couldn't be added.");
        if (m.Jpeg == null && m.Pixels == null) return;
        if (!PdfCombiner.SetPicture(obj, m.Jpeg, m.Pixels)) throw new IOException("The picture couldn't be added.");
        // the picture's square (0..1, upwards) onto its box
        var bottomLeft = map.ToPage(new Point(m.Box.Left, m.Box.Bottom));
        var right = map.Right * m.Box.Width; var up = map.Up * m.Box.Height;
        Pdfium.FPDFImageObj_SetMatrix(obj, right.X, right.Y, up.X, up.Y, bottomLeft.X, bottomLeft.Y);
        if (m.Watermark) TagWatermark(doc, obj);
        Pdfium.FPDFPage_InsertObject(page, obj);
    }
}
