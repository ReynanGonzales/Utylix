using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace IdmClone.Engine;

/// <summary>An area of a page to black out for good. Box: points from the top-left of the page as it is shown.</summary>
public sealed record PdfRedactMark(int Page, Rect Box) : PdfMark(Page);

/// <summary>
/// Real redaction, not just a black box: what is under the areas is taken out of the page - the letters (a piece of text only partly under
/// an area is made again from its other letters), the pixels of pictures, small drawings, links and comments - and the saved file is then
/// checked: nothing readable may be left under an area, the objects no page uses any more (the old copies of what was changed) are
/// dropped, and the document's properties (title, author, ...) and the pages' small preview pictures are cleared. If the check finds
/// anything, nothing is saved. Everything runs on this PC.
/// </summary>
public static class PdfRedactor
{
    // ---------- a plain 2D transform: x' = a x + c y + e, y' = b x + d y + f ----------
    private readonly record struct Aff(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Aff Identity = new(1, 0, 0, 1, 0, 0);
        public static Aff Of(Pdfium.Matrix m) => new(m.A, m.B, m.C, m.D, m.E, m.F);
        /// <summary>First this, then <paramref name="then"/>.</summary>
        public Aff Then(Aff t) => new(A * t.A + B * t.C, A * t.B + B * t.D, C * t.A + D * t.C, C * t.B + D * t.D, E * t.A + F * t.C + t.E, E * t.B + F * t.D + t.F);
        public Point Apply(double x, double y) => new(A * x + C * y + E, B * x + D * y + F);
        public Aff Inverse()
        {
            double det = A * D - B * C;
            if (Math.Abs(det) < 1e-12) return Identity;
            double a = D / det, b = -B / det, c = -C / det, d = A / det;
            return new(a, b, c, d, -(E * a + F * c), -(E * b + F * d));
        }
    }

    private sealed class Ch { public int Index; public string Text = ""; public Rect Shown; public double OriginX, OriginY; public bool Hit; }

    /// <summary>Is this box (on the page as shown) under an area? (a letter counts when a fifth of it, or its middle, is covered)</summary>
    private static bool Covered(Rect box, IReadOnlyList<Rect> areas)
    {
        double size = box.Width * box.Height;
        var middle = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
        foreach (var a in areas)
        {
            if (a.Contains(middle)) return true;
            var inter = Rect.Intersect(a, box);
            if (!inter.IsEmpty && size > 1e-9 && inter.Width * inter.Height >= 0.2 * size) return true;
        }
        return false;
    }

    private static bool Touches(Rect box, IReadOnlyList<Rect> areas) => areas.Any(a => !Rect.Intersect(a, box).IsEmpty);

