using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;

namespace IdmClone.Engine;

/// <summary>Taking pages out of a PDF (extract, split), putting pages in (from another PDF or from pictures) and saving pages as pictures.</summary>
public static class PdfPageTools
{
    /// <summary>The given pages (0-based, in this order) as a new PDF.</summary>
    public static byte[] Extract(PdfFile source, IReadOnlyList<int> pages)
    {
        if (pages.Count == 0) throw new IOException("No page was chosen.");
        if (source.IsProtected) throw new PdfProtectedException("A password-protected PDF can't be split or extracted here (the new file would lose its protection).");
        Pdfium.Init();
        IntPtr target;
        lock (Pdfium.Sync) target = Pdfium.FPDF_CreateNewDocument();
        if (target == IntPtr.Zero) throw new IOException("Couldn't start a new PDF.");
        try
        {
            lock (Pdfium.Sync)
            {
                if (Pdfium.FPDF_ImportPagesByIndex(target, source.Handle, pages.ToArray(), (uint)pages.Count, 0) == 0) throw new IOException("The pages couldn't be copied.");
                using var ms = new MemoryStream();
                if (!Pdfium.Save(target, ms)) throw new IOException("The PDF couldn't be written.");
                return ms.ToArray();
            }
        }
        finally { lock (Pdfium.Sync) Pdfium.FPDF_CloseDocument(target); }
    }

    /// <summary>Puts every page of another PDF into <paramref name="dest"/> at this place (0 = in front of the first page).</summary>
    public static int InsertPdf(PdfFile dest, string path, int at)
    {
        PdfFile source;
        try { source = PdfFile.Open(path); }
        catch (PdfPasswordException) { throw new PdfProtectedException(Path.GetFileName(path) + " needs a password to open, so its pages can't be added."); }
        using (source)
        {
            if (source.IsProtected) throw new PdfProtectedException(Path.GetFileName(path) + " is password-protected, so its pages can't be added.");
            int added = source.PageCount;
            lock (Pdfium.Sync)
            {
                if (Pdfium.FPDF_ImportPagesByIndex(dest.Handle, source.Handle, null, 0, Math.Clamp(at, 0, dest.PageCount)) == 0) throw new IOException("The pages of " + Path.GetFileName(path) + " couldn't be copied.");
            }
            dest.Reload();                                        // (the new pages are written into the document while the other file is still open)
            return added;
        }
    }

    /// <summary>What to cut off a page, in points, as seen on the page (from its shown edges).</summary>
    public readonly record struct CropMargins(double Left, double Top, double Right, double Bottom);

    /// <summary>The smallest a cropped page may get (points).</summary>
    public const double MinCrop = 36;

    /// <summary>
    /// Crops the pages: the PDF's /CropBox is set, so every reader shows only what is inside. What is cut off stays in the file (it is hidden, not
    /// erased). <paramref name="margins"/> gives each page's cut (null = leave that page). Returns how many pages were cropped.
    /// </summary>
    public static int Crop(PdfFile pdf, IEnumerable<int> pages, Func<int, CropMargins?> margins)
    {
        int done = 0;
        lock (Pdfium.Sync)
        {
            foreach (int i in pages.Distinct().Where(i => i >= 0 && i < pdf.PageCount))
            {
                var cut = margins(i);
                if (cut is not CropMargins m) continue;
                Pdfium.FPDF_GetPageSizeByIndexF(pdf.Handle, i, out var size);
                double w = size.Width, h = size.Height;
                if (w - m.Left - m.Right < MinCrop || h - m.Top - m.Bottom < MinCrop) throw new IOException($"Page {i + 1} would be left too small. Cut less.");
                IntPtr page = Pdfium.FPDF_LoadPage(pdf.Handle, i);
                if (page == IntPtr.Zero) throw new IOException("Page " + (i + 1) + " can't be read.");
                try
                {
                    var map = new PageMapping(page, w, h);
                    var a = map.ToPage(new Point(m.Left, m.Top)); var b = map.ToPage(new Point(w - m.Right, h - m.Bottom));
                    Pdfium.FPDFPage_SetCropBox(page, (float)Math.Min(a.X, b.X), (float)Math.Min(a.Y, b.Y), (float)Math.Max(a.X, b.X), (float)Math.Max(a.Y, b.Y));
                    done++;
                }
                finally { Pdfium.FPDF_ClosePage(page); }
            }
        }
        pdf.Reload();
        return done;
    }

