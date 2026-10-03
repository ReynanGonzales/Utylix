using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace IdmClone.Engine;

/// <summary>A word found in a picture of a page, with its box in pixels of that picture.</summary>
public sealed record OcrWord(string Text, Rect Box);

/// <summary>
/// Making scans searchable: Windows' own text recognition (offline, nothing is sent anywhere) reads the picture of a page, and each word is put
/// on the page as invisible text over the place where it was read. The picture is not changed; afterwards Search, select and copy work.
/// </summary>
public static class PdfOcr
{
    /// <summary>Resolution the pages are read at (dots per inch): sharp enough for small print, not slow.</summary>
    public const int Dpi = 300;

    /// <summary>The recognition engine for the person's languages (English when Windows has none), or null with a message.</summary>
    public static OcrEngine? CreateEngine(out string message)
    {
        message = "";
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine != null) return engine;
            engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"));
            if (engine != null) return engine;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException) { }
        message = "Windows has no language installed for reading text from pictures. Add one in Settings > Time & language > Language & region (choose a language that lists \"text recognition\"), then try again.";
        return null;
    }

    /// <summary>The size in pixels a page is drawn at for reading (its long side kept within what the engine can take).</summary>
    public static (int Width, int Height) PixelSize(Size pointSize)
    {
        double k = Dpi / 72.0;
        int max = (int)Math.Min(OcrEngine.MaxImageDimension, 5000);
        double fit = Math.Min(1, max / Math.Max(pointSize.Width * k, pointSize.Height * k));
        return (Math.Max(1, (int)Math.Round(pointSize.Width * k * fit)), Math.Max(1, (int)Math.Round(pointSize.Height * k * fit)));
    }

    /// <summary>Reads the words of a picture.</summary>
    public static async Task<List<OcrWord>> ReadAsync(OcrEngine engine, BitmapSource picture)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(picture));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(ms.ToArray());
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);
        var words = new List<OcrWord>();
        foreach (var line in result.Lines)
            foreach (var word in line.Words)
                if (!string.IsNullOrWhiteSpace(word.Text)) words.Add(new OcrWord(word.Text, new Rect(word.BoundingRect.X, word.BoundingRect.Y, word.BoundingRect.Width, word.BoundingRect.Height)));
        return words;
    }

    /// <summary>The invisible text for the words of one page: the page as shown is <paramref name="pageSize"/> points, the picture it was read from <paramref name="pixels"/>.</summary>
    public static IEnumerable<PdfTextMark> Marks(int page, Size pageSize, (int Width, int Height) pixels, IEnumerable<OcrWord> words)
    {
        double kx = pageSize.Width / pixels.Width, ky = pageSize.Height / pixels.Height;
        var arial = new FontFamily("Arial");
        var face = new Typeface(arial, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        foreach (var w in words)
        {
            double x = w.Box.X * kx, y = w.Box.Y * ky, width = w.Box.Width * kx, height = w.Box.Height * ky;
            if (width < 0.5 || height < 1) continue;
            double size = Math.Clamp(height * 0.8, 3, 200);
            double natural = new FormattedText(w.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;
            double stretch = natural > 0 ? Math.Clamp(width / natural, 0.2, 5) : 1;
            double baseline = y + height * 0.84;                                               // (the letters sit near the bottom of the word's box)
            yield return new PdfTextMark(page, new Point(x, baseline - arial.Baseline * size), w.Text, PdfFontKind.Sans, false, size, Colors.Black, arial.LineSpacing, arial.Baseline, null, 0, null, Invisible: true, Stretch: stretch);
        }
    }
}
