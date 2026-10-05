using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace IdmClone.Engine;

/// <summary>
/// One piece of text already in a page (a line or a part of one), as the PDF stores it: where it is (points from the top-left of the page
/// as shown), its words, size, font and colour. Only upright text is offered for changing. A line that the PDF stores in several pieces
/// ("Dep" + "artment") is one run with <paramref name="Parts"/> (left to right): its Index, Box and Baseline are the first piece's / all together.
/// </summary>
public sealed record PdfTextRun(int Index, Rect Box, string Text, double Size, string Family, bool Bold, bool Italic, Color Color, Point Baseline, IReadOnlyList<PdfTextRun>? Parts = null);

/// <summary>
/// Change the words of a piece of text already in the page (empty = remove it) and / or move it (Dx, Dy in points as shown). A new text with several
/// lines (separated by newlines) is written one line under the other, <paramref name="LineAdvance"/> points apart.
/// </summary>
/// <remarks>
/// <paramref name="Family"/> (any font name Windows knows), <paramref name="Bold"/>, <paramref name="Size"/> (points, as shown) and <paramref name="Color"/> are only set
/// when the person changed the look of the text; null keeps the PDF's own.
/// </remarks>
public sealed record PdfReplaceTextMark(int Page, int Index, string OldText, string NewText, double Dx = 0, double Dy = 0, double LineAdvance = 0,
                                        string? Family = null, bool? Bold = null, double? Size = null, Color? Color = null) : PdfMark(Page);

internal static class PdfTextRuns
{
    /// <summary>The pieces of text of a page (call under Pdfium.Sync).</summary>
    public static List<PdfTextRun> Read(IntPtr doc, int index, bool includeHidden = false)
    {
        var runs = new List<PdfTextRun>();
        Pdfium.FPDF_GetPageSizeByIndexF(doc, index, out var size);
        IntPtr page = Pdfium.FPDF_LoadPage(doc, index);
        if (page == IntPtr.Zero) return runs;
        IntPtr tp = Pdfium.FPDFText_LoadPage(page);
        try
        {
            var map = new PageMapping(page, size.Width, size.Height);
            int count = Pdfium.FPDFPage_CountObjects(page);
            for (int k = 0; k < count; k++)
            {
                IntPtr obj = Pdfium.FPDFPage_GetObject(page, k);
                if (obj == IntPtr.Zero || Pdfium.FPDFPageObj_GetType(obj) != Pdfium.ObjText) continue;
                if (!includeHidden && Pdfium.FPDFTextObj_GetTextRenderMode(obj) == Pdfium.TextInvisible) continue;      // (the hidden text of a scan made searchable)
                string text = TextOf(obj, tp);
                if (text.Trim().Length == 0) continue;
                if (Pdfium.FPDFPageObj_GetMatrix(obj, out var m) == 0 || Pdfium.FPDFTextObj_GetFontSize(obj, out float fs) == 0) continue;
                if (Pdfium.FPDFPageObj_GetBounds(obj, out float l, out float b, out float r, out float t) == 0) continue;
                // which way it reads on the page as shown: only upright text (left to right, not turned or mirrored)
                var origin = map.ToShown(new Point(m.E, m.F));
                Vector right = map.ToShown(new Point(m.E + m.A, m.F + m.B)) - origin, up = map.ToShown(new Point(m.E + m.C, m.F + m.D)) - origin;
                if (right.Length < 1e-6 || Math.Abs(right.Y) > right.Length * 0.02 || right.X <= 0 || up.Y >= 0 || Math.Abs(up.X) > up.Length * 0.02) continue;
                IntPtr font = Pdfium.FPDFTextObj_GetFont(obj);
                var (family, bold, italic) = Describe(font);
                Pdfium.FPDFPageObj_GetFillColor(obj, out uint cr, out uint cg, out uint cb, out uint ca);
                runs.Add(new PdfTextRun(k, map.ToShown(l, b, r, t), text, fs * up.Length, family, bold, italic, Color.FromArgb((byte)ca, (byte)cr, (byte)cg, (byte)cb), origin));
            }
        }
        finally
        {
            if (tp != IntPtr.Zero) Pdfium.FPDFText_ClosePage(tp);
            Pdfium.FPDF_ClosePage(page);
        }
        return runs;
    }

