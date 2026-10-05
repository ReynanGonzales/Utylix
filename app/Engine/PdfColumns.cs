using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace IdmClone.Engine;

/// <summary>Text set in columns, like Word's: the words, how many columns, the gap between them, the width of the whole block, the font.</summary>
public sealed class ColumnsSpec
{
    public string Text { get; set; } = "";
    public int Columns { get; set; } = 2;
    /// <summary>Space between two columns, points.</summary>
    public double Gap { get; set; } = 18;
    /// <summary>Width of the whole block, points.</summary>
    public double Width { get; set; } = 400;
    public PdfFontKind Font { get; set; } = PdfFontKind.Serif;
    public string? FontName { get; set; }
    public bool Bold { get; set; }
    public double Size { get; set; } = 11;
    public Color Color { get; set; } = Colors.Black;

    public ColumnsSpec Clone() => (ColumnsSpec)MemberwiseClone();
}

public static class PdfColumns
{
    public static FontFamily Family(ColumnsSpec s) => new(s.FontName ?? s.Font switch { PdfFontKind.Serif => "Times New Roman", PdfFontKind.Mono => "Courier New", _ => "Arial" });

    private static Typeface Face(ColumnsSpec s) => new(Family(s), FontStyles.Normal, s.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);

    private static double Measure(string text, Typeface face, double size) =>
        new FormattedText(text.Length == 0 ? " " : text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;

    public static double ColumnWidth(ColumnsSpec s) => Math.Max(20, (s.Width - s.Gap * (Math.Max(1, s.Columns) - 1)) / Math.Max(1, s.Columns));

    /// <summary>The text cut into lines that fit one column.</summary>
    public static List<string> Wrap(ColumnsSpec s)
    {
        var face = Face(s);
        double width = ColumnWidth(s);
        var lines = new List<string>();
        foreach (string paragraph in s.Text.Replace("\r\n", "\n").Split('\n'))
        {
            if (paragraph.Trim().Length == 0) { lines.Add(""); continue; }
            string current = "";
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string attempt = current.Length == 0 ? word : current + " " + word;
                if (Measure(attempt, face, s.Size) <= width) { current = attempt; continue; }
                if (current.Length > 0) { lines.Add(current); current = ""; }
                // a word wider than a column is broken
                string rest = word;
                while (Measure(rest, face, s.Size) > width && rest.Length > 1)
                {
                    int cut = rest.Length - 1;
                    while (cut > 1 && Measure(rest[..cut], face, s.Size) > width) cut--;
                    lines.Add(rest[..cut]); rest = rest[cut..];
                }
                current = rest;
            }
            lines.Add(current);
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>The columns' lines, shared out evenly (so the block is as short as it can be), and the block's height in points.</summary>
    public static (List<List<string>> Columns, double Height, double LineHeight) Flow(ColumnsSpec s)
    {
        var lines = Wrap(s);
        int n = Math.Max(1, s.Columns);
        double lineHeight = Family(s).LineSpacing * s.Size;
        int perColumn = Math.Max(1, (int)Math.Ceiling(lines.Count / (double)n));
        var columns = new List<List<string>>();
        for (int c = 0; c < n; c++) columns.Add(lines.Skip(c * perColumn).Take(perColumn).ToList());
        // a paragraph break that lands at the top of a column would start it with a blank line
        foreach (var col in columns) while (col.Count > 1 && col[0].Length == 0) col.RemoveAt(0);
        return (columns, perColumn * lineHeight, lineHeight);
    }
}
