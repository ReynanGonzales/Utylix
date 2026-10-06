using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace IdmClone.Engine;

public enum PdfNewFieldKind { Text, CheckBox }

/// <summary>A new fillable field to put on a page. Box is in points from the top-left of the page as shown.</summary>
public sealed record PdfFieldMark(int Page, Rect Box, PdfNewFieldKind Kind, string Name, double FontSize) : PdfMark(Page);

/// <summary>
/// Makes real, fillable form fields (text boxes and check boxes) in a PDF: PDFium can't create them, so they are written into the saved bytes with PDFsharp
/// (widget annotations + the document's /AcroForm). They then fill in and print like the fields of any other form, in any PDF reader.
/// </summary>
public static class PdfFormFields
{
    /// <summary>The fields' boxes on the page's own coordinates (left, bottom, right, top), asked from the open document (it knows each page's turn and crop box).</summary>
    private static List<(double L, double B, double R, double T)> ToPage(PdfFile pdf, IReadOnlyList<PdfFieldMark> fields)
    {
        var ordered = new (double L, double B, double R, double T)[fields.Count];
        lock (Pdfium.Sync)
        {
            foreach (var group in fields.Select((f, i) => (f, i)).GroupBy(x => x.f.Page))
            {
                IntPtr page = Pdfium.FPDF_LoadPage(pdf.Handle, group.Key);
                if (page == IntPtr.Zero) throw new IOException("Page " + (group.Key + 1) + " can't be changed.");
                try
                {
                    Pdfium.FPDF_GetPageSizeByIndexF(pdf.Handle, group.Key, out var size);
                    var map = new PageMapping(page, size.Width, size.Height);
                    foreach (var (f, i) in group)
                    {
                        var a = map.ToPage(f.Box.TopLeft); var b = map.ToPage(f.Box.BottomRight);
                        ordered[i] = (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
                    }
                }
                finally { Pdfium.FPDF_ClosePage(page); }
            }
        }
        return ordered.ToList();
    }

    /// <summary>The PDF (as saved by PDFium) with the new fields in it.</summary>
    public static byte[] Add(PdfFile pdf, byte[] bytes, IReadOnlyList<PdfFieldMark> fields)
    {
        var boxes = ToPage(pdf, fields);
        using var input = new MemoryStream(bytes);
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(input, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);

        // the document's form: made if there is none, and told to draw fields by itself where an appearance is missing
        var catalog = doc.Internals.Catalog;
        PdfDictionary acro;
        if (Resolve(catalog.Elements["/AcroForm"]) is PdfDictionary existing) acro = existing;
        else
        {
            acro = new PdfDictionary(doc);
            doc.Internals.AddObject(acro);
            catalog.Elements["/AcroForm"] = acro.Reference;
        }
        var all = Resolve(acro.Elements["/Fields"]) as PdfArray;
        if (all == null) { all = new PdfArray(doc); acro.Elements["/Fields"] = all; }
        EnsureFont(doc, acro);
        acro.Elements.SetBoolean("/NeedAppearances", true);
        if (!acro.Elements.ContainsKey("/DA")) acro.Elements.SetString("/DA", "/Helv 0 Tf 0 g");

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in all.Elements) if (Resolve(item) is PdfDictionary d && d.Elements.GetString("/T") is { Length: > 0 } t) used.Add(t);

        for (int i = 0; i < fields.Count; i++)
        {
            var f = fields[i]; var (l, b, r, t) = boxes[i];
            double w = Math.Max(4, r - l), h = Math.Max(4, t - b);
            string name = f.Name; int n = 2;
            while (!used.Add(name)) name = f.Name + " (" + n++ + ")";

            var widget = new PdfDictionary(doc);
            widget.Elements.SetName("/Type", "/Annot");
            widget.Elements.SetName("/Subtype", "/Widget");
            widget.Elements["/Rect"] = Box(doc, l, b, l + w, b + h);
            widget.Elements.SetInteger("/F", 4);                                              // (printed)
            widget.Elements.SetString("/T", name);
            var mk = new PdfDictionary(doc);
            mk.Elements["/BC"] = new PdfArray(doc, new PdfReal(0.35), new PdfReal(0.45), new PdfReal(0.65));
            mk.Elements["/BG"] = new PdfArray(doc, new PdfReal(0.94), new PdfReal(0.96), new PdfReal(1));
            widget.Elements["/MK"] = mk;

            var normal = new PdfDictionary(doc);
            if (f.Kind == PdfNewFieldKind.Text)
            {
                widget.Elements.SetName("/FT", "/Tx");
                double size = Math.Clamp(f.FontSize, 4, Math.Max(4, h * 0.75));
                widget.Elements.SetString("/DA", "/Helv " + Num(size) + " Tf 0 g");
                if (h > size * 2.4) widget.Elements.SetInteger("/Ff", 1 << 12);               // (a tall box: several lines)
                var ap = new PdfDictionary(doc);
                ap.Elements["/N"] = Form(doc, w, h, Frame(w, h, "") );
                widget.Elements["/AP"] = ap;
            }
            else
            {
                widget.Elements.SetName("/FT", "/Btn");
                widget.Elements.SetName("/V", "/Off");
                widget.Elements.SetName("/AS", "/Off");
                mk.Elements.SetString("/CA", "4");
                double s = Math.Min(w, h);
                string tick = $"0 g 0 G {Num(s * 0.12)} w 1 J 1 j {Num(w * 0.2)} {Num(h * 0.5)} m {Num(w * 0.42)} {Num(h * 0.24)} l {Num(w * 0.8)} {Num(h * 0.78)} l S\n";
                normal.Elements["/Yes"] = Form(doc, w, h, Frame(w, h, tick));
                normal.Elements["/Off"] = Form(doc, w, h, Frame(w, h, ""));
                var ap = new PdfDictionary(doc);
                ap.Elements["/N"] = normal;
                widget.Elements["/AP"] = ap;
            }

            doc.Internals.AddObject(widget);
            var page = doc.Pages[f.Page];
            widget.Elements["/P"] = page.Reference;
            var annots = Resolve(page.Elements["/Annots"]) as PdfArray;
            if (annots == null) { annots = new PdfArray(doc); page.Elements["/Annots"] = annots; }
            annots.Elements.Add(widget.Reference!);
            all.Elements.Add(widget.Reference!);
        }

        using var output = new MemoryStream();
        doc.Save(output, false);
        return output.ToArray();
    }

