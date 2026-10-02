using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;

namespace IdmClone.Engine;

/// <summary>From "points from the top-left of the page as shown" to the page's own coordinates (bottom-up, its crop box, its /Rotate), and back.</summary>
internal readonly struct PageMapping
{
    private readonly Point _origin;
    private readonly Vector _x, _y;          // one point right / one point down, in page coordinates

    public PageMapping(IntPtr page, double width, double height)
    {
        const int k = 100;                   // (device units of 1/100 point keep it exact enough)
        int w = Math.Max(1, (int)Math.Round(width * k)), h = Math.Max(1, (int)Math.Round(height * k));
        Pdfium.FPDF_DeviceToPage(page, 0, 0, w, h, 0, 0, 0, out double x0, out double y0);
        Pdfium.FPDF_DeviceToPage(page, 0, 0, w, h, 0, 1000 * k, 0, out double x1, out double y1);
        Pdfium.FPDF_DeviceToPage(page, 0, 0, w, h, 0, 0, 1000 * k, out double x2, out double y2);
        _origin = new Point(x0, y0);
        _x = new Vector((x1 - x0) / 1000, (y1 - y0) / 1000);
        _y = new Vector((x2 - x0) / 1000, (y2 - y0) / 1000);
    }

    public Point ToPage(Point shown) => _origin + _x * shown.X + _y * shown.Y;

    public Point ToShown(Point onPage)
    {
        Vector d = onPage - _origin;
        double det = _x.X * _y.Y - _x.Y * _y.X;
        if (Math.Abs(det) < 1e-12) return new Point();
        return new Point((d.X * _y.Y - d.Y * _y.X) / det, (_x.X * d.Y - _x.Y * d.X) / det);
    }

    /// <summary>A box in page coordinates (any corner order) as a box on the page as shown.</summary>
    public Rect ToShown(double left, double bottom, double right, double top)
    {
        var a = ToShown(new Point(left, bottom)); var b = ToShown(new Point(right, top));
        return new Rect(a, b);
    }

    public Vector Right => _x;
    public Vector Up => -_y;
}

/// <summary>A link on a page: to another page of the PDF, or to a web address.</summary>
public sealed record PdfLink(Rect Box, int Page, string? Uri);

/// <summary>A sticky note (or other comment with text) already in the PDF.</summary>
public sealed record PdfNote(Rect Box, string Text);

/// <summary>An entry of the PDF's table of contents.</summary>
public sealed record PdfBookmark(string Title, int Page, IReadOnlyList<PdfBookmark> Children);

/// <summary>
/// The text of one page, letter by letter, with where each letter is (points from the top-left of the page as shown); also its links
/// and notes. Used for selecting / copying, searching, highlighting text and following links.
/// </summary>
public sealed class PdfPageText
{
    public string Text { get; }
    public Rect[] Boxes { get; }                     // Rect.Empty for letters PDFium made up (spaces, line ends)
    public IReadOnlyList<PdfLink> Links { get; }
    public IReadOnlyList<PdfNote> Notes { get; }

    internal PdfPageText(string text, Rect[] boxes, List<PdfLink> links, List<PdfNote> notes) { Text = text; Boxes = boxes; Links = links; Notes = notes; }

    /// <summary>The letter under a point, or -1.</summary>
    public int IndexAt(Point p, double tolerance = 1.5)
    {
        for (int i = 0; i < Boxes.Length; i++)
        {
            var b = Boxes[i];
            if (b.IsEmpty) continue;
            b.Inflate(tolerance, tolerance);
            if (b.Contains(p)) return i;
        }
        return -1;
    }

