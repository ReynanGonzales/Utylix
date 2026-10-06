using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace IdmClone.Engine;

/// <summary>One thing drawn on a page (text, line / shape, picture ...), as PDFium lists it: Index = its place in the page's list, Kind = PDFium's type (1 text, 2 path, 3 picture, 4 shading, 5 form), Box in points from the top-left of the page as shown.</summary>
public sealed record PdfPageObject(int Index, int Kind, Rect Box);

/// <summary>A text box / check box made by Utylix and saved in a PDF (found again so it can be moved, resized, copied or deleted).</summary>
public sealed record PdfOwnField(int AnnotIndex, PdfNewFieldKind Kind, Rect Box, string Name, double FontSize, PdfFontKind Font, bool Bold, Color Color);

/// <summary>Changing what is already drawn on a page of the open document: pick it up, move it, resize it, delete it. (Save writes it.)</summary>
public sealed partial class PdfFile
{
    public const string OwnFieldKey = "UtlxField", OwnColorKey = "UtlxColor";

    private IntPtr LoadPageOrThrow(int pageIndex)
    {
        IntPtr page = Pdfium.FPDF_LoadPage(_doc, pageIndex);
        if (page == IntPtr.Zero) throw new IOException("Page " + (pageIndex + 1) + " can't be read.");
        return page;
    }