    /// <summary>The empty edges of a page: what to cut so that only its content (plus <paramref name="pad"/> points around it) stays. Null for a page with nothing on it.</summary>
    public static CropMargins? ContentMargins(PdfFile pdf, int index, double pad)
    {
        var size = pdf.PageSize(index);
        int pw = 700, ph = Math.Max(1, (int)Math.Round(pw * size.Height / size.Width));
        var picture = pdf.Render(index, pw, ph, 0, forScreen: false);
        int stride = pw * 4;
        var px = new byte[stride * ph];
        picture.CopyPixels(px, stride, 0);
        int left = pw, right = -1, top = ph, bottom = -1;
        for (int y = 0; y < ph; y++)
            for (int x = 0; x < pw; x++)
            {
                int o = y * stride + x * 4;
                if (px[o] > 235 && px[o + 1] > 235 && px[o + 2] > 235) continue;           // (paper)
                if (x < left) left = x; if (x > right) right = x; if (y < top) top = y; if (y > bottom) bottom = y;
            }
        if (right < 0) return null;
        double kx = size.Width / pw, ky = size.Height / ph;
        return new CropMargins(Math.Max(0, left * kx - pad), Math.Max(0, top * ky - pad), Math.Max(0, size.Width - (right + 1) * kx - pad), Math.Max(0, size.Height - (bottom + 1) * ky - pad));
    }

    /// <summary>One page of the new order: a page of the document (0-based) or <c>-1</c> for a new blank page, and how many quarter turns clockwise to turn it.</summary>
    public readonly record struct PagePlan(int Source, int Turns);

    /// <summary>
    /// Makes the document's pages match <paramref name="plan"/>: pages left out are deleted, the others are put in the order given and turned, and blank
    /// pages go in where the plan has them. At least one page of the document must stay.
    /// </summary>
    public static void Rearrange(PdfFile pdf, IReadOnlyList<PagePlan> plan)
    {
        var keep = plan.Where(p => p.Source >= 0).Select(p => p.Source).ToList();
        if (keep.Count == 0) throw new InvalidOperationException("At least one page of the PDF must stay.");
        if (keep.Distinct().Count() != keep.Count || keep.Any(i => i >= pdf.PageCount)) throw new InvalidOperationException("The page plan doesn't fit this PDF.");
        pdf.DeletePages(Enumerable.Range(0, pdf.PageCount).Except(keep).ToList());
        // the order: bring each page, front to back, to its place
        var current = keep.OrderBy(i => i).ToList();
        for (int i = 0; i < keep.Count; i++)
        {
            int at = current.IndexOf(keep[i]);
            if (at == i) continue;
            pdf.MovePages(new[] { at }, i);
            current.RemoveAt(at); current.Insert(i, keep[i]);
        }
        // the turns (pages are now in the order of the plan, without the blanks)
        int k = 0;
        var byTurns = new Dictionary<int, List<int>>();
        foreach (var p in plan)
        {
            if (p.Source < 0) continue;
            int t = ((p.Turns % 4) + 4) % 4;
            if (t != 0) { if (!byTurns.TryGetValue(t, out var list)) byTurns[t] = list = new List<int>(); list.Add(k); }
            k++;
        }
        foreach (var (turns, pages) in byTurns) pdf.TurnPages(pages, turns);
        // the blank pages, front to back, so every position is still right when it is inserted
        for (int pos = 0; pos < plan.Count; pos++)
        {
            if (plan[pos].Source >= 0) continue;
            var like = pdf.PageSize(Math.Clamp(pos > 0 ? pos - 1 : 0, 0, pdf.PageCount - 1));
            InsertBlank(pdf, pos, (like.Width, like.Height));
        }
        pdf.Reload();
    }

    /// <summary>Puts an empty page at this place (0 = in front of the first page); its size is <paramref name="like"/> (a neighbouring page), points.</summary>
    public static void InsertBlank(PdfFile dest, int at, (double Width, double Height) like)
    {
        lock (Pdfium.Sync)
        {
            IntPtr page = Pdfium.FPDFPage_New(dest.Handle, Math.Clamp(at, 0, dest.PageCount), like.Width, like.Height);
            if (page == IntPtr.Zero) throw new IOException("The blank page couldn't be added.");
            Pdfium.FPDF_ClosePage(page);
        }
        dest.Reload();
    }

