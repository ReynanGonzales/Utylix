using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
