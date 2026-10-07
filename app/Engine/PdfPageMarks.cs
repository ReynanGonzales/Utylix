using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IdmClone.Engine;

/// <summary>Where a line of text sits on the page: top or bottom, and left, centre or right.</summary>
public enum PdfSpot { TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter, BottomRight }

/// <summary>A line of text at the top or bottom of the pages: page numbers ("Page {n} of {total}"), a title, the date ...</summary>
public sealed class PdfHeaderFooter
{
    /// <summary>Text with {n} (page number), {total} (number of pages), {date} (today) and {file} (the PDF's name).</summary>
    public string Text { get; set; } = "Page {n} of {total}";
    public PdfSpot Spot { get; set; } = PdfSpot.BottomCenter;
    public PdfFontKind Font { get; set; } = PdfFontKind.Sans;
    public bool Bold { get; set; }
    public double Size { get; set; } = 10;
    public Color Color { get; set; } = Colors.Black;
    /// <summary>Distance from the edge of the page, points.</summary>
    public double Margin { get; set; } = 30;
    /// <summary>The number the first page of the range gets.</summary>
    public int Start { get; set; } = 1;
    /// <summary>The first page of the range gets no text (a cover page), but is still counted.</summary>
    public bool SkipFirst { get; set; }
}

/// <summary>A big pale text (or a picture) across the pages, like "CONFIDENTIAL" or a logo.</summary>
public sealed class PdfWatermark
{
    public string Text { get; set; } = "CONFIDENTIAL";
    public PdfPicture? Picture { get; set; }
    /// <summary>How wide it is, as a share of the page width (0.1 .. 1).</summary>
    public double Width { get; set; } = 0.7;
    /// <summary>Turned clockwise by degrees (-45 = rising to the right). Pictures are not turned.</summary>
    public double Angle { get; set; } = -45;
    public double Opacity { get; set; } = 0.25;
    public Color Color { get; set; } = Color.FromRgb(0xD3, 0x2F, 0x2F);
    public PdfFontKind Font { get; set; } = PdfFontKind.Sans;
}

/// <summary>Works out the marks for page numbers, headers / footers and watermarks (the same marks are drawn in the preview and written into the PDF).</summary>
public static class PdfPageMarks
{
    private static readonly FontFamily Sans = new("Arial"), Serif = new("Times New Roman"), Mono = new("Courier New");

    public static FontFamily Family(PdfFontKind kind) => kind switch { PdfFontKind.Serif => Serif, PdfFontKind.Mono => Mono, _ => Sans };

    /// <summary>The text with {n}, {total}, {date} and {file} filled in.</summary>
    public static string Expand(string text, int number, int total, string file)
    {
        // {n:000000} = the number padded with zeros to that many digits (Bates numbering: "CASE-{n:000000}" gives CASE-000001, CASE-000002 ...)
        text = Regex.Replace(text, @"\{(n|total):(0{1,12})\}", m => (m.Groups[1].Value.Equals("n", StringComparison.OrdinalIgnoreCase) ? number : total).ToString(new string('0', m.Groups[2].Length), CultureInfo.InvariantCulture), RegexOptions.IgnoreCase);
        return ExpandPlain(text, number, total, file);
    }