    /// <summary>Everything drawn on the page, bottom to top, with where it is.</summary>
    public List<PdfPageObject> GetPageObjects(int pageIndex)
    {
        var list = new List<PdfPageObject>();
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            Pdfium.FPDF_GetPageSizeByIndexF(_doc, pageIndex, out var size);
            IntPtr page = LoadPageOrThrow(pageIndex);
            try
            {
                var map = new PageMapping(page, size.Width, size.Height);
                int count = Pdfium.FPDFPage_CountObjects(page);
                for (int i = 0; i < count; i++)
                {
                    IntPtr obj = Pdfium.FPDFPage_GetObject(page, i);
                    if (obj == IntPtr.Zero || Pdfium.FPDFPageObj_GetBounds(obj, out float l, out float b, out float r, out float t) == 0) continue;
                    list.Add(new PdfPageObject(i, Pdfium.FPDFPageObj_GetType(obj), map.ToShown(l, b, r, t)));
                }
            }
            finally { Pdfium.FPDF_ClosePage(page); }
        }
        return list;
    }

    /// <summary>Moves things by <paramref name="shownDelta"/> (points, as seen on the page).</summary>
    public void MovePageObjects(int pageIndex, IReadOnlyList<int> indices, Vector shownDelta) =>
        ChangeObjects(pageIndex, indices, (map, obj) =>
        {
            var d = map.ToPage(new Point(shownDelta.X, shownDelta.Y)) - map.ToPage(new Point(0, 0));
            Pdfium.FPDFPageObj_Transform(obj, 1, 0, 0, 1, d.X, d.Y);
        });

    /// <summary>Stretches things so that what filled <paramref name="from"/> fills <paramref name="to"/> (both as seen on the page).</summary>
    public void ScalePageObjects(int pageIndex, IReadOnlyList<int> indices, Rect from, Rect to) =>
        ChangeObjects(pageIndex, indices, (map, obj) =>
        {
            double sx = from.Width < 0.01 ? 1 : to.Width / from.Width, sy = from.Height < 0.01 ? 1 : to.Height / from.Height;
            Point Image(Point page)                                    // where a point of the page goes: to shown coordinates, scaled round the corner of the old box, back to the page
            {
                var s = map.ToShown(page);
                return map.ToPage(new Point(to.X + (s.X - from.X) * sx, to.Y + (s.Y - from.Y) * sy));
            }
            Point r0 = Image(new Point(0, 0)), r1 = Image(new Point(1, 0)), r2 = Image(new Point(0, 1));
            Pdfium.FPDFPageObj_Transform(obj, r1.X - r0.X, r1.Y - r0.Y, r2.X - r0.X, r2.Y - r0.Y, r0.X, r0.Y);
        });

    /// <summary>Takes things off the page for good (they are not in the saved file).</summary>
    public void RemovePageObjects(int pageIndex, IReadOnlyList<int> indices)
    {
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            IntPtr page = LoadPageOrThrow(pageIndex);
            try
            {
                foreach (int i in indices.Distinct().OrderByDescending(x => x))          // (from the last: the places of the others stay)
                {
                    IntPtr obj = Pdfium.FPDFPage_GetObject(page, i);
                    if (obj == IntPtr.Zero) continue;
                    if (Pdfium.FPDFPage_RemoveObject(page, obj) != 0) Pdfium.FPDFPageObj_Destroy(obj);
                }
                if (Pdfium.FPDFPage_GenerateContent(page) == 0) throw new IOException("Page " + (pageIndex + 1) + " couldn't be written.");
            }
            finally { Pdfium.FPDF_ClosePage(page); }
            _texts.Remove(pageIndex); _runs.Remove(pageIndex);
        }
    }

    /// <summary>Changes the stacking of things on the page: mode 0 = to the very front, 1 = one step forward, 2 = one step back, 3 = to the very back. Returns where they are in the list afterwards.</summary>
    public List<int> ReorderPageObjects(int pageIndex, IReadOnlyList<int> indices, int mode)
    {
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            IntPtr page = LoadPageOrThrow(pageIndex);
            try
            {
                int total = Pdfium.FPDFPage_CountObjects(page);
                var sel = indices.Where(i => i >= 0 && i < total).Distinct().OrderBy(i => i).ToList();
                var set = new HashSet<int>(sel);
                var result = new List<int>();
                void Move(int from, int to)                                        // (take one thing out of the list and put it back at another place)
                {
                    IntPtr obj = Pdfium.FPDFPage_GetObject(page, from);
                    if (obj == IntPtr.Zero || Pdfium.FPDFPage_RemoveObject(page, obj) == 0) return;
                    Pdfium.FPDFPage_InsertObjectAtIndex(page, obj, (UIntPtr)(uint)to);
                }
                switch (mode)
                {
                    case 0:
                        for (int k = 0; k < sel.Count; k++) Move(sel[k] - k, total - 1);          // (the k ones before it were already taken out and put at the end: its place is k less)
                        for (int k = 0; k < sel.Count; k++) result.Add(total - sel.Count + k);
                        break;
                    case 3:
                        for (int k = 0; k < sel.Count; k++) { Move(sel[k], k); result.Add(k); }
                        break;
                    case 1:
                    {
                        var now = sel.ToList();
                        for (int k = now.Count - 1; k >= 0; k--)
                        {
                            int i = now[k];
                            if (i + 1 < total && !now.Contains(i + 1)) { Move(i, i + 1); now[k] = i + 1; }
                        }
                        result = now;
                        break;
                    }
                    default:
                    {
                        var now = sel.ToList();
                        for (int k = 0; k < now.Count; k++)
                        {
                            int i = now[k];
                            if (i - 1 >= 0 && !now.Contains(i - 1)) { Move(i, i - 1); now[k] = i - 1; }
                        }
                        result = now;
                        break;
                    }
                }
                if (Pdfium.FPDFPage_GenerateContent(page) == 0) throw new IOException("Page " + (pageIndex + 1) + " couldn't be written.");
                return result;
            }
            finally { Pdfium.FPDF_ClosePage(page); _texts.Remove(pageIndex); _runs.Remove(pageIndex); }
        }
    }

    private void ChangeObjects(int pageIndex, IReadOnlyList<int> indices, Action<PageMapping, IntPtr> change)
    {
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            Pdfium.FPDF_GetPageSizeByIndexF(_doc, pageIndex, out var size);
            IntPtr page = LoadPageOrThrow(pageIndex);
            try
            {
                var map = new PageMapping(page, size.Width, size.Height);
                foreach (int i in indices.Distinct())
                {
                    IntPtr obj = Pdfium.FPDFPage_GetObject(page, i);
                    if (obj != IntPtr.Zero) change(map, obj);
                }
                if (Pdfium.FPDFPage_GenerateContent(page) == 0) throw new IOException("Page " + (pageIndex + 1) + " couldn't be written.");
            }
            finally { Pdfium.FPDF_ClosePage(page); }
            _texts.Remove(pageIndex); _runs.Remove(pageIndex);
        }
    }

    private static string AnnotString(IntPtr annot, string key)
    {
        uint len = Pdfium.FPDFAnnot_GetStringValue(annot, key, null, 0);
        if (len <= 2 || len > 1 << 16) return "";
        var buf = new byte[len];
        Pdfium.FPDFAnnot_GetStringValue(annot, key, buf, len);
        return Encoding.Unicode.GetString(buf, 0, (int)len - 2);
    }

    /// <summary>The text boxes and check boxes of this page that Utylix made (marked in the file, or named like the ones it makes), with what is needed to make them editable again.</summary>
    public List<PdfOwnField> GetOwnFields(int pageIndex)
    {
        var result = new List<PdfOwnField>();
        List<PdfField> fields;
        try { fields = GetFields(pageIndex); } catch (IOException) { return result; }
        var candidates = fields.Where(f => f.Kind is PdfFieldKind.Text or PdfFieldKind.CheckBox && !f.ReadOnly && !f.Password).ToList();
        if (candidates.Count == 0) return result;
        lock (Pdfium.Sync)
        {
            ThrowIfClosed();
            IntPtr page = LoadPageOrThrow(pageIndex);
            try
            {
                foreach (var f in candidates)
                {
                    IntPtr annot = Pdfium.FPDFPage_GetAnnot(page, f.AnnotIndex);
                    if (annot == IntPtr.Zero) continue;
                    try
                    {
                        bool marked = AnnotString(annot, OwnFieldKey).Length > 0;
                        bool named = f.Name.StartsWith("Text ", StringComparison.Ordinal) || f.Name.StartsWith("Check box ", StringComparison.Ordinal);
                        if (!marked && !named) continue;
                        var color = Colors.Black;                                       // (older fields have no colour of their own saved: they were drawn in the colour the person chose, black is the usual one)
                        string hex = AnnotString(annot, OwnColorKey);
                        if (hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int rgb)) color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
                        string da = AnnotString(annot, "DA");                              // e.g. "/HeBo 12 Tf 0 g"
                        var kind = da.Contains("/TiRo") || da.Contains("/TiBo") ? PdfFontKind.Serif : da.Contains("/Cour") || da.Contains("/CoBo") ? PdfFontKind.Mono : PdfFontKind.Sans;
                        bool bold = da.Contains("/HeBo") || da.Contains("/TiBo") || da.Contains("/CoBo");
                        result.Add(new PdfOwnField(f.AnnotIndex, f.Kind == PdfFieldKind.Text ? PdfNewFieldKind.Text : PdfNewFieldKind.CheckBox, f.Box, f.Name, f.FontSize > 0 ? f.FontSize : 12, kind, bold, color));
                    }
                    finally { Pdfium.FPDFPage_CloseAnnot(annot); }
                }
            }
            finally { Pdfium.FPDF_ClosePage(page); }
        }
        return result;
    }
}
