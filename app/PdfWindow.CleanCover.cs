using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IdmClone.Engine;

namespace IdmClone;

/// <summary>
/// What hides the old words of a changed line or paragraph on screen: not a flat colour (that would cut a hole in a watermark, a picture or a coloured cell) but the page itself, drawn again WITHOUT those
/// words and cut to the place where they were. Saving takes the old words out of the page the same way, so what you see is what is saved.
/// </summary>
public sealed partial class PdfWindow
{
    /// <summary>The page without some of its text objects (and without the ones already changed in this session), cut to each of the <paramref name="areas"/> (points as shown); null where it could not be made.</summary>
    private List<BitmapSource?> CleanCrops(int page, IEnumerable<int> objects, IReadOnlyList<Rect> areas)
    {
        var result = areas.Select(_ => (BitmapSource?)null).ToList();
        if (_pdf == null || _rotation != 0 || page < 0 || page >= _sizes.Length || page >= _pages.Count || areas.Count == 0) return result;
        var size = _sizes[page];
        double scale = Math.Clamp(_pages[page].OverlayScale.ScaleX * 1.5, 1.5, 3.0);
        int w = (int)Math.Ceiling(size.Width * scale), h = (int)Math.Ceiling(size.Height * scale);
        if ((long)w * h > 36_000_000) return result;
        var gone = new HashSet<int>(EditedRunIndices(page));
        foreach (int o in objects) gone.Add(o);
        BitmapSource clean;
        try { clean = _pdf.RenderWithout(page, w, h, gone); }
        catch (Exception e) when (e is System.IO.IOException or ObjectDisposedException or OutOfMemoryException) { return result; }
        for (int i = 0; i < areas.Count; i++)
        {
            var a = areas[i];
            a.Intersect(new Rect(0, 0, size.Width, size.Height));
            if (a.IsEmpty) continue;
            int x = Math.Clamp((int)Math.Floor(a.X * scale), 0, w - 1), y = Math.Clamp((int)Math.Floor(a.Y * scale), 0, h - 1);
            int cw = Math.Clamp((int)Math.Ceiling(a.Width * scale), 1, w - x), ch = Math.Clamp((int)Math.Ceiling(a.Height * scale), 1, h - y);
            // copied out, so the picture of the whole page is not kept for every line
            var pixels = new byte[cw * ch * 4];
            new CroppedBitmap(clean, new Int32Rect(x, y, cw, ch)).CopyPixels(pixels, cw * 4, 0);
            var crop = BitmapSource.Create(cw, ch, 96, 96, PixelFormats.Bgr32, null, pixels, cw * 4);
            crop.Freeze();
            result[i] = crop;
        }
        return result;
    }

    private static IEnumerable<int> ObjectsOf(PdfTextRun run) => run.Parts?.Select(p => p.Index) ?? new[] { run.Index };

    /// <summary>Gives changed lines the picture that hides their old words (one drawing of the page for all of them).</summary>
    private void AttachRunCovers(int page, IReadOnlyList<RunEditItem> items)
    {
        if (items.Count == 0) return;
        var areas = items.Select(i => Grow(i.Run.Box, 1.2)).ToList();
        var crops = CleanCrops(page, items.SelectMany(i => ObjectsOf(i.Run)), areas);
        for (int k = 0; k < items.Count; k++)
        {
            items[k].CoverPicture = crops[k];
            items[k].CoverArea = areas[k];
            if (items.Count <= 5) items[k].JustifyTo = JustifyWidthFor(page, items[k].Run);       // (a few lines: a justified one stays justified when its words change)
        }
    }

    /// <summary>How wide a line must stay (from where it starts) when it is a justified line of a paragraph, else 0 (a left-aligned line, or the last line of a paragraph).</summary>
    private double JustifyWidthFor(int page, PdfTextRun run)
    {
        var para = FindParagraph(page, run);
        if (para is not { Align: 3 }) return 0;
        int k = para.Lines.FindIndex(l => l.Runs.Any(r => r.Index == run.Index));
        if (k < 0 || k >= para.Lines.Count - 1) return 0;
        return Math.Max(0, para.Right - run.Baseline.X);
    }

    /// <summary>The background of the box a line is typed in: the page behind the old words (or the colour chosen for behind the words).</summary>
    private static Brush RunBoxBrush(RunEditItem item)
    {
        if (item.BackColor is Color back) return new SolidColorBrush(back);
        if (item.CoverPicture != null && item.Run.Turn == 0) return new ImageBrush(item.CoverPicture) { Stretch = Stretch.Fill };      // (a turned line: its box is turned too, so a flat colour)
        return new SolidColorBrush(item.Cover);
    }
}