    /// <summary>
    /// Joins the pieces that sit side by side on one line, in the same font, size and colour, into one run: a PDF often stores a line in bits
    /// (one word, or half a word, each) and the person wants to move or change the line, not a bit. Pieces further apart than a normal gap
    /// (table columns) stay separate.
    /// </summary>
    public static List<PdfTextRun> Group(List<PdfTextRun> runs)
    {
        var result = new List<PdfTextRun>();
        var used = new bool[runs.Count];
        for (int i = 0; i < runs.Count; i++)
        {
            if (used[i]) continue;
            var a = runs[i];
            // everything on the same line with the same look, left to right
            var line = new List<int>();
            for (int j = 0; j < runs.Count; j++)
            {
                if (used[j]) continue;
                var b = runs[j];
                if (Math.Abs(b.Baseline.Y - a.Baseline.Y) <= a.Size * 0.25 && Math.Abs(b.Size - a.Size) <= a.Size * 0.05 && b.Family == a.Family && b.Bold == a.Bold && b.Italic == a.Italic && b.Color == a.Color)
                    line.Add(j);
            }
            line.Sort((x, y) => runs[x].Box.X.CompareTo(runs[y].Box.X));
            // the chain of neighbours that contains this piece
            int at = line.IndexOf(i);
            int first = at, last = at;
            while (first > 0 && Near(runs[line[first - 1]], runs[line[first]])) first--;
            while (last < line.Count - 1 && Near(runs[line[last]], runs[line[last + 1]])) last++;
            var chain = line.GetRange(first, last - first + 1);
            foreach (int k in chain) used[k] = true;
            if (chain.Count == 1) { result.Add(runs[chain[0]]); continue; }
            var parts = chain.Select(k => runs[k]).ToList();
            var text = new StringBuilder();
            for (int k = 0; k < parts.Count; k++)
            {
                if (k > 0)
                {
                    double gap = parts[k].Box.Left - parts[k - 1].Box.Right;
                    if (gap > parts[k].Size * 0.12 && !char.IsWhiteSpace(text[^1]) && !char.IsWhiteSpace(parts[k].Text[0])) text.Append(' ');
                }
                text.Append(parts[k].Text);
            }
            var box = parts[0].Box;
            foreach (var part in parts) box.Union(part.Box);
            result.Add(parts[0] with { Box = box, Text = text.ToString(), Parts = parts });
        }
        return result;

        static bool Near(PdfTextRun left, PdfTextRun right)
        {
            double gap = right.Box.Left - left.Box.Right;
            return gap <= left.Size * 0.9 && gap >= -left.Size * 0.3;
        }
    }

    internal static string TextOf(IntPtr obj, IntPtr textPage)
    {
        uint len = Pdfium.FPDFTextObj_GetText(obj, textPage, null, 0);
        if (len <= 2 || len > 1 << 20) return "";
        var buf = new byte[len];
        Pdfium.FPDFTextObj_GetText(obj, textPage, buf, len);
        return Encoding.Unicode.GetString(buf, 0, (int)len - 2).Replace("\r", "").Replace("\n", "");
    }

    /// <summary>A font's family ("Calibri"), and whether it is bold / italic.</summary>
    public static (string Family, bool Bold, bool Italic) Describe(IntPtr font)
    {
        string Ascii(Func<byte[]?, UIntPtr, UIntPtr> get)
        {
            ulong n = (ulong)get(null, UIntPtr.Zero);
            if (n <= 1 || n > 4096) return "";
            var buf = new byte[n];
            get(buf, (UIntPtr)n);
            return Encoding.UTF8.GetString(buf, 0, (int)n - 1);
        }
        if (font == IntPtr.Zero) return ("Arial", false, false);
        string baseName = Ascii((b, n) => Pdfium.FPDFFont_GetBaseFontName(font, b, n));
        string family = Ascii((b, n) => Pdfium.FPDFFont_GetFamilyName(font, b, n));
        int plus = baseName.IndexOf('+');
        if (plus is >= 0 and <= 7) baseName = baseName[(plus + 1)..];                // ("ABCDEF+Calibri-Bold": a subset)
        if (family.Length == 0) family = baseName.Split('-', ',')[0];
        int weight = Pdfium.FPDFFont_GetWeight(font);
        Pdfium.FPDFFont_GetItalicAngle(font, out int angle);
        string lower = baseName.ToLowerInvariant();
        bool bold = weight >= 600 || lower.Contains("bold") || lower.Contains("black") || lower.Contains("heavy") || lower.Contains("semibold");
        bool italic = angle != 0 || lower.Contains("italic") || lower.Contains("oblique");
        return (Readable(family), bold, italic);
    }

    /// <summary>"TimesNewRomanPSMT" / "ArialMT" -> "Times New Roman" / "Arial" (the names Windows knows).</summary>
    private static string Readable(string family)
    {
        string f = family.Trim();
        foreach (string suffix in new[] { "PSMT", "MT", "PS" }) if (f.EndsWith(suffix, StringComparison.Ordinal) && f.Length > suffix.Length + 2) f = f[..^suffix.Length];
        if (!f.Contains(' ')) f = System.Text.RegularExpressions.Regex.Replace(f, "(?<=[a-z])(?=[A-Z])", " ");
        return f;
    }

