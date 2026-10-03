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
/// as shown), its words, size, font and colour. Only upright text is offered for changing.
/// </summary>
public sealed record PdfTextRun(int Index, Rect Box, string Text, double Size, string Family, bool Bold, bool Italic, Color Color, Point Baseline);

/// <summary>Change the words of a piece of text already in the page (empty = remove it).</summary>
public sealed record PdfReplaceTextMark(int Page, int Index, string OldText, string NewText) : PdfMark(Page);

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

    private static string TextOf(IntPtr obj, IntPtr textPage)
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
    public static void Replace(IntPtr doc, IntPtr page, IEnumerable<PdfReplaceTextMark> marks, Dictionary<string, IntPtr> fonts)
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
                if (m.NewText.Trim().Length == 0) { Pdfium.FPDFPage_RemoveObject(page, obj); Pdfium.FPDFPageObj_Destroy(obj); continue; }
                IntPtr font = Pdfium.FPDFTextObj_GetFont(obj);
                letters ??= LettersByFont(page, tp);
                // the PDF's own font, when it already has every letter needed (a font in a PDF often has only the letters it uses)
                if (letters.TryGetValue(font, out var has) && m.NewText.All(c => c == ' ' ? has.Contains(' ') : has.Contains(c)))
                {
                    if (Pdfium.FPDFText_SetText(obj, m.NewText) != 0) continue;
                }
                // otherwise a new piece of text in the same place, size and colour, with the same font from Windows (or one like it)
                var (family, bold, italic) = Describe(font);
                Pdfium.FPDFPageObj_GetMatrix(obj, out var matrix);
                Pdfium.FPDFTextObj_GetFontSize(obj, out float fs);
                Pdfium.FPDFPageObj_GetFillColor(obj, out uint r, out uint g, out uint b, out uint a);
                IntPtr newFont = Substitute(doc, family, bold, italic, m.NewText, fonts);
                IntPtr fresh = Pdfium.FPDFPageObj_CreateTextObj(doc, newFont, fs);
                if (fresh == IntPtr.Zero) throw new IOException("The changed text couldn't be written.");
                Pdfium.FPDFText_SetText(fresh, m.NewText);
                Pdfium.FPDFPageObj_SetFillColor(fresh, r, g, b, a);
                Pdfium.FPDFPageObj_SetMatrix(fresh, ref matrix);
                Pdfium.FPDFPage_InsertObject(page, fresh);
                Pdfium.FPDFPage_RemoveObject(page, obj);
                Pdfium.FPDFPageObj_Destroy(obj);
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