    private static PdfArray Box(PdfDocument doc, double l, double b, double r, double t) => new(doc, new PdfReal(l), new PdfReal(b), new PdfReal(r), new PdfReal(t));

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

    /// <summary>Helvetica as /Helv in the form's default resources (what the fields' /DA refers to).</summary>
    private static void EnsureFont(PdfDocument doc, PdfDictionary acro)
    {
        var dr = Resolve(acro.Elements["/DR"]) as PdfDictionary;
        if (dr == null) { dr = new PdfDictionary(doc); acro.Elements["/DR"] = dr; }
        var fonts = Resolve(dr.Elements["/Font"]) as PdfDictionary;
        if (fonts == null) { fonts = new PdfDictionary(doc); dr.Elements["/Font"] = fonts; }
        if (fonts.Elements.ContainsKey("/Helv")) return;
        var helv = new PdfDictionary(doc);
        helv.Elements.SetName("/Type", "/Font");
        helv.Elements.SetName("/Subtype", "/Type1");
        helv.Elements.SetName("/BaseFont", "/Helvetica");
        helv.Elements.SetName("/Encoding", "/WinAnsiEncoding");
        doc.Internals.AddObject(helv);
        fonts.Elements["/Helv"] = helv.Reference;
    }

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A pale blue box with a dark blue edge (and anything else drawn on top), as an appearance stream.</summary>
    private static string Frame(double w, double h, string extra) =>
        $"q 0.94 0.96 1 rg 0 0 {Num(w)} {Num(h)} re f 0.35 0.45 0.65 RG 0.75 w 0.375 0.375 {Num(w - 0.75)} {Num(h - 0.75)} re S Q\n{extra}";

    private static PdfReference Form(PdfDocument doc, double w, double h, string content)
    {
        var form = new PdfDictionary(doc);
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements["/BBox"] = Box(doc, 0, 0, w, h);
        form.CreateStream(Encoding.ASCII.GetBytes(content));
        doc.Internals.AddObject(form);
        return form.Reference!;
    }
}