    private static Rect BoundsOf(Aff m, double l, double b, double r, double t, PageMapping map)
    {
        var pts = new[] { m.Apply(l, b), m.Apply(r, b), m.Apply(r, t), m.Apply(l, t) }.Select(map.ToShown).ToArray();
        double x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    private readonly record struct Found(IntPtr Obj, IntPtr Form, Aff M);

    /// <summary>All the objects of the page, also those inside form objects, with the transform from their own space to the page (call under Sync).</summary>
    private static void Collect(IntPtr container, bool isForm, IntPtr form, Aff m, List<Found> into, int depth)
    {
        if (depth > 12) return;
        int count = isForm ? Pdfium.FPDFFormObj_CountObjects(container) : Pdfium.FPDFPage_CountObjects(container);
        for (int i = 0; i < count; i++)
        {
            IntPtr obj = isForm ? Pdfium.FPDFFormObj_GetObject(container, (uint)i) : Pdfium.FPDFPage_GetObject(container, i);
            if (obj == IntPtr.Zero) continue;
            if (Pdfium.FPDFPageObj_GetType(obj) == Pdfium.ObjForm)
            {
                var fm = Pdfium.FPDFPageObj_GetMatrix(obj, out var fmat) != 0 ? Aff.Of(fmat) : Aff.Identity;
                Collect(obj, true, obj, fm.Then(m), into, depth + 1);
            }
            else into.Add(new Found(obj, form, m));
        }
    }

    // ---------- taking out what is under the areas ----------
    /// <summary>
    /// Removes what is under the <paramref name="areas"/> from one page (call under <see cref="Pdfium.Sync"/>; the caller writes the page's
    /// content afterwards). Throws <see cref="IOException"/> when something under an area can't be taken out. Returns true when the page
    /// had to be made into a picture: PDFium cannot change what is inside a "form" (a group of drawing a PDF uses again and again), so a page
    /// with something under an area inside one is drawn as it looks (the areas black) and that picture replaces its content.
    /// </summary>
    internal static bool RemoveUnder(IntPtr doc, IntPtr page, PageMapping map, Size pageSize, IReadOnlyList<Rect> areas, Dictionary<string, IntPtr> fonts)
    {
        var objects = new List<Found>();
        Collect(page, false, IntPtr.Zero, Aff.Identity, objects, 0);

        // every letter of the page, and which piece of text it belongs to
        var letters = new Dictionary<IntPtr, List<Ch>>();
        IntPtr tp = Pdfium.FPDFText_LoadPage(page);
        try
        {
            if (tp != IntPtr.Zero)
            {
                int n = Pdfium.FPDFText_CountChars(tp);
                for (int i = 0; i < n; i++)
                {
                    IntPtr owner = Pdfium.FPDFText_GetTextObject(tp, i);
                    if (owner == IntPtr.Zero) continue;                              // (a space or line break the reader made up)
                    if (Pdfium.FPDFText_GetCharBox(tp, i, out double l, out double r, out double b, out double t) == 0) continue;
                    uint code = Pdfium.FPDFText_GetUnicode(tp, i);
                    string text = code is > 0 and < 0x110000 && !(code is >= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32((int)code) : "";
                    Pdfium.FPDFText_GetCharOrigin(tp, i, out double ox, out double oy);
                    var shown = map.ToShown(l, b, r, t);
                    if (!letters.TryGetValue(owner, out var list)) letters[owner] = list = new List<Ch>();
                    list.Add(new Ch { Index = i, Text = text, Shown = shown, OriginX = ox, OriginY = oy, Hit = Covered(shown, areas) });
                }
            }

            // something under an area inside a form can't be edited: the whole page is made into a picture instead
            foreach (var f in objects.Where(o => o.Form != IntPtr.Zero))
            {
                int t0 = Pdfium.FPDFPageObj_GetType(f.Obj);
                bool nestedHit = false;
                if (t0 == Pdfium.ObjText) nestedHit = letters.TryGetValue(f.Obj, out var cs) && cs.Any(c => c.Hit);
                else if (t0 == Pdfium.ObjImage) nestedHit = Pdfium.FPDFPageObj_GetMatrix(f.Obj, out var nim) != 0 && Touches(BoundsOf(Aff.Of(nim).Then(f.M), 0, 0, 1, 1, map), areas);
                else if (t0 == Pdfium.ObjPath) nestedHit = Pdfium.FPDFPageObj_GetBounds(f.Obj, out float pl, out float pb, out float pr, out float pt) != 0 && Touches(BoundsOf(f.M, pl, pb, pr, pt, map), areas);
                if (nestedHit) { FlattenPage(doc, page, map, pageSize, areas); RemoveAnnots(page, map, areas); return true; }
            }

            foreach (var f in objects)
            {
                int type = Pdfium.FPDFPageObj_GetType(f.Obj);
                if (type == Pdfium.ObjText && letters.TryGetValue(f.Obj, out var chars) && chars.Any(c => c.Hit)) RedactText(doc, page, f, chars, fonts);
                else if (type == Pdfium.ObjImage) RedactImage(page, f, map, areas);
                else if (type == Pdfium.ObjPath && Pdfium.FPDFPageObj_GetBounds(f.Obj, out float l, out float b, out float r, out float t) != 0)
                {
                    var box = BoundsOf(f.M, l, b, r, t, map);
                    if (areas.Any(a => a.Contains(new Rect(box.X + 0.4, box.Y + 0.4, Math.Max(0, box.Width - 0.8), Math.Max(0, box.Height - 0.8))))) Remove(page, f);     // a drawing that is wholly under an area
                }
            }
        }
        finally { if (tp != IntPtr.Zero) Pdfium.FPDFText_ClosePage(tp); }

        RemoveAnnots(page, map, areas);
        return false;
    }

    /// <summary>The page drawn as it looks with the areas black, in place of its own content (the text of this page can no longer be selected).</summary>
    private static void FlattenPage(IntPtr doc, IntPtr page, PageMapping map, Size pageSize, IReadOnlyList<Rect> areas)
    {
        double scale = Math.Min(200.0 / 72, 5000.0 / Math.Max(pageSize.Width, pageSize.Height));
        int w = Math.Max(1, (int)Math.Round(pageSize.Width * scale)), h = Math.Max(1, (int)Math.Round(pageSize.Height * scale));
        IntPtr bmp = Pdfium.FPDFBitmap_CreateEx(w, h, Pdfium.BitmapBgrx, IntPtr.Zero, 0);
        if (bmp == IntPtr.Zero) throw new IOException("A page could not be made into a picture (not enough memory).");
        byte[] jpeg;
        try
        {
            Pdfium.FPDFBitmap_FillRect(bmp, 0, 0, w, h, 0xFFFFFFFF);
            Pdfium.FPDF_RenderPageBitmap(bmp, page, 0, 0, w, h, 0, Pdfium.RenderAnnotations | Pdfium.RenderLcdText);
            foreach (var a in areas)
            {
                int x0 = Math.Max(0, (int)Math.Floor(a.Left * scale) - 1), y0 = Math.Max(0, (int)Math.Floor(a.Top * scale) - 1);
                int x1 = Math.Min(w, (int)Math.Ceiling(a.Right * scale) + 1), y1 = Math.Min(h, (int)Math.Ceiling(a.Bottom * scale) + 1);
                if (x1 > x0 && y1 > y0) Pdfium.FPDFBitmap_FillRect(bmp, x0, y0, x1 - x0, y1 - y0, 0xFF000000);
            }
            int stride = Pdfium.FPDFBitmap_GetStride(bmp);
            var pixels = new byte[(long)stride * h];
            Marshal.Copy(Pdfium.FPDFBitmap_GetBuffer(bmp), pixels, 0, pixels.Length);
            var source = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, pixels, stride);
            var enc = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 88 };
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(new System.Windows.Media.Imaging.FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgr24, null, 0)));
            using var ms = new MemoryStream(); enc.Save(ms); jpeg = ms.ToArray();
        }
        finally { Pdfium.FPDFBitmap_Destroy(bmp); }

        // everything of the page goes, and the picture takes its place
        for (int i = Pdfium.FPDFPage_CountObjects(page) - 1; i >= 0; i--)
        {
            IntPtr obj = Pdfium.FPDFPage_GetObject(page, i);
            if (obj == IntPtr.Zero) continue;
            if (Pdfium.FPDFPage_RemoveObject(page, obj) == 0) throw new IOException("The old content of a page could not be taken out.");
            Pdfium.FPDFPageObj_Destroy(obj);
        }
        IntPtr image = Pdfium.FPDFPageObj_NewImageObj(doc);
        if (image == IntPtr.Zero || !PdfCombiner.SetPicture(image, jpeg, null)) throw new IOException("The page's picture could not be written.");
        Point bl = map.ToPage(new Point(0, pageSize.Height)), br = map.ToPage(new Point(pageSize.Width, pageSize.Height)), tl = map.ToPage(new Point(0, 0));
        Pdfium.FPDFImageObj_SetMatrix(image, br.X - bl.X, br.Y - bl.Y, tl.X - bl.X, tl.Y - bl.Y, bl.X, bl.Y);
        Pdfium.FPDFPage_InsertObject(page, image);
    }

    /// <summary>Links, comments and form fields under an area are taken out.</summary>
    private static void RemoveAnnots(IntPtr page, PageMapping map, IReadOnlyList<Rect> areas)
    {
        for (int i = Pdfium.FPDFPage_GetAnnotCount(page) - 1; i >= 0; i--)
        {
            IntPtr annot = Pdfium.FPDFPage_GetAnnot(page, i);
            if (annot == IntPtr.Zero) continue;
            bool under = false;
            try { under = Pdfium.FPDFAnnot_GetRect(annot, out var rect) != 0 && Touches(map.ToShown(rect.Left, rect.Bottom, rect.Right, rect.Top), areas); }
            finally { Pdfium.FPDFPage_CloseAnnot(annot); }
            if (under) Pdfium.FPDFPage_RemoveAnnot(page, i);
        }
    }

    private static void Remove(IntPtr page, Found f)
    {
        int ok = f.Form == IntPtr.Zero ? Pdfium.FPDFPage_RemoveObject(page, f.Obj) : Pdfium.FPDFFormObj_RemoveObject(f.Form, f.Obj);
        if (ok == 0) throw new IOException("Something under a black box could not be taken out of the page.");
        Pdfium.FPDFPageObj_Destroy(f.Obj);
    }

    /// <summary>A piece of text that has a letter under an area: gone as a whole, and its other letters put back one by one where they were.</summary>
    private static void RedactText(IntPtr doc, IntPtr page, Found f, List<Ch> chars, Dictionary<string, IntPtr> fonts)
    {
        var keep = chars.Where(c => !c.Hit && c.Text.Length > 0 && !char.IsWhiteSpace(c.Text[0])).ToList();
        if (keep.Count > 0)
        {
            IntPtr font = Pdfium.FPDFTextObj_GetFont(f.Obj);
            Pdfium.FPDFTextObj_GetFontSize(f.Obj, out float fontSize);
            Pdfium.FPDFPageObj_GetFillColor(f.Obj, out uint r, out uint g, out uint b, out uint a);
            int mode = Pdfium.FPDFTextObj_GetTextRenderMode(f.Obj);
            var own = Pdfium.FPDFPageObj_GetMatrix(f.Obj, out var tm) != 0 ? Aff.Of(tm) : Aff.Identity;
            var linear = own.Then(f.M);                                              // how the letters are turned and scaled on the page
            var (family, bold, italic) = PdfTextRuns.Describe(font);
            int at = -1;                                                              // where the old piece sits among the page's objects: the new letters take its place (the reading order stays)
            if (f.Form == IntPtr.Zero) { int total = Pdfium.FPDFPage_CountObjects(page); for (int i = 0; i < total; i++) if (Pdfium.FPDFPage_GetObject(page, i) == f.Obj) { at = i; break; } }
            foreach (var c in keep)
            {
                IntPtr fresh = Pdfium.FPDFPageObj_CreateTextObj(doc, font, fontSize);
                if (fresh == IntPtr.Zero || Pdfium.FPDFText_SetText(fresh, c.Text) == 0)
                {
                    if (fresh != IntPtr.Zero) Pdfium.FPDFPageObj_Destroy(fresh);
                    // the PDF's own font cannot write this letter again: a look-alike from Windows
                    IntPtr other = PdfTextRuns.Substitute(doc, family, bold, italic, c.Text, fonts);
                    fresh = Pdfium.FPDFPageObj_CreateTextObj(doc, other, fontSize);
                    if (fresh == IntPtr.Zero || Pdfium.FPDFText_SetText(fresh, c.Text) == 0) throw new IOException("A letter next to a black box could not be written again.");
                }
                Pdfium.FPDFPageObj_SetFillColor(fresh, r, g, b, a);
                if (mode != 0) { try { Pdfium.FPDFTextObj_SetTextRenderMode(fresh, mode); } catch (EntryPointNotFoundException) { } }
                var m = new Pdfium.Matrix { A = (float)linear.A, B = (float)linear.B, C = (float)linear.C, D = (float)linear.D, E = (float)c.OriginX, F = (float)c.OriginY };
                Pdfium.FPDFPageObj_SetMatrix(fresh, ref m);
                bool placed = false;
                if (at >= 0) { try { placed = Pdfium.FPDFPage_InsertObjectAtIndex(page, fresh, (UIntPtr)at) != 0; if (placed) at++; } catch (EntryPointNotFoundException) { } }
                if (!placed) Pdfium.FPDFPage_InsertObject(page, fresh);
            }
        }
        Remove(page, f);
    }

    /// <summary>A picture under an area: those pixels turned black (the whole picture when it is turned or sheared, or can't be read).</summary>
    private static void RedactImage(IntPtr page, Found f, PageMapping map, IReadOnlyList<Rect> areas)
    {
        if (Pdfium.FPDFPageObj_GetMatrix(f.Obj, out var im) == 0) return;
        var m = Aff.Of(im).Then(f.M);                                               // picture space (a unit square) -> page
        var shown = BoundsOf(m, 0, 0, 1, 1, map);
        if (!Touches(shown, areas)) return;
        IntPtr bmp = Pdfium.FPDFImageObj_GetBitmap(f.Obj);
        if (bmp == IntPtr.Zero) { Remove(page, f); return; }                          // a kind of picture PDFium can't change: the whole picture goes
        try
        {
            int w = Pdfium.FPDFBitmap_GetWidth(bmp), h = Pdfium.FPDFBitmap_GetHeight(bmp), stride = Pdfium.FPDFBitmap_GetStride(bmp), format = Pdfium.FPDFBitmap_GetFormat(bmp);
            IntPtr buffer = Pdfium.FPDFBitmap_GetBuffer(bmp);
            int bytes = format switch { Pdfium.BitmapGray => 1, Pdfium.BitmapBgr => 3, _ => 4 };
            if (buffer == IntPtr.Zero || w <= 0 || h <= 0) { Remove(page, f); return; }
            bool upright = Math.Abs(m.B) < 1e-6 * Math.Abs(m.A) + 1e-9 && Math.Abs(m.C) < 1e-6 * Math.Abs(m.D) + 1e-9 && Math.Abs(m.A) > 1e-9 && Math.Abs(m.D) > 1e-9;
            var inverse = m.Inverse();
            var zero = new byte[Math.Max(stride, 1)];
            foreach (var area in areas.Where(a => Touches(shown, new[] { a })))
            {
                int x0 = 0, x1 = w, y0 = 0, y1 = h;                                   // pixels to blacken (the whole picture unless it can be worked out)
                if (upright)
                {
                    // the area's corners in picture space (0..1 across, 0..1 from the bottom), then pixels (rows count from the top)
                    var corners = new[] { new Point(area.Left, area.Top), new Point(area.Right, area.Top), new Point(area.Right, area.Bottom), new Point(area.Left, area.Bottom) }
                        .Select(p => inverse.Apply(map.ToPage(p).X, map.ToPage(p).Y)).ToArray();
                    double u0 = corners.Min(p => p.X), u1 = corners.Max(p => p.X), v0 = corners.Min(p => p.Y), v1 = corners.Max(p => p.Y);
                    x0 = Math.Clamp((int)Math.Floor(u0 * w) - 1, 0, w); x1 = Math.Clamp((int)Math.Ceiling(u1 * w) + 1, 0, w);
                    y0 = Math.Clamp((int)Math.Floor((1 - v1) * h) - 1, 0, h); y1 = Math.Clamp((int)Math.Ceiling((1 - v0) * h) + 1, 0, h);
                }
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        IntPtr p = buffer + y * stride + x * bytes;
                        for (int k = 0; k < Math.Min(bytes, 3); k++) Marshal.WriteByte(p, k, 0);          // (colour black, a transparency channel is left alone)
                    }
            }
            if (Pdfium.FPDFImageObj_SetBitmap(IntPtr.Zero, 0, f.Obj, bmp) == 0) Remove(page, f);
        }
        finally { Pdfium.FPDFBitmap_Destroy(bmp); }
    }

    // ---------- after saving: clean the file and check it ----------
    /// <summary>
    /// The saved file, rewritten without the objects nothing uses any more (the old copies of what was changed), without the document's
    /// properties and the pages' preview pictures - then checked. Throws <see cref="IOException"/> when anything of what was under the
    /// areas is still in it.
    /// </summary>
    public static byte[] Finish(byte[] saved, IReadOnlyList<PdfRedactMark> areas)
    {
        byte[] clean = Scrub(saved);
        Verify(clean, areas);
        return clean;
    }

    private static byte[] Scrub(byte[] bytes)
    {
        try
        {
            using var input = new MemoryStream(bytes);
            using var doc = PdfSharp.Pdf.IO.PdfReader.Open(input, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
            foreach (string key in new[] { "/Title", "/Author", "/Subject", "/Keywords", "/Creator" }) doc.Info.Elements.Remove(key);
            doc.Internals.Catalog.Elements.Remove("/Metadata");                     // (the same properties again, as XML)
            doc.Internals.Catalog.Elements.Remove("/PieceInfo");
            foreach (var page in doc.Pages) { page.Elements.Remove("/Thumb"); page.Elements.Remove("/PieceInfo"); page.Elements.Remove("/Metadata"); }
            using var output = new MemoryStream();
            doc.Save(output, false);                                                 // (writing leaves out the objects no page reaches)
            return output.ToArray();
        }
        catch (Exception e) when (e is PdfSharp.Pdf.IO.PdfReaderException or InvalidOperationException or NotImplementedException or NotSupportedException or IOException or ArgumentException or InvalidCastException or NullReferenceException or IndexOutOfRangeException)
        {
            throw new IOException("The redacted file could not be cleaned up (" + e.Message + "), so nothing was saved.");
        }
    }

    /// <summary>Opens the finished file again and looks for anything left under the areas.</summary>
    private static void Verify(byte[] bytes, IReadOnlyList<PdfRedactMark> areas)
    {
        Pdfium.Init();
        IntPtr data = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, data, bytes.Length);
        try
        {
            lock (Pdfium.Sync)
            {
                IntPtr doc = Pdfium.FPDF_LoadMemDocument64(data, (UIntPtr)bytes.Length, null);
                if (doc == IntPtr.Zero) throw new IOException("The redacted file could not be read back to check it, so nothing was saved.");
                try
                {
                    foreach (var group in areas.GroupBy(a => a.Page))
                    {
                        var boxes = group.Select(g => g.Box).ToList();
                        IntPtr page = Pdfium.FPDF_LoadPage(doc, group.Key);
                        if (page == IntPtr.Zero) throw new IOException("Page " + (group.Key + 1) + " could not be read back to check it, so nothing was saved.");
                        try
                        {
                            Pdfium.FPDF_GetPageSizeByIndexF(doc, group.Key, out var size);
                            var map = new PageMapping(page, size.Width, size.Height);
                            CheckText(page, map, boxes, group.Key);
                            CheckObjects(page, map, boxes, group.Key);
                        }
                        finally { Pdfium.FPDF_ClosePage(page); }
                    }
                }
                finally { Pdfium.FPDF_CloseDocument(doc); }
            }
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    private static void CheckText(IntPtr page, PageMapping map, IReadOnlyList<Rect> boxes, int index)
    {
        IntPtr tp = Pdfium.FPDFText_LoadPage(page);
        if (tp == IntPtr.Zero) return;
        try
        {
            int n = Pdfium.FPDFText_CountChars(tp);
            for (int i = 0; i < n; i++)
            {
                uint code = Pdfium.FPDFText_GetUnicode(tp, i);
                if (code <= 0x20 || Pdfium.FPDFText_GetCharBox(tp, i, out double l, out double r, out double b, out double t) == 0) continue;
                if (Covered(map.ToShown(l, b, r, t), boxes)) throw new IOException($"Text under a black box on page {index + 1} could still be read from the file, so nothing was saved.");
            }
        }
        finally { Pdfium.FPDFText_ClosePage(tp); }
    }

    private static void CheckObjects(IntPtr page, PageMapping map, IReadOnlyList<Rect> boxes, int index)
    {
        var objects = new List<Found>();
        Collect(page, false, IntPtr.Zero, Aff.Identity, objects, 0);
        foreach (var f in objects.Where(o => Pdfium.FPDFPageObj_GetType(o.Obj) == Pdfium.ObjImage))
        {
            if (Pdfium.FPDFPageObj_GetMatrix(f.Obj, out var im) == 0) continue;
            var m = Aff.Of(im).Then(f.M);
            if (!Touches(BoundsOf(m, 0, 0, 1, 1, map), boxes)) continue;
            IntPtr bmp = Pdfium.FPDFImageObj_GetBitmap(f.Obj);
            if (bmp == IntPtr.Zero) throw new IOException($"A picture under a black box on page {index + 1} could not be checked, so nothing was saved.");
            try
            {
                int w = Pdfium.FPDFBitmap_GetWidth(bmp), h = Pdfium.FPDFBitmap_GetHeight(bmp), stride = Pdfium.FPDFBitmap_GetStride(bmp);
                int bytes = Pdfium.FPDFBitmap_GetFormat(bmp) switch { Pdfium.BitmapGray => 1, Pdfium.BitmapBgr => 3, _ => 4 };
                IntPtr buffer = Pdfium.FPDFBitmap_GetBuffer(bmp);
                var inverse = m.Inverse();
                if (buffer == IntPtr.Zero || Math.Abs(m.B) > 1e-6 * Math.Abs(m.A) + 1e-9 || Math.Abs(m.C) > 1e-6 * Math.Abs(m.D) + 1e-9) continue;     // (a turned picture was blackened whole)
                foreach (var area in boxes.Where(a => Touches(BoundsOf(m, 0, 0, 1, 1, map), new[] { a })))
                {
                    var corners = new[] { new Point(area.Left, area.Top), new Point(area.Right, area.Bottom) }.Select(p => { var q = map.ToPage(p); return inverse.Apply(q.X, q.Y); }).ToArray();
                    double u0 = corners.Min(p => p.X), u1 = corners.Max(p => p.X), v0 = corners.Min(p => p.Y), v1 = corners.Max(p => p.Y);
                    // only pixels well inside the area (the edge pixels may be shared with what is next to it, or blurred by JPEG)
                    int x0 = Math.Clamp((int)Math.Ceiling(u0 * w) + 3, 0, w), x1 = Math.Clamp((int)Math.Floor(u1 * w) - 3, 0, w);
                    int y0 = Math.Clamp((int)Math.Ceiling((1 - v1) * h) + 3, 0, h), y1 = Math.Clamp((int)Math.Floor((1 - v0) * h) - 3, 0, h);
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++)
                            for (int k = 0; k < Math.Min(bytes, 3); k++)
                                if (Marshal.ReadByte(buffer + y * stride + x * bytes + k) > 40) throw new IOException($"A picture under a black box on page {index + 1} still shows through, so nothing was saved.");
                }
            }
            finally { Pdfium.FPDFBitmap_Destroy(bmp); }
        }
    }
}