    /// <summary>The letter nearest to a point (for selecting by dragging): on the closest line, then the closest along it.</summary>
    public int IndexNear(Point p)
    {
        int best = -1; double bestScore = double.MaxValue;
        for (int i = 0; i < Boxes.Length; i++)
        {
            var b = Boxes[i];
            if (b.IsEmpty) continue;
            double dy = p.Y < b.Top ? b.Top - p.Y : p.Y > b.Bottom ? p.Y - b.Bottom : 0;
            double dx = p.X < b.Left ? b.Left - p.X : p.X > b.Right ? p.X - b.Right : 0;
            double score = dy * 8 + dx;                      // (a different line counts much more than a few letters along)
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    /// <summary>The whole word around a letter: [start, end).</summary>
    public (int Start, int End) WordAt(int index)
    {
        if (index < 0 || index >= Text.Length) return (index, index);
        static bool W(char c) => char.IsLetterOrDigit(c) || c is '\'' or '-' or '_' or '’';
        if (!W(Text[index])) return (index, index + 1);
        int s = index, e = index;
        while (s > 0 && W(Text[s - 1])) s--;
        while (e < Text.Length && W(Text[e])) e++;
        return (s, e);
    }

    /// <summary>The boxes covering letters [start, end), one per piece of line (for drawing a selection or a highlight).</summary>
    public List<Rect> LineBoxes(int start, int end)
    {
        var result = new List<Rect>();
        Rect current = Rect.Empty;
        for (int i = Math.Max(0, start); i < Math.Min(end, Boxes.Length); i++)
        {
            var b = Boxes[i];
            if (b.IsEmpty) continue;
            if (!current.IsEmpty)
            {
                double overlap = Math.Min(current.Bottom, b.Bottom) - Math.Max(current.Top, b.Top);
                bool sameLine = overlap > Math.Min(current.Height, b.Height) * 0.5 && b.Left >= current.Left - 2 && b.Left - current.Right < Math.Max(30, b.Height * 3);
                if (sameLine) { current.Union(b); continue; }
                result.Add(current);
            }
            current = b;
        }
        if (!current.IsEmpty) result.Add(current);
        return result;
    }

    /// <summary>Where a text occurs on this page (not minding upper / lower case, and any kind of space for a space).</summary>
    public List<(int Start, int End)> Find(string query)
    {
        var found = new List<(int, int)>();
        string q = Squash(query).Trim();
        if (q.Length == 0) return found;
        // the page's text with runs of spaces / line ends squashed to one space, remembering where each letter came from
        var sb = new StringBuilder(Text.Length);
        var from = new List<int>(Text.Length);
        for (int i = 0; i < Text.Length; i++)
        {
            char c = Text[i];
            bool space = char.IsWhiteSpace(c) || c == ' ';
            if (space) { if (sb.Length > 0 && sb[^1] == ' ') continue; c = ' '; }
            sb.Append(c); from.Add(i);
        }
        string hay = sb.ToString();
        int at = 0;
        while ((at = hay.IndexOf(q, at, StringComparison.CurrentCultureIgnoreCase)) >= 0)
        {
            found.Add((from[at], from[at + q.Length - 1] + 1));
            at += Math.Max(1, q.Length);
        }
        return found;
    }

    private static string Squash(string s) => string.Join(' ', s.Split(new[] { ' ', '\t', '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Letters [start, end) as text to copy (line ends become new lines).</summary>
    public string Slice(int start, int end)
    {
        start = Math.Clamp(start, 0, Text.Length); end = Math.Clamp(end, start, Text.Length);
        return Text[start..end].Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", Environment.NewLine);
    }
}

internal static class PdfTextReader
{
    /// <summary>Reads one page (call under Pdfium.Sync).</summary>
    public static PdfPageText Read(IntPtr doc, int index)
    {
        Pdfium.FPDF_GetPageSizeByIndexF(doc, index, out var size);
        IntPtr page = Pdfium.FPDF_LoadPage(doc, index);
        if (page == IntPtr.Zero) return new PdfPageText("", Array.Empty<Rect>(), new(), new());
        IntPtr tp = IntPtr.Zero;
        try
        {
            var map = new PageMapping(page, size.Width, size.Height);
            tp = Pdfium.FPDFText_LoadPage(page);
            var sb = new StringBuilder();
            var boxes = new List<Rect>();
            if (tp != IntPtr.Zero)
            {
                int n = Pdfium.FPDFText_CountChars(tp);
                for (int i = 0; i < n; i++)
                {
                    uint u = Pdfium.FPDFText_GetUnicode(tp, i);
                    string s = u == 0 ? " " : u <= 0xFFFF ? ((char)u).ToString() : char.ConvertFromUtf32((int)Math.Min(u, 0x10FFFF));
                    Rect box = Rect.Empty;
                    if (!char.IsWhiteSpace(s[0]) && Pdfium.FPDFText_GetLooseCharBox(tp, i, out var r) != 0 && r.Right > r.Left)
                        box = map.ToShown(r.Left, r.Bottom, r.Right, r.Top);
                    else if (char.IsWhiteSpace(s[0]) && s[0] == ' ' && Pdfium.FPDFText_GetLooseCharBox(tp, i, out var sp) != 0 && sp.Right > sp.Left)
                        box = map.ToShown(sp.Left, sp.Bottom, sp.Right, sp.Top);          // (real spaces have a place too: selections stay in one piece)
                    // (a letter outside the basic plane takes two places in a .NET string: both get its box)
                    foreach (char c in s) { sb.Append(c); boxes.Add(box); }
                }
            }

            // links: the PDF's own, and web addresses written in the text
            var links = new List<PdfLink>();
            int start = 0;
            while (Pdfium.FPDFLink_Enumerate(page, ref start, out IntPtr link) != 0)
            {
                if (Pdfium.FPDFLink_GetAnnotRect(link, out var lr) == 0) continue;
                var box = map.ToShown(lr.Left, lr.Bottom, lr.Right, lr.Top);
                IntPtr dest = Pdfium.FPDFLink_GetDest(doc, link);
                IntPtr action = Pdfium.FPDFLink_GetAction(link);
                if (dest == IntPtr.Zero && action != IntPtr.Zero && Pdfium.FPDFAction_GetType(action) == Pdfium.ActionGoTo) dest = Pdfium.FPDFAction_GetDest(doc, action);
                if (dest != IntPtr.Zero) { int p = Pdfium.FPDFDest_GetDestPageIndex(doc, dest); if (p >= 0) links.Add(new PdfLink(box, p, null)); continue; }
                if (action != IntPtr.Zero && Pdfium.FPDFAction_GetType(action) == Pdfium.ActionUri)
                {
                    uint len = Pdfium.FPDFAction_GetURIPath(doc, action, null, 0);
                    if (len > 1 && len < 8192)
                    {
                        var buf = new byte[len];
                        Pdfium.FPDFAction_GetURIPath(doc, action, buf, len);
                        links.Add(new PdfLink(box, -1, Encoding.UTF8.GetString(buf, 0, (int)len - 1)));
                    }
                }
            }
            if (tp != IntPtr.Zero)
            {
                IntPtr web = Pdfium.FPDFLink_LoadWebLinks(tp);
                if (web != IntPtr.Zero)
                {
                    try
                    {
                        int count = Pdfium.FPDFLink_CountWebLinks(web);
                        for (int i = 0; i < count; i++)
                        {
                            int chars = Pdfium.FPDFLink_GetURL(web, i, null, 0);
                            if (chars <= 1 || chars > 8192) continue;
                            var buf = new char[chars];
                            Pdfium.FPDFLink_GetURL(web, i, buf, chars);
                            string url = new string(buf, 0, chars - 1);
                            for (int k = 0; k < Pdfium.FPDFLink_CountRects(web, i); k++)
                                if (Pdfium.FPDFLink_GetRect(web, i, k, out double l, out double t, out double r, out double b) != 0)
                                {
                                    var box = map.ToShown(l, b, r, t);
                                    if (!links.Any(x => x.Box.IntersectsWith(box))) links.Add(new PdfLink(box, -1, url));
                                }
                        }
                    }
                    finally { Pdfium.FPDFLink_CloseWebLinks(web); }
                }
            }

            // notes already in the PDF (their text shows when the pointer is on them)
            var notes = new List<PdfNote>();
            int annots = Pdfium.FPDFPage_GetAnnotCount(page);
            for (int i = 0; i < annots; i++)
            {
                IntPtr a = Pdfium.FPDFPage_GetAnnot(page, i);
                if (a == IntPtr.Zero) continue;
                try
                {
                    int sub = Pdfium.FPDFAnnot_GetSubtype(a);
                    if (sub is Pdfium.AnnotPopup or 2 /* link */ or 20 /* widget */) continue;
                    string text = Utf16(a, "Contents");
                    if (text.Trim().Length == 0 || Pdfium.FPDFAnnot_GetRect(a, out var ar) == 0) continue;
                    notes.Add(new PdfNote(map.ToShown(ar.Left, ar.Bottom, ar.Right, ar.Top), text.Trim()));
                }
                finally { Pdfium.FPDFPage_CloseAnnot(a); }
            }
            return new PdfPageText(sb.ToString(), boxes.ToArray(), links, notes);
        }
        finally
        {
            if (tp != IntPtr.Zero) Pdfium.FPDFText_ClosePage(tp);
            Pdfium.FPDF_ClosePage(page);
        }
    }

    private static string Utf16(IntPtr annot, string key)
    {
        uint len = Pdfium.FPDFAnnot_GetStringValue(annot, key, null, 0);
        if (len <= 2 || len > 1 << 20) return "";
        var buf = new byte[len];
        Pdfium.FPDFAnnot_GetStringValue(annot, key, buf, len);
        return Encoding.Unicode.GetString(buf, 0, (int)len - 2);
    }

    /// <summary>The table of contents (call under Pdfium.Sync).</summary>
    public static List<PdfBookmark> Bookmarks(IntPtr doc)
    {
        var seen = new HashSet<IntPtr>();
        List<PdfBookmark> Level(IntPtr parent, int depth)
        {
            var list = new List<PdfBookmark>();
            if (depth > 24) return list;
            for (IntPtr b = Pdfium.FPDFBookmark_GetFirstChild(doc, parent); b != IntPtr.Zero && list.Count < 5000; b = Pdfium.FPDFBookmark_GetNextSibling(doc, b))
            {
                if (!seen.Add(b)) break;                                          // (a broken PDF can loop)
                uint len = Pdfium.FPDFBookmark_GetTitle(b, null, 0);
                string title = "";
                if (len > 2 && len < 65536) { var buf = new byte[len]; Pdfium.FPDFBookmark_GetTitle(b, buf, len); title = Encoding.Unicode.GetString(buf, 0, (int)len - 2); }
                IntPtr dest = Pdfium.FPDFBookmark_GetDest(doc, b);
                if (dest == IntPtr.Zero) { IntPtr action = Pdfium.FPDFBookmark_GetAction(b); if (action != IntPtr.Zero && Pdfium.FPDFAction_GetType(action) == Pdfium.ActionGoTo) dest = Pdfium.FPDFAction_GetDest(doc, action); }
                int page = dest != IntPtr.Zero ? Pdfium.FPDFDest_GetDestPageIndex(doc, dest) : -1;
                list.Add(new PdfBookmark(title.Trim(), page, Level(b, depth + 1)));
            }
            return list;
        }
        return Level(IntPtr.Zero, 0);
    }
}
