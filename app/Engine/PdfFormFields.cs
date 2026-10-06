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

public enum PdfNewFieldKind { Text, CheckBox, Radio, Signature, Dropdown }

/// <summary>What can be set on a field besides its look: must be filled in, several lines, longest text, starting text, the choices of a drop-down list, ticked at the start.</summary>
public sealed record PdfFieldExtra(bool Required = false, bool Multiline = false, int MaxLength = 0, string DefaultText = "", IReadOnlyList<string>? Choices = null, bool Ticked = false);

/// <summary>A new fillable field to put on a page. Box is in points from the top-left of the page as shown. A radio button's Name is its GROUP (one choice out of the group) and Value is what this button stands for.</summary>
public sealed record PdfFieldMark(int Page, Rect Box, PdfNewFieldKind Kind, string Name, double FontSize, System.Windows.Media.Color Color, PdfFontKind Font = PdfFontKind.Sans, bool Bold = false, string Value = "", PdfFieldExtra? Extra = null) : PdfMark(Page);

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
        else if (fields.Count == 0) return bytes;                        // (nothing to add and no form to tidy)
        else
        {
            acro = new PdfDictionary(doc);
            doc.Internals.AddObject(acro);
            catalog.Elements["/AcroForm"] = acro.Reference;
        }
        var all = Resolve(acro.Elements["/Fields"]) as PdfArray;
        if (all == null) { all = new PdfArray(doc); acro.Elements["/Fields"] = all; }
        // fields whose box was taken off its page (a saved field picked up again to move or delete it) are not in the form any more
        var live = new HashSet<PdfObjectID>();
        foreach (var pg in doc.Pages)
            if (Resolve(pg.Elements["/Annots"]) is PdfArray onPage) foreach (var it in onPage.Elements) if (it is PdfReference rr) live.Add(rr.ObjectID);
        for (int k = all.Elements.Count - 1; k >= 0; k--)
        {
            if (all.Elements[k] is not PdfReference fr || fr.Value is not PdfDictionary fd) continue;
            if (fd.Elements.GetName("/Subtype") == "/Widget" && !fd.Elements.ContainsKey("/Kids") && !live.Contains(fr.ObjectID)) { all.Elements.RemoveAt(k); continue; }
            // a group of round buttons: the buttons that left the page (picked up again) go from its list, and an empty group goes
            if (Resolve(fd.Elements["/Kids"]) is PdfArray kids && fd.Elements.GetName("/FT") == "/Btn")
            {
                for (int q = kids.Elements.Count - 1; q >= 0; q--)
                    if (kids.Elements[q] is PdfReference kr && kr.Value is PdfDictionary kd && kd.Elements.GetName("/Subtype") == "/Widget" && !live.Contains(kr.ObjectID)) kids.Elements.RemoveAt(q);
                if (kids.Elements.Count == 0) all.Elements.RemoveAt(k);
            }
        }
        if (fields.Count == 0) { using var tidied = new MemoryStream(); doc.Save(tidied, false); return tidied.ToArray(); }
        EnsureFont(doc, acro);
        acro.Elements.SetBoolean("/NeedAppearances", true);
        if (!acro.Elements.ContainsKey("/DA")) acro.Elements.SetString("/DA", "/Helv 0 Tf 0 g");

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in all.Elements) if (Resolve(item) is PdfDictionary d && d.Elements.GetString("/T") is { Length: > 0 } t) used.Add(t);

        // a group of round buttons is ONE field (its parent) whose widgets are the buttons; an existing group of that name is added to
        var radioParents = new Dictionary<string, PdfDictionary>();
        PdfDictionary RadioParent(string group)
        {
            if (radioParents.TryGetValue(group, out var known)) return known;
            foreach (var item in all.Elements)
                if (Resolve(item) is PdfDictionary d && d.Elements.GetString("/T") == group && d.Elements.GetName("/FT") == "/Btn" && (d.Elements.GetInteger("/Ff") & (1 << 15)) != 0)
                {
                    if (Resolve(d.Elements["/Kids"]) is not PdfArray) d.Elements["/Kids"] = new PdfArray(doc);
                    return radioParents[group] = d;
                }
            var parent = new PdfDictionary(doc);
            parent.Elements.SetName("/FT", "/Btn");
            parent.Elements.SetInteger("/Ff", (1 << 15) | (1 << 14));                          // (round buttons; one of them has to stay chosen once chosen)
            parent.Elements.SetString("/T", group);
            parent.Elements.SetName("/V", "/Off");
            parent.Elements["/Kids"] = new PdfArray(doc);
            doc.Internals.AddObject(parent);
            all.Elements.Add(parent.Reference!);
            used.Add(group);
            return radioParents[group] = parent;
        }

        for (int i = 0; i < fields.Count; i++)
        {
            var f = fields[i]; var (l, b, r, t) = boxes[i];
            double w = Math.Max(4, r - l), h = Math.Max(4, t - b);
            string name = f.Name;
            if (f.Kind != PdfNewFieldKind.Radio) { int n = 2; while (!used.Add(name)) name = f.Name + " (" + n++ + ")"; }       // (round buttons of one group share the group's name on purpose)

            var widget = new PdfDictionary(doc);
            widget.Elements.SetName("/Type", "/Annot");
            widget.Elements.SetName("/Subtype", "/Widget");
            widget.Elements["/Rect"] = Box(doc, l, b, l + w, b + h);
            widget.Elements.SetInteger("/F", 4);                                              // (printed)
            if (f.Kind != PdfNewFieldKind.Radio) widget.Elements.SetString("/T", name);
            widget.Elements.SetString("/" + PdfFile.OwnFieldKey, "1");                       // (made here: it can be picked up and edited again)
            widget.Elements.SetString("/" + PdfFile.OwnColorKey, $"{f.Color.R:X2}{f.Color.G:X2}{f.Color.B:X2}");
            // the person's colour: a solid edge, nothing inside (no background)
            double cr = f.Color.R / 255.0, cg = f.Color.G / 255.0, cb = f.Color.B / 255.0;
            string edge = $"{Num(cr)} {Num(cg)} {Num(cb)}";
            var mk = new PdfDictionary(doc);
            mk.Elements["/BC"] = new PdfArray(doc, new PdfReal(cr), new PdfReal(cg), new PdfReal(cb));
            widget.Elements["/MK"] = mk;
            var bs = new PdfDictionary(doc);                                                   // (border style: solid, 1.5 pt)
            bs.Elements.SetName("/S", "/S");
            bs.Elements["/W"] = new PdfReal(BorderWidth);
            widget.Elements["/BS"] = bs;

            var normal = new PdfDictionary(doc);
            var x = f.Extra ?? new PdfFieldExtra();
            int flags = x.Required ? 1 << 1 : 0;                                                 // (field flags: bit 2 = required, bit 13 = several lines, bit 18 = drop-down list)
            if (f.Kind == PdfNewFieldKind.Text)
            {
                widget.Elements.SetName("/FT", "/Tx");
                double size = Math.Clamp(f.FontSize, 4, Math.Max(4, h * 0.75));
                string font = FontResource(f.Font, f.Bold);
                EnsureFont(doc, acro, font);
                widget.Elements.SetString("/DA", "/" + font + " " + Num(size) + " Tf 0 g");
                if (x.Multiline) flags |= 1 << 12;
                if (x.MaxLength > 0) widget.Elements.SetInteger("/MaxLen", x.MaxLength);
                if (x.DefaultText.Length > 0) { widget.Elements.SetString("/V", x.DefaultText); widget.Elements.SetString("/DV", x.DefaultText); }
                var ap = new PdfDictionary(doc);
                ap.Elements["/N"] = Form(doc, w, h, Frame(w, h, edge, ""));
                widget.Elements["/AP"] = ap;
            }
            else if (f.Kind == PdfNewFieldKind.Dropdown)
            {
                widget.Elements.SetName("/FT", "/Ch");
                double size = Math.Clamp(f.FontSize, 4, Math.Max(4, h * 0.75));
                string font = FontResource(f.Font, f.Bold);
                EnsureFont(doc, acro, font);
                widget.Elements.SetString("/DA", "/" + font + " " + Num(size) + " Tf 0 g");
                flags |= 1 << 17;
                var opt = new PdfArray(doc);
                foreach (string choice in x.Choices ?? Array.Empty<string>()) opt.Elements.Add(new PdfString(choice));
                widget.Elements["/Opt"] = opt;
                if (x.DefaultText.Length > 0 && (x.Choices?.Contains(x.DefaultText) ?? false)) { widget.Elements.SetString("/V", x.DefaultText); widget.Elements.SetString("/DV", x.DefaultText); }
                var ap = new PdfDictionary(doc);
                ap.Elements["/N"] = Form(doc, w, h, Frame(w, h, edge, $"{edge} rg {Num(w - 4 - Math.Min(10, h * 0.5))} {Num(h * 0.62)} m {Num(w - 4)} {Num(h * 0.62)} l {Num(w - 4 - Math.Min(10, h * 0.5) / 2)} {Num(h * 0.32)} l f\n"));
                widget.Elements["/AP"] = ap;
            }
            else if (f.Kind == PdfNewFieldKind.Radio)
            {
                // a round button of a group: the group is the field (its parent), this is one of its widgets; "Off" or its own name is the state
                var parent = RadioParent(name);
                string on = "/" + ExportName(f.Value);
                mk.Elements.SetString("/CA", "l");
                widget.Elements.SetName("/AS", "/Off");
                widget.Elements["/Parent"] = parent.Reference;
                normal.Elements[on] = Form(doc, w, h, Circle(w, h, edge, filled: true));
                normal.Elements["/Off"] = Form(doc, w, h, Circle(w, h, edge, filled: false));
                var ap = new PdfDictionary(doc);
                ap.Elements["/N"] = normal;
                widget.Elements["/AP"] = ap;
                doc.Internals.AddObject(widget);
                (Resolve(parent.Elements["/Kids"]) as PdfArray)!.Elements.Add(widget.Reference!);
            }
            else if (f.Kind == PdfNewFieldKind.Signature)
            {
                widget.Elements.SetName("/FT", "/Sig");
                var ap = new PdfDictionary(doc);
                ap.Elements["/N"] = Form(doc, w, h, SignatureLook(w, h, edge));
                widget.Elements["/AP"] = ap;
            }
            else
            {
                widget.Elements.SetName("/FT", "/Btn");
                widget.Elements.SetName("/V", x.Ticked ? "/Yes" : "/Off");
                widget.Elements.SetName("/AS", x.Ticked ? "/Yes" : "/Off");
                mk.Elements.SetString("/CA", "4");
                double s = Math.Min(w, h);
                string tick = $"{edge} RG {Num(s * 0.13)} w 1 J 1 j {Num(w * 0.22)} {Num(h * 0.5)} m {Num(w * 0.43)} {Num(h * 0.25)} l {Num(w * 0.79)} {Num(h * 0.77)} l S\n";
                normal.Elements["/Yes"] = Form(doc, w, h, Frame(w, h, edge, tick));
                normal.Elements["/Off"] = Form(doc, w, h, Frame(w, h, edge, ""));
                var ap = new PdfDictionary(doc);
                ap.Elements["/N"] = normal;
                widget.Elements["/AP"] = ap;
            }

            if (flags != 0 && f.Kind != PdfNewFieldKind.Radio) widget.Elements.SetInteger("/Ff", flags);
            if (widget.Reference == null) doc.Internals.AddObject(widget);             // (a round button was added to the object list above, to be listed in its group)
            var page = doc.Pages[f.Page];
            page.Elements.SetName("/Tabs", "/R");                                       // (Tab goes along the rows of the page, top to bottom)
            widget.Elements["/P"] = page.Reference;
            var annots = Resolve(page.Elements["/Annots"]) as PdfArray;
            if (annots == null) { annots = new PdfArray(doc); page.Elements["/Annots"] = annots; }
            annots.Elements.Add(widget.Reference!);
            if (f.Kind != PdfNewFieldKind.Radio) all.Elements.Add(widget.Reference!);       // (the group is in the form's list, not each of its buttons)
        }

        using var output = new MemoryStream();
        doc.Save(output, false);
        return output.ToArray();
    }

    private static PdfArray Box(PdfDocument doc, double l, double b, double r, double t) => new(doc, new PdfReal(l), new PdfReal(b), new PdfReal(r), new PdfReal(t));

    private static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

    /// <summary>The names Acrobat uses in a form's default resources for the standard fonts.</summary>
    private static string FontResource(PdfFontKind kind, bool bold) => kind switch
    {
        PdfFontKind.Serif => bold ? "TiBo" : "TiRo",
        PdfFontKind.Mono => bold ? "CoBo" : "Cour",
        _ => bold ? "HeBo" : "Helv",
    };

    private static string BaseFont(string resource) => resource switch
    {
        "TiBo" => "Times-Bold", "TiRo" => "Times-Roman", "CoBo" => "Courier-Bold", "Cour" => "Courier", "HeBo" => "Helvetica-Bold", _ => "Helvetica",
    };

    /// <summary>A standard font under its short name in the form's default resources (what the fields' /DA refers to).</summary>
    private static void EnsureFont(PdfDocument doc, PdfDictionary acro, string name = "Helv")
    {
        var dr = Resolve(acro.Elements["/DR"]) as PdfDictionary;
        if (dr == null) { dr = new PdfDictionary(doc); acro.Elements["/DR"] = dr; }
        var fonts = Resolve(dr.Elements["/Font"]) as PdfDictionary;
        if (fonts == null) { fonts = new PdfDictionary(doc); dr.Elements["/Font"] = fonts; }
        if (fonts.Elements.ContainsKey("/" + name)) return;
        var helv = new PdfDictionary(doc);
        helv.Elements.SetName("/Type", "/Font");
        helv.Elements.SetName("/Subtype", "/Type1");
        helv.Elements.SetName("/BaseFont", "/" + BaseFont(name));
        helv.Elements.SetName("/Encoding", "/WinAnsiEncoding");
        doc.Internals.AddObject(helv);
        fonts.Elements["/" + name] = helv.Reference;
    }

    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private const double BorderWidth = 1;

    /// <summary>What a round button stands for, as a PDF name (letters and digits only).</summary>
    private static string ExportName(string value)
    {
        string s = new string((value ?? "").Where(char.IsLetterOrDigit).ToArray());
        return s.Length == 0 ? "Choice" : s;
    }

    private static string EllipsePath(double cx, double cy, double rx, double ry)
    {
        const double k = 0.5523;
        return $"{Num(cx + rx)} {Num(cy)} m " +
               $"{Num(cx + rx)} {Num(cy + k * ry)} {Num(cx + k * rx)} {Num(cy + ry)} {Num(cx)} {Num(cy + ry)} c " +
               $"{Num(cx - k * rx)} {Num(cy + ry)} {Num(cx - rx)} {Num(cy + k * ry)} {Num(cx - rx)} {Num(cy)} c " +
               $"{Num(cx - rx)} {Num(cy - k * ry)} {Num(cx - k * rx)} {Num(cy - ry)} {Num(cx)} {Num(cy - ry)} c " +
               $"{Num(cx + k * rx)} {Num(cy - ry)} {Num(cx + rx)} {Num(cy - k * ry)} {Num(cx + rx)} {Num(cy)} c h\n";
    }

    /// <summary>A round button: a ring, with a dot in it when chosen.</summary>
    private static string Circle(double w, double h, string edge, bool filled)
    {
        double cx = w / 2, cy = h / 2;
        string ring = EllipsePath(cx, cy, w / 2 - BorderWidth / 2, h / 2 - BorderWidth / 2) + $"{edge} RG {Num(BorderWidth)} w S\n";
        string dot = filled ? EllipsePath(cx, cy, w * 0.22, h * 0.22) + $"{edge} rg f\n" : "";
        return $"q\n{ring}{dot}Q\n";
    }

    /// <summary>An empty signature box: a dashed frame with a line to sign on.</summary>
    private static string SignatureLook(double w, double h, string edge) =>
        $"q {edge} RG 1 w [4 3] 0 d {Num(0.5)} {Num(0.5)} {Num(w - 1)} {Num(h - 1)} re S [] 0 d 0.8 w {Num(w * 0.06)} {Num(h * 0.28)} m {Num(w * 0.94)} {Num(h * 0.28)} l S Q\n";

    /// <summary>An empty box with a solid edge (no fill), the edge drawn inside the box so it is as sharp as the screen allows (and anything else on top), as an appearance stream.</summary>
    private static string Frame(double w, double h, string edge, string extra) =>
        $"q {edge} RG {Num(BorderWidth)} w {Num(BorderWidth / 2)} {Num(BorderWidth / 2)} {Num(w - BorderWidth)} {Num(h - BorderWidth)} re S Q\n{extra}";

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