    /// <summary>Puts a picture on a new page at this place; the sheet is the size of <paramref name="like"/> (a neighbouring page), points.</summary>
    public static void InsertPicture(PdfFile dest, string path, int at, (double Width, double Height) like)
    {
        PdfPicture picture;
        try { picture = PdfCombiner.LoadPicture(File.ReadAllBytes(path), 0); }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException or OverflowException)
        {
            throw new IOException(Path.GetFileName(path) + " couldn't be read as a picture.");
        }
        // the sheet is turned like the picture
        bool wide = picture.Pixels.PixelWidth > picture.Pixels.PixelHeight;
        double a = Math.Max(like.Width, like.Height), b = Math.Min(like.Width, like.Height);
        var sheet = wide ? (a, b) : (b, a);
        lock (Pdfium.Sync) PdfCombiner.AddPicturePage(dest.Handle, picture, PdfPaper.A4, Math.Clamp(at, 0, dest.PageCount), sheet);
        dest.Reload();
    }

    /// <summary>"1-3, 5, 8-10" -> [[0,1,2],[4],[7,8,9]]. Throws <see cref="FormatException"/> with a message for the person.</summary>
    public static List<List<int>> ParseRanges(string text, int pageCount)
    {
        var result = new List<List<int>>();
        foreach (string part in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] ends = part.Split(new[] { '-', '–' }, StringSplitOptions.TrimEntries);
            if (ends.Length > 2 || !int.TryParse(ends[0], NumberStyles.None, CultureInfo.InvariantCulture, out int from) || !int.TryParse(ends[^1], NumberStyles.None, CultureInfo.InvariantCulture, out int to))
                throw new FormatException($"\"{part}\" isn't a page or a range like 3-5.");
            if (from < 1 || to < 1 || from > pageCount || to > pageCount) throw new FormatException($"\"{part}\": this PDF has pages 1 to {pageCount}.");
            if (to < from) (from, to) = (to, from);
            result.Add(Enumerable.Range(from - 1, to - from + 1).ToList());
        }
        if (result.Count == 0) throw new FormatException("Type the pages, like 1-3, 4-6, 9.");
        return result;
    }

    /// <summary>Every N pages: [[0..N-1],[N..2N-1],...].</summary>
    public static List<List<int>> Every(int n, int pageCount)
    {
        n = Math.Max(1, n);
        var result = new List<List<int>>();
        for (int start = 0; start < pageCount; start += n) result.Add(Enumerable.Range(start, Math.Min(n, pageCount - start)).ToList());
        return result;
    }

    /// <summary>"3" or "3-5" for the file name of a piece (1-based).</summary>
    public static string Label(IReadOnlyList<int> pages)
    {
        bool together = pages.Count > 1 && pages[^1] - pages[0] == pages.Count - 1;
        return pages.Count == 1 ? (pages[0] + 1).ToString(CultureInfo.InvariantCulture)
             : together ? $"{pages[0] + 1}-{pages[^1] + 1}"
             : string.Join(",", pages.Take(6).Select(p => p + 1)) + (pages.Count > 6 ? ",…" : "");
    }

    /// <summary>Saves pages as PNG or JPG files ("name - page 3.png") in a folder; returns the files made.</summary>
    public static List<string> SavePictures(PdfFile source, IReadOnlyList<int> pages, int dpi, bool png, string folder, string stem, IProgress<(int Done, int Total)>? progress, System.Threading.CancellationToken cancel)
    {
        var files = new List<string>();
        Directory.CreateDirectory(folder);
        int digits = Math.Max(1, source.PageCount.ToString(CultureInfo.InvariantCulture).Length);
        for (int i = 0; i < pages.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            progress?.Report((i, pages.Count));
            var size = source.PageSize(pages[i]);
            int w = Math.Max(1, (int)Math.Round(size.Width * dpi / 72)), h = Math.Max(1, (int)Math.Round(size.Height * dpi / 72));
            double big = Math.Sqrt((double)w * h / 90_000_000);                       // (a poster at 600 dpi would not fit in memory)
            if (big > 1) { w = (int)(w / big); h = (int)(h / big); }
            var picture = source.Render(pages[i], w, h, 0, forScreen: false);
            BitmapEncoder enc = png ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 92 };
            enc.Frames.Add(BitmapFrame.Create(png ? picture : new FormatConvertedBitmap(picture, System.Windows.Media.PixelFormats.Bgr24, null, 0)));
            string path = Path.Combine(folder, $"{Safe(stem)} - page {(pages[i] + 1).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0')}.{(png ? "png" : "jpg")}");
            using (var fs = File.Create(path)) enc.Save(fs);
            files.Add(path);
        }
        progress?.Report((pages.Count, pages.Count));
        return files;
    }

    /// <summary>A file name without the characters Windows doesn't allow.</summary>
    public static string Safe(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }
}
