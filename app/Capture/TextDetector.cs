using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace IdmClone;

/// <summary>
/// Reads the text in a picture with the text recognition that is built into Windows (nothing to download).
/// It uses the languages installed in Windows; add more under Settings → Time &amp; Language → Language.
/// </summary>
public static class TextDetector
{
    /// <summary>Languages Windows can read right now (display name, tag).</summary>
    public static List<(string Name, string Tag)> Languages() =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => (l.DisplayName, l.LanguageTag)).ToList();

    /// <param name="languageTag">null = the languages of your Windows profile.</param>
    public static async Task<string> ReadAsync(BitmapSource image, string? languageTag)
    {
        var engine = languageTag != null ? OcrEngine.TryCreateFromLanguage(new Language(languageTag)) : OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null)
            throw new InvalidOperationException("Windows has no text-recognition language installed. Add one under Settings → Time & Language → Language.");

        image = Prepare(image, (int)OcrEngine.MaxImageDimension);
        using var png = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        encoder.Save(png);
        png.Position = 0;

        using var stream = png.AsRandomAccessStream();
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);
        return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text)).Trim();
    }

    /// <summary>Small pictures are enlarged (the recognizer likes text at least ~30 px tall); huge ones are shrunk to its limit.</summary>
    private static BitmapSource Prepare(BitmapSource src, int maxSide)
    {
        int longest = Math.Max(src.PixelWidth, src.PixelHeight);
        double k = 1;
        if (longest > maxSide) k = maxSide / (double)longest;
        else if (src.PixelHeight < 220 && longest * 2 <= maxSide) k = 2;
        if (Math.Abs(k - 1) < 0.001 && src.Format == PixelFormats.Bgra32) return src;

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        int w = Math.Max(1, (int)Math.Round(src.PixelWidth * k)), h = Math.Max(1, (int)Math.Round(src.PixelHeight * k));
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));       // text on transparent → black text on white
            dc.DrawImage(src, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }
}