    private static string ExpandPlain(string text, int number, int total, string file) =>
        text.Replace("{n}", number.ToString(CultureInfo.CurrentCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{total}", total.ToString(CultureInfo.CurrentCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", DateTime.Today.ToString("d MMM yyyy", CultureInfo.CurrentCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{file}", file, StringComparison.OrdinalIgnoreCase);

    private static double WidthOf(string text, PdfFontKind kind, bool bold, double size) =>
        new FormattedText(text.Length == 0 ? " " : text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(Family(kind), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal), size, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;

    /// <summary>
    /// The marks for the pages first..last (0-based, both included). <paramref name="size"/> gives a page's size in points as shown.
    /// </summary>
    public static List<PdfMark> Build(int pageCount, Func<int, Size> size, string file, int first, int last, PdfHeaderFooter? line, PdfWatermark? watermark, int? only = null)
    {
        var marks = new List<PdfMark>();
        first = Math.Clamp(first, 0, Math.Max(0, pageCount - 1));
        last = Math.Clamp(last, first, Math.Max(0, pageCount - 1));
        for (int page = first; page <= last; page++)
        {
            if (only != null && page != only) continue;                       // (the preview needs one page, but numbered as in the whole range)
            var s = size(page);
            if (line != null && !(line.SkipFirst && page == first) && line.Text.Trim().Length > 0)
            {
                string text = Expand(line.Text, line.Start + (page - first), pageCount, file);
                var f = Family(line.Font);
                double w = WidthOf(text, line.Font, line.Bold, line.Size);
                double x = line.Spot switch
                {
                    PdfSpot.TopLeft or PdfSpot.BottomLeft => line.Margin,
                    PdfSpot.TopCenter or PdfSpot.BottomCenter => (s.Width - w) / 2,
                    _ => s.Width - line.Margin - w,
                };
                double lineHeight = f.LineSpacing * line.Size;
                double y = line.Spot is PdfSpot.TopLeft or PdfSpot.TopCenter or PdfSpot.TopRight ? line.Margin : s.Height - line.Margin - lineHeight;
                marks.Add(new PdfTextMark(page, new Point(Math.Max(0, x), Math.Max(0, y)), text, line.Font, line.Bold, line.Size, line.Color, f.LineSpacing, f.Baseline));
            }
            if (watermark != null) marks.AddRange(WatermarkMarks(page, s, watermark));
        }
        return marks;
    }

    private static IEnumerable<PdfMark> WatermarkMarks(int page, Size s, PdfWatermark wm)
    {
        byte alpha = (byte)Math.Clamp(Math.Round(wm.Opacity * 255), 5, 255);
        var centre = new Point(s.Width / 2, s.Height / 2);
        if (wm.Picture is { } picture)
        {
            double pw = picture.Pixels.PixelWidth, ph = picture.Pixels.PixelHeight;
            double w = Math.Clamp(s.Width * wm.Width, 8, s.Width), h = w * ph / pw;
            if (h > s.Height * 0.95) { h = s.Height * 0.95; w = h * pw / ph; }
            yield return new PdfImageMark(page, new Rect(centre.X - w / 2, centre.Y - h / 2, w, h), null, WithOpacity(picture.Pixels, wm.Opacity), Watermark: true);
            yield break;
        }
        string text = wm.Text.Trim();
        if (text.Length == 0) yield break;
        // the text is as long as asked along its own direction (a diagonal one may be longer than the page is wide)
        double length = s.Width * wm.Width / Math.Max(0.2, Math.Abs(Math.Cos(wm.Angle * Math.PI / 180)) * 0.85 + 0.15);
        length = Math.Min(length, Math.Sqrt(s.Width * s.Width + s.Height * s.Height) * 0.9);
        double natural = WidthOf(text, wm.Font, true, 100);
        double size = Math.Clamp(length / natural * 100, 6, 600);
        double textW = WidthOf(text, wm.Font, true, size);
        var f = Family(wm.Font);
        var topLeft = new Point(centre.X - textW / 2, centre.Y - f.LineSpacing * size / 2);
        yield return new PdfTextMark(page, topLeft, text, wm.Font, true, size, Color.FromArgb(alpha, wm.Color.R, wm.Color.G, wm.Color.B), f.LineSpacing, f.Baseline, null, wm.Angle, centre, Watermark: true);
    }

    /// <summary>The picture with its transparency made less opaque (a picture without transparency becomes see-through all over).</summary>
    public static BitmapSource WithOpacity(BitmapSource picture, double opacity)
    {
        var src = picture.Format == PixelFormats.Bgra32 ? picture : new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var data = new byte[(long)stride * h];
        src.CopyPixels(data, stride, 0);
        for (int i = 3; i < data.Length; i += 4) data[i] = (byte)Math.Round(data[i] * Math.Clamp(opacity, 0, 1));
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, data, stride);
        result.Freeze();
        return result;
    }
}
