using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;

namespace IdmClone.Engine;

/// <summary>The fonts installed in Windows that can be put into a PDF (plain TrueType files; the very big ones - Asian fonts of many MB - are left out).</summary>
public static class PdfFonts
{
    private const long MaxFileSize = 8_000_000;
    private static List<string>? _names;

    /// <summary>Names of the usable font families, sorted (found once, the first time it is asked for).</summary>
    public static IReadOnlyList<string> Names()
    {
        if (_names != null) return _names;
        var list = new List<string>();
        foreach (var family in Fonts.SystemFontFamilies)
        {
            try
            {
                if (FileFor(family.Source, bold: false) != null) list.Add(family.Source);
            }
            catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException) { }
        }
        _names = list.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        return _names;
    }

    /// <summary>The .ttf file of a family (its bold face when asked and there is one), or null when it can't be embedded.</summary>
    public static string? FileFor(string family, bool bold)
    {
        var face = new Typeface(new FontFamily(family), System.Windows.FontStyles.Normal, bold ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);
        if (!face.TryGetGlyphTypeface(out var glyphs) || glyphs.Symbol || glyphs.FontUri is not { IsFile: true } uri) return null;
        string path = uri.LocalPath;
        if (!string.Equals(Path.GetExtension(path), ".ttf", StringComparison.OrdinalIgnoreCase)) return null;
        var info = new FileInfo(path);
        return info.Exists && info.Length <= MaxFileSize ? path : null;
    }
}