    // ---------- changing the text when saving ----------
    /// <summary>Applies the text changes of one page, before anything else is added to it (call under Pdfium.Sync).</summary>
    public static void Replace(IntPtr doc, IntPtr page, PageMapping map, IEnumerable<PdfReplaceTextMark> marks, Dictionary<string, IntPtr> fonts)
    {
        IntPtr tp = Pdfium.FPDFText_LoadPage(page);
        try
        {
            Dictionary<IntPtr, HashSet<char>>? letters = null;
            // the highest first: removing a piece moves the ones after it
            foreach (var m in marks.OrderByDescending(x => x.Index))
            {
                IntPtr obj = Pdfium.FPDFPage_GetObject(page, m.Index);
                if (obj == IntPtr.Zero || Pdfium.FPDFPageObj_GetType(obj) != Pdfium.ObjText || TextOf(obj, tp) != m.OldText)
                    throw new IOException("Page " + (m.Page + 1) + " isn't as it was when the text was changed. Open the PDF again and redo the change.");
                string[] lines = m.NewText.Replace("\r\n", "\n").Split('\n');
                if (m.NewText.Trim().Length == 0) { Pdfium.FPDFPage_RemoveObject(page, obj); Pdfium.FPDFPageObj_Destroy(obj); continue; }
                // the move, as a step in page space
                var origin = map.ToPage(new Point(0, 0)); var step = map.ToPage(new Point(m.Dx, m.Dy));
                double moveX = step.X - origin.X, moveY = step.Y - origin.Y;
                void Shift(IntPtr o) { if (moveX != 0 || moveY != 0) Pdfium.FPDFPageObj_Transform(o, 1, 0, 0, 1, moveX, moveY); }

                bool fontChanged = m.Family != null || m.Bold != null;
                bool restyled = fontChanged || m.Size != null || m.Color != null;
                if (lines.Length == 1 && m.NewText == m.OldText && !restyled) { Shift(obj); continue; }             // only moved

                IntPtr font = Pdfium.FPDFTextObj_GetFont(obj);
                letters ??= LettersByFont(page, tp);
                Pdfium.FPDFPageObj_GetMatrix(obj, out var matrix);
                Pdfium.FPDFTextObj_GetFontSize(obj, out float fs);
                Pdfium.FPDFPageObj_GetFillColor(obj, out uint r, out uint g, out uint b, out uint a);
                // the PDF's own font, when it already has every letter needed (a font in a PDF often has only the letters it uses)
                bool ownFont = !fontChanged && letters.TryGetValue(font, out var has) && lines.All(l => l.All(c => has!.Contains(c)));
                IntPtr useFont = font;
                if (!ownFont)
                {
                    // otherwise the font the person chose, or the same font from Windows (or one like it)
                    var (family, bold, italic) = Describe(font);
                    useFont = Substitute(doc, m.Family ?? family, m.Bold ?? bold, m.Family == null && italic, m.NewText.Replace("\n", " "), fonts);
                }
                // the lines below the first sit one step down the text's own "up" direction (matrix c, d), a line apart
                double up = Math.Sqrt(matrix.C * matrix.C + matrix.D * matrix.D);
                double ux = up > 1e-9 ? matrix.C / up : 0, uy = up > 1e-9 ? matrix.D / up : 1;
                double advance = m.LineAdvance > 0 ? m.LineAdvance : fs * up * 1.2;
                if (m.Size is double wanted && up > 1e-9) fs = (float)(wanted / up);                                 // (size as shown = font size x the matrix's scale)
                if (m.Color is Color nc) { r = nc.R; g = nc.G; b = nc.B; a = nc.A; }

                bool firstDone = false;
                // (a piece can only change its words in place: another size or colour is written as a new piece)
                if (ownFont && !restyled && lines[0].Length > 0 && Pdfium.FPDFText_SetText(obj, lines[0]) != 0) { Shift(obj); firstDone = true; }
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i == 0 && firstDone) continue;
                    if (lines[i].Length == 0) continue;
                    IntPtr fresh = Pdfium.FPDFPageObj_CreateTextObj(doc, useFont, fs);
                    if (fresh == IntPtr.Zero) throw new IOException("The changed text couldn't be written.");
                    Pdfium.FPDFText_SetText(fresh, lines[i]);
                    Pdfium.FPDFPageObj_SetFillColor(fresh, r, g, b, a);
                    var line = matrix;
                    line.E -= (float)(i * advance * ux); line.F -= (float)(i * advance * uy);
                    Pdfium.FPDFPageObj_SetMatrix(fresh, ref line);
                    Shift(fresh);
                    Pdfium.FPDFPage_InsertObject(page, fresh);
                }
                if (!firstDone) { Pdfium.FPDFPage_RemoveObject(page, obj); Pdfium.FPDFPageObj_Destroy(obj); }
            }
        }
        finally { if (tp != IntPtr.Zero) Pdfium.FPDFText_ClosePage(tp); }
    }

    /// <summary>For every font of the page: the letters its text uses (so: the letters the font surely has).</summary>
    private static Dictionary<IntPtr, HashSet<char>> LettersByFont(IntPtr page, IntPtr tp)
    {
        var result = new Dictionary<IntPtr, HashSet<char>>();
        int count = Pdfium.FPDFPage_CountObjects(page);
        for (int k = 0; k < count; k++)
        {
            IntPtr obj = Pdfium.FPDFPage_GetObject(page, k);
            if (obj == IntPtr.Zero || Pdfium.FPDFPageObj_GetType(obj) != Pdfium.ObjText) continue;
            IntPtr font = Pdfium.FPDFTextObj_GetFont(obj);
            if (!result.TryGetValue(font, out var set)) result[font] = set = new HashSet<char>();
            foreach (char c in TextOf(obj, tp)) set.Add(c);
        }
        return result;
    }

    internal static IntPtr Substitute(IntPtr doc, string family, bool bold, bool italic, string text, Dictionary<string, IntPtr> fonts)
    {
        string l = family.ToLowerInvariant();
        bool serif = new[] { "times", "serif", "roman", "garamond", "georgia", "cambria", "book", "minion", "palatino" }.Any(l.Contains) && !l.Contains("sans");
        bool mono = new[] { "courier", "mono", "consol", "lucida console" }.Any(l.Contains);
        string standard = mono ? "Courier" + (bold && italic ? "-BoldOblique" : bold ? "-Bold" : italic ? "-Oblique" : "")
                        : serif ? (bold && italic ? "Times-BoldItalic" : bold ? "Times-Bold" : italic ? "Times-Italic" : "Times-Roman")
                        : "Helvetica" + (bold && italic ? "-BoldOblique" : bold ? "-Bold" : italic ? "-Oblique" : "");
        // Arial / Times New Roman / Courier New have the very same letter shapes and widths as the standard PDF fonts: nothing to embed
        bool sameAsStandard = l.StartsWith("arial") && !l.Contains("narrow") && !l.Contains("black") || l.StartsWith("helvetica") || l.StartsWith("times") || l.StartsWith("courier");
        if (sameAsStandard && StandardCanShow(text))
        {
            string skey = "std|" + standard;
            if (!fonts.TryGetValue(skey, out var sf)) fonts[skey] = sf = Pdfium.FPDFText_LoadStandardFont(doc, standard);
            if (sf != IntPtr.Zero) return sf;
        }
        string key = $"sub|{family}|{bold}|{italic}";
        if (fonts.TryGetValue(key, out var f)) return f;
        string? file = InstalledFont(family, bold, italic);
        if (file != null)
        {
            try
            {
                byte[] data = File.ReadAllBytes(file);
                f = Pdfium.FPDFText_LoadFont(doc, data, (uint)data.Length, Pdfium.FontTrueType, 1);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { f = IntPtr.Zero; }
        }
        if (f == IntPtr.Zero) f = Pdfium.FPDFText_LoadStandardFont(doc, standard);         // one of the 14 standard fonts that looks like it
        if (f == IntPtr.Zero) throw new IOException("No font could be found for the changed text.");
        fonts[key] = f;
        return f;
    }

    private static bool StandardCanShow(string s) =>
        s.All(c => (c >= 0x20 && c <= 0x7E) || (c >= 0xA0 && c <= 0xFF) || "€‚ƒ„…†‡ˆ‰Š‹ŒŽ‘’“”•–—˜™š›œžŸ".IndexOf(c) >= 0);

    private static Dictionary<string, string>? _installed;

    /// <summary>The file of a font installed in Windows ("Calibri", bold) -> C:\Windows\Fonts\calibrib.ttf; null when it isn't there.</summary>
    public static string? InstalledFont(string family, bool bold, bool italic)
    {
        if (_installed == null)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Read(RegistryKey root, string folder)
            {
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                if (key == null) return;
                foreach (string name in key.GetValueNames())
                {
                    if (key.GetValue(name) is not string file) continue;
                    if (!file.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)) continue;   // (.ttc collections can't be embedded)
                    string clean = name.Replace("(TrueType)", "").Replace("(OpenType)", "").Trim();
                    map[Squash(clean)] = Path.IsPathRooted(file) ? file : Path.Combine(folder, file);
                }
            }
            string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            try { Read(Registry.LocalMachine, fontsDir); Read(Registry.CurrentUser, fontsDir); } catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            _installed = map;
        }
        string style = bold && italic ? "bolditalic" : bold ? "bold" : italic ? "italic" : "";
        foreach (string candidate in new[] { Squash(family) + style, Squash(family) + (style.Length == 0 ? "regular" : style) })
            if (_installed.TryGetValue(candidate, out var path) && File.Exists(path)) return path;
        return null;
    }

    private static string Squash(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
